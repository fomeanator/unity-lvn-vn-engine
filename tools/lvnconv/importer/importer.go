// Package importer is the one-shot articy:draft (.adpd) → playable-novel pipeline
// behind the IDE's single "Import articy" button. Given an extracted .adpd project
// directory it produces everything a title needs to appear in the game, the admin
// IDE and the content server at once:
//
//   - a compiled .lvn script (adpd → articy model → .lvn), auto-staged so scenes
//     get backgrounds and speaking characters walk on;
//   - the referenced art, resolved from the project's Assets, with character
//     sprites matted (white background cut out) — keyed to the paths the script
//     uses (/content/art/*, /content/bg/*);
//   - a manifest title entry (carousel card + first chapter) wired to the script.
//
// It performs no I/O to the server itself — it returns the artifacts so the caller
// (the lvnconv CLI, or the server's import endpoint) writes them wherever content
// lives. That keeps the pipeline reusable and testable.
package importer

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"github.com/fomeanator/unity-lvn-vn-engine/tools/lvnconv/internal/adpd"
	"github.com/fomeanator/unity-lvn-vn-engine/tools/lvnconv/internal/articy"
)

// Options controls a single import.
type Options struct {
	ID        string // title id / script base name, e.g. "soviet"
	Name      string // display name for the carousel card
	Subtitle  string // small line under the name
	Start     int    // adpd start node ordinal (-1 = story opening)
	Max       int    // cap chapter at N nodes (0 = no cap)
	AutoStage bool   // emit bg/actor staging (default on via Run)
	Localize  bool   // extract text into a <script>.<lang>.json catalog (i18n)
}

// ArtFile is one resolved asset and the content-relative path it must be written
// to (matching the sprite_url the script references): "art/<file>" or "bg/<file>".
type ArtFile struct {
	Rel  string
	Data []byte
}

// Manifest shapes (a subset of the engine's LvnManifest, enough to add a title).
type Chapter struct {
	ID        string `json:"id"`
	Number    int    `json:"number"`
	Name      string `json:"name,omitempty"` // episode title ("Эпизод 3. …") for the chapter list
	ScriptURL string `json:"script_url"`
	BgURL     string `json:"bg_url,omitempty"`
	// Assets is the chapter's prioritized release set (content url → meta): the
	// runtime's AssetScheduler warms the `critical` ones (the opening scene) BEFORE
	// Play, so the first beats never pop-in; the rest stream during play. Without
	// this the loading screen has nothing to gate on and sprites draw a frame late.
	Assets map[string]AssetMeta `json:"assets,omitempty"`
}

// AssetMeta mirrors the runtime's LvnAssetMeta (the fields the client reads to
// schedule downloads). critical → required set (gates Play); eta_ms orders the
// deferred stream (earliest-used first).
type AssetMeta struct {
	Kind     string `json:"kind,omitempty"`
	Scope    string `json:"scope,omitempty"`
	Critical bool   `json:"critical,omitempty"`
	ETAms    int    `json:"eta_ms,omitempty"`
}

// collectChapterAssets scans a compiled chapter for the sprites it shows (bg /
// actor / obj) and builds its release set. The opening scene's first few sprites
// are `critical` (warmed before Play so the start never pops in); everything after
// is deferred, ordered by first appearance so it streams in just ahead of use.
func collectChapterAssets(doc *articy.Doc) map[string]AssetMeta {
	assets := map[string]AssetMeta{}
	bgCount := 0 // scene boundaries: the first scene is bgCount<=1
	visuals := 0
	for _, c := range doc.Script {
		op, _ := c["op"].(string)
		if op != "bg" && op != "actor" && op != "obj" {
			continue
		}
		if op == "bg" {
			bgCount++
		}
		url, _ := c["sprite_url"].(string)
		if url == "" {
			continue
		}
		visuals++
		// The opening SCENE (its bg + the characters shown in it, before the bg
		// changes) is critical → warmed before Play so the start never pops in.
		// Cap it so a scene with a big cast can't bloat the Play gate.
		critical := bgCount <= 1 && visuals <= 8
		m, ok := assets[url]
		if !ok {
			m = AssetMeta{Kind: "sprite", Scope: "chapter", ETAms: visuals * 40}
		}
		if critical {
			m.Critical = true
		}
		assets[url] = m
	}
	return assets
}

