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
            style.justifyContent = Justify.Center;
            style.alignItems = Align.Center;
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
            var btn = new Button(() => OnSelected?.Invoke(index)) { text = string.Empty };
            btn.style.backgroundColor = _theme.ChoiceColor;
            btn.style.minWidth = Length.Percent(58f);
            btn.style.maxWidth = Length.Percent(86f);
            btn.style.marginBottom = 10;
            btn.style.paddingTop = 12;
            btn.style.paddingBottom = 12;
            btn.style.paddingLeft = 20;
            btn.style.paddingRight = 20;
            btn.style.borderTopLeftRadius = 10;
            btn.style.borderTopRightRadius = 10;
            btn.style.borderBottomLeftRadius = 10;
            btn.style.borderBottomRightRadius = 10;
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

            btn.RegisterCallback<MouseEnterEvent>(_ => btn.style.backgroundColor = _theme.ChoiceHoverColor);
            btn.RegisterCallback<MouseLeaveEvent>(_ => btn.style.backgroundColor = _theme.ChoiceColor);
            return btn;
        }
    }
}
