# The `.lvn` container format

A `.lvn` file is a JSON object with an optional `scene` id and a flat `script`
array of **commands**. The runtime executes commands in order, except where a
command changes the cursor (`goto`, `if`, `choice`, `call`/`return`).

```json
{
  "scene": "chapter-1",
  "script": [
    { "op": "bg", "sprite_url": "/content/bg/room.jpg" },
    { "op": "actor", "id": "mara", "position": "left", "sprite_url": "/content/actors/mara.png" },
    { "op": "say", "who": "Mara", "text": "You came back." },
    { "op": "choice", "options": [
        { "text": "I did.",      "goto": "stay" },
        { "text": "I'm leaving.", "goto": "leave" }
    ] },
    { "op": "label", "id": "stay" },
    { "op": "say", "text": "She smiled." },
    { "op": "goto", "label": "__end" }
  ]
}
```

Every command has an `op`. An `op` outside the catalog below is a **validation
error**, never a silent skip (`lvnconv validate`). Targets of `goto`, `if`,
`choice` options and `call` must resolve to a defined `label` (or the builtin
`__end`).

## Command catalog

### Presentation

| op | fields | meaning |
|---|---|---|
| `say` | `text` (required), `who?`, `style?` | Show a line. `who` drives the nameplate; omit for narration. `style` ∈ `thought` (italic) / `shout` (bold, larger) / `narration` (centred, no panel) / `whisper` (italic, faint panel); unknown resets to default. One-off formatting (colour, bold, size) goes inline via rich-text tags in `text`, e.g. `<b>…</b>`, `<color=#f33>…</color>`. |
| `bg` | `sprite_url` (required), `id?` | Set the full-screen background. |
| `actor` | `id` (required), `sprite_url` **or** `body_url`/`clothes_url`/`hair_url`, `show?`, `position?` (`left`/`center`/`right`), `x?`/`y?` (0..1), `width?`/`height?` (fraction of viewport), `scale?`, `emotion?`, `enter?`/`exit?`, `on_click?` (label string or `{ "goto": "label", "set": {...} }`), `hover_opacity?` (0..1) | Place / update / hide a character. Sprites are layered: a null layer url is unchanged, an empty string removes it. `on_click` makes the object a tappable hotspot. |
| `fade` | `to` (`black`/`white`/…), `duration` | Full-screen fade. |
| `dim` | `alpha` (0..1), `duration` | Dim the stage (focus pull). |
| `camera` | `action` (`shake`/`zoom`/`pan`), `amplitude?`, `factor?`, `duration?` | Camera move. |
| `particles` | `type` (`rain`/`snow`/…), `on` (bool) | Toggle a particle layer. |
| `audio` | `channel` (`music`/`sfx`/`ambient`), `action` (`play`/`stop`/…), `url?` | Sound control. |
| `wait` | `ms`, **or** `until` (`"preload"`) + `urls?`/`assets?`/`min_ms?` | Pause before the next command. `until="preload"` blocks until assets are ready instead of on a timer: its own `assets`/`urls`, else the pending `preload` batch — so a scene never shows before its art loads. `min_ms` is an optional floor. |
| `hint` | `text?`, `show` (bool) | Top "this choice will cost something" banner. |
| `preload` | `assets`: `[{ "url", "kind": "sprite"|"audio" }]` | Hint the loader to warm assets. |

### Flow control

| op | fields | meaning |
|---|---|---|
| `label` | `id` (required) | A named jump target. Ids are stable across reimports. |
| `goto` | `label` (required) | Jump. `__end` is the builtin end-of-script target. |
| `choice` | `options`: array of option objects | Branch on player input. |
| `if` | `cond` **or** `expr`, `then`, `else` | Conditional jump to a label. |
| `call` | `label` | Tunnel: push the return point and jump (subroutine). |
| `return` | — | Tunnel: pop and resume after the matching `call`. |

**Choice option object:** `text` (required), `goto` (target label) **or**
`body` (inline command array, usually ending in `goto`), plus optional gating:

- `expr` — a boolean filter. **Hidden** gate: when it evaluates false the option
  is dropped entirely (pure logic branching; the player never sees it).
- `requires_stat` + `requires_min` (legacy alias `min`) — a skill check.
  **Locked** gate: when `var(requires_stat) < requires_min` the option is still
  shown, but greyed and non-interactive, with an auto note (`str ≥ 8`). The
  player sees what they can't do yet (Disco-Elysium style).
- `cost` — either a **string** (pure flavour shown under the option) or a
  **structured** object `{ "var": "gold", "amount": 5 }` (`currency` is an
  accepted alias for `var`, emitted by the ink/articy importers). A structured
  cost **locks** the option when unaffordable and is **deducted from the
  variable** when the option is chosen. `.lvns` shorthand: `cost=gold:5`.
- `locked_text` — override the auto lock note; `hide_if_locked: true` turns a
  failed *locked* gate back into a *hidden* one.

### State

| op | fields | meaning |
|---|---|---|
| `set` | `key`, `value` | Assign a variable (string/number/bool/null). |
| `inc` | `key`, `by` | Increment a numeric variable. |

### Conditions (`if` / option `expr`)

Two equivalent forms:

- **Structured** `cond`: `{ "key": "courage", "op": "gte", "value": 2 }`
  (`eq`/`ne`/`lt`/`lte`/`gt`/`gte`). Missing keys read as `0`/`false`.
- **Expression** `expr`: a string like `courage >= 2 && !met_guest`
  (`|| && !`, comparisons, arithmetic, string literals). Evaluated by the
  runtime's expression module.

Dotted identifiers (`ns.flag`) are valid variable names, so a front-end can
namespace its globals.

## Conventions

- **Variable namespacing.** Auto-generated bookkeeping keys use a `__` prefix
  (`__seen_<label>`, `__once_<id>`, `__alt_<site>_<n>`); avoid that prefix for
  authored variables.
- **Asset urls** are opaque to the format — absolute (`/content/...`) for a
  server, or bundle-relative for offline. The runtime's loader resolves them.
- **Stable ids** on labels/choices/endings are a hard requirement: saves and
  analytics key off them, so renaming text must not change them.
