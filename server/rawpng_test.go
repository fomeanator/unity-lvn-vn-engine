package main

import (
	"bytes"
	"image"
	"image/color"
	"image/png"
	"net/http"
	"os"
	"path/filepath"
	"testing"
	"time"
)

// Сырой PNG — как его сохраняет инструмент художника: deflate «store»,
// размер файла равен w×h×4. Именно такие лежат на проде (28 файлов, 238 МБ).
func writeRawPNG(t *testing.T, path string, img image.Image) {
	t.Helper()
	var buf bytes.Buffer
	enc := png.Encoder{CompressionLevel: png.NoCompression}
	if err := enc.Encode(&buf, img); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, buf.Bytes(), 0o644); err != nil {
		t.Fatal(err)
	}
}

// Облик персонажа: плавный цвет и полосы полупрозрачности — сжимается в
// разы, как и настоящий арт, и при этом несёт РЕАЛЬНУЮ альфу, где ошибка
// премультипликации была бы видна.
func actorLike(w, h int) *image.NRGBA {
	img := image.NewNRGBA(image.Rect(0, 0, w, h))
	for y := 0; y < h; y++ {
		for x := 0; x < w; x++ {
			a := uint8(255)
			if x%3 == 0 {
				a = 200
			}
			if x < w/8 {
				a = 0
			}
			img.SetNRGBA(x, y, color.NRGBA{R: uint8(x), G: uint8(y), B: 128, A: a})
		}
	}
	return img
}

func decodeFile(t *testing.T, path string) image.Image {
	t.Helper()
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	img, err := png.Decode(bytes.NewReader(data))
	if err != nil {
		t.Fatalf("%s: %v", path, err)
	}
	return img
}

func TestHealRawPNG_SmallerPixelIdenticalMtimeKept(t *testing.T) {
	dir := t.TempDir()
	p := filepath.Join(dir, "hero.png")
	src := actorLike(400, 300)
	writeRawPNG(t, p, src)
	old := time.Date(2026, 7, 13, 20, 47, 0, 0, time.UTC)
	if err := os.Chtimes(p, old, old); err != nil {
		t.Fatal(err)
	}
	if !pngLooksRaw(p) {
		t.Fatal("стенд не ставит задачу: файл без сжатия не опознан как сырой")
	}

	before, after := raws.heal(p)
	if after == 0 || after*2 > before {
		t.Fatalf("сырой файл не полегчал хотя бы вдвое: %d → %d", before, after)
	}
	if pngLooksRaw(p) {
		t.Fatal("после лечения файл по-прежнему сырой")
	}
	if !samePixels(src, decodeFile(t, p)) {
		t.Fatal("пережатие изменило пиксели — это уже не lossless")
	}
	info, _ := os.Stat(p)
	if !info.ModTime().Equal(old) {
		t.Fatalf("mtime сдвинулся (%v) — производные @1k/.ktx2 сочтут исходник новым и перекодируются впустую", info.ModTime())
	}
	if leftovers, _ := filepath.Glob(filepath.Join(dir, ".heal-*")); len(leftovers) != 0 {
		t.Fatalf("остался полуфайл: %v", leftovers)
	}
}

// Пережатие обязано быть без потерь и для цветовых моделей, которые Go
// декодирует не в RGBA: палитра и серый. Ошибка тут — не «мыло», а
// сломанный файл, и заметить её можно только декодом обратно.
func TestHealRawPNG_PalettedAndGraySurvive(t *testing.T) {
	dir := t.TempDir()
	pal := image.NewPaletted(image.Rect(0, 0, 200, 120), color.Palette{
		color.NRGBA{A: 0}, color.NRGBA{R: 255, A: 255}, color.NRGBA{G: 255, A: 255}, color.NRGBA{B: 255, A: 128},
	})
	for y := 0; y < 120; y++ {
		for x := 0; x < 200; x++ {
			pal.SetColorIndex(x, y, uint8((x/25+y/30)%4))
		}
	}
	gray := image.NewGray16(image.Rect(0, 0, 200, 120))
	for y := 0; y < 120; y++ {
		for x := 0; x < 200; x++ {
			gray.SetGray16(x, y, color.Gray16{Y: uint16(x * 300)})
		}
	}
	for name, img := range map[string]image.Image{"pal.png": pal, "gray16.png": gray} {
		p := filepath.Join(dir, name)
		writeRawPNG(t, p, img)
		raws.heal(p)
		if !samePixels(img, decodeFile(t, p)) {
			t.Fatalf("%s: пиксели после лечения не совпали", name)
		}
	}
}

