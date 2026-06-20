using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// The novel's loading screen, built entirely in code and themed from a
    /// <see cref="LoadingScreenConfig"/> (manifest <c>ui.loading</c>): backdrop,
    /// dark scrim, optional fog, and a progress bar (optional track/fill/frame
    /// art, else solid colours) with percent, current-file and rotating-tip
    /// labels. The bar maths live in the pure <see cref="LoadingProgressModel"/>;
    /// this only renders. Drop it into any <c>UIDocument.rootVisualElement</c>
    /// (or another full-screen element) and <see cref="RunAsync"/> until your
    /// loading predicate is done.
    /// </summary>
    public sealed class LoadingScreen : VisualElement
    {
        private readonly LoadingScreenConfig _cfg;
        private readonly ILvnAssets _assets;

        private readonly VisualElement _bg;
        private readonly VisualElement _fog;
        private readonly VisualElement _scrim;
        private readonly VisualElement _fill;
        private readonly Label _percent;
        private readonly Label _file;
        private readonly Label _hint;

        private readonly LoadingProgressModel _model;
        private readonly ProgressRenderGate _gate = new ProgressRenderGate();
        private readonly float _fillSpan;
        private readonly float _scrimOpacity;

        public LoadingScreen(LoadingScreenConfig cfg, ILvnAssets assets)
        {
            _cfg = cfg ?? new LoadingScreenConfig();
            _assets = assets;
            _fillSpan = _cfg.fill_span_percent ?? LoadingProgressModel.FillSpanPercent;
            _model = new LoadingProgressModel(fillSpanPercent: _fillSpan);
            _scrimOpacity = _cfg.scrim_opacity ?? 0.65f;

            Fill(this);
            style.backgroundColor = UiColor.Parse(_cfg.bg_color, Color.black);
            pickingMode = PickingMode.Position; // swallow taps under the loader

            _bg = Fill(new VisualElement());
            Add(_bg);

            _scrim = Fill(new VisualElement());
            _scrim.style.backgroundColor = UiColor.Parse(_cfg.scrim_color, Color.black);
            _scrim.style.opacity = _scrimOpacity;
            Add(_scrim);

            _fog = Fill(new VisualElement());
            _fog.style.opacity = 0f;
            _fog.pickingMode = PickingMode.Ignore;
            Add(_fog);

            // ── progress bar (centred on bar_x/bar_y) ──
            float barX = _cfg.bar_x ?? 0.5f;
            float barY = _cfg.bar_y ?? 0.82f;
            float barW = _cfg.bar_width ?? 0.7f;
            float barH = _cfg.bar_height ?? 0.018f;

            var bar = new VisualElement();
            bar.style.position = Position.Absolute;
            bar.style.left = Length.Percent(barX * 100f);
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

            var frame = Fill(new VisualElement());
            frame.pickingMode = PickingMode.Ignore;
            bar.Add(frame);

            // ── labels (placed relative to the bar) ──
            _hint = MakeLabel(barY - 0.07f, UiColor.Parse(_cfg.hint_color, new Color(0.81f, 0.78f, 0.74f)), 24);
            _hint.style.display = (_cfg.show_hint ?? true) ? DisplayStyle.Flex : DisplayStyle.None;
            Add(_hint);

            _percent = MakeLabel(barY + 0.02f, UiColor.Parse(_cfg.percent_color, Color.white), 26);
            _percent.style.display = (_cfg.show_percent ?? true) ? DisplayStyle.Flex : DisplayStyle.None;
            Add(_percent);

            _file = MakeLabel(barY + 0.055f, UiColor.Parse(_cfg.file_color, new Color(0.60f, 0.58f, 0.54f)), 18);
            _file.style.display = (_cfg.show_file ?? true) ? DisplayStyle.Flex : DisplayStyle.None;
            Add(_file);

            // Static art from the config (async — non-fatal if missing).
            _ = AssignBg(_fog, _cfg.fog_url);
            _ = AssignBg(track, _cfg.bar_track_url);
            _ = AssignBg(_fill, _cfg.bar_fill_url);
            _ = AssignBg(frame, _cfg.bar_frame_url);
            if (!string.IsNullOrEmpty(_cfg.bg_url)) _ = AssignBg(_bg, _cfg.bg_url);
        }

        /// <summary>Drives the loading bar until <paramref name="isDone"/> returns
        /// true and at least <c>min_seconds</c> have elapsed. <paramref name="progress"/>
        /// (0..1), when supplied, is the authoritative source; otherwise the bar
        /// creeps on a time floor and snaps to full when done. <paramref name="bgUrl"/>
        /// overrides the config backdrop (e.g. the chapter's loading bg);
        /// <paramref name="fileLabel"/> optionally feeds the under-bar filename.</summary>
        public async Task RunAsync(
            Func<bool> isDone,
            Func<float> progress = null,
            CancellationToken ct = default,
            string bgUrl = null,
            Func<string> fileLabel = null)
        {
            _model.Reset();
            _gate.Reset();
            style.display = DisplayStyle.Flex;
            style.opacity = 1f;
            SetText(_percent, "");
            SetText(_file, "");
            SetText(_hint, "");
            SetFill(0f);

            if (!string.IsNullOrEmpty(bgUrl)) _ = AssignBg(_bg, bgUrl);

            var tips = _cfg.tips;
            float minSeconds = _cfg.min_seconds ?? 0f;
            float start = Time.unscaledTime;
            float lastTip = -999f;
            int tipIdx = 0;

            while (true)
            {
                if (ct.IsCancellationRequested) break;
                float now = Time.unscaledTime;
                float elapsed = now - start;
                bool done = isDone == null || isDone();

                // rotate tips
                if (tips != null && tips.Length > 0 && now - lastTip >= 3.5f)
                {
                    SetText(_hint, tips[tipIdx % tips.Length] ?? "");
                    tipIdx++;
                    lastTip = now;
                }

                float target = progress != null
                    ? Mathf.Clamp01(progress())
                    : (minSeconds > 0f ? Mathf.Clamp01(elapsed / minSeconds) : 1f);

                if (done && elapsed >= minSeconds) _model.SnapToFull();
                else _model.TickToward(Mathf.Min(target, 0.97f), Time.unscaledDeltaTime);

                SetFill(_model.FillPercent);
                if (_percent != null && _gate.PercentMoved(_model.Percent))
                    SetText(_percent, ((done && elapsed >= minSeconds) ? 100 : _model.Percent) + "%");

                if (_file != null && fileLabel != null)
                {
                    var text = (!done) ? (fileLabel() ?? "") : "";
                    if (_gate.LabelChanged(text)) SetText(_file, text);
                }

                if (done && elapsed >= minSeconds) { SetFill(_fillSpan); break; }

                try { await Task.Yield(); }
                catch (OperationCanceledException) { break; }
            }
        }

        /// <summary>Fade the whole loader out (call after RunAsync before swapping
        /// to the title card or the scene).</summary>
        public Task FadeOutAsync(float seconds = 0.4f, CancellationToken ct = default) =>
            ScreenFx.FadeAsync(this, 1f, 0f, seconds, ct);

        public void Hide()
        {
            style.display = DisplayStyle.None;
            style.opacity = 1f;
        }

        private void SetFill(float fillPercent)
        {
            if (_fill != null && _gate.FillMoved(fillPercent))
                _fill.style.width = Length.Percent(Mathf.Clamp(fillPercent, 0f, _fillSpan));
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

        private static Label MakeLabel(float topFraction, Color color, float size)
        {
            var l = new Label();
            l.style.position = Position.Absolute;
            l.style.left = 0;
            l.style.right = 0;
            l.style.top = Length.Percent(topFraction * 100f);
            l.style.unityTextAlign = TextAnchor.MiddleCenter;
            l.style.color = color;
            l.style.fontSize = size;
            l.pickingMode = PickingMode.Ignore;
            return l;
        }

        private static VisualElement Fill(VisualElement el)
        {
            el.style.position = Position.Absolute;
            el.style.left = 0;
            el.style.right = 0;
            el.style.top = 0;
            el.style.bottom = 0;
            return el;
        }

        private static void SetText(Label l, string t) { if (l != null) l.text = t; }
    }
}
