package main

// downscale.go — on-demand downscaled texture variants for the content
// server: a client requests "<name>@2k.png" instead of "<name>.png" and gets
// the same image resized to fit within 2048×2048 (aspect preserved, never
// upscaled). The variant is generated once, written next to the source and
// served as a plain static file from then on — the same "encode once, cache
// to disk forever" pattern astc.go uses. Variants are regenerable artifacts,
// never committed (see .gitignore).
//
// Why this exists: Spine atlas page exports in this project run up to
// 7708×8252 — decoding that PNG on the Unity main thread takes hundreds of
// milliseconds and the decoded RGBA occupies ~254 MB of VRAM for ONE page.
// Spine runtimes compute region UVs from the atlas file's "size:" line, not
// from the actual texture (spine-csharp Atlas.cs normalizes x/width), so a
// downscaled page with the same aspect renders correctly WITHOUT touching
// the .atlas file — the mesh geometry is unchanged, only texel density
// drops. 2048 is the ceiling every mobile-GPU vendor guide (ARM, Android,
// NVIDIA) recommends for atlas pages.
//
// The full-resolution source stays on disk as the single source of truth —
// its PIXELS are never touched, so print-quality art survives for future
// targets. Its bytes may be: a source saved without compression is re-packed
// losslessly in place before it reaches anyone (rawpng.go).

import (
	"fmt"
	"image"
	"image/jpeg"
	"image/png"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"

	"golang.org/x/image/draw"
)

// downscaleMax is the bounding box a variant fits inside. 2048 matches the
// industry ceiling for runtime-loaded atlas pages (RGBA32 2048² = 16 MB in
// VRAM vs 254 MB for this project's largest page).
const downscaleMax = 2048

// downscaleSuffix sits between the base name and the extension:
// "big_scene_atlas@2k.png". "@" never appears in this project's asset
// names, so a variant request can't collide with a real source file.
const downscaleSuffix = "@2k"

// Микровариант для «силуэта-проявления»: опоздавший на медленной сети арт
// входит вовремя крошечной тёмной версией (десятки КБ), полный доезжает
// фоном и проявляет его. Бокс сознательно мал — это заготовка силуэта, а
// не картинка для разглядывания.
const miniSuffix = "@mini"
const miniMax = 256

// Экономный вариант для ручки «Качество арта»: полноценная картинка, но в
// бокс 1024 — вчетверо легче @2k по памяти и трафику, на телефонном экране
// разница почти не читается.
const ecoSuffix = "@1k"
const ecoMax = 1024

// Средняя ступень «как в ютубе» (2K / 1440p / 1K): бокс 1440 — вдвое легче
// 2K по площади, заметно чётче 1K на больших экранах.
const midSuffix = "@1440"
const midMax = 1440

// downscaleExts are the image types a variant can be requested for. The
// variant keeps the source's format (PNG stays PNG — alpha survives; JPEG
// stays JPEG — no alpha to lose).
var downscaleExts = map[string]bool{".png": true, ".jpg": true, ".jpeg": true}

// downscaler serializes concurrent generations of the SAME variant path
// while letting different files resize in parallel — same single-flight
// shape as astcTranscoder. ONE instance is shared by withDownscale and
// withKTX2 (which materializes missing @2k sources before encoding), so the
// same variant path locks the same mutex no matter which door it enters by.
type downscaler struct {
	mu       sync.Mutex
	inFlight map[string]*sync.Mutex
}

func newDownscaler() *downscaler {
	return &downscaler{inFlight: map[string]*sync.Mutex{}}
}

func (d *downscaler) lockFor(path string) *sync.Mutex {
	d.mu.Lock()
	defer d.mu.Unlock()
	m, ok := d.inFlight[path]
	if !ok {
		m = &sync.Mutex{}
		d.inFlight[path] = m
	}
	return m
}

// sourceNewer reports whether src is strictly newer on disk than derived —
// the derived artifact (an @2k downscale, a .ktx2/.astc transcode) then shows
// YESTERDAY'S art and must be regenerated. "Cache to disk forever" broke the
// moment art became replaceable: an author drops a hi-res photo over the old
// file, the version index re-keys the client's cache, the client re-downloads
// the variant — and got the encode of the file that no longer exists.
func sourceNewer(src, derived string) bool {
	si, err := os.Stat(src)
	if err != nil {
		return false
	}
	di, err := os.Stat(derived)
	if err != nil {
		return true // derived missing — as stale as it gets
	}
	return si.ModTime().After(di.ModTime())
}

// variantSource maps a variant path back to its source: "X@2k.png" → "X.png",
// "X@mini.jpg" → "X.jpg". Returns "" (and box 0) for a path that isn't a
// variant request at all.
func variantSource(variantPath string) string {
	src, _ := variantSourceBox(variantPath)
	return src
}

