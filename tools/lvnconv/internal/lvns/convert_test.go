package lvns

import (
	"encoding/json"
	"reflect"
	"testing"
)

func TestConvert(t *testing.T) {
	src := `
scene test_chapter
actor_map Mara=mara_custom

// A background change
bg sprite_url="/content/bg/room.jpg"

:start
Rain ticked on the porch roof.
Mara: You came back.
Mara [smile]: Then come in out of the rain.

- I did. -> warmth_choice min=2 requires_stat="courage"
- I can't stay. -> leave cost="5 coins"

:warmth_choice
goto start

:leave
return
`

	doc, err := Convert(src)
	if err != nil {
		t.Fatalf("Convert failed: %v", err)
	}

	if doc.Scene != "test_chapter" {
		t.Errorf("expected scene to be 'test_chapter', got %q", doc.Scene)
	}

	expectedScript := []Cmd{
		{"op": "bg", "sprite_url": "/content/bg/room.jpg"},
		{"op": "label", "id": "start"},
		{"op": "say", "text": "Rain ticked on the porch roof."},
		{"op": "say", "who": "Mara", "text": "You came back."},
		{"op": "actor", "id": "mara_custom", "emotion": "smile"},
		{"op": "say", "who": "Mara", "text": "Then come in out of the rain."},
		{
			"op": "choice",
			"options": []any{
				map[string]any{"text": "I did.", "goto": "warmth_choice", "min": int64(2), "requires_stat": "courage"},
				map[string]any{"text": "I can't stay.", "goto": "leave", "cost": "5 coins"},
			},
		},
		{"op": "label", "id": "warmth_choice"},
		{"op": "goto", "label": "start"},
		{"op": "label", "id": "leave"},
		{"op": "return"},
	}

	if len(doc.Script) != len(expectedScript) {
		t.Fatalf("expected script length %d, got %d", len(expectedScript), len(doc.Script))
	}

	for i, cmd := range doc.Script {
		expected := expectedScript[i]
		// Marshal and unmarshal to normalize types for comparison (e.g. nested slices/maps)
		cmdJSON, _ := json.Marshal(cmd)
		expectedJSON, _ := json.Marshal(expected)
		var normCmd, normExpected map[string]any
		json.Unmarshal(cmdJSON, &normCmd)
		json.Unmarshal(expectedJSON, &normExpected)

		if !reflect.DeepEqual(normCmd, normExpected) {
			t.Errorf("at index %d:\nexpected: %+v\ngot:      %+v", i, normExpected, normCmd)
		}
	}
}
