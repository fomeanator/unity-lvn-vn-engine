package optimize

import (
	"image"
	"image/color"
	"image/png"
	"math/rand"
	"os"
	"path/filepath"
	"testing"
)

// A flat 2-colour image: PNG (RLE/palette-friendly) crushes this far smaller
// than JPEG's block DCT ever could — the exact shape of the ui/hotspot icons
// that regressed (grew 2–3×) before the "keep whichever candidate is
// smallest" comparison was added. Guards that regression.
func flatImage(w, h int) image.Image {
	img := image.NewNRGBA(image.Rect(0, 0, w, h))
	for y := 0; y < h; y++ {
		for x := 0; x < w; x++ {
			c := color.NRGBA{R: 20, G: 20, B: 20, A: 255}
			if x > w/2 {
				c = color.NRGBA{R: 200, G: 200, B: 200, A: 255}
			}
			img.SetNRGBA(x, y, c)
		}
	}
	return img
}

// Photographic-ish noise: JPEG legitimately wins here, unlike flatImage.
func noiseImage(w, h int) image.Image {
	img := image.NewNRGBA(image.Rect(0, 0, w, h))
	rng := rand.New(rand.NewSource(1))
	for y := 0; y < h; y++ {
		for x := 0; x < w; x++ {
			img.SetNRGBA(x, y, color.NRGBA{
				R: uint8(rng.Intn(256)), G: uint8(rng.Intn(256)), B: uint8(rng.Intn(256)), A: 255,
			})
		}
	}
	return img
}

func imageWithAlphaHole(w, h int) image.Image {
	img := image.NewNRGBA(image.Rect(0, 0, w, h))
	for y := 0; y < h; y++ {
		for x := 0; x < w; x++ {
			a := uint8(255)
			if x < w/4 && y < h/4 {
				a = 0 // a real transparent region — must stay PNG
			}
			img.SetNRGBA(x, y, color.NRGBA{R: 100, G: 150, B: 200, A: a})
		}
	}
	return img
}

func writePNG(t *testing.T, path string, img image.Image) {
	t.Helper()
	f, err := os.Create(path)
	if err != nil {
		t.Fatal(err)
	}
	defer f.Close()
	if err := png.Encode(f, img); err != nil {
		t.Fatal(err)
	}
}

func TestHasRealAlpha(t *testing.T) {
	if hasRealAlpha(flatImage(8, 8)) {
		t.Error("fully opaque NRGBA reported as having real alpha")
	}
	if !hasRealAlpha(imageWithAlphaHole(8, 8)) {
		t.Error("image with a transparent region not detected as having real alpha")
	}
}

func TestRun_FlatIconNeverGrowsUnderJPEG(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "icon.png")
	writePNG(t, path, flatImage(32, 32))
	before, _ := os.Stat(path)

	results, err := Run(dir, Options{Apply: true})
	if err != nil {
		t.Fatal(err)
	}
	if len(results) != 1 {
		t.Fatalf("expected 1 result, got %d", len(results))
	}
	r := results[0]
	if r.Err != nil {
		t.Fatal(r.Err)
	}
	// Never regress: the written file must not be larger than the original,
	// and it must still be a .png (JPEG would have grown this flat image).
	if _, err := os.Stat(path); err != nil {
		t.Fatalf("icon.png should still exist (PNG must win here): %v", err)
	}
	if r.Action == ActionToJPEG {
		t.Errorf("a flat 2-colour icon converted to JPEG (the exact regression this guards against)")
	}
	if r.NewBytes > before.Size() {
		t.Errorf("optimized size %d > original %d — grew a file", r.NewBytes, before.Size())
	}
}

func TestRun_NoisyOpaqueImageConvertsToJPEG(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "photo.png")
	writePNG(t, path, noiseImage(200, 200))

	results, err := Run(dir, Options{Apply: true})
	if err != nil {
		t.Fatal(err)
	}
	r := results[0]
	if r.Err != nil {
		t.Fatal(r.Err)
	}
	if r.Action != ActionToJPEG {
		t.Errorf("expected a noisy opaque image to win as JPEG, got action=%s", r.Action)
	}
	if _, _, ok := r.Rename(); !ok {
		t.Error("ToJPEG result should report a rename (png path → jpg path)")
	}
}

