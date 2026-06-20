using System;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// The app boot / preload splash, themed from a <see cref="BootScreenConfig"/>
    /// (manifest <c>ui.boot</c>): a backdrop, an optional centred logo, and a
    /// progress bar that creeps during the pre-download phase, tracks real
    /// progress, finishes, and fades out. Distinct from the per-chapter
    /// <see cref="LoadingScreen"/> — this is the very first thing the app shows.
    /// </summary>
    public sealed class BootScreen : VisualElement
    {
        private readonly BootScreenConfig _cfg;
        private readonly ILvnAssets _assets;
        private readonly VisualElement _logo;
        private readonly VisualElement _fill;
        private readonly Label _percent;
        private readonly LoadingProgressModel _model = new LoadingProgressModel(4f, 98.5f);
        private readonly ProgressRenderGate _gate = new ProgressRenderGate();
        private readonly bool _showPercent;

        public BootScreen(BootScreenConfig cfg, ILvnAssets assets)
        {
            _cfg = cfg ?? new BootScreenConfig();
            _assets = assets;
            _showPercent = _cfg.show_percent ?? true;

            Fill(this);
            style.backgroundColor = UiColor.Parse(_cfg.bg_color, new Color(0.04f, 0.04f, 0.055f));
            pickingMode = PickingMode.Position;

            var bg = Fill(new VisualElement());
            bg.pickingMode = PickingMode.Ignore;
            Add(bg);

            float logoW = _cfg.logo_width ?? 0.5f;
            float logoY = _cfg.logo_y ?? 0.4f;
            _logo = new VisualElement();
            _logo.style.position = Position.Absolute;
            _logo.style.left = Length.Percent(50f - logoW * 50f);
            _logo.style.width = Length.Percent(logoW * 100f);
            _logo.style.top = Length.Percent(logoY * 100f);
            _logo.style.height = Length.Percent(logoW * 100f);
            _logo.style.translate = new Translate(0f, Length.Percent(-50f), 0f);
            _logo.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
            _logo.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
            _logo.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
            _logo.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
            _logo.pickingMode = PickingMode.Ignore;
            Add(_logo);

            float barY = _cfg.bar_y ?? 0.86f;
            float barW = _cfg.bar_width ?? 0.6f;
            float barH = _cfg.bar_height ?? 0.014f;
            var bar = new VisualElement();
            bar.style.position = Position.Absolute;
            bar.style.left = Length.Percent(50f);
            bar.style.top = Length.Percent(barY * 100f);
            bar.style.width = Length.Percent(barW * 100f);
            bar.style.height = Length.Percent(barH * 100f);
            bar.style.translate = new Translate(Length.Percent(-50f), Length.Percent(-50f), 0f);
            bar.pickingMode = PickingMode.Ignore;
            Add(bar);

            var track = Fill(new VisualElement());
            track.style.backgroundColor = UiColor.Parse(_cfg.bar_track_color, new Color(1f, 1f, 1f, 0.13f));
            bar.Add(track);

            _fill = new VisualElement();
            _fill.style.position = Position.Absolute;
            _fill.style.left = 0;
            _fill.style.top = 0;
            _fill.style.bottom = 0;
            _fill.style.width = Length.Percent(0f);
            _fill.style.backgroundColor = UiColor.Parse(_cfg.bar_fill_color, new Color(0.78f, 0.63f, 0.31f));
            bar.Add(_fill);

            _percent = new Label();
            _percent.style.position = Position.Absolute;
            _percent.style.left = 0;
            _percent.style.right = 0;
            _percent.style.top = Length.Percent((barY + 0.03f) * 100f);
            _percent.style.unityTextAlign = TextAnchor.MiddleCenter;
            _percent.style.color = UiColor.Parse(_cfg.percent_color, new Color(0.81f, 0.78f, 0.74f));
            _percent.style.fontSize = 24;
            _percent.style.display = _showPercent ? DisplayStyle.Flex : DisplayStyle.None;
            _percent.pickingMode = PickingMode.Ignore;
            Add(_percent);

            _ = AssignBg(bg, _cfg.bg_url);
            _ = AssignBg(_logo, _cfg.logo_url);
            _ = AssignBg(_fill, _cfg.bar_fill_url);
        }

        /// <summary>Drive the boot bar until <paramref name="isDone"/> and the
        /// minimum hold elapse. <paramref name="progress"/> (0..1) is authoritative
        /// when supplied; otherwise the bar idle-creeps then finishes. Fades the
        /// splash out at the end.</summary>
        public async Task RunAsync(Func<bool> isDone, Func<float> progress = null, CancellationToken ct = default)
        {
            _model.Reset();
            _gate.Reset();
            style.display = DisplayStyle.Flex;
            style.opacity = 1f;

            float minSeconds = _cfg.min_seconds ?? 1.0f;
            float start = Time.unscaledTime;

            while (!ct.IsCancellationRequested)
            {
                float elapsed = Time.unscaledTime - start;
                bool done = isDone == null || isDone();

                if (progress != null)
                    _model.TickToward(Mathf.Min(0.99f, Mathf.Clamp01(progress())), Time.unscaledDeltaTime);
                else
                    _model.RaiseTo(LoadingProgressModel.IdleCreepTarget(elapsed));

                Render(false);
                if (done && elapsed >= minSeconds) break;
                try { await Task.Yield(); }
                catch (OperationCanceledException) { break; }
            }

            // Finish-fill: smooth from wherever the bar is to 100%, then hold a beat.
            float from = _model.Display;
            float t = 0f;
            while (t < 0.25f && !ct.IsCancellationRequested)
            {
                t += Time.unscaledDeltaTime;
                _model.RaiseTo(Mathf.SmoothStep(from, 1f, Mathf.Clamp01(t / 0.25f)));
                Render(false);
                try { await Task.Yield(); }
                catch (OperationCanceledException) { break; }
            }
            _model.SnapToFull();
            Render(true);
            try { await Task.Delay(120, ct); } catch (OperationCanceledException) { }

            await ScreenFx.FadeAsync(this, 1f, 0f, 0.4f, ct);
            style.display = DisplayStyle.None;
        }

        public void Hide() { style.display = DisplayStyle.None; style.opacity = 1f; }

        private void Render(bool full)
        {
            if (_gate.FillMoved(_model.FillPercent))
                _fill.style.width = Length.Percent(_model.FillPercent);
            if (_showPercent && _gate.PercentMoved(_model.Percent))
                _percent.text = (full ? 100 : _model.Percent) + "%";
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
