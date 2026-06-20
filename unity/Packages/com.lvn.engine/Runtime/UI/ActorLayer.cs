using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.UIElements.Experimental;

namespace Lvn.UI
{
    public enum TransitionType
    {
        None,
        Fade,
        SlideLeft,
        SlideRight,
        Pop,
    }

    /// <summary>
    /// Where to put a stage object, all in screen fractions so a script controls
    /// it without knowing the resolution: the object's <see cref="AnchorX"/>/
    /// <see cref="AnchorY"/> point (0..1 of the object) is placed at
    /// <see cref="X"/>/<see cref="Y"/> (0..1 of the screen), sized by
    /// <see cref="Width"/>/<see cref="Height"/>, ordered by <see cref="Z"/>, with
    /// optional <see cref="Flip"/>, <see cref="Rotation"/> and <see cref="Opacity"/>.
    /// Defaults give the classic standing character: bottom-centre anchored.
    /// </summary>
    public struct Placement
    {
        public bool Show;
        public float X, Y;          // screen position of the anchor point (0..1)
        public float? Width, Height; // size as a fraction of the screen (0..1)
        public float AnchorX, AnchorY;
        public int? Z;
        public bool Flip;
        public float Rotation;       // degrees
        public float Opacity;
        public float HoverOpacity;
        public TransitionType EnterTransition;
        public TransitionType ExitTransition;
        public float TransitionDuration;

        public static Placement Standing(float x) => new Placement
        {
            Show = true, X = x, Y = 1f, AnchorX = 0.5f, AnchorY = 1f, Opacity = 1f,
        };
    }

    /// <summary>
    /// The object layer (z-order 1): every actor or prop is a slot placed by a
    /// <see cref="Placement"/> and drawn as a bottom-to-top stack of sprite
    /// layers. Characters are just objects that also dim when not speaking — the
    /// same `Apply` puts <em>any</em> sprite on screen from a script.
    /// </summary>
    public sealed class ActorLayer : VisualElement
    {
        private readonly Dictionary<string, VisualElement> _slots = new Dictionary<string, VisualElement>();
        private readonly Dictionary<VisualElement, int> _z = new Dictionary<VisualElement, int>();
        private readonly Dictionary<string, Action> _onClick = new Dictionary<string, Action>();
        private readonly Dictionary<string, float> _hoverOpacity = new Dictionary<string, float>();
        private readonly Dictionary<string, float> _baseOpacity = new Dictionary<string, float>();
        private int _nextZ;

        public ActorLayer()
        {
            style.position = Position.Absolute;
            style.left = 0;
            style.right = 0;
            style.top = 0;
            style.bottom = 0;
            pickingMode = PickingMode.Ignore;
        }

