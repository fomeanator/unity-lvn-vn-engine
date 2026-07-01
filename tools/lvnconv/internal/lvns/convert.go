package lvns

import (
	"fmt"
	"regexp"
	"strconv"
	"strings"
)

// Cmd is one .lvn command object.
type Cmd map[string]any

// Doc is the .lvn document shape ({scene?, script}).
type Doc struct {
	Scene  string `json:"scene,omitempty"`
	Script []Cmd  `json:"script"`

	// SrcLine[i] is the 1-based source line that produced Script[i]. Authoring
	// metadata for editor diagnostics — never serialized into the .lvn.
	SrcLine []int `json:"-"`
}

var KnownOps = map[string]bool{
	"say": true, "choice": true, "bg": true, "actor": true, "obj": true,
	"fade": true, "dim": true, "flash": true, "tint": true, "blur": true,
	"camera": true, "particles": true,
	"audio": true, "wait": true, "preload": true, "text_pace": true,
	"text": true,               // reactive HUD/stat label
	"save": true, "load": true, // snapshot save/load (func is lowered away by expandLoops)
	"label": true, "goto": true, "if": true,
	"set": true, "inc": true, "hint": true,
	"call": true, "return": true,
	// Script-driven animation: `anim` tweens any prop of an entity/layer over
	// time; `move` is sugar for a screen-space path. Both compile to an "anim"
	// command carrying an LvnAnim payload (see buildAnimCmd).
	"anim": true, "move": true,
}

var reDialogue = regexp.MustCompile(`(?s)^([^:=\n]+?)(?:\s*\[([^\]]+)\])?\s*:\s*(.*)$`)