func variantSourceBox(variantPath string) (string, int) {
	ext := strings.ToLower(filepath.Ext(variantPath))
	if !downscaleExts[ext] {
		return "", 0
	}
	base := strings.TrimSuffix(variantPath, filepath.Ext(variantPath))
	switch {
	case strings.HasSuffix(base, downscaleSuffix):
		return strings.TrimSuffix(base, downscaleSuffix) + filepath.Ext(variantPath), downscaleMax
	case strings.HasSuffix(base, miniSuffix):
		return strings.TrimSuffix(base, miniSuffix) + filepath.Ext(variantPath), miniMax
	case strings.HasSuffix(base, ecoSuffix):
		return strings.TrimSuffix(base, ecoSuffix) + filepath.Ext(variantPath), ecoMax
	case strings.HasSuffix(base, midSuffix):
		return strings.TrimSuffix(base, midSuffix) + filepath.Ext(variantPath), midMax
	}
	return "", 0
}

// generate decodes src, fits it inside downscaleMax (Catmull-Rom — the same
// resampler tools/lvnconv's optimizer uses), and atomically writes the
// variant. A source that already fits is NOT re-encoded — the caller serves
// the source file directly instead (returns errFitsAlready).
func (d *downscaler) generate(srcPath, variantPath string) error {
	_, box := variantSourceBox(variantPath)
	if box <= 0 {
		box = downscaleMax
	}
	return d.generateBox(srcPath, variantPath, box)
}

func (d *downscaler) generateBox(srcPath, variantPath string, box int) error {
	f, err := os.Open(srcPath)
	if err != nil {
		return err
	}
	src, _, err := image.Decode(f)
	f.Close()
	if err != nil {
		return fmt.Errorf("%s: decode: %w", srcPath, err)
	}

	b := src.Bounds()
	w, h := b.Dx(), b.Dy()
	scale := 1.0
	if w > box || h > box {
		scale = float64(box) / float64(max(w, h))
	}
	if scale >= 1.0 {
		return errFitsAlready
	}
	nw, nh := max(1, int(float64(w)*scale+0.5)), max(1, int(float64(h)*scale+0.5))
	rect := image.Rect(0, 0, nw, nh)

	// Kernel and colour-space choice, in priority order:
	//
	// 1. PMA atlas pages (a sibling .atlas declares `pma: true` — every Spine
	//    export in this project does): the PNG's RGB bytes are ALREADY alpha-
	//    premultiplied and the Spine shader consumes them as such. They must be
	//    filtered raw and written back raw — Go's decoder labels them straight
	//    alpha, so the generic path would premultiply a second time and the PNG
	//    encoder would then UN-premultiply the output, silently converting the
	//    variant to straight alpha. The shader keeps treating it as PMA, so
	//    every semi-transparent edge pixel renders too bright: the live-
	//    observed "little white holes". Tent kernel in PMA space is exactly
	//    the correct filter (that's what PMA exists for).
	// 2. Other alpha images: resample in premultiplied space with the tent
	//    kernel. Catmull-Rom's negative lobes overshoot past the alpha channel
	//    at hard edges and clamp into white specks on un-premultiply.
	// 3. Opaque images (photos, JPEG sources): Catmull-Rom for crispness.
	var dst image.Image
	if isPMAPage(srcPath) {
		raw := image.NewRGBA(b) // reinterpret the PMA bytes as premultiplied
		draw.Draw(raw, b, src, b.Min, draw.Src)
		if n, ok := src.(*image.NRGBA); ok {
			copy(raw.Pix, n.Pix) // undo Draw's double-premultiply: bytes verbatim
		}
		d := image.NewRGBA(rect)
		draw.BiLinear.Scale(d, rect, raw, b, draw.Src, nil)
		out := image.NewNRGBA(rect)
		copy(out.Pix, d.Pix) // write the filtered PMA bytes verbatim (no un-premultiply)
		dst = out
	} else if o, ok := src.(interface{ Opaque() bool }); ok && o.Opaque() {
		d := image.NewNRGBA(rect)
		draw.CatmullRom.Scale(d, rect, src, b, draw.Src, nil)
		dst = d
	} else {
		pm := image.NewRGBA(b) // alpha-premultiplied working copy
		draw.Draw(pm, b, src, b.Min, draw.Src)
		d := image.NewRGBA(rect)
		draw.BiLinear.Scale(d, rect, pm, b, draw.Src, nil)
		dst = d // png.Encode un-premultiplies safely: tent weights can't overshoot
	}

	dir, base := filepath.Split(variantPath)
	ext := filepath.Ext(base)
	tmp := filepath.Join(dir, fmt.Sprintf("%s.tmp-%d%s", strings.TrimSuffix(base, ext), time.Now().UnixNano(), ext))
	defer os.Remove(tmp) // no-op once renamed away

	out, err := os.Create(tmp)
	if err != nil {
		return err
	}
	switch strings.ToLower(ext) {
	case ".jpg", ".jpeg":
		err = jpeg.Encode(out, dst, &jpeg.Options{Quality: 85})
	default:
		// Default compression, not Best: a variant encodes once per deploy and
		// the request is waiting on it — seconds of extra zlib effort for a few
		// percent of disk is the wrong trade here (unlike lvnconv optimize,
		// which runs offline).
		err = png.Encode(out, dst)
	}
	if cerr := out.Close(); err == nil {
		err = cerr
	}
	if err != nil {
		return fmt.Errorf("%s: encode: %w", variantPath, err)
	}
	return os.Rename(tmp, variantPath)
}