func TestRun_AlphaImageNeverBecomesJPEG(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "cutout.png")
	writePNG(t, path, imageWithAlphaHole(200, 200))

	results, err := Run(dir, Options{Apply: false}) // dry run — must not write
	if err != nil {
		t.Fatal(err)
	}
	r := results[0]
	if r.Err != nil {
		t.Fatal(r.Err)
	}
	if r.Action == ActionToJPEG {
		t.Error("an image with a real transparent region must never convert to JPEG")
	}
	if _, err := os.Stat(path); err != nil {
		t.Errorf("dry run must not touch the original file: %v", err)
	}
}

func TestRun_AtlasPageNeverResizedOrConverted(t *testing.T) {
	dir := t.TempDir()
	pagePath := filepath.Join(dir, "Hero.png")
	writePNG(t, pagePath, imageWithAlphaHole(4000, 100)) // oversized on the long side
	atlas := "Hero.png\nsize:4000,100\nfilter:Linear,Linear\nregion\n  bounds:0,0,4000,100\n"
	if err := os.WriteFile(filepath.Join(dir, "hero.atlas.txt"), []byte(atlas), 0o644); err != nil {
		t.Fatal(err)
	}

	results, err := Run(dir, Options{MaxSize: 2560, Apply: true})
	if err != nil {
		t.Fatal(err)
	}
	var got Result
	for _, r := range results {
		if r.Path == pagePath {
			got = r
		}
	}
	if got.Kind != KindAtlasPage {
		t.Fatalf("Hero.png should classify as an atlas page, got %s", got.Kind)
	}
	if got.NewW != got.OldW || got.NewH != got.OldH {
		t.Errorf("atlas page dimensions changed: %dx%d → %dx%d (must never resize a packed atlas)",
			got.OldW, got.OldH, got.NewW, got.NewH)
	}
	if _, _, ok := got.Rename(); ok {
		t.Error("an atlas page must never change extension/format")
	}
}

func TestResizeToFitPreservesAspect(t *testing.T) {
	src := flatImage(8000, 4000)
	out := resizeToFit(src, 2560)
	b := out.Bounds()
	if b.Dx() != 2560 {
		t.Errorf("expected longest side 2560, got %d", b.Dx())
	}
	wantH := 1280 // 4000 * (2560/8000)
	if d := b.Dy() - wantH; d < -1 || d > 1 {
		t.Errorf("aspect not preserved: want height ~%d, got %d", wantH, b.Dy())
	}
}

func TestAtlasPageNamesBothExtensions(t *testing.T) {
	dir := t.TempDir()
	os.WriteFile(filepath.Join(dir, "a.atlas.txt"), []byte("PageA.png\nsize:10,10\n"), 0o644)
	os.WriteFile(filepath.Join(dir, "b.atlas"), []byte("PageB.png\nsize:10,10\n"), 0o644)

	pages, err := atlasPageNames(dir)
	if err != nil {
		t.Fatal(err)
	}
	if !pages["PageA.png"] {
		t.Error("page declared in a .atlas.txt file not detected")
	}
	if !pages["PageB.png"] {
		t.Error("page declared in a plain .atlas file (no .txt suffix) not detected")
	}
}

