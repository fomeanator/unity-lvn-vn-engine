using System;

namespace Lvn.Content
{
    /// <summary>
    /// Pure progress model for the loading screen — the single source of the
    /// "0 → 100%" math. It turns either raw batch counters (files done/total,
    /// bytes received/expected) OR an external 0..1 provider into a smoothed,
    /// MONOTONIC display fraction in [0..1], plus the derived fill width and
    /// percent. No UnityEngine — fully unit-testable, so the bar's behaviour
    /// (never goes backwards, snaps to 100% when a batch finishes, fakes a floor
    /// when there's nothing to download) is pinned by tests, not eyeballed.
    /// </summary>
    public sealed class LoadingProgressModel
    {
        /// <summary>Default exponential approach rate per second (Lerp k = dt * this).</summary>
        public const float SmoothRate = 4f;

        /// <summary>Default fill span: a sprite bar's fill may only cover 90% of
        /// the track at 100% display; a plain colour bar passes 100.</summary>
        public const float FillSpanPercent = 100f;

        private readonly float _smoothRate;
        private readonly float _fillSpan;

        private float _display;     // smoothed value shown to the player
        private bool _wasActive;    // batch active last tick (edge detect)

        public LoadingProgressModel(float smoothRate = SmoothRate, float fillSpanPercent = FillSpanPercent)
        {
            _smoothRate = smoothRate > 0f ? smoothRate : SmoothRate;
            _fillSpan = fillSpanPercent > 0f ? fillSpanPercent : FillSpanPercent;
        }

        /// <summary>Smoothed fraction in [0..1] currently shown. Never decreases.</summary>
        public float Display => _display;

        /// <summary>Display as an integer percent (0..100), round-half-up.</summary>
        public int Percent => (int)Math.Round(_display * 100f, MidpointRounding.AwayFromZero);

        /// <summary>Width (in %) the fill element should take — Display mapped onto
        /// this instance's usable span.</summary>
        public float FillPercent => Clamp01(_display) * _fillSpan;

        /// <summary>Reset to empty (call when a new loading run begins).</summary>
        public void Reset()
        {
            _display = 0f;
            _wasActive = false;
        }

        /// <summary>Raw target fraction from the batch counters, independent of
        /// smoothing. <c>filesTotal == 0</c> → unknown set: 0 while active, 1 once
        /// idle; otherwise (completed files + fractional current file) / total.</summary>
        public static float Target(int filesDone, int filesTotal,
            long bytesReceived, long bytesExpected, bool active)
        {
            if (filesTotal <= 0) return active ? 0f : 1f;
            float filePct = bytesExpected > 0 ? Clamp01((float)bytesReceived / bytesExpected) : 0f;
            return Clamp01((filesDone + filePct) / filesTotal);
        }

        /// <summary>Advance one frame toward <paramref name="target"/> and return
        /// the new Display. Active → exponential approach, clamped so it NEVER
        /// decreases; just went idle → snap to 100%; idle with an empty set →
        /// time-driven fake floor so a cache-hot entry still animates.</summary>
        public float Tick(float target, float dt, bool active,
            float idleElapsed, float idleMinSeconds, int filesTotal)
        {
            bool justFinished = _wasActive && !active;
            _wasActive = active;

            if (active)
            {
                _display = Math.Max(_display, Approach(target, dt));
            }
            else if (justFinished)
            {
                _display = 1f;
            }
            else if (filesTotal == 0)
            {
                float fake = idleMinSeconds > 0f ? Clamp01(idleElapsed / idleMinSeconds) : 1f;
                if (fake > _display) _display = fake;
            }

            return _display;
        }

        /// <summary>Monotonic smoothing toward an external 0..1 target (no batch
        /// counters). Used when progress comes from a caller-supplied provider.</summary>
        public float TickToward(float target, float dt)
        {
            _display = Math.Max(_display, Approach(Clamp01(target), dt));
            return _display;
        }

        /// <summary>Snap straight to full (batch finished). Monotonic by definition.</summary>
        public void SnapToFull() => _display = 1f;

        // Idle-creep curve for the app-boot phase before any batch starts: the bar
        // rises asymptotically toward a low ceiling so it isn't stuck at 0% while
        // pre-download work runs, and the ceiling stays far below real batch
        // progress so the creep can't fight the bar once downloads begin.
        public const float IdleCreepCeiling = 0.08f;
        public const float IdleCreepTau = 1.2f;

        /// <summary>Time-driven creep target (0 → <see cref="IdleCreepCeiling"/>)
        /// for the pre-download boot phase. Feed it to <see cref="RaiseTo"/>.</summary>
        public static float IdleCreepTarget(float elapsed) =>
            (1f - (float)Math.Exp(-elapsed / IdleCreepTau)) * IdleCreepCeiling;

        /// <summary>Raise the display to at least <paramref name="v"/> (never lowers).</summary>
        public float RaiseTo(float v)
        {
            float c = Clamp01(v);
            if (c > _display) _display = c;
            return _display;
        }

        // Lerp(_display, target, clamp01(dt*rate)) — exponential approach.
        private float Approach(float target, float dt)
        {
            float k = Clamp01(dt * _smoothRate);
            return _display + (target - _display) * k;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }

    /// <summary>
    /// Per-frame render gate for the loading screen. The model's smoothing keeps
    /// drifting by epsilons forever, so a view that writes width/opacity/text
    /// every tick keeps dirtying layout and allocating strings even when the bar
    /// is visually frozen. Views route writes through this gate — it answers "did
    /// the visible value actually move?" so the element is touched only when it
    /// did. Pure, no UnityEngine.
    /// </summary>
    public sealed class ProgressRenderGate
    {
        public const float FillEpsilon = 0.05f;        // ~0.5px on a 1080 track
        public const float OpacityEpsilon = 1f / 255f; // one 8-bit alpha step

        private float _fill = float.MinValue;
        private int _percent = int.MinValue;
        private float _opacity = float.MinValue;
        private string _label;
        private bool _labelSet;

        public void Reset()
        {
            _fill = float.MinValue;
            _percent = int.MinValue;
            _opacity = float.MinValue;
            _label = null;
            _labelSet = false;
        }

        public bool FillMoved(float fillPercent)
        {
            if (Math.Abs(fillPercent - _fill) < FillEpsilon) return false;
            _fill = fillPercent;
            return true;
        }

        public bool PercentMoved(int percent)
        {
            if (percent == _percent) return false;
            _percent = percent;
            return true;
        }

        public bool OpacityMoved(float opacity)
        {
            if (Math.Abs(opacity - _opacity) < OpacityEpsilon) return false;
            _opacity = opacity;
            return true;
        }

        public bool LabelChanged(string label)
        {
            if (_labelSet && string.Equals(label, _label, StringComparison.Ordinal)) return false;
            _label = label;
            _labelSet = true;
            return true;
        }
    }
}