        /// <summary>Place / update / hide an object as a stack of layer sprites.
        /// A null/empty list leaves the current art unchanged. When
        /// <paramref name="onClick"/> is set the object becomes a tappable hotspot
        /// (and swallows the tap so it doesn't also advance the dialogue).</summary>
        public void Apply(string id, IReadOnlyList<Sprite> layers, Placement p, Action onClick = null)
        {
            if (string.IsNullOrEmpty(id)) return;

            if (!_slots.TryGetValue(id, out var slot))
            {
                slot = new VisualElement { name = "vn-obj-" + id, pickingMode = PickingMode.Ignore };
                slot.style.position = Position.Absolute;
                var capturedId = id;
                slot.RegisterCallback<PointerDownEvent>(evt =>
                {
                    if (_onClick.TryGetValue(capturedId, out var cb) && cb != null)
                    {
                        cb();
                        evt.StopPropagation();
                    }
                });
                slot.RegisterCallback<MouseEnterEvent>(_ =>
                {
                    if (_hoverOpacity.TryGetValue(capturedId, out var hover))
                        slot.style.opacity = hover;
                });
                slot.RegisterCallback<MouseLeaveEvent>(_ =>
                {
                    if (_baseOpacity.TryGetValue(capturedId, out var baseOp))
                        slot.style.opacity = baseOp;
                });
                Add(slot);
                _slots[id] = slot;
                _z[slot] = _nextZ++;
            }

            _onClick[id] = onClick;
            _hoverOpacity[id] = p.HoverOpacity;
            _baseOpacity[id] = p.Opacity;
            // Only hotspots are pickable; everything else lets taps fall through
            // to the stage's tap-to-advance.
            slot.pickingMode = onClick != null ? PickingMode.Position : PickingMode.Ignore;

            if (layers != null && layers.Count > 0)
            {
                slot.Clear();
                foreach (var sprite in layers)
                {
                    if (sprite == null) continue;
                    var img = new Image { sprite = sprite, scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
                    img.style.position = Position.Absolute;
                    img.style.left = 0; img.style.right = 0; img.style.top = 0; img.style.bottom = 0;
                    slot.Add(img);
                }
            }

            slot.style.width = Length.Percent((p.Width ?? 0.46f) * 100f);
            slot.style.height = Length.Percent((p.Height ?? 0.62f) * 100f);
            slot.style.left = Length.Percent(p.X * 100f);
            slot.style.top = Length.Percent(p.Y * 100f);
            // Translate so the object's own anchor point lands on (X, Y); UITK
            // percent translate is relative to the element's own size.
            slot.style.translate = new Translate(Length.Percent(-p.AnchorX * 100f), Length.Percent(-p.AnchorY * 100f), 0);
            slot.style.scale = new Scale(new Vector2(p.Flip ? -1f : 1f, 1f));
            slot.style.rotate = new Rotate(new Angle(p.Rotation, AngleUnit.Degree));
            slot.style.opacity = p.Opacity;
            slot.style.display = p.Show ? DisplayStyle.Flex : DisplayStyle.None;

            if (p.Show && p.EnterTransition != TransitionType.None)
                PlayTransition(slot, p.EnterTransition, p.TransitionDuration, p);
            else if (!p.Show && p.ExitTransition != TransitionType.None)
                PlayTransition(slot, p.ExitTransition, p.TransitionDuration, p);

            if (p.Z.HasValue)
            {
                _z[slot] = p.Z.Value;
                Sort((a, b) => ZOf(a).CompareTo(ZOf(b)));
            }
        }

        /// <summary>Full opacity for the speaker, dim for everyone else (null = undim all).</summary>
        public void SetSpeaker(string id)
        {
            foreach (var kv in _slots)
            {
                float target = id == null || kv.Key == id ? 1f : 0.55f;
                kv.Value.style.opacity = target;
                _baseOpacity[kv.Key] = target;
            }
        }

        public void RemoveAll()
        {
            Clear();
            _slots.Clear();
            _z.Clear();
            _onClick.Clear();
            _hoverOpacity.Clear();
            _baseOpacity.Clear();
            _nextZ = 0;
        }

        private int ZOf(VisualElement e) => _z.TryGetValue(e, out var z) ? z : 0;

        private void PlayTransition(VisualElement slot, TransitionType type, float duration, Placement p)
        {
            if (duration <= 0f) duration = 0.3f;
            int ms = Mathf.Max(1, Mathf.RoundToInt(duration * 1000f));

            switch (type)
            {
                case TransitionType.Fade:
                    slot.style.opacity = 0f;
                    slot.experimental.animation
                        .Start(0f, p.Opacity, ms, (e, t) => e.style.opacity = Mathf.Lerp(0f, p.Opacity, t))
                        .Ease(Easing.InOutSine);
                    break;

                case TransitionType.SlideLeft:
                    float targetLeft = p.X * 100f;
                    slot.style.left = Length.Percent(-20f);
                    slot.experimental.animation
                        .Start(0f, 1f, ms, (e, t) =>
                        {
                            float v = Mathf.Lerp(-20f, targetLeft, t);
                            e.style.left = Length.Percent(v);
                        })
                        .Ease(Easing.OutCubic);
                    break;

                case TransitionType.SlideRight:
                    float targetRight = p.X * 100f;
                    slot.style.left = Length.Percent(120f);
                    slot.experimental.animation
                        .Start(0f, 1f, ms, (e, t) =>
                        {
                            float v = Mathf.Lerp(120f, targetRight, t);
                            e.style.left = Length.Percent(v);
                        })
                        .Ease(Easing.OutCubic);
                    break;

                case TransitionType.Pop:
                    slot.style.scale = new Scale(new Vector2(0f, 0f));
                    slot.experimental.animation
                        .Start(0f, 1f, ms, (e, t) =>
                        {
                            float s = Mathf.Lerp(0f, 1f, t);
                            e.style.scale = new Scale(new Vector2(s, s));
                        })
                        .Ease(Easing.OutBack);
                    break;
            }
        }

        /// <summary>Named horizontal placement presets — the common VN slots from
        /// far-left to far-right (plus a few in between). A script can ignore
        /// these and give an explicit x fraction instead.</summary>
        public static float SlotX(string position)
        {
            switch (position)
            {
                case "far_left": return 0.12f;
                case "left": return 0.25f;
                case "center_left": return 0.38f;
                case "center": return 0.50f;
                case "center_right": return 0.62f;
                case "right": return 0.75f;
                case "far_right": return 0.88f;
                default: return 0.50f;
            }
        }
    }
}
