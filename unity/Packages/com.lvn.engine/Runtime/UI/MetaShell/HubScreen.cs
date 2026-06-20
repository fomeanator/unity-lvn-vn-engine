using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.MetaShell
{
    /// <summary>
    /// A hub screen with chapter/section buttons. Shows progress, unlocks,
    /// and navigation to gameplay scenes.
    /// </summary>
    public class HubScreen : MonoBehaviour
    {
        [Tooltip("The stage to play scenes from.")]
        public VnStage Stage;

        [Tooltip("Root USS name for styling.")]
        public string RootStyleName = "hub-screen";

        private UIDocument _doc;
        private VisualElement _root;
        private List<ChapterEntry> _chapters = new List<ChapterEntry>();
        private Action<string> _onSelect;

        public void Init(List<ChapterEntry> chapters, Action<string> onSelect)
        {
            _chapters = chapters;
            _onSelect = onSelect;
        }

        private void Awake()
        {
            _doc = GetComponent<UIDocument>();
        }

        private void OnEnable()
        {
            _root = _doc.rootVisualElement;
            if (_root == null) return;
            _root.Clear();
            _root.Add(BuildUI());
        }

        private VisualElement BuildUI()
        {
            var panel = new VisualElement { name = RootStyleName };
            panel.style.position = Position.Absolute;
            panel.style.left = 0;
            panel.style.right = 0;
            panel.style.top = 0;
            panel.style.bottom = 0;
            panel.style.backgroundColor = new Color(0.05f, 0.05f, 0.1f);
            panel.style.alignItems = Align.Center;
            panel.style.justifyContent = Justify.Center;

            var title = new Label("Chapter Select");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 24;
            title.style.color = Color.white;
            title.style.marginBottom = 30;
            panel.Add(title);

            var grid = new VisualElement();
            grid.style.flexDirection = FlexDirection.Row;
            grid.style.flexWrap = Wrap.Wrap;
            grid.style.justifyContent = Justify.Center;
            grid.style.maxWidth = Length.Percent(80);

            foreach (var ch in _chapters)
            {
                var card = BuildChapterCard(ch);
                grid.Add(card);
            }
            panel.Add(grid);

            return panel;
        }

        private VisualElement BuildChapterCard(ChapterEntry ch)
        {
            var card = new VisualElement();
            card.style.width = 200;
            card.style.height = 120;
            card.style.marginTop = 8;
            card.style.marginBottom = 8;
            card.style.marginLeft = 8;
            card.style.marginRight = 8;
            card.style.paddingTop = 15;
            card.style.paddingBottom = 15;
            card.style.paddingLeft = 15;
            card.style.paddingRight = 15;
            card.style.borderTopLeftRadius = 10;
            card.style.borderTopRightRadius = 10;
            card.style.borderBottomLeftRadius = 10;
            card.style.borderBottomRightRadius = 10;
            card.style.alignItems = Align.Center;
            card.style.justifyContent = Justify.Center;

            if (ch.Locked)
            {
                card.style.backgroundColor = new Color(0.2f, 0.2f, 0.2f);
                card.style.opacity = 0.6f;
            }
            else if (ch.Completed)
            {
                card.style.backgroundColor = new Color(0.1f, 0.3f, 0.1f);
            }
            else
            {
                card.style.backgroundColor = new Color(0.15f, 0.2f, 0.35f);
            }

            var label = new Label(ch.Title);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.fontSize = 16;
            label.style.color = Color.white;
            label.style.marginBottom = 5;
            card.Add(label);

            if (ch.Locked)
            {
                var lockLabel = new Label("Locked");
                lockLabel.style.fontSize = 12;
                lockLabel.style.color = Color.gray;
                card.Add(lockLabel);
            }
            else if (ch.Completed)
            {
                var checkLabel = new Label("Completed");
                checkLabel.style.fontSize = 12;
                checkLabel.style.color = new Color(0.5f, 1f, 0.5f);
                card.Add(checkLabel);
            }

            if (!ch.Locked)
            {
                card.RegisterCallback<ClickEvent>(_ => _onSelect?.Invoke(ch.ScriptUrl));
                card.pickingMode = PickingMode.Position;
            }

            return card;
        }

        public void Open()
        {
            _root.style.display = DisplayStyle.Flex;
        }

        public void Close()
        {
            _root.style.display = DisplayStyle.None;
        }
    }

    [Serializable]
    public class ChapterEntry
    {
        public string Title;
        public string ScriptUrl;
        public bool Locked;
        public bool Completed;
    }
}