// Convert parses lvns source and returns the .lvn document.
func Convert(src string) (*Doc, error) {
	// Lower the sugar before the line parser runs (core language stays tiny):
	//  1. collect func signatures (so calls can bind params positionally),
	//  2. expandLoops: split inline blocks + lower for/while/if/func to label/goto,
	//  3. expandCalls: rewrite call sites + `return <expr>` once they're own-lines.
	funcs := collectFuncs(src)
	expanded, err := expandLoops(src)
	if err != nil {
		return nil, err
	}
	src = expandCalls(expanded, funcs)

	doc := &Doc{Script: []Cmd{}}
	actorMaps := make(map[string]string)
	nf := 0 // counter for synthesized fall-through labels (single-branch `if … -> …`)

	// Pre-process and clean lines. `srcNo` keeps each cleaned line's original
	// 1-based source line number, so commands can map back to the editor.
	var lines []string
	var srcNo []int
	rawLines := strings.Split(src, "\n")
	const urlGuard = "\x00PROTO\x00"

	chevDepth := 0 // >0 while inside an unclosed «…» (multi-line string)
	var cbuf strings.Builder
	cbufSrc := 0
	for idx, raw := range rawLines {
		if chevDepth > 0 {
			// Inside a multi-line «…»: keep the raw line verbatim (no comment strip,
			// no blank-skip) and join with a real newline, until the » closes it.
			cbuf.WriteString("\n")
			cbuf.WriteString(raw)
			for _, r := range raw {
				if r == '«' {
					chevDepth++
				} else if r == '»' && chevDepth > 0 {
					chevDepth--
				}
			}
			if chevDepth == 0 {
				lines = append(lines, strings.TrimSpace(cbuf.String()))
				srcNo = append(srcNo, cbufSrc)
				cbuf.Reset()
			}
			continue
		}

		// Strip inline // comments, protecting URL "://".
		line := strings.ReplaceAll(raw, "://", urlGuard)
		if ci := strings.Index(line, "//"); ci >= 0 {
			line = line[:ci]
		}
		line = strings.TrimSpace(strings.ReplaceAll(line, urlGuard, "://"))

		// Skip comments and empty lines
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}

		// Does this line OPEN an unclosed «…»? If so, start buffering continuation
		// lines so a multi-line label/say/choice text stays one logical line.
		d := 0
		for _, r := range line {
			if r == '«' {
				d++
			} else if r == '»' && d > 0 {
				d--
			}
		}
		if d > 0 {
			chevDepth = d
			cbuf.Reset()
			cbuf.WriteString(line)
			cbufSrc = idx + 1
			continue
		}

		lines = append(lines, line)
		srcNo = append(srcNo, idx+1)
	}
	if cbuf.Len() > 0 { // unterminated «…» at EOF — emit what we have
		lines = append(lines, strings.TrimSpace(cbuf.String()))
		srcNo = append(srcNo, cbufSrc)
	}

	// emit appends a command and records the source line it came from.
	emit := func(c Cmd, line int) {
		doc.Script = append(doc.Script, c)
		doc.SrcLine = append(doc.SrcLine, line)
	}

	for i := 0; i < len(lines); {
		line := lines[i]

		// 1. Directives: scene
		if strings.HasPrefix(line, "scene ") {
			doc.Scene = strings.TrimSpace(line[6:])
			i++
			continue
		}
		if strings.HasPrefix(line, "scene:") {
			doc.Scene = strings.TrimSpace(line[6:])
			i++
			continue
		}

		// 2. Directives: actor_map
		if strings.HasPrefix(line, "actor_map ") {
			mapping := strings.TrimSpace(line[10:])
			parts := strings.SplitN(mapping, "=", 2)
			if len(parts) == 2 {
				actorMaps[strings.TrimSpace(parts[0])] = strings.TrimSpace(parts[1])
			}
			i++
			continue
		}

		// 3. Label: :label_name
		if strings.HasPrefix(line, ":") {
			labelID := strings.TrimPrefix(line, ":")
			labelID = strings.TrimSpace(labelID)
			if labelID == "" {
				return nil, fmt.Errorf("line %d: label cannot be empty", i+1)
			}
			emit(Cmd{"op": "label", "id": labelID}, srcNo[i])
			i++
			continue
		}

		// 4. Choice: consecutive lines starting with `-` (but not `->`, which is goto)
		if strings.HasPrefix(line, "-") && !strings.HasPrefix(line, "->") {
			var options []any
			j := i
			for j < len(lines) {
				curr := lines[j]
				if strings.HasPrefix(curr, "-") && !strings.HasPrefix(curr, "->") {
					opt, err := parseChoiceOption(curr)
					if err != nil {
						return nil, fmt.Errorf("line %d: %w", j+1, err)
					}
					options = append(options, opt)
					j++
				} else {
					break
				}
			}
			emit(Cmd{"op": "choice", "options": options}, srcNo[i])
			i = j
			continue
		}

		// 4b. Arrow goto: `-> label`
		if strings.HasPrefix(line, "->") {
			target := strings.TrimSpace(line[2:])
			if target == "" {
				return nil, fmt.Errorf("line %d: '->' needs a label", srcNo[i])
			}
			emit(Cmd{"op": "goto", "label": target}, srcNo[i])
			i++
			continue
		}

		// 4c. Single-branch if: `if <cond> -> <label>` (falls through when false).
		// Block `if <cond> { … } else { … }` is expanded earlier into the canonical
		// `if expr= then= else=` form, so here only the arrow form reaches us.
		if strings.HasPrefix(line, "if ") && strings.Contains(line, "->") {
			rest := strings.TrimSpace(line[3:])
			ai := strings.Index(rest, "->")
			cond := strings.TrimSpace(rest[:ai])
			target := strings.TrimSpace(rest[ai+2:])
			if cond == "" || target == "" {
				return nil, fmt.Errorf("line %d: expected 'if <cond> -> <label>'", srcNo[i])
			}
			nf++
			fall := fmt.Sprintf("__nf%d", nf)
			emit(Cmd{"op": "if", "expr": cond, "then": target, "else": fall}, srcNo[i])
			emit(Cmd{"op": "label", "id": fall}, srcNo[i])
			i++
			continue
		}

		// 4d. Variable assignment: `name = expr` (init and mutation alike)
		if key, expr, ok := parseAssign(line); ok && !KnownOps[key] {
			emit(Cmd{"op": "set", "key": key, "expr": expr}, srcNo[i])
			i++
			continue
		}

		// 5. Commands and Dialogue
		words := strings.Fields(line)
		firstWord := ""
		if len(words) > 0 {
			firstWord = words[0]
		}

		isCommand := false
		var cmd Cmd

		if KnownOps[firstWord] {
			if firstWord == "anim" || firstWord == "move" {
				// Surface malformed anim/move as a real compile error instead of
				// silently letting the line fall through to narration (`say`).
				rest := strings.TrimSpace(line[len(firstWord):])
				toks := strings.Fields(rest)
				var params map[string]any
				var err error
				if len(toks) > 0 && !strings.Contains(toks[0], "=") {
					params, err = parseAnimPositional(firstWord, rest) // terse: anim goblin2 scale [1 1.03 1] 2s yoyo
				} else {
					params, err = parseKeyValue(rest) // legacy: anim id=… prop=… keys=…
				}
				if err != nil {
					return nil, fmt.Errorf("line %d: %s: %w", srcNo[i], firstWord, err)
				}
				ac, err := buildAnimCmd(firstWord, params)
				if err != nil {
					return nil, fmt.Errorf("line %d: %w", srcNo[i], err)
				}
				isCommand = true
				cmd = ac
			} else if firstWord == "actor" {
				rest := strings.TrimSpace(line[len("actor"):])
				toks := strings.Fields(rest)
				if len(toks) > 0 && !strings.Contains(toks[0], "=") {
					// Terse: actor <id> [pos|emotion|hide|show] [w= h= x= y= scale= anchor= …]
					ac := Cmd{"op": "actor", "id": toks[0], "show": true}
					for _, t := range toks[1:] {
						if strings.Contains(t, "=") {
							kv := strings.SplitN(t, "=", 2)
							k := kv[0]
							switch k {
							case "w":
								k = "width"
							case "h":
								k = "height"
							}
							ac[k] = scalarVal(kv[1])
						} else {
							switch t {
							case "hide":
								ac["show"] = false
							case "show":
								ac["show"] = true
							case "left", "right", "center", "far_left", "far_right", "offscreen_left", "offscreen_right":
								ac["position"] = t
							default:
								ac["emotion"] = t // pose / emotion axis value
							}
						}
					}
					isCommand = true
					cmd = ac
				} else if params, err := parseKeyValue(rest); err == nil {
					// Legacy: actor id=… show=true position=…
					isCommand = true
					cmd = Cmd{"op": "actor"}
					for k, v := range params {
						cmd[k] = v
					}
				}
			} else if firstWord == "bg" {
				rest := strings.TrimSpace(line[len("bg"):])
				if rest != "" && !strings.Contains(rest, "=") {
					// Terse: bg <url>  (id derived from the file name)
					c := Cmd{"op": "bg", "sprite_url": stripQuotes(rest)}
					base := rest
					if sl := strings.LastIndexAny(base, "/\\"); sl >= 0 {
						base = base[sl+1:]
					}
					if dot := strings.LastIndex(base, "."); dot >= 0 {
						base = base[:dot]
					}
					if base != "" {
						c["id"] = base
					}
					isCommand = true
					cmd = c
				} else if params, err := parseKeyValue(rest); err == nil {
					// Legacy: bg id=… sprite_url=…
					isCommand = true
					cmd = Cmd{"op": "bg"}
					for k, v := range params {
						cmd[k] = v
					}
				}
			} else if firstWord == "text" {
				// Reactive label: text <id> [x= y= anchor= size= color= font=] «{expr}…»
				// or `text <id> hide`. Leading whitespace-tokens are id + k=v params;
				// the rest (which may span newlines inside «…») is the template.
				rem := strings.TrimSpace(line[len("text"):])
				id, after := nextWord(rem)
				if id != "" {
					c := Cmd{"op": "text", "id": id}
					rem = after
					for {
						w, next := nextWord(rem)
						if w == "" {
							break
						}
						if w == "hide" && strings.TrimSpace(next) == "" {
							c["hide"] = true
							rem = ""
							break
						}
						if strings.Contains(w, "=") {
							kv := strings.SplitN(w, "=", 2)
							c[kv[0]] = scalarVal(kv[1])
							rem = next
							continue
						}
						break // w begins the template — stop consuming params
					}
					tmpl := strings.TrimSpace(rem)
					if tmpl != "" {
						c["text"] = stripQuotes(tmpl)
					}
					isCommand = true
					cmd = c
				}
			} else if firstWord == "return" && len(words) == 1 {
				isCommand = true
				cmd = Cmd{"op": "return"}
			} else if (firstWord == "goto" || firstWord == "call") && len(words) == 2 {
				isCommand = true
				cmd = Cmd{"op": firstWord, "label": words[1]}
			} else if firstWord != "return" && firstWord != "goto" && firstWord != "call" {
				rest := strings.TrimSpace(line[len(firstWord):])
				if rest == "" {
					isCommand = true
					cmd = Cmd{"op": firstWord}
				} else {
					if params, err := parseKeyValue(rest); err == nil {
						isCommand = true
						cmd = Cmd{"op": firstWord}
						for k, v := range params {
							cmd[k] = v
						}
					}
				}
			}
		}

		if isCommand {
			emit(cmd, srcNo[i])
			i++
			continue
		}

		// Dialogue: Name [emotion]: Text or Narration
		if m := reDialogue.FindStringSubmatch(line); m != nil {
			speaker := strings.TrimSpace(m[1])
			emotion := strings.TrimSpace(m[2])
			text := strings.TrimSpace(m[3])

			text = stripQuotes(text)

			if emotion != "" {
				actorID, ok := actorMaps[speaker]
				if !ok {
					actorID = strings.ToLower(strings.ReplaceAll(speaker, " ", "_"))
				}
				emit(Cmd{"op": "actor", "id": actorID, "emotion": emotion}, srcNo[i])
			}

			emit(Cmd{"op": "say", "who": speaker, "text": text}, srcNo[i])
		} else {
			// Narration
			text := stripQuotes(line)
			emit(Cmd{"op": "say", "text": text}, srcNo[i])
		}

		i++
	}

	return doc, nil
}

