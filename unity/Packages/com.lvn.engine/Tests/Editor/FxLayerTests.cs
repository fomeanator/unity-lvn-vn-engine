using Lvn;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class FxLayerTests
    {
        [Test]
        public void TypewriterClockGlobalCpsOverridesDefault()
        {
            TypewriterClock.GlobalCps = 0f;
            float defaultProgress = TypewriterClock.Progress(1f, 30f);
            Assert.AreEqual(30f, defaultProgress, 0.001f);

            TypewriterClock.GlobalCps = 60f;
            float globalProgress = TypewriterClock.Progress(1f, 30f);
            Assert.AreEqual(60f, globalProgress, 0.001f);

            TypewriterClock.GlobalCps = 0f;
        }

        [Test]
        public void TypewriterClockProgressUsesDefaultWhenGlobalZero()
        {
            TypewriterClock.GlobalCps = 0f;
            float progress = TypewriterClock.Progress(2f, 20f);
            Assert.AreEqual(40f, progress, 0.001f);
        }

        [Test]
        public void TypewriterClockDoneAtCalculatesCorrectly()
        {
            float done = TypewriterClock.DoneAt(10, 3f);
            Assert.AreEqual(12f, done, 0.001f);

            float doneNoFade = TypewriterClock.DoneAt(10, 0f);
            Assert.AreEqual(9f, doneNoFade, 0.001f);
        }

        [Test]
        public void TypewriterClockMinCpsGuardsAgainstZero()
        {
            TypewriterClock.GlobalCps = 0f;
            float progress = TypewriterClock.Progress(1f, 0f);
            Assert.AreEqual(TypewriterClock.MinCps, progress, 0.001f);
        }
    }
}
