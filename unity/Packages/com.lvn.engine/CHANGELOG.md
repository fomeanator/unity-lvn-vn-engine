# Changelog

All notable changes to the LVN Engine package are documented here. The format
follows [Keep a Changelog](https://keepachangelog.com/); versions are SemVer.

## [Unreleased]

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
