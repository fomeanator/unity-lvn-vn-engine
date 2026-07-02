package lvn

import (
	"encoding/json"
	"os"
	"path/filepath"
	"sort"
	"testing"
)

// The op contract has ONE source of truth: tools/lvn-lang/src/grammar.json
// (the editor's grammar.js is generated from it). This test pins the Go
// validator's hand-written tables to that file — add an op or an enum value on
// either side without the other and the suite goes red, which is the whole
// point: drift fails loudly instead of shipping a validator that lies.

type grammarJSON struct {
	Ops            []string                       `json:"ops"`
	StructuralOps  []string                       `json:"structural_ops"`
	OpFields       map[string][]string            `json:"op_fields"`
	ClosedFieldOps []string                       `json:"closed_field_ops"`
	Enums          map[string]map[string][]string `json:"enums"`
}

func loadGrammar(t *testing.T) grammarJSON {
	t.Helper()
	path := filepath.Join("..", "..", "lvn-lang", "src", "grammar.json")
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("grammar.json (the op contract) unreadable: %v", err)
	}
	var g grammarJSON
	if err := json.Unmarshal(data, &g); err != nil {
		t.Fatalf("grammar.json invalid: %v", err)
	}
	return g
}

func sorted(s []string) []string {
	out := append([]string(nil), s...)
	sort.Strings(out)
	return out
}

func TestKnownOpsMatchGrammar(t *testing.T) {
	g := loadGrammar(t)
	want := map[string]bool{}
	for _, op := range g.Ops {
		want[op] = true
	}
	for _, op := range g.StructuralOps {
		want[op] = true
	}
	for op := range want {
		if !KnownOps[op] {
			t.Errorf("grammar.json op %q missing from KnownOps", op)
		}
	}
	for op := range KnownOps {
		if !want[op] {
			t.Errorf("KnownOps has %q which grammar.json doesn't declare", op)
		}
	}
}

func TestOpFieldsMatchGrammarClosedSet(t *testing.T) {
	g := loadGrammar(t)
	closed := map[string]bool{}
	for _, op := range g.ClosedFieldOps {
		closed[op] = true
	}
	// Every closed op in the contract must be checked by the validator, with
	// exactly the contract's field set.
	for op := range closed {
		fields, ok := OpFields[op]
		if !ok {
			t.Errorf("closed op %q missing from validator OpFields", op)
			continue
		}
		want := sorted(g.OpFields[op])
		got := sorted(fields)
		if len(want) != len(got) {
			t.Errorf("op %q fields differ: grammar=%v validator=%v", op, want, got)
			continue
		}
		for i := range want {
			if want[i] != got[i] {
				t.Errorf("op %q fields differ: grammar=%v validator=%v", op, want, got)
				break
			}
		}
	}
	// And the validator must not strict-check any op the contract calls OPEN
	// (actor/obj/say/choice carry open-ended keys — checking them would
	// false-positive on catalog emotion axes).
	for op := range OpFields {
		if !closed[op] {
			t.Errorf("validator strict-checks %q but grammar.json lists it as open", op)
		}
	}
}

func TestEnumValuesMatchGrammar(t *testing.T) {
	g := loadGrammar(t)
	for op, fields := range g.Enums {
		vf, ok := EnumValues[op]
		if !ok {
			t.Errorf("grammar enum op %q missing from validator EnumValues", op)
			continue
		}
		for field, want := range fields {
			got, ok := vf[field]
			if !ok {
				t.Errorf("grammar enum %s.%s missing from validator", op, field)
				continue
			}
			w, gg := sorted(want), sorted(got)
			if len(w) != len(gg) {
				t.Errorf("enum %s.%s differs: grammar=%v validator=%v", op, field, want, got)
				continue
			}
			for i := range w {
				if w[i] != gg[i] {
					t.Errorf("enum %s.%s differs: grammar=%v validator=%v", op, field, want, got)
					break
				}
			}
		}
	}
	for op, fields := range EnumValues {
		for field := range fields {
			if g.Enums[op] == nil || g.Enums[op][field] == nil {
				t.Errorf("validator enum %s.%s not declared in grammar.json", op, field)
			}
		}
	}
}
