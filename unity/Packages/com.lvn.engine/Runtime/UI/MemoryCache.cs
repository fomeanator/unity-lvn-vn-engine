using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// An L1 <see cref="ILvnAssets"/> cache backed by in-memory dictionaries.
    /// This is the fastest loader — if the asset is here, no I/O happens.
    /// Use as the first link in a <see cref="ChainAssets"/> chain.
    ///
    /// This loader does not load assets itself — it only serves from its cache.
    /// Populate it by adding it after a loader that actually loads, or use
    /// <see cref="Seed"/> to pre-populate.
    /// </summary>
    public sealed class MemoryCache : ILvnAssets
    {
        private readonly Dictionary<string, Sprite> _sprites = new Dictionary<string, Sprite>();
        private readonly Dictionary<string, AudioClip> _audio = new Dictionary<string, AudioClip>();

        /// <summary>Pre-populate the cache with a sprite.</summary>
        public void Seed(string url, Sprite sprite)
        {
            if (!string.IsNullOrEmpty(url) && sprite != null)
                _sprites[url] = sprite;
        }

        /// <summary>Pre-populate the cache with an audio clip.</summary>
        public void Seed(string url, AudioClip clip)
        {
            if (!string.IsNullOrEmpty(url) && clip != null)
                _audio[url] = clip;
        }

        /// <summary>Check if a url is cached without loading it.</summary>
        public bool HasSprite(string url) =>
            !string.IsNullOrEmpty(url) && _sprites.ContainsKey(url);

        /// <summary>Check if a url is cached without loading it.</summary>
        public bool HasAudio(string url) =>
            !string.IsNullOrEmpty(url) && _audio.ContainsKey(url);

        public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(url)) return Task.FromResult<Sprite>(null);
            _sprites.TryGetValue(url, out var sprite);
            return Task.FromResult(sprite);
        }

        public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(url)) return Task.FromResult<AudioClip>(null);
            _audio.TryGetValue(url, out var clip);
            return Task.FromResult(clip);
        }

        public Task PreloadAsync(IReadOnlyList<string> urls, string kind, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public void Unload(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            _sprites.Remove(url);
            _audio.Remove(url);
        }

        public void UnloadAll()
        {
            _sprites.Clear();
            _audio.Clear();
        }
    }
}