// splitInline turns a one-line control block into the own-line brace form, so
// authors can write `if c { x }`, `if c { x } else { y }`, `} else { y }`,
// `for i in xs { x }` on a single line. Brace matching is string-/«»-aware and
// depth-counted, so interpolation ({hp}) and map literals ({a:1}) in the body
// survive intact. A non-control line, or a control line already in own-line form
// (ends with `{`, or is bare `}` / `} else {`), passes through unchanged.
func splitInline(line string) []string {
	t := stripLineComment(strings.TrimSpace(line))
	det := strings.TrimSpace(t)
	if det == "" {
		return []string{line}
	}
	isCtl := strings.HasPrefix(det, "if ") || strings.HasPrefix(det, "for ") ||
		strings.HasPrefix(det, "while ") || strings.HasPrefix(det, "func ") ||
		strings.HasPrefix(det, "}")
	if !isCtl || strings.HasSuffix(det, "{") || det == "}" ||
		strings.ReplaceAll(det, " ", "") == "}else{" {
		return []string{line} // not inline, or already own-line form
	}

	rs := []rune(det)
	open := firstBlockBrace(rs)
	if open < 0 {
		return []string{line} // e.g. `if c -> label` (handled elsewhere)
	}
	close := matchBrace(rs, open)
	if close < 0 {
		return []string{line}
	}

	var out []string
	if strings.HasPrefix(det, "}") {
		out = append(out, "} else {") // shape: } else { BODY }
	} else {
		out = append(out, strings.TrimSpace(string(rs[:open]))+" {")
	}
	body := strings.TrimSpace(string(rs[open+1 : close]))
	if body != "" {
		out = append(out, splitInline(body)...)
	}
	tail := strings.TrimSpace(string(rs[close+1:]))
	switch {
	case tail == "":
		out = append(out, "}")
	case strings.HasPrefix(tail, "else"):
		out = append(out, splitInline("} "+tail)...) // BODY } else { BODY2 }
	default:
		out = append(out, "}")
		out = append(out, splitInline(tail)...)
	}
	return out
}

// firstBlockBrace returns the index of the first '{' that is not inside a string
// or «…» (so a quoted condition or chevron text doesn't fool it), or -1.
func firstBlockBrace(rs []rune) int {
	var inStr rune
	chev := 0
	for i := 0; i < len(rs); i++ {
		c := rs[i]
		if inStr != 0 {
			if c == inStr {
				inStr = 0
			}
			continue
		}
		switch {
		case c == '«':
			chev++
		case c == '»':
			if chev > 0 {
				chev--
			}
		case chev > 0:
			// inside chevrons
		case c == '"' || c == '\'':
			inStr = c
		case c == '{':
			return i
		}
	}
	return -1
}

