using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
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

        [Tooltip("Optional content folder. If set and Assets is unwired, the stage " +
                 "loads sprites from here via DirectoryAssets — so a scene plays with " +
                 "art straight from Play, no code. Editor/standalone file paths.")]
        public string ContentRoot;

        private VisualElement _world;   // bg + actors, the camera target
        private BackgroundLayer _bg;
        private ActorLayer _actors;
        private CameraRig _camera;
        private ParticleField _particles;
        private DialogueBox _dialogue;
        private ChoiceList _choices;
        private FxLayer _fx;
        private AudioSource _music, _ambient, _sfx;
        private Dictionary<string, CastEntity> _cast;
        private LvnPlayer _player;
        private CancellationTokenSource _cts;
        private bool _awaitingTap;

        private bool _built;

        // UIDocument's rootVisualElement can be null in OnEnable (it initializes
        // its panel on its own OnEnable, and script order isn't guaranteed), so we
        // also try in Start, by which point the panel is ready. Whichever sees a
        // non-null root first builds; the other is a no-op.
        private void OnEnable() => Build();
        private void Start() => Build();

        private void Build()
        {
            if (_built) return;
            var root = GetComponent<UIDocument>().rootVisualElement;
            if (root == null) return; // panel not ready yet — Start will retry
            _built = true;

            if (Assets == null && !string.IsNullOrEmpty(ContentRoot))
                Assets = new DirectoryAssets(ContentRoot);
            root.Clear();
            root.style.flexGrow = 1;

            // World = background + actors, wrapped so camera shake/zoom moves
            // the scene but not the dialogue/choice chrome above it.
            _world = new VisualElement { name = "vn-world", pickingMode = PickingMode.Ignore };
            _world.style.position = Position.Absolute;
            _world.style.left = 0; _world.style.right = 0; _world.style.top = 0; _world.style.bottom = 0;
            _bg = new BackgroundLayer();
            _actors = new ActorLayer();
            _world.Add(_bg);
            _world.Add(_actors);
            _camera = new CameraRig(_world);

            _particles = new ParticleField();
            _dialogue = new DialogueBox(Theme);
            _choices = new ChoiceList(Theme);
            _fx = new FxLayer();

            root.Add(_world);
            root.Add(_particles);   // weather sits over the scene, under the UI
            root.Add(_dialogue);
            root.Add(_choices);
            root.Add(_fx);          // top: fades/dim veil everything below
            _choices.OnSelected += OnChoiceSelected;

            root.pickingMode = PickingMode.Position;
            root.RegisterCallback<PointerDownEvent>(OnPointerDown);

            // Audio channels: looping music/ambient beds and a one-shot sfx bus.
            _music = gameObject.AddComponent<AudioSource>();
            _ambient = gameObject.AddComponent<AudioSource>();
            _sfx = gameObject.AddComponent<AudioSource>();
            foreach (var s in new[] { _music, _ambient, _sfx }) s.playOnAwake = false;
            _music.loop = true;
            _ambient.loop = true;

            _cts = new CancellationTokenSource();
            if (Script != null) Play(Script.text);
        }

        private void OnDisable()
        {
            _cts?.Cancel();
            if (_choices != null) _choices.OnSelected -= OnChoiceSelected;
        }

        /// <summary>Parse and start playing a .lvn document.</summary>
        public void Play(string lvnJson)
        {
            var doc = LvnDocument.Parse(lvnJson);
            _cast = SpriteComposer.ParseCast(doc.Cast);
            _player = new LvnPlayer(doc, this);
            _player.Advance();
        }

        private void OnPointerDown(PointerDownEvent _)
        {
            if (_player == null || _player.Finished) return;
            if (_dialogue.IsRevealing) { _dialogue.Complete(); return; }
            if (_awaitingTap)
            {
                _awaitingTap = false;
                _player.Advance();
            }
        }

        private void OnChoiceSelected(int index)
        {
            _choices.Dismiss();
            _awaitingTap = false;
            if (_player == null) return;
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
        }

        public void ShowChoice(IReadOnlyList<LvnOption> options)
        {
            _awaitingTap = false;
            _choices.Present(options);
        }

        public void ApplyStage(JObject command)
        {
            switch ((string)command["op"])
            {
                case "bg": _ = ApplyBgAsync(command); break;
                case "actor": _ = ApplyActorAsync(command); break;
                case "obj": _ = ApplyActorAsync(command); break; // any placeable sprite
                case "fade": ApplyFade(command); break;
                case "dim": ApplyDim(command); break;
                case "camera": ApplyCamera(command); break;
                case "particles":
                    _particles.Set((string)command["type"], command["on"] == null || (bool)command["on"]);
                    break;
                case "audio": _ = ApplyAudioAsync(command); break;
                // wait / hint / preload are loader/pacing hints handled elsewhere
                // or no-ops here; unknown-but-registered ops are simply not drawn.
            }
        }

        public void OnEnd()
        {
            _dialogue.SetSpeaker(null);
            _dialogue.SetText(string.Empty);
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

        private void ApplyCamera(JObject cmd)
        {
            float dur = cmd["duration"] != null ? (float)cmd["duration"] : 0.3f;
            switch ((string)cmd["action"])
            {
                case "shake":
                    _camera.Shake(cmd["amplitude"] != null ? (float)cmd["amplitude"] : 8f, dur);
                    break;
                case "zoom":
                    _camera.Zoom(cmd["factor"] != null ? (float)cmd["factor"] : 1.2f, dur);
                    break;
                case "reset":
                    _camera.Reset(dur);
                    break;
            }
        }

        private async Task ApplyAudioAsync(JObject cmd)
        {
            var channel = (string)cmd["channel"] ?? "sfx";
            var src = channel == "music" ? _music : channel == "ambient" ? _ambient : _sfx;
            float fade = cmd["fade"] != null ? (float)cmd["fade"] : 0f;

            if ((string)cmd["action"] == "stop")
            {
                if (fade > 0f) StartCoroutine(FadeAudio(src, src.volume, 0f, fade, stopAtEnd: true));
                else src.Stop();
                return;
            }

            var url = (string)cmd["url"];
            if (Assets == null || string.IsNullOrEmpty(url)) return;

            AudioClip clip = null;
            try { clip = await Assets.LoadAudioAsync(url, _cts.Token); }
            catch { /* silent if the host ships no audio */ }
            if (clip == null) return;

            float volume = cmd["volume"] != null ? (float)cmd["volume"] : 1f;
            if (channel != "sfx") src.loop = cmd["loop"] == null || (bool)cmd["loop"];
            src.clip = clip;
            if (fade > 0f)
            {
                src.volume = 0f;
                src.Play();
                StartCoroutine(FadeAudio(src, 0f, volume, fade, stopAtEnd: false));
            }
            else
            {
                src.volume = volume;
                src.Play();
            }
        }

        private static IEnumerator FadeAudio(AudioSource src, float from, float to, float seconds, bool stopAtEnd)
        {
            float t = 0f;
            while (t < seconds)
            {
                t += Time.unscaledDeltaTime;
                src.volume = Mathf.Lerp(from, to, Mathf.Clamp01(t / seconds));
                yield return null;
            }
            src.volume = to;
            if (stopAtEnd) src.Stop();
        }

        private async Task ApplyBgAsync(JObject cmd)
        {
            var url = (string)cmd["sprite_url"];
            if (Assets == null || string.IsNullOrEmpty(url)) return;
            var sprite = await Assets.LoadSpriteAsync(url, _cts.Token);
            if (sprite != null) _bg.SetSprite(sprite);
        }

        private static readonly HashSet<string> ReservedActorFields = new HashSet<string>
        {
            "op", "id", "show", "position", "x", "y", "width", "height", "scale",
            "anchor", "anchor_x", "anchor_y", "z", "flip", "rotation", "opacity",
            "on_click", "breathing", "sprite_url", "body_url", "clothes_url", "hair_url",
            "transition", "enter", "exit",
        };

        private async Task ApplyActorAsync(JObject cmd)
        {
            var id = (string)cmd["id"];
            if (string.IsNullOrEmpty(id)) return;

            List<Sprite> layers = null;

            if (_cast != null && _cast.TryGetValue(id, out var entity))
            {
                // Named entity: resolve its layer templates by the command's axes.
                var urls = SpriteComposer.Resolve(entity, AxesFrom(cmd));
                if (urls.Count > 0 && Assets != null)
                {
                    layers = new List<Sprite>(urls.Count);
                    foreach (var u in urls)
                    {
                        Sprite s = null;
                        try { s = await Assets.LoadSpriteAsync(u, _cts.Token); }
                        catch { }
                        if (s != null) layers.Add(s);
                    }
                }
            }
            else
            {
                // Direct layers: body → clothes → hair (skipping any absent),
                // or a single sprite_url for a flat character.
                var urls = new List<string>();
                var body = (string)cmd["body_url"]; if (!string.IsNullOrEmpty(body)) urls.Add(body);
                var clothes = (string)cmd["clothes_url"]; if (!string.IsNullOrEmpty(clothes)) urls.Add(clothes);
                var hair = (string)cmd["hair_url"]; if (!string.IsNullOrEmpty(hair)) urls.Add(hair);
                if (urls.Count == 0)
                {
                    var sp = (string)cmd["sprite_url"]; if (!string.IsNullOrEmpty(sp)) urls.Add(sp);
                }
                if (urls.Count > 0 && Assets != null)
                {
                    layers = new List<Sprite>(urls.Count);
                    foreach (var u in urls)
                    {
                        Sprite s = null;
                        try { s = await Assets.LoadSpriteAsync(u, _cts.Token); }
                        catch { }
                        if (s != null) layers.Add(s);
                    }
                }
            }

            System.Action onClick = null;
            var clickTarget = (string)cmd["on_click"];
            if (!string.IsNullOrEmpty(clickTarget))
                onClick = () =>
                {
                    if (_player == null) return;
                    _player.GoTo(clickTarget);
                    _awaitingTap = false;
                    _choices.Dismiss();
                    _player.Advance();
                };

            _actors.Apply(id, layers, PlacementFrom(cmd), onClick);
        }

        // Build placement from the command — everything in screen fractions so a
        // script controls any object's position, size, anchor, z, flip, rotation
        // and opacity without knowing the resolution.
        private static Placement PlacementFrom(JObject cmd)
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

        // The actor command's free-form named fields (pose, emotion, prop, …) —
        // everything outside the reserved layout/control set — are the cast axes.
        private static Dictionary<string, string> AxesFrom(JObject cmd)
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
