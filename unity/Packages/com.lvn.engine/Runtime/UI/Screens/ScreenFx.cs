using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>Small async helpers shared by the built-in screens — currently a
    /// smoothstep opacity fade driven off unscaled time.</summary>
    public static class ScreenFx
    {
        public static async Task FadeAsync(VisualElement el, float from, float to, float seconds, CancellationToken ct)
        {
            if (el == null) return;
            if (seconds <= 0f) { el.style.opacity = to; return; }
            float t0 = Time.unscaledTime;
            while (true)
            {
                if (ct.IsCancellationRequested) { el.style.opacity = to; return; }
                float t = Mathf.Clamp01((Time.unscaledTime - t0) / seconds);
                t = t * t * (3f - 2f * t); // smoothstep
                el.style.opacity = Mathf.Lerp(from, to, t);
                if (t >= 1f) return;
                try { await Task.Yield(); }
                catch (System.OperationCanceledException) { el.style.opacity = to; return; }
            }
        }
    }
}
