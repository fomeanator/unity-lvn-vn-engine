using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.UIElements.Experimental;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// A swipeable, snapping carousel of title cards, themed from a
    /// <see cref="CarouselConfig"/> (manifest <c>ui.carousel</c>) and populated
    /// from the manifest's <c>titles</c>. Each card shows a cover, a name and a
    /// subtitle; a Play button under the deck launches the selected title. Drag
    /// to scroll, release to snap (paged math in the pure <see cref="CarouselSnap"/>).
    /// <see cref="OnPlay"/> fires the selected title's index.
    /// </summary>
    public sealed class TitleCarousel : VisualElement
    {
        private readonly CarouselConfig _cfg;
        private readonly ILvnAssets _assets;
        private IReadOnlyList<LvnTitle> _titles;

        private readonly VisualElement _viewport;
        private readonly VisualElement _strip;
        private readonly VisualElement _dots;
        private readonly List<VisualElement> _dotEls = new List<VisualElement>();
        private readonly Color _dotColor, _dotActiveColor;

        private readonly float _cardWFrac, _cardHFrac, _gapFrac;
        private float _stride = 1f;
        private float _centerPad;
        private float _offset;          // current scroll (px)
        private int _index;
        private bool _dragging;
        private float _dragStartX, _dragStartOffset;
        private bool _pendingPlay;      // latched RequestPlay fired before the shell subscribed
        private int _pendingPlayIdx;

        public int Index => _index;
        public LvnTitle Current => (_titles != null && _index >= 0 && _index < _titles.Count) ? _titles[_index] : null;

        private Button _play;
        private Button _chaptersBtn;
        private Label _progressLabel;
        private VisualElement _picker; // the chapter-picker overlay, when open

        /// <summary>Fired when the resting card changes (drag-snap or SetIndex).</summary>
        public event Action<int> OnIndexChanged;
        /// <summary>Fired when the player taps Play; argument is the title index.</summary>
        public event Action<int> OnPlay;

        public TitleCarousel(IReadOnlyList<LvnTitle> titles, CarouselConfig cfg, ILvnAssets assets)
        {
            _cfg = cfg ?? new CarouselConfig();
            _assets = assets;
            _titles = titles ?? new List<LvnTitle>();
            _cardWFrac = _cfg.card_width ?? 0.62f;
            _cardHFrac = _cfg.card_height ?? 0.62f;
            _gapFrac = _cfg.card_gap ?? 0.06f;
            _dotColor = UiColor.Parse(_cfg.dot_color, new Color(1f, 1f, 1f, 0.33f));
            _dotActiveColor = UiColor.Parse(_cfg.dot_active_color, new Color(0.96f, 0.93f, 0.85f));

            ScreenUi.Stretch(this);
            style.backgroundColor = UiColor.Parse(_cfg.bg_color, new Color(0.06f, 0.06f, 0.08f));

            _viewport = new VisualElement();
            _viewport.style.position = Position.Absolute;
            _viewport.style.left = 0; _viewport.style.right = 0;
            _viewport.style.top = Length.Percent(12f);
            _viewport.style.height = Length.Percent(_cardHFrac * 100f);
            _viewport.style.overflow = Overflow.Hidden;
            Add(_viewport);

            _strip = new VisualElement();
            _strip.style.position = Position.Absolute;
            _strip.style.top = 0; _strip.style.bottom = 0;
            _strip.style.flexDirection = FlexDirection.Row;
            _strip.style.alignItems = Align.Center;
            _viewport.Add(_strip);

            // page dots
            _dots = new VisualElement();
            _dots.style.position = Position.Absolute;
            _dots.style.left = 0; _dots.style.right = 0;
            _dots.style.top = Length.Percent(78f);
            _dots.style.flexDirection = FlexDirection.Row;
            _dots.style.justifyContent = Justify.Center;
            _dots.pickingMode = PickingMode.Ignore;
            Add(_dots);

            BuildDeck();

            // Play button — labelled "Continue" (with the episode underneath) when
            // the selected title has a saved reading position.
            _play = new Button { text = _cfg.play_text ?? "Play" };
            _play.style.position = Position.Absolute;
            _play.style.left = Length.Percent(30f);
            _play.style.right = Length.Percent(30f);
            _play.style.top = Length.Percent(84f);
            _play.style.height = Length.Percent(8f);
            _play.style.fontSize = 28;
            _play.style.color = UiColor.Parse(_cfg.play_color, new Color(0.96f, 0.93f, 0.85f));
            _play.style.backgroundColor = UiColor.Parse(_cfg.play_bg_color, new Color(0.23f, 0.23f, 0.27f));
            _play.style.borderTopLeftRadius = 12; _play.style.borderTopRightRadius = 12;
            _play.style.borderBottomLeftRadius = 12; _play.style.borderBottomRightRadius = 12;
            _play.clicked += () => OnPlay?.Invoke(_index);
            Add(_play);

            // Where the player IS in this title — under the Play button.
            _progressLabel = new Label { pickingMode = PickingMode.Ignore };
            _progressLabel.style.position = Position.Absolute;
            _progressLabel.style.left = 0; _progressLabel.style.right = 0;
            _progressLabel.style.top = Length.Percent(92.5f);
            _progressLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _progressLabel.style.fontSize = 18;
            _progressLabel.style.color = UiColor.Parse(_cfg.subtitle_color, new Color(0.80f, 0.72f, 0.56f));
            Add(_progressLabel);

            // Chapter picker — only meaningful for multi-chapter titles; shown per
            // selection in UpdatePlayLabel.
            _chaptersBtn = new Button(OpenChapterPicker) { text = _cfg.chapters_text ?? "Chapters" };
            _chaptersBtn.style.position = Position.Absolute;
            _chaptersBtn.style.left = Length.Percent(72f);
            _chaptersBtn.style.right = Length.Percent(6f);
            _chaptersBtn.style.top = Length.Percent(84f);
            _chaptersBtn.style.height = Length.Percent(8f);
            _chaptersBtn.style.fontSize = 18;
            _chaptersBtn.style.color = UiColor.Parse(_cfg.play_color, new Color(0.96f, 0.93f, 0.85f));
            _chaptersBtn.style.backgroundColor = UiColor.Parse(_cfg.card_bg_color, new Color(0.11f, 0.11f, 0.13f));
            _chaptersBtn.style.borderTopLeftRadius = 12; _chaptersBtn.style.borderTopRightRadius = 12;
            _chaptersBtn.style.borderBottomLeftRadius = 12; _chaptersBtn.style.borderBottomRightRadius = 12;
            Add(_chaptersBtn);

            OnIndexChanged += _ => UpdatePlayLabel();
            UpdatePlayLabel();

            // drag handling
            _viewport.RegisterCallback<PointerDownEvent>(OnPointerDown);
            _viewport.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            _viewport.RegisterCallback<PointerUpEvent>(OnPointerUp);

            RegisterCallback<GeometryChangedEvent>(OnGeometry);
        }

        /// <summary>Programmatically launch the selected title (same as tapping
        /// Play) — for automation, tests, or a keyboard/gamepad binding.</summary>
        public void Play() => OnPlay?.Invoke(_index);

        /// <summary>Race-free programmatic play for auto-start / deep-linking into a
        /// title. Unlike <see cref="Play"/>, this never drops the request when fired
        /// before the shell hands control to the carousel (e.g. during the boot
        /// splash, when <see cref="OnPlay"/> has no subscriber yet): the request is
        /// latched and delivered the moment the shell starts waiting.</summary>
        public void RequestPlay(int index)
        {
            int n = _titles?.Count ?? 0;
            _index = n > 0 ? new CarouselSnap(_stride, n).Clamp(index) : 0;
            if (OnPlay != null) OnPlay.Invoke(_index);
            else { _pendingPlay = true; _pendingPlayIdx = _index; }
        }

        /// <summary>Drain a latched <see cref="RequestPlay"/>, if any. The shell calls
        /// this right after subscribing so an early request is honoured exactly once.</summary>
        public bool TryConsumePendingPlay(out int index)
        {
            index = _pendingPlayIdx;
            if (!_pendingPlay) return false;
            _pendingPlay = false;
            return true;
        }

        /// <summary>Replace the deck with a new title list (live content update) —
        /// rebuilds the cards and dots, keeps the selected index where possible,
        /// and re-lays out. Lets the carousel react to a manifest change without a
        /// full screen rebuild.</summary>
        public void SetTitles(IReadOnlyList<LvnTitle> titles)
        {
            _titles = titles ?? new List<LvnTitle>();
            BuildDeck();
            _index = new CarouselSnap(_stride, _titles.Count).Clamp(_index);
            Relayout();
            UpdateDots();
            UpdatePlayLabel();
            OnIndexChanged?.Invoke(_index);
        }

        // (Re)builds the card strip and the page dots from the current title list.
        private void BuildDeck()
        {
            _strip.Clear();
            for (int i = 0; i < _titles.Count; i++)
                _strip.Add(BuildCard(_titles[i]));

            _dots.Clear();
            _dotEls.Clear();
            for (int i = 0; i < _titles.Count; i++)
            {
                var dot = new VisualElement();
                dot.style.width = 10; dot.style.height = 10;
                dot.style.marginLeft = 5; dot.style.marginRight = 5;
                dot.style.borderTopLeftRadius = 5; dot.style.borderTopRightRadius = 5;
                dot.style.borderBottomLeftRadius = 5; dot.style.borderBottomRightRadius = 5;
                dot.style.backgroundColor = i == _index ? _dotActiveColor : _dotColor;
                _dots.Add(dot);
                _dotEls.Add(dot);
            }
        }

        /// <summary>Snap to a card index (clamped), animating the deck.</summary>
        public void SetIndex(int index, bool animate = true)
        {
            var snap = new CarouselSnap(_stride, _titles.Count);
            _index = snap.Clamp(index);
            AnimateTo(snap.OffsetFor(_index), animate);
            UpdateDots();
            OnIndexChanged?.Invoke(_index);
        }

        private void OnGeometry(GeometryChangedEvent _) => Relayout();

        // Sizes the cards in pixels and centres the deck on the current index.
        // Safe to call any time the viewport has a resolved width (else the
        // GeometryChanged callback re-runs it once layout settles).
        private void Relayout()
        {
            float w = _viewport.resolvedStyle.width;
            if (w <= 1f) return;
            float cardW = w * _cardWFrac;
            float gap = w * _gapFrac;
            _stride = cardW + gap;
            _centerPad = (w - cardW) * 0.5f;

            for (int i = 0; i < _strip.childCount; i++)
            {
                var card = _strip[i];
                card.style.width = cardW;
                card.style.height = Length.Percent(100f);
                card.style.marginRight = (i == _strip.childCount - 1) ? 0 : gap;
            }
            _offset = new CarouselSnap(_stride, _titles.Count).OffsetFor(_index);
            ApplyOffset();
        }

        private VisualElement BuildCard(LvnTitle t)
        {
            var card = new VisualElement();
            card.style.flexShrink = 0;
            card.style.backgroundColor = UiColor.Parse(_cfg.card_bg_color, new Color(0.11f, 0.11f, 0.13f));
            float r = _cfg.card_radius ?? 18f;
            card.style.borderTopLeftRadius = r; card.style.borderTopRightRadius = r;
            card.style.borderBottomLeftRadius = r; card.style.borderBottomRightRadius = r;
            card.style.overflow = Overflow.Hidden;
            card.style.justifyContent = Justify.FlexEnd;

            var cover = new VisualElement();
            cover.style.position = Position.Absolute;
            cover.style.left = 0; cover.style.right = 0; cover.style.top = 0; cover.style.bottom = 0;
            cover.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
            cover.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
            cover.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
            cover.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
            cover.pickingMode = PickingMode.Ignore;
            card.Add(cover);
            _ = ScreenUi.AssignBgAsync(cover, t?.cover_url, _assets);

            var caption = new VisualElement();
            caption.style.paddingLeft = 18; caption.style.paddingRight = 18;
            caption.style.paddingTop = 14; caption.style.paddingBottom = 18;
            caption.style.backgroundColor = new Color(0f, 0f, 0f, 0.45f);
            caption.pickingMode = PickingMode.Ignore;
            card.Add(caption);

            var name = new Label(string.IsNullOrEmpty(t?.name) ? (t?.id ?? "") : t.name);
            name.style.color = UiColor.Parse(_cfg.title_color, new Color(0.96f, 0.93f, 0.85f));
            name.style.fontSize = _cfg.title_size ?? 40f;
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.style.whiteSpace = WhiteSpace.Normal;
            caption.Add(name);

            if (!string.IsNullOrEmpty(t?.subtitle))
            {
                var sub = new Label(t.subtitle);
                sub.style.color = UiColor.Parse(_cfg.subtitle_color, new Color(0.80f, 0.72f, 0.56f));
                sub.style.fontSize = _cfg.subtitle_size ?? 22f;
                sub.style.marginTop = 4;
                sub.style.whiteSpace = WhiteSpace.Normal;
                caption.Add(sub);
            }
            return card;
        }

        // ── continue / chapter picker ───────────────────────────────────────

        private static string ChapterLabel(LvnChapter c) =>
            !string.IsNullOrEmpty(c?.name) ? c.name : (c != null && c.number > 0 ? "Chapter " + c.number : c?.id ?? "");

        private static List<LvnChapter> ChaptersOf(LvnTitle t)
        {
            var list = new List<LvnChapter>();
            if (t?.seasons == null) return list;
            foreach (var s in t.seasons)
                if (s?.chapters != null)
                    foreach (var c in s.chapters)
                        if (c != null)
                            list.Add(c);
            list.Sort((a, b) => a.number.CompareTo(b.number));
            return list;
        }

        /// <summary>Re-read the selected title's saved progress into the Play
        /// button ("Continue" + the episode) and the chapter-picker visibility.
        /// The shell calls this whenever the carousel regains the screen —
        /// progress changed while a chapter was playing.</summary>
        public void RefreshProgress() => UpdatePlayLabel();

        private void UpdatePlayLabel()
        {
            var t = Current;
            var cur = t != null ? LvnProgress.Current(t) : null;
            if (cur != null)
            {
                _play.text = _cfg.continue_text ?? "Continue";
                _progressLabel.text = ChapterLabel(cur);
            }
            else
            {
                _play.text = _cfg.play_text ?? "Play";
                _progressLabel.text = "";
            }
            var chapters = ChaptersOf(t);
            _chaptersBtn.style.display = chapters.Count > 1 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void OpenChapterPicker()
        {
            var t = Current;
            var chapters = ChaptersOf(t);
            if (chapters.Count < 2) return;
            CloseChapterPicker();

            int reached = LvnProgress.Reached(t);
            int firstNumber = chapters[0].number;

            // Scrim: swallow every tap; tapping outside the panel closes.
            _picker = new VisualElement();
            _picker.style.position = Position.Absolute;
            _picker.style.left = 0; _picker.style.right = 0; _picker.style.top = 0; _picker.style.bottom = 0;
            _picker.style.backgroundColor = new Color(0f, 0f, 0f, 0.6f);
            _picker.RegisterCallback<PointerDownEvent>(e =>
            {
                e.StopPropagation();
                if (e.target == _picker) CloseChapterPicker();
            });
            Add(_picker);

            var panel = new VisualElement();
            panel.style.position = Position.Absolute;
            panel.style.left = Length.Percent(10f); panel.style.right = Length.Percent(10f);
            panel.style.top = Length.Percent(10f); panel.style.bottom = Length.Percent(10f);
            panel.style.backgroundColor = UiColor.Parse(_cfg.card_bg_color, new Color(0.11f, 0.11f, 0.13f));
            panel.style.borderTopLeftRadius = 14; panel.style.borderTopRightRadius = 14;
            panel.style.borderBottomLeftRadius = 14; panel.style.borderBottomRightRadius = 14;
            panel.style.paddingLeft = 16; panel.style.paddingRight = 16;
            panel.style.paddingTop = 14; panel.style.paddingBottom = 14;
            panel.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            _picker.Add(panel);

            var head = new Label(_cfg.chapters_text ?? "Chapters");
            head.style.fontSize = 24;
            head.style.unityFontStyleAndWeight = FontStyle.Bold;
            head.style.color = UiColor.Parse(_cfg.title_color, new Color(0.96f, 0.93f, 0.85f));
            head.style.marginBottom = 10;
            panel.Add(head);

            var scroll = new ScrollView();
            scroll.style.flexGrow = 1;
            panel.Add(scroll);

            foreach (var c in chapters)
            {
                var ch = c;
                // The first chapter is always open; later ones unlock as reached.
                bool unlocked = ch.number <= reached || ch.number == firstNumber;
                var row = new Button(() =>
                {
                    // An explicit pick means "start THIS chapter from its top,
                    // with the variables it originally began with" (the genre
                    // convention): move the continue point, drop the mid-chapter
                    // autosave, and ask the play loop to seed from the chapter's
                    // entry checkpoint instead of the live accumulated state.
                    LvnProgress.SetCurrent(t, ch);
                    LvnProgress.RequestRestart(t.id, ch.id);
                    LvnSaveStore.Delete(t.id, LvnSaveStore.AutoSlot);
                    CloseChapterPicker();
                    OnPlay?.Invoke(_index);
                })
                { text = ChapterLabel(ch) };
                row.style.height = 52;
                row.style.marginBottom = 6;
                row.style.fontSize = 19;
                row.style.unityTextAlign = TextAnchor.MiddleLeft;
                row.style.paddingLeft = 14;
                row.style.color = UiColor.Parse(_cfg.title_color, new Color(0.96f, 0.93f, 0.85f));
                var bg = UiColor.Parse(_cfg.play_bg_color, new Color(0.23f, 0.23f, 0.27f));
                row.style.backgroundColor = new Color(bg.r, bg.g, bg.b, unlocked ? bg.a : bg.a * 0.35f);
                row.style.borderTopLeftRadius = 10; row.style.borderTopRightRadius = 10;
                row.style.borderBottomLeftRadius = 10; row.style.borderBottomRightRadius = 10;
                row.SetEnabled(unlocked);
                scroll.Add(row);
            }
        }

        private void CloseChapterPicker()
        {
            _picker?.RemoveFromHierarchy();
            _picker = null;
        }

        private void OnPointerDown(PointerDownEvent e)
        {
            _dragging = true;
            _dragStartX = e.position.x;
            _dragStartOffset = _offset;
            _viewport.CapturePointer(e.pointerId);
        }

        private void OnPointerMove(PointerMoveEvent e)
        {
            if (!_dragging) return;
            _offset = _dragStartOffset - (e.position.x - _dragStartX);
            ApplyOffset();
        }

        private void OnPointerUp(PointerUpEvent e)
        {
            if (!_dragging) return;
            _dragging = false;
            _viewport.ReleasePointer(e.pointerId);
            var snap = new CarouselSnap(_stride, _titles.Count);
            SetIndex(snap.IndexAt(_offset));
        }

        private void AnimateTo(float targetOffset, bool animate)
        {
            if (!animate || float.IsNaN(_offset))
            {
                _offset = targetOffset;
                ApplyOffset();
                return;
            }
            float from = _offset;
            experimental.animation
                .Start(0f, 1f, 220, (el, t) => { _offset = Mathf.Lerp(from, targetOffset, t); ApplyOffset(); })
                .Ease(Easing.OutCubic);
        }

        private void ApplyOffset() => _strip.style.translate = new Translate(_centerPad - _offset, 0f, 0f);

        private void UpdateDots()
        {
            for (int i = 0; i < _dotEls.Count; i++)
                _dotEls[i].style.backgroundColor = i == _index ? _dotActiveColor : _dotColor;
        }
    }
}
