using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Lvn.Content
{
    /// <summary>
    /// THE single content-download authority — the host-level orchestration layer
    /// over <see cref="ContentLoader"/> (fetch/decode/cache) and
    /// <see cref="AssetScheduler"/> (the prioritized chapter set), with the
    /// per-asset rules in the pure <see cref="DownloadPolicy"/>. It gathers
    /// "what loads when" into four explicit, named phases so each phase says
    /// exactly WHAT it pulls and WHY:
    /// <list type="number">
    ///   <item><b>BootPrefetchAsync</b> — launch: verify+fetch the boot set
    ///   (shared UI chrome, menu covers, chapter loading backgrounds), warm the
    ///   warm-set into memory, precache chapter scripts for offline.</item>
    ///   <item><b>MenuRefreshAsync</b> — while the menu is up: re-verify the boot
    ///   set against the latest version index and background-download anything
    ///   that changed.</item>
    ///   <item><b>BeginChapter</b> — chapter entry: start the prioritized scheduler
    ///   for the chapter's release set; required gates Play.</item>
    ///   <item><b>(in-game / next)</b> — the scheduler's eta-ordered deferred phase
    ///   streams the rest during play; once the whole set is on disk, the next
    ///   chapter's required assets are pulled ahead.</item>
    /// </list>
    /// Engine-agnostic: feed it an <see cref="LvnManifest"/> deserialized from
    /// your backend. Construct it from the same <see cref="ContentLoader"/> your
    /// <c>CachingAssets</c> wraps so the cache is shared.
    /// </summary>
    public sealed class DownloadManager
    {
        private readonly ContentLoader _loader;

        /// <summary>The chapter scheduler, owned here so its lifetime (start on
        /// entry, stop on exit) lives in one place. The loading screen reads it
        /// for the Play gate (<c>RequiredReady</c>) and progress.</summary>
        public AssetScheduler ChapterScheduler { get; }

        /// <summary>Optional fallback boot-UI urls, used only when the manifest
        /// isn't projecting a top-level <see cref="LvnManifest.assets"/> set yet.
        /// Empty by default — the engine hardcodes no paths. Set it to your shared
        /// interface art (dialogue frame parts, badges, …) if your server doesn't
        /// list them.</summary>
        public string[] FallbackBootUi = Array.Empty<string>();

        private LvnManifest _manifest;
        private LvnChapter _currentChapter;
        private CancellationToken _chapterCt;

        public DownloadManager(ContentLoader loader)
        {
            _loader = loader ?? throw new ArgumentNullException(nameof(loader));
            ChapterScheduler = new AssetScheduler(loader);
            // When the current chapter's WHOLE set lands on disk, pull the next
            // chapter's required (critical) assets ahead so its entry is instant.
            ChapterScheduler.OnAllComplete += OnCurrentChapterFullyDownloaded;
        }

        // ── Phase 1: boot ─────────────────────────────────────────────────────

        /// <summary>Launch-time prefetch. Online: verify the boot set against the
        /// version index, download what's missing/changed, then warm the warm-set
        /// into memory and precache chapter scripts to disk (so a chapter opens
        /// offline). Offline: no network — relies on the disk cache; still warms
        /// whatever is already cached so the menu/loading paint instantly.</summary>
        public async Task BootPrefetchAsync(LvnManifest manifest, bool online, CancellationToken ct)
        {
            _manifest = manifest;
            RegisterAliases(manifest);
            var images  = BootImageSet(manifest);
            var scripts = new List<string>();
            CollectScripts(manifest, scripts);

            if (online)
            {
                try
                {
                    IReadOnlyList<string> toDownload = images;
                    if (_loader.HasCachedAssets())
                        toDownload = await _loader.VerifyAsync(images, ct);

                    if (toDownload.Count > 0)
                    {
                        var items = new List<PreloadItem>(toDownload.Count);
                        foreach (var url in toDownload)
                            items.Add(new PreloadItem { Url = url, Kind = DownloadPolicy.Kind(url) });
                        await _loader.StartPreloadBatch(items, ct);
                        await _loader.WaitForAll(null, ct);
                    }
                }
                catch (OperationCanceledException) { throw; }
                // network errors bubble to the caller (host degrades to offline).
            }

            // Warm the warm-set (UI + chapter loading bgs) into the sprite cache
            // so the first frame that shows them has no decode gap. Disk-only
            // classes (covers, scene bgs, actors) are skipped — loaded on demand.
            var warm = new List<Task>(images.Count);
            foreach (var url in images)
                if (DownloadPolicy.WarmToMemory(url))
                    warm.Add(_loader.DownloadSpriteAsync(url, ct));
            await Task.WhenAll(warm);

            // Precache chapter scripts (tiny JSON) so a chapter can open offline
            // even if never entered. Fire-and-forget; never gates the menu.
            if (online)
                foreach (var s in scripts)
                    _loader.RefreshScriptInBackground(s, reloadIndex: false);
        }

        // ── Phase 2: menu background ──────────────────────────────────────────

        /// <summary>While the menu is on screen: re-verify the boot image set
        /// against the freshly-loaded version index and background-download
        /// anything whose hash changed, then re-warm the changed warm-set. Safe to
        /// call every tick — a no-op when nothing changed. Never throws.</summary>
        public async Task MenuRefreshAsync(LvnManifest manifest, CancellationToken ct)
        {
            _manifest = manifest;
            RegisterAliases(manifest);
            try
            {
                var images = BootImageSet(manifest);
                if (!_loader.HasCachedAssets()) return;

                var changed = await _loader.VerifyAsync(images, ct);
                if (changed.Count == 0) return;

                var items = new List<PreloadItem>(changed.Count);
                foreach (var url in changed)
                    items.Add(new PreloadItem { Url = url, Kind = DownloadPolicy.Kind(url) });
                await _loader.StartPreloadBatch(items, ct);
                await _loader.WaitForAll(null, ct);

                // Re-warm any changed warm-set sprites so the new art is live.
                // Drop the stale in-memory sprite first so the new bytes decode.
                var warm = new List<Task>(changed.Count);
                foreach (var url in changed)
                    if (DownloadPolicy.WarmToMemory(url))
                    {
                        _loader.Unload(url);
                        warm.Add(_loader.DownloadSpriteAsync(url, ct));
                    }
                await Task.WhenAll(warm);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Debug.LogWarning($"[downloads] menu refresh skipped: {ex.Message}"); }
        }

        // ── Phase 3: chapter entry ────────────────────────────────────────────

        /// <summary>Start the prioritized scheduler for a chapter's release set.
        /// Required (critical) assets gate the loading screen via
        /// <c>ChapterScheduler.RequiredReady</c>; deferred stream in during play.
        /// The chapter is remembered so that, once its whole set is on disk, the
        /// NEXT chapter's required assets can be pulled ahead. Returns the
        /// scheduler so the caller can read its progress/gate.</summary>
        public AssetScheduler BeginChapter(LvnChapter chapter, CancellationToken ct)
        {
            _currentChapter = chapter;
            _chapterCt = ct;
            ChapterScheduler.Start(chapter?.assets, ct);
            return ChapterScheduler;
        }

        /// <summary>Stop the chapter scheduler (on chapter exit / runner teardown).</summary>
        public void EndChapter()
        {
            _currentChapter = null;
            ChapterScheduler.Stop();
        }

        // ── Phase 4: in-game background ───────────────────────────────────────

        /// Fires when the CURRENT chapter's whole release set is on disk. The
        /// player is still mid-chapter, the channel is now idle, so spend it
        /// pulling the NEXT chapter's obligatory part ahead → entering it later is
        /// instant. Runs off the scheduler's completion; all work is fire-and-forget.
        private void OnCurrentChapterFullyDownloaded()
        {
            var current = _currentChapter;
            if (current == null) return;
            if (LvnNetworkStatus.IsOffline) return; // can't fetch; boot/menu re-primes later
            PrefetchNextChapterRequired(current, _chapterCt);
        }

        /// <summary>Pull the NEXT chapter's obligatory part ahead of time: its
        /// script, its loading background, and every asset the server marked
        /// <c>critical</c>. Deferred (later-used) assets are left for that
        /// chapter's own scheduler. Fire-and-forget, deduped by ContentLoader; a
        /// no-op when there is no next chapter or no metadata.</summary>
        public void PrefetchNextChapterRequired(LvnChapter current, CancellationToken ct)
        {
            var next = FindNextChapter(_manifest, current);
            if (next == null) return;

            if (!string.IsNullOrEmpty(next.script_url))
                _loader.RefreshScriptInBackground(next.script_url, reloadIndex: false);
            if (!string.IsNullOrEmpty(next.bg_url))
                _ = _loader.Prefetch(next.bg_url, DownloadPolicy.Kind(next.bg_url), ct);

            int n = 0;
            if (next.assets != null)
                foreach (var kv in next.assets)
                {
                    if (string.IsNullOrEmpty(kv.Key) || kv.Value == null) continue;
                    if (!kv.Value.critical) continue;          // obligatory part only
                    if (DownloadPolicy.IsScript(kv.Key)) continue; // handled above
                    _ = _loader.Prefetch(kv.Key, DownloadPolicy.Kind(kv.Key), ct);
                    n++;
                }
            Debug.Log($"[downloads] next chapter '{next.id}' prefetch: {n} required asset(s) + script/bg");
        }

        /// <summary>The chapter the shell should auto-continue into after
        /// <paramref name="current"/> finishes (next by number within the same
        /// title). Null = <paramref name="current"/> is the last chapter → return
        /// to menu.</summary>
        public LvnChapter NextChapterAfter(LvnChapter current) => FindNextChapter(_manifest, current);

        /// Pure: the chapter that follows `current` within the SAME title — the one
        /// with the smallest `number` strictly greater than current.number. Ordering
        /// by number (not array position) skips pilots (number 0) and survives
        /// chapters listed out of order. Null when current is last or not found.
        internal static LvnChapter FindNextChapter(LvnManifest m, LvnChapter current)
        {
            if (m?.titles == null || current == null) return null;
            foreach (var title in m.titles)
            {
                if (title?.seasons == null) continue;
                bool contains = false;
                foreach (var s in title.seasons)
                {
                    if (s?.chapters == null) continue;
                    foreach (var c in s.chapters)
                        if (c != null && c.id == current.id) { contains = true; break; }
                    if (contains) break;
                }
                if (!contains) continue;

                LvnChapter best = null;
                foreach (var s in title.seasons)
                {
                    if (s?.chapters == null) continue;
                    foreach (var c in s.chapters)
                    {
                        if (c == null || c.number <= current.number) continue;
                        if (best == null || c.number < best.number) best = c;
                    }
                }
                return best;
            }
            return null;
        }

        // ── Manifest walk ─────────────────────────────────────────────────────

        // The boot/menu image set the client downloads up front. Primary source:
        // the server's top-level manifest.assets (fully server-driven). Fallback
        // (server not projecting it): FallbackBootUi + the per-title covers /
        // per-chapter loading bgs walked from the manifest.
        private List<string> BootImageSet(LvnManifest manifest)
        {
            var images = new List<string>();
            if (manifest?.assets != null && manifest.assets.Count > 0)
            {
                foreach (var kv in manifest.assets)
                    if (!string.IsNullOrEmpty(kv.Key)) images.Add(kv.Key);
                return images;
            }
            if (FallbackBootUi != null) images.AddRange(FallbackBootUi);
            CollectFromManifest(manifest, images, scriptsOut: null);
            return images;
        }

        // Registers every asset's loading-screen alias (from AssetMeta.alias) with
        // the ContentLoader, so the label under the bar shows it as each file
        // downloads — top-level and per-chapter alike. One pass over the manifest.
        private void RegisterAliases(LvnManifest manifest)
        {
            if (manifest == null) return;
            if (manifest.assets != null)
                foreach (var kv in manifest.assets)
                    if (kv.Value != null && !string.IsNullOrEmpty(kv.Value.alias))
                        _loader.RegisterAlias(kv.Key, kv.Value.alias);
            if (manifest.titles == null) return;
            foreach (var title in manifest.titles)
            {
                if (title?.seasons == null) continue;
                foreach (var season in title.seasons)
                {
                    if (season?.chapters == null) continue;
                    foreach (var ch in season.chapters)
                    {
                        if (ch?.assets == null) continue;
                        foreach (var kv in ch.assets)
                            if (kv.Value != null && !string.IsNullOrEmpty(kv.Value.alias))
                                _loader.RegisterAlias(kv.Key, kv.Value.alias);
                    }
                }
            }
        }

        // Collects every chapter script url into `scripts` (for offline precache).
        private static void CollectScripts(LvnManifest manifest, List<string> scripts)
        {
            if (manifest?.titles == null) return;
            foreach (var title in manifest.titles)
            {
                if (title?.seasons == null) continue;
                foreach (var season in title.seasons)
                {
                    if (season?.chapters == null) continue;
                    foreach (var ch in season.chapters)
                        if (ch != null && !string.IsNullOrEmpty(ch.script_url)) scripts.Add(ch.script_url);
                }
            }
        }

        // Collects every chapter bg + title cover into `images`, and every chapter
        // script into `scripts` (when non-null). One walk, one place.
        private static void CollectFromManifest(LvnManifest manifest, List<string> images, List<string> scriptsOut)
        {
            if (manifest?.titles == null) return;
            foreach (var title in manifest.titles)
            {
                if (title == null) continue;
                if (!string.IsNullOrEmpty(title.cover_url)) images.Add(title.cover_url);
                if (title.seasons == null) continue;
                foreach (var season in title.seasons)
                {
                    if (season?.chapters == null) continue;
                    foreach (var ch in season.chapters)
                    {
                        if (ch == null) continue;
                        if (!string.IsNullOrEmpty(ch.bg_url)) images.Add(ch.bg_url);
                        if (scriptsOut != null && !string.IsNullOrEmpty(ch.script_url))
                            scriptsOut.Add(ch.script_url);
                    }
                }
            }
        }
    }
}
