using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// The in-game quick menu: two floating buttons (menu ☰ and rollback ↩) that
    /// unfold into Save / Load / History / Auto / Settings panels — the standard
    /// VN chrome, built from the engine's own primitives (LvnSaveStore, LvnPrefs,
    /// the stage's backlog and rollback). Lives as a top layer inside the stage's
    /// UIDocument; while a sheet is open the stage's tap-to-advance is blocked.
    /// </summary>
    public sealed class StageMenu : VisualElement
    {
        private const int SlotCount = 6;

        private readonly VnStage _stage;
        private readonly VnTheme _theme;
        private readonly VisualElement _fabRow;
        private VisualElement _scrim;

        public bool IsOpen { get; private set; }

        public StageMenu(VnStage stage, VnTheme theme)
        {
            _stage = stage;
            _theme = theme ?? new VnTheme();

            name = "vn-menu";
            style.position = Position.Absolute;
            style.left = 0; style.right = 0; style.top = 0; style.bottom = 0;
            pickingMode = PickingMode.Ignore; // the closed layer never eats stage taps

            // Floating buttons, top-right under the shell HUD strip. Which ones
            // exist — and every colour below — comes from the theme (manifest.ui.menu).
            _fabRow = new VisualElement();
            _fabRow.style.position = Position.Absolute;
            _fabRow.style.top = Length.Percent(8.5f);
            _fabRow.style.right = 10;
            _fabRow.style.flexDirection = FlexDirection.Row;
            if (_theme.MenuShowRollback) _fabRow.Add(Fab("↩", () => _stage.RollbackStep()));
            if (_theme.MenuShowMenu) _fabRow.Add(Fab("☰", OpenSheet));
            Add(_fabRow);
        }

        private VisualElement Fab(string glyph, Action onClick)
        {
            var b = new Button(onClick) { text = glyph };
            b.style.width = 44; b.style.height = 44;
            b.style.marginLeft = 8;
            b.style.fontSize = 20;
            b.style.color = _theme.MenuTextColor;
            b.style.backgroundColor = _theme.MenuFabColor;
            Round(b, 22);
            ClearBorder(b);
            // A press on the chrome must never bubble into tap-to-advance.
            b.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            if (_theme.Font != null) b.style.unityFont = new StyleFont(_theme.Font);
            return b;
        }

        // ── sheet ────────────────────────────────────────────────────────────

        private void OpenSheet()
        {
            if (IsOpen) return;
            IsOpen = true;
            _stage.InputBlocked = true;

            // Full-screen scrim: swallows every tap; tapping empty space closes.
            _scrim = new VisualElement();
            _scrim.style.position = Position.Absolute;
            _scrim.style.left = 0; _scrim.style.right = 0; _scrim.style.top = 0; _scrim.style.bottom = 0;
            _scrim.style.backgroundColor = _theme.MenuScrimColor;
            _scrim.RegisterCallback<PointerDownEvent>(e =>
            {
                e.StopPropagation();
                if (e.target == _scrim) Close();
            });
            Add(_scrim);

            ShowMain();
        }

        /// <summary>Close every open sheet/panel and unblock the stage.</summary>
        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            _stage.InputBlocked = false;
            _scrim?.RemoveFromHierarchy();
            _scrim = null;
        }

        // Swap the scrim's content for a fresh panel.
        private VisualElement Panel(string title)
        {
            _scrim.Clear();
            var p = new VisualElement();
            p.style.position = Position.Absolute;
            p.style.left = Length.Percent(8); p.style.right = Length.Percent(8);
            p.style.top = Length.Percent(12); p.style.bottom = Length.Percent(12);
            p.style.backgroundColor = _theme.MenuBgColor;
            p.style.paddingLeft = 18; p.style.paddingRight = 18;
            p.style.paddingTop = 14; p.style.paddingBottom = 14;
            Round(p, _theme.MenuCornerRadius + 2f);
            p.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            _scrim.Add(p);

            var head = new VisualElement();
            head.style.flexDirection = FlexDirection.Row;
            head.style.justifyContent = Justify.SpaceBetween;
            head.style.marginBottom = 10;
            var t = Text(title, 20, FontStyle.Bold);
            head.Add(t);
            var back = new Button(ShowMain) { text = "‹" };
            StyleGhost(back);
            head.Add(back);
            p.Add(head);
            return p;
        }

        private void ShowMain()
        {
            _scrim.Clear();
            var sheet = new VisualElement();
            sheet.style.position = Position.Absolute;
            sheet.style.right = 12;
            sheet.style.top = Length.Percent(10);
            sheet.style.width = 240;
            sheet.style.backgroundColor = _theme.MenuBgColor;
            sheet.style.paddingTop = 8; sheet.style.paddingBottom = 8;
            Round(sheet, _theme.MenuCornerRadius);
            sheet.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            _scrim.Add(sheet);

            sheet.Add(Item("Save", () => ShowSlots(saveMode: true)));
            sheet.Add(Item("Load", () => ShowSlots(saveMode: false)));
            sheet.Add(Item("History", ShowHistory));
            sheet.Add(Item(LvnPrefs.AutoAdvance ? "Auto ✓" : "Auto", () =>
            {
                LvnPrefs.AutoAdvance = !LvnPrefs.AutoAdvance;
                Close(); // hands-free mode starts/stops right away
            }));
            sheet.Add(Item("Settings", ShowSettings));
            sheet.Add(Item("Close", Close));
        }

        private VisualElement Item(string label, Action onClick)
        {
            var b = new Button(onClick) { text = label };
            b.style.height = 46;
            b.style.fontSize = 17;
            b.style.color = _theme.MenuTextColor;
            b.style.backgroundColor = Color.clear;
            b.style.unityTextAlign = TextAnchor.MiddleLeft;
            b.style.paddingLeft = 18;
            ClearBorder(b);
            if (_theme.Font != null) b.style.unityFont = new StyleFont(_theme.Font);
            return b;
        }

        // ── save / load slots ────────────────────────────────────────────────

        private void ShowSlots(bool saveMode)
        {
            var p = Panel(saveMode ? "Save" : "Load");
            var scroll = new ScrollView();
            scroll.style.flexGrow = 1;
            p.Add(scroll);

            var all = LvnSaveStore.Slots(_stage.SaveTitleId);

            // The autosave appears in load mode only (it's the engine's own slot).
            if (!saveMode && all.TryGetValue(LvnSaveStore.AutoSlot, out var auto) && auto?.Snap != null)
                scroll.Add(SlotRow("Autosave", auto, () => TryLoad(LvnSaveStore.AutoSlot)));

            for (int i = 0; i < SlotCount; i++)
            {
                var name = "slot" + (i + 1);
                all.TryGetValue(name, out var slot);
                var label = "Slot " + (i + 1);
                if (saveMode)
                    scroll.Add(SlotRow(label, slot, () =>
                    {
                        if (_stage.SaveToSlot(name)) ShowSlots(true); // refresh with the new stamp
                    }));
                else
                    scroll.Add(SlotRow(label, slot, () => TryLoad(name), enabled: _stage.CanLoadSlot(name)));
            }
        }

        private async void TryLoad(string slot)
        {
            // Same-chapter slots restore in place; another chapter's slot routes
            // through the host (fetch that chapter's script, play, restore).
            if (await _stage.LoadFromSlotAsync(slot)) Close();
        }

        private VisualElement SlotRow(string label, LvnSaveSlot slot, Action onClick, bool enabled = true)
        {
            var row = new Button(onClick);
            row.style.height = 56;
            row.style.marginBottom = 6;
            var tint = _theme.MenuTextColor;
            row.style.backgroundColor = new Color(tint.r, tint.g, tint.b, 0.06f);
            row.style.unityTextAlign = TextAnchor.MiddleLeft;
            row.style.paddingLeft = 12;
            row.style.flexDirection = FlexDirection.Column;
            row.style.justifyContent = Justify.Center;
            Round(row, Mathf.Max(4f, _theme.MenuCornerRadius - 4f));
            ClearBorder(row);
            row.SetEnabled(enabled);

            string when = slot?.Snap == null ? "— empty —"
                : DateTimeOffset.FromUnixTimeMilliseconds(slot.SavedAtUnixMs).ToLocalTime().ToString("dd.MM HH:mm");
            row.Add(Text(label + "   " + when, 15, FontStyle.Bold));
            if (!string.IsNullOrEmpty(slot?.Preview))
                row.Add(Text("«" + Trunc(slot.Preview, 46) + "»", 13, FontStyle.Italic, dim: true));
            return row;
        }

        // ── history ──────────────────────────────────────────────────────────

        private void ShowHistory()
        {
            var p = Panel("History");
            var scroll = new ScrollView();
            scroll.style.flexGrow = 1;
            p.Add(scroll);

            foreach (var (who, text, _) in _stage.Backlog)
            {
                var line = new VisualElement();
                line.style.marginBottom = 8;
                if (!string.IsNullOrEmpty(who)) line.Add(Text(who, 14, FontStyle.Bold));
                line.Add(Text(text, 15, FontStyle.Normal, dim: string.IsNullOrEmpty(who)));
                scroll.Add(line);
            }
            // Newest last — land the reader there.
            scroll.schedule.Execute(() =>
                scroll.scrollOffset = new Vector2(0, float.MaxValue)).ExecuteLater(50);
        }

        // ── settings ─────────────────────────────────────────────────────────

        private void ShowSettings()
        {
            var p = Panel("Settings");
            var scroll = new ScrollView();
            scroll.style.flexGrow = 1;
            p.Add(scroll);

            scroll.Add(SliderRow("Text speed", 0.25f, 3f, LvnPrefs.TextSpeed, v => LvnPrefs.TextSpeed = v));
            scroll.Add(ToggleRow("Auto-advance", LvnPrefs.AutoAdvance, v => LvnPrefs.AutoAdvance = v));
            scroll.Add(SliderRow("Auto delay", 0.5f, 2.5f, LvnPrefs.AutoDelayScale, v => LvnPrefs.AutoDelayScale = v));
            scroll.Add(SliderRow("Music", 0f, 1f, LvnPrefs.VolMusic, v => LvnPrefs.VolMusic = v));
            scroll.Add(SliderRow("Ambient", 0f, 1f, LvnPrefs.VolAmbient, v => LvnPrefs.VolAmbient = v));
            scroll.Add(SliderRow("Sound FX", 0f, 1f, LvnPrefs.VolSfx, v => LvnPrefs.VolSfx = v));
            scroll.Add(SliderRow("Window opacity", 0.2f, 1f, LvnPrefs.DialogOpacity, v => LvnPrefs.DialogOpacity = v));
            scroll.Add(ToggleRow("Reduce motion", LvnPrefs.ReduceMotion, v => LvnPrefs.ReduceMotion = v));
        }

        private VisualElement SliderRow(string label, float min, float max, float value, Action<float> onChange)
        {
            var row = new VisualElement();
            row.style.marginBottom = 10;
            row.Add(Text(label, 14, FontStyle.Normal));
            var s = new Slider(min, max) { value = value };
            s.RegisterValueChangedCallback(e => onChange(e.newValue));
            row.Add(s);
            return row;
        }

        private VisualElement ToggleRow(string label, bool value, Action<bool> onChange)
        {
            var row = new VisualElement();
            row.style.marginBottom = 10;
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.SpaceBetween;
            row.Add(Text(label, 14, FontStyle.Normal));
            var t = new Toggle { value = value };
            t.RegisterValueChangedCallback(e => onChange(e.newValue));
            row.Add(t);
            return row;
        }

        // ── little style helpers ─────────────────────────────────────────────

        private Label Text(string s, int size, FontStyle weight, bool dim = false)
        {
            var l = new Label(s);
            l.style.fontSize = size;
            l.style.unityFontStyleAndWeight = weight;
            l.style.color = dim ? _theme.MenuDimTextColor : _theme.MenuTextColor;
            l.style.whiteSpace = WhiteSpace.Normal;
            if (_theme.Font != null) l.style.unityFont = new StyleFont(_theme.Font);
            return l;
        }

        private void StyleGhost(Button b)
        {
            b.style.backgroundColor = Color.clear;
            b.style.color = _theme.MenuTextColor;
            b.style.fontSize = 22;
            b.style.width = 34; b.style.height = 30;
            ClearBorder(b);
            if (_theme.Font != null) b.style.unityFont = new StyleFont(_theme.Font);
        }

        private static void Round(VisualElement el, float r)
        {
            el.style.borderTopLeftRadius = r; el.style.borderTopRightRadius = r;
            el.style.borderBottomLeftRadius = r; el.style.borderBottomRightRadius = r;
        }

        private static void ClearBorder(VisualElement el)
        {
            el.style.borderTopWidth = 0; el.style.borderBottomWidth = 0;
            el.style.borderLeftWidth = 0; el.style.borderRightWidth = 0;
        }

        private static string Trunc(string s, int max)
            => s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