// matchBrace returns the index of the '}' matching the '{' at open (depth-counted,
// ignoring braces inside strings/«…»), or -1.
func matchBrace(rs []rune, open int) int {
	var inStr rune
	chev, depth := 0, 0
	for i := open; i < len(rs); i++ {
		c := rs[i]
		if inStr != 0 {
			if c == inStr {
				inStr = 0
			}
			continue
		}
		switch {
		case c == '«':
			chev++
		case c == '»':
			if chev > 0 {
				chev--
			}
		case chev > 0:
			// inside chevrons
		case c == '"' || c == '\'':
			inStr = c
		case c == '{':
			depth++
		case c == '}':
			depth--
			if depth == 0 {
				return i
			}
		}
	}
	return -1
}

// stripLineComment removes a trailing // comment that is not inside a string,
// «…» or a URL (://). Used only for inline-block detection/splitting.
func stripLineComment(s string) string {
	rs := []rune(s)
	var inStr rune
	chev := 0
	for i := 0; i < len(rs); i++ {
		c := rs[i]
		if inStr != 0 {
			if c == inStr {
				inStr = 0
			}
			continue
		}
		switch {
		case c == '«':
			chev++
		case c == '»':
			if chev > 0 {
				chev--
			}
		case chev > 0:
			// inside chevrons
		case c == '"' || c == '\'':
			inStr = c
		case c == '/' && i+1 < len(rs) && rs[i+1] == '/':
			if i > 0 && rs[i-1] == ':' {
				continue // part of :// in a URL
			}
			return string(rs[:i])
		}
	}
	return s
}

var reFuncDef = regexp.MustCompile(`^\s*func\s+([A-Za-z_]\w*)\s*\(([^)]*)\)\s*\{\s*$`)
var reCall = regexp.MustCompile(`^\s*(?:([A-Za-z_]\w*)\s*=\s*)?([A-Za-z_]\w*)\s*\((.*)\)\s*$`)

// collectFuncs records each `func name(p1, p2) { … }` signature so call sites can
// bind arguments to the parameter names positionally.
func collectFuncs(src string) map[string][]string {
	m := map[string][]string{}
	for _, line := range strings.Split(src, "\n") {
		mm := reFuncDef.FindStringSubmatch(line)
		if mm == nil {
			continue
		}
		var ps []string
		for _, p := range strings.Split(mm[2], ",") {
			if p = strings.TrimSpace(p); p != "" {
				ps = append(ps, p)
			}
		}
		m[mm[1]] = ps
	}
	return m
}

// expandCalls rewrites call statements and `return <expr>` into core primitives,
// once blocks have been flattened to own-lines. A call `name(a, b)` becomes
// `<param1> = a` / `<param2> = b` / `call __fn_name`; `r = name(a)` adds
// `r = __ret`. Only registered func names are touched, so built-in expression
// calls (push/rand/…) and ordinary text pass through untouched.
func expandCalls(src string, funcs map[string][]string) string {
	var out []string
	for _, line := range strings.Split(src, "\n") {
		t := strings.TrimSpace(line)

		// `return <expr>` → stash the value, then return.
		if strings.HasPrefix(t, "return ") {
			if expr := strings.TrimSpace(t[len("return "):]); expr != "" {
				out = append(out, "__ret = "+expr, "return")
				continue
			}
		}

		if mm := reCall.FindStringSubmatch(t); mm != nil {
			lhs, fname, argstr := mm[1], mm[2], mm[3]
			if params, ok := funcs[fname]; ok {
				args := splitArgs(argstr)
				for i, p := range params {
					if i < len(args) {
						out = append(out, p+" = "+args[i]) // bind param (assignment sugar)
					}
				}
				out = append(out, "call __fn_"+fname)
				if lhs != "" {
					out = append(out, lhs+" = __ret")
				}
				continue
			}
		}
		out = append(out, line)
	}
	return strings.Join(out, "\n")
}

// splitArgs splits a call's argument list on top-level commas, respecting
// nested (), [], {}, quotes and «…».
func splitArgs(s string) []string {
	var args []string
	rs := []rune(s)
	var inStr rune
	chev, depth, start := 0, 0, 0
	for i := 0; i < len(rs); i++ {
		c := rs[i]
		if inStr != 0 {
			if c == inStr {
				inStr = 0
			}
			continue
		}
		switch {
		case c == '«':
			chev++
		case c == '»':
			if chev > 0 {
				chev--
			}
		case chev > 0:
		case c == '"' || c == '\'':
			inStr = c
		case c == '(' || c == '[' || c == '{':
			depth++
		case c == ')' || c == ']' || c == '}':
			depth--
		case c == ',' && depth == 0:
			args = append(args, strings.TrimSpace(string(rs[start:i])))
			start = i + 1
		}
	}
	if last := strings.TrimSpace(string(rs[start:])); last != "" {
		args = append(args, last)
	}
	return args
}

