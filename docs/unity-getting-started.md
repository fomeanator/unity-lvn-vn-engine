# Getting started in Unity

From zero to a playable game. Two paths: the **5-minute sample** (press Play, see
a game) and the **your-own-game** path (drop a `.lvns`, wire one component).

Verified on Unity **6000.x** (works on 2022.3+). The whole chain below — a `.lvns`
compiled in-Unity, played by `VnStage` — is exercised by the package's EditMode
tests and was run end-to-end in the editor.

---

## 1. Install the package

**Package Manager → Add package from git URL:**

```
https://github.com/fomeanator/unity-lvn-vn-engine.git?path=/unity/Packages/com.lvn.engine
```

It pulls its one dependency (`com.unity.nuget.newtonsoft-json`) automatically.

## 2. Run the sample (≈5 minutes)

1. In Package Manager, select **Elvin — Narrative Game Engine → Samples** and
   **Import** "Hello Elvin".
2. Create an empty GameObject in a scene and add the **`ElvinVnStageSample`**
   component (or drop the script on it).
3. Press **Play**. A camera, an EventSystem, a `UIDocument` and a `VnStage` are
   created in code; the bundled story (`Resources/hello.lvns`, compiled
   automatically) plays — tap to advance, click choices.

That sample is the smallest self-contained setup: a story (`.lvns`), a UI theme
(`.tss`), and one bootstrap component. Copy it and swap in your own `.lvns`.

> Prefer no UI? The **`HelloLvnRunner`** sample is a headless `ILvnStage` that
> prints the story to the Console — the minimal proof the engine runs.

## 3. Make your own game

1. **Write the story.** Create a `.lvns` file anywhere under `Assets/` (start from
   `howto/CHEATSHEET.md` / a genre example). Unity compiles it to a playable asset
   automatically — the importer runs on save, no external tool.
2. **Put it on screen.** Either reuse `ElvinVnStageSample` (set its
   `scriptResourcePath` to your `.lvns` in a `Resources/` folder), or wire it by
   hand:
   - Add a GameObject with a **`UIDocument`** and a **`VnStage`** component.
   - Give the `UIDocument` a **PanelSettings** with a UI Toolkit **ThemeStyleSheet**
     (any theme; a one-line default `@import url("unity-theme://default");` works) —
     without a theme the dialogue text has no font.
   - Assign your compiled `.lvns` asset to `VnStage.Script`. It plays on enable.
3. **Press Play.**

```csharp
// Or drive it yourself for a custom skin (headless):
var doc    = LvnDocument.Parse(textAsset.text);
var player = new LvnPlayer(doc, myStage);   // myStage : ILvnStage
player.Advance();
```

## 4. Add art (optional, anytime)

Asset URLs in the script (`bg /content/bg/room.jpg`, `actor mara …`) are data.
Provide them by implementing **`ILvnAssets`** on `VnStage.Assets`, or run with no
provider to **greybox** (solid-colour backgrounds, missing sprites skipped). You
can write and play the whole game's logic before any art exists. Characters are
defined in the manifest's cast, not in the script — see `docs/cast.md` and
`howto/CAPABILITIES.md` §7.

## 5. Troubleshooting

- **Dialogue text invisible** → the `UIDocument`'s PanelSettings has no
  ThemeStyleSheet. Assign one (see step 3).
- **`.lvns` shows a red import error** → the script has a compile error; the
  message names the line. Fix and save; it re-imports. (`lvnconv validate` from the
  CLI gives the same checks.)
- **Nothing happens on Play** → `VnStage.Script` is unassigned, or there's no
  EventSystem in the scene (needed for tap/click). The sample creates one for you.

## See also

- [`howto/`](../howto/) — language reference, capabilities/limits, genre examples
- [`howto/CHEATSHEET.md`](../howto/CHEATSHEET.md) — the whole syntax on one page
- [`unity/Packages/com.lvn.engine/README.md`](../unity/Packages/com.lvn.engine/README.md) — runtime API
