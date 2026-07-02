using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// The drop-in stage: a <see cref="MonoBehaviour"/> that composes the
    /// reference layers (background → actors → dialogue → choices) into a
    /// <see cref="UIDocument"/> and plays a <c>.lvn</c> through an
    /// <see cref="LvnPlayer"/>. Implements <see cref="ILvnStage"/> itself, so
    /// dropping it on a GameObject with a UIDocument and a script TextAsset is a
    /// playable game. Swap <see cref="Theme"/> to restyle, assign
    /// <see cref="Assets"/> to load art.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class VnStage : MonoBehaviour, ILvnStage
    {
        [Tooltip("Look-and-feel for the built-in components.")]
        public VnTheme Theme = new VnTheme();

        [Tooltip("A .lvn file as a TextAsset; played on enable. Optional — call Play() instead.")]
        public TextAsset Script;

        /// <summary>Resolves <c>sprite_url</c>s to sprites. Null → solid-colour
        /// backgrounds and no character art. Assign in code before play.</summary>
        public ILvnAssets Assets;

        /// <summary>Optional sprite/entity catalog (from <c>manifest.sprites</c>).
        /// When set, <c>actor</c>/<c>obj</c>/<c>bg id="..."</c> resolve their
        /// layers (with conditional <c>when</c> display) from it instead of raw
        /// urls. Assign from the host's manifest before play.</summary>
        public SpriteCatalog Catalog;

        /// <summary>Optional localization catalog (<c>text_id</c> → string) for the
        /// active language, applied to each chapter's player. Assign before Play.</summary>
        public System.Collections.Generic.IReadOnlyDictionary<string, string> Strings;

        [Tooltip("Optional content folder. If set and Assets is unwired, the stage " +
                 "loads sprites from here via DirectoryAssets — so a scene plays with " +
                 "art straight from Play, no code. Editor/standalone file paths.")]
        public string ContentRoot;

        [Tooltip("Render the scene (background + actors + camera) on a uGUI Canvas " +
                 "instead of UI Toolkit — the 60fps / Spine path. Dialogue and choices " +
                 "stay on UI Toolkit above it. Off by default (UITK scene).")]
        public bool UseCanvasScene;

        private VisualElement _world;      // the camera target (UITK path)
        private ISceneRenderer _renderer;  // bg + actors + camera, renderer-agnostic
        private ParticleField _particles;
        private DialogueBox _dialogue;
        private ChoiceList _choices;
        private VisualElement _labelLayer; // reactive HUD/stat text overlay (the `text` op)
        private readonly Dictionary<string, Label> _labelEls = new Dictionary<string, Label>();
        private readonly Dictionary<string, string> _labelTmpl = new Dictionary<string, string>(); // id → live `{expr}` template
        private FxLayer _fx;
        private StageAudio _audio;
        private StageMenu _menu;
        private Dictionary<string, CastEntity> _cast;
        private readonly Dictionary<string, LvnAnim> _talkAnims = new Dictionary<string, LvnAnim>(); // actor id → lip-sync anim
        private LvnPlayer _player;
        private CancellationTokenSource _cts;
        private bool _awaitingTap;
        private bool _awaitingWait;
        // Current on-screen beat — restored after a live theme rebuild so ApplyTheme
        // is safe to call mid-line (realtime theming keeps the line/choices visible).
        private bool _sayUp;
        private IReadOnlyList<LvnOption> _curChoices;

        /// <summary>Public access to the underlying player for save/load.</summary>
        public LvnPlayer Player => _player;

        private readonly List<(string who, string text, string style)> _backlog
            = new List<(string, string, string)>();

        /// <summary>Read-only access to the dialogue history.</summary>
        public IReadOnlyList<(string who, string text, string style)> Backlog => _backlog;

        private bool _built;
        private VisualElement _uiRoot; // panel root — normalizes the pointer position
        // Clickable hotspots for the Canvas scene (which has no uGUI raycaster) —
        // hit-tested in OnPointerDown against each actor's real on-screen RectTransform
        // (so the clickable area matches the visible sprite exactly).
        private readonly List<(string id, System.Action onClick)> _hotspots = new List<(string, System.Action)>();

        // UIDocument's rootVisualElement can be null in OnEnable (it initializes
        // its panel on its own OnEnable, and script order isn't guaranteed), so we
        // also try in Start, by which point the panel is ready. Whichever sees a
        // non-null root first builds; the other is a no-op.
        // Renew the cancellation source on every enable — it is the token every
        // asset load uses. Build() is gated by `_built`, so without this a
        // disable/enable cycle would leave the source cancelled (from OnDisable)
        // and every bg/actor/audio load would throw immediately → a blank stage.
        private void OnEnable() { _cts?.Dispose(); _cts = new CancellationTokenSource(); Build(); }
        private void Start() => Build();

        private void Build()
        {
            if (_built) return;
            var root = GetComponent<UIDocument>().rootVisualElement;
            if (root == null) return; // panel not ready yet — Start will retry
            _uiRoot = root;
            _built = true;
            LvnPlayer.Log = m => Debug.Log("[LVN] " + m); // full step trace to the console

            if (Assets == null && !string.IsNullOrEmpty(ContentRoot))
                Assets = new DirectoryAssets(ContentRoot);
            root.Clear();
            root.style.flexGrow = 1;

            // Scene = background + actors + camera. Two interchangeable renderers:
            // the uGUI Canvas (60fps / Spine) sits on a sibling canvas *below* this
            // UITK panel; the UITK path wraps them in a "vn-world" element. Either
            // way the dialogue/choice chrome draws above the scene.
            if (UseCanvasScene)
            {
                // sortingOrder below the panel (10) so the UITK chrome composites on top.
                var scene = new World.WorldStage(transform, sortingOrder: 0);
                scene.SetBackgroundColor(Color.black);
                _renderer = new CanvasSceneRenderer(scene);
            }
            else
            {
                _world = new VisualElement { name = "vn-world", pickingMode = PickingMode.Ignore };
                _world.style.position = Position.Absolute;
                _world.style.left = 0; _world.style.right = 0; _world.style.top = 0; _world.style.bottom = 0;
                var bg = new BackgroundLayer();
                var actors = new ActorLayer();
                _world.Add(bg);
                _world.Add(actors);
                _renderer = new UitkSceneRenderer(bg, actors, new CameraRig(_world));
            }

            _particles = new ParticleField();
            ResolveFont();
            _dialogue = new DialogueBox(Theme);
            _choices = new ChoiceList(Theme);
            _fx = new FxLayer();

            _labelLayer = new VisualElement { name = "vn-labels", pickingMode = PickingMode.Ignore };
            _labelLayer.style.position = Position.Absolute;
            _labelLayer.style.left = 0; _labelLayer.style.right = 0; _labelLayer.style.top = 0; _labelLayer.style.bottom = 0;

            if (_world != null) root.Add(_world);
            root.Add(_particles);   // weather sits over the scene, under the UI
            root.Add(_dialogue);
            root.Add(_choices);
            root.Add(_labelLayer);  // HUD/stat labels above dialogue/choices
            root.Add(_fx);          // top: fades/dim veil everything below
            _menu = new StageMenu(this, Theme);
            root.Add(_menu);        // quick menu above even the FX veil — always reachable
            _choices.OnSelected += OnChoiceSelected;

            // Reactive tick: re-evaluate every live label's {expr} template against the
            // current variables so on-screen stats track changes (incl. background ones).
            root.schedule.Execute(RefreshLabels).Every(200);

            // Auto-advance: hands-free reading — once a line finishes revealing and
            // its reading delay passes, advance as if tapped. Choices always wait.
            root.schedule.Execute(AutoAdvanceTick).Every(100);

            // Player comfort settings (dialogue window opacity now, live on change).
            _dialogue.SetUserOpacity(LvnPrefs.DialogOpacity);
            LvnPrefs.Changed -= OnPrefsChanged;
            LvnPrefs.Changed += OnPrefsChanged;

            root.pickingMode = PickingMode.Position;
            root.RegisterCallback<PointerDownEvent>(OnPointerDown);
            // Desktop convenience (the Ren'Py convention): wheel-up steps back one beat.
            root.RegisterCallback<WheelEvent>(evt =>
            {
                if (InputBlocked) return;
                if (evt.delta.y < 0f && RollbackStep()) evt.StopPropagation();
            });

            // Audio channels (music/ambient/sfx) live in their own component.
            _audio = gameObject.AddComponent<StageAudio>();

            _cts ??= new CancellationTokenSource(); // OnEnable usually made it; safety for a direct Build()
            if (Script != null) Play(Script.text);
        }

        /// <summary>Replace the visual theme. If the stage is already built, the
        /// dialogue box and choice list are recreated with the new look — so a
        /// manifest-driven theme (<see cref="VnThemeBuilder"/>) can be applied
        /// after construction. Call before the first chapter plays.</summary>
        public void ApplyTheme(VnTheme theme)
        {
            Theme = theme ?? new VnTheme();
            if (!_built) return; // Build() will pick up the new Theme
            RebuildChrome();
            // Resolve any manifest-driven background-image urls to sprites, then
            // rebuild once more so the dialogue/choices show their skinned panels.
            _ = EnsureThemeImagesAsync();
        }

        // Recreate the dialogue box and choice list from the current Theme, keeping
        // their z-order (…, dialogue, choices, fx). Used by ApplyTheme and after the
        // theme's background images finish loading.
        private void RebuildChrome()
        {
            var root = GetComponent<UIDocument>()?.rootVisualElement;
            if (root == null || _fx == null) return;

            if (_choices != null) { _choices.OnSelected -= OnChoiceSelected; _choices.RemoveFromHierarchy(); }
            _dialogue?.RemoveFromHierarchy();

            ResolveFont();
            _dialogue = new DialogueBox(Theme);
            _dialogue.SetUserOpacity(LvnPrefs.DialogOpacity);
            _choices = new ChoiceList(Theme);
            int fxIndex = root.IndexOf(_fx); // keep z-order: …, dialogue, choices, fx
            if (fxIndex < 0) fxIndex = root.childCount;
            root.Insert(fxIndex, _dialogue);
            root.Insert(fxIndex + 1, _choices);
            _choices.OnSelected += OnChoiceSelected;

            // The quick menu is themeable too (manifest.ui.menu) — rebuild it with
            // the fresh theme, keeping it the topmost layer.
            _menu?.Close();
            _menu?.RemoveFromHierarchy();
            _menu = new StageMenu(this, Theme);
            root.Add(_menu);

            // Restore the visible beat onto the fresh chrome so a live theme change
            // never blanks the line/choices the player is mid-reading (the text is
            // set instantly — no typewriter restart on each live tweak).
            if (_sayUp && _backlog.Count > 0)
            {
                var beat = _backlog[_backlog.Count - 1];
                _dialogue.SetSpeaker(beat.who);
                _dialogue.ApplyStyle(beat.style);
                _dialogue.SetText(beat.text);
            }
            if (_curChoices != null) _choices.Present(_curChoices);
        }

        // Load the theme's background-image urls (panel/nameplate/choice buttons)
        // through ILvnAssets and assign the resolved sprites onto the Theme, then
        // rebuild the chrome so they show. Each url loads at most once (skipped when
        // the sprite is already set), so this is safe to call after every ApplyTheme.
        private async Task EnsureThemeImagesAsync()
        {
            if (Theme == null || Assets == null || _cts == null) return;

            async Task<bool> Resolve(string url, System.Action<Sprite> assign)
            {
                if (string.IsNullOrEmpty(url)) return false;
                var sprite = await Assets.LoadSpriteAsync(url, _cts.Token);
                if (sprite == null) return false;
                assign(sprite);
                return true;
            }

            bool any = false;
            if (Theme.PanelSprite == null) any |= await Resolve(Theme.PanelImageUrl, s => Theme.PanelSprite = s);
            if (Theme.PlateSprite == null) any |= await Resolve(Theme.PlateImageUrl, s => Theme.PlateSprite = s);
            if (Theme.ChoiceSprite == null) any |= await Resolve(Theme.ChoiceImageUrl, s => Theme.ChoiceSprite = s);
            if (Theme.ChoiceHoverSprite == null) any |= await Resolve(Theme.ChoiceHoverImageUrl, s => Theme.ChoiceHoverSprite = s);

            if (any && _built) RebuildChrome();
        }

        // Load a Font named by Theme.FontResourcePath (from a Resources folder)
        // when no explicit Font is assigned — lets a manifest pick the typeface.
        private void ResolveFont()
        {
            if (Theme != null && Theme.Font == null && !string.IsNullOrEmpty(Theme.FontResourcePath))
                Theme.Font = Resources.Load<Font>(Theme.FontResourcePath);
        }

        private void OnDisable()
        {
            _cts?.Cancel();
            if (_player != null) _player.OnSay -= RecordSay;
            if (_choices != null) _choices.OnSelected -= OnChoiceSelected;
            LvnPrefs.Changed -= OnPrefsChanged;
        }

        private void OnPrefsChanged()
        {
            _dialogue?.SetUserOpacity(LvnPrefs.DialogOpacity);
        }

        // ── auto-advance ─────────────────────────────────────────────────────
        // Reading delay after the reveal completes, scaled by line length and the
        // player's preference — the standard hands-free mode.
        private float _autoRevealDoneAt = -1f;
        private int _lastSayLength;

        /// <summary>Extra gate a host/menu can close to hold auto-advance (and
        /// tap handling) while an overlay is up.</summary>
        public bool InputBlocked;

        /// <summary>Set when the player asks to leave the chapter (the quick
        /// menu's Exit). The host's play loop watches it and returns to the
        /// title screen; position and stats are already autosaved, so Continue
        /// brings the player back to this exact line.</summary>
        public bool ExitRequested { get; private set; }

        /// <summary>Player-initiated exit to the menu: autosave the position,
        /// then signal the host loop.</summary>
        public void RequestExit()
        {
            AutosaveNow();
            ExitRequested = true;
        }

        /// <summary>Host acknowledgment — called by the play loop once it has
        /// acted on the request (and by Play for a fresh chapter).</summary>
        public void ClearExitRequest() => ExitRequested = false;

        // ── look-ahead prefetch ──────────────────────────────────────────────
        // While the player reads a line, warm the assets the next few beats will
        // show — the decode happens during the pause, so a cold bg/portrait never
        // pops in a frame late mid-scene. Bounded per beat (the sprite cache and
        // in-flight dedup make repeats free); the set resets with the stage.
        private readonly HashSet<string> _prefetched = new HashSet<string>();

        private void PrefetchAhead()
        {
            if (_player == null || Assets == null) return;
            const int lookAhead = 25, maxSprites = 6, maxAudio = 2;
            List<string> sprites = null, audio = null;
            foreach (var c in _player.PeekForward(lookAhead))
            {
                var op = (string)c["op"];
                if (op == "bg" || op == "actor" || op == "obj")
                {
                    var url = (string)c["sprite_url"];
                    if (string.IsNullOrEmpty(url) || !_prefetched.Add(url)) continue;
                    (sprites ??= new List<string>()).Add(url);
                }
                else if (op == "audio")
                {
                    var url = (string)c["url"];
                    if (string.IsNullOrEmpty(url) || !_prefetched.Add(url)) continue;
                    (audio ??= new List<string>()).Add(url);
                }
                if ((sprites?.Count ?? 0) >= maxSprites && (audio?.Count ?? 0) >= maxAudio) break;
            }
            if (sprites != null && sprites.Count > maxSprites) sprites.RemoveRange(maxSprites, sprites.Count - maxSprites);
            if (audio != null && audio.Count > maxAudio) audio.RemoveRange(maxAudio, audio.Count - maxAudio);
            if (sprites != null) _ = Assets.PreloadAsync(sprites, "sprite", _cts.Token);
            if (audio != null) _ = Assets.PreloadAsync(audio, "audio", _cts.Token);
        }

        private void AutoAdvanceTick()
        {
            if (!LvnPrefs.AutoAdvance || InputBlocked
                || _player == null || _player.Finished || _player.AtChoice
                || !_awaitingTap || _awaitingWait
                || _dialogue == null || _dialogue.IsRevealing)
            {
                _autoRevealDoneAt = -1f;
                return;
            }
            // First tick after the reveal finished: start the reading timer.
            if (_autoRevealDoneAt < 0f)
            {
                _autoRevealDoneAt = Time.realtimeSinceStartup;
                return;
            }
            float delay = (0.55f + 0.035f * _lastSayLength) * LvnPrefs.AutoDelayScale;
            if (Time.realtimeSinceStartup - _autoRevealDoneAt < delay) return;
            _autoRevealDoneAt = -1f;
            _awaitingTap = false;
            _player.Advance();
        }

        private void OnDestroy()
        {
            Assets?.UnloadAll();
        }

        /// <summary>
        /// Live-edit hot-swap: replace the running chapter's script WITHOUT
        /// restarting it, when the edit didn't change the command structure. The
        /// player keeps its position, variables and call stack, the stage keeps its
        /// current background/actors, and the beat on screen is re-rendered so an
        /// edit to the visible line shows at once. Returns false when nothing is
        /// playing or the structure changed — the caller should then <see
        /// cref="Play"/> from the top.
        /// </summary>
        public bool TryHotSwap(string lvnJson)
        {
            if (_player == null || _player.Finished) return false;
            LvnDocument doc;
            try { doc = LvnDocument.Parse(lvnJson); }
            catch { return false; }
            if (!_player.TryReplaceScript(doc)) return false;
            _cast = SpriteComposer.ParseCast(doc.Cast); // cast metadata is safe to refresh in place
            _player.RerenderCurrent();
            return true;
        }

        /// <summary>Wipe the stage to a clean slate NOW — actors, background, FX,
        /// dialogue. The host calls this when a chapter starts (before the script
        /// finishes downloading) so the previous chapter never lingers during the
        /// load, not only when the previous one ended.</summary>
        public void ClearStage()
        {
            if (!_built) return;
            ResetStage();
            _sayUp = false;
            _curChoices = null;
            _dialogue?.SetSpeaker(null);
            _dialogue?.SetText(string.Empty);
        }

        /// <summary>Persistent variables to preload into the next chapter BEFORE it
        /// runs (set by the host from its state store). With the imported global
        /// defaults marked `default:true`, these carried-in values survive the
        /// chapter's init block — so relationship/route/memory stats flow from one
        /// chapter to the next and across sessions.</summary>
        public Newtonsoft.Json.Linq.JObject SeedVars;

        /// <summary>Parse and start playing a .lvn document.</summary>
        public void Play(string lvnJson)
        {
            var doc = LvnDocument.Parse(lvnJson);
            LvnPlayer.Log?.Invoke("════ PLAY scene=" + doc.Scene + " (" + (doc.Script?.Count ?? 0) + " cmds) ════");
            ExitRequested = false; // a fresh chapter is a fresh run
            _cast = SpriteComposer.ParseCast(doc.Cast);
            ResetStage();
            _player = new LvnPlayer(doc, this);
            _player.Strings = Strings; // localization catalog (text_id → string), if any
            if (SeedVars != null)      // carry stats in before the init defaults run
                foreach (var p in SeedVars.Properties()) _player.Vars[p.Name] = p.Value;
            _player.OnSay += RecordSay;
            _player.Advance();
        }

        /// <summary>
        /// Wipe the stage to a clean slate before a chapter plays. Without this,
        /// actors, the background and effect veils left on screen by the previous
        /// chapter (or a live hot-reload) bleed into the new one — e.g. a character
        /// standing on the very first beat, before any <c>actor</c> command runs.
        /// </summary>
        private void ResetStage()
        {
            // Kill any in-flight `wait` coroutine — it reads the _player field, so
            // after Play() swaps in a new player it would otherwise fire Advance()
            // on the fresh chapter when its old timer elapses.
            StopAllCoroutines();
            _hotspots.Clear();
            _renderer?.RemoveAll();
            _renderer?.ResetCamera(0f);
            _talkAnims.Clear();
            _renderer?.ClearBackground();
            _particles?.Set("rain", false);
            _particles?.Set("snow", false);
            _fx?.Clear(0f);
            _fx?.ClearBlur(0f);
            _backlog.Clear();
            _prefetched.Clear(); // the next chapter/load re-warms from scratch
            _awaitingTap = false;
            _awaitingWait = false;
            _sayUp = false;
            _curChoices = null;
            _choices?.Dismiss(); // clear any on-screen choice buttons (avoid stale clicks)
            _labelLayer?.Clear();
            _labelEls.Clear();
            _labelTmpl.Clear();
        }

        private void RecordSay(string who, string text, string style)
        {
            // After a rollback, the restored beat re-runs and would duplicate its
            // own backlog entry — swallow exactly that one repeat.
            if (_suppressDupSay)
            {
                _suppressDupSay = false;
                if (_backlog.Count > 0)
                {
                    var last = _backlog[_backlog.Count - 1];
                    if (last.who == who && last.text == text) return;
                }
            }
            _backlog.Add((who, text, style));
            // Rolling autosave so a crash mid-scene loses a few lines at most.
            if (++_saySinceAutosave >= 5)
            {
                _saySinceAutosave = 0;
                AutosaveNow();
            }
        }

        private bool _suppressDupSay;

        /// <summary>True when there is a previous beat to roll back to.</summary>
        public bool CanRollback => _player != null && _player.CanRollback && !_awaitingWait;

        /// <summary>Step one beat back (a mis-tap safety net): restore the previous
        /// say/choice's snapshot — variables as they were BEFORE it ran, so a picked
        /// option's set/inc is undone — rebuild the scene there and re-show it.
        /// Returns false when already at the first beat.</summary>
        public bool RollbackStep()
        {
            if (_player == null || _awaitingWait) return false;
            var snap = _player.PopRollback();
            if (snap == null) return false;

            // ResetStage wipes the dialogue history; a one-step rewind must keep it
            // (minus the beat being undone — its re-run is dedup'd in RecordSay).
            var kept = new List<(string who, string text, string style)>(_backlog);
            if (kept.Count > 0) kept.RemoveAt(kept.Count - 1);

            ResetStage();
            _backlog.AddRange(kept);
            _suppressDupSay = true;

            _player.Restore(snap);
            int at = _player.Index;
            _player.ReplayVisuals(at);
            _player.ContinueFrom(at);
            return true;
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
            if (InputBlocked) return; // an overlay (quick menu) owns the screen
            if (_player == null || _player.Finished) return;
            if (_awaitingWait) return;

            // Canvas-scene hotspots: there's no uGUI raycaster, so a tap is routed
            // here. Test it against each obj's normalized placement rect (top-left
            // origin, matching both placement.Y and UITK's y-down). Topmost
            // (last-placed) wins; a hit fires its on_click and swallows the advance.
            // A point-and-click screen (the Canvas scene has registered hotspots):
            // only hotspots act. A hit fires its on_click; a miss is IGNORED (it must
            // not advance/re-print the room). Hotspots win over tap-to-advance.
            if (_hotspots.Count > 0 && _uiRoot != null)
            {
                Vector2 pos = evt.position; // event-driven → works with mouse, touch & Simulator
                float pw = _uiRoot.layout.width, ph = _uiRoot.layout.height;
                var hit = HotspotAt(pos, pw, ph);
                if (hit != null)
                {
                    LvnPlayer.Log?.Invoke($"[click {pos.x:0},{pos.y:0} of {pw:0}x{ph:0}] → HOTSPOT");
                    // Hotspots stay armed (no clear): clicking another object jumps
                    // straight to it (its on_click GoTo overrides the cursor), so no
                    // phantom "dismiss" tap is needed. A MISS falls through to the
                    // normal tap-advance below — so descriptions and the ending are
                    // still dismissable by tapping empty space.
                    if (_dialogue.IsRevealing) _dialogue.Complete();
                    hit();
                    return;
                }
                LvnPlayer.Log?.Invoke($"[click {pos.x:0},{pos.y:0} of {pw:0}x{ph:0}] → miss → advance");
                // fall through to tap-to-advance
            }

            if (_dialogue.IsRevealing) { _dialogue.Complete(); return; }
            if (_awaitingTap)
            {
                _awaitingTap = false;
                _player.Advance();
            }
        }

        // The hotspot under a pointer — topmost (last-placed) first; null if none.
        // Works from the EVENT position (not Input.mousePosition, which is dead in
        // the Device Simulator / touch). Both the pointer and each actor's real
        // on-screen rect are normalized to 0..1 top-left, so it's independent of
        // pixel scale and aspect (and panel-vs-canvas coordinate differences).
        private System.Action HotspotAt(Vector2 panelPos, float panelW, float panelH)
        {
            if (_renderer == null || panelW <= 0f || panelH <= 0f) return null;
            float nx = panelPos.x / panelW, ny = panelPos.y / panelH; // UITK: top-left, y-down
            for (int i = _hotspots.Count - 1; i >= 0; i--)
            {
                // Renderer-normalized rect (0..1, top-left origin); null when the
                // renderer does its own picking or the actor is gone.
                var r = _renderer.ActorScreenRect(_hotspots[i].id);
                if (r == null) continue;
                if (r.Value.Contains(new Vector2(nx, ny))) return _hotspots[i].onClick;
            }
            return null;
        }

        private void OnChoiceSelected(int index)
        {
            _choices.Dismiss();
            _curChoices = null;
            _awaitingTap = false;
            // Ignore a click on a stale button (the beat moved on via load/hot-reload
            // and these options no longer apply) instead of throwing.
            if (_player == null || !_player.AtChoice) return;
            _player.Choose(index);
            _player.Advance();
            // A picked branch is exactly what a crash must not lose — autosave here.
            AutosaveNow();
        }

        // ── ILvnStage ─────────────────────────────────────────────────────────

        public void ShowSay(string who, string text, string style)
        {
            _dialogue.SetSpeaker(who);
            _dialogue.ApplyStyle(style);
            _dialogue.Reveal(text);
            _lastSayLength = text?.Length ?? 0; // drives the auto-advance reading delay
            _autoRevealDoneAt = -1f;
            _awaitingTap = true;
            _sayUp = true;
            _curChoices = null;
            PrefetchAhead(); // warm the next beats' art/audio while the player reads

            // Classic VN focus: the speaker is at full opacity, everyone else present
            // dims — so a two-shot reads as "this one is talking" instead of a flat row.
            SceneHighlightSpeaker(who);

            // Lip-sync: only the speaking actor's mouth moves while the line is up.
            var spId = ResolveSpeakerId(who);
            foreach (var kv in _talkAnims) SceneTalk(kv.Key, kv.Value, kv.Key == spId);
        }

        // Scene calls go through the ISceneRenderer seam — path-specific behaviour
        // lives inside UitkSceneRenderer / CanvasSceneRenderer, not in per-call-site
        // conditionals here. These thin aliases keep historical call names readable.
        private void SceneSetFrames(string id, Dictionary<string, Dictionary<string, Sprite>> frames) => _renderer?.SetFrames(id, frames);
        private void SceneEnsureIdle(string id, LvnAnim a) => _renderer?.EnsureIdle(id, a);
        private void SceneEnsureBlink(string id, LvnAnim a) => _renderer?.EnsureBlink(id, a);
        private void ScenePlayGesture(string id, LvnAnim g, LvnAnim idle) => _renderer?.PlayGesture(id, g, idle);
        private void ScenePlayAnim(string id, string channel, LvnAnim a) => _renderer?.PlayAnim(id, channel, a);
        private void ScenePlayAnimQueued(string id, string channel, LvnAnim a) => _renderer?.PlayAnimQueued(id, channel, a);
        private void SceneStopAnim(string id, string target) => _renderer?.StopAnim(id, target);
        private void SceneTalk(string id, LvnAnim t, bool on) => _renderer?.Talk(id, t, on);
        private void SceneHighlightSpeaker(string who) => _renderer?.HighlightSpeaker(who);

        // ── save / load ──────────────────────────────────────────────────────
        // `save [slot=name]` writes the player snapshot (cursor + vars + call stack)
        // to PlayerPrefs; `load [slot=name]` restores it, rebuilds the scene from the
        // saved point (ReplayVisuals) and resumes. Default slot is "quick".
        private static string SaveKey(JObject cmd)
        {
            var slot = (string)cmd["slot"];
            return "lvn_save_" + (string.IsNullOrEmpty(slot) ? "quick" : slot);
        }

        private void SaveSlot(JObject cmd)
        {
            if (_player == null) return;
            try
            {
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(_player.Save());
                PlayerPrefs.SetString(SaveKey(cmd), json);
                PlayerPrefs.Save();
                LvnPlayer.Log?.Invoke("saved → " + SaveKey(cmd));
            }
            catch (System.Exception e) { Debug.LogWarning("[lvn] save failed: " + e.Message); }
        }

        private void LoadSlot(JObject cmd)
        {
            if (_player == null) return;
            var json = PlayerPrefs.GetString(SaveKey(cmd), "");
            LvnPlayer.LvnSnapshot snap = null;
            if (!string.IsNullOrEmpty(json))
                try { snap = Newtonsoft.Json.JsonConvert.DeserializeObject<LvnPlayer.LvnSnapshot>(json); }
                catch (System.Exception e) { Debug.LogWarning("[lvn] load parse failed: " + e.Message); }

            if (snap == null || snap.Vars == null)
            {
                _player.ContinueFrom(_player.Index + 1); // no/invalid save → skip the load op
                return;
            }
            RestoreSnapshot(snap);
        }

        /// <summary>Restore a snapshot of the CURRENT chapter in place: clean the
        /// stage, restore cursor/vars/call stack (position resolves via its label
        /// anchor), rebuild the scene's visuals/FX/audio up to that point, resume.
        /// The shared machinery behind the in-script `load` op, the save/load
        /// panel and the autosave resume.</summary>
        public void RestoreSnapshot(LvnPlayer.LvnSnapshot snap)
        {
            if (_player == null || snap == null) return;
            ResetStage();                       // clean slate
            _player.Restore(snap);              // cursor (via label anchor) + vars + call stack
            _player.ClearHistory();             // the rollback trail no longer describes the path here
            int at = _player.Index;             // the anchor-relocated cursor, not the raw saved index
            _player.ReplayVisuals(at);          // rebuild bg / actors / FX / audio up to the saved point
            _player.ContinueFrom(at);           // resume → renders the saved beat
        }

        // ── persistent save slots (per title, survive restarts) ─────────────

        /// <summary>Save-slot namespace + labels, set by the host per chapter entry
        /// (title id keys the slot store; script url tags snapshots so a slot is
        /// only restored into the chapter it belongs to).</summary>
        public void SetSaveContext(string titleId, string chapterId, string scriptUrl)
        {
            _saveTitleId = titleId;
            _saveChapterId = chapterId;
            _saveScriptUrl = scriptUrl;
        }

        private string _saveTitleId, _saveChapterId, _saveScriptUrl;
        private int _saySinceAutosave;

        /// <summary>The title id save slots are namespaced under (host-set).</summary>
        public string SaveTitleId => _saveTitleId;

        /// <summary>Write the current position into a named persistent slot.</summary>
        public bool SaveToSlot(string slot)
        {
            if (_player == null || string.IsNullOrEmpty(slot)) return false;
            var snap = _player.Save();
            snap.ScriptUrl = _saveScriptUrl;
            var last = _backlog.Count > 0 ? _backlog[_backlog.Count - 1].text : "";
            LvnSaveStore.Put(_saveTitleId, slot, new LvnSaveSlot
            {
                Snap = snap,
                ChapterId = _saveChapterId,
                Preview = last,
            });
            LvnPlayer.Log?.Invoke("saved slot '" + slot + "' @#" + snap.Index);
            return true;
        }

        /// <summary>Restore a persistent slot taken in the CURRENT chapter; returns
        /// false for another chapter's slot (see <see cref="LoadFromSlotAsync"/> for
        /// the cross-chapter path).</summary>
        public bool LoadFromSlot(string slot)
        {
            var s = LvnSaveStore.Get(_saveTitleId, slot);
            if (s?.Snap == null || _player == null) return false;
            if (!string.IsNullOrEmpty(s.Snap.ScriptUrl) && s.Snap.ScriptUrl != _saveScriptUrl) return false;
            RestoreSnapshot(s.Snap);
            return true;
        }

        /// <summary>Host hook for loading a slot that belongs to ANOTHER chapter:
        /// resolve the chapter by <c>Snap.ScriptUrl</c>, fetch its script, play it
        /// and restore. Wired by NovelApp; when null, cross-chapter slots simply
        /// aren't loadable (greyed out in the menu).</summary>
        public Func<LvnSaveSlot, Task<bool>> CrossChapterLoader;

        /// <summary>Load a slot wherever it points: in-place for the current
        /// chapter, via <see cref="CrossChapterLoader"/> for another one.</summary>
        public async Task<bool> LoadFromSlotAsync(string slot)
        {
            if (LoadFromSlot(slot)) return true;
            var s = LvnSaveStore.Get(_saveTitleId, slot);
            if (s?.Snap == null || CrossChapterLoader == null) return false;
            try { return await CrossChapterLoader(s); }
            catch (Exception e)
            {
                Debug.LogWarning("[lvn] cross-chapter load failed: " + e.Message);
                return false;
            }
        }

        /// <summary>True when the slot exists and is reachable — taken in the
        /// current chapter, or in another one the host can route to.</summary>
        public bool CanLoadSlot(string slot)
        {
            var s = LvnSaveStore.Get(_saveTitleId, slot);
            if (s?.Snap == null) return false;
            if (string.IsNullOrEmpty(s.Snap.ScriptUrl) || s.Snap.ScriptUrl == _saveScriptUrl) return true;
            return CrossChapterLoader != null;
        }

        /// <summary>Autosave into the reserved slot now — called by the host on
        /// app pause, and internally on choices / every few lines.</summary>
        public void AutosaveNow()
        {
            if (_player == null || _player.Finished) return;
            SaveToSlot(LvnSaveStore.AutoSlot);
        }

        // A persistent reactive text label (`text id=… x= y= anchor= «{expr}»`): a
        // HUD/stat readout placed like an actor but living in the UITK overlay. Its
        // {expr} template is re-evaluated on the reactive tick, so the shown value
        // tracks the variable. Re-issuing the same id updates it; `hide` removes it.
        private void ApplyText(JObject cmd)
        {
            var id = (string)cmd["id"];
            if (string.IsNullOrEmpty(id) || _labelLayer == null) return;

            if (BoolOr(cmd["hide"], false))
            {
                if (_labelEls.TryGetValue(id, out var old)) { old.RemoveFromHierarchy(); _labelEls.Remove(id); }
                _labelTmpl.Remove(id);
                return;
            }

            if (!_labelEls.TryGetValue(id, out var el))
            {
                el = new Label { name = "lbl-" + id, pickingMode = PickingMode.Ignore };
                el.style.position = Position.Absolute;
                el.style.whiteSpace = WhiteSpace.Normal;
                _labelLayer.Add(el);
                _labelEls[id] = el;
            }

            // placement: x/y are screen percents; anchor picks the label's reference point
            float x = NumOr(cmd["x"], 3f), y = NumOr(cmd["y"], 3f);
            el.style.left = Length.Percent(Mathf.Clamp(x, 0f, 100f));
            el.style.top = Length.Percent(Mathf.Clamp(y, 0f, 100f));
            var (tx, ty) = LabelAnchor((string)cmd["anchor"]);
            el.style.translate = new Translate(Length.Percent(tx), Length.Percent(ty));

            // look: per-label font / size / colour, falling back to the theme
            el.style.color = Lvn.UI.Screens.UiColor.Parse((string)cmd["color"], Theme.TextColor);
            el.style.fontSize = (int)NumOr(cmd["size"], Theme.BodyFontSize);
            var fontPath = (string)cmd["font"];
            Font font = !string.IsNullOrEmpty(fontPath) ? Resources.Load<Font>(fontPath) : Theme.Font;
            if (font != null) el.style.unityFont = new StyleFont(font);

            var tmpl = (string)cmd["text"] ?? "";
            _labelTmpl[id] = tmpl;
            el.text = TextInterpolation.Apply(tmpl, _player?.Vars); // immediate paint; tick keeps it live
        }

        // Re-evaluate every live label's template against the current variables.
        private void RefreshLabels()
        {
            if (_labelTmpl.Count == 0) return;
            var vars = _player?.Vars;
            foreach (var kv in _labelTmpl)
                if (_labelEls.TryGetValue(kv.Key, out var el))
                {
                    var t = TextInterpolation.Apply(kv.Value, vars);
                    if (el.text != t) el.text = t;
                }
        }

        private static float NumOr(JToken t, float dflt) => NumOrNull(t) ?? dflt;

        // Nullable numeric read: absent → null, malformed → null (never throws), so
        // one bad field can't abort the whole chapter. A number written as a string
        // ("0.5") is still accepted.
        private static float? NumOrNull(JToken t)
        {
            if (t == null) return null;
            try { return (float)t; } catch { }
            try
            {
                if (float.TryParse((string)t, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                    return f;
            }
            catch { }
            return null;
        }

        private static int? IntOrNull(JToken t)
        {
            var f = NumOrNull(t);
            return f == null ? (int?)null : (int)Mathf.Round(f.Value);
        }

        // Tolerant boolean read: absent → dflt, and true/false/1/0 written as a
        // string or number are all accepted rather than throwing an invalid cast.
        private static bool BoolOr(JToken t, bool dflt)
        {
            if (t == null) return dflt;
            try { return (bool)t; } catch { }
            switch (t.ToString().Trim().ToLowerInvariant())
            {
                case "true": case "1": case "yes": return true;
                case "false": case "0": case "no": return false;
                default: return dflt;
            }
        }

        // Translate fractions for a label anchor (default top-left, so x/y read as an
        // inset from the corner). center → -50%, right/bottom → -100%.
        private static (float, float) LabelAnchor(string anchor)
        {
            string a = string.IsNullOrEmpty(anchor) ? "top-left" : anchor.ToLowerInvariant();
            float tx = a.Contains("left") ? 0f : a.Contains("right") ? -100f : -50f;
            float ty = a.Contains("top") ? 0f : a.Contains("bottom") ? -100f : -50f;
            return (tx, ty);
        }

        // A script-driven `anim` command: deserialize its LvnAnim payload and play
        // it on the named channel (default "script") of an already-shown entity, so
        // .lvns can tween any prop/layer or move a sprite along a path live.
        private void ApplyAnim(JObject cmd)
        {
            var id = (string)cmd["id"];
            if (string.IsNullOrEmpty(id)) return;
            // Stop form: `anim id=x stop=all` / `stop=<channel/prop>`.
            var stop = (string)cmd["stop"];
            if (!string.IsNullOrEmpty(stop)) { SceneStopAnim(id, stop); return; }
            var payload = cmd["anim"];
            if (payload == null) return;
            LvnAnim anim;
            try { anim = payload.ToObject<LvnAnim>(); }
            catch { return; }
            if (anim == null || anim.tracks == null || anim.tracks.Count == 0) return;
            // Channel: explicit if given, else derived from the first track's target
            // (e.g. "script:rotation", "script:face:y") — so distinct properties run
            // and compose at once, while re-animating the same property replaces it.
            var channel = (string)cmd["channel"];
            if (string.IsNullOrEmpty(channel))
            {
                var t0 = anim.tracks[0];
                channel = "script:" + (string.IsNullOrEmpty(t0.layer) ? "" : t0.layer + ":") + t0.prop;
            }
            // mode=queue → chain after the current anim on this channel (non-blocking)
            if ((string)cmd["mode"] == "queue") ScenePlayAnimQueued(id, channel, anim);
            else ScenePlayAnim(id, channel, anim);
        }

        // Speaker label → on-stage actor id (mirrors the authoring speakerEntity
        // rule: actor_map alias, else the lowercased name).
        private string ResolveSpeakerId(string who)
        {
            if (string.IsNullOrEmpty(who)) return null;
            var sb = new StringBuilder(who.Length);
            foreach (var c in who.ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }

        public void ShowChoice(IReadOnlyList<LvnOption> options)
        {
            _awaitingTap = false;
            _curChoices = options;
            _choices.Present(options);
        }

        public void ApplyStage(JObject command)
        {
            switch ((string)command["op"])
            {
                case "bg": _ = ApplyBgAsync(command); break;
                case "actor": _ = ApplyActorAsync(command); break;
                case "obj": _ = ApplyActorAsync(command); break; // any placeable sprite
                case "anim": ApplyAnim(command); break; // script-driven tween / path
                case "fade": ApplyFade(command); break;
                case "dim": ApplyDim(command); break;
                case "flash": ApplyFlash(command); break;
                case "tint": ApplyTint(command); break;
                case "blur": ApplyBlur(command); break;
                case "camera": ApplyCamera(command); break;
                case "particles":
                    _particles.Set((string)command["type"], BoolOr(command["on"], true));
                    break;
                case "audio": _ = _audio.ApplyAsync(command, Assets, _cts.Token); break;
                case "text": ApplyText(command); break; // reactive HUD/stat label
                case "save": SaveSlot(command); break;
                case "load": LoadSlot(command); break;
                case "text_pace": ApplyTextPace(command); break;
                case "wait":
                    _awaitingWait = true;
                    StartCoroutine(WaitCoroutine(command));
                    break;
                case "preload":
                    _ = PreloadAssetsAsync(command);
                    break;
                // hint is a no-op; unknown-but-registered ops are simply not drawn.
            }
        }

        public void OnEnd()
        {
            // The chapter is finished — its mid-chapter autosave must not hijack the
            // next entry back to a stale position.
            LvnSaveStore.Delete(_saveTitleId, LvnSaveStore.AutoSlot);
            // Garbage-collect the scene when the chapter ends: without this the last
            // actors keep their (looping) animations running and bleed into the menu
            // or the next chapter. ResetStage stops coroutines, removes actors,
            // clears the background and FX.
            ResetStage();
            _dialogue.SetSpeaker(null);
            _dialogue.SetText(string.Empty);
        }

        // ── wait / preload ──────────────────────────────────────────────────

        private IEnumerator WaitCoroutine(JObject cmd)
        {
            float ms = NumOr(cmd["ms"], 1000f);
            yield return new WaitForSecondsRealtime(ms / 1000f);
            _awaitingWait = false;
            if (_player != null && !_player.Finished)
                _player.Advance();
        }

        private async Task PreloadAssetsAsync(JObject cmd)
        {
            if (Assets == null) return;
            var assetArray = cmd["assets"] as JArray;
            if (assetArray == null || assetArray.Count == 0) return;

            var spriteUrls = new List<string>();
            var audioUrls = new List<string>();
            foreach (var a in assetArray)
            {
                var url = (string)((JObject)a)["url"];
                var kind = (string)((JObject)a)["kind"];
                if (string.IsNullOrEmpty(url)) continue;
                if (kind == "audio") audioUrls.Add(url);
                else spriteUrls.Add(url);
            }

            var tasks = new List<Task>();
            if (spriteUrls.Count > 0)
                tasks.Add(Assets.PreloadAsync(spriteUrls, "sprite", _cts.Token));
            if (audioUrls.Count > 0)
                tasks.Add(Assets.PreloadAsync(audioUrls, "audio", _cts.Token));
            await Task.WhenAll(tasks);
        }

        // ── stage command helpers ─────────────────────────────────────────────

        private void ApplyFade(JObject cmd)
        {
            var to = (string)cmd["to"] ?? "black";
            float dur = NumOr(cmd["duration"], 0.5f);
            if (to == "clear" || to == "none") _fx.Clear(dur);
            else _fx.Fade(to == "white" ? Color.white : Color.black, dur);
        }

        private void ApplyDim(JObject cmd)
        {
            float alpha = NumOr(cmd["alpha"], 0.4f);
            float dur = NumOr(cmd["duration"], 0.5f);
            _fx.Dim(alpha, dur);
        }

        private void ApplyFlash(JObject cmd)
        {
            if (LvnPrefs.ReduceMotion) return; // vestibular/photosensitivity comfort
            var colour = ParseColor((string)cmd["color"], Color.white);
            float dur = NumOr(cmd["duration"], 0.2f);
            _fx.Flash(colour, dur);
        }

        private void ApplyTint(JObject cmd)
        {
            var colour = ParseColor((string)cmd["color"], Color.white);
            float alpha = NumOr(cmd["alpha"], 0.3f);
            float dur = NumOr(cmd["duration"], 0.5f);
            _fx.Tint(colour, alpha, dur);
        }

        private void ApplyBlur(JObject cmd)
        {
            float alpha = NumOr(cmd["alpha"], 0.5f);
            float dur = NumOr(cmd["duration"], 0.5f);
            if (alpha <= 0f) _fx.ClearBlur(dur);
            else _fx.Blur(alpha, dur);
        }

        private void ApplyTextPace(JObject cmd)
        {
            float cps = NumOr(cmd["cps"], 0f);
            TypewriterClock.GlobalCps = cps;
        }

        internal static TransitionType ParseTransition(string name)
        {
            if (string.IsNullOrEmpty(name)) return TransitionType.None;
            switch (name.ToLowerInvariant())
            {
                case "fade": return TransitionType.Fade;
                case "slide_left": return TransitionType.SlideLeft;
                case "slide_right": return TransitionType.SlideRight;
                case "pop": return TransitionType.Pop;
                default: return TransitionType.None;
            }
        }

        internal static Color ParseColor(string name, Color fallback)
        {
            if (string.IsNullOrEmpty(name)) return fallback;
            switch (name.ToLowerInvariant())
            {
                case "white": return Color.white;
                case "black": return Color.black;
                case "red": return Color.red;
                case "blue": return Color.blue;
                case "green": return Color.green;
                case "yellow": return Color.yellow;
                case "cyan": return Color.cyan;
                case "magenta": return Color.magenta;
                case "cold":
                case "tint_cold": return new Color(0.6f, 0.7f, 1f, 1f);
                case "warm":
                case "tint_warm": return new Color(1f, 0.85f, 0.7f, 1f);
                case "sepia": return new Color(0.76f, 0.6f, 0.42f, 1f);
                default: return fallback;
            }
        }

        private void ApplyCamera(JObject cmd)
        {
            float dur = NumOr(cmd["duration"], 0.3f);
            switch ((string)cmd["action"])
            {
                case "shake":
                {
                    if (LvnPrefs.ReduceMotion) break; // comfort setting: no screen shake
                    float amp = NumOr(cmd["amplitude"], 8f);
                    _renderer?.Shake(amp, dur);
                    break;
                }
                case "zoom":
                {
                    float factor = NumOr(cmd["factor"], 1.2f);
                    _renderer?.Zoom(factor, dur);
                    break;
                }
                case "pan":
                {
                    float px = NumOr(cmd["x"], 0f);
                    float py = NumOr(cmd["y"], 0f);
                    _renderer?.Pan(px, py, dur);
                    break;
                }
                case "reset":
                    _renderer?.ResetCamera(dur);
                    break;
            }
        }


        private async Task ApplyBgAsync(JObject cmd)
        {
            var url = (string)cmd["sprite_url"];
            // bg id="porch" — resolve the catalog entity to its (first) layer url.
            if (string.IsNullOrEmpty(url))
            {
                var id = (string)cmd["id"];
                if (Catalog != null && Catalog.Has(id))
                {
                    var urls = Catalog.Resolve(id, AxesFrom(cmd), CatalogCond());
                    if (urls.Count > 0) url = urls[0];
                }
            }
            if (Assets == null || string.IsNullOrEmpty(url)) return;
            var sprite = await Assets.LoadSpriteAsync(url, _cts.Token);
            if (sprite == null) return;
            _renderer?.SetBackground(sprite);
        }

        // Evaluates a layer's `when` condition against the player's vars, so a
        // conditional sprite layer appears only when its expression holds.
        private System.Func<string, bool> CatalogCond() => expr =>
        {
            if (_player == null || string.IsNullOrEmpty(expr)) return false;
            try { return LvnExpression.EvaluateBool(expr, _player.Vars); }
            catch { return false; }
        };

        internal static readonly HashSet<string> ReservedActorFields = new HashSet<string>
        {
            "op", "id", "show", "position", "x", "y", "width", "height", "scale",
            "anchor", "anchor_x", "anchor_y", "z", "flip", "rotation", "opacity",
            "on_click", "hover_opacity", "breathing", "sprite_url", "body_url", "clothes_url", "hair_url",
            "transition", "transition_duration", "enter", "exit", "play",
        };

        private async Task ApplyActorAsync(JObject cmd)
        {
            var id = (string)cmd["id"];
            if (string.IsNullOrEmpty(id)) return;

            // Resolve the layer urls, in priority order:
            //   1. catalog id (manifest.sprites) — layered, with conditional `when`;
            //   2. per-doc cast block — layered by the command's axes;
            //   3. direct body/clothes/hair layers, or a single sprite_url.
            List<string> urls;
            List<string> urlIds = null;      // parallel layer ids (catalog path), for blink/lip-sync
            List<Vector4> urlRects = null;    // parallel per-layer sub-rects (x,y,w,h); w≤0 = fill
            if (Catalog != null && Catalog.Has(id))
            {
                var rls = Catalog.ResolveLayers(id, AxesOf(cmd), CatalogCond());
                urls = new List<string>(rls.Count);
                urlIds = new List<string>(rls.Count);
                urlRects = new List<Vector4>(rls.Count);
                foreach (var rl in rls) { urls.Add(rl.Url); urlIds.Add(rl.Id); urlRects.Add(new Vector4(rl.X, rl.Y, rl.W, rl.H)); }
            }
            else if (_cast != null && _cast.TryGetValue(id, out var entity))
            {
                urls = SpriteComposer.Resolve(entity, AxesFrom(cmd));
            }
            else
            {
                urls = new List<string>();
                var body = (string)cmd["body_url"]; if (!string.IsNullOrEmpty(body)) urls.Add(body);
                var clothes = (string)cmd["clothes_url"]; if (!string.IsNullOrEmpty(clothes)) urls.Add(clothes);
                var hair = (string)cmd["hair_url"]; if (!string.IsNullOrEmpty(hair)) urls.Add(hair);
                if (urls.Count == 0)
                {
                    var sp = (string)cmd["sprite_url"]; if (!string.IsNullOrEmpty(sp)) urls.Add(sp);
                }
            }

            // Build the click action + placement SYNCHRONOUSLY (everything here runs
            // before the first `await` below). For the Canvas scene we also place the
            // actor and register its hotspot NOW — so it's clickable the instant the
            // obj command runs, before the next command (the room's narration `say`)
            // shows. Otherwise the hotspot armed only a few frames later (after the
            // async art load), and a tap in that gap fell through to "advance",
            // re-printing the room — the "first click does nothing" bug.
            System.Action onClick = null;
            var clickField = cmd["on_click"];
            if (clickField != null)
            {
                if (clickField.Type == JTokenType.Object)
                {
                    var clickObj = (JObject)clickField;
                    var target = (string)clickObj["goto"];
                    var setOps = clickObj["set"] as JObject;
                    onClick = () =>
                    {
                        if (_player == null) return;
                        if (setOps != null)
                        {
                            foreach (var prop in setOps.Properties())
                                _player.Vars[prop.Name] = prop.Value;
                        }
                        if (!string.IsNullOrEmpty(target))
                            _player.GoTo(target);
                        _awaitingTap = false;
                        _curChoices = null;
                        _choices.Dismiss();
                        _player.Advance();
                    };
                }
                else
                {
                    var clickTarget = (string)clickField;
                    if (!string.IsNullOrEmpty(clickTarget))
                        onClick = () =>
                        {
                            if (_player == null) return;
                            _player.GoTo(clickTarget);
                            _awaitingTap = false;
                            _curChoices = null;
                            _choices.Dismiss();
                            _player.Advance();
                        };
                }
            }

            var placement = PlacementFrom(cmd);
            // Place first so the slot exists before the (async) art arrives — a
            // no-op on renderers that apply placement together with the art.
            _renderer?.PlaceActor(id, placement);
            _hotspots.RemoveAll(h => h.id == id);
            // Manual hotspot hit-testing only applies to renderers that expose
            // actor rects (the canvas path); the UITK path uses element picking.
            if (onClick != null && placement.Show && UseCanvasScene) _hotspots.Add((id, onClick));

            // Now load the layer sprites (async) and set them on the placed actor.
            List<Sprite> layers = null;
            List<string> layerIds = null;
            List<Vector4> layerRects = null;
            if (urls != null && urls.Count > 0 && Assets != null)
            {
                layers = new List<Sprite>(urls.Count);
                layerIds = urlIds != null ? new List<string>(urls.Count) : null;
                layerRects = urlRects != null ? new List<Vector4>(urls.Count) : null;
                for (int i = 0; i < urls.Count; i++)
                {
                    Sprite s = null;
                    try { s = await Assets.LoadSpriteAsync(urls[i], _cts.Token); }
                    catch { }
                    if (s != null)
                    {
                        layers.Add(s);
                        layerIds?.Add(i < urlIds.Count ? urlIds[i] : null);
                        layerRects?.Add(i < urlRects.Count ? urlRects[i] : Vector4.zero);
                    }
                }
            }

            _renderer?.ApplyActor(id, layers, placement, onClick, layerIds, layerRects);

            // Animations (rigged entities): idle (whole-actor) + blink (a layer)
            // auto-run on show; play="name" fires a one-shot gesture; an
            // auto:"speaking" anim is remembered for lip-sync while this actor talks.
            var animEntity = Catalog != null ? Catalog.Get(id) : null;
            if (animEntity != null && animEntity.anim != null && animEntity.anim.Count > 0)
            {
                await PreloadFramesAsync(id, animEntity);

                LvnAnim idle = null, blink = null, talk = null;
                foreach (var kv in animEntity.anim)
                {
                    var a = kv.Value;
                    if (a == null) continue;
                    if (a.auto == "speaking") { talk = a; continue; }
                    if (a.auto == "true") { if (HasLayerTrack(a)) blink = blink ?? a; else idle = idle ?? a; }
                }
                _talkAnims[id] = talk; // null clears it

                var playName = (string)cmd["play"];
                if (!string.IsNullOrEmpty(playName) && animEntity.anim.TryGetValue(playName, out var gesture))
                    ScenePlayGesture(id, gesture, idle);
                else if (placement.Show && idle != null)
                    SceneEnsureIdle(id, idle);
                if (placement.Show && blink != null) SceneEnsureBlink(id, blink);
            }
        }

        private static bool HasLayerTrack(LvnAnim a)
        {
            if (a.tracks == null) return false;
            foreach (var t in a.tracks) if (t != null && !string.IsNullOrEmpty(t.layer)) return true;
            return false;
        }

        // Preload the sprite variants a frame track needs (e.g. eyes=open/closed),
        // so blink/lip-sync swaps are instant. Resolves each layer's url template
        // with axis=value via the catalog.
        private async Task PreloadFramesAsync(string id, LvnSpriteEntity entity)
        {
            if (entity.anim == null || entity.layers == null || Assets == null || Catalog == null) return;
            var frames = new Dictionary<string, Dictionary<string, Sprite>>();
            foreach (var anim in entity.anim.Values)
            {
                if (anim?.tracks == null) continue;
                foreach (var tr in anim.tracks)
                {
                    if (tr == null || tr.prop != "frame" || string.IsNullOrEmpty(tr.layer) || string.IsNullOrEmpty(tr.axis) || tr.keys == null) continue;
                    string template = null;
                    foreach (var l in entity.layers) if (l != null && l.id == tr.layer) { template = l.url; break; }
                    if (string.IsNullOrEmpty(template)) continue;
                    if (!frames.TryGetValue(tr.layer, out var map)) frames[tr.layer] = map = new Dictionary<string, Sprite>();
                    foreach (var key in tr.keys)
                    {
                        var val = key != null && key.Length > 1 ? key[1]?.ToString() : null;
                        if (string.IsNullOrEmpty(val) || map.ContainsKey(val)) continue;
                        var url = Catalog.FillFor(id, template, new Dictionary<string, string> { { tr.axis, val } });
                        if (string.IsNullOrEmpty(url)) continue;
                        try { var sp = await Assets.LoadSpriteAsync(url, _cts.Token); if (sp != null) map[val] = sp; }
                        catch { }
                    }
                }
            }
            if (frames.Count > 0) SceneSetFrames(id, frames);
        }

        // Build placement from the command — everything in screen fractions so a
        // script controls any object's position, size, anchor, z, flip, rotation
        // and opacity without knowing the resolution.
        internal static Placement PlacementFrom(JObject cmd)
        {
            var p = new Placement
            {
                Show = BoolOr(cmd["show"], true),
                X = NumOrNull(cmd["x"]) ?? ActorLayer.SlotX((string)cmd["position"]),
                Y = NumOr(cmd["y"], 1f),
                Width = NumOrNull(cmd["width"]),
                Height = NumOrNull(cmd["height"]),
                AnchorX = 0.5f,
                AnchorY = 1f,
                Z = IntOrNull(cmd["z"]),
                Flip = BoolOr(cmd["flip"], false),
                Rotation = NumOr(cmd["rotation"], 0f),
                Opacity = NumOr(cmd["opacity"], 1f),
                HoverOpacity = NumOr(cmd["hover_opacity"], 1f),
                EnterTransition = ParseTransition((string)cmd["enter"]),
                ExitTransition = ParseTransition((string)cmd["exit"]),
                TransitionDuration = NumOr(cmd["transition_duration"], 0.3f),
            };

            var anchor = (string)cmd["anchor"];
            if (!string.IsNullOrEmpty(anchor))
            {
                var parts = anchor.Split(',');
                if (parts.Length == 2
                    && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var ax)
                    && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var ay))
                {
                    p.AnchorX = ax;
                    p.AnchorY = ay;
                }
            }
            else
            {
                if (cmd["anchor_x"] != null) p.AnchorX = NumOr(cmd["anchor_x"], p.AnchorX);
                if (cmd["anchor_y"] != null) p.AnchorY = NumOr(cmd["anchor_y"], p.AnchorY);
            }
            return p;
        }

        // Like AxesFrom but with {var} interpolation against the player's variables,
        // so equipment can be data-driven: `actor hero armor={arm} weapon={wpn}`.
        // An axis that resolves to empty or stays unresolved is DROPPED, leaving its
        // {axis} token unfilled → that layer is skipped (the "nothing equipped" case).
        private Dictionary<string, string> AxesOf(JObject cmd)
        {
            var axes = AxesFrom(cmd);
            var vars = _player?.Vars;
            foreach (var k in new List<string>(axes.Keys))
            {
                var v = axes[k];
                if (!string.IsNullOrEmpty(v) && v.IndexOf('{') >= 0 && vars != null)
                    v = TextInterpolation.Apply(v, vars);
                if (string.IsNullOrEmpty(v) || v.IndexOf('{') >= 0) axes.Remove(k); // no value → no layer
                else axes[k] = v;
            }
            return axes;
        }

        // The actor command's free-form named fields (pose, emotion, prop, …) —
        // everything outside the reserved layout/control set — are the cast axes.
        internal static Dictionary<string, string> AxesFrom(JObject cmd)
        {
            var axes = new Dictionary<string, string>();
            foreach (var p in cmd.Properties())
            {
                if (ReservedActorFields.Contains(p.Name)) continue;
                switch (p.Value.Type)
                {
                    case JTokenType.String:
                    case JTokenType.Integer:
                    case JTokenType.Float:
                    case JTokenType.Boolean:
                        axes[p.Name] = p.Value.ToString();
                        break;
                }
            }
            return axes;
        }
    }
}
