using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Lvn
{
    /// <summary>
    /// The .lvn interpreter: a cursor over the command list plus a variable bag.
    /// It owns flow control (goto / if / choice / call-return) and hands every
    /// presentation command to an <see cref="ILvnStage"/>. It has no Unity
    /// dependency — the same player drives a UI Toolkit stage, a uGUI stage, or
    /// a headless test host.
    ///
    /// Drive it by alternating with the stage:
    ///   • call <see cref="Advance"/> to run until the next pause (a say, a
    ///     choice, or the end);
    ///   • after a say, call <see cref="Advance"/> again on the player's tap;
    ///   • after a choice, call <see cref="Choose"/> then <see cref="Advance"/>.
    /// </summary>
    public sealed class LvnPlayer
    {
        private JArray _script; // swappable for hot-reload (see TryReplaceScript)
        private readonly ILvnStage _stage;
        private readonly Dictionary<string, int> _labels = new Dictionary<string, int>();
        private readonly Stack<int> _callStack = new Stack<int>();

        /// <summary>Authored + bookkeeping variables. Public for save/restore.</summary>
        public readonly Dictionary<string, JToken> Vars = new Dictionary<string, JToken>();

        /// <summary>Fired when a say command is executed. Arguments: who, text, style.</summary>
        public Action<string, string, string> OnSay;

        /// <summary>Optional trace sink — set by the host (e.g. to Debug.Log) to get
        /// a full step-by-step log of execution. No-op when null (zero overhead).</summary>
        public static Action<string> Log;

        /// <summary>Optional localization catalog: <c>text_id</c> → string for the
        /// active language. When a say/choice carries a <c>text_id</c> (instead of
        /// inline <c>text</c>), it is resolved here. Swap this to switch language;
        /// the <c>.lvn</c> structure is language-independent.</summary>
        public IReadOnlyDictionary<string, string> Strings;

        // Resolve a line's text in the active language. Two keying schemes share
        // one catalog: an explicit "text_id" (stable id, e.g. an articy GUID), or —
        // for inline-authored lines — the source string itself as the key
        // (gettext/Ren'Py style). Missing translation falls back to the source.
        private string Localized(JObject c)
        {
            var inline = (string)c["text"];
            if (inline != null)
                return Strings != null && Strings.TryGetValue(inline, out var tr) ? tr : inline;
            var id = (string)c["text_id"];
            if (id == null) return "";
            return Strings != null && Strings.TryGetValue(id, out var s) ? s : id;
        }

        /// <summary>
        /// Optional override for string <c>expr</c> conditions (option filters
        /// and <c>if</c>). When unset, the built-in <see cref="LvnExpression"/>
        /// evaluator is used; set this only to plug in a different expression
        /// dialect. Structured <c>cond</c> is unaffected.
        /// </summary>
        public Func<string, IReadOnlyDictionary<string, JToken>, bool> ExprEvaluator;

        // A malformed expression in the content must never crash the runtime — a
        // bad condition simply gates closed (false). Authoring tools catch these at
        // compile time; the player degrades gracefully.
        private bool EvalExpr(string expr)
        {
            try
            {
                return ExprEvaluator != null ? ExprEvaluator(expr, Vars) : LvnExpression.EvaluateBool(expr, Vars);
            }
            catch (LvnException)
            {
                return false;
            }
        }

        private int _ip;

        public bool Finished { get; private set; }
        public int Index => _ip;

        /// <summary>Total command count — pairs with <see cref="Index"/> to drive
        /// a chapter-progress readout (e.g. the in-game HUD percent).</summary>
        public int Count => _script.Count;

        public LvnPlayer(LvnDocument doc, ILvnStage stage)
        {
            _script = doc.Script;
            _stage = stage;
            for (int i = 0; i < _script.Count; i++)
            {
                if (_script[i] is JObject c && (string)c["op"] == "label")
                {
                    var id = (string)c["id"];
                    if (!string.IsNullOrEmpty(id))
                        _labels[id] = i;
                }
            }
        }

        /// <summary>Restore a saved position and state (for autosave/resume).</summary>
        public void Restore(int index, IDictionary<string, JToken> vars, IEnumerable<int> callStack)
        {
            _ip = index;
            Finished = false;
            Vars.Clear();
            if (vars != null)
                foreach (var kv in vars) Vars[kv.Key] = kv.Value;
            _callStack.Clear();
            if (callStack != null)
            {
                // Save() emits the stack top-first (Stack.ToArray order); push in
                // reverse so the restored stack matches the original exactly.
                var frames = new List<int>(callStack);
                for (int i = frames.Count - 1; i >= 0; i--) _callStack.Push(frames[i]);
            }
        }

        /// <summary>Re-apply the persistent visual side-effects (background, actors,
        /// HUD labels, idle animations) of commands <c>0..upto</c> without showing
        /// any dialogue — used after <see cref="Restore(LvnSnapshot)"/> to rebuild
        /// the scene a save was taken in before resuming.</summary>
        public void ReplayVisuals(int upto)
        {
            if (_script == null) return;
            int end = System.Math.Min(upto, _script.Count);
            for (int i = 0; i < end; i++)
                if (_script[i] is JObject c && IsReapplyable((string)c["op"]))
                    _stage.ApplyStage(c);
        }

        /// <summary>Set the cursor and run forward to the next pause — the resume
        /// step after a load (the scene is rebuilt by <see cref="ReplayVisuals"/>).</summary>
        public void ContinueFrom(int index)
        {
            _ip = index;
            Finished = false;
            Advance();
        }

        public IReadOnlyCollection<int> CallStack => _callStack;

        /// <summary>Snapshot of the player's state for save/load. <see cref="CommandCount"/>
        /// and <see cref="Finished"/> let a host feed <see cref="ResumePlanner"/> so
        /// a resume survives the script changing length between sessions;
        /// <see cref="ScriptUrl"/> is set by the host (the player doesn't know it).</summary>
        public class LvnSnapshot
        {
            public int Index;
            public Dictionary<string, JToken> Vars;
            public int[] CallStack;
            /// <summary>Command count of the script when this snapshot was taken.</summary>
            public int CommandCount;
            /// <summary>True if the chapter had reached its end when saved.</summary>
            public bool Finished;
            /// <summary>Host-supplied id/url of the script this slot belongs to.</summary>
            public string ScriptUrl;
        }

        /// <summary>Capture the current state for serialization.</summary>
        public LvnSnapshot Save()
        {
            return new LvnSnapshot
            {
                Index = _ip,
                Vars = new Dictionary<string, JToken>(Vars),
                CallStack = _callStack.ToArray(),
                CommandCount = _script.Count,
                Finished = Finished,
            };
        }

        /// <summary>Restore from a snapshot.</summary>
        public void Restore(LvnSnapshot snapshot)
        {
            if (snapshot == null) return;
            Restore(snapshot.Index, snapshot.Vars, snapshot.CallStack);
        }

        /// <summary>
        /// Hot-swap the underlying script in place — for a live edit that didn't
        /// change the command STRUCTURE — keeping the cursor, variables and call
        /// stack so the chapter continues exactly where it is. Returns false when
        /// the structure changed (different command count, a changed op, or a moved
        /// label id): the host must then restart the chapter from the top, because
        /// the saved cursor no longer means the same beat. Text/parameter edits
        /// (a reworded line, a tweaked emotion or position) all pass.
        /// </summary>
        public bool TryReplaceScript(LvnDocument doc)
        {
            var next = doc?.Script;
            if (next == null || next.Count != _script.Count) return false;
            // Staging ops that are pure-visual and side-effect-free to re-issue
            // (no var mutation, no pause). On a live edit we re-apply the ones that
            // changed and have already run, so editing an on-screen animation /
            // actor / background updates it in place instead of only on next entry.
            List<int> reapply = null;
            for (int i = 0; i < next.Count; i++)
            {
                var a = _script[i] as JObject;
                var b = next[i] as JObject;
                if (a == null || b == null) return false;
                var op = (string)a["op"];
                if (op != (string)b["op"]) return false;
                // labels are the jump targets — a renamed/moved label breaks flow.
                if (op == "label" && (string)a["id"] != (string)b["id"]) return false;
                if (i < _ip && IsReapplyable(op) && !JToken.DeepEquals(a, b))
                    (reapply ??= new List<int>()).Add(i);
            }
            _script = next;
            _labels.Clear();
            for (int i = 0; i < _script.Count; i++)
                if (_script[i] is JObject c && (string)c["op"] == "label")
                {
                    var id = (string)c["id"];
                    if (!string.IsNullOrEmpty(id)) _labels[id] = i;
                }
            if (_ip > _script.Count) _ip = _script.Count;
            // re-issue edited, already-run staging so the live picture reflects it
            if (reapply != null)
                foreach (var i in reapply) _stage.ApplyStage((JObject)_script[i]);
            return true;
        }

        // Pure-visual staging ops safe to re-apply on a hot-swap (no side effects
        // on vars/flow/pauses). NOT set/inc (would double-count) nor say/choice/wait.
        private static bool IsReapplyable(string op) =>
            op == "bg" || op == "actor" || op == "obj" || op == "anim" || op == "text";

        /// <summary>
        /// Re-issue the stage command for the beat currently on screen (the say
        /// just shown, or the choice we're waiting on) — called after a hot-swap so
        /// an edit to the visible line appears immediately without advancing. Does
        /// not fire <see cref="OnSay"/> (so the history backlog isn't duplicated).
        /// </summary>
        public void RerenderCurrent()
        {
            if (_script == null || _script.Count == 0 || Finished) return;
            // A choice pauses AT its index; a say advances past it, so look back one.
            if (_ip >= 0 && _ip < _script.Count && _script[_ip] is JObject atIp && (string)atIp["op"] == "choice")
            {
                _stage.ShowChoice(BuildOptions(atIp));
                return;
            }
            int j = _ip - 1;
            if (j >= 0 && j < _script.Count && _script[j] is JObject c && (string)c["op"] == "say")
            {
                var who = TextInterpolation.Apply((string)c["who"], Vars);
                var text = TextAlternatives.Apply(Localized(c), Vars, j);
                text = TextInterpolation.Apply(text, Vars);
                _stage.ShowSay(who, text, (string)c["style"]);
            }
        }

        /// <summary>Run commands until the next pause point or the end.</summary>
        public void Advance()
        {
            // A pause (say/choice) or the end breaks this loop. A guard catches a
            // cyclic goto with no pause between iterations, which would otherwise
            // spin the main thread forever (a freeze) instead of failing loudly.
            int budget = _script.Count + 100000;
            while (!Finished && _ip >= 0 && _ip < _script.Count)
            {
                if (--budget < 0)
                    throw new LvnException("possible infinite loop: a goto cycle has no say/choice between jumps");
                var c = (JObject)_script[_ip];
                var curOp = (string)c["op"];
                if (Log != null) Log("#" + _ip + " " + curOp + DescribeCmd(c));
                switch (curOp)
                {
                    case "label":
                        _ip++;
                        break;

                    case "set":
                    case "inc":
                        ApplyData(c);
                        Log?.Invoke("    → " + (string)c["key"] + " = " + (Vars.TryGetValue((string)c["key"] ?? "", out var nv) ? nv.ToString() : "?"));
                        _ip++;
                        break;

                    case "goto":
                        Jump((string)c["label"]);
                        break;

                    case "call":
                        _callStack.Push(_ip + 1);
                        Jump((string)c["label"]);
                        break;

                    case "return":
                        _ip = _callStack.Count > 0 ? _callStack.Pop() : _script.Count;
                        break;

                    case "if":
                        bool cond = EvalCond(c);
                        var branch = cond ? (string)c["then"] : (string)c["else"];
                        Log?.Invoke("    if \"" + (string)c["expr"] + "\" → " + cond + " → :" + branch);
                        SeekTo(branch);
                        break;

                    case "choice":
                        _stage.ShowChoice(BuildOptions(c));
                        return;

                    case "say":
                        // Ink-style alternatives first (their counters key off the
                        // command index), then {var} interpolation — for both the
                        // line and the speaker name.
                        var sayWho = TextInterpolation.Apply((string)c["who"], Vars);
                        var sayText = TextAlternatives.Apply(Localized(c), Vars, _ip);
                        sayText = TextInterpolation.Apply(sayText, Vars);
                        var sayStyle = (string)c["style"];
                        Log?.Invoke("    \"" + (string.IsNullOrEmpty(sayWho) ? "" : sayWho + ": ") + sayText + "\"");
                        OnSay?.Invoke(sayWho, sayText, sayStyle);
                        _stage.ShowSay(sayWho, sayText, sayStyle);
                        _ip++;
                        // If a choice follows immediately, present it together with
                        // this line — the dialogue (prompt) and the choices show in
                        // one step, no tap between. They stay two separate, fully
                        // themable UIs; layout is up to the theme.
                        if (_ip < _script.Count && _script[_ip] is JObject afterSay && (string)afterSay["op"] == "choice")
                            break;
                        return;

                    case "wait":
                        _stage.ApplyStage(c);
                        _ip++;
                        return;

                    case "preload":
                        _stage.ApplyStage(c);
                        _ip++;
                        break;

                    case "load":
                        // The stage restores a snapshot and resumes (ReplayVisuals +
                        // ContinueFrom), which runs its own Advance — so bail out of
                        // this one instead of falling through to _ip++.
                        _stage.ApplyStage(c);
                        return;

                    default:
                        _stage.ApplyStage(c);
                        _ip++;
                        break;
                }
            }
            if (!Finished)
                Finish();
        }

        /// <summary>True when the cursor is sitting on a choice command — the only
        /// time <see cref="Choose"/> is valid. Hosts check this before forwarding a
        /// click so a stale choice button (left over after a reload/load) is ignored
        /// rather than throwing.</summary>
        public bool AtChoice =>
            !Finished && _script != null && _ip >= 0 && _ip < _script.Count
            && _script[_ip] is JObject c && (string)c["op"] == "choice";

        /// <summary>
        /// Resolve a picked option (by its <see cref="LvnOption.Index"/>). Sets
        /// up the next position; the caller then calls <see cref="Advance"/>.
        /// </summary>
        public void Choose(int optionIndex)
        {
            if (!AtChoice)
                throw new InvalidOperationException("Choose called when not at a choice");
            var c = (JObject)_script[_ip];

            var opts = (JArray)c["options"];
            var opt = (JObject)opts[optionIndex];
            Log?.Invoke("CHOOSE [" + optionIndex + "] \"" + (string)opt["text"] + "\"" + (opt["goto"] != null ? " → :" + opt["goto"] : ""));

            // Defensive: a locked option must never resolve. The host greys it out,
            // but a stale click (e.g. after a reload) is ignored — leave the cursor
            // on the choice so the next Advance re-presents it.
            if (OptionLocked(opt)) { Log?.Invoke("  locked — ignored"); return; }

            // Spend a structured cost from its variable before navigating.
            if (opt["cost"] is JObject costObj)
            {
                var cv = (string)(costObj["var"] ?? costObj["currency"]);
                if (cv != null)
                {
                    Vars[cv] = new JValue(VarNum(cv) - Num(costObj["amount"], 0));
                    Log?.Invoke("  spent " + FmtNum(Num(costObj["amount"], 0)) + " " + cv + " → " + VarNum(cv));
                }
            }

            if (opt["body"] is JArray body)
            {
                foreach (var bt in body)
                {
                    var bc = (JObject)bt;
                    var bop = (string)bc["op"];
                    if (bop == "set" || bop == "inc") ApplyData(bc);
                    else if (bop == "goto") { Jump((string)bc["label"]); return; }
                    else _stage.ApplyStage(bc);
                }
                _ip++; // body without a goto → fall through past the choice
                return;
            }

            var target = (string)opt["goto"];
            if (target != null) Jump(target);
            else _ip++;
        }

        /// <summary>
        /// Jump to a label on demand — the hook for clickable hotspots and other
        /// out-of-band navigation. The caller then calls <see cref="Advance"/>.
        /// Re-activates a finished player so a hotspot on an end screen can drive
        /// flow again. This plus placeable, clickable objects is enough to build
        /// a button-driven game: each screen is a pause with its own hotspots.
        /// </summary>
        public void GoTo(string label)
        {
            Log?.Invoke("GoTo :" + label);
            Finished = false;
            SeekTo(label);
        }

        // ── internals ────────────────────────────────────────────────────────

        // A short human suffix for the per-command trace (id/label/key).
        private static string DescribeCmd(JObject c)
        {
            var id = (string)c["id"]; if (id != null) return " id=" + id + (c["on_click"] != null ? " on_click=" + c["on_click"] : "");
            var lbl = (string)c["label"]; if (lbl != null) return " :" + lbl;
            var key = (string)c["key"]; if (key != null) return " " + key;
            return "";
        }

        private void Jump(string label) => SeekTo(label);

        private void SeekTo(string label)
        {
            if (string.IsNullOrEmpty(label) || label == "__end")
            {
                Finish();
                return;
            }
            if (_labels.TryGetValue(label, out var i)) { _ip = i; }
            else { Log?.Invoke("  !! unknown label :" + label + " → end"); Finish(); } // validator catches these pre-ship
        }

        private void Finish()
        {
            Log?.Invoke("FINISHED @#" + _ip);
            Finished = true;
            _stage.OnEnd();
        }

        private List<LvnOption> BuildOptions(JObject choice)
        {
            var result = new List<LvnOption>();
            var opts = (JArray)choice["options"];
            for (int i = 0; i < opts.Count; i++)
            {
                var o = (JObject)opts[i];

                // Hidden gate: an `expr` that evaluates false drops the option
                // entirely — logic branching the player never sees.
                var expr = (string)o["expr"];
                if (expr != null && !EvalExpr(expr))
                    continue;

                // Locked gate: a failed skill check (requires_stat/requires_min)
                // or an unaffordable structured cost keeps the option visible but
                // greyed, so the player knows what they can't do yet. `hide_if_locked`
                // opts back into hiding for authors who prefer it.
                bool locked = OptionLocked(o);
                if (locked && (bool?)o["hide_if_locked"] == true)
                    continue;

                // {expr}/{var} interpolation so option text/cost track variables too
                // (e.g. "Атаковать ({wname})", "Купить (-{price} зол)").
                var optText = TextInterpolation.Apply(Localized(o), Vars);
                var optCost = TextInterpolation.Apply(CostDisplay(o["cost"]), Vars);
                var note = locked ? LockNote(o) : null;
                result.Add(new LvnOption(i, optText, optCost, !locked, note));
            }
            return result;
        }

        // A skill check or an affordable-cost gate that is currently unmet. Kept
        // separate from the hidden `expr` filter so the host can grey the option
        // out (see BuildOptions) and Choose can refuse it defensively.
        private bool OptionLocked(JObject o)
        {
            var requires = (string)o["requires_stat"];
            if (requires != null && VarNum(requires) < Num(o["requires_min"] ?? o["min"], 0))
                return true;
            if (o["cost"] is JObject co)
            {
                var cv = (string)(co["var"] ?? co["currency"]);
                if (cv != null && VarNum(cv) < Num(co["amount"], 0))
                    return true;
            }
            return false;
        }

        // Human note for a locked option: an author-supplied `locked_text`, else a
        // terse auto label ("strength ≥ 5", "−5 gold").
        private string LockNote(JObject o)
        {
            var custom = (string)o["locked_text"];
            if (custom != null) return TextInterpolation.Apply(custom, Vars);
            var requires = (string)o["requires_stat"];
            if (requires != null && VarNum(requires) < Num(o["requires_min"] ?? o["min"], 0))
                return requires + " ≥ " + FmtNum(Num(o["requires_min"] ?? o["min"], 0));
            if (o["cost"] is JObject co)
            {
                var cv = (string)(co["var"] ?? co["currency"]);
                if (cv != null) return "−" + FmtNum(Num(co["amount"], 0)) + " " + cv;
            }
            return null;
        }

        // The narrative cost line beneath an option. A string cost is shown as-is
        // (pure flavour); a structured {var,amount[,label]} cost formats to
        // "−<amount> <var>" unless it carries its own label.
        private static string CostDisplay(JToken cost)
        {
            if (cost == null) return null;
            if (cost.Type == JTokenType.String) return (string)cost;
            if (cost is JObject co)
            {
                var label = (string)co["label"];
                if (label != null) return label;
                var cv = (string)(co["var"] ?? co["currency"]);
                if (cv == null) return null;
                return "−" + FmtNum(Num(co["amount"], 0)) + " " + cv;
            }
            return null;
        }

        private static string FmtNum(double d) =>
            d == System.Math.Floor(d) && !double.IsInfinity(d)
                ? ((long)d).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : d.ToString(System.Globalization.CultureInfo.InvariantCulture);

        private bool EvalCond(JObject c)
        {
            var expr = (string)c["expr"];
            if (expr != null)
                return EvalExpr(expr);

            if (!(c["cond"] is JObject cond))
                return false;

            var key = (string)cond["key"];
            var left = key != null && Vars.TryGetValue(key, out var lv) ? lv : null;
            var right = cond["value"];
            switch ((string)cond["op"])
            {
                // eq/ne compare by value (strings & bools too, with ink "unset == 0/
                // false/'' " semantics), not just numerically.
                case "eq": return JEq(left, right);
                case "ne": return !JEq(left, right);
                case "lt": return Num(left, 0) < Num(right, 0);
                case "lte": return Num(left, 0) <= Num(right, 0);
                case "gt": return Num(left, 0) > Num(right, 0);
                case "gte": return Num(left, 0) >= Num(right, 0);
                default: return Num(left, 0) != 0;
            }
        }

        // Value equality with ink-style defaulting: an unset (null) variable equals
        // 0 / false / "" so first-visit gates hold before anything sets them.
        private static bool JEq(JToken a, JToken b)
        {
            bool an = a == null || a.Type == JTokenType.Null;
            bool bn = b == null || b.Type == JTokenType.Null;
            if (an || bn)
            {
                var o = an ? b : a;
                if (o == null || o.Type == JTokenType.Null) return true;
                switch (o.Type)
                {
                    case JTokenType.Integer:
                    case JTokenType.Float: return o.Value<double>() == 0;
                    case JTokenType.Boolean: return o.Value<bool>() == false;
                    case JTokenType.String: return string.IsNullOrEmpty((string)o);
                    default: return false;
                }
            }
            if (a.Type == JTokenType.String || b.Type == JTokenType.String)
                return string.Equals(a.ToString(), b.ToString(), StringComparison.Ordinal);
            return Num(a, 0) == Num(b, 0);
        }

        private void ApplyData(JObject c)
        {
            var key = (string)c["key"];
            if (string.IsNullOrEmpty(key)) return;
            if ((string)c["op"] == "inc")
            {
                Vars[key] = new JValue(VarNum(key) + Num(c["by"], 1));
                return;
            }
            // set: a computed `expr` (mirrors `if expr`) takes priority over a
            // literal `value`, so `set key="score" expr="courage + bonus*2"` works.
            var exprTok = c["expr"];
            if (exprTok != null && exprTok.Type == JTokenType.String)
            {
                // A malformed set-expression must not crash the novel; fall back to
                // the literal value (or leave the variable untouched).
                try { Vars[key] = LvnExpression.Evaluate((string)exprTok, Vars); }
                catch (LvnException) { if (c["value"] != null) Vars[key] = c["value"]; }
            }
            else
                Vars[key] = c["value"] ?? JValue.CreateNull();
        }

        private double VarNum(string key) =>
            key != null && Vars.TryGetValue(key, out var t) ? Num(t, 0) : 0;

        private static double Num(JToken t, double def)
        {
            if (t == null) return def;
            switch (t.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    return t.Value<double>();
                case JTokenType.Boolean:
                    return t.Value<bool>() ? 1 : 0;
                default:
                    return def;
            }
        }
    }
}