// expandLoops rewrites block iteration into the flat primitives the line parser
// already understands. Two forms (the brace must end the opening line; `}` stands
// alone):
//
//	for <var> in <expr> { … }     while <expr> { … }
//
// A `for` desugars to: stash the collection, walk an index with len()+[], bind
// <var> each pass. A `while` desugars to a guarded label loop. Labels are unique
// per loop and nest via a stack, so loops can contain loops.
func expandLoops(src string) (string, error) {
	type frame struct {
		kind            string // "for" | "while" | "if"
		loopLbl, endLbl string
		idxVar          string // for-only
		elseLbl         string // if-only
		sawElse         bool   // if-only
	}
	var stack []frame
	var out []string
	ctr := 0

	// Flatten inline blocks (`if c { … }`, `} else { … }`, `for/while c { … }`)
	// into the own-line brace form the loop below expects.
	var srcLines []string
	for _, raw := range strings.Split(src, "\n") {
		srcLines = append(srcLines, splitInline(raw)...)
	}

	for _, raw := range srcLines {
		det := strings.TrimSpace(raw)
		if ci := strings.Index(det, "//"); ci >= 0 { // ignore trailing comments for detection
			det = strings.TrimSpace(det[:ci])
		}

		switch {
		case strings.HasPrefix(det, "for ") && strings.HasSuffix(det, "{"):
			inner := strings.TrimSpace(strings.TrimSuffix(det[4:], "{"))
			pos := strings.Index(inner, " in ")
			if pos < 0 {
				return "", fmt.Errorf("for: expected 'for <var> in <expr> {', got %q", det)
			}
			itemVar := strings.TrimSpace(inner[:pos])
			expr := strings.TrimSpace(inner[pos+4:])
			if itemVar == "" || expr == "" {
				return "", fmt.Errorf("for: empty variable or collection in %q", det)
			}
			ctr++
			idx := fmt.Sprintf("__i%d", ctr)
			sv := fmt.Sprintf("__src%d", ctr)
			loop := fmt.Sprintf("__loop%d", ctr)
			body := fmt.Sprintf("__body%d", ctr)
			end := fmt.Sprintf("__end%d", ctr)
			out = append(out,
				fmt.Sprintf("set key=%s expr=%q", sv, expr),
				fmt.Sprintf("set key=%s value=0", idx),
				":"+loop,
				fmt.Sprintf("if expr=%q then=%s else=%s", fmt.Sprintf("%s < len(%s)", idx, sv), body, end),
				":"+body,
				fmt.Sprintf("set key=%s expr=%q", itemVar, fmt.Sprintf("%s[%s]", sv, idx)),
			)
			stack = append(stack, frame{kind: "for", loopLbl: loop, endLbl: end, idxVar: idx})

		case strings.HasPrefix(det, "while ") && strings.HasSuffix(det, "{"):
			expr := strings.TrimSpace(strings.TrimSuffix(det[6:], "{"))
			if expr == "" {
				return "", fmt.Errorf("while: empty condition in %q", det)
			}
			ctr++
			loop := fmt.Sprintf("__loop%d", ctr)
			body := fmt.Sprintf("__body%d", ctr)
			end := fmt.Sprintf("__end%d", ctr)
			out = append(out,
				":"+loop,
				fmt.Sprintf("if expr=%q then=%s else=%s", expr, body, end),
				":"+body,
			)
			stack = append(stack, frame{kind: "while", loopLbl: loop, endLbl: end})

		case strings.HasPrefix(det, "func ") && strings.HasSuffix(det, "{"):
			inner := strings.TrimSpace(strings.TrimSuffix(strings.TrimPrefix(det, "func "), "{"))
			name := inner
			if p := strings.Index(inner, "("); p >= 0 {
				name = strings.TrimSpace(inner[:p])
			}
			if name == "" {
				return "", fmt.Errorf("func: missing name in %q", det)
			}
			ctr++
			skip := fmt.Sprintf("__fnskip%d", ctr)
			// jump over the definition in linear flow; body is a `call`-only routine
			out = append(out, "goto "+skip, ":__fn_"+name)
			stack = append(stack, frame{kind: "func", endLbl: skip})

		case strings.HasPrefix(det, "if ") && strings.HasSuffix(det, "{"):
			cond := strings.TrimSpace(strings.TrimSuffix(det[3:], "{"))
			if cond == "" {
				return "", fmt.Errorf("if: empty condition in %q", det)
			}
			ctr++
			thenL := fmt.Sprintf("__then%d", ctr)
			elseL := fmt.Sprintf("__else%d", ctr)
			endL := fmt.Sprintf("__end%d", ctr)
			out = append(out,
				fmt.Sprintf("if expr=%q then=%s else=%s", cond, thenL, elseL),
				":"+thenL,
			)
			stack = append(stack, frame{kind: "if", endLbl: endL, elseLbl: elseL})

		case strings.ReplaceAll(det, " ", "") == "}else{":
			if len(stack) == 0 || stack[len(stack)-1].kind != "if" {
				return "", fmt.Errorf("'} else {' without a matching 'if … {'")
			}
			f := &stack[len(stack)-1]
			out = append(out, "goto "+f.endLbl, ":"+f.elseLbl) // end of then-branch; else-branch follows
			f.sawElse = true

		case det == "}":
			if len(stack) == 0 {
				return "", fmt.Errorf("unmatched '}' (no open for/while/if block)")
			}
			f := stack[len(stack)-1]
			stack = stack[:len(stack)-1]
			switch f.kind {
			case "for":
				out = append(out, fmt.Sprintf("set key=%s expr=%q", f.idxVar, fmt.Sprintf("%s + 1", f.idxVar)), "goto "+f.loopLbl, ":"+f.endLbl)
			case "while":
				out = append(out, "goto "+f.loopLbl, ":"+f.endLbl)
			case "func":
				out = append(out, "return", ":"+f.endLbl) // safety return + skip-over label
			case "if":
				if f.sawElse {
					out = append(out, ":"+f.endLbl) // else-branch falls into end
				} else {
					out = append(out, ":"+f.elseLbl, ":"+f.endLbl) // no else: else target == end
				}
			}

		default:
			out = append(out, raw)
		}
	}

	if len(stack) > 0 {
		return "", fmt.Errorf("unclosed for/while block (missing '}')")
	}
	return strings.Join(out, "\n"), nil
}

