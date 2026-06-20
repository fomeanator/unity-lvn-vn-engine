using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// A scrollable dialogue history panel. Wire to a UIDocument and assign
    /// a <see cref="VnStage"/> to display the backlog of spoken lines.
    /// </summary>
    public class BacklogPanel : MonoBehaviour
    {
        [Tooltip("The stage to read backlog from.")]
        public VnStage Stage;

        [Tooltip("Root USS name for styling.")]
        public string RootStyleName = "backlog-panel";

        private UIDocument _doc;
        private bool _isOpen;

        private void Awake()
        {
            _doc = GetComponent<UIDocument>();
        }

        private void OnEnable()
        {
            var root = _doc.rootVisualElement;
            if (root == null) return;
            root.Clear();
            root.Add(BuildUI());
        }

        private VisualElement BuildUI()
        {
            var panel = new VisualElement { name = RootStyleName };
            panel.style.position = Position.Absolute;
            panel.style.left = Length.Percent(5);
            panel.style.right = Length.Percent(5);
            panel.style.top = Length.Percent(5);
            panel.style.bottom = Length.Percent(5);
            panel.style.backgroundColor = new Color(0, 0, 0, 0.9f);
            panel.style.display = DisplayStyle.None;
            panel.style.borderTopLeftRadius = 12;
            panel.style.borderTopRightRadius = 12;
            panel.style.borderBottomLeftRadius = 12;
            panel.style.borderBottomRightRadius = 12;

            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.justifyContent = Justify.SpaceBetween;
            header.style.paddingTop = 10;
            header.style.paddingBottom = 10;
            header.style.paddingLeft = 15;
            header.style.paddingRight = 15;

            var title = new Label("Backlog");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 18;
            title.style.color = Color.white;
            header.Add(title);

            var closeBtn = new Button(() => Close()) { text = "X" };
            closeBtn.style.width = 30;
            closeBtn.style.height = 30;
            closeBtn.style.fontSize = 14;
            header.Add(closeBtn);

            panel.Add(header);

            var scroll = new ScrollView();
            scroll.name = "backlog-scroll";
            scroll.style.flexGrow = 1;
            panel.Add(scroll);

            return panel;
        }

        public void Open()
        {
            if (Stage == null) return;
            _isOpen = true;
            var root = _doc.rootVisualElement;
            var panel = root.Q(RootStyleName);
            panel.style.display = DisplayStyle.Flex;

            var scroll = panel.Q<ScrollView>("backlog-scroll");
            scroll.Clear();

            var backlog = Stage.Backlog;
            for (int i = 0; i < backlog.Count; i++)
            {
                var (who, text, style) = backlog[i];
                var entry = BuildEntry(who, text, style);
                scroll.Add(entry);
            }

            scroll.scrollOffset = new Vector2(0, scroll.contentContainer.worldBound.height);
        }

        public void Close()
        {
            _isOpen = false;
            var root = _doc.rootVisualElement;
            root.Q(RootStyleName).style.display = DisplayStyle.None;
        }

        public bool IsOpen => _isOpen;

        private VisualElement BuildEntry(string who, string text, string style)
        {
            var entry = new VisualElement();
            entry.style.paddingTop = 8;
            entry.style.paddingBottom = 8;
            entry.style.paddingLeft = 15;
            entry.style.paddingRight = 15;
            entry.style.borderBottomWidth = 1;
            entry.style.borderBottomColor = new Color(1, 1, 1, 0.1f);

            if (!string.IsNullOrEmpty(who))
            {
                var name = new Label(who);
                name.style.unityFontStyleAndWeight = FontStyle.Bold;
                name.style.fontSize = 13;
                name.style.color = new Color(0.7f, 0.85f, 1f);
                name.style.marginBottom = 2;
                entry.Add(name);
            }

            var body = new Label(text ?? "");
            body.style.fontSize = 14;
            body.style.color = Color.white;
            body.enableRichText = true;
            body.style.whiteSpace = WhiteSpace.Normal;
            entry.Add(body);

            return entry;
        }
    }
}
