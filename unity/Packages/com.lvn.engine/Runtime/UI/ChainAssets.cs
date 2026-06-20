using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// A chain of <see cref="ILvnAssets"/> loaders tried in order. The first
    /// loader that returns a non-null result wins, and the result is cached
    /// by the winning loader (or an optional shared cache layer).
    ///
    /// Typical setup:
    /// <code>
    ///   var assets = new ChainAssets()
    ///       .Add(new MemoryCache())        // L1: fastest
    ///       .Add(new DirectoryAssets(dir)) // L2: local disk
    ///       .Add(new AddressablesAssets()) // L3: Unity bundles
    ///       .Add(new NetworkAssets(cdn));  // L4: HTTP fallback
    /// </code>
    /// </summary>
    public sealed class ChainAssets : ILvnAssets
    {
        private readonly List<ILvnAssets> _chain = new List<ILvnAssets>();

        /// <summary>Add a loader to the end of the chain. Returns this for
        /// fluent configuration.</summary>
        public ChainAssets Add(ILvnAssets loader)
        {
            if (loader != null) _chain.Add(loader);
            return this;
        }

        public async Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct)
        {
            foreach (var loader in _chain)
            {
                var sprite = await loader.LoadSpriteAsync(url, ct);
                if (sprite != null) return sprite;
            }
            return null;
        }

        public async Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct)
        {
            foreach (var loader in _chain)
            {
                var clip = await loader.LoadAudioAsync(url, ct);
                if (clip != null) return clip;
            }
            return null;
        }

        public async Task PreloadAsync(IReadOnlyList<string> urls, string kind, CancellationToken ct)
        {
            if (urls == null || urls.Count == 0) return;

            var tasks = new List<Task>();
            foreach (var url in urls)
            {
                if (string.IsNullOrEmpty(url)) continue;
                tasks.Add(kind == "audio"
                    ? LoadAudioAsync(url, ct)
                    : LoadSpriteAsync(url, ct).ContinueWith(_ => { }));
            }
            await Task.WhenAll(tasks);
        }

        public void Unload(string url)
        {
            foreach (var loader in _chain)
                loader.Unload(url);
        }

        public void UnloadAll()
        {
            foreach (var loader in _chain)
                loader.UnloadAll();
        }
    }
}