type Season struct {
	Chapters []Chapter `json:"chapters"`
}
type Title struct {
	ID       string   `json:"id"`
	Name     string   `json:"name"`
	Subtitle string   `json:"subtitle,omitempty"`
	CoverURL string   `json:"cover_url,omitempty"`
	Seasons  []Season `json:"seasons"`
}

// Result is everything an import produced. ScriptRel is the content-relative path
// for Lvn ("scripts/<id>.lvn"); Art carries the resolved/matted assets; Title is
// the manifest entry to splice in.
type Result struct {
	ScriptRel string
	Lvn       []byte
	Art       []ArtFile
	Title     Title
	Stats     map[string]int // op counts: say/choice/bg/actor/set/if…
	MissingBg []string       // scene locations with no matching art file (rendered dark)

	// Sprites is the auto-built cast catalog (id → entity) merged into
	// manifest.sprites, so the imported novel's whole roster shows up in the panel
	// ready to re-art. See BuildCatalog.
	Sprites map[string]any

	// Lvns is the .lvns decompilation of the script (editable Elvin Script source),
	// written beside the .lvn so the novel can be reworked as source in the panel.
	Lvns    []byte
	LvnsRel string

	// Localization (set only when Options.Localize): the extracted string catalog
	// (text_id → string), the project language code, and the content-relative path
	// the catalog must be written to ("scripts/<id>.<lang>.json"). The runtime
	// loads it per locale beside the script; other languages are extra catalogs
	// against the same keys.
	Catalog    map[string]string
	Lang       string
	CatalogRel string

	// Scripts holds every chapter's files (.lvn + .lvns) for a multi-chapter import.
	// When non-empty, WriteToContentDir writes these (ScriptRel/Lvn is the single-
	// chapter path).
	Scripts []ScriptFile

	// Catalogs holds each chapter's localization sidecar (scripts/<cid>.<lang>.json)
	// for a localized multi-chapter import. The runtime loads a chapter's catalog
	// beside its script by the same <script>.<lang>.json convention as single-chapter.
	Catalogs []ScriptFile
}

// ScriptFile is one written script (content-relative path + bytes).
type ScriptFile struct {
	Rel  string
	Data []byte
}

