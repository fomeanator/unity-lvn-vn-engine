package importer

import (
	"testing"

	"github.com/fomeanator/unity-lvn-vn-engine/tools/lvnconv/internal/articy"
)

func ops(s []articy.Cmd) []string {
	out := make([]string, len(s))
	for i, c := range s {
		out[i], _ = c["op"].(string)
	}
	return out
}

func countOp(s []articy.Cmd, op string) int {
	n := 0
	for _, c := range s {
		if c["op"] == op {
			n++
		}
	}
	return n
}

func TestSlug(t *testing.T) {
	cases := map[string]string{
		"Тимур":         "Тимур",
		"Главный герой": "Главный_герой",
		"  padded  ":    "padded",
		"a b c":         "a_b_c",
	}
	for in, want := range cases {
		if got := Slug(in); got != want {
			t.Errorf("Slug(%q) = %q, want %q", in, got, want)
		}
	}
}

// AutoStage turns a scene marker into a bg (dropping the marker line) and drives
// the mobile single-speaker stage: only the current speaker is shown; a narrator
// or a scene change clears it; a speaker already shown isn't re-emitted.
func TestAutoStageScenesAndActors(t *testing.T) {
	doc := &articy.Doc{Script: []articy.Cmd{
		{"op": "say", "text": "Сцена 1. Двор."},
		{"op": "say", "who": "Тимур", "text": "Привет"},
		{"op": "say", "who": "Игрок", "text": "..."},   // narrator role → clears stage
		{"op": "say", "who": "Тимур", "text": "Снова"}, // stage was cleared → re-enter
		{"op": "say", "text": "Сцена 2. Парк."},
		{"op": "say", "who": "Тимур", "text": "В парке"},
	}}
	cast := map[string]string{"Тимур": "Тимур_Обычный.png", "Игрок": "Игрок.png"}

	AutoStage(doc, cast)
	got := ops(doc.Script)
	// show → (narrator) hide → show → (scene) hide → show
	want := []string{"bg", "actor", "say", "actor", "say", "actor", "say", "actor", "bg", "actor", "say"}
	if len(got) != len(want) {
		t.Fatalf("op sequence = %v, want %v", got, want)
	}
	for i := range want {
		if got[i] != want[i] {
			t.Fatalf("op[%d] = %q, want %q (full %v)", i, got[i], want[i], got)
		}
	}

	// scene markers became backgrounds and the spoken marker lines were dropped
	if n := countOp(doc.Script, "bg"); n != 2 {
		t.Fatalf("want 2 bg, got %d", n)
	}
	if doc.Script[0]["id"] != "Двор" || doc.Script[0]["sprite_url"] != "/content/bg/Двор.jpg" {
		t.Errorf("first bg = %v", doc.Script[0])
	}
	for _, c := range doc.Script {
		if c["op"] == "say" {
			if txt, _ := c["text"].(string); txt == "Сцена 1. Двор." || txt == "Сцена 2. Парк." {
				t.Errorf("scene marker survived as a say line: %q", txt)
			}
		}
	}

	// Тимур is shown 3× (right side, non-protagonist) and cleared 2× (narrator +
	// scene change); Игрок (a narrator role) is never staged.
	shows, hides := 0, 0
	for _, c := range doc.Script {
		if c["op"] != "actor" {
			continue
		}
		if c["id"] == "Игрок" {
			t.Error("a narrator role was walked on stage")
		}
		if c["show"] == true {
			shows++
			if c["position"] != "right" {
				t.Errorf("a non-protagonist should stand right, got %v", c["position"])
			}
			if c["sprite_url"] != "/content/art/Тимур_Обычный.png" {
				t.Errorf("actor sprite_url = %v", c["sprite_url"])
			}
		} else {
			hides++
		}
	}
	if shows != 3 || hides != 2 {
		t.Errorf("actor shows=%d hides=%d, want 3/2", shows, hides)
	}
}

// Single-speaker: a new speaker replaces the previous one (only one on stage at a
// time), and non-protagonists stand on the right.
func TestAutoStageSingleSpeakerSwap(t *testing.T) {
	doc := &articy.Doc{Script: []articy.Cmd{
		{"op": "say", "text": "Сцена 1. Класс."},
		{"op": "say", "who": "Тимур", "text": "a"},
		{"op": "say", "who": "Люба", "text": "b"},
	}}
	cast := map[string]string{"Тимур": "t.png", "Люба": "l.png"}
	AutoStage(doc, cast)

	// Люба's turn must first hide Тимур, then show Люба — never two up at once.
	on := map[string]bool{}
	maxOn := 0
	var shownPos []string
	for _, c := range doc.Script {
		if c["op"] != "actor" {
			continue
		}
		if c["show"] == true {
			on[c["id"].(string)] = true
			shownPos = append(shownPos, c["position"].(string))
		} else {
			delete(on, c["id"].(string))
		}
		if len(on) > maxOn {
			maxOn = len(on)
		}
	}
	if maxOn != 1 {
		t.Fatalf("single-speaker violated: %d actors on stage at once", maxOn)
	}
	if len(shownPos) != 2 || shownPos[0] != "right" || shownPos[1] != "right" {
		t.Fatalf("positions = %v, want [right right]", shownPos)
	}
}

