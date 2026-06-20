# LVN Engine (Unity)

The runtime that plays the `.lvn` container format. It owns flow control; you
own rendering. That split is the whole idea — the same engine plays any story,
and you build your game by implementing one interface.

## Install

**Package Manager → Add package from git URL:**

```
https://github.com/fomeanator/unity-lvn-vn-engine.git?path=/unity/Packages/com.lvn.engine
```

Requires `com.unity.nuget.newtonsoft-json` (declared as a dependency; Package
Manager pulls it automatically).

## Use

```csharp
using Lvn;

var doc    = LvnDocument.Parse(lvnJsonText);   // from a TextAsset or download
var player = new LvnPlayer(doc, myStage);      // myStage : ILvnStage
player.Advance();                              // run to the first pause
```

Implement `ILvnStage` on whatever renders your game:

```csharp
public void ShowSay(string who, string text, string style) { /* draw the line */ }
public void ShowChoice(IReadOnlyList<LvnOption> options)    { /* draw the buttons */ }
public void ApplyStage(JObject command)                    { /* bg/actor/fade/… */ }
public void OnEnd()                                        { /* finale */ }
```

Drive it by alternating with the player:

- `Advance()` runs commands until a **say**, a **choice**, or the **end**;
- after a say, call `Advance()` again on the player's tap;
- after a choice, call `Choose(option.Index)` then `Advance()`.

Flow control — `goto`, `if`, `choice`, `call`/`return` tunnels — and the
variable bag (`set`/`inc`, exposed as `player.Vars`) are handled for you.
`player.Index`, `player.Vars` and `player.CallStack` are the autosave snapshot;
`player.Restore(...)` puts a player back. Both structured `cond` and string
`expr` conditions (`courage >= 2 && !lied`) evaluate out of the box via
`LvnExpression`; set `player.ExprEvaluator` only to plug in a different dialect.

## Sample

Import **Hello LVN** from the package's Samples, drop `HelloLvnRunner` on a
GameObject, assign `hello.lvn.txt`, and press Play — the story prints to the
Console and advances on click. It is a complete, minimal `ILvnStage`.

## Scope (v0.1)

The interpreter, document model, host contract and op registry are here. The
full effect modules (camera/particles/tint), the Pratt expression evaluator,
the layered-sprite compositor and the premium meta-shell template are tracked
for following releases — see the repo root README. Author content with
`lvnconv` and validate it before shipping.
