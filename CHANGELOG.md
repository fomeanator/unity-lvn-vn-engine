# Changelog

All notable changes to Elvin. Format based on [Keep a Changelog](https://keepachangelog.com/);
the project aims for [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- **Stat-gated & paid choices** (`choice` options): a failed `requires_stat` skill
  check or an unaffordable structured `cost` `{var, amount}` now *locks* the option
  (shown greyed with a reason) instead of hiding it — Disco-Elysium style — and a
  paid option's cost is deducted from its variable on pick. `expr` still hides;
  `hide_if_locked`/`locked_text` tune it. `.lvns` shorthand `cost=gold:5`. The ink
  and articy importers already emit this shape, so imported paywalled/skill choices
  light up automatically.
- **Asset-streaming end-to-end**: chapter entry now runs the prioritized
  `AssetScheduler` — the loading screen waits on a chapter's *required* (critical)
  assets and shows real download progress, then *deferred* assets stream in during
  play and pre-pull the next chapter. Gated by the ported `OfflinePolicy` so a
  fully-cached or offline chapter plays instantly and never hangs (wires
  `NovelApp` ↔ `DownloadManager`/`AssetScheduler`, previously dead code).
- **`wait until=preload`**: pause the script until assets are loaded (its own
  `assets`/`urls`, else the pending `preload` batch) with an optional `min_ms`
  floor — the script-level counterpart to the chapter-entry asset gate.
- **Robust save slots** (`LvnSaveStore`): save snapshots now carry a schema
  `Version` and migrate on load (an old save upgrades; a save from a newer build
  is refused rather than misread), a corrupt slot reads back as absent instead of
  throwing, and a slot index makes saves listable/deletable (`List`/`Delete`).
  `VnStage.ScriptUrl` stamps which chapter a slot belongs to. `load` clamps a stale
  cursor into range so a resume never runs off a shortened script. Backend is
  injectable (`ILvnKeyStore`: PlayerPrefs in-build, in-memory in tests). The drop-in
  `SaveLoadPanel` now persists through this store (slots survive a quit, show their
  timestamp, and a load rebuilds the scene via `ResumeFrom`) instead of holding
  snapshots in memory.
- **Autosave & resume** (`NovelApp`): chapter progress autosaves on each beat
  (conflated through `CoalescingWriter`) and re-entering a chapter resumes exactly
  where the player left off — surviving app exit and a script that changed length
  since, via `ResumePlanner` (rewind to the edit point / clamp; never reset to 0 and
  lose the player name). `VnStage.ResumeFrom` is the cross-session counterpart of the
  in-script `load`. (Wires up `ResumePlanner`/`CoalescingWriter`, previously only
  unit-tested.)
- **In-Unity `.lvns` importer** — a `ScriptedImporter` compiles Elvin Script to a
  playable `.lvn` asset on import; no external CLI needed
  (`unity/.../Editor/LvnsImporter.cs`).
- **C# Elvin Script compiler** — a faithful port of the Go transcoder
  (`unity/.../Editor/LvnsCompiler.cs`), guarded against drift by a golden corpus
  (13 `.lvns` + Go-produced `.lvn`) and an EditMode parity test. Verified: all
  EditMode tests pass; a `.lvns` imports and plays through `VnStage` end-to-end.
- **`ElvinVnStageSample`** — a one-component, self-contained visual sample
  (story + theme bundled): add it, press Play, see a game.
- **`docs/unity-getting-started.md`** — zero-to-playable Unity guide.
- **`howto/`** — a build-a-game kit: language reference, a code-verified
  capabilities/limitations map, cheatsheet, recipes, and 12 genre guides each with
  a validated `.lvns` example.
- **`AGENTS.md`** (root + `howto/`) — onboarding for coding agents and authors.
- **`GROWTH.md`** — growth analysis and a Unity-native plan.
- **Faithful branch reconvergence in the articy `.adpd` importer**
  (`linearizeByComponents`): within a scene the 0x02 pin graph drives the flow, so
  a choice's branches rejoin at their shared next stop (the merge points the
  authoring-order spine flattened away); scenes are chained in authoring order onto
  reached dead-ends (never a bogus choice). Self-validates 100% coverage and falls
  back to the spine, so it can never produce worse coverage. Validated on 5 real
  novels — 100% content coverage, 0 lost lines, 348–606 real merge points each
  (previously 0), cleaner choice counts.

### Changed
- **Rebrand to "Elvin"** (how "LVN" is pronounced). The `.lvn`/`.lvns` extensions
  and all code identifiers (`com.lvn.engine`, `Lvn.*`, `LvnPlayer`) are unchanged.
- README / package description now lead with a plain value statement instead of
  the "ffmpeg" metaphor.

### Fixed
- CI: `gofmt` alignment in `lvnconv`; missing `golang.org/x/image` entry in the
  server `go.sum` (each Go module now builds standalone with `GOWORK=off`).

## [0.4.0]

- Transcoder (`lvnconv`): Elvin Script + Ink + articy (XML and binary `.adpd`)
  front-ends, structural validator, `probe`, WASM build.
- Unity runtime (`com.lvn.engine`): interpreter (flow, vars, expressions,
  subroutines, autosave, `wait`, `preload`), parametric cast/compositor, animation
  engine (channels, easing, yoyo, queue), effects (fade/dim/flash/tint/blur/
  camera/particles/audio), reactive HUD, save/load, and the novel-shell.
- Server template (Go) + web authoring panel.

[Unreleased]: https://github.com/fomeanator/unity-lvn-vn-engine/compare/v0.4.0...HEAD
[0.4.0]: https://github.com/fomeanator/unity-lvn-vn-engine/releases/tag/v0.4.0