func parseChoiceOption(line string) (map[string]any, error) {
	text := strings.TrimSpace(line[1:]) // strip '-'
	arrowIdx := strings.Index(text, "->")
	if arrowIdx == -1 {
		return nil, fmt.Errorf("choice option must have a target label (use '-> label')")
	}
	optText := strings.TrimSpace(text[:arrowIdx])
	if optText == "" {
		return nil, fmt.Errorf("choice option text cannot be empty")
	}
	rest := strings.TrimSpace(text[arrowIdx+2:])
	if rest == "" {
		return nil, fmt.Errorf("choice option must specify a target label after '->'")
	}

	spaceIdx := strings.IndexAny(rest, " \t")
	var targetLabel string
	var paramsStr string
	if spaceIdx == -1 {
		targetLabel = rest
	} else {
		targetLabel = rest[:spaceIdx]
		paramsStr = strings.TrimSpace(rest[spaceIdx+1:])
	}

	opt := map[string]any{
		"text": stripQuotes(optText),
		"goto": targetLabel,
	}

	if paramsStr != "" {
		params, err := parseKeyValue(paramsStr)
		if err != nil {
			return nil, fmt.Errorf("invalid choice option parameters: %w", err)
		}
		for k, v := range params {
			opt[k] = v
		}
	}
	normalizeChoiceCost(opt)
	return opt, nil
}

// normalizeChoiceCost rewrites a `cost=<var>:<amount>` shorthand into a
// structured {var,amount} cost the runtime can actually deduct. A plain-string
// cost with no numeric var:amount pair (e.g. cost="дорого") is left untouched —
// it stays pure flavour text shown beneath the option.
func normalizeChoiceCost(opt map[string]any) {
	s, ok := opt["cost"].(string)
	if !ok {
		return
	}
	i := strings.LastIndex(s, ":")
	if i <= 0 || i == len(s)-1 {
		return
	}
	varName := strings.TrimSpace(s[:i])
	amtStr := strings.TrimSpace(s[i+1:])
	if !isValidKey(varName) {
		return
	}
	if n, err := strconv.ParseInt(amtStr, 10, 64); err == nil {
		opt["cost"] = map[string]any{"var": varName, "amount": n}
	} else if f, err := strconv.ParseFloat(amtStr, 64); err == nil {
		opt["cost"] = map[string]any{"var": varName, "amount": f}
	}
}

func parseKeyValue(s string) (map[string]any, error) {
	res := make(map[string]any)
	s = strings.TrimSpace(s)
	for len(s) > 0 {
		eqIdx := strings.Index(s, "=")
		if eqIdx == -1 {
			return nil, fmt.Errorf("expected '=' in key-value pair at %q", s)
		}
		key := strings.TrimSpace(s[:eqIdx])
		if !isValidKey(key) {
			return nil, fmt.Errorf("invalid key name %q", key)
		}
		s = s[eqIdx+1:]
		s = strings.TrimSpace(s)
		if len(s) == 0 {
			return nil, fmt.Errorf("missing value for key %q", key)
		}

		var val string
		if s[0] == '"' || s[0] == '\'' {
			quote := s[0]
			end := -1
			for i := 1; i < len(s); i++ {
				if s[i] == quote {
					// count consecutive preceding backslashes — an even count
					// means this quote is NOT escaped (handles a trailing "\\").
					nb := 0
					for j := i - 1; j >= 1 && s[j] == '\\'; j-- {
						nb++
					}
					if nb%2 == 0 {
						end = i
						break
					}
				}
			}
			if end == -1 {
				return nil, fmt.Errorf("unclosed quote for key %q", key)
			}
			val = s[1:end]
			val = strings.ReplaceAll(val, "\\\"", "\"")
			val = strings.ReplaceAll(val, "\\'", "'")
			s = s[end+1:]
		} else {
			spaceIdx := strings.IndexAny(s, " \t")
			if spaceIdx == -1 {
				val = s
				s = ""
			} else {
				val = s[:spaceIdx]
				s = s[spaceIdx+1:]
			}
		}

		if val == "true" {
			res[key] = true
		} else if val == "false" {
			res[key] = false
		} else if val == "null" {
			res[key] = nil
		} else if n, err := strconv.ParseFloat(val, 64); err == nil {
			if !strings.Contains(val, ".") {
				if valInt, err := strconv.ParseInt(val, 10, 64); err == nil {
					res[key] = valInt
				} else {
					res[key] = n
				}
			} else {
				res[key] = n
			}
		} else {
			res[key] = val
		}
		s = strings.TrimSpace(s)
	}
	return res, nil
}

