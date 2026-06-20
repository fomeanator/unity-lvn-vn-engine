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
    /// </summary>
    public interface ILvnAssets
    {
        Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct);

        /// <summary>Resolve an <c>audio</c> command's url to a clip. Return null
        /// (or throw) if you don't ship audio — the stage just stays silent.</summary>
        Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct);
    }
}
