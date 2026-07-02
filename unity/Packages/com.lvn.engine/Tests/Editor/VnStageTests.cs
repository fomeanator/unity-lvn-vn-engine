using System.Collections.Generic;
using Lvn;
using Lvn.UI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    public class VnStageTests
    {
        // ── PlacementFrom ───────────────────────────────────────────────────

        [Test]
        public void PlacementFromDefaultValues()
        {
            var cmd = new JObject { ["op"] = "actor", ["id"] = "x" };
            var p = VnStage.PlacementFrom(cmd);
            Assert.IsTrue(p.Show);
            Assert.AreEqual(0.5f, p.X, 0.001f);
            Assert.AreEqual(1f, p.Y, 0.001f);
            Assert.AreEqual(0.5f, p.AnchorX, 0.001f);
            Assert.AreEqual(1f, p.AnchorY, 0.001f);
            Assert.AreEqual(1f, p.Opacity, 0.001f);
            Assert.AreEqual(1f, p.HoverOpacity, 0.001f);
            Assert.AreEqual(0.3f, p.TransitionDuration, 0.001f);
        }

        [Test]
        public void PlacementFromExplicitPosition()
        {
            var cmd = new JObject
            {
                ["op"] = "actor", ["id"] = "x",
                ["position"] = "left",
            };
            var p = VnStage.PlacementFrom(cmd);
            Assert.AreEqual(0.25f, p.X, 0.001f);
        }

        [Test]
        public void PlacementFromExplicitXY()
        {
            var cmd = new JObject
            {
                ["op"] = "actor", ["id"] = "x",
                ["x"] = 0.3f, ["y"] = 0.7f,
            };
            var p = VnStage.PlacementFrom(cmd);
            Assert.AreEqual(0.3f, p.X, 0.001f);
            Assert.AreEqual(0.7f, p.Y, 0.001f);
        }

        [Test]
        public void PlacementFromSizeAnchorFlip()
        {
            var cmd = new JObject
            {
                ["op"] = "actor", ["id"] = "x",
                ["width"] = 0.6f, ["height"] = 0.8f,
                ["anchor"] = "0.5,0.5",
                ["flip"] = true,
                ["rotation"] = 15f,
                ["z"] = 10,
            };
            var p = VnStage.PlacementFrom(cmd);
            Assert.AreEqual(0.6f, (float)p.Width, 0.001f);
            Assert.AreEqual(0.8f, (float)p.Height, 0.001f);
            Assert.AreEqual(0.5f, p.AnchorX, 0.001f);
            Assert.AreEqual(0.5f, p.AnchorY, 0.001f);
            Assert.IsTrue(p.Flip);
            Assert.AreEqual(15f, p.Rotation, 0.001f);
            Assert.AreEqual(10, (int)p.Z);
        }

        [Test]
        public void PlacementFromAnchorXYFields()
        {
            var cmd = new JObject
            {
                ["op"] = "actor", ["id"] = "x",
                ["anchor_x"] = 0.3f,
                ["anchor_y"] = 0.7f,
            };
            var p = VnStage.PlacementFrom(cmd);
            Assert.AreEqual(0.3f, p.AnchorX, 0.001f);
            Assert.AreEqual(0.7f, p.AnchorY, 0.001f);
        }

        [Test]
        public void PlacementFromShowFalse()
        {
            var cmd = new JObject
            {
                ["op"] = "actor", ["id"] = "x",
                ["show"] = false,
            };
            var p = VnStage.PlacementFrom(cmd);
            Assert.IsFalse(p.Show);
        }

        [Test]
        public void PlacementFromOpacityAndHover()
        {
            var cmd = new JObject
            {
                ["op"] = "actor", ["id"] = "x",
                ["opacity"] = 0.6f,
                ["hover_opacity"] = 0.9f,
            };
            var p = VnStage.PlacementFrom(cmd);
            Assert.AreEqual(0.6f, p.Opacity, 0.001f);
            Assert.AreEqual(0.9f, p.HoverOpacity, 0.001f);
        }

        [Test]
        public void PlacementFromTransitions()
        {
            var cmd = new JObject
            {
                ["op"] = "actor", ["id"] = "x",
                ["enter"] = "fade",
                ["exit"] = "slide_left",
                ["transition_duration"] = 0.5f,
            };
            var p = VnStage.PlacementFrom(cmd);
            Assert.AreEqual(TransitionType.Fade, p.EnterTransition);
            Assert.AreEqual(TransitionType.SlideLeft, p.ExitTransition);
            Assert.AreEqual(0.5f, p.TransitionDuration, 0.001f);
        }

        // A malformed field (wrong type) must degrade to a sensible default rather
        // than throw and abort the whole chapter. Numeric/bool written as strings
        // are still honoured.
        [Test]
        public void PlacementFromMalformedFieldsDoNotThrow()
        {
            var cmd = new JObject
            {
                ["op"] = "actor", ["id"] = "x", ["position"] = "right",
                ["x"] = "not-a-number", // malformed → falls back to the slot for "right"
                ["opacity"] = "0.5",    // numeric string → parsed
                ["show"] = "true",      // stringy bool → true
                ["flip"] = "yes",       // stringy bool → true
                ["z"] = "abc",          // malformed → null
                ["rotation"] = new JArray(1, 2), // wrong type → default 0
            };
            Placement p = default;
            Assert.DoesNotThrow(() => p = VnStage.PlacementFrom(cmd));
            Assert.AreEqual(ActorLayer.SlotX("right"), p.X, 0.001f); // malformed x → slot
            Assert.AreEqual(0.5f, p.Opacity, 0.001f);               // "0.5" parsed
            Assert.IsTrue(p.Show);                                  // "true" parsed
            Assert.IsTrue(p.Flip);                                  // "yes" parsed
            Assert.IsFalse(p.Z.HasValue);                           // "abc" → null
            Assert.AreEqual(0f, p.Rotation, 0.001f);                // array → default
        }

        // ── ParseTransition ─────────────────────────────────────────────────

        [Test]
        public void ParseTransitionFade()
        {
            Assert.AreEqual(TransitionType.Fade, VnStage.ParseTransition("fade"));
            Assert.AreEqual(TransitionType.Fade, VnStage.ParseTransition("Fade"));
        }

        [Test]
        public void ParseTransitionSlideLeft()
        {
            Assert.AreEqual(TransitionType.SlideLeft, VnStage.ParseTransition("slide_left"));
        }

        [Test]
        public void ParseTransitionSlideRight()
        {
            Assert.AreEqual(TransitionType.SlideRight, VnStage.ParseTransition("slide_right"));
        }

        [Test]
        public void ParseTransitionPop()
        {
            Assert.AreEqual(TransitionType.Pop, VnStage.ParseTransition("pop"));
        }

        [Test]
        public void ParseTransitionNullReturnsNone()
        {
            Assert.AreEqual(TransitionType.None, VnStage.ParseTransition(null));
            Assert.AreEqual(TransitionType.None, VnStage.ParseTransition(""));
        }

        [Test]
        public void ParseTransitionUnknownReturnsNone()
        {
            Assert.AreEqual(TransitionType.None, VnStage.ParseTransition("bounce"));
        }

        // ── ParseColor ─────────────────────────────────────────────────────

        [Test]
        public void ParseColorNamedColors()
        {
            Assert.AreEqual(Color.white, VnStage.ParseColor("white", Color.black));
            Assert.AreEqual(Color.black, VnStage.ParseColor("black", Color.white));
            Assert.AreEqual(Color.red, VnStage.ParseColor("red", Color.black));
            Assert.AreEqual(Color.blue, VnStage.ParseColor("blue", Color.black));
            Assert.AreEqual(Color.green, VnStage.ParseColor("green", Color.black));
            Assert.AreEqual(Color.yellow, VnStage.ParseColor("yellow", Color.black));
            Assert.AreEqual(Color.cyan, VnStage.ParseColor("cyan", Color.black));
            Assert.AreEqual(Color.magenta, VnStage.ParseColor("magenta", Color.black));
        }

        [Test]
        public void ParseColorTintColors()
        {
            var cold = VnStage.ParseColor("cold", Color.black);
            Assert.Greater(cold.b, cold.r);

            var warm = VnStage.ParseColor("warm", Color.black);
            Assert.Greater(warm.r, warm.b);

            var sepia = VnStage.ParseColor("sepia", Color.black);
            Assert.Greater(sepia.r, sepia.b);
        }

        [Test]
        public void ParseColorNullReturnsFallback()
        {
            Assert.AreEqual(Color.magenta, VnStage.ParseColor(null, Color.magenta));
            Assert.AreEqual(Color.magenta, VnStage.ParseColor("", Color.magenta));
        }

        [Test]
        public void ParseColorUnknownReturnsFallback()
        {
            Assert.AreEqual(Color.magenta, VnStage.ParseColor("chartreuse", Color.magenta));
        }

        // ── AxesFrom ───────────────────────────────────────────────────────

        [Test]
        public void AxesFromExtractsFreeFields()
        {
            var cmd = new JObject
            {
                ["op"] = "actor", ["id"] = "mara",
                ["emotion"] = "happy",
                ["pose"] = "stand",
            };
            var axes = VnStage.AxesFrom(cmd);
            Assert.AreEqual("happy", axes["emotion"]);
            Assert.AreEqual("stand", axes["pose"]);
            Assert.IsFalse(axes.ContainsKey("op"));
            Assert.IsFalse(axes.ContainsKey("id"));
        }

        [Test]
        public void AxesFromSkipsReservedFields()
        {
            var cmd = new JObject
            {
                ["op"] = "actor", ["id"] = "x",
                ["show"] = true, ["position"] = "left",
                ["x"] = 0.5, ["y"] = 0.5,
                ["width"] = 0.4, ["height"] = 0.6,
                ["opacity"] = 1.0, ["flip"] = false,
                ["on_click"] = "label",
                ["emotion"] = "sad",
            };
            var axes = VnStage.AxesFrom(cmd);
            Assert.AreEqual(1, axes.Count);
            Assert.AreEqual("sad", axes["emotion"]);
        }

        [Test]
        public void AxesFromSkipsNullAndArrayValues()
        {
            var cmd = new JObject
            {
                ["op"] = "actor", ["id"] = "x",
                ["nested"] = new JArray(1, 2, 3),
                ["emotion"] = "happy",
            };
            var axes = VnStage.AxesFrom(cmd);
            Assert.AreEqual(1, axes.Count);
            Assert.AreEqual("happy", axes["emotion"]);
        }

        // ── ReservedActorFields ─────────────────────────────────────────────

        [Test]
        public void ReservedActorFieldsContainsExpectedEntries()
        {
            var reserved = new HashSet<string>
            {
                "op", "id", "show", "position", "x", "y", "width", "height", "scale",
                "anchor", "anchor_x", "anchor_y", "z", "flip", "rotation", "opacity",
                "on_click", "hover_opacity", "breathing", "sprite_url", "body_url",
                "clothes_url", "hair_url", "transition", "enter", "exit",
                "transition_duration",
            };
            foreach (var field in reserved)
            {
                Assert.IsTrue(VnStage.ReservedActorFields.Contains(field),
                    $"ReservedActorFields should contain '{field}'");
            }
        }

        // ── AllPositionPresets ──────────────────────────────────────────────

        [Test]
        public void AllPositionPresetsReturnExpectedValues()
        {
            Assert.AreEqual(0.12f, ActorLayer.SlotX("far_left"), 0.001f);
            Assert.AreEqual(0.25f, ActorLayer.SlotX("left"), 0.001f);
            Assert.AreEqual(0.38f, ActorLayer.SlotX("center_left"), 0.001f);
            Assert.AreEqual(0.50f, ActorLayer.SlotX("center"), 0.001f);
            Assert.AreEqual(0.62f, ActorLayer.SlotX("center_right"), 0.001f);
            Assert.AreEqual(0.75f, ActorLayer.SlotX("right"), 0.001f);
            Assert.AreEqual(0.88f, ActorLayer.SlotX("far_right"), 0.001f);
            Assert.AreEqual(0.50f, ActorLayer.SlotX("unknown"), 0.001f);
        }
    }
}
