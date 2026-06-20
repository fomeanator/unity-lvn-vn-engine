using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// The in-game top HUD, themed from a <see cref="HudConfig"/> (manifest
    /// <c>ui.hud</c>): a thin strip with chapter progress on the left (optional
    /// icon + percent) and a row of currency pills (icon + amount) on the right.
    /// Pills are created on demand. <see cref="SetProgress"/> uses the shared
    /// <see cref="Percent"/> rule so every "%" in the UI matches the loading bar.
    /// </summary>
    public sealed class GameHud : VisualElement
    {
        private readonly HudConfig _cfg;
        private readonly ILvnAssets _assets;
        private readonly VisualElement _progressIcon;
        private readonly Label _progressLabel;
        private readonly VisualElement _pillsRow;
        private readonly Color _pillBg;
        private readonly Color _pillText;

        private sealed class Pill { public VisualElement Root; public Label Label; }
        private readonly Dictionary<string, Pill> _pills = new Dictionary<string, Pill>();

        public GameHud(HudConfig cfg, ILvnAssets assets)
        {
            _cfg = cfg ?? new HudConfig();
            _assets = assets;
            _pillBg = UiColor.Parse(_cfg.pill_bg_color, new Color(0f, 0f, 0f, 0.4f));
            _pillText = UiColor.Parse(_cfg.pill_text_color, new Color(0.96f, 0.93f, 0.85f));

            float h = _cfg.height ?? 0.07f;
            style.position = Position.Absolute;
            style.left = 0; style.right = 0; style.top = 0;
            style.height = Length.Percent(h * 100f);
            style.flexDirection = FlexDirection.Row;
            style.alignItems = Align.Center;
            style.justifyContent = Justify.SpaceBetween;
            style.paddingLeft = 24; style.paddingRight = 24;
            style.backgroundColor = UiColor.Parse(_cfg.bg_color, new Color(0f, 0f, 0f, 0.53f));
            pickingMode = PickingMode.Ignore;

            // left: progress
            var left = new VisualElement { pickingMode = PickingMode.Ignore };
            left.style.flexDirection = FlexDirection.Row;
            left.style.alignItems = Align.Center;
            left.style.display = (_cfg.show_progress ?? true) ? DisplayStyle.Flex : DisplayStyle.None;
            Add(left);

            _progressIcon = new VisualElement { pickingMode = PickingMode.Ignore };
            _progressIcon.style.width = 28; _progressIcon.style.height = 28;
            _progressIcon.style.marginRight = 8;
            _progressIcon.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
            _progressIcon.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
            _progressIcon.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
            _progressIcon.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
            _progressIcon.style.display = string.IsNullOrEmpty(_cfg.progress_icon_url) ? DisplayStyle.None : DisplayStyle.Flex;
            left.Add(_progressIcon);

            _progressLabel = new Label("0%") { pickingMode = PickingMode.Ignore };
            _progressLabel.style.color = UiColor.Parse(_cfg.progress_color, new Color(0.96f, 0.93f, 0.85f));
            _progressLabel.style.fontSize = 22;
            left.Add(_progressLabel);

            // right: currency pills
            _pillsRow = new VisualElement { pickingMode = PickingMode.Ignore };
            _pillsRow.style.flexDirection = FlexDirection.Row;
            _pillsRow.style.alignItems = Align.Center;
            Add(_pillsRow);

            _ = AssignBg(_progressIcon, _cfg.progress_icon_url);
        }

        /// <summary>Update the chapter-progress percent (current command / total).</summary>
        public void SetProgress(int currentIndex, int totalCommands)
        {
            if (_progressLabel != null) _progressLabel.text = Percent.Text(currentIndex, totalCommands);
        }

        public void SetBalances(IDictionary<string, long> balances)
        {
            if (balances == null) return;
            foreach (var kv in balances) SetBalance(kv.Key, kv.Value);
        }

        /// <summary>Set (creating if needed) a currency pill's amount. <paramref
        /// name="iconUrl"/> overrides the default icon for this currency.</summary>
        public void SetBalance(string currency, long amount, string iconUrl = null)
        {
            if (string.IsNullOrEmpty(currency) || _pillsRow == null) return;
            if (!_pills.TryGetValue(currency, out var p))
            {
                p = SpawnPill(iconUrl ?? _cfg.default_currency_icon_url);
                _pills[currency] = p;
            }
            p.Label.text = amount.ToString("N0");
        }

        private Pill SpawnPill(string iconUrl)
        {
            var pill = new VisualElement { pickingMode = PickingMode.Ignore };
            pill.style.flexDirection = FlexDirection.Row;
            pill.style.alignItems = Align.Center;
            pill.style.marginLeft = 10;
            pill.style.paddingLeft = 12; pill.style.paddingRight = 12;
            pill.style.paddingTop = 5; pill.style.paddingBottom = 5;
            pill.style.backgroundColor = _pillBg;
            pill.style.borderTopLeftRadius = 14; pill.style.borderTopRightRadius = 14;
            pill.style.borderBottomLeftRadius = 14; pill.style.borderBottomRightRadius = 14;

            if (!string.IsNullOrEmpty(iconUrl))
            {
                var icon = new VisualElement { pickingMode = PickingMode.Ignore };
                icon.style.width = 22; icon.style.height = 22; icon.style.marginRight = 6;
                icon.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
                icon.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
                icon.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
                icon.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
                pill.Add(icon);
                _ = AssignBg(icon, iconUrl);
            }

            var label = new Label("0") { pickingMode = PickingMode.Ignore };
            label.style.color = _pillText;
            label.style.fontSize = 20;
            pill.Add(label);

            _pillsRow.Add(pill);
            return new Pill { Root = pill, Label = label };
        }

        private async Task AssignBg(VisualElement el, string url)
        {
            if (el == null || string.IsNullOrEmpty(url) || _assets == null) return;
            try
            {
                var sprite = await _assets.LoadSpriteAsync(url, CancellationToken.None);
                if (sprite != null) el.style.backgroundImage = new StyleBackground(sprite);
            }
            catch { }
        }
    }
}
