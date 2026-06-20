using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Lvn.UI
{
    /// <summary>
    /// A reference <see cref="ILvnAssets"/> that loads sprites from a local
    /// folder: a url like <c>/content/bg/room.png</c> maps to
    /// <c>&lt;baseDir&gt;/bg/room.png</c> (the <see cref="ContentPrefix"/> is
    /// stripped). Sprites are cached by url, and the file read happens off the
    /// main thread so showing a character or background doesn't freeze the click
    /// that triggered it. Audio clips are loaded from .wav/.ogg files in the
    /// same base directory.
    /// </summary>
    public sealed class DirectoryAssets : ILvnAssets
    {
        private readonly string _base;
        private readonly Dictionary<string, Sprite> _spriteCache = new Dictionary<string, Sprite>();
        private readonly Dictionary<string, AudioClip> _audioCache = new Dictionary<string, AudioClip>();

        /// <summary>Url prefix stripped before mapping to a file (default "/content").</summary>
        public string ContentPrefix = "/content";

        public DirectoryAssets(string baseDir) => _base = baseDir;

        private string PathFor(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var rel = url;
            if (!string.IsNullOrEmpty(ContentPrefix) && rel.StartsWith(ContentPrefix))
                rel = rel.Substring(ContentPrefix.Length);
            return Path.Combine(_base, rel.TrimStart('/'));
        }

        public async Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (_spriteCache.TryGetValue(url, out var hit)) return hit;

            var path = PathFor(url);
            if (path == null || !File.Exists(path)) return null;

            byte[] bytes;
            try { bytes = await Task.Run(() => File.ReadAllBytes(path), ct); }
            catch { return null; }
            if (ct.IsCancellationRequested) return null;

            if (_spriteCache.TryGetValue(url, out hit)) return hit;

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(bytes)) return null;
            var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
            _spriteCache[url] = sprite;
            return sprite;
        }

        public async Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (_audioCache.TryGetValue(url, out var hit)) return hit;

            var path = PathFor(url);
            if (path == null || !File.Exists(path)) return null;

            // Decode through UnityWebRequestMultimedia from a file:// url — Unity's
            // own decoder, run on the main thread (the only place AudioClip can be
            // built). This handles wav/ogg/mp3 correctly; never hand-roll PCM.
            using var req = UnityWebRequestMultimedia.GetAudioClip("file://" + path, GuessAudioType(path));
            var op = req.SendWebRequest();
            while (!op.isDone)
            {
                if (ct.IsCancellationRequested) { req.Abort(); return null; }
                await Task.Yield();
            }
            if (req.result is UnityWebRequest.Result.ConnectionError
                           or UnityWebRequest.Result.DataProcessingError)
                return null;

            if (_audioCache.TryGetValue(url, out hit)) return hit;

            var clip = DownloadHandlerAudioClip.GetContent(req);
            if (clip != null) _audioCache[url] = clip;
            return clip;
        }

        private static AudioType GuessAudioType(string path)
        {
            var lower = path.ToLowerInvariant();
            if (lower.EndsWith(".ogg")) return AudioType.OGGVORBIS;
            if (lower.EndsWith(".wav")) return AudioType.WAV;
            if (lower.EndsWith(".mp3")) return AudioType.MPEG;
            return AudioType.UNKNOWN;
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