// The protagonist stands on the LEFT (mobile framing) when they have a sprite.
func TestAutoStageProtagonistLeft(t *testing.T) {
	doc := &articy.Doc{Script: []articy.Cmd{
		{"op": "say", "who": "Главный герой", "text": "я"},
		{"op": "say", "who": "Тимур", "text": "он"},
	}}
	cast := map[string]string{"Главный герой": "hero.png", "Тимур": "t.png"}
	AutoStage(doc, cast)

	var pos []string
	for _, c := range doc.Script {
		if c["op"] == "actor" && c["show"] == true {
			pos = append(pos, c["position"].(string))
		}
	}
	if len(pos) != 2 || pos[0] != "left" || pos[1] != "right" {
		t.Fatalf("positions = %v, want [left right] (hero left, other right)", pos)
	}
}

// Localize moves each line's text into the catalog keyed by its stable id, leaving
// a text_id reference; a line without an id falls back to keying on its text.
func TestLocalizeExtractsCatalogByStableId(t *testing.T) {
	doc := &articy.Doc{Script: []articy.Cmd{
		{"op": "say", "who": "Тимур", "text": "Привет", "id": "g-1"},
		{"op": "say", "text": "Безымянная строка"}, // no id → keyed by its own text
		{"op": "choice", "options": []any{
			articy.Cmd{"goto": "a", "text": "Вариант А", "id": "g-2"},
			articy.Cmd{"goto": "b", "text": "Вариант Б", "id": "g-3"},
		}},
	}}

	cat := Localize(doc)

	// Choice captions are namespaced with "#opt" so they never collide with a
	// spoken line that shares the same fragment stable id.
	if cat["g-1"] != "Привет" || cat["g-2#opt"] != "Вариант А" || cat["g-3#opt"] != "Вариант Б" {
		t.Fatalf("catalog by stable id wrong: %v", cat)
	}
	if cat["Безымянная строка"] != "Безымянная строка" {
		t.Errorf("id-less line should key on its text: %v", cat)
	}
	say := doc.Script[0]
	if say["text_id"] != "g-1" || say["text"] != nil || say["id"] != nil {
		t.Errorf("say not rewritten to text_id: %v", say)
	}
	opt := doc.Script[2]["options"].([]any)[0].(articy.Cmd)
	if opt["text_id"] != "g-2#opt" || opt["text"] != nil {
		t.Errorf("option not rewritten to text_id: %v", opt)
	}
	if opt["goto"] != "a" {
		t.Errorf("option goto must be preserved: %v", opt)
	}
}

// A choice option's caption and the spoken line of the SAME fragment share a
// stable id. Localization must keep them as two distinct catalog entries, or the
// button ends up showing the full spoken line instead of the short caption.
func TestLocalizeChoiceCaptionDoesNotCollideWithLine(t *testing.T) {
	doc := &articy.Doc{Script: []articy.Cmd{
		{"op": "choice", "options": []any{
			articy.Cmd{"goto": "frag", "text": "Спросить о городе", "id": "frag"},
		}},
		{"op": "label", "id": "frag"},
		{"op": "say", "who": "Тимур", "text": "Это долгая история о нашем городе…", "id": "frag"},
	}}

	cat := Localize(doc)

	if cat["frag"] != "Это долгая история о нашем городе…" {
		t.Fatalf("spoken line lost its full text: %v", cat)
	}
	if cat["frag#opt"] != "Спросить о городе" {
		t.Fatalf("caption collided with the line: %v", cat)
	}
	opt := doc.Script[0]["options"].([]any)[0].(articy.Cmd)
	if opt["text_id"] != "frag#opt" {
		t.Errorf("option must reference the caption key: %v", opt)
	}
	say := doc.Script[2]
	if say["text_id"] != "frag" {
		t.Errorf("say must reference the line key: %v", say)
	}
}

// The two passes compose: AutoStage reads inline text (scene markers → bg) and
// Localize then extracts what remains — the ordering bug where localized text hid
// the scene markers from staging is gone.
func TestAutoStageThenLocalizeCompose(t *testing.T) {
	doc := &articy.Doc{Script: []articy.Cmd{
		{"op": "say", "text": "Сцена 1. Двор.", "id": "g-scene"},
		{"op": "say", "who": "Тимур", "text": "Привет", "id": "g-1"},
	}}
	cast := map[string]string{"Тимур": "t.png"}

	AutoStage(doc, cast)
	if countOp(doc.Script, "bg") != 1 {
		t.Fatalf("scene marker did not become a bg: %v", ops(doc.Script))
	}
	cat := Localize(doc)

	if cat["g-1"] != "Привет" {
		t.Errorf("dialogue not localized after staging: %v", cat)
	}
	for _, c := range doc.Script {
		if c["op"] == "say" && c["text_id"] == nil {
			t.Errorf("a say survived without a text_id: %v", c)
		}
	}
}

// A speaker with no art in the cast never gets an actor command.
func TestAutoStageUnknownSpeakerNoActor(t *testing.T) {
	doc := &articy.Doc{Script: []articy.Cmd{
		{"op": "say", "who": "Незнакомец", "text": "hi"},
	}}
	AutoStage(doc, map[string]string{})
	if countOp(doc.Script, "actor") != 0 {
		t.Error("unknown speaker should not be staged")
	}
}
