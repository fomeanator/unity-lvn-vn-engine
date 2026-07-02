using System;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// The drop-in app bootstrap — the whole Liminal-style flow in one component:
    /// fetch the manifest from a server, boot-prefetch its assets, raise the
    /// <see cref="NovelShell"/> (boot → carousel → name → loading → title), and on
    /// Play stream the chosen chapter's <c>.lvn</c> and run it through a wired
    /// <see cref="VnStage"/>, updating the HUD, then loop back to the carousel.
    ///
    /// <para>Scene setup: one GameObject with this component (set
    /// <see cref="ServerUrl"/> + <see cref="ShellTheme"/>) and a second GameObject
    /// with a <see cref="VnStage"/> (its own UIDocument, a lower panel
    /// <c>sortingOrder</c> than the shell) assigned to <see cref="Stage"/>.</para>
    /// </summary>
    public sealed class NovelApp : MonoBehaviour
    {
        [Tooltip("Content origin — the LVN server (manifest + scripts + assets).")]
        public string ServerUrl = "http://127.0.0.1:8000";

        [Tooltip("Offline build: load the novel from content bundled in StreamingAssets " +
                 "instead of a server. The exporter writes the manifest, scripts and assets " +
                 "under StreamingAssets/<BundleSubdir>, mirroring the server's URL paths.")]
        public bool OfflineBundled = false;

        [Tooltip("Subfolder under StreamingAssets that holds the bundled content (offline builds).")]
        public string BundleSubdir = "lvn";

        [Tooltip("The VnStage that renders chapters. Its panel sortingOrder should be below the shell's (30).")]
        public VnStage Stage;

        [Tooltip("Language code for localized chapters. When set, each chapter loads " +
                 "its sidecar string catalog <script>.<locale>.json; lines with a " +
                 "text_id resolve through it. Empty = chapters use their inline text.")]
        public string Locale = "";

        [Tooltip("Runtime ThemeStyleSheet so the shell's text has a font.")]
        public ThemeStyleSheet ShellTheme;

        [Tooltip("Optional: Resources path to a ThemeStyleSheet, loaded when ShellTheme is unset. " +
                 "Lets you wire the theme by string (e.g. \"UI/AppLoading/UnityDefaultRuntimeTheme\").")]
        public string ThemeResourcePath = "";

        public bool AskName = true;

        [Tooltip("Player/account id for server-synced saves (/v1/state?user=…). Leave " +
                 "empty to use a per-device id generated once and kept in PlayerPrefs. " +
                 "Stats always work offline; the server is a durable cross-device backup.")]
        public string UserId = "";

        [Tooltip("Shared secret gating this user's server saves (X-State-Key). MUST be the same on every device when UserId is a cross-device account; leave empty for a per-device secret.")]
        public string StateKey = "";

        [Tooltip("Live content sync: poll the server's version endpoint this often (seconds). " +
                 "Edit a .lvn or the manifest on the server and the app reloads within one interval. " +
                 "0 disables polling.")]
        public float SyncInterval = 2f;

        private CachingAssets _assets;
        private NovelShell _shell;
        private DownloadManager _downloads;
        private ContentSync _sync;
        private ILvnStateStore _state;   // stat/var persistence (local-first, optional server sync)
        private LvnChapter _currentChapter;
        private LvnTitle _currentTitle; // the playing title — for live per-title re-theming
        private string _currentScriptJson;
        private string _playerName;
        private LvnUiConfig _globalUi; // manifest.ui — the base for per-title theming
        private LvnManifest _manifest; // the live manifest (cross-chapter save routing)

        public CachingAssets Assets => _assets;
        public NovelShell Shell => _shell;

        private async void Start()
        {
            if (ShellTheme == null && !string.IsNullOrEmpty(ThemeResourcePath))
                ShellTheme = Resources.Load<ThemeStyleSheet>(ThemeResourcePath);

            var contentBase = ServerUrl;
            if (OfflineBundled)
            {
                contentBase = LocalContentBase(BundleSubdir);
                SyncInterval = 0f; // nothing to poll — content is baked into the build
                Debug.Log($"[novelapp] offline bundle → {contentBase}");
            }

            _assets = new CachingAssets(contentBase);

            // Stat/var persistence: a bundled offline build keeps stats locally; a
            // server build syncs through /v1/state (local-first, so it still plays and
            // keeps stats when the server is down).
            _state = OfflineBundled
                ? (ILvnStateStore)new LocalStateStore()
                : new HttpStateStore(contentBase, ResolveUserId(), StateKey);

            // Connectivity gate (Liminal-style): probe the server with a hard 3s
            // deadline so an unreachable server falls straight through to the offline
            // path instead of hanging on a stuck socket. A local/bundled origin is
            // always reachable. The probe pins the global offline flag so every later
            // fetch fast-fails into the disk cache.
            bool online = _assets.Loader.IsLocal || await ProbeOnlineAsync();
            if (!online) LvnNetworkStatus.MarkOffline("boot healthz: server unreachable");
            Debug.Log($"[novelapp] connectivity → {(online ? "online" : "offline")}");

            try { await _assets.WarmVersionsAsync(); } catch { /* offline: last-known index */ }

            // Manifest: fresh from the server when online (cached for next time), else
            // the last cached copy — so a previously-online install still plays offline.
            LvnManifest manifest = null;
            if (online)
            {
                try { manifest = await FetchManifestAsync(); CacheManifest(manifest); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[novelapp] manifest fetch failed: {ex.Message} — falling back to cache");
                    online = false;
                    LvnNetworkStatus.MarkOffline("manifest fetch failed");
                }
            }
            if (manifest == null) manifest = LoadCachedManifest();
            if (manifest == null)
            {
                Debug.LogError("[novelapp] offline and no cached manifest — launch once online " +
                               "(or ship an offline bundle) to cache the novel for offline play");
                return;
            }
            Debug.Log($"[novelapp] manifest: {manifest.titles?.Count ?? 0} title(s) (online={online})");

            if (Stage == null) Stage = CreateStage();
            Stage.Assets = _assets;
            Stage.Catalog = new SpriteCatalog(manifest.sprites);
            // Theme the in-game dialogue/choices from the manifest, the same way
            // the shell screens read manifest.ui — so the whole game is themeable.
            // (A title can override this per-game; applied in PlayChapterAsync.)
            _globalUi = manifest.ui;
            _manifest = manifest;
            Stage.ApplyTheme(VnThemeBuilder.From(manifest.ui, Stage.Theme));
            Stage.CrossChapterLoader = CrossChapterLoadAsync;

            _downloads = new DownloadManager(_assets.Loader);
            var prefetch = SafeBootPrefetch(manifest, online);

            _shell = NovelShell.Create(transform, 30, ShellTheme);
            _shell.Build(manifest, _assets);

            // The long-press art view hides the stage's chrome; mirror it onto the
            // shell HUD (a separate UIDocument) so the WHOLE screen is just the scene.
            Stage.ChromeHiddenChanged += hidden =>
            {
                if (_shell?.Hud != null)
                    _shell.Hud.style.visibility = hidden
                        ? UnityEngine.UIElements.Visibility.Hidden
                        : UnityEngine.UIElements.Visibility.Visible;
            };

            // Live content sync — poll the version endpoint; reload on change.
            if (SyncInterval > 0f)
            {
                _sync = new ContentSync(_assets.Loader) { IntervalSeconds = SyncInterval };
                _sync.OnChanged += OnContentChanged;
                _sync.Start();
            }

            await _shell.RunAsync(
                bootReady: () => prefetch.IsCompleted,
                chapterReady: ch => () => true,
                chapterProgress: null,
                playChapter: PlayChapterAsync,
                askName: AskName);
        }

        // Builds a VnStage on a child GameObject with its own UIDocument + panel
        // (sortingOrder below the shell's 30) so dropping a single NovelApp on an
        // empty object is enough to run the whole flow.
        private VnStage CreateStage()
        {
            var go = new GameObject("VnStage");
            go.transform.SetParent(transform, false);
            // Configure while inactive so OnEnable/Build runs only after every field
            // (notably UseCanvasScene) is set — otherwise Build() would read the
            // default and pick the wrong scene renderer.
            go.SetActive(false);
            var doc = go.AddComponent<UIDocument>();
            var ps = ScriptableObject.CreateInstance<PanelSettings>();
            ps.name = "VnStagePanel";
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(1080, 1920);
            ps.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            ps.match = 0.5f;
            ps.sortingOrder = 10;
            if (ShellTheme != null) ps.themeStyleSheet = ShellTheme;
            doc.panelSettings = ps;
            var stage = go.AddComponent<VnStage>();
            // Render the scene (bg + actors + camera) on a uGUI Canvas below this
            // UITK panel — the 60fps / Spine path. Dialogue & choices stay on UITK
            // above it. The shell content uses no click-hotspots or actor enter/exit
            // transitions (the features not yet on the Canvas path), so this is safe.
            stage.UseCanvasScene = true;
            go.SetActive(true);
            return stage;
        }

        // Build the platform-correct content base for a StreamingAssets bundle.
        // Android already yields a jar:file:// url that UnityWebRequest reads
        // straight from the APK; desktop/iOS need an explicit file:// scheme.
        private static string LocalContentBase(string sub)
        {
            var p = Application.streamingAssetsPath;
            if (!string.IsNullOrEmpty(sub)) p += "/" + sub.Trim('/');
            return p.Contains("://") ? p : "file://" + p;
        }

        // Load a chapter's localization catalog (text_id → string) for the active
        // Locale from <script>.<locale>.json. Best-effort: missing catalog → null,
        // so the chapter falls back to its inline text.
        private async Task<System.Collections.Generic.IReadOnlyDictionary<string, string>> LoadCatalogAsync(string scriptUrl)
        {
            if (string.IsNullOrEmpty(Locale) || string.IsNullOrEmpty(scriptUrl)) return null;
            var baseUrl = scriptUrl.EndsWith(".lvn") ? scriptUrl.Substring(0, scriptUrl.Length - 4) : scriptUrl;
            var url = baseUrl + "." + Locale + ".json";
            try
            {
                var json = await _assets.Loader.DownloadScriptText(url, default, singleAttempt: true);
                if (string.IsNullOrEmpty(json)) return null;
                return Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, string>>(json);
            }
            catch { return null; }
        }

        private async Task<LvnManifest> FetchManifestAsync()
        {
            var json = await _assets.Loader.DownloadScriptText("/v1/content/manifest", default, singleAttempt: true);
            return Newtonsoft.Json.JsonConvert.DeserializeObject<LvnManifest>(json) ?? new LvnManifest();
        }

        private async Task SafeBootPrefetch(LvnManifest manifest, bool online)
        {
            // Online: verify + download the boot set. Offline: warm only what's
            // already on disk (no network), so a cached install still shows its art.
            try { await _downloads.BootPrefetchAsync(manifest, online, default); }
            catch { /* best-effort — missing boot art is non-fatal */ }
        }

        // Probe the server's /healthz with a hard 3s deadline. Token-based, because
        // UnityWebRequest.timeout doesn't reliably interrupt a DNS/TLS stall — the
        // difference between an instant offline fallback and a ~30s boot hang.
        private async Task<bool> ProbeOnlineAsync()
        {
            try
            {
                using var probe = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3));
                return await _assets.Loader.HealthzAsync("/healthz", probe.Token);
            }
            catch { return false; }
        }

        // ── Offline manifest cache ───────────────────────────────────────────────
        // The manifest is cached locally on every successful online fetch and read
        // back when the server is unreachable, so a previously-online install boots
        // straight into the menu offline (chapters then play from the disk cache).
        private const string ManifestCacheKey = "lvn_manifest_cache";

        private static void CacheManifest(LvnManifest m)
        {
            if (m == null) return;
            try
            {
                PlayerPrefs.SetString(ManifestCacheKey, Newtonsoft.Json.JsonConvert.SerializeObject(m));
                PlayerPrefs.Save();
            }
            catch { /* cache write best-effort */ }
        }

        private static LvnManifest LoadCachedManifest()
        {
            try
            {
                var json = PlayerPrefs.GetString(ManifestCacheKey, null);
                return string.IsNullOrEmpty(json)
                    ? null
                    : Newtonsoft.Json.JsonConvert.DeserializeObject<LvnManifest>(json);
            }
            catch { return null; }
        }

        // Play a title from its entry point and KEEP GOING: when a chapter finishes,
        // the next one (by number) follows seamlessly — the player reads the whole
        // novel without bouncing off the carousel between episodes. A progress
        // marker remembers the furthest chapter started, so re-entering the title
        // continues there (and the in-chapter autosave restores the exact line);
        // finishing the last chapter clears it so a replay starts clean.
        private async Task PlayChapterAsync(LvnTitle title, LvnChapter chapter, string playerName)
        {
            var resume = LvnProgress.Current(title);
            if (resume != null) chapter = resume;
            while (chapter != null)
            {
                LvnProgress.SetCurrent(title, chapter);
                var finished = await PlayOneChapterAsync(title, chapter, playerName);
                if (finished == null) break; // left mid-chapter (cancel/error) → carousel
                // A cross-chapter save load can land the player in another title —
                // continue along whichever title the finished chapter belongs to.
                var (owner, _) = FindChapterByScriptUrl(finished.script_url);
                if (owner != null) title = owner;
                var next = NextChapterOf(title, finished);
                if (next == null)
                {
                    LvnProgress.ClearCurrent(title); // the novel is complete — replays restart
                    break;
                }
                chapter = next;
            }
        }

        // The next chapter by number, or null when this was the last one.
        private static LvnChapter NextChapterOf(LvnTitle title, LvnChapter current)
        {
            if (title?.seasons == null || current == null) return null;
            LvnChapter best = null;
            foreach (var s in title.seasons)
            {
                if (s?.chapters == null) continue;
                foreach (var c in s.chapters)
                {
                    if (c == null || c.number <= current.number) continue;
                    if (best == null || c.number < best.number) best = c;
                }
            }
            return best;
        }

        // Stream one chapter's script and run it through the VnStage, driving the
        // HUD until it ends. Returns the chapter that actually FINISHED (it can
        // differ from the requested one — a cross-chapter save load switches the
        // stage mid-play), or null when the player left mid-chapter.
        private async Task<LvnChapter> PlayOneChapterAsync(LvnTitle title, LvnChapter chapter, string playerName)
        {
            if (Stage == null || chapter == null || string.IsNullOrEmpty(chapter.script_url))
            {
                await Task.Delay(400);
                return null;
            }

            // Clean the stage at the START too — not just on the previous chapter's
            // end — so a leftover actor/animation never lingers while this chapter's
            // script is still downloading.
            Stage.ClearStage();

            // Per-title theme: engine defaults → global manifest.ui → this title's ui.
            // Rebuilt fresh each entry so a previous title's look never leaks in.
            var theme = VnThemeBuilder.From(_globalUi, new VnTheme());
            if (title?.ui != null) theme = VnThemeBuilder.From(title.ui, theme);
            Stage.ApplyTheme(theme);

            // Offline decision layer (ported from the Liminal client): decide how
            // to enter the chapter from connectivity + what's on disk. A local
            // bundle reports everything cached/reachable, so it plays instantly;
            // an online client degrades gracefully and never hangs.
            bool online = _assets.Loader.IsLocal || !LvnNetworkStatus.IsOffline;
            var readiness = OfflinePolicy.ComputeReadiness(
                _assets.Loader.IsScriptCached(chapter.script_url),
                chapter.assets,
                _assets.Loader.IsAssetCached);
            var plan = ChapterEntryPlan.From(online, in readiness);
            if (!plan.CanPlay)
            {
                Debug.LogWarning($"[novelapp] chapter '{chapter.id}' unavailable offline (script not cached)");
                await Task.Delay(300);
                return null;
            }

            string json;
            try { json = await _assets.Loader.DownloadScriptCached(chapter.script_url); }
            catch (Exception ex) { Debug.LogWarning($"[novelapp] script fetch failed: {ex.Message}"); return null; }
            if (string.IsNullOrEmpty(json)) { Debug.LogWarning($"[novelapp] no script for '{chapter.id}'"); return null; }

            _currentChapter = chapter;
            _currentTitle = title;
            _playerName = playerName;
            _currentScriptJson = json;
            Stage.Strings = await LoadCatalogAsync(chapter.script_url); // localization (null → inline text)
            // Carry this title's persisted stats into the chapter (relationships, route,
            // memory flags…). The imported global defaults are `default:true`, so they
            // don't overwrite these; a fresh game starts empty. The store is local-first
            // (offline-safe) and, when a server is configured, syncs through /v1/state.
            Stage.SeedVars = await _state.LoadVarsAsync(title?.id, default);

            // The genre-standard restart semantics: picking a chapter from the
            // picker resets the variables to what they were when that chapter was
            // FIRST entered — stats from the future must not leak into the past
            // and mis-gate its choices. The live state store rolls back with it,
            // so a later stat sync doesn't resurrect the discarded future.
            bool restart = LvnProgress.TakeRestart(title?.id, chapter.id);
            if (restart)
            {
                Stage.SeedVars = LvnProgress.Checkpoint(title?.id, chapter.id)
                                 ?? new Newtonsoft.Json.Linq.JObject();
                await _state.SaveVarsAsync(title?.id, Stage.SeedVars, default);
                LvnSaveStore.Delete(title?.id, LvnSaveStore.AutoSlot);
                Debug.Log($"[novelapp] restarting '{chapter.id}' from its entry checkpoint");
            }

            // Resume where the player actually was: a mid-chapter autosave for THIS
            // script (written on choices/every few lines/app pause) beats replaying
            // the chapter from the top. A finished chapter's autosave was deleted on
            // OnEnd, so replays start clean.
            var autosave = LvnSaveStore.Get(title?.id, LvnSaveStore.AutoSlot);
            bool resuming = !restart && autosave?.Snap != null
                            && autosave.Snap.ScriptUrl == chapter.script_url
                            && !autosave.Snap.Finished;

            // A FRESH entry (chapter transition, picker restart, first launch) is
            // the moment the entry checkpoint captures; a mid-chapter resume must
            // NOT overwrite it with mid-chapter stats.
            if (!resuming)
                LvnProgress.SaveCheckpoint(title?.id, chapter.id, Stage.SeedVars);

            Stage.SetSaveContext(title?.id, chapter.id, chapter.script_url);
            Stage.Play(json);
            if (Stage.Player != null && !string.IsNullOrEmpty(playerName))
                Stage.Player.Vars["player"] = playerName;

            if (resuming)
            {
                Debug.Log($"[novelapp] resuming '{chapter.id}' from autosave (@{autosave.Snap.Index})");
                Stage.RestoreSnapshot(autosave.Snap);
                if (Stage.Player != null && !string.IsNullOrEmpty(playerName))
                    Stage.Player.Vars["player"] = playerName;
            }

            // Drive the HUD percent until the chapter ends — or the player asks
            // out (the quick menu's Exit; position already autosaved, so the
            // carousel's Continue leads straight back to this line).
            while (Stage.Player != null && !Stage.Player.Finished && !Stage.ExitRequested)
            {
                _shell.Hud.SetProgress(Stage.Player.Index, Stage.Player.Count);
                try { await Task.Yield(); }
                catch (OperationCanceledException) { break; }
            }
            bool exited = Stage.ExitRequested;
            Stage.ClearExitRequest();
            if (exited) Stage.ClearStage(); // leave nothing behind under the carousel
            // Persist the chapter's ending state so the next chapter (and the next
            // session) resume with the same stats — whether it finished or the player
            // left mid-chapter (the loop also breaks on cancellation).
            if (Stage.Player != null) await _state.SaveVarsAsync(title?.id, VarsToJObject(Stage.Player.Vars), default);
            _shell.Hud.SetProgress(1, 1);
            // The chapter that actually played to the end — a cross-chapter save
            // load may have switched the stage away from the requested one.
            bool finished = Stage.Player != null && Stage.Player.Finished;
            var played = _currentChapter ?? chapter;
            _currentChapter = null;
            _currentTitle = null;
            // Free the finished chapter's decoded art (a chapter can hold dozens of
            // full-res RGBA sprites). UI art — covers, theme skins under ui/ — stays
            // warm; the disk cache is intact so the next entry re-decodes quickly.
            _assets.Loader.UnloadWhere(u => u.Contains("/art/") || u.Contains("/bg/"));
            return finished ? played : null;
        }

        // Cross-chapter save routing: a slot taken in another chapter resolves to
        // its chapter by script url, fetches that script, plays it and restores —
        // all in place, while the shell's play-loop keeps driving whatever player
        // the stage currently holds. Wired into VnStage.CrossChapterLoader.
        private async Task<bool> CrossChapterLoadAsync(LvnSaveSlot slot)
        {
            var url = slot?.Snap?.ScriptUrl;
            if (string.IsNullOrEmpty(url) || Stage == null) return false;
            var (title, chapter) = FindChapterByScriptUrl(url);
            if (chapter == null)
            {
                Debug.LogWarning($"[novelapp] save points at unknown chapter: {url}");
                return false;
            }

            string json;
            try { json = await _assets.Loader.DownloadScriptCached(url); }
            catch (Exception ex) { Debug.LogWarning($"[novelapp] cross-chapter fetch failed: {ex.Message}"); return false; }
            if (string.IsNullOrEmpty(json)) return false;

            Stage.ClearStage();
            Stage.Strings = await LoadCatalogAsync(url);
            Stage.SeedVars = await _state.LoadVarsAsync(title?.id, default);
            Stage.SetSaveContext(title?.id, chapter.id, url);
            Stage.Play(json);
            if (Stage.Player != null && !string.IsNullOrEmpty(_playerName))
                Stage.Player.Vars["player"] = _playerName;
            Stage.RestoreSnapshot(slot.Snap);
            _currentChapter = chapter;
            _currentTitle = title ?? _currentTitle;
            _currentScriptJson = json;
            LvnProgress.SetCurrent(_currentTitle, chapter); // continue follows the jump
            Debug.Log($"[novelapp] loaded save into '{chapter.id}' (@{slot.Snap.Index})");
            return true;
        }

        private (LvnTitle title, LvnChapter chapter) FindChapterByScriptUrl(string scriptUrl)
        {
            if (_manifest?.titles == null) return (null, null);
            foreach (var t in _manifest.titles)
            {
                if (t?.seasons == null) continue;
                foreach (var s in t.seasons)
                {
                    if (s?.chapters == null) continue;
                    foreach (var c in s.chapters)
                        if (c != null && c.script_url == scriptUrl)
                            return (t, c);
                }
            }
            return (null, null);
        }

        // The save identity for /v1/state. An explicit UserId (an account) wins; else
        // a per-device id generated once and kept in PlayerPrefs.
        private string ResolveUserId()
        {
            if (!string.IsNullOrEmpty(UserId)) return UserId;
            var id = PlayerPrefs.GetString("lvn_user", "");
            if (string.IsNullOrEmpty(id))
            {
                id = System.Guid.NewGuid().ToString("N");
                PlayerPrefs.SetString("lvn_user", id);
                PlayerPrefs.Save();
            }
            return id;
        }

        // Snapshot the player's live variables as a JObject the state store persists.
        private static Newtonsoft.Json.Linq.JObject VarsToJObject(
            System.Collections.Generic.IReadOnlyDictionary<string, Newtonsoft.Json.Linq.JToken> vars)
        {
            var jo = new Newtonsoft.Json.Linq.JObject();
            if (vars != null)
                foreach (var kv in vars)
                    jo[kv.Key] = kv.Value?.DeepClone();
            return jo;
        }

        // Mobile: persist stats when the app is backgrounded / quit mid-chapter.
        // Fire-and-forget — the store writes its LOCAL cache synchronously before the
        // first await, so stats are safe even if the process is suspended immediately.
        private void OnApplicationPause(bool paused)
        {
            if (paused && _state != null && Stage?.Player != null && _currentTitle != null)
                _ = _state.SaveVarsAsync(_currentTitle.id, VarsToJObject(Stage.Player.Vars), default);
            // Position too, not just stats — so a suspended app resumes on the same
            // line (the autosave slot; SaveToSlot is synchronous PlayerPrefs).
            if (paused) Stage?.AutosaveNow();
        }

        // Server content changed: refresh the version index, re-apply the manifest
        // (carousel rebuilds), and hot-reload the open chapter if its script moved.
        private async void OnContentChanged()
        {
            Debug.Log("[novelapp] content changed — reloading");
            try { await _assets.WarmVersionsAsync(); } catch { /* offline */ }

            LvnManifest manifest;
            try { manifest = await FetchManifestAsync(); }
            catch (Exception ex) { Debug.LogWarning($"[novelapp] live manifest fetch failed: {ex.Message}"); return; }
            CacheManifest(manifest); // keep the offline copy fresh on every live update
            _shell?.ApplyLiveUpdate(manifest);
            _globalUi = manifest.ui;
            _manifest = manifest; // cross-chapter routing follows the live manifest
            if (Stage != null)
            {
                Stage.Catalog = new SpriteCatalog(manifest.sprites);
                // Re-theme live — rebuilt fresh from the NEW manifest: engine
                // defaults → global ui → the playing title's ui override (matched
                // by id in the new manifest, so per-title edits take effect). Safe
                // mid-line: VnStage.ApplyTheme restores the visible line/choices.
                var theme = VnThemeBuilder.From(manifest.ui, new VnTheme());
                LvnTitle liveTitle = null;
                if (_currentTitle != null && manifest.titles != null)
                    liveTitle = manifest.titles.Find(t => t != null && t.id == _currentTitle.id);
                if (liveTitle?.ui != null) theme = VnThemeBuilder.From(liveTitle.ui, theme);
                Stage.ApplyTheme(theme);
            }

            if (_currentChapter == null || Stage == null || Stage.Player == null || Stage.Player.Finished)
                return;

            // Fetch the script FRESH (not the version-pinned disk cache, which can
            // hand back the old text when reacting to a live edit — the whole point
            // here is to apply what just changed). The disk cache is refreshed in
            // the background so an offline replay of the new version still works.
            string json;
            try { json = await _assets.Loader.DownloadScriptText(_currentChapter.script_url); }
            catch { return; }
            if (string.IsNullOrEmpty(json)) return;
            if (json == _currentScriptJson)
            {
                // The script didn't change — only assets did (a replaced sprite or
                // background). Re-apply the visible stage in place so the new art shows
                // live, without restarting the chapter. The version index was just
                // re-warmed, so each sprite reloads under its new content hash.
                if (Stage.Player != null && !Stage.Player.Finished)
                    Stage.Player.ReplayVisuals(Stage.Player.Index + 1);
                return;
            }
            _assets.Loader.RefreshScriptInBackground(_currentChapter.script_url);

            _currentScriptJson = json;
            // A non-structural edit (reworded line, tweaked emotion/position) keeps
            // the chapter playing exactly where it is; only a changed command
            // structure forces a restart from the top.
            if (Stage.TryHotSwap(json))
            {
                Debug.Log($"[novelapp] hot-swapped chapter '{_currentChapter.id}' in place (kept position)");
            }
            else
            {
                Stage.Play(json);
                if (Stage.Player != null && !string.IsNullOrEmpty(_playerName))
                    Stage.Player.Vars["player"] = _playerName;
                Debug.Log($"[novelapp] reloaded chapter '{_currentChapter.id}' (structure changed — restarted)");
            }
        }

        private void OnDestroy() => _sync?.Stop();
    }
}
