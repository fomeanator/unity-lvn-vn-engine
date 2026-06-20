using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Lvn.UI
{
    /// <summary>
    /// An <see cref="ILvnAssets"/> backed by Unity Addressables. Sprites and
    /// audio clips are loaded by address (the url field from .lvn commands).
    /// Assets are cached by handle so repeated loads are instant; call
    /// <see cref="Unload"/> or <see cref="UnloadAll"/> to release memory.
    ///
    /// Requires the Addressables package (com.unity.addressables).
    /// Install via Package Manager or add to your manifest:
    ///   "com.unity.addressables": "1.21.x"
    /// </summary>
    public sealed class AddressablesAssets : ILvnAssets
    {
        private readonly Dictionary<string, AsyncOperationHandle<Sprite>> _spriteHandles
            = new Dictionary<string, AsyncOperationHandle<Sprite>>();
        private readonly Dictionary<string, AsyncOperationHandle<AudioClip>> _audioHandles
            = new Dictionary<string, AsyncOperationHandle<AudioClip>>();
        private readonly Dictionary<string, AsyncOperationHandle<IList<Sprite>>> _batchHandles
            = new Dictionary<string, AsyncOperationHandle<IList<Sprite>>>();

        /// <summary>Optional prefix stripped from urls before passing to Addressables.
        /// Default "/content" matches the convention in .lvn files.</summary>
        public string ContentPrefix = "/content";

        private string AddressFor(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var addr = url;
            if (!string.IsNullOrEmpty(ContentPrefix) && addr.StartsWith(ContentPrefix))
                addr = addr.Substring(ContentPrefix.Length);
            return addr.TrimStart('/');
        }

        public async Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var addr = AddressFor(url);
            if (addr == null) return null;

            if (_spriteHandles.TryGetValue(addr, out var existing) && existing.IsValid())
                return existing.Result;

            try
            {
                var handle = Addressables.LoadAssetAsync<Sprite>(addr);
                _spriteHandles[addr] = handle;
                await handle.Task;
                ct.ThrowIfCancellationRequested();
                return handle.Status == AsyncOperationStatus.Succeeded ? handle.Result : null;
            }
            catch
            {
                return null;
            }
        }

        public async Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var addr = AddressFor(url);
            if (addr == null) return null;

            if (_audioHandles.TryGetValue(addr, out var existing) && existing.IsValid())
                return existing.Result;

            try
            {
                var handle = Addressables.LoadAssetAsync<AudioClip>(addr);
                _audioHandles[addr] = handle;
                await handle.Task;
                ct.ThrowIfCancellationRequested();
                return handle.Status == AsyncOperationStatus.Succeeded ? handle.Result : null;
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
            var addr = AddressFor(url);
            if (addr == null) return;

            if (_spriteHandles.TryGetValue(addr, out var spriteHandle))
            {
                if (spriteHandle.IsValid()) Addressables.Release(spriteHandle);
                _spriteHandles.Remove(addr);
            }
            if (_audioHandles.TryGetValue(addr, out var audioHandle))
            {
                if (audioHandle.IsValid()) Addressables.Release(audioHandle);
                _audioHandles.Remove(addr);
            }
            if (_batchHandles.TryGetValue(addr, out var batchHandle))
            {
                if (batchHandle.IsValid()) Addressables.Release(batchHandle);
                _batchHandles.Remove(addr);
            }
        }

        public void UnloadAll()
        {
            foreach (var kv in _spriteHandles)
                if (kv.Value.IsValid()) Addressables.Release(kv.Value);
            foreach (var kv in _audioHandles)
                if (kv.Value.IsValid()) Addressables.Release(kv.Value);
            foreach (var kv in _batchHandles)
                if (kv.Value.IsValid()) Addressables.Release(kv.Value);

            _spriteHandles.Clear();
            _audioHandles.Clear();
            _batchHandles.Clear();
        }
    }
}
