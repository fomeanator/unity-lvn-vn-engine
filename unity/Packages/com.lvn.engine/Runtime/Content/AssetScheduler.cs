using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Lvn.Content
{
    /// <summary>
    /// Prioritized download planner that sits on top of <see cref="ContentLoader"/>.
    /// A chapter's release set arrives from the server as a map of content-path →
    /// <see cref="LvnAssetMeta"/> (sha/size/tier/critical/eta_ms). The scheduler
    /// splits it into:
    /// <list type="bullet">
    ///   <item><b>required</b> — assets first needed at/near chapter start
    ///   (critical:true). These gate the Play button: <see cref="RequiredReady"/>
    ///   flips true only once every one is on disk.</item>
    ///   <item><b>deferred</b> — assets first used later. They download in the
    ///   background, at lower priority, and KEEP downloading after the chapter
    ///   starts (the player can begin while art trickles in).</item>
    /// </list>
    /// Ordering within a phase is EDF-ish: critical first, then earliest eta, then
    /// smallest size. Concurrency is bounded per tier (mini wide, large narrow) so
    /// a big background file can't starve the critical queue. ContentLoader already
    /// does the heavy lifting (disk cache, dedup, the global download semaphore,
    /// retries), so this class only decides WHAT to fetch WHEN, and tracks progress.
    /// </summary>
    public sealed class AssetScheduler
    {
        // Per-tier concurrency caps under ContentLoader's global 12 (HTTP/2
        // multiplexed). Mini is wide so a chapter's small files all download in
        // one parallel burst; large is capped low so a couple of big files can't
        // monopolise the connection and stall that burst.
        private const int MiniParallel = 12;  // < 50KB — full width, burst them all
        private const int NormalParallel = 6; // < 2MB
        private const int LargeParallel = 2;  // ≥ 2MB

        // Floor for a missing/zero size so a not-yet-uploaded asset still
        // contributes a little to the byte totals (keeps the bar honest).
        private const long MinAssetBytes = 1;

        private readonly ContentLoader _loader;

        private readonly SemaphoreSlim _miniSlots = new(MiniParallel, MiniParallel);
        private readonly SemaphoreSlim _normalSlots = new(NormalParallel, NormalParallel);
        private readonly SemaphoreSlim _largeSlots = new(LargeParallel, LargeParallel);

        private readonly object _lock = new();
        private CancellationTokenSource _cts;

        // Progress, polled by the loading UI. Bytes drive the single progress bar
        // (required + deferred together); the required counters/flag drive when
        // the Play button lights up.
        public int RequiredTotal { get; private set; }
        public int RequiredDone { get; private set; }
        public bool RequiredReady { get; private set; }
        public long TotalBytes { get; private set; }
        public long DoneBytes { get; private set; }
        public bool AllDone { get; private set; }

        /// <summary>0..1 over the WHOLE set (required + deferred). Falls back to 0
        /// if the server reported no sizes (use the count-based gate instead).</summary>
        public float Progress
        {
            get
            {
                if (AllDone) return 1f;
                lock (_lock)
                {
                    if (TotalBytes > 0) return Mathf.Clamp01((float)DoneBytes / TotalBytes);
                    return 0f;
                }
            }
        }

        /// <summary>Fired once the required set is fully on disk. The host (loading
        /// screen) uses it to enable the Play button.</summary>
        public event Action OnRequiredReady;
        /// <summary>Fired once when the entire set (required + deferred) is on disk.
        /// The host uses it to auto-start the chapter if the player waited.</summary>
        public event Action OnAllComplete;

        public AssetScheduler(ContentLoader loader)
        {
            _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        }

        /// <summary>One asset to fetch, with the metadata used to order it.</summary>
        private readonly struct Item
        {
            public readonly string Url;
            public readonly long Size;
            public readonly string Kind;
            public readonly string Tier;
            public readonly bool Critical;
            public readonly long EtaMs;

            public Item(string url, LvnAssetMeta m)
            {
                Url = url;
                Size = m != null && m.size > 0 ? m.size : MinAssetBytes;
                Kind = m?.kind;
                Tier = m?.tier;
                Critical = m?.critical ?? false;
                EtaMs = m?.eta_ms ?? 0;
            }
        }

        /// <summary>(Re)plans and starts downloading the given release set. Any
        /// in-flight plan from a previous call is cancelled first (e.g. the player
        /// swiped to a different chapter). Returns immediately; progress is
        /// observed via the public properties/events. <paramref name="ct"/> is the
        /// host's lifetime token (app/scene shutdown).</summary>
        public void Start(IReadOnlyDictionary<string, LvnAssetMeta> assets, CancellationToken ct = default)
        {
            Stop();

            var (reqPlan, defPlan) = OrderForDownload(assets);
            var required = new List<Item>(reqPlan.Count);
            foreach (var kv in reqPlan) required.Add(new Item(kv.Key, kv.Value));
            var deferred = new List<Item>(defPlan.Count);
            foreach (var kv in defPlan) deferred.Add(new Item(kv.Key, kv.Value));

            long total = 0;
            foreach (var i in required) total += i.Size;
            foreach (var i in deferred) total += i.Size;

            lock (_lock)
            {
                RequiredTotal = required.Count;
                RequiredDone = 0;
                RequiredReady = required.Count == 0;
                TotalBytes = total;
                DoneBytes = 0;
                AllDone = required.Count == 0 && deferred.Count == 0;
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _cts.Token;

            _ = RunAsync(required, deferred, token);

            // Empty set → already "ready"/"complete"; notify synchronously.
            if (RequiredReady) OnRequiredReady?.Invoke();
            if (AllDone) OnAllComplete?.Invoke();
        }

        /// <summary>Cancels the current plan (if any). Safe to call repeatedly.</summary>
        public void Stop()
        {
            CancellationTokenSource cts;
            lock (_lock) { cts = _cts; _cts = null; }
            if (cts == null) return;
            try { cts.Cancel(); } catch { /* already disposed */ }
            cts.Dispose();
        }

        private async Task RunAsync(List<Item> required, List<Item> deferred, CancellationToken ct)
        {
            try
            {
                // Phase 1 — required: download all, gate the Play button.
                await RunPhase(required, isRequired: true, ct);
                if (ct.IsCancellationRequested) return;

                lock (_lock) RequiredReady = true;
                OnRequiredReady?.Invoke();

                // Phase 2 — deferred: keep filling in the background. The player
                // may already have pressed Play; these continue during playback.
                await RunPhase(deferred, isRequired: false, ct);
                if (ct.IsCancellationRequested) return;

                lock (_lock) AllDone = true;
                OnAllComplete?.Invoke();
            }
            catch (OperationCanceledException) { /* replanned or shutting down */ }
            catch (Exception ex)
            {
                Debug.LogWarning($"[scheduler] run failed: {ex.Message}");
            }
        }

        private async Task RunPhase(List<Item> items, bool isRequired, CancellationToken ct)
        {
            if (items.Count == 0) return;
            var tasks = new List<Task>(items.Count);
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                tasks.Add(WarmOne(item, isRequired, ct));
            }
            await Task.WhenAll(tasks);
        }

        private async Task WarmOne(Item item, bool isRequired, CancellationToken ct)
        {
            var slot = SlotFor(item.Tier, item.Size);
            await slot.WaitAsync(ct);
            try
            {
                await Warm(item, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // ContentLoader already retried/gave up; log and move on so the
                // phase can complete (a dead asset can't block Play forever).
                Debug.LogWarning($"[scheduler] {item.Url} not fetched: {ex.Message}");
            }
            finally
            {
                slot.Release();
                MarkDone(item, isRequired);
            }
        }

        // Routes to the right ContentLoader primitive so the asset lands under the
        // cache key the in-game loader looks for. Uses the SERVER's classification
        // (kind) when present, falling back to the extension guess.
        private async Task Warm(Item item, CancellationToken ct)
        {
            var kind = string.IsNullOrEmpty(item.Kind) ? KindOf(item.Url) : item.Kind;
            switch (kind)
            {
                case "audio":
                    await _loader.DownloadAudioClipAsync(item.Url, ct);
                    break;
                default:
                    await _loader.DownloadSpriteAsync(item.Url, ct);
                    break;
            }
        }

        private void MarkDone(Item item, bool isRequired)
        {
            lock (_lock)
            {
                DoneBytes += item.Size;
                if (DoneBytes > TotalBytes) DoneBytes = TotalBytes;
                if (isRequired) RequiredDone++;
            }
        }

        private SemaphoreSlim SlotFor(string tier, long size)
        {
            var t = tier;
            if (string.IsNullOrEmpty(t))
                t = size < 50 * 1024 ? "mini" : size < 2 * 1024 * 1024 ? "normal" : "large";
            return t switch
            {
                "mini" => _miniSlots,
                "large" => _largeSlots,
                _ => _normalSlots,
            };
        }

        /// <summary>Pure planner: partition a release set into (required, deferred)
        /// and order each by priority. Required = critical assets; deferred = the
        /// rest. The chapter script (.lvn) is excluded — the play flow fetches it
        /// directly. Required is smallest-first (the mini burst fills the bar fast
        /// with quick wins; <see cref="RequiredReady"/> still waits for ALL, so
        /// this only changes completion ORDER, never total time). Deferred is
        /// earliest-eta first (use order). Static and side-effect-free.</summary>
        internal static (List<KeyValuePair<string, LvnAssetMeta>> required,
                         List<KeyValuePair<string, LvnAssetMeta>> deferred)
            OrderForDownload(IReadOnlyDictionary<string, LvnAssetMeta> assets)
        {
            var required = new List<KeyValuePair<string, LvnAssetMeta>>();
            var deferred = new List<KeyValuePair<string, LvnAssetMeta>>();
            if (assets != null)
            {
                foreach (var kv in assets)
                {
                    if (string.IsNullOrEmpty(kv.Key) || IsScript(kv.Key)) continue;
                    var critical = kv.Value?.critical ?? false;
                    (critical ? required : deferred).Add(kv);
                }
            }
            required.Sort(CompareSizeFirst);
            deferred.Sort(ComparePriority);
            return (required, deferred);
        }

        // smallest file first (mini burst), then earliest eta, then path (stable).
        private static int CompareSizeFirst(KeyValuePair<string, LvnAssetMeta> a,
                                            KeyValuePair<string, LvnAssetMeta> b)
        {
            long asz = a.Value?.size ?? 0, bsz = b.Value?.size ?? 0;
            if (asz != bsz) return asz.CompareTo(bsz);
            long ae = a.Value?.eta_ms ?? 0, be = b.Value?.eta_ms ?? 0;
            if (ae != be) return ae.CompareTo(be);
            return string.CompareOrdinal(a.Key, b.Key);
        }

        // earliest eta, then smallest file (quick wins), then path (stable).
        private static int ComparePriority(KeyValuePair<string, LvnAssetMeta> a,
                                           KeyValuePair<string, LvnAssetMeta> b)
        {
            long ae = a.Value?.eta_ms ?? 0, be = b.Value?.eta_ms ?? 0;
            if (ae != be) return ae.CompareTo(be);
            long asz = a.Value?.size ?? 0, bsz = b.Value?.size ?? 0;
            if (asz != bsz) return asz.CompareTo(bsz);
            return string.CompareOrdinal(a.Key, b.Key);
        }

        private static bool IsScript(string url) =>
            StripQuery(url).ToLowerInvariant().EndsWith(".lvn");

        private static string KindOf(string url)
        {
            var u = StripQuery(url).ToLowerInvariant();
            if (u.EndsWith(".lvn")) return "script";
            if (u.EndsWith(".ogg") || u.EndsWith(".wav") || u.EndsWith(".mp3")) return "audio";
            return "sprite";
        }

        private static string StripQuery(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            int q = url.IndexOf('?');
            return q >= 0 ? url.Substring(0, q) : url;
        }
    }
}
