using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Lvn.Editor
{
    /// <summary>Thrown when LVNScript source cannot be compiled.</summary>
    public class LvnsCompileException : Exception
    {
        public LvnsCompileException(string message) : base(message) { }
    }

    /// <summary>
    /// Compiles LVNScript (<c>.lvns</c>) source to the <c>.lvn</c> JSON container.
    ///
    /// This is a faithful port of the Go transcoder
    /// (<c>tools/lvnconv/internal/lvns/convert.go</c>). The two implementations are
    /// kept identical by a shared golden corpus (Tests/Editor). Keep this in sync
    /// with the Go source; the Go module remains the single source of truth for the
    /// CLI, server and browser-WASM paths.
    /// </summary>
    public static class LvnsCompiler
    {
        static readonly Regex reDialogue =
            new Regex(@"^([^:=\n]+?)(?:\s*\[([^\]]+)\])?\s*:\s*(.*)$", RegexOptions.Singleline);
        static readonly Regex reFuncDef =
            new Regex(@"^\s*func\s+([A-Za-z_]\w*)\s*\(([^)]*)\)\s*\{\s*$");
        static readonly Regex reCall =
            new Regex(@"^\s*(?:([A-Za-z_]\w*)\s*=\s*)?([A-Za-z_]\w*)\s*\((.*)\)\s*$");

        // Ops the .lvns layer recognises (convert.go KnownOps — includes `move`,
        // which lowers to an `anim` command).
        static readonly HashSet<string> KnownOps = new HashSet<string>
        {
            "say", "choice", "bg", "actor", "obj",
            "fade", "dim", "flash", "tint", "blur",
            "camera", "particles",
            "audio", "wait", "preload", "text_pace",
            "text",
            "save", "load",
            "label", "goto", "if",
            "set", "inc", "hint",
            "call", "return",
            "anim", "move",
        };

        /// <summary>Compile source to indented .lvn JSON ({scene?, script}).</summary>
        public static string Compile(string src)
        {
            JObject doc = Convert(src);
            return doc.ToString(Formatting.Indented) + "\n";
        }

        // ── Convert: the main pipeline (mirrors Go Convert) ──────────────────
        static JObject Convert(string src)
        {
            var funcs = CollectFuncs(src);
            string expanded = ExpandLoops(src);
            src = ExpandCalls(expanded, funcs);

            string scene = null;
            var script = new JArray();
            var actorMaps = new Dictionary<string, string>();
            int nf = 0;

            // Pre-process lines: strip // comments (guarding URLs), skip blanks/#,
            // and buffer multi-line «…» strings into one logical line.
            var lines = new List<string>();
            string[] rawLines = src.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            const string urlGuard = "\x00PROTO\x00";

            int chevDepth = 0;
            var cbuf = new StringBuilder();
            foreach (string raw in rawLines)
            {
                if (chevDepth > 0)
                {
                    cbuf.Append("\n");
                    cbuf.Append(raw);
                    foreach (char r in raw)
                    {
                        if (r == '«') chevDepth++;
                        else if (r == '»' && chevDepth > 0) chevDepth--;
                    }
                    if (chevDepth == 0)
                    {
                        lines.Add(cbuf.ToString().Trim());
                        cbuf.Clear();
                    }
                    continue;
                }

                string line = raw.Replace("://", urlGuard);
                int ci = line.IndexOf("//", StringComparison.Ordinal);
                if (ci >= 0) line = line.Substring(0, ci);
                line = line.Replace(urlGuard, "://").Trim();

                if (line.Length == 0 || line.StartsWith("#")) continue;

                int d = 0;
                foreach (char r in line)
                {
                    if (r == '«') d++;
                    else if (r == '»' && d > 0) d--;
                }
                if (d > 0)
                {
                    chevDepth = d;
                    cbuf.Clear();
                    cbuf.Append(line);
                    continue;
                }

                lines.Add(line);
            }
            if (cbuf.Length > 0) lines.Add(cbuf.ToString().Trim());

            for (int i = 0; i < lines.Count;)
            {
                string line = lines[i];

                // 1. scene
                if (line.StartsWith("scene "))
                {
                    scene = line.Substring(6).Trim();
                    i++; continue;
                }
                if (line.StartsWith("scene:"))
                {
                    scene = line.Substring(6).Trim();
                    i++; continue;
                }

                // 2. actor_map
                if (line.StartsWith("actor_map "))
                {
                    string mapping = line.Substring(10).Trim();
                    int eq = mapping.IndexOf('=');
                    if (eq >= 0)
                        actorMaps[mapping.Substring(0, eq).Trim()] = mapping.Substring(eq + 1).Trim();
                    i++; continue;
                }

                // 3. label  :name
                if (line.StartsWith(":"))
                {
                    string labelId = line.Substring(1).Trim();
                    if (labelId == "")
                        throw new LvnsCompileException($"line {i + 1}: label cannot be empty");
                    script.Add(new JObject { ["op"] = "label", ["id"] = labelId });
                    i++; continue;
                }

                // 4. choice — consecutive lines starting with `-` (but not `->`)
                if (line.StartsWith("-") && !line.StartsWith("->"))
                {
                    var options = new JArray();
                    int j = i;
                    while (j < lines.Count)
                    {
                        string curr = lines[j];
                        if (curr.StartsWith("-") && !curr.StartsWith("->"))
                        {
                            options.Add(ParseChoiceOption(curr, j + 1));
                            j++;
                        }
                        else break;
                    }
                    script.Add(new JObject { ["op"] = "choice", ["options"] = options });
                    i = j; continue;
                }

                // 4b. arrow goto  -> label
                if (line.StartsWith("->"))
                {
                    string target = line.Substring(2).Trim();
                    if (target == "")
                        throw new LvnsCompileException($"line {i + 1}: '->' needs a label");
                    script.Add(new JObject { ["op"] = "goto", ["label"] = target });
                    i++; continue;
                }

                // 4c. single-branch if  `if <cond> -> <label>`
                if (line.StartsWith("if ") && line.Contains("->"))
                {
                    string rest = line.Substring(3).Trim();
                    int ai = rest.IndexOf("->", StringComparison.Ordinal);
                    string cond = rest.Substring(0, ai).Trim();
                    string target = rest.Substring(ai + 2).Trim();
                    if (cond == "" || target == "")
                        throw new LvnsCompileException($"line {i + 1}: expected 'if <cond> -> <label>'");
                    nf++;
                    string fall = $"__nf{nf}";
                    script.Add(new JObject { ["op"] = "if", ["expr"] = cond, ["then"] = target, ["else"] = fall });
                    script.Add(new JObject { ["op"] = "label", ["id"] = fall });
                    i++; continue;
                }

                // 4d. assignment  name = expr
                if (TryParseAssign(line, out string akey, out string aexpr) && !KnownOps.Contains(akey))
                {
                    script.Add(new JObject { ["op"] = "set", ["key"] = akey, ["expr"] = aexpr });
                    i++; continue;
                }

                // 5. commands + dialogue
                string[] words = SplitFields(line);
                string firstWord = words.Length > 0 ? words[0] : "";

                bool isCommand = false;
                JObject cmd = null;

                if (KnownOps.Contains(firstWord))
                {
                    if (firstWord == "anim" || firstWord == "move")
                    {
                        string rest = line.Substring(firstWord.Length).Trim();
                        string[] toks = SplitFields(rest);
                        Dictionary<string, object> p;
                        if (toks.Length > 0 && !toks[0].Contains("="))
                            p = ParseAnimPositional(firstWord, rest);
                        else
                            p = ParseKeyValue(rest);
                        cmd = BuildAnimCmd(firstWord, p);
                        isCommand = true;
                    }
                    else if (firstWord == "actor")
                    {
                        string rest = line.Substring("actor".Length).Trim();
                        string[] toks = SplitFields(rest);
                        if (toks.Length > 0 && !toks[0].Contains("="))
                        {
                            var ac = new JObject { ["op"] = "actor", ["id"] = toks[0], ["show"] = true };
                            for (int t = 1; t < toks.Length; t++)
                            {
                                string tok = toks[t];
                                if (tok.Contains("="))
                                {
                                    int e = tok.IndexOf('=');
                                    string k = tok.Substring(0, e);
                                    string v = tok.Substring(e + 1);
                                    if (k == "w") k = "width";
                                    else if (k == "h") k = "height";
                                    ac[k] = Tok(ScalarVal(v));
                                }
                                else
                                {
                                    switch (tok)
                                    {
                                        case "hide": ac["show"] = false; break;
                                        case "show": ac["show"] = true; break;
                                        case "left":
                                        case "right":
                                        case "center":
                                        case "far_left":
                                        case "far_right":
                                        case "offscreen_left":
                                        case "offscreen_right":
                                            ac["position"] = tok; break;
                                        default:
                                            ac["emotion"] = tok; break;
                                    }
                                }
                            }
                            cmd = ac; isCommand = true;
                        }
                        else
                        {
                            var p = ParseKeyValue(rest);
                            cmd = new JObject { ["op"] = "actor" };
                            foreach (var kv in p) cmd[kv.Key] = Tok(kv.Value);
                            isCommand = true;
                        }
                    }
                    else if (firstWord == "bg")
                    {
                        string rest = line.Substring("bg".Length).Trim();
                        if (rest != "" && !rest.Contains("="))
                        {
                            var c = new JObject { ["op"] = "bg", ["sprite_url"] = StripQuotes(rest) };
                            string base_ = rest;
                            int sl = base_.LastIndexOfAny(new[] { '/', '\\' });
                            if (sl >= 0) base_ = base_.Substring(sl + 1);
                            int dot = base_.LastIndexOf('.');
                            if (dot >= 0) base_ = base_.Substring(0, dot);
                            if (base_ != "") c["id"] = base_;
                            cmd = c; isCommand = true;
                        }
                        else
                        {
                            var p = ParseKeyValue(rest);
                            cmd = new JObject { ["op"] = "bg" };
                            foreach (var kv in p) cmd[kv.Key] = Tok(kv.Value);
                            isCommand = true;
                        }
                    }
                    else if (firstWord == "text")
                    {
                        string rem = line.Substring("text".Length).Trim();
                        NextWord(rem, out string id, out string after);
                        if (id != "")
                        {
                            var c = new JObject { ["op"] = "text", ["id"] = id };
                            rem = after;
                            while (true)
                            {
                                NextWord(rem, out string w, out string next);
                                if (w == "") break;
                                if (w == "hide" && next.Trim() == "")
                                {
                                    c["hide"] = true; rem = ""; break;
                                }
                                if (w.Contains("="))
                                {
                                    int e = w.IndexOf('=');
                                    c[w.Substring(0, e)] = Tok(ScalarVal(w.Substring(e + 1)));
                                    rem = next; continue;
                                }
                                break; // w begins the template
                            }
                            string tmpl = rem.Trim();
                            if (tmpl != "") c["text"] = StripQuotes(tmpl);
                            cmd = c; isCommand = true;
                        }
                    }
                    else if (firstWord == "return" && words.Length == 1)
                    {
                        cmd = new JObject { ["op"] = "return" }; isCommand = true;
                    }
                    else if ((firstWord == "goto" || firstWord == "call") && words.Length == 2)
                    {
                        cmd = new JObject { ["op"] = firstWord, ["label"] = words[1] }; isCommand = true;
                    }
                    else if (firstWord != "return" && firstWord != "goto" && firstWord != "call")
                    {
                        string rest = line.Substring(firstWord.Length).Trim();
                        if (rest == "")
                        {
                            cmd = new JObject { ["op"] = firstWord }; isCommand = true;
                        }
                        else
                        {
                            var p = ParseKeyValueSafe(rest);
                            if (p != null)
                            {
                                cmd = new JObject { ["op"] = firstWord };
                                foreach (var kv in p) cmd[kv.Key] = Tok(kv.Value);
                                isCommand = true;
                            }
                        }
                    }
                }

                if (isCommand)
                {
                    script.Add(cmd);
                    i++; continue;
                }

                // Dialogue: Name [emotion]: Text   — or narration
                Match m = reDialogue.Match(line);
                if (m.Success)
                {
                    string speaker = m.Groups[1].Value.Trim();
                    string emotion = m.Groups[2].Value.Trim();
                    string text = m.Groups[3].Value.Trim();
                    text = StripQuotes(text);

                    if (emotion != "")
                    {
                        if (!actorMaps.TryGetValue(speaker, out string actorID))
                            actorID = speaker.ToLowerInvariant().Replace(" ", "_");
                        script.Add(new JObject { ["op"] = "actor", ["id"] = actorID, ["emotion"] = emotion });
                    }
                    script.Add(new JObject { ["op"] = "say", ["who"] = speaker, ["text"] = text });
                }
                else
                {
                    script.Add(new JObject { ["op"] = "say", ["text"] = StripQuotes(line) });
                }

                i++;
            }

            var outDoc = new JObject();
            if (scene != null && scene != "") outDoc["scene"] = scene;
            outDoc["script"] = script;
            return outDoc;
        }

        // ── sugar lowering ───────────────────────────────────────────────────

        static Dictionary<string, List<string>> CollectFuncs(string src)
        {
            var m = new Dictionary<string, List<string>>();
            foreach (string line in src.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
            {
                Match mm = reFuncDef.Match(line);
                if (!mm.Success) continue;
                var ps = new List<string>();
                foreach (string p in mm.Groups[2].Value.Split(','))
                {
                    string t = p.Trim();
                    if (t != "") ps.Add(t);
                }
                m[mm.Groups[1].Value] = ps;
            }
            return m;
        }

        static string ExpandCalls(string src, Dictionary<string, List<string>> funcs)
        {
            var outLines = new List<string>();
            foreach (string line in src.Split('\n'))
            {
                string t = line.Trim();

                if (t.StartsWith("return "))
                {
                    string expr = t.Substring("return ".Length).Trim();
                    if (expr != "")
                    {
                        outLines.Add("__ret = " + expr);
                        outLines.Add("return");
                        continue;
                    }
                }

                Match mm = reCall.Match(t);
                if (mm.Success)
                {
                    string lhs = mm.Groups[1].Value;
                    string fname = mm.Groups[2].Value;
                    string argstr = mm.Groups[3].Value;
                    if (funcs.TryGetValue(fname, out var pars))
                    {
                        var args = SplitArgs(argstr);
                        for (int k = 0; k < pars.Count; k++)
                            if (k < args.Count) outLines.Add(pars[k] + " = " + args[k]);
                        outLines.Add("call __fn_" + fname);
                        if (lhs != "") outLines.Add(lhs + " = __ret");
                        continue;
                    }
                }
                outLines.Add(line);
            }
            return string.Join("\n", outLines);
        }

        static List<string> SplitArgs(string s)
        {
            var args = new List<string>();
            char inStr = '\0';
            int chev = 0, depth = 0, start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr != '\0') { if (c == inStr) inStr = '\0'; continue; }
                if (c == '«') chev++;
                else if (c == '»') { if (chev > 0) chev--; }
                else if (chev > 0) { }
                else if (c == '"' || c == '\'') inStr = c;
                else if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}') depth--;
                else if (c == ',' && depth == 0)
                {
                    args.Add(s.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
            string last = s.Substring(start).Trim();
            if (last != "") args.Add(last);
            return args;
        }

        struct Frame
        {
            public string kind;
            public string loopLbl, endLbl;
            public string idxVar;
            public string elseLbl;
            public bool sawElse;
        }

        static string ExpandLoops(string src)
        {
            var stack = new List<Frame>();
            var outLines = new List<string>();
            int ctr = 0;

            var srcLines = new List<string>();
            foreach (string raw in src.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
                srcLines.AddRange(SplitInline(raw));

            foreach (string raw in srcLines)
            {
                string det = raw.Trim();
                int ci = det.IndexOf("//", StringComparison.Ordinal);
                if (ci >= 0) det = det.Substring(0, ci).Trim();

                if (det.StartsWith("for ") && det.EndsWith("{"))
                {
                    string inner = det.Substring(4);
                    inner = inner.Substring(0, inner.Length - 1).Trim(); // drop trailing {
                    int pos = inner.IndexOf(" in ", StringComparison.Ordinal);
                    if (pos < 0)
                        throw new LvnsCompileException($"for: expected 'for <var> in <expr> {{', got \"{det}\"");
                    string itemVar = inner.Substring(0, pos).Trim();
                    string expr = inner.Substring(pos + 4).Trim();
                    if (itemVar == "" || expr == "")
                        throw new LvnsCompileException($"for: empty variable or collection in \"{det}\"");
                    ctr++;
                    string idx = $"__i{ctr}", sv = $"__src{ctr}", loop = $"__loop{ctr}", body = $"__body{ctr}", end = $"__end{ctr}";
                    outLines.Add($"set key={sv} expr={GoQuote(expr)}");
                    outLines.Add($"set key={idx} value=0");
                    outLines.Add(":" + loop);
                    outLines.Add($"if expr={GoQuote($"{idx} < len({sv})")} then={body} else={end}");
                    outLines.Add(":" + body);
                    outLines.Add($"set key={itemVar} expr={GoQuote($"{sv}[{idx}]")}");
                    stack.Add(new Frame { kind = "for", loopLbl = loop, endLbl = end, idxVar = idx });
                }
                else if (det.StartsWith("while ") && det.EndsWith("{"))
                {
                    string expr = det.Substring(6);
                    expr = expr.Substring(0, expr.Length - 1).Trim();
                    if (expr == "")
                        throw new LvnsCompileException($"while: empty condition in \"{det}\"");
                    ctr++;
                    string loop = $"__loop{ctr}", body = $"__body{ctr}", end = $"__end{ctr}";
                    outLines.Add(":" + loop);
                    outLines.Add($"if expr={GoQuote(expr)} then={body} else={end}");
                    outLines.Add(":" + body);
                    stack.Add(new Frame { kind = "while", loopLbl = loop, endLbl = end });
                }
                else if (det.StartsWith("func ") && det.EndsWith("{"))
                {
                    string inner = det.Substring("func ".Length);
                    inner = inner.Substring(0, inner.Length - 1).Trim();
                    string name = inner;
                    int p = inner.IndexOf('(');
                    if (p >= 0) name = inner.Substring(0, p).Trim();
                    if (name == "")
                        throw new LvnsCompileException($"func: missing name in \"{det}\"");
                    ctr++;
                    string skip = $"__fnskip{ctr}";
                    outLines.Add("goto " + skip);
                    outLines.Add(":__fn_" + name);
                    stack.Add(new Frame { kind = "func", endLbl = skip });
                }
                else if (det.StartsWith("if ") && det.EndsWith("{"))
                {
                    string cond = det.Substring(3);
                    cond = cond.Substring(0, cond.Length - 1).Trim();
                    if (cond == "")
                        throw new LvnsCompileException($"if: empty condition in \"{det}\"");
                    ctr++;
                    string thenL = $"__then{ctr}", elseL = $"__else{ctr}", endL = $"__end{ctr}";
                    outLines.Add($"if expr={GoQuote(cond)} then={thenL} else={elseL}");
                    outLines.Add(":" + thenL);
                    stack.Add(new Frame { kind = "if", endLbl = endL, elseLbl = elseL });
                }
                else if (det.Replace(" ", "") == "}else{")
                {
                    if (stack.Count == 0 || stack[stack.Count - 1].kind != "if")
                        throw new LvnsCompileException("'} else {' without a matching 'if … {'");
                    Frame f = stack[stack.Count - 1];
                    outLines.Add("goto " + f.endLbl);
                    outLines.Add(":" + f.elseLbl);
                    f.sawElse = true;
                    stack[stack.Count - 1] = f;
                }
                else if (det == "}")
                {
                    if (stack.Count == 0)
                        throw new LvnsCompileException("unmatched '}' (no open for/while/if block)");
                    Frame f = stack[stack.Count - 1];
                    stack.RemoveAt(stack.Count - 1);
                    switch (f.kind)
                    {
                        case "for":
                            outLines.Add($"set key={f.idxVar} expr={GoQuote($"{f.idxVar} + 1")}");
                            outLines.Add("goto " + f.loopLbl);
                            outLines.Add(":" + f.endLbl);
                            break;
                        case "while":
                            outLines.Add("goto " + f.loopLbl);
                            outLines.Add(":" + f.endLbl);
                            break;
                        case "func":
                            outLines.Add("return");
                            outLines.Add(":" + f.endLbl);
                            break;
                        case "if":
                            if (f.sawElse)
                                outLines.Add(":" + f.endLbl);
                            else
                            {
                                outLines.Add(":" + f.elseLbl);
                                outLines.Add(":" + f.endLbl);
                            }
                            break;
                    }
                }
                else
                {
                    outLines.Add(raw);
                }
            }

            if (stack.Count > 0)
                throw new LvnsCompileException("unclosed for/while block (missing '}')");
            return string.Join("\n", outLines);
        }

        // splitInline: flatten one-line control blocks to own-line brace form.
        static List<string> SplitInline(string line)
        {
            string t = StripLineComment(line.Trim());
            string det = t.Trim();
            if (det == "") return new List<string> { line };

            bool isCtl = det.StartsWith("if ") || det.StartsWith("for ") ||
                         det.StartsWith("while ") || det.StartsWith("func ") ||
                         det.StartsWith("}");
            if (!isCtl || det.EndsWith("{") || det == "}" ||
                det.Replace(" ", "") == "}else{")
                return new List<string> { line };

            int open = FirstBlockBrace(det);
            if (open < 0) return new List<string> { line };
            int close = MatchBrace(det, open);
            if (close < 0) return new List<string> { line };

            var outList = new List<string>();
            if (det.StartsWith("}"))
                outList.Add("} else {");
            else
                outList.Add(det.Substring(0, open).Trim() + " {");

            string body = det.Substring(open + 1, close - open - 1).Trim();
            if (body != "") outList.AddRange(SplitInline(body));

            string tail = det.Substring(close + 1).Trim();
            if (tail == "")
                outList.Add("}");
            else if (tail.StartsWith("else"))
                outList.AddRange(SplitInline("} " + tail));
            else
            {
                outList.Add("}");
                outList.AddRange(SplitInline(tail));
            }
            return outList;
        }

        static int FirstBlockBrace(string rs)
        {
            char inStr = '\0';
            int chev = 0;
            for (int i = 0; i < rs.Length; i++)
            {
                char c = rs[i];
                if (inStr != '\0') { if (c == inStr) inStr = '\0'; continue; }
                if (c == '«') chev++;
                else if (c == '»') { if (chev > 0) chev--; }
                else if (chev > 0) { }
                else if (c == '"' || c == '\'') inStr = c;
                else if (c == '{') return i;
            }
            return -1;
        }

        static int MatchBrace(string rs, int open)
        {
            char inStr = '\0';
            int chev = 0, depth = 0;
            for (int i = open; i < rs.Length; i++)
            {
                char c = rs[i];
                if (inStr != '\0') { if (c == inStr) inStr = '\0'; continue; }
                if (c == '«') chev++;
                else if (c == '»') { if (chev > 0) chev--; }
                else if (chev > 0) { }
                else if (c == '"' || c == '\'') inStr = c;
                else if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        static string StripLineComment(string s)
        {
            char inStr = '\0';
            int chev = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr != '\0') { if (c == inStr) inStr = '\0'; continue; }
                if (c == '«') chev++;
                else if (c == '»') { if (chev > 0) chev--; }
                else if (chev > 0) { }
                else if (c == '"' || c == '\'') inStr = c;
                else if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
                {
                    if (i > 0 && s[i - 1] == ':') continue; // part of ://
                    return s.Substring(0, i);
                }
            }
            return s;
        }

        // ── choice / key-value parsing ───────────────────────────────────────

        static JObject ParseChoiceOption(string line, int lineNo)
        {
            string text = line.Substring(1).Trim(); // strip '-'
            int arrowIdx = text.IndexOf("->", StringComparison.Ordinal);
            if (arrowIdx == -1)
                throw new LvnsCompileException($"line {lineNo}: choice option must have a target label (use '-> label')");
            string optText = text.Substring(0, arrowIdx).Trim();
            if (optText == "")
                throw new LvnsCompileException($"line {lineNo}: choice option text cannot be empty");
            string rest = text.Substring(arrowIdx + 2).Trim();
            if (rest == "")
                throw new LvnsCompileException($"line {lineNo}: choice option must specify a target label after '->'");

            int spaceIdx = IndexOfAny(rest, ' ', '\t');
            string targetLabel, paramsStr = "";
            if (spaceIdx == -1) targetLabel = rest;
            else { targetLabel = rest.Substring(0, spaceIdx); paramsStr = rest.Substring(spaceIdx + 1).Trim(); }

            var opt = new JObject
            {
                ["text"] = StripQuotes(optText),
                ["goto"] = targetLabel,
            };
            if (paramsStr != "")
            {
                var pars = ParseKeyValue(paramsStr);
                foreach (var kv in pars) opt[kv.Key] = Tok(kv.Value);
            }
            NormalizeChoiceCost(opt);
            return opt;
        }

        // Rewrite a `cost=<var>:<amount>` shorthand into a structured {var,amount}
        // cost the runtime can deduct. A plain-string cost (no numeric var:amount)
        // is left as flavour text. Mirrors normalizeChoiceCost in the Go converter.
        static void NormalizeChoiceCost(JObject opt)
        {
            if (!(opt["cost"] is JValue jv) || jv.Type != JTokenType.String) return;
            string s = (string)jv;
            int i = s.LastIndexOf(':');
            if (i <= 0 || i == s.Length - 1) return;
            string varName = s.Substring(0, i).Trim();
            string amtStr = s.Substring(i + 1).Trim();
            if (!IsValidKey(varName)) return;
            if (long.TryParse(amtStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n))
                opt["cost"] = new JObject { ["var"] = varName, ["amount"] = n };
            else if (double.TryParse(amtStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f))
                opt["cost"] = new JObject { ["var"] = varName, ["amount"] = f };
        }

        // ParseKeyValue throws on malformed input (mirrors Go error return used as
        // a hard failure at choice/anim/legacy-command sites).
        static Dictionary<string, object> ParseKeyValue(string s)
        {
            var res = new Dictionary<string, object>();
            s = s.Trim();
            while (s.Length > 0)
            {
                int eqIdx = s.IndexOf('=');
                if (eqIdx == -1)
                    throw new LvnsCompileException($"expected '=' in key-value pair at \"{s}\"");
                string key = s.Substring(0, eqIdx).Trim();
                if (!IsValidKey(key))
                    throw new LvnsCompileException($"invalid key name \"{key}\"");
                s = s.Substring(eqIdx + 1).TrimStart();
                if (s.Length == 0)
                    throw new LvnsCompileException($"missing value for key \"{key}\"");

                string val;
                if (s[0] == '"' || s[0] == '\'')
                {
                    char quote = s[0];
                    int end = -1;
                    for (int i = 1; i < s.Length; i++)
                    {
                        if (s[i] == quote)
                        {
                            int nb = 0;
                            for (int jj = i - 1; jj >= 1 && s[jj] == '\\'; jj--) nb++;
                            if (nb % 2 == 0) { end = i; break; }
                        }
                    }
                    if (end == -1)
                        throw new LvnsCompileException($"unclosed quote for key \"{key}\"");
                    val = s.Substring(1, end - 1);
                    val = val.Replace("\\\"", "\"").Replace("\\'", "'");
                    s = s.Substring(end + 1);
                }
                else
                {
                    int spaceIdx = IndexOfAny(s, ' ', '\t');
                    if (spaceIdx == -1) { val = s; s = ""; }
                    else { val = s.Substring(0, spaceIdx); s = s.Substring(spaceIdx + 1); }
                }

                res[key] = TypeScalar(val);
                s = s.Trim();
            }
            return res;
        }

        static Dictionary<string, object> ParseKeyValueSafe(string s)
        {
            try { return ParseKeyValue(s); }
            catch (LvnsCompileException) { return null; }
        }

        // TypeScalar coerces a bare (unquoted) value the way Go parseKeyValue does:
        // bool/null, then int64 (no dot) or float64, else string.
        static object TypeScalar(string val)
        {
            if (val == "true") return true;
            if (val == "false") return false;
            if (val == "null") return null;
            if (double.TryParse(val, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double n))
            {
                if (!val.Contains("."))
                {
                    if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out long vi))
                        return vi;
                    return n;
                }
                return n;
            }
            return val;
        }

        static bool IsValidKey(string k)
        {
            if (k.Length == 0) return false;
            foreach (char r in k)
            {
                bool ok = (r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') ||
                          (r >= '0' && r <= '9') || r == '_' || r == '.';
                if (!ok) return false;
            }
            return true;
        }

        // ── animation (anim/move) ────────────────────────────────────────────

        static bool IsDur(string t)
        {
            if (!t.EndsWith("s") || t.Length < 2) return false;
            return double.TryParse(t.Substring(0, t.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        }

        static bool IsAnimWord(string t) =>
            t == "yoyo" || t == "loop" || t == "pingpong" || t == "stop" || IsDur(t);

        static Dictionary<string, object> ParseAnimPositional(string op, string rest)
        {
            var p = new Dictionary<string, object>();
            string[] bracket = null;
            int lb = rest.IndexOf('[');
            if (lb >= 0)
            {
                int rel = rest.Substring(lb).IndexOf(']');
                if (rel < 0) throw new LvnsCompileException("unclosed '[' in keys");
                bracket = SplitFields(rest.Substring(lb + 1, rel - 1).Trim());
                rest = (rest.Substring(0, lb) + " " + rest.Substring(lb + rel + 1)).Trim();
            }
            string[] toks = SplitFields(rest);
            if (toks.Length == 0) throw new LvnsCompileException("need an id");
            p["id"] = toks[0];
            int idx = 1;
            if (op == "anim" && idx < toks.Length && !toks[idx].Contains("=") &&
                !IsAnimWord(toks[idx]) && !toks[idx].Contains(":"))
            {
                p["prop"] = toks[idx];
                idx++;
            }
            var inlineKeys = new List<string>();
            for (int t = idx; t < toks.Length; t++)
            {
                string tok = toks[t];
                if (tok.Contains("="))
                {
                    int e = tok.IndexOf('=');
                    p[tok.Substring(0, e)] = ScalarVal(tok.Substring(e + 1));
                }
                else if (IsDur(tok))
                {
                    double dv = double.Parse(tok.Substring(0, tok.Length - 1), CultureInfo.InvariantCulture);
                    p["dur"] = dv;
                }
                else if (tok == "yoyo" || tok == "loop" || tok == "pingpong")
                {
                    p["loop"] = tok;
                }
                else if (tok == "stop")
                {
                    p["stop"] = true;
                }
                else if (tok.Contains(":"))
                {
                    inlineKeys.Add(tok);
                }
                else if (op == "move")
                {
                    if (p.TryGetValue("path", out object cur) && cur is string cs)
                        p["path"] = cs + " " + tok;
                    else
                        p["path"] = tok;
                }
            }
            if (inlineKeys.Count > 0)
            {
                p["keys"] = string.Join(" ", inlineKeys);
            }
            else if (bracket != null && bracket.Length > 0)
            {
                double d = 1.0;
                if (NumParam(p.TryGetValue("dur", out var dd) ? dd : null, out double dv) && dv > 0) d = dv;
                int nn = bracket.Length;
                var parts = new string[nn];
                for (int k = 0; k < nn; k++)
                {
                    double tt = 0.0;
                    if (nn > 1) tt = (double)k / (nn - 1) * d;
                    parts[k] = G(tt) + ":" + bracket[k];
                }
                p["keys"] = string.Join(" ", parts);
            }
            return p;
        }

        static double[] ParseAnimKeysMaxT(string s, out JArray keys)
        {
            keys = new JArray();
            double maxT = 0;
            foreach (string tok in SplitFields(s))
            {
                string[] parts = tok.Split(new[] { ':' }, 2);
                if (parts.Length != 2)
                    throw new LvnsCompileException($"bad keyframe \"{tok}\" (want t:v)");
                if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double t))
                    throw new LvnsCompileException($"bad time in \"{tok}\"");
                if (!double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    throw new LvnsCompileException($"bad value in \"{tok}\"");
                keys.Add(new JArray { t, v });
                if (t > maxT) maxT = t;
            }
            if (keys.Count == 0) throw new LvnsCompileException("no keyframes");
            return new[] { maxT };
        }

        static JArray ParsePathPoints(string s)
        {
            var pts = new JArray();
            int count = 0;
            foreach (string tok in SplitFields(s))
            {
                string[] parts = tok.Split(new[] { ',' }, 2);
                if (parts.Length != 2)
                    throw new LvnsCompileException($"bad point \"{tok}\" (want x,y)");
                if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double x))
                    throw new LvnsCompileException($"bad x in \"{tok}\"");
                if (!double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
                    throw new LvnsCompileException($"bad y in \"{tok}\"");
                pts.Add(new JArray { x, y });
                count++;
            }
            if (count < 2) throw new LvnsCompileException("path needs at least 2 points");
            return pts;
        }

        static double PropIdentity(string prop)
        {
            switch (prop)
            {
                case "scale":
                case "scalex":
                case "scaley":
                case "alpha":
                    return 1;
                default:
                    return 0;
            }
        }

        static void ParseLoop(object v, out bool loop, out bool yoyo)
        {
            loop = false; yoyo = false;
            if (v is bool b) { loop = b; return; }
            if (v is string s)
            {
                switch (s)
                {
                    case "yoyo":
                    case "pingpong": loop = true; yoyo = true; break;
                    case "true":
                    case "restart":
                    case "loop": loop = true; break;
                }
            }
        }

        static JObject BuildAnimCmd(string op, Dictionary<string, object> p)
        {
            string id = p.TryGetValue("id", out var idv) ? idv as string : null;
            if (string.IsNullOrEmpty(id))
                throw new LvnsCompileException($"{op}: id required");

            // Stop form
            if (p.TryGetValue("stop", out object sv))
            {
                bool isBool = sv is bool;
                bool b = sv is bool bb && bb;
                if (!isBool || b)
                {
                    string target = "all";
                    if (sv is string ss && ss != "" && ss != "true") target = ss;
                    return new JObject { ["op"] = "anim", ["id"] = id, ["stop"] = target };
                }
            }

            string channel = p.TryGetValue("channel", out var ch) ? ch as string : null;
            string mode = p.TryGetValue("mode", out var md) ? md as string : null;
            ParseLoop(p.TryGetValue("loop", out var lp) ? lp : null, out bool loop, out bool yoyo);
            string ease = p.TryGetValue("ease", out var es) ? es as string : null;
            string interp = p.TryGetValue("interp", out var ip) ? ip as string : null;
            bool durSet = NumParam(p.TryGetValue("dur", out var du) ? du : null, out double dur);

            JObject WithShaping(JObject tr)
            {
                if (!string.IsNullOrEmpty(ease)) tr["ease"] = ease;
                if (!string.IsNullOrEmpty(interp)) tr["interp"] = interp;
                return tr;
            }

            var tracks = new JArray();
            double duration;

            if (op == "move")
            {
                double d = dur;
                if (!durSet || d <= 0) d = 1;
                var xs = new JArray();
                var ys = new JArray();
                if (p.TryGetValue("to", out var toObj) && toObj is string to && to != "")
                {
                    JArray pt = ParsePathPoints(to + " " + to);
                    var p0 = (JArray)pt[0];
                    xs.Add(new JArray { 0.0, 0.0 });
                    xs.Add(new JArray { d, p0[0] });
                    ys.Add(new JArray { 0.0, 0.0 });
                    ys.Add(new JArray { d, p0[1] });
                }
                else
                {
                    string pathStr = p.TryGetValue("path", out var pa) ? pa as string : null;
                    JArray pts = ParsePathPoints(pathStr ?? "");
                    int nn = pts.Count;
                    for (int k = 0; k < nn; k++)
                    {
                        var pk = (JArray)pts[k];
                        double t = 0.0;
                        if (nn > 1) t = (double)k / (nn - 1) * d;
                        xs.Add(new JArray { t, pk[0] });
                        ys.Add(new JArray { t, pk[1] });
                    }
                }
                tracks.Add(WithShaping(new JObject { ["prop"] = "screen_x", ["keys"] = xs }));
                tracks.Add(WithShaping(new JObject { ["prop"] = "screen_y", ["keys"] = ys }));
                duration = d;
                if (p.TryGetValue("orient", out var orv) && orv is bool ob && ob)
                    ((JObject)tracks[0])["orient"] = true;
            }
            else // anim
            {
                string prop = p.TryGetValue("prop", out var pr) ? pr as string : null;
                if (string.IsNullOrEmpty(prop))
                    throw new LvnsCompileException("anim: prop required");
                var tr = new JObject { ["prop"] = prop };
                if (NumParam(p.TryGetValue("to", out var tov) ? tov : null, out double toNum))
                {
                    double d = dur;
                    if (!durSet || d <= 0) d = 1;
                    tr["keys"] = new JArray { new JArray { 0.0, PropIdentity(prop) }, new JArray { d, toNum } };
                    duration = d;
                }
                else
                {
                    string keysStr = p.TryGetValue("keys", out var ks) ? ks as string : null;
                    double maxT = ParseAnimKeysMaxT(keysStr ?? "", out JArray keys)[0];
                    tr["keys"] = keys;
                    duration = maxT;
                    if (durSet && dur > 0) duration = dur;
                }
                if (p.TryGetValue("layer", out var ly) && ly is string lstr && lstr != "")
                    tr["layer"] = lstr;
                tracks.Add(WithShaping(tr));
            }

            var anim = new JObject { ["loop"] = loop, ["duration"] = duration, ["tracks"] = tracks };
            if (yoyo) anim["yoyo"] = true;
            var cmd = new JObject { ["op"] = "anim", ["id"] = id, ["anim"] = anim };
            if (!string.IsNullOrEmpty(channel)) cmd["channel"] = channel;
            if (!string.IsNullOrEmpty(mode)) cmd["mode"] = mode;
            return cmd;
        }

        // ── small helpers ────────────────────────────────────────────────────

        static object ScalarVal(string v)
        {
            v = v.Trim();
            if (double.TryParse(v, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double n))
                return n;
            return StripQuotes(v);
        }

        static string StripQuotes(string s)
        {
            s = s.Trim();
            if (s.Length >= 2)
            {
                if ((s[0] == '"' && s[s.Length - 1] == '"') || (s[0] == '\'' && s[s.Length - 1] == '\''))
                    return s.Substring(1, s.Length - 2);
            }
            if (s.StartsWith("«") && s.EndsWith("»"))
            {
                string inner = s.Substring("«".Length, s.Length - "«".Length - "»".Length);
                return inner.Trim();
            }
            return s;
        }

        static bool NumParam(object v, out double result)
        {
            switch (v)
            {
                case double d: result = d; return true;
                case long l: result = l; return true;
                case int i: result = i; return true;
                default: result = 0; return false;
            }
        }

        static bool TryParseAssign(string line, out string key, out string expr)
        {
            key = ""; expr = "";
            int eq = -1;
            for (int idx = 0; idx < line.Length; idx++)
            {
                if (line[idx] != '=') continue;
                char prev = idx > 0 ? line[idx - 1] : '\0';
                char next = idx + 1 < line.Length ? line[idx + 1] : '\0';
                if (prev == '!' || prev == '<' || prev == '>' || prev == '=' || next == '=') continue;
                eq = idx; break;
            }
            if (eq < 0) return false;
            key = line.Substring(0, eq).Trim();
            expr = line.Substring(eq + 1).Trim();
            if (expr == "" || !IsValidKey(key)) return false;
            return true;
        }

        static void NextWord(string s, out string word, out string rest)
        {
            s = s.TrimStart(' ', '\t');
            if (s == "") { word = ""; rest = ""; return; }
            int i = IndexOfAny(s, ' ', '\t', '\n');
            if (i >= 0) { word = s.Substring(0, i); rest = s.Substring(i); }
            else { word = s; rest = ""; }
        }

        static int IndexOfAny(string s, params char[] chars)
        {
            return s.IndexOfAny(chars);
        }

        static string[] SplitFields(string s) =>
            s.Split(new[] { ' ', '\t', '\n', '\r', '\f', '\v' }, StringSplitOptions.RemoveEmptyEntries);

        // GoQuote mirrors Go's %q (strconv.Quote) for the subset that appears in
        // lowered control lines, paired with ParseKeyValue's limited unescaping.
        static string GoQuote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\r': sb.Append("\\r"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append("\"");
            return sb.ToString();
        }

        // G formats a float like Go's %g (shortest round-trip), used to build keys.
        static string G(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        // Tok wraps a parsed scalar (bool/long/double/string/null) as a JToken.
        static JToken Tok(object v)
        {
            if (v == null) return JValue.CreateNull();
            switch (v)
            {
                case bool b: return new JValue(b);
                case long l: return new JValue(l);
                case int i: return new JValue((long)i);
                case double d: return new JValue(d);
                case string s: return new JValue(s);
                default: return JToken.FromObject(v);
            }
        }
    }
}
