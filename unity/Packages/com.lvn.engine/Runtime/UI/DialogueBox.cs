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
        private readonly VisualElement _box;
        private readonly VisualElement _plate;
        private readonly VisualElement _panel;
        private readonly Label _speaker;
        private readonly Label _body;
        private readonly RichTextTypewriter _tw = new RichTextTypewriter();

        private IVisualElementScheduledItem _tick;
        private float _startTime;
        private float _cps;

        private Label _advanceHint;
        private IVisualElementScheduledItem _hintPulse;
        private bool _hintSuppressed;

        /// <summary>Hide the ▼ "tap to continue" marker while a choice is up (a
        /// tap shouldn't be invited when the player must pick).</summary>
        public void SuppressAdvanceHint(bool suppressed)
        {
            _hintSuppressed = suppressed;
            RefreshAdvanceHint();
        }

        private void RefreshAdvanceHint()
        {
            bool show = !_hintSuppressed && !IsRevealing && _tw.VisibleCount > 0;
            _advanceHint.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (show)
            {
                if (_hintPulse == null)
                {
                    bool dim = false;
                    _hintPulse = schedule.Execute(() =>
                    {
                        dim = !dim;
                        _advanceHint.style.opacity = dim ? 0.35f : 0.95f;
                    }).Every(600);
                }
                _hintPulse.Resume();
            }
            else _hintPulse?.Pause();
        }

        /// <summary>True while the typewriter is still revealing the line.</summary>
        public bool IsRevealing { get; private set; }

        public DialogueBox(VnTheme theme)
        {
            _theme = theme ?? new VnTheme();

            var align = string.IsNullOrEmpty(_theme.BoxAlign) ? "stretch" : _theme.BoxAlign;
            // The box is a universal popup. Three placement modes, decided by theme:
            //  • free  — any x/y given: positioned absolutely anywhere on screen, the
            //            given anchor point of the box landing on (x,y). Full control.
            //  • NVL   — a tall full-width reading surface from a top inset.
            //  • dock  — bottom-docked; BoxAlign sets the horizontal placement
            //            (stretch bar / centre / left / right), hugging the text.
            bool free = !_theme.Nvl && (_theme.BoxXPercent >= 0f || _theme.BoxYPercent >= 0f);
            bool stretch = _theme.Nvl || (!free && align == "stretch");

            // The root is a full-screen, click-through canvas; the box lives inside it.
            style.position = Position.Absolute;

            _box = new VisualElement { name = "vn-box" };
            _box.style.flexDirection = FlexDirection.Column;

            if (free)
            {
                style.left = 0; style.right = 0; style.top = 0; style.bottom = 0;
                _box.style.position = Position.Absolute;
                _box.style.left = Length.Percent(Mathf.Clamp(_theme.BoxXPercent >= 0f ? _theme.BoxXPercent : 50f, 0f, 100f));
                _box.style.top = Length.Percent(Mathf.Clamp(_theme.BoxYPercent >= 0f ? _theme.BoxYPercent : 50f, 0f, 100f));
                var (tx, ty) = AnchorTranslate(_theme.BoxAnchor);
                _box.style.translate = new Translate(Length.Percent(tx), Length.Percent(ty));
            }
            else if (_theme.Nvl)
            {
                // NVL: stretch from a top inset to the bottom as a tall reading surface.
                style.left = 0; style.right = 0; style.bottom = 0;
                style.top = Length.Percent(Mathf.Clamp01(_theme.NvlTop) * 100f);
                style.paddingLeft = _theme.EdgePadding;
                style.paddingRight = _theme.EdgePadding;
                style.paddingTop = _theme.EdgePadding;
                style.paddingBottom = _theme.BottomPadding;
                _box.style.flexGrow = 1;
            }
            else
            {
                style.left = 0; style.right = 0; style.bottom = 0;
                style.paddingLeft = _theme.EdgePadding;
                style.paddingRight = _theme.EdgePadding;
                style.paddingBottom = _theme.BottomPadding;
                // alignItems places the box across the screen width.
                style.alignItems = stretch ? Align.Stretch
                    : align == "center" ? Align.Center
                    : align == "right" ? Align.FlexEnd
                    : Align.FlexStart;
                if (stretch) _box.style.flexGrow = 1;
            }

            // Box width/height (skipped for stretch & NVL, which fill their region):
            // The box has a FIXED width (it does NOT shrink to the text) — width =
            // BoxWidthPercent, else BoxMaxWidthPercent, else a sensible default. The
            // HEIGHT grows with the text (flex content height over PanelMinHeight),
            // so a long line makes the box taller, not wider. BoxMaxHeightPercent caps
            // that growth (the body clamps/scrolls beyond it).
            if (!stretch && !_theme.Nvl)
            {
                float w = _theme.BoxWidthPercent > 0f ? _theme.BoxWidthPercent
                        : _theme.BoxMaxWidthPercent > 0f ? _theme.BoxMaxWidthPercent
                        : 80f;
                _box.style.width = Length.Percent(Mathf.Clamp(w, 5f, 100f));
                if (_theme.BoxMaxHeightPercent > 0f)
                    _box.style.maxHeight = Length.Percent(Mathf.Clamp(_theme.BoxMaxHeightPercent, 5f, 100f));
            }
            Add(_box);

            // Nameplate (hidden for narration).
            _plate = new VisualElement { name = "vn-plate" };
            _plate.style.alignSelf = Align.FlexStart;
            _plate.style.backgroundColor = _theme.PanelColor;
            _plate.style.paddingLeft = _theme.NamePaddingX;
            _plate.style.paddingRight = _theme.NamePaddingX;
            _plate.style.paddingTop = _theme.NamePaddingY;
            _plate.style.paddingBottom = _theme.NamePaddingY;
            _plate.style.marginBottom = -2;
            SetCorner(_plate, _theme.PanelCornerRadius * 0.6f, top: true, bottom: false);
            UiStyle.ApplyBackground(_plate, _theme.PlateSprite, _theme.PanelSlice);
            _speaker = new Label { name = "vn-speaker" };
            _speaker.style.color = _theme.SpeakerColor;
            _speaker.style.fontSize = _theme.SpeakerFontSize;
            _speaker.style.unityFontStyleAndWeight = FontStyle.Bold;
            if (_theme.Font != null) _speaker.style.unityFont = new StyleFont(_theme.Font);
            _plate.Add(_speaker);
            _box.Add(_plate);

            // Body panel.
            _panel = new VisualElement { name = "vn-panel" };
            _panel.style.backgroundColor = _theme.PanelColor;
            _panel.style.paddingLeft = _theme.PanelPaddingX;
            _panel.style.paddingRight = _theme.PanelPaddingX;
            _panel.style.paddingTop = _theme.PanelPaddingY;
            _panel.style.paddingBottom = _theme.PanelPaddingY;
            _panel.style.minHeight = _theme.PanelMinHeight;
            if (_theme.Nvl) _panel.style.flexGrow = 1; // fill the tall NVL region
            SetCorner(_panel, _theme.PanelCornerRadius, top: true, bottom: true);
            UiStyle.ApplyBackground(_panel, _theme.PanelSprite, _theme.PanelSlice);
            _body = new Label { name = "vn-body" };
            _body.style.color = _theme.TextColor;
            _body.style.fontSize = _theme.BodyFontSize;
            _body.style.whiteSpace = WhiteSpace.Normal;
            if (_theme.Font != null) _body.style.unityFont = new StyleFont(_theme.Font);
            _panel.Add(_body);

            // The genre's "line finished — tap" marker: a small pulsing ▼ in the
            // panel's bottom-right corner. Shown when the reveal is done (and no
            // choice is up — the host suppresses it then).
            _advanceHint = new Label("▼") { name = "vn-advance-hint", pickingMode = PickingMode.Ignore };
            _advanceHint.style.position = Position.Absolute;
            _advanceHint.style.right = 10;
            _advanceHint.style.bottom = 4;
            _advanceHint.style.fontSize = Mathf.RoundToInt(_theme.BodyFontSize * 0.55f);
            _advanceHint.style.color = _theme.SpeakerColor;
            _advanceHint.style.display = DisplayStyle.None;
            if (_theme.Font != null) _advanceHint.style.unityFont = new StyleFont(_theme.Font);
            _panel.Add(_advanceHint);

            _box.Add(_panel);

            pickingMode = PickingMode.Ignore; // the host root owns tap-to-advance
        }

        /// <summary>Translate fractions for an anchor keyword so the box's (x,y)
        /// positions <em>that</em> point of the box: <c>center</c> → -50%,
        /// <c>right</c>/<c>bottom</c> → -100%, <c>left</c>/<c>top</c> → 0. Accepts
        /// combos like <c>"bottom-center"</c>, <c>"top-left"</c>, <c>"center"</c>.</summary>
        private static (float tx, float ty) AnchorTranslate(string anchor)
        {
            string a = string.IsNullOrEmpty(anchor) ? "center" : anchor.ToLowerInvariant();
            float tx = a.Contains("left") ? 0f : a.Contains("right") ? -100f : -50f;
            float ty = a.Contains("top") ? 0f : a.Contains("bottom") ? -100f : -50f;
            return (tx, ty);
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
            _lastQuantum = -1;
            _tick?.Pause();

            IsRevealing = _tw.VisibleCount > 0;
            RefreshAdvanceHint(); // hidden while revealing
            if (IsRevealing)
            {
                // Fixed layout from frame 0: the whole line is present (hidden),
                // so word-wrap and box height never shift during the reveal.
                _body.text = _tw.SliceFadedFixed(0f, _theme.FadeWidth);
                _tick = schedule.Execute(Tick).Every(16);
            }
            else _body.text = _tw.Full();
        }

        /// <summary>Snap to the full line immediately (e.g. on the first tap).</summary>
        public void Complete()
        {
            _tick?.Pause();
            _body.text = _tw.Full();
            IsRevealing = false;
            RefreshAdvanceHint();
        }

        /// <summary>Show a complete line with no reveal (resume / backlog).</summary>
        public void SetText(string text)
        {
            _tick?.Pause();
            _tw.SetText(text ?? "");
            _body.text = _tw.Full();
            IsRevealing = false;
            RefreshAdvanceHint();
        }

        // The player's window-opacity preference and the current style's own panel
        // scale compose multiplicatively onto the PANEL BACKGROUND only — element
        // opacity would dim the text with it (and "narration"'s old opacity=0
        // silently hid the line, since the body label is a child of the panel).
        private float _userOpacity = 1f;
        private float _styleBgScale = 1f;

        /// <summary>Scale the dialogue window's background opacity (0.2–1) — the
        /// player's comfort setting. Text stays fully opaque.</summary>
        public void SetUserOpacity(float value)
        {
            _userOpacity = Mathf.Clamp(value, 0.2f, 1f);
            ApplyPanelBackground();
        }

        private void ApplyPanelBackground()
        {
            float a = _styleBgScale * _userOpacity;
            var c = _theme.PanelColor;
            c.a *= a;
            _panel.style.backgroundColor = _theme.PanelSprite != null ? Color.clear : c;
            // Sprite-skinned panels dim via the image tint instead.
            if (_theme.PanelSprite != null)
                _panel.style.unityBackgroundImageTintColor = new Color(1f, 1f, 1f, a);
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
            _styleBgScale = 1f;

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
                    _styleBgScale = 0f; // no panel behind pure narration — text stays visible
                    break;
                case "whisper":
                    _body.style.unityFontStyleAndWeight = FontStyle.Italic;
                    _styleBgScale = 0.5f;
                    break;
            }
            ApplyPanelBackground();
        }

        // Progress quantum of the last rebuild — rebuilding the rich-text string
        // (and regenerating the text mesh) every 16ms tick is pure GC churn when
        // the reveal head barely moved. Eighth-glyph steps look identical.
        private int _lastQuantum = -1;

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
            int q = (int)(p * 8f);
            if (q == _lastQuantum) return; // head hasn't visibly moved — skip the rebuild
            _lastQuantum = q;
            _body.text = _tw.SliceFadedFixed(p, _theme.FadeWidth);
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
