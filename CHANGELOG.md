# Changelog

All notable changes to Elvin. Format based on [Keep a Changelog](https://keepachangelog.com/);
the project aims for [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
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
