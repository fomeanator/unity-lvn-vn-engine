using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// The "Chapter N / chapter name" reveal card, themed from a
    /// <see cref="TitleCardConfig"/> (manifest <c>ui.title</c>): an optional fog
    /// wash and decorative frame behind a chapter line and a subtitle line. Set
    /// the text, then <see cref="RevealAsync"/> to fade it in, hold, and fade out.
    /// </summary>
    public sealed class TitleCard : VisualElement
    {
        private readonly TitleCardConfig _cfg;
        private readonly ILvnAssets _assets;
        private readonly VisualElement _fog;
        private readonly VisualElement _card;
        private readonly Label _chapter;
        private readonly Label _subtitle;
        private readonly float _hold;
        private readonly float _fade;

        public TitleCard(TitleCardConfig cfg, ILvnAssets assets)
        {
            _cfg = cfg ?? new TitleCardConfig();
            _assets = assets;
            _hold = _cfg.hold_seconds ?? 2.5f;
            _fade = _cfg.fade_seconds ?? 0.6f;

            FullScreen(this);
            style.opacity = 0f;
            pickingMode = PickingMode.Ignore;

            _fog = FullScreen(new VisualElement());
            _fog.style.opacity = 0f;
            Add(_fog);

            _card = new VisualElement();
            _card.style.position = Position.Absolute;
            _card.style.left = 0;
            _card.style.right = 0;
            _card.style.top = Length.Percent(34f);
            _card.style.alignItems = Align.Center;
            _card.style.justifyContent = Justify.Center;
            _card.style.paddingTop = 40;
            _card.style.paddingBottom = 40;
            Add(_card);

            _chapter = new Label();
            _chapter.style.unityTextAlign = TextAnchor.MiddleCenter;
            _chapter.style.color = UiColor.Parse(_cfg.chapter_color, new Color(0.96f, 0.93f, 0.85f));
            _chapter.style.fontSize = _cfg.chapter_size ?? 64f;
            _chapter.style.unityFontStyleAndWeight = FontStyle.Bold;
            _card.Add(_chapter);

            _subtitle = new Label();
            _subtitle.style.unityTextAlign = TextAnchor.MiddleCenter;
            _subtitle.style.color = UiColor.Parse(_cfg.subtitle_color, new Color(0.80f, 0.72f, 0.56f));
            _subtitle.style.fontSize = _cfg.subtitle_size ?? 34f;
            _subtitle.style.marginTop = 12;
            _card.Add(_subtitle);

            _ = AssignBg(_fog, _cfg.fog_url);
            _ = AssignBg(_card, _cfg.frame_url);
        }

        /// <summary>Set the two lines. Either may be null/empty to hide it.</summary>
        public void Set(string chapter, string subtitle)
        {
            _chapter.text = chapter ?? "";
            _chapter.style.display = string.IsNullOrEmpty(chapter) ? DisplayStyle.None : DisplayStyle.Flex;
            _subtitle.text = subtitle ?? "";
            _subtitle.style.display = string.IsNullOrEmpty(subtitle) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        /// <summary>Fade the fog + card in, hold for <c>hold_seconds</c>, then fade
        /// the whole card back out. No-op (instant return) if both lines are empty.</summary>
        public async Task RevealAsync(CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_chapter.text) && string.IsNullOrEmpty(_subtitle.text)) return;

            style.display = DisplayStyle.Flex;
            style.opacity = 1f;
            var fogIn = ScreenFx.FadeAsync(_fog, 0f, 1f, _fade, ct);
            var cardIn = ScreenFx.FadeAsync(_card, 0f, 1f, _fade, ct);
            await Task.WhenAll(fogIn, cardIn);
            if (ct.IsCancellationRequested) return;

            try { await Task.Delay(Mathf.RoundToInt(Mathf.Max(0f, _hold) * 1000f), ct); }
            catch (System.OperationCanceledException) { return; }

            await ScreenFx.FadeAsync(this, 1f, 0f, _fade, ct);
            style.display = DisplayStyle.None;
        }

        public void Hide()
        {
            style.opacity = 0f;
            style.display = DisplayStyle.None;
        }

        private async Task AssignBg(VisualElement el, string url)
        {
            if (el == null || string.IsNullOrEmpty(url) || _assets == null) return;
            try
            {
                var sprite = await _assets.LoadSpriteAsync(url, CancellationToken.None);
                if (sprite != null) el.style.backgroundImage = new StyleBackground(sprite);
            }
            catch { /* missing art is non-fatal */ }
        }

        private static VisualElement FullScreen(VisualElement el)
        {
            el.style.position = Position.Absolute;
            el.style.left = 0;
            el.style.right = 0;
            el.style.top = 0;
            el.style.bottom = 0;
            return el;
        }
    }
}
