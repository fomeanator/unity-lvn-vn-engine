package importer

import (
	"regexp"
	"strings"

	"github.com/fomeanator/unity-lvn-vn-engine/tools/lvnconv/internal/articy"
)

// Slug turns a display name into an id/filename token (spaces → underscores).
func Slug(s string) string { return strings.ReplaceAll(strings.TrimSpace(s), " ", "_") }

// Localize moves every dialogue/option string out of the script into a catalog
// and replaces it with a text_id reference, returning the catalog (key → string).
// The key is the line's reimport-stable id (the articy GUID the front-end stamped
// on the command); a line without one falls back to keying on its own text. The
// catalog is written beside the script as <chapter>.<lang>.json, which the runtime
// loads per locale — so translating a novel is just shipping more catalogs against
// the same keys, the flow/choices/logic untouched.
//
// Run AFTER AutoStage: staging reads inline say text (scene markers), which this
// pass removes.
func Localize(doc *articy.Doc) map[string]string {
	catalog := map[string]string{}
	// suffix namespaces a choice caption's key. A choice option and the spoken
	// line share the SAME fragment stable id (the option's caption is the target
	// fragment's MenuText; the walked target is emitted as a say with its Text) —
	// so without a distinct suffix they collide on one catalog key and the button
	// ends up showing the full spoken line. "#opt" keeps them separate.
	move := func(m map[string]any, suffix string) {
		text, _ := m["text"].(string)
		if text == "" {
			return
		}
		key, _ := m["id"].(string)
		if key == "" {
			key = text // no stable id available — the text is its own key
		}
		key += suffix
		catalog[key] = text
		m["text_id"] = key
		delete(m, "text")
		delete(m, "id")
	}
	for _, c := range doc.Script {
		switch c["op"] {
		case "say":
			move(c, "")
		case "choice":
			if opts, ok := c["options"].([]any); ok {
				for _, o := range opts {
					if m, ok := o.(articy.Cmd); ok {
						move(m, "#opt")
					}
				}
			}
		}
	}
	return catalog
}

// StripStableIds removes the reimport-stable line ids the front-end stamps on
// says/options. They are only needed as localization keys; when a chapter is not
// localized they would just bloat the .lvn, so the non-localized pipeline drops
// them.
func StripStableIds(doc *articy.Doc) {
	for _, c := range doc.Script {
		switch c["op"] {
		case "say":
			delete(c, "id")
		case "choice":
			if opts, ok := c["options"].([]any); ok {
				for _, o := range opts {
					if m, ok := o.(articy.Cmd); ok {
						delete(m, "id")
					}
				}
			}
		}
	}
}

// ── auto-staging ─────────────────────────────────────────────────────────────

var sceneMarkerRe = regexp.MustCompile(`^\s*Сцена\s+\d+\.\s*(.+?)\.?\s*$`)

// narratorRoles never get an on-stage sprite — the narrator, the player-choice
// echo, and articy bookkeeping "speakers". A line from any of these clears the
// stage (mobile convention: narration shows no one).
var narratorRoles = map[string]bool{
	"Автор": true, "Игрок": true, "Выбор пути": true, "Информация": true,
	"Эпизод": true, "Отношения": true, "Туториал": true, "Смена ГГ": true,
}

// protagonistRoles are the player character — shown on the LEFT when they have a
// sprite (the usual mobile framing: hero left, everyone else right). In a first-
// person novel the protagonist has no sprite, so their lines just clear the stage.
var protagonistRoles = map[string]bool{"Главный герой": true, "ГГ": true}

// AutoStage enriches a dialogue script with a first pass of staging by rule.
//
// Mobile single-speaker model: only the CURRENT speaker is on screen, centred.
// A character with art speaks → they fade in (and whoever was there fades out);
// the narrator ("Автор") or any speaker with no sprite → the stage clears. A
// scene-marker line ("Сцена N. <Location>") becomes a `bg` and clears the stage.
// The result is regular .lvn ops the author can refine in the editor.
func AutoStage(doc *articy.Doc, cast map[string]string) {
	out := make([]articy.Cmd, 0, len(doc.Script))
	current := "" // the single character on screen ("" = empty stage)
	hide := func() {
		if current != "" {
			out = append(out, articy.Cmd{"op": "actor", "id": Slug(current), "show": false, "exit": "fade"})
			current = ""
		}
	}
	for _, c := range doc.Script {
		if c["op"] == "say" {
			text, _ := c["text"].(string)
			who, _ := c["who"].(string)
			if m := sceneMarkerRe.FindStringSubmatch(text); m != nil {
				hide()
				loc := Slug(m[1])
				out = append(out, articy.Cmd{"op": "bg", "id": loc, "sprite_url": "/content/bg/" + loc + ".jpg"})
				continue // a scene marker is a background, not a spoken line
			}
			if spr, ok := cast[who]; ok && !narratorRoles[who] {
				// a character with a sprite speaks → show ONLY them: the hero on the
				// left, everyone else on the right (the usual mobile framing).
				if who != current {
					hide() // swap out whoever was there
					side := "right"
					if protagonistRoles[who] {
						side = "left"
					}
					out = append(out, articy.Cmd{"op": "actor", "id": Slug(who), "show": true,
						"position": side, "sprite_url": "/content/art/" + spr, "enter": "fade"})
					current = who
				}
			} else {
				hide() // narrator / off-screen voice → clear the stage
			}
		}
		out = append(out, c)
	}
	doc.Script = out
}

// ── filename matching (disk ↔ .lvn) ──────────────────────────────────────────

// hashSuffixRe strips articy's "(ABCD)" hex tag from an exported asset filename:
// "Тимур_Обычный(00A9)" → "Тимур_Обычный".
var hashSuffixRe = regexp.MustCompile(`\([0-9A-Fa-f]+\)$`)

func stripHash(base string) string { return hashSuffixRe.ReplaceAllString(base, "") }

// normKey folds a name to a match key that is stable across Unicode forms and
// case. macOS stores filenames decomposed (NFD) while the .adpd strings are
// composed (NFC); in Cyrillic only й and ё differ between the two. We canonicalise
// by decomposing those precomposed letters and dropping combining marks, so an
// NFC "Тимур_Обычный" and an NFD one collapse to the same key.
func normKey(s string) string {
	var b strings.Builder
	for _, r := range s {
		switch r {
		case 'й':
			r = 'и'
		case 'Й':
			r = 'И'
		case 'ё':
			r = 'е'
		case 'Ё':
			r = 'Е'
		}
		if r >= 0x0300 && r <= 0x036F { // combining diacritical marks
			continue
		}
		b.WriteRune(r)
	}
	return strings.ToLower(strings.TrimSpace(b.String()))
}
