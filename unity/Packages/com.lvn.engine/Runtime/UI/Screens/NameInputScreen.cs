using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// The character name-input screen, themed from a <see cref="NameInputConfig"/>
    /// (manifest <c>ui.name_input</c>): a full-screen backdrop, optional character
    /// art, a prompt, a text field and a confirm button. <see cref="AskAsync"/>
    /// fades it in, waits for a valid name (sanitised by the pure
    /// <see cref="PlayerNameInput"/> rules), and returns it. Tapping confirm or
    /// pressing Enter commits; an empty/whitespace value is rejected.
    /// </summary>
    public sealed class NameInputScreen : VisualElement
    {
        private readonly NameInputConfig _cfg;
        private readonly ILvnAssets _assets;
        private readonly VisualElement _bg;
        private readonly VisualElement _hero;
        private readonly Label _prompt;
        private readonly TextField _field;
        private readonly Button _confirm;
        private readonly int _maxLength;

        private TaskCompletionSource<string> _tcs;

        public NameInputScreen(NameInputConfig cfg, ILvnAssets assets)
        {
            _cfg = cfg ?? new NameInputConfig();
            _assets = assets;
            _maxLength = _cfg.max_length ?? PlayerNameInput.MaxLength;

            FullScreen(this);
            style.backgroundColor = UiColor.Parse(_cfg.bg_color, new Color(0.06f, 0.06f, 0.08f));
            style.opacity = 0f;
            style.display = DisplayStyle.None;

            _bg = FullScreen(new VisualElement());
            Add(_bg);

            _hero = new VisualElement();
            _hero.style.position = Position.Absolute;
            _hero.style.left = 0;
            _hero.style.right = 0;
            _hero.style.top = Length.Percent(8f);
            _hero.style.bottom = Length.Percent(28f);
            _hero.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
            _hero.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
            _hero.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
            _hero.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
            _hero.pickingMode = PickingMode.Ignore;
            Add(_hero);

            // ── bottom panel: prompt + (field | confirm) ──
            var panel = new VisualElement();
            panel.style.position = Position.Absolute;
            panel.style.left = Length.Percent(8f);
            panel.style.right = Length.Percent(8f);
            panel.style.bottom = Length.Percent(8f);
            panel.style.paddingTop = 24;
            panel.style.paddingBottom = 24;
            panel.style.paddingLeft = 24;
            panel.style.paddingRight = 24;
            panel.style.backgroundColor = new Color(0f, 0f, 0f, 0.55f);
            panel.style.borderTopLeftRadius = 14;
            panel.style.borderTopRightRadius = 14;
            panel.style.borderBottomLeftRadius = 14;
            panel.style.borderBottomRightRadius = 14;
            Add(panel);

            _prompt = new Label(_cfg.prompt ?? "Enter your name");
            _prompt.style.color = UiColor.Parse(_cfg.prompt_color, new Color(0.80f, 0.72f, 0.56f));
            _prompt.style.fontSize = 28;
            _prompt.style.marginBottom = 14;
            panel.Add(_prompt);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            panel.Add(row);

            _field = new TextField { maxLength = _maxLength };
            _field.style.flexGrow = 1;
            _field.style.fontSize = 30;
            _field.style.marginRight = 16;
            var fieldColor = UiColor.Parse(_cfg.field_color, new Color(0.11f, 0.11f, 0.13f));
            var textColor = UiColor.Parse(_cfg.text_color, new Color(0.96f, 0.93f, 0.85f));
            StyleField(_field, fieldColor, textColor);
            _field.value = _cfg.default_name ?? "";
            _field.RegisterCallback<KeyDownEvent>(OnKey);
            row.Add(_field);

            _confirm = new Button { text = _cfg.confirm_text ?? "Confirm" };
            _confirm.style.fontSize = 26;
            _confirm.style.paddingLeft = 28;
            _confirm.style.paddingRight = 28;
            _confirm.style.paddingTop = 12;
            _confirm.style.paddingBottom = 12;
            _confirm.style.color = textColor;
            _confirm.style.backgroundColor = UiColor.Parse(_cfg.button_color, new Color(0.23f, 0.23f, 0.27f));
            _confirm.clicked += TryConfirm;
            row.Add(_confirm);

            _ = AssignBg(_bg, _cfg.bg_url);
            _ = AssignBg(_hero, _cfg.hero_url);
            if (!string.IsNullOrEmpty(_cfg.field_url)) _ = AssignBg(_field, _cfg.field_url);
            if (!string.IsNullOrEmpty(_cfg.button_url)) _ = AssignBg(_confirm, _cfg.button_url);
        }

        /// <summary>Show the screen and resolve with the player's sanitised name
        /// once they confirm a non-empty value. Cancelling the token abandons the
        /// prompt (the task cancels).</summary>
        public async Task<string> AskAsync(CancellationToken ct = default)
        {
            style.display = DisplayStyle.Flex;
            await ScreenFx.FadeAsync(this, 0f, 1f, 0.3f, ct);

            _field.value = _cfg.default_name ?? "";
            _field.schedule.Execute(() => { _field.Focus(); _field.SelectAll(); }).ExecuteLater(16);

            _tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = ct.Register(() => _tcs.TrySetCanceled());

            string result;
            try { result = await _tcs.Task; }
            finally
            {
                await ScreenFx.FadeAsync(this, 1f, 0f, 0.3f, CancellationToken.None);
                style.display = DisplayStyle.None;
            }
            return result;
        }

        public void Hide()
        {
            style.opacity = 0f;
            style.display = DisplayStyle.None;
            _tcs?.TrySetCanceled();
        }

        private void OnKey(KeyDownEvent e)
        {
            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                TryConfirm();
        }

        private void TryConfirm()
        {
            var name = PlayerNameInput.Sanitize(_field?.value, _maxLength);
            if (string.IsNullOrEmpty(name)) return;
            _tcs?.TrySetResult(name);
        }

        private async Task AssignBg(VisualElement el, string url)
        {
            if (el == null || string.IsNullOrEmpty(url) || _assets == null) return;
            try
            {
                var sprite = await _assets.LoadSpriteAsync(url, CancellationToken.None);
                if (sprite != null) el.style.backgroundImage = new StyleBackground(sprite);
            }
            catch { /* missing art is non-fatal */ }
        }

        private static void StyleField(TextField f, Color bg, Color text)
        {
            f.style.color = text;
            var input = f.Q(TextField.textInputUssName);
            if (input != null)
            {
                input.style.backgroundColor = bg;
                input.style.color = text;
                input.style.paddingTop = 10;
                input.style.paddingBottom = 10;
                input.style.paddingLeft = 14;
                input.style.paddingRight = 14;
            }
        }

        private static VisualElement FullScreen(VisualElement el)
        {
            el.style.position = Position.Absolute;
            el.style.left = 0;
            el.style.right = 0;
            el.style.top = 0;
            el.style.bottom = 0;
            return el;
        }
    }
}
