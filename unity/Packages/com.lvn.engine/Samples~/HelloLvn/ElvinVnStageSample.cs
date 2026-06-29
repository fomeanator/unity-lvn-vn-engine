using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.EventSystems;
using Lvn.UI;

namespace Lvn.Samples
{
    /// <summary>
    /// The fastest way to see a real game: put this on an empty GameObject and
    /// press Play. It creates a camera, an EventSystem, and a <see cref="VnStage"/>
    /// on its own UIDocument, then plays a bundled Elvin Script — no scene wiring,
    /// no server.
    ///
    /// The story (<c>Resources/hello.lvns</c>) is compiled to a playable asset
    /// automatically by the ScriptedImporter; the UI theme
    /// (<c>Resources/ElvinDefaultTheme.tss</c>) gives the dialogue text a font.
    /// Both ship with this sample. To play your own game, drop a <c>.lvns</c> into
    /// a Resources folder and set <see cref="scriptResourcePath"/>.
    ///
    /// (Verified to compile against the engine; see docs/unity-getting-started.md
    /// for the full visual-setup walkthrough and the headless console sample
    /// <see cref="HelloLvnRunner"/>.)
    /// </summary>
    public sealed class ElvinVnStageSample : MonoBehaviour
    {
        [Tooltip("Resources path to the Elvin Script (.lvns) or .lvn TextAsset to play.")]
        public string scriptResourcePath = "hello";

        [Tooltip("Resources path to a UI Toolkit ThemeStyleSheet so dialogue text has a font.")]
        public string themeResourcePath = "ElvinDefaultTheme";

        private void Awake()
        {
            if (FindFirstObjectByType<Camera>() == null)
            {
                var camGo = new GameObject("Main Camera");
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                camGo.tag = "MainCamera";
            }

            // UI Toolkit pointer events (tap-to-advance, choices) need an EventSystem.
            if (FindFirstObjectByType<EventSystem>() == null)
                new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));

            var script = Resources.Load<TextAsset>(scriptResourcePath);
            if (script == null)
            {
                Debug.LogError($"ElvinVnStageSample: no TextAsset at Resources/{scriptResourcePath}");
                return;
            }

            var theme = Resources.Load<ThemeStyleSheet>(themeResourcePath);
            if (theme == null)
                Debug.LogWarning($"ElvinVnStageSample: no ThemeStyleSheet at Resources/{themeResourcePath}; " +
                                 "dialogue text may not render. Assign a UI Toolkit theme to see it.");

            // PanelSettings drives the UIDocument (same pattern the novel-shell uses).
            var ps = ScriptableObject.CreateInstance<PanelSettings>();
            ps.themeStyleSheet = theme;

            // Build inactive so VnStage.OnEnable sees Script already assigned.
            var go = new GameObject("Elvin VnStage");
            go.SetActive(false);
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = ps;
            var stage = go.AddComponent<VnStage>();
            stage.Script = script;          // VnStage plays Script on enable
            go.SetActive(true);
        }
    }
}