// Здоровый файл не трогается вовсе: ни байта, ни времени правки. Иначе
// «лечение» пережимало бы весь контент на каждом старте.
func TestHeal_CompressedFileUntouched(t *testing.T) {
	dir := t.TempDir()
	p := filepath.Join(dir, "fine.png")
	writeTestPNG(t, p, 400, 300)
	was, _ := os.ReadFile(p)
	if pngLooksRaw(p) {
		t.Fatal("нормально сжатый PNG опознан как сырой — лечение шло бы по всему контенту")
	}
	if b, a := raws.heal(p); b != 0 || a != 0 {
		t.Fatalf("здоровый файл пережали: %d → %d", b, a)
	}
	now, _ := os.ReadFile(p)
	if !bytes.Equal(was, now) {
		t.Fatal("байты здорового файла изменились")
	}
}

// ДВЕРЬ ВАРИАНТОВ: исходник, который в бокс уже влезает, отдавался КАК ЕСТЬ —
// ровно так 9,95 МБ уезжали на ступени «2k». Теперь наружу уходит
// пережатый, и это тот же кадр пиксель в пиксель.
func TestRawGuard_FitsAlreadyVariantIsNotRaw(t *testing.T) {
	_, h, dir := newDownscaleTestServer(t)
	if err := os.MkdirAll(filepath.Join(dir, "art"), 0o755); err != nil {
		t.Fatal(err)
	}
	src := actorLike(600, 400)
	writeRawPNG(t, filepath.Join(dir, "art", "hero.png"), src)
	rawSize, _ := os.Stat(filepath.Join(dir, "art", "hero.png"))

	rec := get(t, h, "/content/art/hero@2k.png")
	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d, want 200", rec.Code)
	}
	if int64(rec.Body.Len())*2 > rawSize.Size() {
		t.Fatalf("на «@2k» ушло %d байт при сыром исходнике %d — оригинал наружу", rec.Body.Len(), rawSize.Size())
	}
	got, err := png.Decode(bytes.NewReader(rec.Body.Bytes()))
	if err != nil {
		t.Fatal(err)
	}
	if !samePixels(src, got) {
		t.Fatal("с провода ушли не те пиксели")
	}
}

// СТАТИЧЕСКАЯ ДВЕРЬ — та, в которую стучат по адресу без ступени (фоновый
// прогрев, сид, экспорт): и она не выпускает сырое.
func TestRawGuard_PlainDoorServesCompressed(t *testing.T) {
	dir := t.TempDir()
	s := &server{content: dir}
	h := s.contentHandler(dir)
	if err := os.MkdirAll(filepath.Join(dir, "art"), 0o755); err != nil {
		t.Fatal(err)
	}
	src := actorLike(600, 400)
	writeRawPNG(t, filepath.Join(dir, "art", "hero.png"), src)
	rawSize, _ := os.Stat(filepath.Join(dir, "art", "hero.png"))

	rec := get(t, h, "/content/art/hero.png")
	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d, want 200", rec.Code)
	}
	if int64(rec.Body.Len())*2 > rawSize.Size() {
		t.Fatalf("статическая дверь отдала %d байт при сыром %d — оригинал наружу", rec.Body.Len(), rawSize.Size())
	}
	got, err := png.Decode(bytes.NewReader(rec.Body.Bytes()))
	if err != nil {
		t.Fatal(err)
	}
	if !samePixels(src, got) {
		t.Fatal("с провода ушли не те пиксели")
	}
	// ETag обязан описывать то, что ушло: клиент докачивает по If-Range, и
	// метка сырого файла приклеила бы хвост нового к голове старого.
	info, _ := os.Stat(filepath.Join(dir, "art", "hero.png"))
	if want := fileETag(dir, "art/hero.png"); rec.Header().Get("ETag") != want || info.Size() != int64(rec.Body.Len()) {
		t.Fatalf("ETag %q не про отданный файл (%d байт на диске, %d ушло)", rec.Header().Get("ETag"), info.Size(), rec.Body.Len())
	}
}

// Обход при старте лечит всё сырое дерево, но не лезет в служебные
// поддеревья: там лежат чужие файлы, и трогать их сервер не вправе.
func TestHealRawTree_SweepsContentSkipsPrivate(t *testing.T) {
	dir := t.TempDir()
	for _, sub := range []string{"art", "bg", "state", ".history"} {
		if err := os.MkdirAll(filepath.Join(dir, sub), 0o755); err != nil {
			t.Fatal(err)
		}
		writeRawPNG(t, filepath.Join(dir, sub, "x.png"), actorLike(300, 200))
	}
	healRawTree(dir)
	for _, sub := range []string{"art", "bg"} {
		if pngLooksRaw(filepath.Join(dir, sub, "x.png")) {
			t.Fatalf("%s/x.png остался сырым после обхода", sub)
		}
	}
	for _, sub := range []string{"state", ".history"} {
		if !pngLooksRaw(filepath.Join(dir, sub, "x.png")) {
			t.Fatalf("%s/x.png тронут — обход зашёл в служебное поддерево", sub)
		}
	}
}
