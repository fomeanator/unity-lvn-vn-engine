# Changelog

All notable changes to the LVN Engine package are documented here. The format
follows [Keep a Changelog](https://keepachangelog.com/); versions are SemVer.

## [Unreleased]

### Added
- **Manifest-driven screen kit** (`Lvn.UI.Screens`) — three fully themeable novel
  screens built in code from the manifest's `ui` block: `LoadingScreen` (backdrop,
  scrim, fog, progress bar with optional track/fill/frame art, percent / current-file
  / rotating-tip labels), `TitleCard` (chapter + subtitle reveal with fog and frame),
  and `NameInputScreen` (backdrop, character art, prompt, field, confirm). Every
  colour, image url, text, size and duration comes from `LvnUiConfig`
  (`loading` / `title` / `name_input`); all optional with sensible defaults. The bar
  maths (`LoadingProgressModel`, `ProgressRenderGate`) and the name rules
  (`PlayerNameInput`) are pure and unit-tested. Referenced from Liminal's shipping
  loading/title/name-input screens.
- **Content pipeline** (`Lvn.Content`) — a networked, disk-cached content system
  ported from a shipping VN client. `ContentLoader` (sha1(url@version) disk cache,
  in-memory sprite cache, dedup of parallel fetches, `asset-versions.json`
  cache-busting, byte-level progress, resumable retries, pipelined preload batch,
  audio via `UnityWebRequestMultimedia`); `AssetScheduler` (prioritized
  required/deferred release set, per-tier concurrency, EDF ordering);
  `DownloadPolicy` (pure URL classification); `DownloadManager` (four phases —
  boot / menu refresh / chapter entry / in-game look-ahead — over a generic
  `LvnManifest`/`LvnTitle`/`LvnSeason`/`LvnChapter` model). Bridged to the engine
  via `CachingAssets : ILvnAssets`.