func isValidKey(k string) bool {
	if len(k) == 0 {
		return false
	}
	for _, r := range k {
		if !((r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') || (r >= '0' && r <= '9') || r == '_' || r == '.') {
			return false
		}
	}
	return true
}

func stripQuotes(s string) string {
	s = strings.TrimSpace(s)
	if len(s) >= 2 {
		if (s[0] == '"' && s[len(s)-1] == '"') || (s[0] == '\'' && s[len(s)-1] == '\'') {
			return s[1 : len(s)-1]
		}
	}
	// French quotes «…» (the multi-line/dialogue delimiter), trimmed as a unit.
	if strings.HasPrefix(s, "«") && strings.HasSuffix(s, "»") {
		return strings.TrimSpace(strings.TrimSuffix(strings.TrimPrefix(s, "«"), "»"))
	}
	return s
}

// numParam coerces a parsed key-value number (int64 or float64) to a float.
func numParam(v any) (float64, bool) {
	switch n := v.(type) {
	case float64:
		return n, true
	case int64:
		return float64(n), true
	case int:
		return float64(n), true
	}
	return 0, false
}

// parseAnimKeys turns "t:v t:v …" into [[t,v],…] keyframes and the max time.
func parseAnimKeys(s string) ([]any, float64, error) {
	var keys []any
	var maxT float64
	for _, tok := range strings.Fields(s) {
		parts := strings.SplitN(tok, ":", 2)
		if len(parts) != 2 {
			return nil, 0, fmt.Errorf("bad keyframe %q (want t:v)", tok)
		}
		t, err := strconv.ParseFloat(strings.TrimSpace(parts[0]), 64)
		if err != nil {
			return nil, 0, fmt.Errorf("bad time in %q", tok)
		}
		v, err := strconv.ParseFloat(strings.TrimSpace(parts[1]), 64)
		if err != nil {
			return nil, 0, fmt.Errorf("bad value in %q", tok)
		}
		keys = append(keys, []any{t, v})
		if t > maxT {
			maxT = t
		}
	}
	if len(keys) == 0 {
		return nil, 0, fmt.Errorf("no keyframes")
	}
	return keys, maxT, nil
}

// parseAssign recognises a bare `name = expr` line (variable init/mutation). It
// rejects comparison operators (==, !=, >=, <=) and any left side that isn't a
// plain identifier, so `if a == b`, `actor g center y=.5` etc. are left alone.
func parseAssign(line string) (key, expr string, ok bool) {
	eq := -1
	for idx := 0; idx < len(line); idx++ {
		if line[idx] != '=' {
			continue
		}
		var prev, next byte
		if idx > 0 {
			prev = line[idx-1]
		}
		if idx+1 < len(line) {
			next = line[idx+1]
		}
		if prev == '!' || prev == '<' || prev == '>' || prev == '=' || next == '=' {
			continue // part of == != >= <=
		}
		eq = idx
		break
	}
	if eq < 0 {
		return "", "", false
	}
	key = strings.TrimSpace(line[:eq])
	expr = strings.TrimSpace(line[eq+1:])
	if expr == "" || !isValidKey(key) {
		return "", "", false
	}
	return key, expr, true
}

// nextWord returns the first whitespace-delimited token of s and the remainder
// starting at the delimiter (so a following multi-line «…» template keeps its
// newlines). Empty token when s has no token.
func nextWord(s string) (word, rest string) {
	s = strings.TrimLeft(s, " \t")
	if s == "" {
		return "", ""
	}
	if i := strings.IndexAny(s, " \t\n"); i >= 0 {
		return s[:i], s[i:]
	}
	return s, ""
}

// scalarVal types a bare value: a number stays numeric, anything else is a
// (quote-stripped) string. Used by the terse positional actor/anim forms.
func scalarVal(v string) any {
	v = strings.TrimSpace(v)
	if n, err := strconv.ParseFloat(v, 64); err == nil {
		return n
	}
	return stripQuotes(v)
}

func isDur(t string) bool { // "2s", "0.2s", ".5s"
	if !strings.HasSuffix(t, "s") || len(t) < 2 {
		return false
	}
	_, err := strconv.ParseFloat(strings.TrimSuffix(t, "s"), 64)
	return err == nil
}

func isAnimWord(t string) bool {
	return t == "yoyo" || t == "loop" || t == "pingpong" || t == "stop" || isDur(t)
}

// parseAnimPositional reads the terse anim/move form into the param map that
// buildAnimCmd expects:
//
//	anim <id> <prop> [v v v] <dur>s [yoyo|loop] [ease= …]   // bracket list spread over dur
//	anim <id> <prop> 0:1 .5:1.1 1:1 …                       // explicit t:v keyframes
//	move <id> 0.2,0.5 0.8,0.5 1s                            // path points
func parseAnimPositional(op, rest string) (map[string]any, error) {
	p := map[string]any{}
	var bracket []string
	if lb := strings.Index(rest, "["); lb >= 0 {
		rel := strings.Index(rest[lb:], "]")
		if rel < 0 {
			return nil, fmt.Errorf("unclosed '[' in keys")
		}
		bracket = strings.Fields(strings.TrimSpace(rest[lb+1 : lb+rel]))
		rest = strings.TrimSpace(rest[:lb] + " " + rest[lb+rel+1:])
	}
	toks := strings.Fields(rest)
	if len(toks) == 0 {
		return nil, fmt.Errorf("need an id")
	}
	p["id"] = toks[0]
	idx := 1
	if op == "anim" && idx < len(toks) && !strings.Contains(toks[idx], "=") && !isAnimWord(toks[idx]) && !strings.Contains(toks[idx], ":") {
		p["prop"] = toks[idx]
		idx++
	}
	var inlineKeys []string
	for _, t := range toks[idx:] {
		switch {
		case strings.Contains(t, "="):
			kv := strings.SplitN(t, "=", 2)
			p[kv[0]] = scalarVal(kv[1])
		case isDur(t):
			d, _ := strconv.ParseFloat(strings.TrimSuffix(t, "s"), 64)
			p["dur"] = d
		case t == "yoyo" || t == "loop" || t == "pingpong":
			p["loop"] = t
		case t == "stop":
			p["stop"] = true
		case strings.Contains(t, ":"):
			inlineKeys = append(inlineKeys, t)
		case op == "move":
			if cur, ok := p["path"].(string); ok {
				p["path"] = cur + " " + t
			} else {
				p["path"] = t
			}
		}
	}
	if len(inlineKeys) > 0 {
		p["keys"] = strings.Join(inlineKeys, " ")
	} else if len(bracket) > 0 {
		d := 1.0
		if dv, ok := numParam(p["dur"]); ok && dv > 0 {
			d = dv
		}
		n := len(bracket)
		parts := make([]string, n)
		for i, v := range bracket {
			t := 0.0
			if n > 1 {
				t = float64(i) / float64(n-1) * d
			}
			parts[i] = fmt.Sprintf("%g:%s", t, v)
		}
		p["keys"] = strings.Join(parts, " ")
	}
	return p, nil
}

// parsePathPoints turns "x,y x,y …" into a list of 2D control points.
func parsePathPoints(s string) ([][2]float64, error) {
	var pts [][2]float64
	for _, tok := range strings.Fields(s) {
		parts := strings.SplitN(tok, ",", 2)
		if len(parts) != 2 {
			return nil, fmt.Errorf("bad point %q (want x,y)", tok)
		}
		x, err := strconv.ParseFloat(strings.TrimSpace(parts[0]), 64)
		if err != nil {
			return nil, fmt.Errorf("bad x in %q", tok)
		}
		y, err := strconv.ParseFloat(strings.TrimSpace(parts[1]), 64)
		if err != nil {
			return nil, fmt.Errorf("bad y in %q", tok)
		}
		pts = append(pts, [2]float64{x, y})
	}
	if len(pts) < 2 {
		return nil, fmt.Errorf("path needs at least 2 points")
	}
	return pts, nil
}

// propIdentity is a property's rest value — the start a `to=` one-liner tweens
// FROM (transforms rest at 0; scale/alpha rest at 1).
func propIdentity(prop string) float64 {
	switch prop {
	case "scale", "scalex", "scaley", "alpha":
		return 1
	default:
		return 0
	}
}

// parseLoop reads the loop param, which is either a bool (true/false) or a word
// (once/restart/yoyo). Returns (loop, yoyo).
func parseLoop(v any) (bool, bool) {
	switch n := v.(type) {
	case bool:
		return n, false
	case string:
		switch n {
		case "yoyo", "pingpong":
			return true, true
		case "true", "restart", "loop":
			return true, false
		}
	}
	return false, false
}

// buildAnimCmd compiles an `anim`/`move` source line into a runtime "anim"
// command carrying an LvnAnim payload (loop/duration/tracks). `move` is sugar:
// a screen-space path becomes synced screen_x/screen_y tracks. Keeping both as
// one runtime op means the engine only learns a single new verb.
func buildAnimCmd(op string, p map[string]any) (Cmd, error) {
	id, _ := p["id"].(string)
	if id == "" {
		return nil, fmt.Errorf("%s: id required", op)
	}

	// Stop form: `anim id=x stop=all` (every script lane) or `stop=<channel/prop>`.
	// `stop=false` (bool) is NOT a stop — fall through to a normal animate.
	if sv, ok := p["stop"]; ok {
		if b, isBool := sv.(bool); !isBool || b {
			target := "all"
			if s, isStr := sv.(string); isStr && s != "" && s != "true" {
				target = s
			}
			return Cmd{"op": "anim", "id": id, "stop": target}, nil
		}
	}

	// `channel` is optional: when omitted the runtime derives one per animated
	// property (so rotation/scale/move run at once and compose, while re-animating
	// the same property replaces it). An explicit channel lets you group/override.
	channel, _ := p["channel"].(string)
	mode, _ := p["mode"].(string)
	loop, yoyo := parseLoop(p["loop"])
	ease, _ := p["ease"].(string)
	interp, _ := p["interp"].(string)
	dur, durSet := numParam(p["dur"])

	withShaping := func(tr map[string]any) map[string]any {
		if ease != "" {
			tr["ease"] = ease
		}
		if interp != "" {
			tr["interp"] = interp
		}
		return tr
	}

	var tracks []any
	var duration float64

	if op == "move" {
		d := dur
		if !durSet || d <= 0 {
			d = 1
		}
		var xs, ys []any
		if to, ok := p["to"].(string); ok && to != "" {
			// one-liner: glide from the current spot to a single point
			pt, err := parsePathPoints(to + " " + to) // reuse parser; take first
			if err != nil {
				return nil, fmt.Errorf("move: bad to=%q (want x,y)", to)
			}
			xs = []any{[]any{0.0, 0.0}, []any{d, pt[0][0]}}
			ys = []any{[]any{0.0, 0.0}, []any{d, pt[0][1]}}
		} else {
			pathStr, _ := p["path"].(string)
			pts, err := parsePathPoints(pathStr)
			if err != nil {
				return nil, fmt.Errorf("move: %w", err)
			}
			n := len(pts)
			for i, pt := range pts {
				t := 0.0
				if n > 1 {
					t = float64(i) / float64(n-1) * d
				}
				xs = append(xs, []any{t, pt[0]})
				ys = append(ys, []any{t, pt[1]})
			}
		}
		tracks = []any{
			withShaping(map[string]any{"prop": "screen_x", "keys": xs}),
			withShaping(map[string]any{"prop": "screen_y", "keys": ys}),
		}
		duration = d
		if orient, ok := p["orient"].(bool); ok && orient {
			// runtime reads this to rotate along the path tangent (phase 2)
			tracks[0].(map[string]any)["orient"] = true
		}
	} else { // anim
		prop, _ := p["prop"].(string)
		if prop == "" {
			return nil, fmt.Errorf("anim: prop required")
		}
		tr := map[string]any{"prop": prop}
		if to, hasTo := numParam(p["to"]); hasTo {
			// one-liner: tween from the property's rest value to the target
			d := dur
			if !durSet || d <= 0 {
				d = 1
			}
			tr["keys"] = []any{[]any{0.0, propIdentity(prop)}, []any{d, to}}
			duration = d
		} else {
			keysStr, _ := p["keys"].(string)
			keys, maxT, err := parseAnimKeys(keysStr)
			if err != nil {
				return nil, fmt.Errorf("anim: %w", err)
			}
			tr["keys"] = keys
			duration = maxT
			if durSet && dur > 0 {
				duration = dur
			}
		}
		if layer, _ := p["layer"].(string); layer != "" {
			tr["layer"] = layer
		}
		tracks = []any{withShaping(tr)}
	}

	anim := map[string]any{"loop": loop, "duration": duration, "tracks": tracks}
	if yoyo {
		anim["yoyo"] = true
	}
	cmd := Cmd{"op": "anim", "id": id, "anim": anim}
	if channel != "" {
		cmd["channel"] = channel
	}
	if mode != "" {
		cmd["mode"] = mode
	}
	return cmd, nil
}
