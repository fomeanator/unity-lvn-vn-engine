using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// A save/load panel with multiple slots. Wire it to a UIDocument and
    /// assign a <see cref="VnStage"/> to enable saving and loading game state.
    /// </summary>
    public class SaveLoadPanel : MonoBehaviour
    {
        [Tooltip("The stage to save/load from.")]
        public VnStage Stage;

        [Tooltip("Number of save slots.")]
        public int SlotCount = 6;

        [Tooltip("Root USS name for styling.")]
        public string RootStyleName = "save-load-panel";

        private UIDocument _doc;
        private List<VisualElement> _slots = new List<VisualElement>();
        private List<LvnPlayer.LvnSnapshot> _snapshots = new List<LvnPlayer.LvnSnapshot>();
        private bool _isOpen;

        private void Awake()
        {
            _doc = GetComponent<UIDocument>();
            for (int i = 0; i < SlotCount; i++)
            {
                _snapshots.Add(null);
            }
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
            panel.style.left = Length.Percent(10);
            panel.style.right = Length.Percent(10);
            panel.style.top = Length.Percent(10);
            panel.style.bottom = Length.Percent(10);
            panel.style.backgroundColor = new Color(0, 0, 0, 0.85f);
            panel.style.display = DisplayStyle.None;

            var title = new Label("Save / Load");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 20;
            title.style.color = Color.white;
            title.style.marginBottom = 10;
            panel.Add(title);

            var grid = new VisualElement();
            grid.style.flexDirection = FlexDirection.Row;
            grid.style.flexWrap = Wrap.Wrap;
            grid.style.justifyContent = Justify.Center;

            for (int i = 0; i < SlotCount; i++)
            {
                var slot = BuildSlot(i);
                grid.Add(slot);
                _slots.Add(slot);
            }
            panel.Add(grid);

            var closeBtn = new Button(() => Close()) { text = "Close" };
            closeBtn.style.marginTop = 10;
            closeBtn.style.alignSelf = Align.Center;
            panel.Add(closeBtn);

            return panel;
        }

        private VisualElement BuildSlot(int index)
        {
            var slot = new VisualElement();
            slot.style.width = Length.Percent(45);
            slot.style.height = 80;
            slot.style.marginTop = 5;
            slot.style.marginBottom = 5;
            slot.style.marginLeft = 5;
            slot.style.marginRight = 5;
            slot.style.backgroundColor = new Color(0.2f, 0.2f, 0.2f);
            slot.style.borderTopLeftRadius = 8;
            slot.style.borderTopRightRadius = 8;
            slot.style.borderBottomLeftRadius = 8;
            slot.style.borderBottomRightRadius = 8;
            slot.style.paddingTop = 8;
            slot.style.paddingBottom = 8;
            slot.style.paddingLeft = 10;
            slot.style.paddingRight = 10;
            slot.style.justifyContent = Justify.SpaceBetween;

            var label = new Label($"Slot {index + 1}: Empty");
            label.style.color = Color.gray;
            label.style.fontSize = 14;
            slot.Add(label);

            var btnRow = new VisualElement();
            btnRow.style.flexDirection = FlexDirection.Row;

            var saveBtn = new Button(() => SaveToSlot(index)) { text = "Save" };
            saveBtn.style.marginRight = 5;
            saveBtn.style.fontSize = 12;
            btnRow.Add(saveBtn);

            var loadBtn = new Button(() => LoadFromSlot(index)) { text = "Load" };
            loadBtn.style.fontSize = 12;
            loadBtn.style.opacity = 0.5f;
            btnRow.Add(loadBtn);

            slot.Add(btnRow);
            return slot;
        }

        public void Open()
        {
            if (Stage == null || Stage.gameObject == null) return;
            _isOpen = true;
            var root = _doc.rootVisualElement;
            root.Q(RootStyleName).style.display = DisplayStyle.Flex;
            RefreshSlots();
        }

        public void Close()
        {
            _isOpen = false;
            var root = _doc.rootVisualElement;
            root.Q(RootStyleName).style.display = DisplayStyle.None;
        }

        public bool IsOpen => _isOpen;

        private void RefreshSlots()
        {
            for (int i = 0; i < _slots.Count && i < _snapshots.Count; i++)
            {
                var label = _slots[i].Q<Label>();
                var snap = _snapshots[i];
                if (snap != null)
                {
                    label.text = $"Slot {i + 1}: Saved (IP {snap.Index})";
                    label.style.color = Color.white;
                    _slots[i].Query<Button>().Last().style.opacity = 1f;
                }
                else
                {
                    label.text = $"Slot {i + 1}: Empty";
                    label.style.color = Color.gray;
                    _slots[i].Query<Button>().Last().style.opacity = 0.5f;
                }
            }
        }

        private void SaveToSlot(int index)
        {
            if (Stage == null || Stage.Player == null) return;
            _snapshots[index] = Stage.Player.Save();
            RefreshSlots();
        }

        private void LoadFromSlot(int index)
        {
            if (Stage == null || Stage.Player == null) return;
            var snap = _snapshots[index];
            if (snap == null) return;
            Stage.Player.Restore(snap);
            Close();
        }
    }
}
