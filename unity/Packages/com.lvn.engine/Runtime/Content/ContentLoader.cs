using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Lvn.Content
{
    /// <summary>
    /// Downloads <c>.lvn</c> scripts and asset bytes, caches them on disk, and
    /// returns local data on subsequent reads. Cache key = <c>sha1(url@version)</c>
    /// — a re-uploaded asset (new hash in the version index) maps to a NEW cache
    /// file and is re-downloaded automatically, while the old file stays as an
    /// offline fallback.
    ///
    /// This is the low-level fetch/decode/cache engine ported from a shipping
    /// visual-novel client: disk cache, an in-memory sprite cache, dedup of
    /// in-flight fetches, a global HTTP/2 download semaphore, resumable retries
    /// with exponential backoff, content-version cache-busting, and byte-level
    /// progress for a loading HUD. <see cref="AssetScheduler"/> sits on top to
    /// prioritize a chapter's release set; <c>NetworkAssets</c> adapts it to the
    /// engine's <c>ILvnAssets</c> seam.
    /// </summary>
    public class ContentLoader
    {
        private readonly string _baseUrl;
        // True when the content origin is a local bundle (file:// on desktop, or
        // jar:file:// for Android StreamingAssets). Local reads are always
        // available — they skip the offline gate and the ?v= cache-buster (which
        // would corrupt a file path), so an exported game plays with no server.
        private readonly bool _local;
        private readonly string _cacheRoot;
        private readonly string _scriptCacheDir;
        private readonly string _assetCacheDir;

        // Content-version index: path → sha256, fetched from
        // /content/asset-versions.json. Folded into the disk-cache key so a
        // re-uploaded asset (new hash) maps to a NEW cache file and is
        // re-downloaded automatically, while the old file stays as an offline
        // fallback. Empty until LoadAssetVersionsAsync runs; an unknown asset
        // falls back to the legacy url-only key (still works, just not auto-busted).
        private Dictionary<string, string> _versions = new();
        private readonly object _versionsLock = new();
        private const string VersionsPath = "/content/asset-versions.json";

        // Caps simultaneous in-flight downloads. HTTP/2 MULTIPLEXES many
        // concurrent requests over a SINGLE TLS connection — so a wider cap
        // doesn't open more sockets, it fills more h2 streams. 12 lets a burst of
        // small files (UI/script/actors) all fly at once without the
        // request-per-file round-trip tax a 6-cap (the HTTP/1.1 socket limit)
        // imposed.
        private static readonly SemaphoreSlim _downloadSlots = new(12, 12);

        // Hard per-request timeout. Deliberately short: a dead/blackhole socket
        // must fail fast so chapter loading degrades to cache instead of hanging
        // (offline a UnityWebRequest can otherwise sit the full timeout). The
        // global LvnNetworkStatus flag is the fast path; this timeout is the LIVE
        // backstop for when the flag is stale (e.g. wifi dropped mid-session).
        private const int RequestTimeoutSeconds = 10;

        // Fast-fail when we already know we're offline: skip the wire entirely so
        // callers fall straight back to the on-disk cache. Code "network" →
        // callers/retry-loops treat it as a connectivity miss.
        private void ThrowIfOffline()
        {
            if (_local) return; // local bundle is always available
            if (LvnNetworkStatus.IsOffline)
                throw new LvnFetchException(0, "network", "offline (global status)");
        }

        // MarkOffline only when reading from a real network origin; a missing
        // local file must not poison the global offline status. Going offline also
        // starts the recovery probe so the app self-heals when the wire returns.
        private void MarkOfflineUnlessLocal(string reason)
        {
            if (_local) return;
            LvnNetworkStatus.MarkOffline(reason);
            EnsureRecoveryLoop();
        }

        // 1 while the background recovery probe is running (guards against starting
        // a second one on every subsequent failed fetch).
        private int _recovering;

        // Once we've gone offline, nothing else re-probes connectivity — every
        // fetch just fast-fails on the global flag — so a single network blip would
        // wedge the app offline for the whole session (dead live-sync, no new
        // chapters, dropped saves). This loop probes /healthz with backoff while
        // offline and flips the flag back on the moment the server answers, which
        // unblocks the next fetch/sync automatically. HealthzAsync MarkOnlines on a
        // 2xx (and never MarkOffline), so a failed probe just waits and retries.
        private void EnsureRecoveryLoop()
        {
            if (_local || LvnNetworkStatus.ForceOffline) return; // never probe a local bundle / a test kill-switch
            if (Interlocked.Exchange(ref _recovering, 1) == 1) return; // already probing
            _ = RecoveryLoopAsync();
        }

        private async Task RecoveryLoopAsync()
        {
            try
            {
                int attempt = 2; // start at the first non-zero backoff step
                while (LvnNetworkStatus.IsOffline && !LvnNetworkStatus.ForceOffline)
                {
                    var delay = LvnBackoff.DelaySeconds(attempt++);
                    // Wake the sleep early on ANY status change (recovered via another
                    // path, or ForceOffline set) so the loop reacts at once instead of
                    // idling out the full backoff. A fresh token per iteration avoids a
                    // stale-cancelled-token hot spin.
                    using (var wake = new CancellationTokenSource())
                    {
                        Action<bool> onChange = _ => { try { wake.Cancel(); } catch { } };
                        LvnNetworkStatus.Changed += onChange;
                        try { await Task.Delay((int)(delay * 1000f) + 500, wake.Token); }
                        catch (OperationCanceledException) { /* status changed — re-check now */ }
                        finally { LvnNetworkStatus.Changed -= onChange; }
                    }
                    if (LvnNetworkStatus.IsOnline || LvnNetworkStatus.ForceOffline) break;
                    try { if (await HealthzAsync()) break; } // MarkOnlines on success
                    catch { /* probe failed — keep waiting */ }
                }
            }
            finally { Interlocked.Exchange(ref _recovering, 0); }
        }

        // Dedup tracker for in-flight fetches. Key = url, value = the running
        // task. Lets two callers (a preload + a later regular download) await the
        // same fetch instead of re-issuing it. Tasks self-evict on completion so
        // the dictionary doesn't leak.
        private readonly Dictionary<string, Task> _inflight = new();

        // Batch counters used by a network-progress HUD. A "batch" is the
        // contiguous run of fetches between idle moments — once every queued task
        // finishes, the counters reset to zero so the next batch starts clean.
        public int BatchTotal { get; private set; }
        public int BatchDone { get; private set; }
        public string LastStartedUrl { get; private set; }
        public bool BatchActive => BatchTotal > 0 && BatchDone < BatchTotal;

        // True while VerifyAsync is scanning local cache — the HUD shows a
        // "verifying files" state instead of a filename.
        public bool IsVerifying { get; private set; }

        // Retry count per url (1 = first try, 2+ = previous attempts failed).
        // Surfaced to the HUD so it can show "attempt N" on a flaky network.
        private readonly Dictionary<string, int> _attempts = new();

        // Session-scoped sprite cache. Sprites are keyed by URL so the same
        // background or portrait is decoded once — and BOUNDED: full-res RGBA32
        // decodes are big (a 1080p background ≈ 8 MB), so an unbounded cache is an
        // OOM on a large title. Over budget, the least-recently-requested entries
        // are destroyed — except anything touched within the grace window, which
        // is how "probably still on screen" art is protected without a pin API.
        private sealed class SpriteEntry
        {
            public Sprite Sprite;
            public long Bytes;
            public long Seq;   // request recency (monotonic)
            public float At;   // request time (realtime seconds)
        }

        private readonly Dictionary<string, SpriteEntry> _spriteCache = new();
        private readonly Dictionary<string, Task<Sprite>> _decoding = new();
        private long _spriteSeq;
        private long _spriteBytes;

        /// <summary>Decoded-sprite memory budget. Over it, the least-recently-used
        /// sprites are evicted (grace-protected — see <see cref="SpriteEvictionGraceSeconds"/>).
        /// Tune down for low-memory targets.</summary>
        public static long SpriteCacheBudgetBytes = 384L << 20;

        /// <summary>Entries requested within this window are never evicted — art
        /// requested recently is very likely still on screen.</summary>
        public static float SpriteEvictionGraceSeconds = 60f;
        public int AttemptOf(string url) =>
            url != null && _attempts.TryGetValue(url, out var n) ? n : 1;

        // Author-supplied display labels for urls, persistent across the session.
        private readonly Dictionary<string, string> _aliases = new();
        public void RegisterAlias(string url, string alias)
        {
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(alias)) return;
            lock (_aliases) _aliases[url] = alias;
        }
        public string AliasOf(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            lock (_aliases)
            {
                if (_aliases.TryGetValue(url, out var a)) return a;
                // Aliases are stored as relative paths (/content/...) but url may
                // be an absolute URL — try matching by path only.
                try
                {
                    var path = new System.Uri(url).AbsolutePath;
                    return _aliases.TryGetValue(path, out var b) ? b : null;
                }
                catch { return null; }
            }
        }

        // Per-url byte progress, updated each frame while a fetch runs. Lets the
        // HUD show byte-level progress instead of file-count progress, which
        // feels stuck when a single file downloads for many seconds.
        private readonly Dictionary<string, long> _bytesExpected = new();
        private readonly Dictionary<string, long> _bytesReceived = new();

        // Label of the file currently being fetched (alias or short name).
        public string CurrentFileLabel { get; private set; }
        public long BatchBytesExpected
        {
            get { lock (_inflight) { long s = 0; foreach (var v in _bytesExpected.Values) s += v; return s; } }
        }
        public long BatchBytesReceived
        {
            get { lock (_inflight) { long s = 0; foreach (var v in _bytesReceived.Values) s += v; return s; } }
        }

        public ContentLoader(string baseUrl, string cacheRoot = null)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _local = _baseUrl.StartsWith("file://") || _baseUrl.StartsWith("jar:");
            cacheRoot ??= Path.Combine(Application.persistentDataPath, "cache");
            _cacheRoot = cacheRoot;
            _scriptCacheDir = Path.Combine(cacheRoot, "scripts");
            _assetCacheDir = Path.Combine(cacheRoot, "assets");
            Directory.CreateDirectory(_scriptCacheDir);
            Directory.CreateDirectory(_assetCacheDir);
            SweepStaleParts();
        }

        // Resume files (.part) enable interrupted downloads to continue — but one
        // abandoned mid-download (its version has moved on, so its cache key will
        // never be requested again) would sit on disk forever. Sweep any not
        // touched for a week; a live download re-creates its .part instantly.
        private void SweepStaleParts()
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddDays(-7);
                foreach (var f in new DirectoryInfo(_assetCacheDir).GetFiles("*.part"))
                    if (f.LastWriteTimeUtc < cutoff)
                        try { f.Delete(); } catch { }
            }
            catch { /* best-effort housekeeping */ }
        }

        /// <summary>Lightweight connectivity probe: GET <c>&lt;baseUrl&gt;/healthz</c>.
        /// Returns true and marks the process online on a 2xx; returns false on any
        /// error, non-2xx or cancellation WITHOUT flipping the global flag (the
        /// caller decides whether to <see cref="LvnNetworkStatus.MarkOffline"/>), so
        /// a cancelled probe never poisons a still-good connection. A local
        /// (<c>file://</c>) origin is always reachable → true.
        ///
        /// <para>Pass a token with a hard deadline (e.g. <c>CancelAfter(3s)</c>):
        /// <c>UnityWebRequest.timeout</c> alone doesn't reliably interrupt a stall at
        /// DNS/TLS setup (a dead VPN), so the loop aborts on the token instead — the
        /// difference between an instant offline fallback and a ~30s boot hang.</para></summary>
        public async Task<bool> HealthzAsync(string path = "/healthz", CancellationToken ct = default)
        {
            if (_local) return true;
            try
            {
                using var req = UnityWebRequest.Get(ResolveUrl(path));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.timeout = RequestTimeoutSeconds;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                req.certificateHandler = new AcceptAllCertificates();
#endif
                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    if (ct.IsCancellationRequested) { req.Abort(); return false; }
                    await Task.Yield();
                }
                bool ok = req.result is not (UnityWebRequest.Result.ConnectionError
                                          or UnityWebRequest.Result.DataProcessingError)
                          && req.responseCode is >= 200 and < 300;
                if (ok) LvnNetworkStatus.MarkOnline("healthz ok");
                return ok;
            }
            catch { return false; }
        }

        /// <summary>Fetches the server's content-version index (path → sha256) and
        /// folds it into the disk-cache key, so changed assets auto-invalidate.
        /// Call once early in boot, before the verify/preload pass. Always fetched
        /// fresh (never disk-cached) and mirrored to disk so a later offline
        /// launch can still resolve versioned cache keys. Network failure is
        /// non-fatal: fall back to the last persisted index, else legacy url-only
        /// keys.</summary>
        public async Task LoadAssetVersionsAsync(CancellationToken ct = default)
        {
            var persistPath = Path.Combine(_cacheRoot, "asset-versions.json");
            try
            {
                // Single attempt (not Fetch's retry-with-backoff): if we're
                // offline this fails fast on host-resolve instead of stalling
                // boot, and we immediately fall back to the disk mirror.
                var bytes = await FetchOnce(VersionsPath, ct);
                var map = ParseVersions(Encoding.UTF8.GetString(bytes));
                if (map.Count > 0)
                {
                    Dictionary<string, string> prev;
                    lock (_versionsLock) { prev = _versions; _versions = map; }
                    EvictStaleSprites(prev, map);
                    try { await WriteAllBytesAsync(persistPath, bytes, ct); } catch { /* mirror is best-effort */ }
                    return;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* offline / 404 — fall back to last-known index below */ }

            try
            {
                if (File.Exists(persistPath))
                {
                    var json = Encoding.UTF8.GetString(await ReadAllBytesAsync(persistPath, ct));
                    var map = ParseVersions(json);
                    if (map.Count > 0) lock (_versionsLock) _versions = map;
                }
            }
            catch { /* no usable index — legacy url-only keys */ }
        }

        private static Dictionary<string, string> ParseVersions(string json)
        {
            try
            {
                var map = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                return map ?? new Dictionary<string, string>();
            }
            catch { return new Dictionary<string, string>(); }
        }

        // sha256 for a content url from the version index, or null if unknown.
        // Index keys are content-relative paths ("bg/ch1/x.png"); urls arrive as
        // "/content/bg/ch1/x.png" (or absolute urls). Try both the post-/content/
        // form and the raw-with-"content/" form.
        private string VersionFor(string url)
        {
            Dictionary<string, string> map;
            lock (_versionsLock) map = _versions;
            return Lookup(map, url);
        }

        private static string Lookup(Dictionary<string, string> map, string url)
        {
            if (string.IsNullOrEmpty(url) || map == null || map.Count == 0) return null;
            var path = url;
            if (path.StartsWith("http://") || path.StartsWith("https://"))
            {
                try { path = new System.Uri(path).AbsolutePath; } catch { }
            }
            var p = path.TrimStart('/');                                  // content/bg/... or bg/...
            var afterContent = p.StartsWith("content/") ? p.Substring("content/".Length) : p;
            if (map.TryGetValue(afterContent, out var v)) return v;
            if (map.TryGetValue(p, out var v2)) return v2;
            return null;
        }

        // When the version index changes (a live content update), any in-memory sprite
        // whose content hash moved is stale — the memory cache is url-keyed, so it would
        // otherwise keep handing back the OLD art forever. Evict exactly those, so the
        // next load (e.g. a live ReplayVisuals) decodes the replaced file.
        private void EvictStaleSprites(Dictionary<string, string> oldMap, Dictionary<string, string> newMap)
        {
            List<string> stale = null;
            lock (_spriteCache)
            {
                foreach (var url in _spriteCache.Keys)
                    if (Lookup(oldMap, url) != Lookup(newMap, url))
                        (stale ??= new List<string>()).Add(url);
            }
            if (stale != null) foreach (var u in stale) Unload(u);
        }

        // Scripts ship from the server and change often — skip the on-disk cache
        // and refetch every time (a few KB, cheap; stale copies cause "why is the
        // old version playing" bugs). `singleAttempt` skips the retry/backoff loop
        // — use it for non-critical boot fetches so an offline launch fails fast.
        public async Task<string> DownloadScriptText(string scriptUrl, CancellationToken ct = default,
            bool singleAttempt = false)
        {
            var bytes = singleAttempt
                ? await FetchOnce(scriptUrl, ct)
                : await Fetch(scriptUrl, ct);
            return Encoding.UTF8.GetString(bytes);
        }

        // Version-pinned script load for chapter playback. Unlike
        // DownloadScriptText (always-fresh, no disk cache) this CACHES the script
        // on disk under a version-folded key, so a chapter opens OFFLINE if ever
        // played online, the version is pinned for the whole session, and an
        // edited script (new hash → new key) is re-downloaded on the next entry.
        // Returns null only if there's no cache AND we can't fetch.
        public async Task<string> DownloadScriptCached(string scriptUrl, CancellationToken ct = default)
        {
            var path = CachePath(_scriptCacheDir, scriptUrl, ".txt");
            if (File.Exists(path))
            {
                try { return await ReadAllTextAsync(path, ct); }
                catch { /* unreadable — fall through to refetch */ }
            }
            try
            {
                var bytes = await FetchOnce(scriptUrl, ct);
                try
                {
                    await WriteAllBytesAsync(path, bytes, ct);
                    await WriteScriptUrlSidecar(path, scriptUrl, ct);
                }
                catch { /* cache write best-effort */ }
                return Encoding.UTF8.GetString(bytes);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Offline and not cached for this version. Last resort: a previously
                // cached version OF THE SAME url (older but the right chapter).
                var stale = NewestCachedScript(scriptUrl);
                if (stale != null)
                {
                    try { return await ReadAllTextAsync(stale, ct); } catch { }
                }
                return null;
            }
        }

        // Fire-and-forget: pull the latest version of a script to disk so the
        // NEXT chapter entry picks it up. `reloadIndex` re-reads the (no-store)
        // version index first to detect a hash published since boot.
        public void RefreshScriptInBackground(string scriptUrl, bool reloadIndex = true)
        {
            if (string.IsNullOrEmpty(scriptUrl)) return;
            _ = RefreshScriptAsync(scriptUrl, reloadIndex);
        }

        private async Task RefreshScriptAsync(string scriptUrl, bool reloadIndex)
        {
            try
            {
                if (reloadIndex)
                    await LoadAssetVersionsAsync(CancellationToken.None);
                var path = CachePath(_scriptCacheDir, scriptUrl, ".txt");
                if (File.Exists(path)) return; // newest version already cached
                var bytes = await FetchOnce(scriptUrl, CancellationToken.None);
                await WriteAllBytesAsync(path, bytes, CancellationToken.None);
                await WriteScriptUrlSidecar(path, scriptUrl, CancellationToken.None);
                Debug.Log($"[content] script cache refreshed: {scriptUrl}");
            }
            catch { /* best-effort background refresh */ }
        }

        // Finds the most recently written cached version OF THE SAME script url —
        // the offline fallback. The version-folded filename (sha1(url@version))
        // can't be reversed, so each cached script is written with a `.url` sidecar
        // holding its plain url; we only accept a `.txt` whose sidecar matches the
        // requested url. Without this the fallback returned whatever chapter was
        // cached most recently — silently dropping the player into the wrong
        // chapter and saving the wrong ending. Returns null (→ Unavailable) rather
        // than ever serving a different script.
        private string NewestCachedScript(string scriptUrl)
        {
            if (string.IsNullOrEmpty(scriptUrl)) return null;
            try
            {
                var dir = new DirectoryInfo(_scriptCacheDir);
                if (!dir.Exists) return null;
                FileInfo newest = null;
                foreach (var f in dir.GetFiles("*.txt"))
                {
                    var sidecar = Path.ChangeExtension(f.FullName, ".url");
                    string cachedUrl = null;
                    try { if (File.Exists(sidecar)) cachedUrl = File.ReadAllText(sidecar).Trim(); }
                    catch { }
                    if (cachedUrl != scriptUrl) continue; // different (or legacy, un-tagged) script
                    if (newest == null || f.LastWriteTimeUtc > newest.LastWriteTimeUtc) newest = f;
                }
                return newest?.FullName;
            }
            catch { return null; }
        }

        // Records the plain url of a just-cached script beside its version-folded
        // cache file, so the offline fallback can match cached versions to the
        // requested url (see NewestCachedScript).
        private static async Task WriteScriptUrlSidecar(string scriptPath, string scriptUrl, CancellationToken ct)
        {
            try
            {
                await WriteAllBytesAsync(Path.ChangeExtension(scriptPath, ".url"),
                    Encoding.UTF8.GetBytes(scriptUrl), ct);
            }
            catch { /* sidecar is best-effort; a missing one just disables offline fallback for this file */ }
        }

        public Task<byte[]> DownloadAssetBytes(string assetUrl, CancellationToken ct = default) =>
            DownloadBytes(assetUrl, _assetCacheDir, ct);

        /// <summary>Loads (or fetches and caches) the URL, decodes the bytes into
        /// a texture, and wraps it as a Sprite. Returns null on missing data.
        /// Concurrent requests for the same url share ONE decode (no leaked
        /// Texture2D from a lost race), and the cache is LRU-bounded by
        /// <see cref="SpriteCacheBudgetBytes"/>.</summary>
        public Task<Sprite> DownloadSpriteAsync(string url, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(url)) return Task.FromResult<Sprite>(null);
            lock (_spriteCache)
            {
                if (_spriteCache.TryGetValue(url, out var hit) && hit.Sprite != null)
                {
                    Touch(hit);
                    return Task.FromResult(hit.Sprite);
                }
                // Someone is already decoding this url — share their result instead
                // of decoding a second texture and leaking the loser.
                if (_decoding.TryGetValue(url, out var inflight)) return inflight;
                var task = DecodeSpriteAsync(url, ct);
                _decoding[url] = task;
                return task;
            }
        }

        private async Task<Sprite> DecodeSpriteAsync(string url, CancellationToken ct)
        {
            try
            {
                var bytes = await DownloadAssetBytes(url, ct);
                if (bytes == null || bytes.Length == 0) return null;
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
                if (!tex.LoadImage(bytes))
                {
                    UnityEngine.Object.Destroy(tex);
                    return null;
                }
                tex.wrapMode   = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Bilinear;
                tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
                var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);

                List<SpriteEntry> victims;
                lock (_spriteCache)
                {
                    var e = new SpriteEntry { Sprite = sprite, Bytes = (long)tex.width * tex.height * 4 };
                    Touch(e);
                    _spriteCache[url] = e;
                    _spriteBytes += e.Bytes;
                    victims = EvictOverBudgetLocked();
                }
                foreach (var v in victims) DestroySprite(v.Sprite);
                return sprite;
            }
            finally
            {
                lock (_spriteCache) _decoding.Remove(url);
            }
        }

        private void Touch(SpriteEntry e)
        {
            e.Seq = ++_spriteSeq;
            e.At = Time.realtimeSinceStartup;
        }

        // Must run under the _spriteCache lock. Returns the evicted entries so the
        // caller destroys their textures OUTSIDE the lock.
        private List<SpriteEntry> EvictOverBudgetLocked()
        {
            var victims = new List<SpriteEntry>();
            if (_spriteBytes <= SpriteCacheBudgetBytes) return victims;
            float now = Time.realtimeSinceStartup;
            foreach (var url in PickEvictions(
                         SnapshotLocked(), SpriteCacheBudgetBytes, now, SpriteEvictionGraceSeconds))
            {
                if (!_spriteCache.TryGetValue(url, out var e)) continue;
                _spriteCache.Remove(url);
                _spriteBytes -= e.Bytes;
                victims.Add(e);
            }
            return victims;
        }

        private List<(string url, long bytes, long seq, float at)> SnapshotLocked()
        {
            var list = new List<(string, long, long, float)>(_spriteCache.Count);
            foreach (var kv in _spriteCache)
                list.Add((kv.Key, kv.Value.Bytes, kv.Value.Seq, kv.Value.At));
            return list;
        }

        /// <summary>Pure eviction policy, exposed for tests: evict oldest-requested
        /// first until the total fits the budget, skipping anything requested within
        /// the grace window (it's very likely still on screen).</summary>
        internal static List<string> PickEvictions(
            List<(string url, long bytes, long seq, float at)> entries,
            long budgetBytes, float now, float graceSeconds)
        {
            var evict = new List<string>();
            long total = 0;
            foreach (var e in entries) total += e.bytes;
            if (total <= budgetBytes) return evict;
            entries.Sort((a, b) => a.seq.CompareTo(b.seq)); // oldest request first
            foreach (var e in entries)
            {
                if (total <= budgetBytes) break;
                if (now - e.at < graceSeconds) continue; // recently used — protected
                evict.Add(e.url);
                total -= e.bytes;
            }
            return evict;
        }

        /// <summary>Synchronous in-memory lookup — returns true (and the sprite)
        /// only if it's ALREADY decoded in the sprite cache, never touching disk
        /// or wire. Lets a view paint a warmed sprite on the very first frame
        /// instead of awaiting DownloadSpriteAsync (which, even from disk, costs a
        /// decode + a frame or two).</summary>
        public bool TryGetSprite(string url, out Sprite sprite)
        {
            sprite = null;
            if (string.IsNullOrEmpty(url)) return false;
            lock (_spriteCache)
            {
                if (!_spriteCache.TryGetValue(url, out var e) || e.Sprite == null) return false;
                Touch(e);
                sprite = e.Sprite;
                return true;
            }
        }

        /// <summary>Releases the in-memory sprite cached for a single url and
        /// destroys its texture. Safe to call if the url was never loaded. The
        /// disk cache is left intact (a later load re-decodes from disk).</summary>
        public void Unload(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            SpriteEntry entry;
            lock (_spriteCache)
            {
                if (!_spriteCache.TryGetValue(url, out entry)) return;
                _spriteCache.Remove(url);
                _spriteBytes -= entry.Bytes;
            }
            DestroySprite(entry.Sprite);
        }

        /// <summary>Releases every cached sprite whose url matches — e.g. a chapter's
        /// art/backgrounds on chapter exit, keeping UI covers/skins warm.</summary>
        public void UnloadWhere(Func<string, bool> match)
        {
            if (match == null) return;
            var victims = new List<SpriteEntry>();
            lock (_spriteCache)
            {
                var keys = new List<string>(_spriteCache.Keys);
                foreach (var k in keys)
                {
                    if (!match(k)) continue;
                    victims.Add(_spriteCache[k]);
                    _spriteBytes -= _spriteCache[k].Bytes;
                    _spriteCache.Remove(k);
                }
            }
            foreach (var v in victims) DestroySprite(v.Sprite);
        }

        /// <summary>Releases every in-memory sprite and destroys its texture. Call
        /// on a scene transition or app exit to free GPU memory. The disk cache is
        /// untouched.</summary>
        public void UnloadAll()
        {
            List<SpriteEntry> entries;
            lock (_spriteCache)
            {
                entries = new List<SpriteEntry>(_spriteCache.Values);
                _spriteCache.Clear();
                _spriteBytes = 0;
            }
            foreach (var e in entries) DestroySprite(e.Sprite);
        }

        private static void DestroySprite(Sprite sprite)
        {
            if (sprite == null) return;
            if (sprite.texture != null) UnityEngine.Object.Destroy(sprite.texture);
            UnityEngine.Object.Destroy(sprite);
        }

        /// <summary>Downloads an audio asset through UnityWebRequestMultimedia so
        /// the engine decodes the format streaming-style on the main thread (the
        /// correct path — never hand-roll a PCM parser). Caches the raw bytes on
        /// disk, then loads the clip from the cached file.</summary>
        public async Task<AudioClip> DownloadAudioClipAsync(string url, CancellationToken ct = default)
        {
            var path = CachePath(_assetCacheDir, url, ".audio");
            if (!File.Exists(path))
            {
                var bytes = await Fetch(url, ct);
                await WriteAllBytesAsync(path, bytes, ct);
            }
            var fileUrl = "file://" + path;
            var type = GuessAudioType(url);
            using var req = UnityWebRequestMultimedia.GetAudioClip(fileUrl, type);

            var op = req.SendWebRequest();
            while (!op.isDone)
            {
                if (ct.IsCancellationRequested)
                {
                    req.Abort();
                    throw new OperationCanceledException(ct);
                }
                await Task.Yield();
            }
            if (req.result is UnityWebRequest.Result.ConnectionError or UnityWebRequest.Result.DataProcessingError)
                return null;
            return DownloadHandlerAudioClip.GetContent(req);
        }

        private static AudioType GuessAudioType(string url)
        {
            var lower = url.ToLowerInvariant();
            if (lower.EndsWith(".ogg")) return AudioType.OGGVORBIS;
            if (lower.EndsWith(".wav")) return AudioType.WAV;
            if (lower.EndsWith(".mp3")) return AudioType.MPEG;
            return AudioType.UNKNOWN;
        }

        /// <summary>Kicks off a background fetch for <paramref name="url"/> with
        /// the given <paramref name="kind"/> ("sprite"|"audio"|"script"|"bin").
        /// Idempotent — if the same url is already being prefetched (or cached on
        /// disk) this is essentially a no-op. Returns the underlying task so
        /// callers can await it.</summary>
        public Task Prefetch(string url, string kind, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(url)) return Task.CompletedTask;
            return kind switch
            {
                "script" => DownloadScriptText(url, ct),
                _ => DownloadAssetBytes(url, ct),
            };
        }

        /// <summary>Downloads a list of assets, pipelining disk writes with the
        /// next file's network setup so the progress bar shows smooth overall
        /// progress and there's no idle gap between files. Files already on disk
        /// are skipped (they don't inflate the total). Returns a Task the caller
        /// can await; <see cref="WaitForAll"/>(null) also works.</summary>
        public Task StartPreloadBatch(IReadOnlyList<PreloadItem> assets, CancellationToken ct)
        {
            if (assets == null || assets.Count == 0) return Task.CompletedTask;

            // Register all aliases up-front so the HUD label is ready the moment a
            // fetch starts (no one-frame flash of raw URL).
            foreach (var a in assets)
                if (!string.IsNullOrEmpty(a.Alias) && !string.IsNullOrEmpty(a.Url))
                    lock (_aliases) _aliases[a.Url] = a.Alias;

            // Count how many files are actually missing from disk cache.
            var pending = new List<PreloadItem>(assets.Count);
            foreach (var a in assets)
            {
                if (string.IsNullOrEmpty(a.Url)) continue;
                var path = CachePath(_assetCacheDir, a.Url, ".bin");
                if (!File.Exists(path)) pending.Add(a);
            }
            if (pending.Count == 0) return Task.CompletedTask;

            const string batchKey = "__preload_batch__";
            Task<byte[]> batchTask;
            lock (_inflight)
            {
                if (_inflight.ContainsKey(batchKey)) return _inflight[batchKey];
                BatchTotal     = pending.Count;
                BatchDone      = 0;
                batchTask      = RunBatchAsync(pending, ct);
                _inflight[batchKey] = batchTask;
                LastStartedUrl = pending[0].Url;
            }
            _ = batchTask.ContinueWith(_ =>
            {
                lock (_inflight)
                {
                    _inflight.Remove(batchKey);
                    BatchTotal     = 0;
                    BatchDone      = 0;
                    LastStartedUrl = null;
                    _attempts.Clear();
                    _bytesExpected.Clear();
                    _bytesReceived.Clear();
                }
            }, TaskScheduler.Default);
            return batchTask;
        }

        private async Task<byte[]> RunBatchAsync(List<PreloadItem> pending, CancellationToken ct)
        {
            // Pipeline: at 90% of file N, warm-start file N+1 via a silent
            // prefetch (no progress counters — avoids the bar jumping backward).
            // By the time N finishes, N+1's TCP/TLS is already up and data is
            // flowing, so there's no idle gap between files.
            Task diskTask = Task.CompletedTask;
            Task<byte[]> prefetchTask = null;
            string       prefetchUrl  = null; // URL the prefetch is downloading

            for (int i = 0; i < pending.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var asset = pending[i];
                var path  = CachePath(_assetCacheDir, asset.Url, ".bin");

                if (File.Exists(path)) { lock (_inflight) BatchDone++; continue; }

                CurrentFileLabel = AliasOf(asset.Url);
                LastStartedUrl   = asset.Url;

                const int MaxRetries = 10;
                byte[] body = null;
                int attempt = 1;
                while (true)
                {
                    try
                    {
                        lock (_inflight) _attempts[asset.Url] = attempt;

                        // Reuse warm prefetch if it was for this URL and didn't fault.
                        Task<byte[]> fetchTask;
                        if (prefetchUrl == asset.Url &&
                            prefetchTask is { IsFaulted: false, IsCanceled: false })
                        {
                            fetchTask    = prefetchTask;
                            prefetchTask = null;
                            prefetchUrl  = null;
                        }
                        else
                        {
                            if (prefetchUrl == asset.Url) { prefetchTask = null; prefetchUrl = null; }
                            fetchTask = FetchToMemory(asset.Url, ct);
                        }

                        // Drive the download; fire a silent warm-start for the next
                        // file once this one crosses 90%.
                        while (!fetchTask.IsCompleted)
                        {
                            ct.ThrowIfCancellationRequested();

                            if (prefetchUrl == null) // not yet decided for next file
                            {
                                long exp, rec;
                                lock (_inflight)
                                {
                                    exp = _bytesExpected.GetValueOrDefault(asset.Url);
                                    rec = _bytesReceived.GetValueOrDefault(asset.Url);
                                }
                                if (exp > 0 && (float)rec / exp >= 0.9f)
                                {
                                    var nextUrl = FindNextUncachedUrl(pending, i + 1);
                                    prefetchUrl  = nextUrl ?? ""; // "" = nothing to prefetch
                                    if (nextUrl != null)
                                        prefetchTask = FetchToMemoryPrefetch(nextUrl, ct);
                                }
                            }

                            await Task.Yield();
                        }

                        body = await fetchTask;
                        // Same integrity rule as DownloadBytes: never cache bytes
                        // that don't match the version index's sha256.
                        var expect = VersionFor(asset.Url);
                        if (body != null && expect != null && !Sha256Matches(body, expect))
                            throw new LvnFetchException(0, "integrity",
                                "sha256 mismatch for " + asset.Url + " — refetching");
                        if (prefetchUrl == "") prefetchUrl = null;
                        break;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (LvnFetchException ex) when (ex.Status is >= 400 and < 500)
                    {
                        Debug.LogWarning($"[content] preload {asset.Url} permanent {ex.Status}");
                        if (prefetchUrl == "") prefetchUrl = null;
                        break;
                    }
                    catch (Exception ex)
                    {
                        attempt++;
                        if (attempt > MaxRetries)
                        {
                            Debug.LogWarning($"[content] preload {asset.Url} gave up after {MaxRetries} attempts");
                            if (prefetchUrl == "") prefetchUrl = null;
                            break;
                        }
                        var backoff = LvnBackoff.DelaySeconds(attempt);
                        Debug.LogWarning($"[content] preload {asset.Url} attempt {attempt}, retry in {backoff:F1}s: {ex.Message}");
                        try { await Task.Delay(Mathf.RoundToInt(backoff * 1000f), ct); }
                        catch (OperationCanceledException) { throw; }
                    }
                }

                var capPath = path;
                var capBody = body;
                await diskTask;
                diskTask = capBody != null
                    // Write atomically (staged temp + move): a crash mid-write must
                    // not leave a truncated .bin, which File.Exists would then treat
                    // as a valid cache entry forever (permanent boot-art corruption).
                    ? Task.Run(() => AtomicWriteAllBytes(capPath, capBody), CancellationToken.None)
                    : Task.CompletedTask;

                lock (_inflight) BatchDone++;
            }

            await diskTask;
            CurrentFileLabel = null;
            return null;
        }

        // Returns the URL of the first file in pending[fromIdx..] not yet on disk.
        private string FindNextUncachedUrl(List<PreloadItem> pending, int fromIdx)
        {
            for (int j = fromIdx; j < pending.Count; j++)
                if (!File.Exists(CachePath(_assetCacheDir, pending[j].Url, ".bin")))
                    return pending[j].Url;
            return null;
        }

        // Silent prefetch variant: does NOT update the byte counters so the
        // progress bar doesn't see the parallel warm-start and jump backward.
        private async Task<byte[]> FetchToMemoryPrefetch(string url, CancellationToken ct)
        {
            ThrowIfOffline();
            await _downloadSlots.WaitAsync(ct);
            try
            {
                var full = ResolveUrl(url);
                using var req = UnityWebRequest.Get(full);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.timeout = RequestTimeoutSeconds;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                req.certificateHandler = new AcceptAllCertificates();
#endif
                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    if (ct.IsCancellationRequested) { req.Abort(); throw new OperationCanceledException(ct); }
                    await Task.Yield();
                }
                if (req.result is UnityWebRequest.Result.ConnectionError
                               or UnityWebRequest.Result.DataProcessingError)
                {
                    MarkOfflineUnlessLocal("content fetch network error");
                    throw new LvnFetchException((int)req.responseCode, "network", req.error ?? "network error");
                }
                if (req.responseCode is < 200 or >= 300)
                    throw new LvnFetchException((int)req.responseCode, "http_" + req.responseCode, $"GET {full}");
                return req.downloadHandler.data ?? Array.Empty<byte>();
            }
            finally { _downloadSlots.Release(); }
        }

        // Downloads url into memory, updating byte-progress counters. No disk I/O
        // — used by RunBatchAsync so disk writes can be pipelined.
        private async Task<byte[]> FetchToMemory(string url, CancellationToken ct)
        {
            ThrowIfOffline();
            await _downloadSlots.WaitAsync(ct);
            try
            {
                var full = ResolveUrl(url);
                lock (_inflight) { _bytesReceived[url] = 0; _bytesExpected[url] = 0; }

                using var req = UnityWebRequest.Get(full);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.timeout = RequestTimeoutSeconds;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                req.certificateHandler = new AcceptAllCertificates();
#endif
                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    if (ct.IsCancellationRequested) { req.Abort(); throw new OperationCanceledException(ct); }
                    lock (_inflight) _bytesReceived[url] = (long)req.downloadedBytes;
                    if (_bytesExpected.GetValueOrDefault(url) == 0)
                    {
                        var cl = req.GetResponseHeader("Content-Length");
                        if (cl != null && long.TryParse(cl, out var sz) && sz > 0)
                            lock (_inflight) _bytesExpected[url] = sz;
                    }
                    await Task.Yield();
                }

                if (req.result is UnityWebRequest.Result.ConnectionError
                               or UnityWebRequest.Result.DataProcessingError)
                {
                    MarkOfflineUnlessLocal("content fetch network error");
                    throw new LvnFetchException((int)req.responseCode, "network", req.error ?? "network error");
                }
                if (req.responseCode is < 200 or >= 300)
                    throw new LvnFetchException((int)req.responseCode, "http_" + req.responseCode, $"GET {full}");

                var body = req.downloadHandler.data ?? Array.Empty<byte>();
                lock (_inflight) { _bytesReceived[url] = body.Length; _bytesExpected[url] = body.Length; }
                return body;
            }
            finally { _downloadSlots.Release(); }
        }

        /// <summary>Waits until either the listed urls finish prefetching, or (if
        /// <paramref name="urls"/> is null) until the whole batch settles — no
        /// task in flight and the counters reset. Polling BatchActive catches
        /// tasks that join the queue mid-wait.</summary>
        public async Task WaitForAll(IEnumerable<string> urls, CancellationToken ct = default)
        {
            if (urls == null)
            {
                while (BatchActive)
                {
                    ct.ThrowIfCancellationRequested();
                    try { await Task.Delay(50, ct); }
                    catch (OperationCanceledException) { throw; }
                }
                return;
            }
            List<Task> tasks;
            lock (_inflight)
            {
                tasks = urls.Where(u => _inflight.ContainsKey(u))
                            .Select(u => _inflight[u]).ToList();
            }
            if (tasks.Count == 0) return;
            try { await Task.WhenAll(tasks).WithCancellation(ct); }
            catch (OperationCanceledException) { throw; }
            catch { /* individual asset failures don't block the wait */ }
        }

        /// <summary>True if at least one asset has been downloaded and cached
        /// locally. Used to decide whether to show the verify phase on startup.</summary>
        public bool HasCachedAssets()
        {
            try
            {
                return Directory.Exists(_assetCacheDir) &&
                       Directory.EnumerateFiles(_assetCacheDir, "*.bin").Any();
            }
            catch { return false; }
        }

        /// <summary>True when the content origin is a local bundle (StreamingAssets
        /// via file://). For the offline policy this means everything is "cached"
        /// and always reachable, so a bundled build lands on ReadyFromCache.</summary>
        public bool IsLocal => _local;

        /// <summary>True if the version-pinned script for <paramref name="scriptUrl"/>
        /// is on disk. Pure disk check (no network) — used by the offline policy.
        /// A local bundle is authoritative and complete, so it always reports true.</summary>
        public bool IsScriptCached(string scriptUrl)
        {
            if (string.IsNullOrEmpty(scriptUrl)) return false;
            if (_local) return true;
            try { return File.Exists(CachePath(_scriptCacheDir, scriptUrl, ".txt")); }
            catch { return false; }
        }

        /// <summary>True if the asset bytes for <paramref name="url"/> are on disk
        /// under the current version key. Pure disk check (no network). A local
        /// bundle reports true (the asset ships inside the build).</summary>
        public bool IsAssetCached(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            if (_local) return true;
            try { return File.Exists(CachePath(_assetCacheDir, url, ".bin")); }
            catch { return false; }
        }

        /// <summary>Scans <paramref name="urls"/> against the local asset cache
        /// and returns the subset that are missing. Sets IsVerifying during the
        /// scan so the HUD can show a verifying state instead of filenames.</summary>
        public async Task<IReadOnlyList<string>> VerifyAsync(
            IReadOnlyList<string> urls, CancellationToken ct)
        {
            if (urls == null || urls.Count == 0) return Array.Empty<string>();
            IsVerifying = true;
            lock (_inflight)
            {
                BatchTotal       = urls.Count;
                BatchDone        = 0;
                CurrentFileLabel = null;
                LastStartedUrl   = null;
            }
            var missing = new List<string>();
            foreach (var url in urls)
            {
                try { ct.ThrowIfCancellationRequested(); }
                catch (OperationCanceledException) { IsVerifying = false; throw; }
                if (!File.Exists(CachePath(_assetCacheDir, url, ".bin")))
                    missing.Add(url);
                lock (_inflight) BatchDone++;
                try { await Task.Yield(); }
                catch (OperationCanceledException) { IsVerifying = false; throw; }
            }
            lock (_inflight)
            {
                BatchTotal       = 0;
                BatchDone        = 0;
                CurrentFileLabel = null;
                LastStartedUrl   = null;
            }
            IsVerifying = false;
            return missing;
        }

        private async Task<byte[]> DownloadBytes(string url, string dir, CancellationToken ct)
        {
            var path     = CachePath(dir, url, ".bin");
            var partPath = path + ".part";

            if (File.Exists(path))
                return await ReadAllBytesAsync(path, ct);

            return await TrackedFetch(url, async () =>
            {
                const int MaxAttempts = 10;
                lock (_inflight) _attempts[url] = 1;

                while (true)
                {
                    try
                    {
                        // Each retry reads the current .part size → resumes from there.
                        long resumeFrom = 0;
                        if (File.Exists(partPath))
                            try { resumeFrom = new FileInfo(partPath).Length; } catch { }

                        var bytes = await FetchResumable(url, partPath, resumeFrom, ct);

                        // Integrity: the version index carries each asset's sha256.
                        // A torn resume (server changed the file between two Range
                        // requests) would otherwise cache spliced bytes as valid
                        // forever. Mismatch → drop the .part and refetch clean.
                        var expect = VersionFor(url);
                        if (expect != null && !Sha256Matches(bytes, expect))
                        {
                            try { File.Delete(partPath); } catch { }
                            throw new LvnFetchException(0, "integrity",
                                "sha256 mismatch for " + url + " — refetching");
                        }

                        lock (_inflight) _attempts.Remove(url);

                        if (File.Exists(path)) File.Delete(path);
                        File.Move(partPath, path);
                        return bytes;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (LvnFetchException ex) when (ex.Code == "network" && LvnNetworkStatus.IsOffline)
                    {
                        throw; // offline — retrying is pointless; caller falls back to cache
                    }
                    catch (LvnFetchException ex) when (ex.Status is >= 400 and < 500)
                    {
                        Debug.LogWarning($"[content] {url} permanent {ex.Status}");
                        throw;
                    }
                    catch (Exception ex)
                    {
                        int attempt;
                        lock (_inflight) attempt = _attempts[url] = _attempts.GetValueOrDefault(url, 1) + 1;
                        if (attempt > MaxAttempts)
                        {
                            Debug.LogWarning($"[content] {url} gave up after {MaxAttempts} attempts");
                            throw;
                        }
                        var backoff = LvnBackoff.DelaySeconds(attempt);
                        Debug.LogWarning($"[content] {url} attempt {attempt} failed, resume in {backoff:F1}s: {ex.Message}");
                        try { await Task.Delay(Mathf.RoundToInt(backoff * 1000f), ct); }
                        catch (OperationCanceledException) { throw; }
                    }
                }
            });
        }

        // Single streaming GET for the whole file. If resumeFrom > 0 sends
        // Range: bytes=N- so the server picks up from that offset. One HTTP
        // request per file — no chunk loop, no extra round-trips.
        private async Task<byte[]> FetchResumable(string url, string partPath, long resumeFrom, CancellationToken ct)
        {
            ThrowIfOffline();
            await _downloadSlots.WaitAsync(ct);
            try
            {
                var full = ResolveUrl(url);
                lock (_inflight) { _bytesReceived[url] = resumeFrom; }

                using var req = UnityWebRequest.Get(full);
                if (resumeFrom > 0)
                    req.SetRequestHeader("Range", $"bytes={resumeFrom}-");
                req.downloadHandler = new DownloadHandlerBuffer();
                req.timeout = RequestTimeoutSeconds;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                req.certificateHandler = new AcceptAllCertificates();
#endif
                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    if (ct.IsCancellationRequested) { req.Abort(); throw new OperationCanceledException(ct); }
                    lock (_inflight) _bytesReceived[url] = resumeFrom + (long)req.downloadedBytes;
                    if (_bytesExpected.GetValueOrDefault(url) <= resumeFrom)
                    {
                        var cl = req.GetResponseHeader("Content-Length");
                        if (cl != null && long.TryParse(cl, out var sz) && sz > 0)
                            lock (_inflight) _bytesExpected[url] = resumeFrom + sz;
                    }
                    await Task.Yield();
                }

                if (req.result is UnityWebRequest.Result.ConnectionError
                               or UnityWebRequest.Result.DataProcessingError)
                {
                    MarkOfflineUnlessLocal("content fetch network error");
                    throw new LvnFetchException((int)req.responseCode, "network", req.error ?? "network error");
                }
                if (req.responseCode is < 200 or >= 300)
                    throw new LvnFetchException((int)req.responseCode, "http_" + req.responseCode, $"GET {full}");

                var body = req.downloadHandler.data ?? Array.Empty<byte>();
                // Server returned 200 when we asked for 206 → no resume support,
                // overwrite .part with the full fresh response.
                bool overwrite = resumeFrom == 0 || (int)req.responseCode == 200;
                await AppendBytesAsync(partPath, body, overwrite, ct);

                lock (_inflight)
                {
                    var total = resumeFrom + body.Length;
                    _bytesReceived[url] = total;
                    _bytesExpected[url] = total;
                }
                return await ReadAllBytesAsync(partPath, ct);
            }
            finally { _downloadSlots.Release(); }
        }

        private static async Task AppendBytesAsync(string path, byte[] data, bool overwrite, CancellationToken ct)
        {
            await Task.Run(() =>
            {
                var mode = overwrite ? FileMode.Create : FileMode.Append;
                using var fs = new FileStream(path, mode, FileAccess.Write, FileShare.None);
                fs.Write(data, 0, data.Length);
            }, ct);
        }

        // Wraps the actual network work in the in-flight tracker so any cache-miss
        // shows up in the BatchTotal/BatchDone counters. Dedups duplicate calls to
        // the same url — second caller awaits the first one's task.
        private Task<T> TrackedFetch<T>(string url, Func<Task<T>> work)
        {
            lock (_inflight)
            {
                if (_inflight.TryGetValue(url, out var existing) && existing is Task<T> typed)
                    return typed;
            }
            var task = work();
            lock (_inflight)
            {
                _inflight[url] = task;
                BatchTotal++;
                LastStartedUrl = url;
            }
            _ = task.ContinueWith(_ =>
            {
                lock (_inflight)
                {
                    _inflight.Remove(url);
                    BatchDone++;
                    if (BatchDone >= BatchTotal)
                    {
                        BatchTotal = 0;
                        BatchDone = 0;
                        LastStartedUrl = null;
                        _attempts.Clear();
                        _bytesExpected.Clear();
                        _bytesReceived.Clear();
                    }
                }
            }, TaskScheduler.Default);
            return task;
        }

        // Retries with exponential backoff until the asset arrives or the token
        // fires. FetchOnce (private) does the single request with a short timeout
        // so a stuck connection can't hang the whole batch.
        private async Task<byte[]> Fetch(string url, CancellationToken ct)
        {
            lock (_inflight) _attempts[url] = 1;
            const int MaxAttempts = 5;
            while (true)
            {
                try
                {
                    var bytes = await FetchOnce(url, ct);
                    lock (_inflight) _attempts.Remove(url);
                    return bytes;
                }
                catch (OperationCanceledException) { throw; }
                catch (LvnFetchException ex) when (ex.Code == "network" && LvnNetworkStatus.IsOffline)
                {
                    throw; // offline — retrying is pointless; caller falls back to cache
                }
                catch (LvnFetchException ex) when (ex.Status is >= 400 and < 500)
                {
                    Debug.LogWarning($"[content] {url} permanent {ex.Status}: {ex.Message}");
                    throw;
                }
                catch (Exception ex)
                {
                    int attempt;
                    lock (_inflight) attempt = _attempts[url] = _attempts.GetValueOrDefault(url, 1) + 1;
                    if (attempt > MaxAttempts)
                    {
                        Debug.LogWarning($"[content] {url} gave up after {MaxAttempts} attempts: {ex.Message}");
                        throw;
                    }
                    var backoff = LvnBackoff.DelaySeconds(attempt);
                    Debug.LogWarning($"[content] {url} failed (was attempt {attempt - 1}): {ex.Message}; retry #{attempt} in {backoff:F1}s");
                    try { await Task.Delay(Mathf.RoundToInt(backoff * 1000f), ct); }
                    catch (OperationCanceledException) { throw; }
                }
            }
        }

        // Single attempt — downloads url into memory, no disk writes. Used for
        // text (scripts, version index) and on-demand bytes not worth persisting.
        private async Task<byte[]> FetchOnce(string url, CancellationToken ct)
        {
            ThrowIfOffline();
            await _downloadSlots.WaitAsync(ct);
            try
            {
                var full = ResolveUrl(url);
                lock (_inflight) { _bytesReceived[url] = 0; }

                using var req = UnityWebRequest.Get(full);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.timeout = RequestTimeoutSeconds;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                req.certificateHandler = new AcceptAllCertificates();
#endif
                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    if (ct.IsCancellationRequested) { req.Abort(); throw new OperationCanceledException(ct); }
                    lock (_inflight) _bytesReceived[url] = (long)req.downloadedBytes;
                    await Task.Yield();
                }

                if (req.result is UnityWebRequest.Result.ConnectionError
                               or UnityWebRequest.Result.DataProcessingError)
                {
                    MarkOfflineUnlessLocal("content fetch network error");
                    throw new LvnFetchException((int)req.responseCode, "network", req.error ?? "network error");
                }
                if (req.responseCode is < 200 or >= 300)
                    throw new LvnFetchException((int)req.responseCode, "http_" + req.responseCode, $"GET {full}");

                return req.downloadHandler.data ?? Array.Empty<byte>();
            }
            finally { _downloadSlots.Release(); }
        }

        private string ResolveUrl(string url)
        {
            if (url.StartsWith("file://")) return url;
            string full;
            if (url.StartsWith("http://") || url.StartsWith("https://"))
                full = url;
            else
            {
                if (!url.StartsWith("/")) url = "/" + url;
                full = _baseUrl + url;
            }
            // A local bundle reads files by path — a ?v= query would corrupt it.
            if (_local) return full;
            // Append the content version as a query param so the device's HTTP
            // cache treats each asset version as a distinct immutable resource.
            var ver = VersionFor(url);
            if (ver != null)
            {
                var sep = full.Contains('?') ? '&' : '?';
                full += sep + "v=" + ver.Substring(0, Math.Min(12, ver.Length));
            }
            return full;
        }

        // On-disk cache file for a content URL: sha1(url@version) hex + ext, where
        // `version` is the asset's sha256 from the version index. Folding the
        // version into the key means a re-uploaded asset gets a fresh cache file
        // (auto-invalidation) without clobbering the old one. Unknown/unversioned
        // assets fall back to sha1(url) — legacy behaviour.
        private string CachePath(string dir, string url, string ext)
        {
            var ver = VersionFor(url);
            return Path.Combine(dir, HashKey(url, ver) + ext);
        }

        /// <summary>Content-integrity check: does the payload hash to the version
        /// index's sha256 hex? Exposed for tests.</summary>
        internal static bool Sha256Matches(byte[] data, string expectedHex)
        {
            if (data == null || string.IsNullOrEmpty(expectedHex)) return false;
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(data);
            if (expectedHex.Length != hash.Length * 2) return false;
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return string.Equals(sb.ToString(), expectedHex, StringComparison.OrdinalIgnoreCase);
        }

        // Pure cache-key hash, exposed for tests: sha1(url) or sha1(url@version).
        internal static string HashKey(string url, string version)
        {
            var key = version == null ? url : url + "@" + version;
            using var sha = SHA1.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static async Task<string> ReadAllTextAsync(string path, CancellationToken ct)
        {
#if UNITY_2021_2_OR_NEWER
            return await File.ReadAllTextAsync(path, ct);
#else
            return await Task.Run(() => File.ReadAllText(path), ct);
#endif
        }

        private static async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct)
        {
#if UNITY_2021_2_OR_NEWER
            return await File.ReadAllBytesAsync(path, ct);
#else
            return await Task.Run(() => File.ReadAllBytes(path), ct);
#endif
        }

        private static async Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken ct)
        {
            // Always atomic — a half-written cache file is worse than none (File.Exists
            // would treat the truncated file as valid on the next run).
            await Task.Run(() => AtomicWriteAllBytes(path, bytes), ct);
        }

        // Atomic write: stage to a unique temp file in the same directory, then move
        // it into place (mirrors the .part → File.Move pattern DownloadBytes uses).
        // The destination path therefore only ever holds a complete file — never a
        // truncated one from an interrupted write.
        internal static void AtomicWriteAllBytes(string path, byte[] bytes)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllBytes(tmp, bytes);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }
    }

    /// <summary>Lightweight descriptor for a single preload batch entry.</summary>
    public sealed class PreloadItem
    {
        public string Url;
        public string Kind;
        public string Alias;
    }

    internal static class TaskExtensions
    {
        /// <summary>Adds cancellation support to any Task. Wraps it in WhenAny with
        /// a CT-driven completion source so awaiting can throw on shutdown even if
        /// the underlying task ignores the token.</summary>
        public static async Task WithCancellation(this Task task, CancellationToken ct)
        {
            if (!ct.CanBeCanceled) { await task; return; }
            var tcs = new TaskCompletionSource<bool>();
            using (ct.Register(s => ((TaskCompletionSource<bool>)s).TrySetResult(true), tcs))
            {
                if (task != await Task.WhenAny(task, tcs.Task))
                    throw new OperationCanceledException(ct);
            }
            await task; // surface exceptions
        }
    }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
    internal sealed class AcceptAllCertificates : UnityEngine.Networking.CertificateHandler
    {
        protected override bool ValidateCertificate(byte[] certificateData) => true;
    }
#endif
}
