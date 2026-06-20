using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// The character layer (z-order 1): sprites placed in horizontal slots
    /// (left / center / right or an explicit x), anchored to the bottom and
    /// sized by a height fraction of the viewport. Non-speakers can be dimmed.
    /// Sprites are resolved by the stage through <see cref="ILvnAssets"/>.
    /// </summary>
    public sealed class ActorLayer : VisualElement
    {
        private readonly Dictionary<string, Image> _actors = new Dictionary<string, Image>();

        public ActorLayer()
        {
            style.position = Position.Absolute;
            style.left = 0;
            style.right = 0;
            style.top = 0;
            style.bottom = 0;
            pickingMode = PickingMode.Ignore;
        }

        /// <summary>Place / update / hide an actor. A null sprite leaves the
        /// current art unchanged.</summary>
        public void Apply(string id, Sprite sprite, string position, float? x, float heightFraction, bool show)
        {
            if (string.IsNullOrEmpty(id)) return;

            if (!_actors.TryGetValue(id, out var img))
            {
                img = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
                img.style.position = Position.Absolute;
                img.style.bottom = 0;
                Add(img);
                _actors[id] = img;
            }

            if (sprite != null) img.sprite = sprite;

            float fx = x ?? SlotX(position);
            float h = heightFraction > 0.05f ? heightFraction : 0.62f;
            img.style.height = Length.Percent(h * 100f);
            img.style.width = Length.Percent(46f);
            img.style.left = Length.Percent(fx * 100f);
            img.style.translate = new Translate(Length.Percent(-50f), 0, 0);
            img.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>Full opacity for the speaker, dim for everyone else. Pass
        /// null to undim all.</summary>
        public void SetSpeaker(string id)
        {
            foreach (var kv in _actors)
                kv.Value.style.opacity = id == null || kv.Key == id ? 1f : 0.55f;
        }

        public void RemoveAll()
        {
            Clear();
            _actors.Clear();
        }

        private static float SlotX(string position)
        {
            switch (position)
            {
                case "left": return 0.25f;
                case "right": return 0.75f;
                case "center": return 0.5f;
                default: return 0.5f;
            }
        }
    }
}
