// Package optimize is the built-in image compressor for large content: it
// shrinks oversized backgrounds/sprites and recompresses PNGs losslessly,
// WITHOUT ever touching a Spine atlas page's pixel dimensions — resizing a
// tightly frame-packed atlas (many small regions sharing edges) bleeds
// neighbouring frames into each other under any resampling filter, which is
// exactly the "Люсьен криво спарсилось" corruption this package guards
// against. Atlas pages get a lossless recompress only; everything else
// (backgrounds, standalone sprites, UI art) can be resized to a cap and,
// when it carries no real alpha, converted to JPEG for a much bigger win.
//
// No WebP: Unity has no built-in WebP decoder (Texture2D.LoadImage/
// UnityWebRequestTexture only understand PNG/JPG/TGA/EXR), and WebP wouldn't
// even help at runtime — once decoded, it's full RGBA in VRAM just like a
// same-resolution PNG. It only saves wire/disk bytes, which JPEG (universally
// decodable, already used throughout this content tree) achieves just as
// well without adding a client-side dependency.
package optimize

import (
	"bytes"
	"context"
	"fmt"
	"image"
	"image/jpeg"
	"image/png"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"sort"
	"strings"
	"sync"
	"time"

	"golang.org/x/image/draw"
)

type Kind string

const (
	KindAtlasPage  Kind = "atlas-page" // a Spine atlas texture page — resize is unsafe
	KindStandalone Kind = "standalone" // a background/sprite/UI image
)

type Action string

const (
	ActionSkip       Action = "skip"       // already optimal — left untouched
	ActionRecompress Action = "recompress" // same format, same size, smaller bytes
	ActionResize     Action = "resize"     // downscaled to the cap (+ recompressed)
	ActionToJPEG     Action = "to-jpeg"    // format converted png→jpg (no real alpha)
)

// Result is one file's outcome — a dry run reports these without writing.
type Result struct {
	Path       string // original path
	NewPath    string // set only when the extension changed (ActionToJPEG)
	Kind       Kind
	Action     Action
	OldW, OldH int
	NewW, NewH int
	OldBytes   int64
	NewBytes   int64
	Err        error
}

// Rename returns the old→new path when this result changed the file's
// extension (the only case a reference in manifest.json/.lvns needs fixing).
func (r Result) Rename() (oldPath, newPath string, ok bool) {
	if r.NewPath == "" || r.NewPath == r.Path {
		return "", "", false
	}
	return r.Path, r.NewPath, true
}

type Options struct {
	MaxSize     int  // longest-side cap in px (default 2560 — see LvnManifest fit modes)
	JPEGQuality int  // default 85
	Apply       bool // false = dry run: decide + measure, write nothing
	// Lossless — только пережатие: те же пиксели, тот же размер, тот же
	// формат. Ни уменьшения до бокса, ни png→jpg. Режим для исходников
	// художника: на проде 06.09 двадцать восемь PNG из art/ (238 МБ из 424)
	// лежали БЕЗ СЖАТИЯ — deflate «store», файл равен w×h×4, — а обычный
	// прогон тут же превратил бы часть из них в JPEG q85, то есть в потерю,
	// которой владелец не просил. Пережатие без потерь на живом файле
	// 9,95 МБ: 2,19 МБ (x4,5), пиксель в пиксель.
	Lossless bool
}

func (o Options) withDefaults() Options {
	if o.MaxSize <= 0 {
		o.MaxSize = 2560
	}
	if o.JPEGQuality <= 0 {
		o.JPEGQuality = 85
	}
	return o
}

// Run walks root for .png/.jpg/.jpeg files and optimizes each: atlas pages
// (detected via a sibling .atlas.txt) get a lossless recompress only; every
// other image gets resized to the cap if oversized, then encoded as PNG (kept
// alpha) or JPEG (no real alpha found) — whichever this file actually needs.
//
// Content trees here run to hundreds of megapixels per file (a Spine export
// can be 7000×8000+) — encoding those through Go's zlib-9 PNG path is the
// real cost, easily tens of seconds each. Files are independent, so they're
// processed on a worker pool sized to the machine rather than one at a time.
func Run(root string, opt Options) ([]Result, error) {
	opt = opt.withDefaults()

	// One atlas-page-name set per directory (parsed once, reused per file;
	// this classification pass is cheap — no image decode — so stays serial).
	atlasPagesByDir := map[string]map[string]bool{}
	var files []string
	err := filepath.WalkDir(root, func(path string, d os.DirEntry, err error) error {
		if err != nil || d.IsDir() {
			return err
		}
		switch strings.ToLower(filepath.Ext(path)) {
		case ".png", ".jpg", ".jpeg":
			files = append(files, path)
		}
		return nil
	})
	if err != nil {
		return nil, err
	}
	sort.Strings(files) // deterministic report order

	kinds := make([]Kind, len(files))
	for i, path := range files {
		dir := filepath.Dir(path)
		pages, ok := atlasPagesByDir[dir]
		if !ok {
			pages, err = atlasPageNames(dir)
			if err != nil {
				return nil, fmt.Errorf("%s: scanning atlas pages: %w", dir, err)
			}
			atlasPagesByDir[dir] = pages
		}
		kinds[i] = KindStandalone
		if pages[filepath.Base(path)] {
			kinds[i] = KindAtlasPage
		}
	}

	// The expensive part: decode+resize+encode, fanned out across workers.
	// Each goroutine owns a distinct result slot — no locking needed.
	results := make([]Result, len(files))
	workers := runtime.NumCPU()
	if workers > len(files) {
		workers = len(files)
	}
	if workers < 1 {
		workers = 1
	}
	jobs := make(chan int)
	var wg sync.WaitGroup
	for w := 0; w < workers; w++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for i := range jobs {
				results[i] = processFile(files[i], kinds[i], opt)
			}
		}()
	}
	for i := range files {
		jobs <- i
	}
	close(jobs)
	wg.Wait()
	return results, nil
}

