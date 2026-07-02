using System;
using System.Collections.Generic;
using Lvn.Content;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// The scene seam: everything VnStage asks of "the thing that draws the
    /// background, actors and camera". Two interchangeable renderers implement
    /// it — the UI Toolkit path (BackgroundLayer + ActorLayer + CameraRig) and
    /// the uGUI Canvas path (WorldStage) — so the stage logic stays renderer-
    /// agnostic without a hand-written <c>if (UseCanvasScene)</c> at every call
    /// site. Path-specific behaviour differences live INSIDE the matching
    /// implementation, where they are visible and testable.
    /// </summary>
    internal interface ISceneRenderer
    {
        // ── background ──
        void SetBackground(Sprite sprite);
        /// <summary>Reset the backdrop on a stage wipe. The UITK path clears its
        /// colour layer; the Canvas path keeps its own black board (its historical
        /// behaviour — the next chapter's bg paints over it).</summary>
        void ClearBackground();

        // ── actors ──
        /// <summary>Create + place an actor BEFORE its art has loaded, so the
        /// slot exists for hit-testing/animation immediately. The UITK path
        /// applies placement and art together, so this is a no-op there.</summary>
        void PlaceActor(string id, Placement placement);
        /// <summary>Apply the actor's final state (art layers + placement).</summary>
        void ApplyActor(string id, IReadOnlyList<Sprite> layers, Placement placement, Action onClick,
            IReadOnlyList<string> layerIds, IReadOnlyList<Vector4> layerRects);
        /// <summary>The actor's on-screen rect, normalized 0..1 with a top-left
        /// origin — for manual hotspot hit-testing. Null when this renderer does
        /// its own picking (UITK) or the actor doesn't exist.</summary>
        Rect? ActorScreenRect(string id);
        void RemoveAll();

        // ── per-actor animation ──
        void SetFrames(string id, Dictionary<string, Dictionary<string, Sprite>> frames);
        void EnsureIdle(string id, LvnAnim idle);
        void EnsureBlink(string id, LvnAnim blink);
        void PlayGesture(string id, LvnAnim gesture, LvnAnim idle);
        void PlayAnim(string id, string channel, LvnAnim anim);
        void PlayAnimQueued(string id, string channel, LvnAnim anim);
        void StopAnim(string id, string target);
        void Talk(string id, LvnAnim talk, bool on);
        void HighlightSpeaker(string who);

        // ── camera ──
        void Shake(float amplitude, float seconds);
        void Zoom(float factor, float seconds);
        void Pan(float x, float y, float seconds);
        void ResetCamera(float seconds);
    }

    /// <summary>The UI Toolkit scene: a colour/sprite background layer and an
    /// actor layer inside a "vn-world" element, moved by a CameraRig.</summary>
    internal sealed class UitkSceneRenderer : ISceneRenderer
    {
        private readonly BackgroundLayer _bg;
        private readonly ActorLayer _actors;
        private readonly CameraRig _camera;

        public UitkSceneRenderer(BackgroundLayer bg, ActorLayer actors, CameraRig camera)
        {
            _bg = bg;
            _actors = actors;
            _camera = camera;
        }

        public void SetBackground(Sprite sprite) => _bg.SetSprite(sprite);
        public void ClearBackground() => _bg.SetColor(Color.clear);

        public void PlaceActor(string id, Placement placement) { /* placement applies with the art */ }

        public void ApplyActor(string id, IReadOnlyList<Sprite> layers, Placement placement, Action onClick,
            IReadOnlyList<string> layerIds, IReadOnlyList<Vector4> layerRects)
            => _actors.Apply(id, layers, placement, onClick, layerIds, layerRects);

        public Rect? ActorScreenRect(string id) => null; // UITK elements do their own picking

        public void RemoveAll() => _actors.RemoveAll();

        public void SetFrames(string id, Dictionary<string, Dictionary<string, Sprite>> frames) => _actors.SetFrames(id, frames);
        public void EnsureIdle(string id, LvnAnim idle) => _actors.EnsureIdle(id, idle);
        public void EnsureBlink(string id, LvnAnim blink) => _actors.EnsureBlink(id, blink);
        public void PlayGesture(string id, LvnAnim gesture, LvnAnim idle) => _actors.PlayGesture(id, gesture, idle);
        public void PlayAnim(string id, string channel, LvnAnim anim) => _actors.PlayAnim(id, channel, anim);
        public void PlayAnimQueued(string id, string channel, LvnAnim anim) => _actors.PlayAnimQueued(id, channel, anim);
        public void StopAnim(string id, string target) => _actors.StopAnim(id, target);
        public void Talk(string id, LvnAnim talk, bool on) => _actors.Talk(id, talk, on);
        public void HighlightSpeaker(string who) => _actors.HighlightSpeaker(who);

        public void Shake(float amplitude, float seconds) => _camera.Shake(amplitude, seconds);
        public void Zoom(float factor, float seconds) => _camera.Zoom(factor, seconds);
        public void Pan(float x, float y, float seconds) => _camera.Pan(x, y, seconds);
        public void ResetCamera(float seconds) => _camera.Reset(seconds);
    }

    /// <summary>The uGUI Canvas scene (WorldStage): 60fps sprites/Spine on a
    /// sibling canvas below the UITK chrome.</summary>
    internal sealed class CanvasSceneRenderer : ISceneRenderer
    {
        private readonly World.WorldStage _scene;

        public CanvasSceneRenderer(World.WorldStage scene) => _scene = scene;

        public void SetBackground(Sprite sprite) => _scene.SetBackgroundSprite(sprite);
        public void ClearBackground() { /* the canvas keeps its black board; the next bg paints over */ }

        public void PlaceActor(string id, Placement placement)
            => _scene.ApplyActor(id, null, placement, null, null); // create + place now; art follows

        public void ApplyActor(string id, IReadOnlyList<Sprite> layers, Placement placement, Action onClick,
            IReadOnlyList<string> layerIds, IReadOnlyList<Vector4> layerRects)
        {
            // onClick is intentionally unused: canvas hotspots are hit-tested by the
            // stage (ActorScreenRect), not by per-element handlers. An actor with no
            // loaded art keeps its PlaceActor slot — nothing to re-apply.
            if (layers != null && layers.Count > 0)
                _scene.ApplyActor(id, layers, placement, layerIds, layerRects);
        }

        public Rect? ActorScreenRect(string id)
        {
            var a = _scene.ActorFor(id);
            if (a == null || a.Slot == null) return null;
            float sw = Screen.width, sh = Screen.height;
            if (sw <= 0f || sh <= 0f) return null;
            var c = new Vector3[4];
            a.Slot.GetWorldCorners(c); // ScreenSpaceOverlay → screen pixels (y-up)
            float left = Mathf.Min(c[0].x, c[2].x) / sw, right = Mathf.Max(c[0].x, c[2].x) / sw;
            float top = 1f - Mathf.Max(c[0].y, c[2].y) / sh, bot = 1f - Mathf.Min(c[0].y, c[2].y) / sh;
            return Rect.MinMaxRect(left, top, right, bot); // normalized, top-left origin
        }

        public void RemoveAll() => _scene.RemoveAll();

        public void SetFrames(string id, Dictionary<string, Dictionary<string, Sprite>> frames) => _scene.SetFrames(id, frames);
        public void EnsureIdle(string id, LvnAnim idle) => _scene.EnsureIdle(id, idle);
        public void EnsureBlink(string id, LvnAnim blink) => _scene.EnsureBlink(id, blink);
        public void PlayGesture(string id, LvnAnim gesture, LvnAnim idle) => _scene.PlayGesture(id, gesture, idle);
        public void PlayAnim(string id, string channel, LvnAnim anim) => _scene.PlayAnim(id, channel, anim);
        public void PlayAnimQueued(string id, string channel, LvnAnim anim) => _scene.PlayAnimQueued(id, channel, anim);
        public void StopAnim(string id, string target) => _scene.StopAnim(id, target);
        public void Talk(string id, LvnAnim talk, bool on) => _scene.Talk(id, talk, on);
        public void HighlightSpeaker(string who) => _scene.HighlightSpeaker(who);

        public void Shake(float amplitude, float seconds) => _scene.Shake(amplitude, seconds);
        public void Zoom(float factor, float seconds) => _scene.Zoom(factor, seconds);
        public void Pan(float x, float y, float seconds) => _scene.Pan(x, y, seconds);
        public void ResetCamera(float seconds) => _scene.ResetCamera(seconds);
    }
}
