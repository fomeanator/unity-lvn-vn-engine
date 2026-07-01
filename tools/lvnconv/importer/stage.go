package importer

import (
	"regexp"
	"sort"
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
	move := func(m map[string]any) {
		text, _ := m["text"].(string)
		if text == "" {
			return
		}
		key, _ := m["id"].(string)
		if key == "" {
			key = text // no stable id available — the text is its own key
		}
		catalog[key] = text
		m["text_id"] = key
		delete(m, "text")
		delete(m, "id")
	}
	for _, c := range doc.Script {
		switch c["op"] {
		case "say":
			move(c)
		case "choice":
			if opts, ok := c["options"].([]any); ok {
				for _, o := range opts {
					if m, ok := o.(articy.Cmd); ok {
						move(m)
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

// narrativeRoles never get an on-stage sprite: the narrator, the first-person
// protagonist, the player-choice voice, and articy bookkeeping "speakers".
var narrativeRoles = map[string]bool{
	"Автор": true, "Игрок": true, "Главный герой": true, "ГГ": true,
	"Выбор пути": true, "Информация": true, "Эпизод": true, "Отношения": true,
	"Туториал": true, "Смена ГГ": true,
}

// maxOnStage caps how many characters stand at once. Auto-import can't know a
// scene's real cast, so "keep everyone who ever spoke" piles 4–5 sprites up; a
// two-shot (the current conversational pair) is the clean classic default — when a
// third speaks, the one silent longest fades out. Runtime dims the non-speaker.
const maxOnStage = 2

// AutoStage enriches a dialogue script with a first pass of staging by rule: a
// scene-marker line ("Сцена N. <Location>") becomes a `bg` and clears the stage;
// a character with art enters (fading in) at a free side when they speak, capped
// at a two-shot. The result is regular .lvn ops the author can refine in the editor.
func AutoStage(doc *articy.Doc, cast map[string]string) {
	out := make([]articy.Cmd, 0, len(doc.Script))
	type slot struct{ name, pos string }
	var stage []slot // ordered oldest→newest by last time they spoke
	sideFree := map[string]bool{"left": true, "right": true}
	freeSide := func() string {
		if sideFree["left"] {
			return "left"
		}
		return "right"
	}
	hide := func(s slot) {
		out = append(out, articy.Cmd{"op": "actor", "id": Slug(s.name), "show": false, "exit": "fade"})
		sideFree[s.pos] = true
	}
	clear := func() {
		names := append([]slot{}, stage...)
		sort.Slice(names, func(i, j int) bool { return names[i].name < names[j].name })
		for _, s := range names {
			hide(s)
		}
		stage = nil
		sideFree = map[string]bool{"left": true, "right": true}
	}
	indexOf := func(name string) int {
		for i, s := range stage {
			if s.name == name {
				return i
			}
		}
		return -1
	}
	for _, c := range doc.Script {
		if c["op"] == "say" {
			text, _ := c["text"].(string)
			who, _ := c["who"].(string)
			if m := sceneMarkerRe.FindStringSubmatch(text); m != nil {
				clear()
				loc := Slug(m[1])
				out = append(out, articy.Cmd{"op": "bg", "id": loc, "sprite_url": "/content/bg/" + loc + ".jpg"})
				continue // a scene marker is a background, not a spoken line
			}
			if spr, ok := cast[who]; ok && !narrativeRoles[who] {
				if i := indexOf(who); i >= 0 {
					// already on stage — just mark them most-recent (so eviction
					// later drops whoever's been quiet longest, not the active pair).
					s := stage[i]
					stage = append(append(stage[:i:i], stage[i+1:]...), s)
				} else {
					if len(stage) >= maxOnStage {
						hide(stage[0]) // evict the longest-silent
						stage = stage[1:]
					}
					pos := freeSide()
					sideFree[pos] = false
					out = append(out, articy.Cmd{"op": "actor", "id": Slug(who), "show": true,
						"position": pos, "sprite_url": "/content/art/" + spr, "enter": "fade"})
					stage = append(stage, slot{name: who, pos: pos})
				}
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
