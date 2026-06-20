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
        private readonly JArray _script;
        private readonly ILvnStage _stage;
        private readonly Dictionary<string, int> _labels = new Dictionary<string, int>();
        private readonly Stack<int> _callStack = new Stack<int>();

        /// <summary>Authored + bookkeeping variables. Public for save/restore.</summary>
        public readonly Dictionary<string, JToken> Vars = new Dictionary<string, JToken>();

        /// <summary>Fired when a say command is executed. Arguments: who, text, style.</summary>
        public Action<string, string, string> OnSay;

        /// <summary>
        /// Optional override for string <c>expr</c> conditions (option filters
        /// and <c>if</c>). When unset, the built-in <see cref="LvnExpression"/>
        /// evaluator is used; set this only to plug in a different expression
        /// dialect. Structured <c>cond</c> is unaffected.
        /// </summary>
        public Func<string, IReadOnlyDictionary<string, JToken>, bool> ExprEvaluator;

        private bool EvalExpr(string expr) =>
            ExprEvaluator != null ? ExprEvaluator(expr, Vars) : LvnExpression.EvaluateBool(expr, Vars);

        private int _ip;

        public bool Finished { get; private set; }
        public int Index => _ip;

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
                foreach (var f in callStack) _callStack.Push(f);
        }

        public IReadOnlyCollection<int> CallStack => _callStack;

        /// <summary>Snapshot of the player's state for save/load.</summary>
        public class LvnSnapshot
        {
            public int Index;
            public Dictionary<string, JToken> Vars;
            public int[] CallStack;
        }

        /// <summary>Capture the current state for serialization.</summary>
        public LvnSnapshot Save()
        {
            return new LvnSnapshot
            {
                Index = _ip,
                Vars = new Dictionary<string, JToken>(Vars),
                CallStack = _callStack.ToArray(),
            };
        }

        /// <summary>Restore from a snapshot.</summary>
        public void Restore(LvnSnapshot snapshot)
        {
            if (snapshot == null) return;
            Restore(snapshot.Index, snapshot.Vars, snapshot.CallStack);
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
                switch ((string)c["op"])
                {
                    case "label":
                        _ip++;
                        break;

                    case "set":
                    case "inc":
                        ApplyData(c);
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
                        SeekTo(EvalCond(c) ? (string)c["then"] : (string)c["else"]);
                        break;

                    case "choice":
                        _stage.ShowChoice(BuildOptions(c));
                        return;

                    case "say":
                        var sayWho = (string)c["who"];
                        var sayText = (string)c["text"];
                        var sayStyle = (string)c["style"];
                        OnSay?.Invoke(sayWho, sayText, sayStyle);
                        _stage.ShowSay(sayWho, sayText, sayStyle);
                        _ip++;
                        return;

                    case "wait":
                        _stage.ApplyStage(c);
                        _ip++;
                        return;

                    case "preload":
                        _stage.ApplyStage(c);
                        _ip++;
                        break;

                    default:
                        _stage.ApplyStage(c);
                        _ip++;
                        break;
                }
            }
            if (!Finished)
                Finish();
        }

        /// <summary>
        /// Resolve a picked option (by its <see cref="LvnOption.Index"/>). Sets
        /// up the next position; the caller then calls <see cref="Advance"/>.
        /// </summary>
        public void Choose(int optionIndex)
        {
            var c = (JObject)_script[_ip];
            if ((string)c["op"] != "choice")
                throw new InvalidOperationException("Choose called when not at a choice");

            var opts = (JArray)c["options"];
            var opt = (JObject)opts[optionIndex];

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
            Finished = false;
            SeekTo(label);
        }

        // ── internals ────────────────────────────────────────────────────────

        private void Jump(string label) => SeekTo(label);

        private void SeekTo(string label)
        {
            if (string.IsNullOrEmpty(label) || label == "__end")
            {
                Finish();
                return;
            }
            if (_labels.TryGetValue(label, out var i)) _ip = i;
            else Finish(); // unknown target — the validator catches these pre-ship
        }

        private void Finish()
        {
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

                var requires = (string)o["requires_stat"];
                if (requires != null && VarNum(requires) < Num(o["min"], 0))
                    continue;

                var expr = (string)o["expr"];
                if (expr != null && !EvalExpr(expr))
                    continue;

                result.Add(new LvnOption(i, (string)o["text"] ?? "", (string)o["cost"]));
            }
            return result;
        }

        private bool EvalCond(JObject c)
        {
            var expr = (string)c["expr"];
            if (expr != null)
                return EvalExpr(expr);

            if (!(c["cond"] is JObject cond))
                return false;

            double a = VarNum((string)cond["key"]);
            double b = Num(cond["value"], 0);
            switch ((string)cond["op"])
            {
                case "eq": return a == b;
                case "ne": return a != b;
                case "lt": return a < b;
                case "lte": return a <= b;
                case "gt": return a > b;
                case "gte": return a >= b;
                default: return a != 0;
            }
        }

        private void ApplyData(JObject c)
        {
            var key = (string)c["key"];
            if (string.IsNullOrEmpty(key)) return;
            if ((string)c["op"] == "inc")
                Vars[key] = new JValue(VarNum(key) + Num(c["by"], 1));
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