func TestURLForAndRewriteRefs(t *testing.T) {
	root := t.TempDir()
	os.MkdirAll(filepath.Join(root, "art"), 0o755)
	os.MkdirAll(filepath.Join(root, "scripts"), 0o755)

	oldPath := filepath.Join(root, "art", "Foo.png")
	newPath := filepath.Join(root, "art", "Foo.jpg")
	url, ok := URLFor(root, oldPath)
	if !ok || url != "/content/art/Foo.png" {
		t.Fatalf("URLFor got (%q, %v)", url, ok)
	}
	newURL, _ := URLFor(root, newPath)

	manifest := filepath.Join(root, "manifest.json")
	os.WriteFile(manifest, []byte(`{"bg":"/content/art/Foo.png"}`), 0o644)
	script := filepath.Join(root, "scripts", "demo.lvns")
	os.WriteFile(script, []byte(`bg sprite_url="/content/art/Foo.png"`), 0o644)

	touched, err := RewriteRefs(root, map[string]string{url: newURL})
	if err != nil {
		t.Fatal(err)
	}
	if len(touched) != 2 {
		t.Fatalf("expected 2 touched files, got %d: %v", len(touched), touched)
	}
	mdata, _ := os.ReadFile(manifest)
	if string(mdata) != `{"bg":"/content/art/Foo.jpg"}` {
		t.Errorf("manifest not rewritten: %s", mdata)
	}
	sdata, _ := os.ReadFile(script)
	if string(sdata) != `bg sprite_url="/content/art/Foo.jpg"` {
		t.Errorf("script not rewritten: %s", sdata)
	}
}

// -lossless: сырой (несжатый) исходник художника пережимается в разы, но
// НИЧЕГО другого с ним не происходит — ни уменьшения до бокса, ни png→jpg,
// хотя по обычным правилам этому файлу положено и то и другое. Пиксели
// сверяются декодом обратно.
func TestRun_LosslessRecompressesWithoutResizeOrJPEG(t *testing.T) {
	dir := t.TempDir()
	// Шире бокса 2560 и без альфы — обычный прогон и уменьшил бы, и сделал
	// JPEG. Плавный цвет, а не шум: шум deflate не сожмёт и в «store», а
	// смысл проверки — что сырой файл ПОЛЕГЧАЛ, оставшись собой.
	src := image.NewNRGBA(image.Rect(0, 0, 3000, 100))
	for y := 0; y < 100; y++ {
		for x := 0; x < 3000; x++ {
			src.SetNRGBA(x, y, color.NRGBA{R: uint8(x), G: uint8(y * 2), B: uint8(x / 12), A: 255})
		}
	}
	p := filepath.Join(dir, "raw.png")
	f, err := os.Create(p)
	if err != nil {
		t.Fatal(err)
	}
	enc := png.Encoder{CompressionLevel: png.NoCompression}
	if err := enc.Encode(f, src); err != nil {
		t.Fatal(err)
	}
	f.Close()
	rawInfo, _ := os.Stat(p)

	res, err := Run(dir, Options{Apply: true, Lossless: true})
	if err != nil {
		t.Fatal(err)
	}
	if len(res) != 1 || res[0].Err != nil {
		t.Fatalf("results: %+v", res)
	}
	r := res[0]
	if r.Action != ActionRecompress {
		t.Fatalf("action = %s, want recompress (no resize, no jpeg)", r.Action)
	}
	if r.NewW != 3000 || r.NewH != 100 || r.NewPath != "" {
		t.Fatalf("lossless changed size/format: %dx%d → %dx%d, newPath=%q", r.OldW, r.OldH, r.NewW, r.NewH, r.NewPath)
	}
	info, _ := os.Stat(p)
	if info.Size() >= rawInfo.Size() {
		t.Fatalf("сырой файл не полегчал: %d → %d", rawInfo.Size(), info.Size())
	}
	back, err := os.Open(p)
	if err != nil {
		t.Fatal(err)
	}
	defer back.Close()
	got, err := png.Decode(back)
	if err != nil {
		t.Fatal(err)
	}
	want := src
	gotN, ok := got.(*image.NRGBA)
	if !ok {
		gotN = image.NewNRGBA(got.Bounds())
		for y := 0; y < 100; y++ {
			for x := 0; x < 3000; x++ {
				gotN.Set(x, y, got.At(x, y))
			}
		}
	}
	for i := range want.Pix {
		if want.Pix[i] != gotN.Pix[i] {
			t.Fatalf("пиксели разошлись на байте %d — это не lossless", i)
		}
	}
}
