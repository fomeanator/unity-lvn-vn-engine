using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// Owns the novel's three audio channels — music, ambient, sfx — and applies
    /// <c>audio</c> stage commands: load a clip and play it (optionally fading in),
    /// or stop a channel (optionally fading out). Extracted from <see cref="VnStage"/>
    /// so the stage doesn't carry mixing concerns; it's a small MonoBehaviour
    /// because the cross-fades run as coroutines.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StageAudio : MonoBehaviour
    {
        private AudioSource _music, _ambient, _sfx;

        // Track what each looping channel is playing (by url) so a replayed audio
        // command after a load/rollback recognises "this track is already on" and
        // adjusts volume instead of restarting it from the beginning.
        private readonly System.Collections.Generic.Dictionary<string, string> _playingUrl
            = new System.Collections.Generic.Dictionary<string, string>();

        private void Awake()
        {
            _music = gameObject.AddComponent<AudioSource>();
            _ambient = gameObject.AddComponent<AudioSource>();
            _sfx = gameObject.AddComponent<AudioSource>();
            foreach (var s in new[] { _music, _ambient, _sfx }) s.playOnAwake = false;
            _music.loop = true;
            _ambient.loop = true;
        }

        /// <summary>Apply one <c>audio</c> command. Missing audio is silent — a host
        /// that ships no sound simply no-ops. <paramref name="ct"/> cancels the
        /// in-flight clip load with the chapter.</summary>
        public async Task ApplyAsync(JObject cmd, ILvnAssets assets, CancellationToken ct)
        {
            var channel = (string)cmd["channel"] ?? "sfx";
            var src = channel == "music" ? _music : channel == "ambient" ? _ambient : _sfx;
            float fade = NumOr(cmd["fade"], 0f);

            if ((string)cmd["action"] == "stop")
            {
                _playingUrl.Remove(channel);
                if (fade > 0f) StartCoroutine(FadeAudio(src, src.volume, 0f, fade, stopAtEnd: true));
                else src.Stop();
                return;
            }

            var url = (string)cmd["url"];
            if (assets == null || string.IsNullOrEmpty(url)) return;

            float volume = NumOr(cmd["volume"], 1f);

            // Idempotent for looping channels: the same track already playing (a
            // load/rollback replay) keeps its position — only the volume updates.
            if (channel != "sfx" && src.isPlaying
                && _playingUrl.TryGetValue(channel, out var cur) && cur == url)
            {
                src.volume = volume;
                return;
            }

            AudioClip clip = null;
            try { clip = await assets.LoadAudioAsync(url, ct); }
            catch { /* silent if the host ships no audio */ }
            if (clip == null) return;

            if (channel != "sfx")
            {
                src.loop = BoolOr(cmd["loop"], true);
                _playingUrl[channel] = url;
            }
            src.clip = clip;
            if (fade > 0f)
            {
                src.volume = 0f;
                src.Play();
                StartCoroutine(FadeAudio(src, 0f, volume, fade, stopAtEnd: false));
            }
            else
            {
                src.volume = volume;
                src.Play();
            }
        }

        // Tolerant field reads (mirror VnStage's): a malformed value degrades to the
        // default instead of throwing and killing the chapter.
        private static float NumOr(JToken t, float dflt)
        {
            if (t == null) return dflt;
            try { return (float)t; } catch { return dflt; }
        }

        private static bool BoolOr(JToken t, bool dflt)
        {
            if (t == null) return dflt;
            try { return (bool)t; } catch { return dflt; }
        }

        private static IEnumerator FadeAudio(AudioSource src, float from, float to, float seconds, bool stopAtEnd)
        {
            float t = 0f;
            while (t < seconds)
            {
                t += Time.unscaledDeltaTime;
                src.volume = Mathf.Lerp(from, to, Mathf.Clamp01(t / seconds));
                yield return null;
            }
            src.volume = to;
            if (stopAtEnd) src.Stop();
        }
    }
}
