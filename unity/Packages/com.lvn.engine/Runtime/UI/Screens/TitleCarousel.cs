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
        private readonly IReadOnlyList<LvnTitle> _titles;

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

        public int Index => _index;
        public LvnTitle Current => (_titles != null && _index >= 0 && _index < _titles.Count) ? _titles[_index] : null;

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

            Fill(this);
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

            for (int i = 0; i < _titles.Count; i++)
                _strip.Add(BuildCard(_titles[i]));

            // page dots
            _dots = new VisualElement();
            _dots.style.position = Position.Absolute;
            _dots.style.left = 0; _dots.style.right = 0;
            _dots.style.top = Length.Percent(78f);
            _dots.style.flexDirection = FlexDirection.Row;
            _dots.style.justifyContent = Justify.Center;
            _dots.pickingMode = PickingMode.Ignore;
            Add(_dots);
            for (int i = 0; i < _titles.Count; i++)
            {
                var dot = new VisualElement();
                dot.style.width = 10; dot.style.height = 10;
                dot.style.marginLeft = 5; dot.style.marginRight = 5;
                dot.style.borderTopLeftRadius = 5; dot.style.borderTopRightRadius = 5;
                dot.style.borderBottomLeftRadius = 5; dot.style.borderBottomRightRadius = 5;
                dot.style.backgroundColor = i == 0 ? _dotActiveColor : _dotColor;
                _dots.Add(dot);
                _dotEls.Add(dot);
            }

            // Play button
            var play = new Button { text = _cfg.play_text ?? "Play" };
            play.style.position = Position.Absolute;
            play.style.left = Length.Percent(30f);
            play.style.right = Length.Percent(30f);
            play.style.top = Length.Percent(84f);
            play.style.height = Length.Percent(8f);
            play.style.fontSize = 28;
            play.style.color = UiColor.Parse(_cfg.play_color, new Color(0.96f, 0.93f, 0.85f));
            play.style.backgroundColor = UiColor.Parse(_cfg.play_bg_color, new Color(0.23f, 0.23f, 0.27f));
            play.style.borderTopLeftRadius = 12; play.style.borderTopRightRadius = 12;
            play.style.borderBottomLeftRadius = 12; play.style.borderBottomRightRadius = 12;
            play.clicked += () => OnPlay?.Invoke(_index);
            Add(play);

            // drag handling
            _viewport.RegisterCallback<PointerDownEvent>(OnPointerDown);
            _viewport.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            _viewport.RegisterCallback<PointerUpEvent>(OnPointerUp);

            RegisterCallback<GeometryChangedEvent>(OnGeometry);
        }

        /// <summary>Programmatically launch the selected title (same as tapping
        /// Play) — for automation, tests, or a keyboard/gamepad binding.</summary>
        public void Play() => OnPlay?.Invoke(_index);

        /// <summary>Snap to a card index (clamped), animating the deck.</summary>
        public void SetIndex(int index, bool animate = true)
        {
            var snap = new CarouselSnap(_stride, _titles.Count);
            _index = snap.Clamp(index);
            AnimateTo(snap.OffsetFor(_index), animate);
            UpdateDots();
            OnIndexChanged?.Invoke(_index);
        }

        private void OnGeometry(GeometryChangedEvent _)
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
            _ = AssignBg(cover, t?.cover_url);

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

        private static VisualElement Fill(VisualElement el)
        {
            el.style.position = Position.Absolute;
            el.style.left = 0; el.style.right = 0; el.style.top = 0; el.style.bottom = 0;
            return el;
        }
    }
}
