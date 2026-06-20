using Lvn;
using Lvn.UI;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class ActorLayerTests
    {
        // ── Placement struct ────────────────────────────────────────────────

        [Test]
        public void PlacementStandingFactory()
        {
            var p = Placement.Standing(0.5f);
            Assert.IsTrue(p.Show);
            Assert.AreEqual(0.5f, p.X, 0.001f);
            Assert.AreEqual(1f, p.Y, 0.001f);
            Assert.AreEqual(0.5f, p.AnchorX, 0.001f);
            Assert.AreEqual(1f, p.AnchorY, 0.001f);
            Assert.AreEqual(1f, p.Opacity, 0.001f);
        }

        [Test]
        public void PlacementDefaultValues()
        {
            var p = new Placement();
            Assert.AreEqual(0f, p.X, 0.001f);
            Assert.AreEqual(0f, p.Y, 0.001f);
            Assert.AreEqual(0f, p.AnchorX, 0.001f);
            Assert.AreEqual(0f, p.AnchorY, 0.001f);
            Assert.AreEqual(0f, p.Rotation, 0.001f);
            Assert.AreEqual(0f, p.HoverOpacity, 0.001f);
            Assert.AreEqual(0f, p.TransitionDuration, 0.001f);
            Assert.AreEqual(TransitionType.None, p.EnterTransition);
            Assert.AreEqual(TransitionType.None, p.ExitTransition);
        }

        [Test]
        public void PlacementSizeNullable()
        {
            var p = new Placement();
            Assert.IsNull(p.Width);
            Assert.IsNull(p.Height);
            Assert.IsNull(p.Z);

            p.Width = 0.5f;
            p.Height = 0.7f;
            p.Z = 5;
            Assert.AreEqual(0.5f, (float)p.Width, 0.001f);
            Assert.AreEqual(0.7f, (float)p.Height, 0.001f);
            Assert.AreEqual(5, (int)p.Z);
        }

        // ── TransitionType enum ─────────────────────────────────────────────

        [Test]
        public void TransitionTypeValues()
        {
            Assert.AreEqual(0, (int)TransitionType.None);
            Assert.AreEqual(1, (int)TransitionType.Fade);
            Assert.AreEqual(2, (int)TransitionType.SlideLeft);
            Assert.AreEqual(3, (int)TransitionType.SlideRight);
            Assert.AreEqual(4, (int)TransitionType.Pop);
        }

        // ── SlotX presets ───────────────────────────────────────────────────

        [Test]
        public void SlotXReturnsValidPositions()
        {
            Assert.Greater(ActorLayer.SlotX("far_left"), 0f);
            Assert.Greater(ActorLayer.SlotX("far_right"), ActorLayer.SlotX("right"));
            Assert.Greater(ActorLayer.SlotX("right"), ActorLayer.SlotX("center_right"));
            Assert.Greater(ActorLayer.SlotX("center_right"), ActorLayer.SlotX("center"));
            Assert.Greater(ActorLayer.SlotX("center"), ActorLayer.SlotX("center_left"));
            Assert.Greater(ActorLayer.SlotX("center_left"), ActorLayer.SlotX("left"));
            Assert.Greater(ActorLayer.SlotX("left"), ActorLayer.SlotX("far_left"));
        }

        [Test]
        public void SlotXUnknownReturnsCenter()
        {
            Assert.AreEqual(0.50f, ActorLayer.SlotX("bogus"), 0.001f);
        }

        [Test]
        public void SlotXNullReturnsCenter()
        {
            Assert.AreEqual(0.50f, ActorLayer.SlotX(null), 0.001f);
        }

        // ── Placement field combinations ────────────────────────────────────

        [Test]
        public void PlacementFlipAndRotation()
        {
            var p = new Placement { Flip = true, Rotation = 45f };
            Assert.IsTrue(p.Flip);
            Assert.AreEqual(45f, p.Rotation, 0.001f);
        }

        [Test]
        public void PlacementTransitions()
        {
            var p = new Placement
            {
                EnterTransition = TransitionType.Pop,
                ExitTransition = TransitionType.SlideLeft,
                TransitionDuration = 0.5f,
            };
            Assert.AreEqual(TransitionType.Pop, p.EnterTransition);
            Assert.AreEqual(TransitionType.SlideLeft, p.ExitTransition);
            Assert.AreEqual(0.5f, p.TransitionDuration, 0.001f);
        }

        [Test]
        public void PlacementShowHide()
        {
            var p1 = new Placement { Show = true };
            Assert.IsTrue(p1.Show);

            var p2 = new Placement { Show = false };
            Assert.IsFalse(p2.Show);
        }

        // ── Hover and opacity ───────────────────────────────────────────────

        [Test]
        public void PlacementOpacityAndHover()
        {
            var p = new Placement { Opacity = 0.5f, HoverOpacity = 0.9f };
            Assert.AreEqual(0.5f, p.Opacity, 0.001f);
            Assert.AreEqual(0.9f, p.HoverOpacity, 0.001f);
        }
    }
}
