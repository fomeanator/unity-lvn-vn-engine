using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// A lightweight weather overlay: a pool of small elements that fall and
    /// wrap, configured as rain (fast thin streaks) or snow (slow drifting
    /// dots). Toggled by the `particles` op. Pure UI Toolkit, no textures —
    /// cheap atmosphere that reads instantly.
    /// </summary>
    public sealed class ParticleField : VisualElement
    {
        private struct Mote
        {
            public VisualElement El;
            public float X, Y, Speed, Drift;
        }

        private Mote[] _motes;
        private IVisualElementScheduledItem _tick;
        private float _last;

        public ParticleField()
        {
            style.position = Position.Absolute;
            style.left = 0;
            style.right = 0;
            style.top = 0;
            style.bottom = 0;
            style.overflow = Overflow.Hidden;
            pickingMode = PickingMode.Ignore;
            style.display = DisplayStyle.None;
        }

        /// <summary>Enable a weather type, or hide when <paramref name="on"/> is false.</summary>
        public void Set(string type, bool on)
        {
            if (!on || string.IsNullOrEmpty(type))
            {
                _tick?.Pause();
                style.display = DisplayStyle.None;
                return;
            }

            Build(type == "snow");
            _last = Time.realtimeSinceStartup;
            style.display = DisplayStyle.Flex;
            _tick?.Pause();
            _tick = schedule.Execute(Tick).Every(16);
        }

        private void Build(bool snow)
        {
            Clear();
            int n = snow ? 60 : 110;
            _motes = new Mote[n];
            for (int i = 0; i < n; i++)
            {
                var el = new VisualElement { pickingMode = PickingMode.Ignore };
                el.style.position = Position.Absolute;
                float size = snow ? Random.Range(3f, 7f) : 2f;
                el.style.width = size;
                el.style.height = snow ? size : Random.Range(10f, 18f);
                el.style.backgroundColor = new Color(1f, 1f, 1f, snow ? 0.85f : 0.45f);
                if (snow)
                {
                    el.style.borderTopLeftRadius = size;
                    el.style.borderTopRightRadius = size;
                    el.style.borderBottomLeftRadius = size;
                    el.style.borderBottomRightRadius = size;
                }
                Add(el);

                _motes[i] = new Mote
                {
                    El = el,
                    X = Random.value,
                    Y = Random.value,
                    Speed = snow ? Random.Range(0.05f, 0.12f) : Random.Range(0.5f, 0.9f),
                    Drift = snow ? Random.Range(-0.03f, 0.03f) : 0f,
                };
            }
        }

        private void Tick()
        {
            float now = Time.realtimeSinceStartup;
            float dt = Mathf.Min(0.1f, now - _last);
            _last = now;

            float w = resolvedStyle.width;
            float h = resolvedStyle.height;
            if (w < 1f) w = 1080f;
            if (h < 1f) h = 1920f;

            for (int i = 0; i < _motes.Length; i++)
            {
                var m = _motes[i];
                m.Y += m.Speed * dt;
                m.X += m.Drift * dt;
                if (m.Y > 1f) { m.Y -= 1f; m.X = Random.value; }
                if (m.X < 0f) m.X += 1f;
                else if (m.X > 1f) m.X -= 1f;
                m.El.style.left = m.X * w;
                m.El.style.top = m.Y * h;
                _motes[i] = m;
            }
        }
    }
}
