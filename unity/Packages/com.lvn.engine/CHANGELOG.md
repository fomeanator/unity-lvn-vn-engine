# Changelog

All notable changes to the LVN Engine package are documented here. The format
follows [Keep a Changelog](https://keepachangelog.com/); versions are SemVer.

## [Unreleased]

### Added
- `LvnExpression` — built-in evaluator for string `expr` conditions
  (`|| && !`, comparisons, arithmetic, strings; unknown vars read as null).
  `if` and option `expr` filters now work out of the box; `LvnPlayer.ExprEvaluator`
  becomes an optional override rather than a requirement.
- `LvnException` — runtime error type for malformed scripts/expressions.

### Verified
- The runtime compiles clean against Unity 6 (6000.4) + Newtonsoft 3.2.

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
