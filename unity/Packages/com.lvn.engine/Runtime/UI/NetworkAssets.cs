using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Lvn.UI
{
    /// <summary>
    /// An <see cref="ILvnAssets"/> that loads sprites and audio from a remote
    /// server via UnityWebRequest. Useful for web games, streaming content,
    /// or as a fallback when local assets are missing.
    ///
    /// Assets are cached by url in memory; call <see cref="Unload"/> or
    /// <see cref="UnloadAll"/> to release GPU/CPU memory.
    /// </summary>
    public sealed class NetworkAssets : ILvnAssets
    {
        private readonly Dictionary<string, Sprite> _spriteCache = new Dictionary<string, Sprite>();
        private readonly Dictionary<string, AudioClip> _audioCache = new Dictionary<string, AudioClip>();
        private readonly string _baseUrl;

        /// <summary>Optional base url prepended to relative urls.
        /// E.g., "https://cdn.example.com/content".</summary>
        public string BaseUrl
        {
            get => _baseUrl;
            init => _baseUrl = value?.TrimEnd('/');
        }

        public NetworkAssets(string baseUrl = null)
        {
            _baseUrl = baseUrl?.TrimEnd('/');
        }

        private string FullUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (!string.IsNullOrEmpty(_baseUrl) && !url.StartsWith("http"))
                return _baseUrl + "/" + url.TrimStart('/');
            return url;
        }

        public async Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (_spriteCache.TryGetValue(url, out var hit)) return hit;

            var fullUrl = FullUrl(url);
            if (fullUrl == null) return null;

            try
            {
                using var request = UnityWebRequestTexture.GetTexture(fullUrl);
                var op = request.SendWebRequest();
                while (!op.isDone)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success) return null;

                var tex = DownloadHandlerTexture.GetContent(request);
                if (tex == null) return null;

                var sprite = Sprite.Create(tex,
                    new Rect(0, 0, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f));
                _spriteCache[url] = sprite;
                return sprite;
            }
            catch
            {
                return null;
            }
        }

        public async Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (_audioCache.TryGetValue(url, out var hit)) return hit;

            var fullUrl = FullUrl(url);
            if (fullUrl == null) return null;

            try
            {
                using var request = UnityWebRequestMultimedia.GetAudioClip(fullUrl, AudioType.UNKNOWN);
                var op = request.SendWebRequest();
                while (!op.isDone)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success) return null;

                var clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip == null) return null;

                _audioCache[url] = clip;
                return clip;
            }
            catch
            {
                return null;
            }
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
            if (string.IsNullOrEmpty(url)) return;

            if (_spriteCache.TryGetValue(url, out var sprite))
            {
                if (sprite != null)
                {
                    if (sprite.texture != null) Object.Destroy(sprite.texture);
                    Object.Destroy(sprite);
                }
                _spriteCache.Remove(url);
            }
            if (_audioCache.TryGetValue(url, out var clip))
            {
                if (clip != null) Object.Destroy(clip);
                _audioCache.Remove(url);
            }
        }

        public void UnloadAll()
        {
            foreach (var kv in _spriteCache)
            {
                if (kv.Value != null)
                {
                    if (kv.Value.texture != null) Object.Destroy(kv.Value.texture);
                    Object.Destroy(kv.Value);
                }
            }
            foreach (var kv in _audioCache)
            {
                if (kv.Value != null) Object.Destroy(kv.Value);
            }
            _spriteCache.Clear();
            _audioCache.Clear();
        }
    }
}
