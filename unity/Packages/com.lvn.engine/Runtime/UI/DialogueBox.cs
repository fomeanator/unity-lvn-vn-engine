using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// The dialogue panel: a nameplate above a body panel that reveals text with
    /// a soft per-glyph fade (driven by <see cref="RichTextTypewriter"/> +
    /// <see cref="TypewriterClock"/>). A props-driven <see cref="VisualElement"/>
    /// — no networking, no asset loader, no game-specific ornament. Anchor it to
    /// the bottom of a UIDocument root; the host taps to advance and calls
    /// <see cref="Complete"/> / <see cref="Reveal"/>.
    /// </summary>
    public sealed class DialogueBox : VisualElement
    {
        private readonly VnTheme _theme;
        private readonly VisualElement _plate;
        private readonly VisualElement _panel;
        private readonly Label _speaker;
        private readonly Label _body;
        private readonly RichTextTypewriter _tw = new RichTextTypewriter();

        private IVisualElementScheduledItem _tick;
        private float _startTime;
        private float _cps;

        /// <summary>True while the typewriter is still revealing the line.</summary>
        public bool IsRevealing { get; private set; }

        public DialogueBox(VnTheme theme)
        {
            _theme = theme ?? new VnTheme();

            style.position = Position.Absolute;
            style.left = 0;
            style.right = 0;
            style.bottom = 0;
            style.paddingLeft = 24;
            style.paddingRight = 24;
            style.paddingBottom = 28;

            // Nameplate (hidden for narration).
            _plate = new VisualElement { name = "vn-plate" };
            _plate.style.alignSelf = Align.FlexStart;
            _plate.style.backgroundColor = _theme.PanelColor;
            _plate.style.paddingLeft = 14;
            _plate.style.paddingRight = 14;
            _plate.style.paddingTop = 4;
            _plate.style.paddingBottom = 4;
            _plate.style.marginBottom = -2;
            SetCorner(_plate, _theme.PanelCornerRadius * 0.6f, top: true, bottom: false);
            _speaker = new Label { name = "vn-speaker" };
            _speaker.style.color = _theme.SpeakerColor;
            _speaker.style.fontSize = _theme.SpeakerFontSize;
            _speaker.style.unityFontStyleAndWeight = FontStyle.Bold;
            if (_theme.Font != null) _speaker.style.unityFont = new StyleFont(_theme.Font);
            _plate.Add(_speaker);
            Add(_plate);

            // Body panel.
            _panel = new VisualElement { name = "vn-panel" };
            _panel.style.backgroundColor = _theme.PanelColor;
            _panel.style.paddingLeft = 22;
            _panel.style.paddingRight = 22;
            _panel.style.paddingTop = 18;
            _panel.style.paddingBottom = 18;
            _panel.style.minHeight = 128;
            SetCorner(_panel, _theme.PanelCornerRadius, top: true, bottom: true);
            _body = new Label { name = "vn-body" };
            _body.style.color = _theme.TextColor;
            _body.style.fontSize = _theme.BodyFontSize;
            _body.style.whiteSpace = WhiteSpace.Normal;
            if (_theme.Font != null) _body.style.unityFont = new StyleFont(_theme.Font);
            _panel.Add(_body);
            Add(_panel);

            pickingMode = PickingMode.Ignore; // the host root owns tap-to-advance
        }

        /// <summary>Set the speaker name; empty/null hides the nameplate.</summary>
        public void SetSpeaker(string who)
        {
            bool show = !string.IsNullOrEmpty(who);
            _plate.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            _speaker.text = show ? who : "";
        }

        /// <summary>
        /// Begin revealing <paramref name="text"/> with the typewriter. Optional
        /// <paramref name="cps"/> overrides the theme speed for this line.
        /// </summary>
        public void Reveal(string text, float? cps = null)
        {
            _tw.SetText(text ?? "");
            _cps = cps.HasValue && cps.Value > TypewriterClock.MinCps ? cps.Value : _theme.CharsPerSecond;
            _startTime = Time.realtimeSinceStartup;
            _body.text = "";
            _tick?.Pause();

            IsRevealing = _tw.VisibleCount > 0;
            if (IsRevealing) _tick = schedule.Execute(Tick).Every(16);
            else _body.text = _tw.Full();
        }

        /// <summary>Snap to the full line immediately (e.g. on the first tap).</summary>
        public void Complete()
        {
            _tick?.Pause();
            _body.text = _tw.Full();
            IsRevealing = false;
        }

        /// <summary>Show a complete line with no reveal (resume / backlog).</summary>
        public void SetText(string text)
        {
            _tick?.Pause();
            _tw.SetText(text ?? "");
            _body.text = _tw.Full();
            IsRevealing = false;
        }

        /// <summary>
        /// Apply a text style preset before <see cref="Reveal"/>: "thought"
        /// (italic), "shout" (bold, larger), "narration" (centered, no panel),
        /// "whisper" (italic, faint panel). Unknown styles reset to default.
        /// </summary>
        public void ApplyStyle(string style)
        {
            _body.style.unityFontStyleAndWeight = FontStyle.Normal;
            _body.style.fontSize = _theme.BodyFontSize;
            _body.style.unityTextAlign = TextAnchor.UpperLeft;
            _panel.style.opacity = 1f;

            switch (style)
            {
                case "thought":
                    _body.style.unityFontStyleAndWeight = FontStyle.Italic;
                    break;
                case "shout":
                    _body.style.unityFontStyleAndWeight = FontStyle.Bold;
                    _body.style.fontSize = Mathf.RoundToInt(_theme.BodyFontSize * 1.2f);
                    break;
                case "narration":
                    _body.style.fontSize = Mathf.RoundToInt(_theme.BodyFontSize * 1.15f);
                    _body.style.unityTextAlign = TextAnchor.MiddleCenter;
                    _panel.style.opacity = 0f;
                    break;
                case "whisper":
                    _body.style.unityFontStyleAndWeight = FontStyle.Italic;
                    _panel.style.opacity = 0.5f;
                    break;
            }
        }

        private void Tick()
        {
            if (!IsRevealing) { _tick?.Pause(); return; }
            float elapsed = Time.realtimeSinceStartup - _startTime;
            float p = TypewriterClock.Progress(elapsed, _cps);
            if (p >= TypewriterClock.DoneAt(_tw.VisibleCount, _theme.FadeWidth))
            {
                Complete();
                return;
            }
            _body.text = _tw.SliceFaded(p, _theme.FadeWidth);
        }

        private static void SetCorner(VisualElement el, float r, bool top, bool bottom)
        {
            if (top)
            {
                el.style.borderTopLeftRadius = r;
                el.style.borderTopRightRadius = r;
            }
            if (bottom)
            {
                el.style.borderBottomLeftRadius = r;
                el.style.borderBottomRightRadius = r;
            }
        }
    }
}