// atlasPageNames scans every *.atlas / *.atlas.txt in dir and returns the set
// of page image filenames declared (a libgdx/Spine atlas names each page on
// its own line ending in an image extension, before that page's region list).
// Both extensions occur in this repo — most exports use ".atlas.txt", but at
// least one (spineboy, an unmodified upstream sample) uses plain ".atlas".
func atlasPageNames(dir string) (map[string]bool, error) {
	pages := map[string]bool{}
	var matches []string
	for _, pat := range []string{"*.atlas.txt", "*.atlas"} {
		m, err := filepath.Glob(filepath.Join(dir, pat))
		if err != nil {
			return nil, err
		}
		matches = append(matches, m...)
	}
	for _, m := range matches {
		data, err := os.ReadFile(m)
		if err != nil {
			return nil, err
		}
		for _, line := range strings.Split(string(data), "\n") {
			t := strings.TrimSpace(line)
			lower := strings.ToLower(t)
			if strings.HasSuffix(lower, ".png") || strings.HasSuffix(lower, ".jpg") || strings.HasSuffix(lower, ".jpeg") {
				pages[t] = true
			}
		}
	}
	return pages, nil
}

func processFile(path string, kind Kind, opt Options) Result {
	r := Result{Path: path, Kind: kind}

	info, err := os.Stat(path)
	if err != nil {
		r.Err = err
		return r
	}
	r.OldBytes = info.Size()

	f, err := os.Open(path)
	if err != nil {
		r.Err = err
		return r
	}
	src, format, err := image.Decode(f)
	f.Close()
	if err != nil {
		r.Err = fmt.Errorf("decode: %w", err)
		return r
	}
	b := src.Bounds()
	r.OldW, r.OldH = b.Dx(), b.Dy()
	r.NewW, r.NewH = r.OldW, r.OldH

	img := src
	resized := false
	if kind == KindStandalone && !opt.Lossless {
		if m := max(r.OldW, r.OldH); m > opt.MaxSize {
			img = resizeToFit(src, opt.MaxSize)
			nb := img.Bounds()
			r.NewW, r.NewH = nb.Dx(), nb.Dy()
			resized = true
		}
	}

	// An unchanged, already-JPEG file gets left COMPLETELY alone: re-encoding
	// it would be a second lossy pass for a size win nobody asked for (real
	// quality loss, no upside) — see lvn-project memory on over-aggressive
	// compression wrecking a soft glow/smoke effect earlier in this project.
	if kind == KindStandalone && format == "jpeg" && !resized {
		r.Action = ActionSkip
		return r
	}

	// Try every SAFE candidate encoding and keep whichever is smallest — never
	// guess. This is what catches small flat-color PNGs (icons, hotspot masks)
	// that actually GROW under JPEG's block-DCT overhead: a naive "no alpha →
	// always JPEG" rule regresses them (confirmed empirically: a few ui/
	// hotspot pngs ballooned 2–3× before this comparison was added).
	pngOut, err := encodePNGBest(img)
	if err != nil {
		r.Err = fmt.Errorf("encode png: %w", err)
		return r
	}
	best, bestFormat := pngOut, "png"
	// Atlas pages NEVER leave PNG (Spine's .atlas.txt names the exact file and
	// needs the alpha channel) — no JPEG candidate for those, ever.
	if kind == KindStandalone && !opt.Lossless && !hasRealAlpha(img) {
		jpegOut, err := encodeJPEG(img, opt.JPEGQuality)
		if err != nil {
			r.Err = fmt.Errorf("encode jpeg: %w", err)
			return r
		}
		if len(jpegOut) < len(best) {
			best, bestFormat = jpegOut, "jpeg"
		}
	}
	r.NewBytes = int64(len(best))

	switch {
	case r.NewBytes >= r.OldBytes && !resized:
		r.Action = ActionSkip
		return r // nothing to write, even with -apply — never grow a file
	case bestFormat == "jpeg" && format != "jpeg":
		r.Action = ActionToJPEG
		r.NewPath = strings.TrimSuffix(path, filepath.Ext(path)) + ".jpg"
	case resized:
		r.Action = ActionResize
	default:
		r.Action = ActionRecompress
	}
	out := best

	if !opt.Apply {
		return r
	}
	writePath := path
	if r.NewPath != "" {
		writePath = r.NewPath
	}
	// Atomic replace: -apply usually overwrites the original in place, and a
	// crash mid-write must never leave a half-written image where art used to be.
	tmp := writePath + ".opt.tmp"
	if err := os.WriteFile(tmp, out, 0o644); err != nil {
		r.Err = fmt.Errorf("write: %w", err)
		return r
	}
	if err := os.Rename(tmp, writePath); err != nil {
		_ = os.Remove(tmp)
		r.Err = fmt.Errorf("write: %w", err)
		return r
	}
	if r.NewPath != "" && r.NewPath != path {
		if err := os.Remove(path); err != nil {
			r.Err = fmt.Errorf("remove old %s: %w", path, err)
		}
	}
	return r
}

