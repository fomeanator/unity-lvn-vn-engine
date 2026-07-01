package importer

import (
	"bytes"
	"image/png"
	"os"
	"path/filepath"
	"testing"

	"github.com/fomeanator/unity-lvn-vn-engine/tools/lvnconv/internal/articy"
)

func writeFile(t *testing.T, dir, rel string, data []byte) {
	t.Helper()
	p := filepath.Join(dir, filepath.FromSlash(rel))
	if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(p, data, 0o644); err != nil {
		t.Fatal(err)
	}
}

// collectArt resolves the script's sprite_urls to files on disk: character art is
// matted and lands under art/, backgrounds are copied under bg/, the first
// background becomes the cover, and scenes with no art file are reported missing.
func TestCollectArtResolvesAndMattes(t *testing.T) {
	proj := t.TempDir()
	// Disk files carry articy's hash suffix; the script references the clean name.
	writeFile(t, proj, "Assets/Тимур_Обычный(00A9).png", makePNG(t, 40, 40))
	writeFile(t, proj, "Assets/Двор(BEEF).jpg", makePNG(t, 40, 40))

	doc := &articy.Doc{Script: []articy.Cmd{
		{"op": "actor", "id": "Тимур", "sprite_url": "/content/art/Тимур_Обычный.png"},
		{"op": "bg", "id": "Двор", "sprite_url": "/content/bg/Двор.jpg"},
		{"op": "bg", "id": "Парк", "sprite_url": "/content/bg/Парк.jpg"}, // no file → missing
		{"op": "say", "who": "Тимур", "text": "hi"},
	}}

	art, missing, firstBg := collectArt(buildAssetIndex(proj), doc, map[string][]byte{})

	byRel := map[string][]byte{}
	for _, a := range art {
		byRel[a.Rel] = a.Data
	}
	if _, ok := byRel["art/Тимур_Обычный.png"]; !ok {
		t.Fatalf("character art not resolved; got %v", keys(byRel))
	}
	if _, ok := byRel["bg/Двор.jpg"]; !ok {
		t.Fatalf("background not resolved; got %v", keys(byRel))
	}
	// the actor sprite must be matted (decodable PNG with a transparent corner)
	img, err := png.Decode(bytes.NewReader(byRel["art/Тимур_Обычный.png"]))
	if err != nil {
		t.Fatalf("matted art is not a valid PNG: %v", err)
	}
	if _, _, _, a := img.At(0, 0).RGBA(); a != 0 {
		t.Error("matted art corner should be transparent")
	}

	if firstBg != "/content/bg/Двор.jpg" {
		t.Errorf("cover = %q, want the first background", firstBg)
	}
	if len(missing) != 1 || missing[0] != "Парк" {
		t.Errorf("missing = %v, want [Парк]", missing)
	}
}

func TestLookupBgExactThenLoose(t *testing.T) {
	index := map[string]string{
		normKey("Двор_утро"): "/x/Двор_утро.jpg",
		normKey("Класс"):     "/x/Класс.jpg",
	}
	if p := lookupBg(index, "Класс"); p != "/x/Класс.jpg" {
		t.Errorf("exact lookup failed: %q", p)
	}
	// the disk file carries an extra qualifier; a loose substring match still hits
	if p := lookupBg(index, "Двор"); p != "/x/Двор_утро.jpg" {
		t.Errorf("loose lookup failed: %q", p)
	}
	if p := lookupBg(index, "Несуществующая"); p != "" {
		t.Errorf("absent location should miss, got %q", p)
	}
}

func keys[V any](m map[string]V) []string {
	out := make([]string, 0, len(m))
	for k := range m {
		out = append(out, k)
	}
	return out
}