// Run executes the whole pipeline against an extracted .adpd project directory.
func Run(projectDir string, opt Options) (*Result, error) {
	if opt.ID == "" {
		opt.ID = "imported"
	}
	if opt.Name == "" {
		opt.Name = opt.ID
	}
	if opt.Start == 0 {
		opt.Start = -1
	}

	// Multi-chapter: a chaptered project (episodes = the Flow root's FlowFragment
	// children) imports as one title with a chapter per episode. Skipped when the
	// writer pinned a single -start chapter.
	if opt.Start < 0 {
		if chs, cerr := adpd.BuildChaptersJSON(projectDir); cerr == nil && len(chs) >= 2 {
			res, rerr := runMultiChapter(projectDir, opt, chs)
			if rerr != nil {
				return nil, rerr
			}
			if res != nil {
				return res, nil
			}
			// res == nil: a chapter isn't completable when scoped — fall through to
			// the single whole-novel chapter (guaranteed playable end-to-end).
		}
	}

	js, err := adpd.BuildExportJSON(projectDir, opt.Start, opt.Max)
	if err != nil {
		return nil, fmt.Errorf("adpd export: %w", err)
	}
	doc, err := articy.Convert(js, "")
	if err != nil {
		return nil, fmt.Errorf("articy convert: %w", err)
	}

	if opt.AutoStage {
		cast, err := adpd.Cast(projectDir)
		if err != nil {
			return nil, fmt.Errorf("adpd cast: %w", err)
		}
		AutoStage(doc, cast) // reads inline say text — must run before Localize
	}

	// Resolve art before localization swaps say text for keys (art reads sprite_url,
	// not text, but keep the ordering intent explicit).
	art, missing, firstBg := collectArt(buildAssetIndex(projectDir), doc, map[string][]byte{})

	// Auto-build the cast catalog from the compiled script (characters + the states
	// they're shown in), reusing the resolved art / placeholders. Extra placeholders
	// cover any actor with no concrete sprite.
	sprites, extraArt := BuildCatalog(doc)
	art = append(art, extraArt...)

	// Decompile to editable .lvns BEFORE localization swaps inline text for keys,
	// so the source reads with the real lines.
	lvns := ToLvns(doc)

	var catalog map[string]string
	var lang, catalogRel string
	if opt.Localize {
		catalog = Localize(doc)
		if lang, err = adpd.Lang(projectDir); err != nil {
			lang = "und"
		}
		catalogRel = "scripts/" + opt.ID + "." + lang + ".json"
	} else {
		StripStableIds(doc) // keys are only needed for the catalog
	}

	lvn, err := json.MarshalIndent(doc, "", " ")
	if err != nil {
		return nil, err
	}

	res := &Result{
		ScriptRel:  "scripts/" + opt.ID + ".lvn",
		Lvn:        lvn,
		Art:        art,
		Stats:      opStats(doc),
		MissingBg:  missing,
		Sprites:    sprites,
		Lvns:       lvns,
		LvnsRel:    "scripts/" + opt.ID + ".lvns",
		Catalog:    catalog,
		Lang:       lang,
		CatalogRel: catalogRel,
	}
	cover := firstBg // a real first-scene background beats a 404 placeholder
	res.Title = Title{
		ID:       opt.ID,
		Name:     opt.Name,
		Subtitle: opt.Subtitle,
		CoverURL: cover,
		Seasons: []Season{{Chapters: []Chapter{{
			ID:        opt.ID + "-ch1",
			Number:    1,
			ScriptURL: "/content/" + res.ScriptRel,
			BgURL:     cover,
			Assets:    collectChapterAssets(doc),
		}}}},
	}
	return res, nil
}

// runMultiChapter assembles one title from a chaptered project: each episode is a
// chapter with its own .lvn (+ editable .lvns), sharing one merged cast and art set.
func runMultiChapter(projectDir string, opt Options, chs []adpd.ChapterExport) (*Result, error) {
	var cast map[string]string
	if opt.AutoStage {
		c, err := adpd.Cast(projectDir)
		if err != nil {
			return nil, fmt.Errorf("adpd cast: %w", err)
		}
		cast = c
	}

	res := &Result{Sprites: map[string]any{}, Stats: map[string]int{}}
	artSeen := map[string]bool{}
	var chapters []Chapter
	var cover string

	// Build the project's asset index ONCE, and share a matte/read cache across all
	// chapters — a sprite used in many chapters is resolved (read + matted) a single
	// time. (Was rebuilt + re-matted per chapter: O(chapters × art), which dominated
	// the import of a large chaptered novel.)
	index := buildAssetIndex(projectDir)
	artCache := map[string][]byte{}

	// Localization is per chapter (each chapter script loads its own sidecar), but
	// the language code is a project-level fact — resolve it once.
	var lang string
	if opt.Localize {
		l, lerr := adpd.Lang(projectDir)
		if lerr != nil {
			l = "und"
		}
		lang = l
		res.Lang = lang
		res.Catalog = map[string]string{} // merged view, for the reported string count
	}

	for i, ch := range chs {
		doc, err := articy.Convert(ch.JSON, "")
		if err != nil {
			return nil, fmt.Errorf("chapter %d convert: %w", i+1, err)
		}
		if opt.AutoStage {
			AutoStage(doc, cast)
		}
		art, missing, firstBg := collectArt(index, doc, artCache)
		sprites, extraArt := BuildCatalog(doc)
		art = append(art, extraArt...)
		lvns := ToLvns(doc)                          // decompile before localization swaps text for keys
		cid := fmt.Sprintf("%s-ch%02d", opt.ID, i+1) // zero-padded → files sort in order

		if opt.Localize {
			catalog := Localize(doc)
			if len(catalog) > 0 {
				cb, cerr := json.MarshalIndent(catalog, "", " ")
				if cerr != nil {
					return nil, cerr
				}
				res.Catalogs = append(res.Catalogs, ScriptFile{
					Rel: "scripts/" + cid + "." + lang + ".json", Data: cb,
				})
				for k, v := range catalog {
					res.Catalog[k] = v
				}
			}
		} else {
			StripStableIds(doc) // keys are only needed for the catalog
		}

		lvn, err := json.MarshalIndent(doc, "", " ")
		if err != nil {
			return nil, err
		}

		if !reachesEnd(doc) {
			return nil, nil // a chapter can't be finished when scoped → fall back to one
		}
		rel := "scripts/" + cid + ".lvn"
		res.Scripts = append(res.Scripts,
			ScriptFile{Rel: rel, Data: lvn},
			ScriptFile{Rel: "scripts/" + cid + ".lvns", Data: lvns})
		for _, a := range art {
			if !artSeen[a.Rel] {
				artSeen[a.Rel] = true
				res.Art = append(res.Art, a)
			}
		}
		for k, v := range sprites {
			res.Sprites[k] = v
		}
		for k, v := range opStats(doc) {
			res.Stats[k] += v
		}
		res.MissingBg = append(res.MissingBg, missing...)
		if cover == "" {
			cover = firstBg
		}
		chapters = append(chapters, Chapter{
			ID: cid, Number: i + 1, Name: ch.Name, ScriptURL: "/content/" + rel, BgURL: firstBg,
			Assets: collectChapterAssets(doc),
		})
	}

	res.MissingBg = dedupe(res.MissingBg)
	res.Title = Title{
		ID: opt.ID, Name: opt.Name, Subtitle: opt.Subtitle, CoverURL: cover,
		Seasons: []Season{{Chapters: chapters}},
	}
	return res, nil
}

