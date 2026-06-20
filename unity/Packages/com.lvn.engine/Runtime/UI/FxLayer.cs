using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.UIElements.Experimental;

namespace Lvn.UI
{
    /// <summary>
    /// The full-screen effects overlay (top z-order): screen fades and the
    /// focus-pull dim. A single coloured veil whose alpha animates — fade-to-black
    /// for scene cuts, fade-to-clear to reveal, a partial black for `dim`. Kept
    /// deliberately tiny; richer effects (tint washes, flash) layer on the same
    /// element later.
    /// </summary>
    public sealed class FxLayer : VisualElement
    {
        public FxLayer()
        {
            style.position = Position.Absolute;
            style.left = 0;
            style.right = 0;
            style.top = 0;
            style.bottom = 0;
            style.backgroundColor = Color.clear;
            pickingMode = PickingMode.Ignore;
        }

        /// <summary>Animate the veil to <paramref name="target"/> over
        /// <paramref name="seconds"/> (0 = instant).</summary>
        public void FadeTo(Color target, float seconds)
        {
            var from = resolvedStyle.backgroundColor;
            if (seconds <= 0f)
            {
                style.backgroundColor = target;
                return;
            }
            int ms = Mathf.Max(1, Mathf.RoundToInt(seconds * 1000f));
            experimental.animation
                .Start(0f, 1f, ms, (e, t) => e.style.backgroundColor = Color.Lerp(from, target, t))
                .Ease(Easing.InOutSine);
        }

        /// <summary>Fade to an opaque colour (default black). Common before a
        /// background swap.</summary>
        public void Fade(Color to, float seconds) => FadeTo(new Color(to.r, to.g, to.b, 1f), seconds);

        /// <summary>Clear the veil, revealing the scene.</summary>
        public void Clear(float seconds) => FadeTo(Color.clear, seconds);

        /// <summary>A partial black veil for a focus pull (0 = none, 1 = black).</summary>
        public void Dim(float alpha, float seconds) =>
            FadeTo(new Color(0f, 0f, 0f, Mathf.Clamp01(alpha)), seconds);
    }
}
