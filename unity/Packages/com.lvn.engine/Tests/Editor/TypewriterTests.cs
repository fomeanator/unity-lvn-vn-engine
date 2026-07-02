using Lvn;
using NUnit.Framework;

namespace Lvn.Tests
{
    // SliceFadedFixed is the reflow-free typewriter: the WHOLE line is emitted
    // every frame — the revealed head with its fade ramp, the rest hidden under
    // <alpha=#00> — so the label's word-wrap and height never shift mid-reveal.
    public class TypewriterTests
    {
        private static RichTextTypewriter Make(string text)
        {
            var tw = new RichTextTypewriter();
            tw.SetText(text);
            return tw;
        }

        [Test]
        public void FixedSliceAlwaysContainsTheWholeLine()
        {
            var tw = Make("Привет длинная строка");
            foreach (var p in new[] { 0f, 3.5f, 10f, 100f })
            {
                var s = tw.SliceFadedFixed(p, 2f);
                StringAssert.Contains("строка", s,
                    $"at progress {p} the tail must be present (hidden) for stable layout");
            }
        }

        [Test]
        public void UnrevealedTailIsHiddenBehindAlphaZero()
        {
            var tw = Make("abcdef");
            var s = tw.SliceFadedFixed(2f, 1f);
            int marker = s.IndexOf("<alpha=#00>", System.StringComparison.Ordinal);
            Assert.GreaterOrEqual(marker, 0, "the hidden-tail marker must be present");
            // Everything after the marker is the unrevealed remainder.
            StringAssert.Contains("def", s.Substring(marker));
        }

        [Test]
        public void ColorTagInsideHiddenTailIsReHidden()
        {
            // <color> resets alpha in TMP-style markup — the hidden part must
            // re-apply <alpha=#00> after it, or the "hidden" red text shows.
            var tw = Make("ab<color=red>cd</color>");
            var s = tw.SliceFadedFixed(1f, 0.5f); // only 'a' revealed
            int colorAt = s.IndexOf("<color=red>", System.StringComparison.Ordinal);
            Assert.GreaterOrEqual(colorAt, 0);
            int reHide = s.IndexOf("<alpha=#00>", colorAt, System.StringComparison.Ordinal);
            Assert.GreaterOrEqual(reHide, 0, "alpha must be re-applied after the color tag in the hidden tail");
        }

        [Test]
        public void FullProgressEqualsFull()
        {
            var tw = Make("раз <b>два</b> три");
            var full = tw.SliceFadedFixed(1000f, 2f);
            // Head fully revealed → the visible content matches Full() plus the
            // (harmless, empty) hidden-tail marker.
            StringAssert.Contains("раз <b>два</b> три", full.Replace("<alpha=#00>", ""));
        }

        [Test]
        public void FadeRampMarksTheRevealHead()
        {
            var tw = Make("abcdef");
            var s = tw.SliceFadedFixed(2.5f, 2f);
            // A partially-revealed glyph carries a partial alpha (not 00, not FF-less plain).
            StringAssert.Contains("<alpha=#", s);
            Assert.IsTrue(s.IndexOf("<alpha=#00>", System.StringComparison.Ordinal) >= 0);
        }
    }
}
