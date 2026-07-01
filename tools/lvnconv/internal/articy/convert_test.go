package articy

import (
	"encoding/json"
	"os"
	"strings"
	"testing"
)

func load(t *testing.T) *Doc {
	t.Helper()
	src, err := os.ReadFile("testdata/sample-export.json")
	if err != nil {
		t.Fatal(err)
	}
	d, err := Convert(src, "")
	if err != nil {
		t.Fatalf("Convert: %v", err)
	}
	return d
}

func js(t *testing.T, v any) string {
	t.Helper()
	b, err := json.Marshal(v)
	if err != nil {
		t.Fatal(err)
	}
	return string(b)
}

func TestLabelGraphIsIntact(t *testing.T) {
	d := load(t)
	labels := map[string]bool{}
	for _, c := range d.Script {
		if c["op"] == "label" {
			labels[c["id"].(string)] = true
		}
	}
	check := func(v any, where string) {
		s, _ := v.(string)
		if s != "" && !labels[s] {
			t.Errorf("%s → missing label %q; script=%s", where, s, js(t, d.Script))
		}
	}
	for _, c := range d.Script {
		switch c["op"] {
		case "goto", "call":
			check(c["label"], "goto/call")
		case "if":
			check(c["then"], "if.then")
			check(c["else"], "if.else")
		case "choice":
			for _, o := range c["options"].([]any) {
				check(o.(Cmd)["goto"], "option")
			}
		}
	}
	if d.Scene != "ch_test" {
		t.Errorf("scene = %q, want ch_test", d.Scene)
	}
}

func TestGlobalVarsInitialiseFirst(t *testing.T) {
	d := load(t)
	// Global-var inits are DEFAULTS (default:true) so a value carried in from an
	// earlier chapter / a save isn't reset to zero.
	if js(t, d.Script[0]) != `{"default":true,"key":"vars.courage","op":"set","value":0}` {
		t.Fatalf("vars init wrong: %s", js(t, d.Script[0]))
	}
	if js(t, d.Script[1]) != `{"default":true,"key":"vars.met","op":"set","value":false}` {
		t.Fatalf("vars init wrong: %s", js(t, d.Script[1]))
	}
}

func TestFragment_SpeakerStagingAndStyle(t *testing.T) {
	d := load(t)
	// stage directions emit before the say; # style: merges into the say
	var bgIdx, sayIdx int = -1, -1
	for i, c := range d.Script {
		if c["op"] == "bg" && bgIdx < 0 {
			bgIdx = i
		}
		if c["op"] == "say" && sayIdx < 0 {
			sayIdx = i
		}
	}
	if bgIdx < 0 || sayIdx < 0 || bgIdx > sayIdx {
		t.Fatalf("bg must precede first say: bg=%d say=%d", bgIdx, sayIdx)
	}
	say := d.Script[sayIdx]
	if say["who"] != "Луна" || say["style"] != "whisper" {
		t.Fatalf("say wrong: %s", js(t, say))
	}
}

func TestChoice_MenuTextCostAndGate(t *testing.T) {
	d := load(t)
	var choice Cmd
	for _, c := range d.Script {
		if c["op"] == "choice" {
			choice = c
		}
	}
	if choice == nil {
		t.Fatalf("no choice emitted: %s", js(t, d.Script))
	}
	opts := choice["options"].([]any)
	o0 := opts[0].(Cmd)
	cost := o0["cost"].(map[string]any)
	if o0["text"] != "Сказать правду" || cost["amount"] != int64(10) {
		t.Fatalf("opt0 wrong: %s", js(t, o0))
	}
	if o0["expr"] != "vars.courage >= 0" {
		t.Fatalf("opt0 gate wrong: %s", js(t, o0))
	}
	if opts[1].(Cmd)["text"] != "Промолчать" {
		t.Fatalf("opt1 wrong: %s", js(t, opts[1]))
	}
}

func TestInstructionAndPinScripts(t *testing.T) {
	d := load(t)
	src := js(t, d.Script)
	// instruction node → inc
	if !strings.Contains(src, `{"by":1,"key":"vars.courage","op":"inc"}`) {
		t.Fatalf("instruction inc missing: %s", src)
	}
	// choice branch output-pin script → set
	if !strings.Contains(src, `{"key":"vars.met","op":"set","value":true}`) {
		t.Fatalf("pin script set missing: %s", src)
	}
}

func TestConditionAndJump(t *testing.T) {
	d := load(t)
	var iff Cmd
	for _, c := range d.Script {
		if c["op"] == "if" {
			iff = c
		}
	}
	if iff == nil || iff["expr"] != "vars.courage >= 1 && vars.met" {
		t.Fatalf("condition expr wrong: %s", js(t, iff))
	}
	// the finale fragment is reached twice (condition-true + jump) — must be
	// emitted ONCE with a label, second arrival becomes goto
	src := js(t, d.Script)
	if strings.Count(src, "Идём. Дальше — вместе.") != 1 {
		t.Fatalf("finale must be emitted once: %s", src)
	}
	// hub got its TechnicalName as label
	if !strings.Contains(src, `{"id":"after_answer","op":"label"}`) {
		t.Fatalf("hub label missing: %s", src)
	}
	// dialogue exit → __end label present
	if !strings.Contains(src, `{"id":"__end","op":"label"}`) {
		t.Fatalf("__end missing: %s", src)
	}
}

func TestErrors(t *testing.T) {
	src, _ := os.ReadFile("testdata/sample-export.json")
	if _, err := Convert(src, "нет-такого"); err == nil || !strings.Contains(err.Error(), "not found") {
		t.Fatalf("want dialogue-not-found error, got %v", err)
	}
	if _, err := Convert([]byte("{не json"), ""); err == nil {
		t.Fatal("want json error")
	}
}