- **Composable asset loaders** — `MemoryCache` (L1), `ChainAssets` (try loaders in
  order), and an optional Addressables backend (`Lvn.Engine.Addressables`, an
  assembly auto-gated by the `com.unity.addressables` package — zero footprint
  when it isn't installed).
- **`flash` op** — quick coloured flash (white/red/etc.) that fades back to clear.
- **`tint` op** — coloured tint wash (cold/warm/sepia) with configurable alpha.
- **`blur` op** — blur overlay for depth-of-field simulation.
- **`text_pace` op** — global characters-per-second override for typewriter speed.
- **`camera` pan** — smooth camera pan to target x/y coordinates.
- **Actor transitions** — `enter`/`exit` fields on `actor` op: `fade`, `slide_left`,
  `slide_right`, `pop` animations with configurable duration.
- **Backlog UI** — `BacklogPanel` component for scrollable dialogue history.
- **Premium Meta-Shell** — `HubScreen` (chapter select), `LifeCardSystem`
  (lives/regen), `PaywallGate` (IAP prompt).
- **`wait` op** — blocking pause with configurable duration (`ms`). The player
  halts execution and resumes automatically after the delay.
- **`preload` op** — speculative asset loading. Non-blocking: the player
  continues immediately while assets load in the background.
- **Backlog** — `LvnPlayer.OnSay` event fires on every `say` command. `VnStage`
  records dialogue history in `Backlog` (read-only list of who/text/style).
- **Hover feedback** — `hover_opacity` field on `actor`/`obj` ops. Hotspots
  brighten on mouse-enter and restore on mouse-leave.
- **Richer `on_click`** — `on_click` now accepts an object: `{ "goto": "label",
  "set": { "key": value } }`. The `set` ops run before the jump.
- **Save/Load** — `LvnPlayer.Save()` returns an `LvnSnapshot` (IP, vars, call
  stack). `Restore(LvnSnapshot)` resumes. `SaveLoadPanel` provides a slot-based
  UI for save/load.
- `FxLayer` — full-screen effects overlay; `VnStage` now renders the `fade`
  (to black/white/clear) and `dim` (focus-pull) ops as animated veils.
- `CameraRig` — `camera` op: shake (diminishing jitter) and zoom on the world
  layer, leaving the UI chrome steady.
- `ParticleField` — `particles` op: procedural rain / snow weather, no textures.
- Audio: `audio` op with music / ambient / sfx channels, looping beds and
  one-shot sfx, with volume fades. `ILvnAssets` gains `LoadAudioAsync`.
- `VnStage` wraps background + actors in a "world" layer so camera effects move
  the scene but not the dialogue/choices.

- **Cast — named, parametric sprite entities** (`SpriteComposer` + the `cast`
  block). A character is a list of layer URL templates parameterised by named
  axes (pose, emotion, outfit…); the `actor` command names the entity and the
  axis values, and the runtime fills the templates and stacks the layers.
  K poses + M emotions need K + M images, not K × M. Pure, engine-agnostic
  resolution — see `docs/cast.md`. `ActorLayer` now composites layered sprites.
- `actor` also takes direct `body_url` / `clothes_url` / `hair_url` layers
  (composited bottom-to-top) for characters authored without a cast block.
- `DirectoryAssets` — a reference `ILvnAssets` that loads sprites from a local
  folder (offline/bundled content, and for tests).
- **Full object placement** — `actor`/`obj` place any sprite by screen fraction:
  `x`/`y`, `width`/`height`, `anchor` (pivot %), `z` (paint order), `flip`,
  `rotation`, `opacity`, plus named slots `far_left`…`far_right`. `obj` puts any
  sprite on screen; `actor` is the same with speaker dimming. See `docs/placement.md`.
- **Clickable hotspots** — `on_click: "label"` makes any object tappable; the tap
  jumps the script (via `LvnPlayer.GoTo`) and is swallowed so it doesn't advance
  the dialogue. With placement + flow + state, the engine assembles button-driven
  games (menus, point-and-click), not only visual novels.

### Verified
- Live in Unity 6: rain renders over the dialogue while the typewriter reveals
  the line; a two-layer cast character (body + face) composites correctly.
- Played a real 338-command production VN chapter end-to-end (its own
  backgrounds, layered characters, fades/camera/dim/particles) through the
  engine via `DirectoryAssets` — characters composite from their body/outfit
  layers over the real art.
- 15/15 EditMode tests green (expression, player, sprite composer).
- New tests: `WaitPreloadTests`, `BacklogTests`, `HotspotTests`, `SaveLoadTests`.

### Added
- `VnStage.ContentRoot` — a serialized content-folder path. When set (and
  `Assets` is unwired) the stage auto-creates a `DirectoryAssets`, so a scene
  plays with real art straight from Play with no code.

### Fixed
- **Compile blockers** that broke the whole `Lvn.Engine.UI` assembly: (1)
  `DirectoryAssets.LoadAudioAsync` constructed an `AudioClip` (no public ctor) and
  called `AudioClip.Create`/`SetData` on a background thread — replaced with
  `UnityWebRequestMultimedia` decoding a `file://` url on the main thread (handles
  wav/ogg/mp3); (2) `CameraRig.Pan` compared a `Length` struct against `null` —
  now reads `.value` directly; (3) the Addressables loader referenced a
  non-dependency package — moved into a separate assembly auto-gated by
  `com.unity.addressables`, so the package compiles with or without it.
- Freeze on click / advancing to the next op: `DirectoryAssets` decoded large
  textures synchronously on the main thread for every show (no cache), so each
  transition that revealed a background or character hitched. It now caches
  sprites by url (instant re-show) and reads files off the main thread, so the
  click → `Advance` path no longer blocks on a decode (measured ~1 ms per op).
- `LvnPlayer.Advance` now guards against a cyclic `goto` with no pause between
  jumps — it fails loudly instead of spinning the main thread forever.
- Black screen on play, two causes: (1) `VnStage` could miss building its layers
  when `UIDocument.rootVisualElement` was still null in `OnEnable` (a script-order
  race) — it now builds in `OnEnable` **and** `Start`; (2) the asset loader was
  code-only (always null on a plain Play, so backgrounds/characters never loaded)
  — `ContentRoot` fixes that from the Inspector.

## [0.2.0] — 2026-06-20

### Added
- `LvnExpression` — built-in evaluator for string `expr` conditions
  (`|| && !`, comparisons, arithmetic, strings; unset vars default like ink).
  `if` and option `expr` filters now work out of the box; `LvnPlayer.ExprEvaluator`
  becomes an optional override rather than a requirement.
- `LvnException` — runtime error type for malformed scripts/expressions.
- Reference UI Toolkit component set (`Lvn.Engine.UI`): `VnTheme`, `DialogueBox`,
  `ChoiceList`, `BackgroundLayer`, `ActorLayer`, `ILvnAssets`, and `VnStage` —
  a `MonoBehaviour : ILvnStage` drop-in that plays a `.lvn` in a `UIDocument`.
  Plus `RichTextTypewriter` / `TypewriterClock` (typewriter core).

### Fixed
- An unset variable now compares as 0 / false / "" (ink defaulting), so
  once-only choice gates (`__once == 0`) and first-visit checks pass on the
  first pass instead of filtering every option out.

### Tests
- EditMode tests for `LvnExpression` and `LvnPlayer` (flow, set/inc, once-only
  gating, call/return tunnels) — 11/11 green in Unity 6's Test Runner, with
  regression cover for the unset-variable fix at both the expression and player
  levels.

### Verified
- The full engine plays a `.lvn` end-to-end in Unity 6 (6000.4): scene → stage →
  dialogue with typewriter → branching choice. Compiles clean, runs error-free.
- Ships with `.meta` files (stable GUIDs) for clean Package Manager installs.

## [0.1.0] — 2026-06-20

### Added
- `LvnDocument` — parse the `.lvn` container (Newtonsoft-backed command list).
- `LvnPlayer` — the interpreter: cursor, variable bag, and flow control for
  `goto` / `if` / `choice` / `call`-`return`, with autosave snapshot/restore.
- `ILvnStage` — the host contract (say, choice, stage commands, end).
- `LvnOption`, `StagingOps` — choice presentation and the op registry.
- Pluggable `ExprEvaluator` hook for string `expr` conditions.
- **Hello LVN** sample: a console host that plays a bundled `.lvn`.

### Known gaps (planned)
- Reference UI Toolkit component set (dialogue box, choice list, background,
  actor layer) — the drop-in "constructor" rendering layer.
- Effect modules (camera, particles, tint) and the layered-sprite compositor.
- Premium meta-shell template (hub / life-card / paywall).
