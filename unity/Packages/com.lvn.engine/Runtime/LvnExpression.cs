using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Lvn
{
    /// <summary>
    /// A tiny expression evaluator over the player's variable bag — the runtime
    /// behind the <c>expr</c> field of <c>if</c> / option commands, for the
    /// cases the structured single-clause <c>cond</c> can't express:
    /// <code>
    ///   courage >= 2 &amp;&amp; !lied        (a + b) * 2        name == "Mara"
    /// </code>
    /// Grammar: <c>|| &amp;&amp; !</c> (also <c>or</c>/<c>and</c>/<c>not</c>),
    /// <c>== != &gt; &gt;= &lt; &lt;=</c>, <c>+ - * / %</c>, unary <c>-</c>,
    /// parentheses, literals (numbers, '…'/"…" strings, true/false/null) and
    /// identifiers (variables; unknown reads as null). Truthiness: bool itself,
    /// number != 0, non-empty string, null = false. <c>+</c> concatenates when
    /// either side is a string.
    /// </summary>
    public static class LvnExpression
    {
        public static JToken Evaluate(string expr, IReadOnlyDictionary<string, JToken> vars)
        {
            var p = new Parser(expr, vars);
            var v = p.ParseOr();
            p.ExpectEnd();
            return v.ToJToken();
        }

        public static bool EvaluateBool(string expr, IReadOnlyDictionary<string, JToken> vars)
        {
            var p = new Parser(expr, vars);
            var v = p.ParseOr();
            p.ExpectEnd();
            return v.Truthy();
        }

        // ── Value model ─────────────────────────────────────────────────────

        private enum Kind { Null, Bool, Num, Str }

        private readonly struct Val
        {
            public readonly Kind Kind;
            public readonly bool B;
            public readonly double N;
            public readonly string S;

            private Val(Kind k, bool b, double n, string s) { Kind = k; B = b; N = n; S = s; }

            public static readonly Val Null = new Val(Kind.Null, false, 0, null);
            public static Val Of(bool b) => new Val(Kind.Bool, b, 0, null);
            public static Val Of(double n) => new Val(Kind.Num, false, n, null);
            public static Val Of(string s) => new Val(Kind.Str, false, 0, s);

            public static Val From(JToken t)
            {
                if (t == null) return Null;
                switch (t.Type)
                {
                    case JTokenType.Boolean: return Of((bool)t);
                    case JTokenType.Integer: return Of((double)(long)t);
                    case JTokenType.Float: return Of((double)t);
                    case JTokenType.String: return Of((string)t);
                    case JTokenType.Null: return Null;
                    default: return Of(t.ToString());
                }
            }

            public bool Truthy()
            {
                switch (Kind)
                {
                    case Kind.Bool: return B;
                    case Kind.Num: return N != 0;
                    case Kind.Str: return !string.IsNullOrEmpty(S);
                    default: return false;
                }
            }

            public double AsNum()
            {
                switch (Kind)
                {
                    case Kind.Num: return N;
                    case Kind.Bool: return B ? 1 : 0;
                    case Kind.Str:
                        if (double.TryParse(S, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                            return d;
                        throw new LvnException($"expr: '{S}' is not a number");
                    default: return 0; // null counts as 0, like an unset counter
                }
            }

            public string AsStr()
            {
                switch (Kind)
                {
                    case Kind.Str: return S;
                    case Kind.Bool: return B ? "true" : "false";
                    case Kind.Num:
                        return N == Math.Floor(N) && !double.IsInfinity(N)
                            ? ((long)N).ToString(CultureInfo.InvariantCulture)
                            : N.ToString(CultureInfo.InvariantCulture);
                    default: return "";
                }
            }

            public JToken ToJToken()
            {
                switch (Kind)
                {
                    case Kind.Bool: return B;
                    case Kind.Str: return S;
                    case Kind.Num: return N == Math.Floor(N) && !double.IsInfinity(N) ? (long)N : (JToken)N;
                    default: return JValue.CreateNull();
                }
            }

            public bool EqualTo(Val o)
            {
                // An unset variable is null here, but in script terms it defaults
                // to 0 / false / "" (ink semantics). So `unseen == 0`, `flag ==
                // false` and `name == ""` all hold before anything sets them —
                // which is what makes once-only choice gates (`__once == 0`) and
                // first-visit checks work on the very first pass.
                if (Kind == Kind.Null || o.Kind == Kind.Null)
                {
                    var other = Kind == Kind.Null ? o : this;
                    switch (other.Kind)
                    {
                        case Kind.Null: return true;
                        case Kind.Num: return other.N == 0;
                        case Kind.Bool: return !other.B;
                        case Kind.Str: return string.IsNullOrEmpty(other.S);
                        default: return false;
                    }
                }
                if (Kind == Kind.Str || o.Kind == Kind.Str) return AsStr() == o.AsStr();
                return AsNum() == o.AsNum();
            }
        }

        // ── Recursive-descent parser/evaluator ──────────────────────────────

        private sealed class Parser
        {
            private readonly string _s;
            private readonly IReadOnlyDictionary<string, JToken> _vars;
            private int _i;

            public Parser(string s, IReadOnlyDictionary<string, JToken> vars)
            {
                _s = s ?? "";
                _vars = vars;
            }

            public void ExpectEnd()
            {
                SkipWs();
                if (_i < _s.Length)
                    throw new LvnException($"expr: unexpected '{_s.Substring(_i)}' in \"{_s}\"");
            }

            public Val ParseOr()
            {
                var v = ParseAnd();
                while (TakeOp("||") || TakeWord("or"))
                {
                    var r = ParseAnd();
                    v = Val.Of(v.Truthy() || r.Truthy());
                }
                return v;
            }

            private Val ParseAnd()
            {
                var v = ParseNot();
                while (TakeOp("&&") || TakeWord("and"))
                {
                    var r = ParseNot();
                    v = Val.Of(v.Truthy() && r.Truthy());
                }
                return v;
            }

            private Val ParseNot()
            {
                SkipWs();
                if (Peek('!') && !(_i + 1 < _s.Length && _s[_i + 1] == '='))
                {
                    _i++;
                    return Val.Of(!ParseNot().Truthy());
                }
                if (TakeWord("not")) return Val.Of(!ParseNot().Truthy());
                return ParseCmp();
            }

            private Val ParseCmp()
            {
                var v = ParseAdd();
                SkipWs();
                string op = null;
                foreach (var cand in new[] { "==", "!=", ">=", "<=", ">", "<" })
                {
                    if (TakeOp(cand)) { op = cand; break; }
                }
                if (op == null) return v;
                var r = ParseAdd();
                switch (op)
                {
                    case "==": return Val.Of(v.EqualTo(r));
                    case "!=": return Val.Of(!v.EqualTo(r));
                    case ">": return Val.Of(v.AsNum() > r.AsNum());
                    case ">=": return Val.Of(v.AsNum() >= r.AsNum());
                    case "<": return Val.Of(v.AsNum() < r.AsNum());
                    default: return Val.Of(v.AsNum() <= r.AsNum());
                }
            }

            private Val ParseAdd()
            {
                var v = ParseMul();
                while (true)
                {
                    SkipWs();
                    if (TakeOp("+"))
                    {
                        var r = ParseMul();
                        v = (v.Kind == Kind.Str || r.Kind == Kind.Str)
                            ? Val.Of(v.AsStr() + r.AsStr())
                            : Val.Of(v.AsNum() + r.AsNum());
                    }
                    else if (PeekBinaryMinus() && TakeOp("-"))
                    {
                        v = Val.Of(v.AsNum() - ParseMul().AsNum());
                    }
                    else return v;
                }
            }

            private Val ParseMul()
            {
                var v = ParseUnary();
                while (true)
                {
                    SkipWs();
                    if (TakeOp("*")) v = Val.Of(v.AsNum() * ParseUnary().AsNum());
                    else if (TakeOp("/"))
                    {
                        var r = ParseUnary().AsNum();
                        if (r == 0) throw new LvnException("expr: division by zero");
                        v = Val.Of(v.AsNum() / r);
                    }
                    else if (TakeOp("%"))
                    {
                        var r = ParseUnary().AsNum();
                        if (r == 0) throw new LvnException("expr: modulo by zero");
                        v = Val.Of(v.AsNum() % r);
                    }
                    else return v;
                }
            }

            private Val ParseUnary()
            {
                SkipWs();
                if (TakeOp("-")) return Val.Of(-ParseUnary().AsNum());
                return ParsePrimary();
            }

            private Val ParsePrimary()
            {
                SkipWs();
                if (_i >= _s.Length)
                    throw new LvnException($"expr: unexpected end of \"{_s}\"");

                var c = _s[_i];
                if (c == '(')
                {
                    _i++;
                    var v = ParseOr();
                    SkipWs();
                    if (_i >= _s.Length || _s[_i] != ')')
                        throw new LvnException($"expr: missing ')' in \"{_s}\"");
                    _i++;
                    return v;
                }
                if (c == '"' || c == '\'')
                {
                    var end = _s.IndexOf(c, _i + 1);
                    if (end < 0) throw new LvnException($"expr: unterminated string in \"{_s}\"");
                    var s = _s.Substring(_i + 1, end - _i - 1);
                    _i = end + 1;
                    return Val.Of(s);
                }
                if (char.IsDigit(c) || (c == '.' && _i + 1 < _s.Length && char.IsDigit(_s[_i + 1])))
                {
                    int start = _i;
                    while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
                    var num = _s.Substring(start, _i - start);
                    if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                        throw new LvnException($"expr: bad number '{num}'");
                    return Val.Of(d);
                }
                if (char.IsLetter(c) || c == '_')
                {
                    int start = _i;
                    while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_' || _s[_i] == '.')) _i++;
                    var word = _s.Substring(start, _i - start);
                    switch (word)
                    {
                        case "true": return Val.Of(true);
                        case "false": return Val.Of(false);
                        case "null": return Val.Null;
                    }
                    if (_vars != null && _vars.TryGetValue(word, out var t)) return Val.From(t);
                    return Val.Null; // unset var reads as null
                }
                throw new LvnException($"expr: unexpected '{c}' in \"{_s}\"");
            }

            // ── lexing helpers ──────────────────────────────────────────────

            private void SkipWs()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
            }

            private bool Peek(char c) => _i < _s.Length && _s[_i] == c;

            private bool PeekBinaryMinus()
            {
                SkipWs();
                return Peek('-');
            }

            private bool TakeOp(string op)
            {
                SkipWs();
                if (_i + op.Length > _s.Length || string.CompareOrdinal(_s, _i, op, 0, op.Length) != 0)
                    return false;
                // don't take ">" out of ">=", "<" out of "<=".
                if ((op == ">" || op == "<") && _i + 1 < _s.Length && _s[_i + 1] == '=')
                    return false;
                _i += op.Length;
                return true;
            }

            private bool TakeWord(string w)
            {
                SkipWs();
                if (_i + w.Length > _s.Length || string.CompareOrdinal(_s, _i, w, 0, w.Length) != 0)
                    return false;
                int after = _i + w.Length;
                if (after < _s.Length && (char.IsLetterOrDigit(_s[after]) || _s[after] == '_'))
                    return false;
                _i = after;
                return true;
            }
        }
    }
}
