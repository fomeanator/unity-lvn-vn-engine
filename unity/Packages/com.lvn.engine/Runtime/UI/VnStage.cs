using System.Collections.Generic;
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

        private BackgroundLayer _bg;
        private ActorLayer _actors;
        private DialogueBox _dialogue;
        private ChoiceList _choices;
        private FxLayer _fx;
        private LvnPlayer _player;
        private CancellationTokenSource _cts;
        private bool _awaitingTap;

        private void OnEnable()
        {
            var root = GetComponent<UIDocument>().rootVisualElement;
            if (root == null) return; // UIDocument not ready yet
            root.Clear();
            root.style.flexGrow = 1;

            _bg = new BackgroundLayer();
            _actors = new ActorLayer();
            _dialogue = new DialogueBox(Theme);
            _choices = new ChoiceList(Theme);
            _fx = new FxLayer();
            root.Add(_bg);
            root.Add(_actors);
            root.Add(_dialogue);
            root.Add(_choices);
            root.Add(_fx); // top: fades/dim veil everything below
            _choices.OnSelected += OnChoiceSelected;

            root.pickingMode = PickingMode.Position;
            root.RegisterCallback<PointerDownEvent>(OnPointerDown);

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
                case "fade": ApplyFade(command); break;
                case "dim": ApplyDim(command); break;
                // camera / particles / audio / wait / hint / preload: further
                // effect modules land in a later release; unknown-but-registered
                // ops are simply not yet rendered here.
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

        private async Task ApplyBgAsync(JObject cmd)
        {
            var url = (string)cmd["sprite_url"];
            if (Assets == null || string.IsNullOrEmpty(url)) return;
            var sprite = await Assets.LoadSpriteAsync(url, _cts.Token);
            if (sprite != null) _bg.SetSprite(sprite);
        }

        private async Task ApplyActorAsync(JObject cmd)
        {
            var id = (string)cmd["id"];
            bool show = cmd["show"] == null || (bool)cmd["show"];
            var position = (string)cmd["position"];
            float? x = cmd["x"] != null ? (float?)(float)cmd["x"] : null;
            float height = cmd["height"] != null ? (float)cmd["height"] : 0.62f;

            Sprite sprite = null;
            var url = (string)cmd["sprite_url"];
            if (Assets != null && !string.IsNullOrEmpty(url))
                sprite = await Assets.LoadSpriteAsync(url, _cts.Token);

            _actors.Apply(id, sprite, position, x, height, show);
        }
    }
}
