package lvn

import (
	"fmt"
	"sort"
)

// KnownOps is the registry of command ops the runtime understands. An op
// outside this set is a content error, not a silent no-op — the same hard
// rule the front-ends apply to staging tags, enforced here for any .lvn.
var KnownOps = map[string]bool{
	"say": true, "choice": true, "bg": true, "actor": true, "obj": true,
	"fade": true, "dim": true, "flash": true, "tint": true, "blur": true,
	"camera": true, "particles": true,
	"audio": true, "wait": true, "preload": true, "text_pace": true,
	"label": true, "goto": true, "if": true,
	"set": true, "inc": true, "hint": true,
	"call": true, "return": true,
}

// Builtin labels are resolved by the runtime and need no definition.
var builtinLabels = map[string]bool{"__end": true}

// Issue is a single validation finding.
type Issue struct {
	Index int    // command index in script, or -1 for document-level
	Op    string // op of the offending command, if any
	Msg   string
}

func (i Issue) String() string {
	if i.Index < 0 {
		return "doc: " + i.Msg
	}
	return fmt.Sprintf("script[%d] %s: %s", i.Index, i.Op, i.Msg)
}

// Validate runs the source-agnostic structural checks a build must pass:
//   - every op is known (unknown op = error, never a silent skip);
//   - no duplicate label ids;
//   - every jump target (goto/if/choice/call) resolves to a defined label;
//   - reports labels that are defined but never targeted (lint, not fatal).
//
// It returns the findings; callers decide which severities gate the build.
// Translation-completeness and stat-condition checks are content-policy
// concerns layered on top by the host game, not encoded here.
func Validate(d *Doc) []Issue {
	var issues []Issue

	// Pass 1: collect defined labels (detect duplicates).
	defined := map[string]bool{}
	for i, c := range d.Script {
		if c.Op() != "label" {
			continue
		}
		id := c.Str("id")
		if id == "" {
			issues = append(issues, Issue{i, "label", "label has no id"})
			continue
		}
		if defined[id] {
			issues = append(issues, Issue{i, "label", fmt.Sprintf("duplicate label %q", id)})
		}
		defined[id] = true
	}

	// Pass 2: walk commands, checking ops and jump targets.
	targeted := map[string]bool{}
	ref := func(i int, op, target string) {
		if target == "" {
			return
		}
		targeted[target] = true
		if !defined[target] && !builtinLabels[target] {
			issues = append(issues, Issue{i, op, fmt.Sprintf("jump to undefined label %q", target)})
		}
	}

	var walk func(i int, c Cmd)
	walk = func(i int, c Cmd) {
		op := c.Op()
		if op == "" {
			issues = append(issues, Issue{i, "", "command has no op"})
			return
		}
		if !KnownOps[op] {
			issues = append(issues, Issue{i, op, fmt.Sprintf("unknown op %q (typo?)", op)})
			return
		}
		switch op {
		case "goto", "call":
			ref(i, op, c.Str("label"))
		case "if":
			ref(i, op, c.Str("then"))
			ref(i, op, c.Str("else"))
		case "obj", "actor":
			// A clickable hotspot jumps to a label, either directly
			// ("on_click": "label") or via an object ("on_click": {"goto": "label"}).
			switch v := c["on_click"].(type) {
			case string:
				ref(i, op, v)
			case map[string]any:
				ref(i, op, Cmd(v).Str("goto"))
			}
		case "choice":
			opts, _ := c["options"].([]any)
			for _, o := range opts {
				om, ok := o.(map[string]any)
				if !ok {
					continue
				}
				oc := Cmd(om)
				ref(i, "choice", oc.Str("goto"))
				if body, ok := oc["body"].([]any); ok {
					for _, b := range body {
						if bm, ok := b.(map[string]any); ok {
							walk(i, Cmd(bm))
						}
					}
				}
			}
		}
	}
	for i, c := range d.Script {
		walk(i, c)
	}

	// Pass 3: lint — labels defined but never targeted. Fall-through reachable
	// labels are common and legitimate, so this is a warning, not an error.
	var unused []string
	for id := range defined {
		if !targeted[id] {
			unused = append(unused, id)
		}
	}
	sort.Strings(unused)
	for _, id := range unused {
		issues = append(issues, Issue{-1, "label", fmt.Sprintf("label %q is never targeted (dead, or fall-through only)", id)})
	}

	return issues
}