// resizeToFit downscales to fit within cap on the longest side, preserving
// aspect ratio. CatmullRom (bicubic) — sharper than bilinear for the large
// downscale ratios these 4k–8k source arts need (see lvn-project memory:
// встречались атласы 7708×8252 и 4000×4048).
func resizeToFit(src image.Image, cap int) image.Image {
	b := src.Bounds()
	w, h := b.Dx(), b.Dy()
	m := max(w, h)
	k := float64(cap) / float64(m)
	nw, nh := max(1, int(float64(w)*k+0.5)), max(1, int(float64(h)*k+0.5))
	dst := image.NewRGBA(image.Rect(0, 0, nw, nh))
	draw.CatmullRom.Scale(dst, dst.Bounds(), src, b, draw.Over, nil)
	return dst
}

// hasRealAlpha reports whether any pixel is meaningfully transparent — a
// fully-opaque image (most photographic backgrounds) is safe to flatten to
// JPEG; anything with actual transparency (character cutouts, UI glows,
// Spine bg overlays) must stay PNG or the alpha channel is simply gone.
func hasRealAlpha(img image.Image) bool {
	switch m := img.(type) {
	case *image.RGBA:
		for i := 3; i < len(m.Pix); i += 4 {
			if m.Pix[i] != 255 {
				return true
			}
		}
		return false
	case *image.NRGBA:
		for i := 3; i < len(m.Pix); i += 4 {
			if m.Pix[i] != 255 {
				return true
			}
		}
		return false
	case *image.YCbCr, *image.Gray, *image.Gray16, *image.CMYK:
		// These color models carry no alpha channel at all (JPEG decodes to
		// YCbCr) — skip the per-pixel .At() scan entirely, it can only ever
		// find "opaque" and costs a virtual call per pixel on nothing.
		return false
	}
	// A rarer model that CAN carry partial alpha (e.g. a palette PNG with a
	// tRNS chunk → image.Paletted, or RGBA64/NRGBA64) — scan for real cases.
	b := img.Bounds()
	for y := b.Min.Y; y < b.Max.Y; y++ {
		for x := b.Min.X; x < b.Max.X; x++ {
			if _, _, _, a := img.At(x, y).RGBA(); a != 0xffff {
				return true
			}
		}
	}
	return false
}

func encodeJPEG(img image.Image, quality int) ([]byte, error) {
	var buf bytes.Buffer
	if err := jpeg.Encode(&buf, img, &jpeg.Options{Quality: quality}); err != nil {
		return nil, err
	}
	return buf.Bytes(), nil
}

// encodePNGBest tries a TRUE-lossless external optimizer (oxipng/zopflipng/
// optipng — same pixels, denser DEFLATE) when one is on PATH, and always
// falls back to the stdlib encoder so the tool needs nothing installed to
// work. pngquant is deliberately NOT used here: it quantizes to a palette
// (lossy), which is exactly the kind of quality loss that wrecked a soft
// glow/smoke effect earlier in this project — see lvn-project memory.
func encodePNGBest(img image.Image) ([]byte, error) {
	var buf bytes.Buffer
	enc := png.Encoder{CompressionLevel: png.BestCompression}
	if err := enc.Encode(&buf, img); err != nil {
		return nil, err
	}
	if best := externalPNGOptimize(buf.Bytes()); best != nil {
		return best, nil
	}
	return buf.Bytes(), nil
}

// externalPNGOptimize best-effort re-squeezes PNG bytes through whichever
// lossless optimizer is installed. Returns nil (not an error) when none is
// available or the pass doesn't help — the stdlib encoding is always a
// valid result on its own.
func externalPNGOptimize(pngBytes []byte) []byte {
	tools := [][]string{
		{"oxipng", "-o", "max", "--stdout", "-"},
		{"zopflipng", "-", "-"},
		{"optipng", "-o7", "-out", "-", "-"},
	}
	for _, argv := range tools {
		bin, err := exec.LookPath(argv[0])
		if err != nil {
			continue
		}
		ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
		cmd := exec.CommandContext(ctx, bin, argv[1:]...)
		cmd.Stdin = bytes.NewReader(pngBytes)
		var out bytes.Buffer
		cmd.Stdout = &out
		err = cmd.Run()
		cancel()
		if err != nil || out.Len() == 0 {
			continue
		}
		if out.Len() < len(pngBytes) {
			return out.Bytes()
		}
		return nil
	}
	return nil
}
