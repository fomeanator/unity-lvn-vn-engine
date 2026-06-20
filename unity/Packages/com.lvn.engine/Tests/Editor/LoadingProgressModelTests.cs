using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class LoadingProgressModelTests
    {
        [Test]
        public void Reset_IsEmpty()
        {
            var m = new LoadingProgressModel();
            m.TickToward(0.8f, 1f);
            m.Reset();
            Assert.AreEqual(0f, m.Display, 0.0001f);
            Assert.AreEqual(0, m.Percent);
        }

        [Test]
        public void TickToward_NeverDecreases()
        {
            var m = new LoadingProgressModel();
            m.TickToward(0.5f, 1f);          // dt*rate >= 1 → reaches target
            var high = m.Display;
            m.TickToward(0.0f, 1f);          // lower target must not rewind the bar
            Assert.GreaterOrEqual(m.Display, high);
        }

        [Test]
        public void SnapToFull_IsHundredPercent()
        {
            var m = new LoadingProgressModel();
            m.SnapToFull();
            Assert.AreEqual(1f, m.Display, 0.0001f);
            Assert.AreEqual(100, m.Percent);
        }

        [Test]
        public void Target_UnknownSet_ZeroWhileActiveOneWhenIdle()
        {
            Assert.AreEqual(0f, LoadingProgressModel.Target(0, 0, 0, 0, active: true), 0.0001f);
            Assert.AreEqual(1f, LoadingProgressModel.Target(0, 0, 0, 0, active: false), 0.0001f);
        }

        [Test]
        public void Target_CountsFilesAndCurrentFileFraction()
        {
            Assert.AreEqual(0.5f, LoadingProgressModel.Target(1, 2, 0, 0, true), 0.0001f);
            Assert.AreEqual(0.75f, LoadingProgressModel.Target(1, 2, 50, 100, true), 0.0001f);
        }

        [Test]
        public void FillPercent_HonoursSpan()
        {
            var full = new LoadingProgressModel(fillSpanPercent: 100f);
            full.TickToward(0.5f, 1f);
            Assert.AreEqual(50f, full.FillPercent, 0.01f);

            var capped = new LoadingProgressModel(fillSpanPercent: 90f);
            capped.TickToward(0.5f, 1f);
            Assert.AreEqual(45f, capped.FillPercent, 0.01f);
        }

        [Test]
        public void Tick_JustFinishedSnapsToFull()
        {
            var m = new LoadingProgressModel();
            m.Tick(0.5f, 0.1f, active: true, 0f, 0f, 4);
            m.Tick(0.5f, 0.1f, active: false, 0f, 0f, 4); // active→idle edge
            Assert.AreEqual(1f, m.Display, 0.0001f);
        }

        [Test]
        public void RaiseTo_OnlyRaises()
        {
            var m = new LoadingProgressModel();
            m.RaiseTo(0.4f);
            Assert.AreEqual(0.4f, m.Display, 0.0001f);
            m.RaiseTo(0.2f);
            Assert.AreEqual(0.4f, m.Display, 0.0001f);
        }

        [Test]
        public void RenderGate_GatesByEpsilonAndChange()
        {
            var g = new ProgressRenderGate();
            Assert.IsTrue(g.FillMoved(10f));   // first write
            Assert.IsFalse(g.FillMoved(10f));  // unchanged
            Assert.IsTrue(g.FillMoved(11f));   // moved past epsilon

            Assert.IsTrue(g.PercentMoved(50));
            Assert.IsFalse(g.PercentMoved(50));
            Assert.IsTrue(g.PercentMoved(51));

            Assert.IsTrue(g.LabelChanged("a"));
            Assert.IsFalse(g.LabelChanged("a"));
            Assert.IsTrue(g.LabelChanged("b"));
        }
    }
}
