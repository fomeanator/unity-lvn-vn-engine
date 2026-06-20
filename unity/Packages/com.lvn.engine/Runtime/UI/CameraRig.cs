using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.UIElements.Experimental;

namespace Lvn.UI
{
    /// <summary>
    /// Camera effects applied to the "world" layer (background + actors), so the
    /// scene shakes and zooms while the UI chrome stays put. Shake jitters the
    /// layer with diminishing amplitude; zoom scales it from its centre. Pure
    /// transform work — no actual camera needed (UI Toolkit renders to a panel).
    /// </summary>
    public sealed class CameraRig
    {
        private readonly VisualElement _t;
        private IVisualElementScheduledItem _shake;
        private float _scale = 1f;

        public CameraRig(VisualElement target)
        {
            _t = target;
            _t.style.transformOrigin = new TransformOrigin(Length.Percent(50), Length.Percent(50));
        }

        public void Shake(float amplitude, float seconds)
        {
            if (amplitude <= 0f || seconds <= 0f) return;
            float start = Time.realtimeSinceStartup;
            _shake?.Pause();
            _shake = _t.schedule.Execute(() =>
            {
                float k = 1f - Mathf.Clamp01((Time.realtimeSinceStartup - start) / seconds);
                if (k <= 0f)
                {
                    _t.style.translate = new Translate(0, 0, 0);
                    _shake.Pause();
                    return;
                }
                float ox = (Random.value * 2f - 1f) * amplitude * k;
                float oy = (Random.value * 2f - 1f) * amplitude * k;
                _t.style.translate = new Translate(ox, oy, 0);
            }).Every(16);
        }

        public void Zoom(float factor, float seconds)
        {
            float from = _scale;
            float to = Mathf.Max(0.1f, factor);
            _scale = to;
            if (seconds <= 0f)
            {
                _t.style.scale = new Scale(new Vector2(to, to));
                return;
            }
            int ms = Mathf.Max(1, Mathf.RoundToInt(seconds * 1000f));
            _t.experimental.animation
                .Start(0f, 1f, ms, (el, p) =>
                {
                    float s = Mathf.LerpUnclamped(from, to, p);
                    el.style.scale = new Scale(new Vector2(s, s));
                })
                .Ease(Easing.InOutCubic);
        }

        public void Reset(float seconds)
        {
            _shake?.Pause();
            _t.style.translate = new Translate(0, 0, 0);
            Zoom(1f, seconds);
        }

        public void Pan(float targetX, float targetY, float seconds)
        {
            var fromT = _t.resolvedStyle.translate;
            // resolvedStyle.translate exposes x/y as floats here — read them directly.
            float fromX = fromT.x;
            float fromY = fromT.y;
            if (seconds <= 0f)
            {
                _t.style.translate = new Translate(targetX, targetY, 0);
                return;
            }
            int ms = Mathf.Max(1, Mathf.RoundToInt(seconds * 1000f));
            _t.experimental.animation
                .Start(0f, 1f, ms, (el, p) =>
                {
                    float x = Mathf.LerpUnclamped(fromX, targetX, p);
                    float y = Mathf.LerpUnclamped(fromY, targetY, p);
                    el.style.translate = new Translate(x, y, 0);
                })
                .Ease(Easing.InOutCubic);
        }
    }
}
