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
    /// The full novel shell — the loop that ties the manifest-driven screens
    /// together: <b>boot splash → title carousel → (name input) → chapter loading
    /// → title card → play → back to the carousel</b>. Build it on a
    /// <see cref="UIDocument"/>, hand it an <see cref="LvnManifest"/> + an
    /// <see cref="ILvnAssets"/>, and a <c>playChapter</c> delegate that runs the
    /// actual chapter (e.g. drives a <c>VnStage</c>) and returns when it ends.
    /// Everything visual is themed from <c>manifest.ui</c>.
    /// </summary>
    public sealed class NovelShell : MonoBehaviour
    {
        public BootScreen Boot { get; private set; }
        public TitleCarousel Carousel { get; private set; }
        public NameInputScreen NameInput { get; private set; }
        public LoadingScreen Loading { get; private set; }
        public TitleCard Title { get; private set; }
        public GameHud Hud { get; private set; }

        private UIDocument _doc;
        private VisualElement _root;
        private LvnManifest _manifest;
        private ILvnAssets _assets;
        private string _playerName;

        /// <summary>The shell's UIDocument. Assign
        /// <c>Document.panelSettings.themeStyleSheet</c> a runtime theme so the
        /// screens' text has a font (UI Toolkit renders no text without one).</summary>
        public UIDocument Document => _doc;

        /// <summary>Create a shell on a fresh GameObject with its own UIDocument.
        /// Pass a <paramref name="theme"/> (a runtime ThemeStyleSheet) so text
        /// renders — without one UI Toolkit draws shapes but no glyphs.</summary>
        public static NovelShell Create(Transform parent = null, int sortingOrder = 30, ThemeStyleSheet theme = null)
        {
            var go = new GameObject("NovelShell", typeof(NovelShell));
            if (parent != null) go.transform.SetParent(parent, false);
            var shell = go.GetComponent<NovelShell>();
            shell.InitDocument(sortingOrder, theme);
            return shell;
        }

        private void InitDocument(int sortingOrder, ThemeStyleSheet theme = null)
        {
            var ps = ScriptableObject.CreateInstance<PanelSettings>();
            ps.name = "NovelShellPanel";
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(1080, 1920);
            ps.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            ps.match = 0.5f;
            ps.sortingOrder = sortingOrder;
            if (theme != null) ps.themeStyleSheet = theme;
            _doc = gameObject.GetComponent<UIDocument>();
            if (_doc == null) _doc = gameObject.AddComponent<UIDocument>();
            _doc.panelSettings = ps;
        }

        /// <summary>Build the screen tree from the manifest. Idempotent.</summary>
        public void Build(LvnManifest manifest, ILvnAssets assets)
        {
            _manifest = manifest ?? new LvnManifest();
            _assets = assets;
            var ui = _manifest.ui ?? new LvnUiConfig();

            if (_doc == null) InitDocument(30);
            _root = _doc.rootVisualElement;
            _root.Clear();
            _root.style.flexGrow = 1;

            Boot = new BootScreen(ui.boot, assets); Boot.Hide(); Add(Boot);
            Carousel = new TitleCarousel(_manifest.titles, ui.carousel, assets); Hide(Carousel); Add(Carousel);
            NameInput = new NameInputScreen(ui.name_input, assets); Add(NameInput);
            Loading = new LoadingScreen(ui.loading, assets); Loading.Hide(); Add(Loading);
            Title = new TitleCard(ui.title, assets); Title.Hide(); Add(Title);
            Hud = new GameHud(ui.hud, assets); Hide(Hud); Add(Hud);
        }

        /// <summary>Apply a live content update — swap in a freshly-fetched
        /// manifest and re-render the data-driven screens (the carousel rebuilds
        /// its deck, keeping the selected title). Cheap and safe to call any time;
        /// the host's content-sync loop calls it when the server version changes.</summary>
        public void ApplyLiveUpdate(LvnManifest manifest)
        {
            if (manifest == null) return;
            _manifest = manifest;
            Carousel?.SetTitles(manifest.titles);
        }

        /// <summary>Run the whole loop. <paramref name="bootReady"/> gates the boot
        /// splash; <paramref name="chapterReady"/> (optional) gates each chapter's
        /// loading bar; <paramref name="playChapter"/> plays the chosen chapter and
        /// returns when it finishes. Loops back to the carousel after each chapter.</summary>
        public async Task RunAsync(
            Func<bool> bootReady = null,
            Func<LvnChapter, Func<bool>> chapterReady = null,
            Func<LvnChapter, Func<float>> chapterProgress = null,
            Func<LvnTitle, LvnChapter, string, Task> playChapter = null,
            bool askName = true,
            CancellationToken ct = default)
        {
            if (_root == null) throw new InvalidOperationException("Call Build() before RunAsync().");

            Boot.Hide();
            ShowOnly(); // hide all
            // ── boot splash ──
            Show(Boot);
            await Boot.RunAsync(bootReady ?? (() => true), null, ct);
            Hide(Boot);

            while (!ct.IsCancellationRequested)
            {
                // ── title carousel: wait for Play ──
                Carousel.RefreshProgress(); // progress moved while a chapter played
                Show(Carousel);
                int idx = await WaitForPlay(ct);
                if (ct.IsCancellationRequested) return;
                Hide(Carousel);

                var title = (_manifest.titles != null && idx >= 0 && idx < _manifest.titles.Count)
                    ? _manifest.titles[idx] : null;
                var chapter = FirstChapter(title);

                // ── name input (once) ──
                if (askName && string.IsNullOrEmpty(_playerName) && (_manifest.ui?.name_input != null))
                {
                    try { _playerName = await NameInput.AskAsync(ct); }
                    catch (OperationCanceledException) { return; }
                }

                // ── chapter loading ──
                Show(Loading);
                var ready = chapterReady?.Invoke(chapter) ?? (() => true);
                var prog = chapterProgress?.Invoke(chapter);
                await Loading.RunAsync(ready, prog, ct, bgUrl: chapter?.bg_url);
                await Loading.FadeOutAsync(0.3f, ct);
                Loading.Hide();

                // ── chapter title card ──
                if (chapter != null)
                {
                    Title.Set(ChapterLine(chapter), title?.name);
                    Show(Title);
                    await Title.RevealAsync(ct);
                    Title.Hide();
                }

                // ── play ──
                if (playChapter != null && chapter != null)
                {
                    Show(Hud);
                    try { await playChapter(title, chapter, _playerName); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { Debug.LogWarning($"[shell] chapter play failed: {ex.Message}"); }
                    Hide(Hud);
                }
            }
        }

        /// <summary>Auto-start a title by id without racing the boot splash — the
        /// request is honoured the moment the carousel takes control. Returns false
        /// if no title carries that id. Pairs with <see cref="TitleCarousel.RequestPlay"/>.</summary>
        public bool RequestPlay(string titleId)
        {
            if (_manifest?.titles == null || Carousel == null) return false;
            for (int i = 0; i < _manifest.titles.Count; i++)
                if (_manifest.titles[i]?.id == titleId) { Carousel.RequestPlay(i); return true; }
            return false;
        }

        private Task<int> WaitForPlay(CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(int i) { Carousel.OnPlay -= Handler; tcs.TrySetResult(i); }
            Carousel.OnPlay += Handler;
            // Honour a play requested before we got here (auto-start / deep-link fired
            // during the boot splash, when OnPlay had no subscriber yet).
            if (Carousel.TryConsumePendingPlay(out int pending))
            {
                Carousel.OnPlay -= Handler;
                tcs.TrySetResult(pending);
                return tcs.Task;
            }
            ct.Register(() => { Carousel.OnPlay -= Handler; tcs.TrySetCanceled(); });
            return tcs.Task;
        }

        /// <summary>The first playable chapter of a title (lowest non-negative
        /// chapter number across its seasons), or null.</summary>
        internal static LvnChapter FirstChapter(LvnTitle title)
        {
            if (title?.seasons == null) return null;
            LvnChapter best = null;
            foreach (var s in title.seasons)
            {
                if (s?.chapters == null) continue;
                foreach (var c in s.chapters)
                {
                    if (c == null) continue;
                    if (best == null || c.number < best.number) best = c;
                }
            }
            return best;
        }

        private static string ChapterLine(LvnChapter c) =>
            c == null ? "" : (c.number > 0 ? $"Chapter {c.number}" : "");

        private void Add(VisualElement el)
        {
            el.style.position = Position.Absolute;
            el.style.left = 0; el.style.right = 0; el.style.top = 0; el.style.bottom = 0;
            _root.Add(el);
        }

        private void ShowOnly()
        {
            Hide(Boot); Hide(Carousel); Hide(Loading); Hide(Title); Hide(Hud);
            NameInput.Hide();
        }

        private static void Show(VisualElement el) { if (el != null) el.style.display = DisplayStyle.Flex; }
        private static void Hide(VisualElement el) { if (el != null) el.style.display = DisplayStyle.None; }
    }
}