// reachesEnd reports whether the chapter can be finished: some path of choices
// from the start reaches __end (or runs off the script). A scoped chapter whose
// flow only cycles (its real exit crossed into another episode) fails this — the
// signal to keep the novel as one chapter.
func reachesEnd(doc *articy.Doc) bool {
	s := doc.Script
	n := len(s)
	lab := map[string]int{}
	for i, c := range s {
		if c["op"] == "label" {
			if id, _ := c["id"].(string); id != "" {
				lab[id] = i
			}
		}
	}
	jump := func(l string, st *[]int) bool {
		if l == "__end" {
			return true
		}
		if j, ok := lab[l]; ok {
			*st = append(*st, j)
		} else {
			return true // unknown label resolves to the end
		}
		return false
	}
	seen := make([]bool, n)
	st := []int{0}
	for len(st) > 0 {
		i := st[len(st)-1]
		st = st[:len(st)-1]
		if i >= n {
			return true // ran off the end
		}
		if seen[i] {
			continue
		}
		seen[i] = true
		c := s[i]
		switch c["op"] {
		case "goto":
			l, _ := c["label"].(string)
			if jump(l, &st) {
				return true
			}
		case "if":
			for _, k := range []string{"then", "else"} {
				if l, _ := c[k].(string); l != "" && jump(l, &st) {
					return true
				}
			}
		case "choice":
			opts, _ := c["options"].([]any)
			for _, o := range opts {
				om, _ := o.(map[string]any)
				if om == nil {
					if cm, ok := o.(articy.Cmd); ok {
						om = map[string]any(cm)
					}
				}
				if l, _ := om["goto"].(string); l != "" {
					if jump(l, &st) {
						return true
					}
					continue
				}
				for _, b := range asAnyList(om["body"]) {
					bm := asMapAny(b)
					if bm["op"] == "goto" {
						if l, _ := bm["label"].(string); l != "" && jump(l, &st) {
							return true
						}
					}
				}
			}
		case "return":
			// tunnel return — treat as progress toward the end
			if i+1 < n {
				st = append(st, i+1)
			} else {
				return true
			}
		default:
			st = append(st, i+1)
		}
	}
	return false
}

func asAnyList(v any) []any {
	if l, ok := v.([]any); ok {
		return l
	}
	return nil
}

func asMapAny(v any) map[string]any {
	if m, ok := v.(map[string]any); ok {
		return m
	}
	if c, ok := v.(articy.Cmd); ok {
		return map[string]any(c)
	}
	return map[string]any{}
}

