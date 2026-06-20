using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// The asset-loading seam: how the stage turns a command's <c>sprite_url</c>
    /// into a <see cref="Sprite"/>. The engine ships no loader so it stays
    /// agnostic — plug in Resources, Addressables, a file reader, or a network
    /// cache. Leave <see cref="VnStage.Assets"/> null to run with solid-colour
    /// backgrounds and no character art (handy for greyboxing a script).
    ///
    /// Implementors should cache by url so repeated loads are instant.
    /// Off-main-thread I/O is strongly recommended to avoid freezing the click
    /// → Advance loop.
    /// </summary>
    public interface ILvnAssets
    {
        /// <summary>Load a single sprite by url. Return null on failure.</summary>
        Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct);

        /// <summary>Resolve an <c>audio</c> command's url to a clip. Return null
        /// (or throw) if you don't ship audio — the stage just stays silent.</summary>
        Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct);

        /// <summary>Speculative batch load: warm the cache for upcoming urls.
        /// Default implementation calls <see cref="LoadSpriteAsync"/> for each
        /// sprite-kind url and <see cref="LoadAudioAsync"/> for audio-kind urls.
        /// Override for parallel loading (Addressables, UnityWebRequest, etc.).</summary>
        Task PreloadAsync(IReadOnlyList<string> urls, string kind, CancellationToken ct)
        {
            var tasks = new List<Task>();
            foreach (var url in urls)
            {
                if (string.IsNullOrEmpty(url)) continue;
                tasks.Add(kind == "audio"
                    ? LoadAudioAsync(url, ct).ContinueWith(_ => { })
                    : LoadSpriteAsync(url, ct).ContinueWith(_ => { }));
            }
            return Task.WhenAll(tasks);
        }

        /// <summary>Release the cached asset for a single url. Safe to call if
        /// the url was never loaded. Implementors should destroy the underlying
        /// Unity Object (Texture2D, AudioClip) to free GPU/CPU memory.</summary>
        void Unload(string url);

        /// <summary>Release all cached assets. Call on scene transition or
        /// application exit to avoid leaked textures.</summary>
        void UnloadAll();
    }
}
