using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.MetaShell
{
    /// <summary>
    /// A paywall gate that blocks access to premium content. Shows a prompt
    /// and optionally triggers an IAP flow.
    /// </summary>
    public class PaywallGate : MonoBehaviour
    {
        [Tooltip("Root USS name for styling.")]
        public string RootStyleName = "paywall-gate";

        private UIDocument _doc;
        private VisualElement _root;
        private Action _onPurchase;
        private Action _onDismiss;

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
            panel.style.backgroundColor = new Color(0, 0, 0, 0.85f);
            panel.style.alignItems = Align.Center;
            panel.style.justifyContent = Justify.Center;
            panel.style.display = DisplayStyle.None;

            var card = new VisualElement();
            card.style.width = 350;
            card.style.paddingTop = 30;
            card.style.paddingBottom = 30;
            card.style.paddingLeft = 30;
            card.style.paddingRight = 30;
            card.style.borderTopLeftRadius = 16;
            card.style.borderTopRightRadius = 16;
            card.style.borderBottomLeftRadius = 16;
            card.style.borderBottomRightRadius = 16;
            card.style.backgroundColor = new Color(0.12f, 0.12f, 0.18f);
            card.style.alignItems = Align.Center;

            var title = new Label("Premium Content");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 20;
            title.style.color = Color.white;
            title.style.marginBottom = 10;
            card.Add(title);

            var desc = new Label("Unlock this chapter to continue the story.");
            desc.style.fontSize = 14;
            desc.style.color = Color.gray;
            desc.style.marginBottom = 20;
            desc.style.whiteSpace = WhiteSpace.Normal;
            card.Add(desc);

            var buyBtn = new Button(() => _onPurchase?.Invoke()) { text = "Unlock" };
            buyBtn.style.width = Length.Percent(100);
            buyBtn.style.height = 44;
            buyBtn.style.fontSize = 16;
            buyBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
            buyBtn.style.backgroundColor = new Color(0.2f, 0.5f, 0.9f);
            buyBtn.style.borderTopLeftRadius = 8;
            buyBtn.style.borderBottomLeftRadius = 8;
            buyBtn.style.borderTopRightRadius = 8;
            buyBtn.style.borderBottomRightRadius = 8;
            buyBtn.style.marginBottom = 10;
            card.Add(buyBtn);

            var dismissBtn = new Button(() => { Close(); _onDismiss?.Invoke(); }) { text = "Maybe Later" };
            dismissBtn.style.fontSize = 12;
            dismissBtn.style.color = Color.gray;
            card.Add(dismissBtn);

            panel.Add(card);
            return panel;
        }

        public void Show(string message, Action onPurchase, Action onDismiss = null)
        {
            _onPurchase = onPurchase;
            _onDismiss = onDismiss;
            _root.style.display = DisplayStyle.Flex;
        }

        public void Close()
        {
            _root.style.display = DisplayStyle.None;
        }
    }
}
