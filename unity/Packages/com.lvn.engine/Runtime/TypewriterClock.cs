namespace Lvn
{
    /// <summary>
    /// Pure timing for the dialogue typewriter — turns elapsed seconds into a
    /// fractional glyph-reveal head, and decides when the whole line (including
    /// its soft trailing fade) has finished. No UnityEngine, so the cadence is
    /// testable rather than an inline formula.
    /// </summary>
    public static class TypewriterClock
    {
        /// <summary>Minimum characters-per-second (guards a 0/garbage cps).</summary>
        public const float MinCps = 1f;

        /// <summary>Global characters-per-second override. Set by the `text_pace`
        /// op. 0 or negative means "use the per-line default".</summary>
        public static float GlobalCps;

        /// <summary>Reveal head position, in glyphs (fractional), after
        /// <paramref name="elapsedSeconds"/> at <paramref name="cps"/>.</summary>
        public static float Progress(float elapsedSeconds, float cps)
        {
            if (elapsedSeconds < 0f) elapsedSeconds = 0f;
            float rate = GlobalCps > MinCps ? GlobalCps : (cps > MinCps ? cps : MinCps);
            return elapsedSeconds * rate;
        }

        /// <summary>Progress (in glyphs) at which the last glyph is fully opaque,
        /// given a soft fade of <paramref name="fadeWidth"/> trailing chars.
        /// Empty line → 0 (already done).</summary>
        public static float DoneAt(int totalGlyphs, float fadeWidth)
        {
            if (totalGlyphs <= 0) return 0f;
            if (fadeWidth < 0f) fadeWidth = 0f;
            return totalGlyphs - 1 + fadeWidth;
        }
    }
}
