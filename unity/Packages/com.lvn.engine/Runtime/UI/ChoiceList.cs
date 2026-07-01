using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// The choice layer (z-order 4): a centered stack of option buttons, each a
    /// caption with an optional narrative-cost line beneath. Raises
    /// <see cref="OnSelected"/> with the picked <see cref="LvnOption.Index"/>.
    /// Options gated out by the player never reach here.
    /// </summary>
    public sealed class ChoiceList : VisualElement
    {
        private readonly VnTheme _theme;

        /// <summary>Fires with the chosen option's <see cref="LvnOption.Index"/>.</summary>
        public event Action<int> OnSelected;

        public ChoiceList(VnTheme theme)
        {
            _theme = theme ?? new VnTheme();
            style.position = Position.Absolute;
            style.left = 0;
            style.right = 0;
            style.top = 0;
            style.bottom = 0;
            style.paddingLeft = _theme.EdgePadding;
            style.paddingRight = _theme.EdgePadding;
            style.paddingBottom = _theme.BottomPadding;

            // Horizontal placement of the button stack across the screen.
            string al = string.IsNullOrEmpty(_theme.ChoiceAlign) ? "center" : _theme.ChoiceAlign;
            style.alignItems = al == "left" ? Align.FlexStart
                : al == "right" ? Align.FlexEnd
                : Align.Center;

            // Vertical placement: a free ChoiceYPercent puts the top of the stack at
            // that screen % (e.g. 70 = lower third); otherwise ChoiceVAlign docks it
            // top / centre / bottom.
            if (_theme.ChoiceYPercent >= 0f)
            {
                style.justifyContent = Justify.FlexStart;
                style.paddingTop = Length.Percent(Mathf.Clamp(_theme.ChoiceYPercent, 0f, 100f));
            }
            else
            {
                string v = string.IsNullOrEmpty(_theme.ChoiceVAlign) ? "center" : _theme.ChoiceVAlign;
                style.justifyContent = v == "top" ? Justify.FlexStart
                    : v == "bottom" ? Justify.FlexEnd
                    : Justify.Center;
            }

            pickingMode = PickingMode.Ignore; // only the buttons are interactive
            style.display = DisplayStyle.None;
        }

        /// <summary>Show the options. Replaces any currently shown.</summary>
        public void Present(IReadOnlyList<LvnOption> options)
        {
            Clear();
            if (options != null)
            {
                foreach (var o in options)
                    Add(BuildOption(o));
            }
            style.display = DisplayStyle.Flex;
        }

        /// <summary>Hide and clear the options.</summary>
        public void Dismiss()
        {
            Clear();
            style.display = DisplayStyle.None;
        }

        private VisualElement BuildOption(LvnOption option)
        {
            int index = option.Index;
            // A locked option (failed skill check / unaffordable) is shown greyed
            // and non-interactive so the player sees what they can't do yet.
            var btn = new Button(() => { if (option.Enabled) OnSelected?.Invoke(index); }) { text = string.Empty };
            btn.SetEnabled(option.Enabled);
            if (!option.Enabled) btn.style.opacity = 0.5f;
            btn.style.backgroundColor = _theme.ChoiceColor;
            btn.style.minWidth = Length.Percent(_theme.ChoiceMinWidthPercent);
            btn.style.maxWidth = Length.Percent(_theme.ChoiceMaxWidthPercent);
            btn.style.marginBottom = _theme.ChoiceSpacing;
            btn.style.paddingTop = _theme.ChoicePaddingY;
            btn.style.paddingBottom = _theme.ChoicePaddingY;
            btn.style.paddingLeft = _theme.ChoicePaddingX;
            btn.style.paddingRight = _theme.ChoicePaddingX;
            btn.style.borderTopLeftRadius = _theme.ChoiceCornerRadius;
            btn.style.borderTopRightRadius = _theme.ChoiceCornerRadius;
            btn.style.borderBottomLeftRadius = _theme.ChoiceCornerRadius;
            btn.style.borderBottomRightRadius = _theme.ChoiceCornerRadius;
            btn.style.flexDirection = FlexDirection.Column;
            btn.style.alignItems = Align.Center;

            var caption = new Label(option.Text ?? string.Empty);
            caption.style.color = _theme.ChoiceTextColor;
            caption.style.fontSize = _theme.ChoiceFontSize;
            caption.style.whiteSpace = WhiteSpace.Normal;
            caption.style.unityTextAlign = TextAnchor.MiddleCenter;
            if (_theme.Font != null) caption.style.unityFont = new StyleFont(_theme.Font);
            btn.Add(caption);

            if (!string.IsNullOrEmpty(option.Cost))
            {
                var cost = new Label(option.Cost);
                cost.style.color = _theme.ChoiceCostColor;
                cost.style.fontSize = Mathf.RoundToInt(_theme.ChoiceFontSize * 0.72f);
                cost.style.marginTop = 4;
                btn.Add(cost);
            }

            // The lock reason (skill requirement / price) sits beneath a greyed option.
            if (!option.Enabled && !string.IsNullOrEmpty(option.Note))
            {
                var note = new Label(option.Note);
                note.style.color = _theme.ChoiceCostColor;
                note.style.fontSize = Mathf.RoundToInt(_theme.ChoiceFontSize * 0.66f);
                note.style.marginTop = 2;
                note.style.unityFontStyleAndWeight = FontStyle.Italic;
                btn.Add(note);
            }

            if (_theme.ChoiceSprite != null)
            {
                UiStyle.ApplyBackground(btn, _theme.ChoiceSprite, _theme.ChoiceSlice);
                var hover = _theme.ChoiceHoverSprite != null ? _theme.ChoiceHoverSprite : _theme.ChoiceSprite;
                btn.RegisterCallback<MouseEnterEvent>(_ => btn.style.backgroundImage = new StyleBackground(hover));
                btn.RegisterCallback<MouseLeaveEvent>(_ => btn.style.backgroundImage = new StyleBackground(_theme.ChoiceSprite));
            }
            else
            {
                btn.RegisterCallback<MouseEnterEvent>(_ => btn.style.backgroundColor = _theme.ChoiceHoverColor);
                btn.RegisterCallback<MouseLeaveEvent>(_ => btn.style.backgroundColor = _theme.ChoiceColor);
            }
            return btn;
        }
    }
}