func opStats(doc *articy.Doc) map[string]int {
	out := map[string]int{}
	for _, c := range doc.Script {
		if op, ok := c["op"].(string); ok {
			out[op]++
		}
	}
	return out
}

// placeholder sizes: characters are portrait, backgrounds/props 16:9.
const (
	phCharW, phCharH = 512, 768
	phBgW, phBgH     = 1280, 720
)

// collectArt resolves every sprite_url the staged script references to a file on
// disk under projectDir, returning the bytes keyed to the content path. Character
// sprites (art/*) are matted; backgrounds (bg/*) are copied as-is. Anything with
// no real art gets a labelled grey placeholder (a dummy showing the combination
// name) so the whole novel is visible — every character, pose and background —
// before any art exists. Returns the files, the scene locations still missing real
// art, and a content URL for the first background (the title cover).
func collectArt(index map[string]string, doc *articy.Doc, cache map[string][]byte) (art []ArtFile, missingBg []string, firstBg string) {
	seen := map[string]bool{} // per-chapter dedup of the returned set
	// add resolves a file's bytes ONCE (read+matte / placeholder) and memoises them
	// in `cache` by content path, so a sprite shared across chapters — or the whole
	// asset index walk — is never re-processed. This turns the multi-chapter import
	// from O(chapters × art) matting into O(art).
	add := func(rel string, compute func() []byte) {
		if seen[rel] {
			return
		}
		seen[rel] = true
		data, ok := cache[rel]
		if !ok {
			data = compute()
			cache[rel] = data
		}
		art = append(art, ArtFile{Rel: rel, Data: data})
	}

	for _, c := range doc.Script {
		op, _ := c["op"].(string)
		url, _ := c["sprite_url"].(string)
		if url == "" {
			continue
		}
		base := filepath.Base(url) // "Тимур_Обычный.png" / "Двор.jpg"
		label := stem(base)
		switch op {
		case "actor", "obj":
			add("art/"+base, func() []byte {
				if p, ok := index[normKey(label)]; ok {
					if data, err := os.ReadFile(p); err == nil {
						if matted, merr := Matte(data); merr == nil {
							return matted
						}
						return data // non-fatal: ship the original
					}
				}
				return Placeholder(label, phCharW, phCharH) // dummy character
			})
		case "bg":
			p := lookupBg(index, label)
			if p == "" {
				missingBg = append(missingBg, label)
			}
			add("bg/"+base, func() []byte {
				if p != "" {
					if data, err := os.ReadFile(p); err == nil {
						return data
					}
				}
				return Placeholder(label, phBgW, phBgH) // dummy background
			})
			if firstBg == "" {
				firstBg = "/content/bg/" + base
			}
		}
	}
	sort.Strings(missingBg)
	return art, dedupe(missingBg), firstBg
}

// buildAssetIndex maps normKey(name-without-hash) → file path for every image
// under projectDir.
func buildAssetIndex(projectDir string) map[string]string {
	index := map[string]string{}
	_ = filepath.Walk(projectDir, func(path string, info os.FileInfo, err error) error {
		if err != nil || info.IsDir() {
			return nil
		}
		switch strings.ToLower(filepath.Ext(path)) {
		case ".png", ".jpg", ".jpeg":
			key := normKey(stripHash(stem(filepath.Base(path))))
			if _, exists := index[key]; !exists {
				index[key] = path
			}
		}
		return nil
	})
	return index
}

// lookupBg resolves a scene location to a background file: exact key first, then a
// loose substring match (the disk file often carries extra qualifiers).
func lookupBg(index map[string]string, loc string) string {
	k := normKey(loc)
	if p, ok := index[k]; ok {
		return p
	}
	for key, p := range index {
		if strings.Contains(key, k) || strings.Contains(k, key) {
			return p
		}
	}
	return ""
}

func stem(name string) string { return strings.TrimSuffix(name, filepath.Ext(name)) }

func dedupe(in []string) []string {
	seen := map[string]bool{}
	out := in[:0]
	for _, s := range in {
		if !seen[s] {
			seen[s] = true
			out = append(out, s)
		}
	}
	return out
}
