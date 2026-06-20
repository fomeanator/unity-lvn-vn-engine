using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// The production-grade, disk-cached <see cref="ILvnAssets"/>. Where
    /// <see cref="DirectoryAssets"/> reads a local folder and the lightweight
    /// <see cref="NetworkAssets"/> streams over the wire with an in-memory cache,
    /// this wraps a full <see cref="ContentLoader"/> pipeline: a sha1(url@version)
    /// <b>disk</b> cache (content survives restarts and plays offline), an
    /// in-memory sprite cache, dedup of parallel loads, content-version
    /// cache-busting (a re-uploaded asset auto-invalidates), resumable retries
    /// with backoff, and byte-level progress for a loading HUD.
    ///
    /// <para>Point it at a base URL (your CDN or the bundled Go server), call
    /// <see cref="WarmVersionsAsync"/> once at boot, then assign it to
    /// <c>VnStage.Assets</c>. For a chapter's prioritized release set (required
    /// gates Play, deferred streams in during play) drive <see cref="Scheduler"/>
    /// with a map of path → <see cref="LvnAssetMeta"/> from your manifest.</para>
    /// </summary>
    public sealed class CachingAssets : ILvnAssets
    {
        /// <summary>The underlying loader — exposed for HUD progress
        /// (<c>BatchActive</c>, <c>BatchBytesReceived</c>, <c>CurrentFileLabel</c>),
        /// version refresh, version-pinned script loads, and warmed-sprite
        /// lookups (<c>TryGetSprite</c>).</summary>
        public ContentLoader Loader { get; }

        private AssetScheduler _scheduler;
        /// <summary>The prioritized chapter download planner (lazily created).
        /// Feed it a release set via <c>Scheduler.Start(assets, ct)</c>; poll
        /// <c>RequiredReady</c>/<c>Progress</c> on the loading screen.</summary>
        public AssetScheduler Scheduler => _scheduler ??= new AssetScheduler(Loader);

        /// <param name="baseUrl">Content origin, e.g. "https://cdn.example.com" or
        /// "http://localhost:8000". Relative urls ("/content/bg/x.png") resolve
        /// against it; absolute urls pass through.</param>
        /// <param name="cacheRoot">Disk cache root. Defaults to
        /// <c>Application.persistentDataPath/cache</c>.</param>
        public CachingAssets(string baseUrl, string cacheRoot = null)
            : this(new ContentLoader(baseUrl, cacheRoot)) { }

        public CachingAssets(ContentLoader loader)
        {
            Loader = loader;
        }

        /// <summary>Fetch the content-version index once at boot so changed assets
        /// auto-invalidate their cache. Non-fatal if offline (falls back to the
        /// last persisted index, then to url-only cache keys).</summary>
        public Task WarmVersionsAsync(CancellationToken ct = default) =>
            Loader.LoadAssetVersionsAsync(ct);

        public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct) =>
            Loader.DownloadSpriteAsync(url, ct);

        public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct) =>
            Loader.DownloadAudioClipAsync(url, ct);

        /// <summary>Batch-warm a set of urls. Sprite-kind urls go through the
        /// pipelined preload batch (overlapping each disk write with the next
        /// file's network setup); audio-kind urls load individually into the
        /// audio cache.</summary>
        public async Task PreloadAsync(IReadOnlyList<string> urls, string kind, CancellationToken ct)
        {
            if (urls == null || urls.Count == 0) return;

            if (kind == "audio")
            {
                var tasks = new List<Task>(urls.Count);
                foreach (var url in urls)
                    if (!string.IsNullOrEmpty(url))
                        tasks.Add(Loader.DownloadAudioClipAsync(url, ct));
                await Task.WhenAll(tasks);
                return;
            }

            var items = new List<PreloadItem>(urls.Count);
            foreach (var url in urls)
                if (!string.IsNullOrEmpty(url))
                    items.Add(new PreloadItem { Url = url, Kind = DownloadPolicy.Kind(url) });
            await Loader.StartPreloadBatch(items, ct);
            await Loader.WaitForAll(null, ct);
        }

        public void Unload(string url) => Loader.Unload(url);

        public void UnloadAll() => Loader.UnloadAll();
    }
}
