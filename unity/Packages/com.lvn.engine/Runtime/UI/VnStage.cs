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

        private VisualElement _world;   // bg + actors, the camera target (UITK path)
        private BackgroundLayer _bg;
        private ActorLayer _actors;
        private CameraRig _camera;
        private World.WorldStage _scene; // uGUI scene path (when UseCanvasScene)
        private ParticleField _particles;
        private DialogueBox _dialogue;
        private ChoiceList _choices;
        private VisualElement _labelLayer; // reactive HUD/stat text overlay (the `text` op)
        private readonly Dictionary<string, Label> _labelEls = new Dictionary<string, Label>();
        private readonly Dictionary<string, string> _labelTmpl = new Dictionary<string, string>(); // id → live `{expr}` template
        private FxLayer _fx;
        private StageAudio _audio;
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
                _scene = new World.WorldStage(transform, sortingOrder: 0);
                _scene.SetBackgroundColor(Color.black);
            }
            else
            {
                _world = new VisualElement { name = "vn-world", pickingMode = PickingMode.Ignore };
                _world.style.position = Position.Absolute;
                _world.style.left = 0; _world.style.right = 0; _world.style.top = 0; _world.style.bottom = 0;
                _bg = new BackgroundLayer();
                _actors = new ActorLayer();
                _world.Add(_bg);
                _world.Add(_actors);
                _camera = new CameraRig(_world);
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
            _choices.OnSelected += OnChoiceSelected;

            // Reactive tick: re-evaluate every live label's {expr} template against the
            // current variables so on-screen stats track changes (incl. background ones).
            root.schedule.Execute(RefreshLabels).Every(200);

            root.pickingMode = PickingMode.Position;
            root.RegisterCallback<PointerDownEvent>(OnPointerDown);

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
            _choices = new ChoiceList(Theme);
            int fxIndex = root.IndexOf(_fx); // keep z-order: …, dialogue, choices, fx
            if (fxIndex < 0) fxIndex = root.childCount;
            root.Insert(fxIndex, _dialogue);
            root.Insert(fxIndex + 1, _choices);
            _choices.OnSelected += OnChoiceSelected;

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

        /// <summary>Parse and start playing a .lvn document.</summary>
        public void Play(string lvnJson)
        {
            var doc = LvnDocument.Parse(lvnJson);
            LvnPlayer.Log?.Invoke("════ PLAY scene=" + doc.Scene + " (" + (doc.Script?.Count ?? 0) + " cmds) ════");
            _cast = SpriteComposer.ParseCast(doc.Cast);
            ResetStage();
            _player = new LvnPlayer(doc, this);
            _player.Strings = Strings; // localization catalog (text_id → string), if any
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
            _actors?.RemoveAll();
            _scene?.RemoveAll();
            _scene?.ResetCamera(0f);
            _talkAnims.Clear();
            _bg?.SetColor(Color.clear);
            _particles?.Set("rain", false);
            _particles?.Set("snow", false);
            _fx?.Clear(0f);
            _fx?.ClearBlur(0f);
            _camera?.Reset(0f);
            _backlog.Clear();
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
            => _backlog.Add((who, text, style));

        private void OnPointerDown(PointerDownEvent evt)
        {
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
            if (_scene == null || panelW <= 0f || panelH <= 0f) return null;
            float nx = panelPos.x / panelW, ny = panelPos.y / panelH; // UITK: top-left, y-down
            float sw = Screen.width, sh = Screen.height;
            if (sw <= 0f || sh <= 0f) return null;
            var c = new Vector3[4];
            for (int i = _hotspots.Count - 1; i >= 0; i--)
            {
                var a = _scene.ActorFor(_hotspots[i].id);
                if (a == null || a.Slot == null) continue;
                a.Slot.GetWorldCorners(c); // ScreenSpaceOverlay → screen pixels (y-up)
                float left = Mathf.Min(c[0].x, c[2].x) / sw, right = Mathf.Max(c[0].x, c[2].x) / sw;
                float top = 1f - Mathf.Max(c[0].y, c[2].y) / sh, bot = 1f - Mathf.Min(c[0].y, c[2].y) / sh;
                if (nx >= left && nx <= right && ny >= top && ny <= bot) return _hotspots[i].onClick;
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
        }

        // ── ILvnStage ─────────────────────────────────────────────────────────

        public void ShowSay(string who, string text, string style)
        {
            _dialogue.SetSpeaker(who);
            _dialogue.ApplyStyle(style);
            _dialogue.Reveal(text);
            _awaitingTap = true;
            _sayUp = true;
            _curChoices = null;

            // Classic VN focus: the speaker is at full opacity, everyone else present
            // dims — so a two-shot reads as "this one is talking" instead of a flat row.
            SceneHighlightSpeaker(who);

            // Lip-sync: only the speaking actor's mouth moves while the line is up.
            var spId = ResolveSpeakerId(who);
            foreach (var kv in _talkAnims) SceneTalk(kv.Key, kv.Value, kv.Key == spId);
        }

        // Scene dispatch: route actor placement/animation to whichever renderer is
        // live (uGUI WorldStage when UseCanvasScene, else the UITK ActorLayer), so
        // the ILvnStage logic stays renderer-agnostic.
        private void SceneSetFrames(string id, Dictionary<string, Dictionary<string, Sprite>> frames)
        { if (UseCanvasScene) _scene?.SetFrames(id, frames); else _actors?.SetFrames(id, frames); }
        private void SceneEnsureIdle(string id, LvnAnim a) { if (UseCanvasScene) _scene?.EnsureIdle(id, a); else _actors?.EnsureIdle(id, a); }
        private void SceneEnsureBlink(string id, LvnAnim a) { if (UseCanvasScene) _scene?.EnsureBlink(id, a); else _actors?.EnsureBlink(id, a); }
        private void ScenePlayGesture(string id, LvnAnim g, LvnAnim idle) { if (UseCanvasScene) _scene?.PlayGesture(id, g, idle); else _actors?.PlayGesture(id, g, idle); }
        private void ScenePlayAnim(string id, string channel, LvnAnim a) { if (UseCanvasScene) _scene?.PlayAnim(id, channel, a); else _actors?.PlayAnim(id, channel, a); }
        private void ScenePlayAnimQueued(string id, string channel, LvnAnim a) { if (UseCanvasScene) _scene?.PlayAnimQueued(id, channel, a); else _actors?.PlayAnimQueued(id, channel, a); }
        private void SceneStopAnim(string id, string target) { if (UseCanvasScene) _scene?.StopAnim(id, target); else _actors?.StopAnim(id, target); }
        private void SceneTalk(string id, LvnAnim t, bool on) { if (UseCanvasScene) _scene?.Talk(id, t, on); else _actors?.Talk(id, t, on); }
        private void SceneHighlightSpeaker(string who) { if (UseCanvasScene) _scene?.HighlightSpeaker(who); else _actors?.HighlightSpeaker(who); }

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
            ResetStage();                       // clean slate
            _player.Restore(snap);              // cursor (via label anchor) + vars + call stack
            int at = _player.Index;             // the anchor-relocated cursor, not the raw saved index
            _player.ReplayVisuals(at);          // rebuild bg / actors / HUD up to the saved point
            _player.ContinueFrom(at);           // resume → renders the saved beat
        }

        // A persistent reactive text label (`text id=… x= y= anchor= «{expr}»`): a
        // HUD/stat readout placed like an actor but living in the UITK overlay. Its
        // {expr} template is re-evaluated on the reactive tick, so the shown value
        // tracks the variable. Re-issuing the same id updates it; `hide` removes it.
        private void ApplyText(JObject cmd)
        {
            var id = (string)cmd["id"];
            if (string.IsNullOrEmpty(id) || _labelLayer == null) return;

            if (cmd["hide"] != null && (bool)cmd["hide"])
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

        private static float NumOr(JToken t, float dflt)
        {
            if (t == null) return dflt;
            try { return (float)t; } catch { return dflt; }
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
                    _particles.Set((string)command["type"], command["on"] == null || (bool)command["on"]);
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
            float ms = cmd["ms"] != null ? (float)cmd["ms"] : 1000f;
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
            float dur = cmd["duration"] != null ? (float)cmd["duration"] : 0.5f;
            if (to == "clear" || to == "none") _fx.Clear(dur);
            else _fx.Fade(to == "white" ? Color.white : Color.black, dur);
        }

        private void ApplyDim(JObject cmd)
        {
            float alpha = cmd["alpha"] != null ? (float)cmd["alpha"] : 0.4f;
            float dur = cmd["duration"] != null ? (float)cmd["duration"] : 0.5f;
            _fx.Dim(alpha, dur);
        }

        private void ApplyFlash(JObject cmd)
        {
            var colour = ParseColor((string)cmd["color"], Color.white);
            float dur = cmd["duration"] != null ? (float)cmd["duration"] : 0.2f;
            _fx.Flash(colour, dur);
        }

        private void ApplyTint(JObject cmd)
        {
            var colour = ParseColor((string)cmd["color"], Color.white);
            float alpha = cmd["alpha"] != null ? (float)cmd["alpha"] : 0.3f;
            float dur = cmd["duration"] != null ? (float)cmd["duration"] : 0.5f;
            _fx.Tint(colour, alpha, dur);
        }

        private void ApplyBlur(JObject cmd)
        {
            float alpha = cmd["alpha"] != null ? (float)cmd["alpha"] : 0.5f;
            float dur = cmd["duration"] != null ? (float)cmd["duration"] : 0.5f;
            if (alpha <= 0f) _fx.ClearBlur(dur);
            else _fx.Blur(alpha, dur);
        }

        private void ApplyTextPace(JObject cmd)
        {
            float cps = cmd["cps"] != null ? (float)cmd["cps"] : 0f;
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
            float dur = cmd["duration"] != null ? (float)cmd["duration"] : 0.3f;
            switch ((string)cmd["action"])
            {
                case "shake":
                {
                    float amp = cmd["amplitude"] != null ? (float)cmd["amplitude"] : 8f;
                    if (UseCanvasScene) _scene?.Shake(amp, dur); else _camera.Shake(amp, dur);
                    break;
                }
                case "zoom":
                {
                    float factor = cmd["factor"] != null ? (float)cmd["factor"] : 1.2f;
                    if (UseCanvasScene) _scene?.Zoom(factor, dur); else _camera.Zoom(factor, dur);
                    break;
                }
                case "pan":
                {
                    float px = cmd["x"] != null ? (float)cmd["x"] : 0f;
                    float py = cmd["y"] != null ? (float)cmd["y"] : 0f;
                    if (UseCanvasScene) _scene?.Pan(px, py, dur); else _camera.Pan(px, py, dur);
                    break;
                }
                case "reset":
                    if (UseCanvasScene) _scene?.ResetCamera(dur); else _camera.Reset(dur);
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
            if (UseCanvasScene) _scene?.SetBackgroundSprite(sprite);
            else _bg.SetSprite(sprite);
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
            if (UseCanvasScene)
            {
                _scene?.ApplyActor(id, null, placement, null, null); // create + place now; art loads below
                _hotspots.RemoveAll(h => h.id == id);
                if (onClick != null && placement.Show) _hotspots.Add((id, onClick));
            }

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

            if (UseCanvasScene)
            {
                if (layers != null && layers.Count > 0)
                    _scene?.ApplyActor(id, layers, placement, layerIds, layerRects);
            }
            else _actors.Apply(id, layers, placement, onClick, layerIds, layerRects);

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
                Show = cmd["show"] == null || (bool)cmd["show"],
                X = cmd["x"] != null ? (float)cmd["x"] : ActorLayer.SlotX((string)cmd["position"]),
                Y = cmd["y"] != null ? (float)cmd["y"] : 1f,
                Width = cmd["width"] != null ? (float?)(float)cmd["width"] : null,
                Height = cmd["height"] != null ? (float?)(float)cmd["height"] : null,
                AnchorX = 0.5f,
                AnchorY = 1f,
                Z = cmd["z"] != null ? (int?)(int)cmd["z"] : null,
                Flip = cmd["flip"] != null && (bool)cmd["flip"],
                Rotation = cmd["rotation"] != null ? (float)cmd["rotation"] : 0f,
                Opacity = cmd["opacity"] != null ? (float)cmd["opacity"] : 1f,
                HoverOpacity = cmd["hover_opacity"] != null ? (float)cmd["hover_opacity"] : 1f,
                EnterTransition = ParseTransition((string)cmd["enter"]),
                ExitTransition = ParseTransition((string)cmd["exit"]),
                TransitionDuration = cmd["transition_duration"] != null ? (float)cmd["transition_duration"] : 0.3f,
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
                if (cmd["anchor_x"] != null) p.AnchorX = (float)cmd["anchor_x"];
                if (cmd["anchor_y"] != null) p.AnchorY = (float)cmd["anchor_y"];
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