var errFitsAlready = fmt.Errorf("source already fits within the variant box")

// heavyGen bounds concurrent on-demand image jobs (downscale decodes + ASTC
// transcodes): each holds a full decoded frame in RAM (a 4k spine page is
// ~64 MB RGBA), so a burst of cold-cache requests without a cap is a
// memory-exhaustion DoS. Queued requests just wait — the per-path locks
// already dedupe identical URLs.
var heavyGen = make(chan struct{}, 3)

// isPMAPage reports whether srcPath is a Spine atlas page whose pixels are
// alpha-premultiplied: a sibling .atlas/.atlas.txt that names this file and
// carries a `pma: true` line. Cheap (two small text files at most per dir)
// and runs once per variant thanks to the cache-to-disk-forever flow.
func isPMAPage(srcPath string) bool {
	dir := filepath.Dir(srcPath)
	base := filepath.Base(srcPath)
	entries, err := os.ReadDir(dir)
	if err != nil {
		return false
	}
	for _, e := range entries {
		name := strings.ToLower(e.Name())
		if !strings.HasSuffix(name, ".atlas") && !strings.HasSuffix(name, ".atlas.txt") {
			continue
		}
		data, err := os.ReadFile(filepath.Join(dir, e.Name()))
		if err != nil {
			continue
		}
		// libgdx atlas: pages are blocks starting with the image filename on
		// its own line, followed by `key: value` page settings. Read pma only
		// from THIS page's block — a multi-page atlas can mix pma flags, and a
		// bare Contains would take another page's setting for ours.
		inPage := false
		for _, line := range strings.Split(string(data), "\n") {
			t := strings.TrimSpace(line)
			if t == "" {
				inPage = false
				continue
			}
			if !strings.Contains(t, ":") { // a page-name line
				inPage = strings.EqualFold(t, base)
				continue
			}
			if inPage && strings.ReplaceAll(t, " ", "") == "pma:true" {
				return true
			}
		}
	}
	return false
}

// withDownscale wraps the content handler: a "<name>@2k.<ext>" request that
// isn't already cached on disk gets generated on demand from its sibling
// full-size source, then falls through to the static-file handler. A source
// that already fits inside the box is served as-is (no variant file is ever
// written for it). Every failure mode 404s — the client's loader treats that
// as "no variant available" and falls back to the original URL, so a server
// without this middleware (or a local-directory install) behaves identically.
func (s *server) withDownscale(d *downscaler, next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		rel := strings.TrimPrefix(r.URL.Path, "/content/")
		variantPath, ok := s.contentPath(rel)
		if !ok {
			http.NotFound(w, r)
			return
		}
		srcRel := variantSource(variantPath)
		if srcRel == "" { // not a variant request — plain static serve
			next.ServeHTTP(w, r)
			return
		}
		if fileExists(variantPath) && !sourceNewer(srcRel, variantPath) {
			next.ServeHTTP(w, r) // already generated and still fresh — plain file-serve hit
			return
		}
		if !fileExists(srcRel) {
			http.NotFound(w, r) // nothing to downscale from
			return
		}

		lock := d.lockFor(variantPath)
		lock.Lock()
		defer lock.Unlock()
		// Re-check under the lock: a queued sibling request may have just
		// regenerated it (freshness included — generate rewrites atomically).
		if !fileExists(variantPath) || sourceNewer(srcRel, variantPath) {
			heavyGen <- struct{}{}
			err := d.generate(srcRel, variantPath)
			<-heavyGen
			switch err {
			case nil:
			case errFitsAlready:
				// The replaced source fits the box itself now — a variant file
				// left from its bigger predecessor would shadow it forever.
				_ = os.Remove(variantPath)
				// СЫРОЙ ИСХОДНИК НАРУЖУ НЕ УХОДИТ. Именно здесь ступень «2k»
				// отдавала 9,95 МБ несжатого PNG за слой облика 1212×2048:
				// бокс не требовал уменьшения, и «исходник и есть вариант»
				// читалось буквально — вместе с deflate «store» художника.
				raws.heal(srcRel)
				http.ServeFile(w, r, srcRel) // small enough already — the source IS the variant
				return
			default:
				log.Printf("downscale: %v", err)
				http.NotFound(w, r)
				return
			}
		}
		next.ServeHTTP(w, r)
	})
}
