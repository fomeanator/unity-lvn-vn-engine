package importer

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
)

// TestE2EProjects imports every real articy:draft project under ARTICY_PROJECTS and
// asserts the quality invariants a shipped novella must hold: every chapter is
// fully reachable from its entry AND can reach its end (no dead loops), the opening
// scene's assets are declared critical (no pop-in), and no choice is a structural
// fan-out (a bogus mega-menu). Env-gated — skipped without a projects root.
//
//	ARTICY_PROJECTS=/Users/fomean/articy_re/run go test ./importer -run TestE2E -v
func TestE2EProjects(t *testing.T) {
	root := os.Getenv("ARTICY_PROJECTS")
	if root == "" {
		t.Skip("set ARTICY_PROJECTS=<dir of extracted articy projects>")
	}
	projects := discoverProjects(root)
	if len(projects) == 0 {
		t.Fatalf("no projects (dirs with a Partitions/ subdir) under %s", root)
	}
	for _, proj := range projects {
		proj := proj
		t.Run(filepath.Base(proj), func(t *testing.T) {
			out := t.TempDir()
			id := "e2e"
			res, err := Run(proj, Options{ID: id, Name: id, Start: -1, AutoStage: true})
			if err != nil {
				t.Fatalf("import: %v", err)
			}
			// Gather the chapters actually written (multi-chapter → res.Scripts; single
			// → res.ScriptRel/Lvn), plus the title's manifest chapters for asset checks.
			if err := WriteToContentDir(out, res); err != nil {
				t.Fatalf("write: %v", err)
			}
			chapters := chaptersOf(res)
			if len(chapters) == 0 {
				t.Fatal("no chapters produced")
			}
			totalCritical := 0
			for _, ch := range res.Title.Seasons {
				for _, c := range ch.Chapters {
					crit := 0
					for _, m := range c.Assets {
						if m.Critical {
							crit++
						}
					}
					totalCritical += crit
					// A chapter with any visuals should declare at least one critical
					// asset so its opening is warmed before Play.
					if len(c.Assets) > 0 && crit == 0 {
						t.Errorf("chapter %s: %d assets but none critical (opening would pop-in)", c.ID, len(c.Assets))
					}
				}
			}
			for _, rel := range chapters {
				data, err := os.ReadFile(filepath.Join(out, filepath.FromSlash(rel)))
				if err != nil {
					t.Fatalf("read %s: %v", rel, err)
				}
				checkScript(t, rel, data)
			}
			t.Logf("%s: %d chapter file(s), %d critical assets", filepath.Base(proj), len(chapters), totalCritical)
		})
	}
}

// TestE2ELocalizeMultiChapter asserts that -localize is honoured on the chaptered
// path (the primary path for real novels): every chapter gets its own catalog
// sidecar and its .lvn references text ONLY by text_id (no inline strings left).
// This is a regression guard — localization used to be a silent no-op here.
func TestE2ELocalizeMultiChapter(t *testing.T) {
	root := os.Getenv("ARTICY_PROJECTS")
	if root == "" {
		t.Skip("set ARTICY_PROJECTS=<dir of extracted articy projects>")
	}
	// Find a project that actually imports as multiple chapters.
	for _, proj := range discoverProjects(root) {
		res, err := Run(proj, Options{ID: "loc", Name: "loc", Start: -1, AutoStage: true, Localize: true})
		if err != nil {
			t.Fatalf("%s: import: %v", filepath.Base(proj), err)
		}
		if len(res.Scripts) == 0 {
			continue // single-chapter project — not what this test targets
		}
		if len(res.Catalogs) == 0 {
			t.Fatalf("%s: multi-chapter import produced no localization catalogs", filepath.Base(proj))
		}
		if res.Lang == "" {
			t.Errorf("%s: localized import has no language code", filepath.Base(proj))
		}
		// Every chapter .lvn should be fully keyed (no inline say text survived).
		out := t.TempDir()
		if err := WriteToContentDir(out, res); err != nil {
			t.Fatalf("write: %v", err)
		}
		inlineFound := false
		for _, rel := range chaptersOf(res) {
			data, _ := os.ReadFile(filepath.Join(out, filepath.FromSlash(rel)))
			var doc lvnDoc
			_ = json.Unmarshal(data, &doc)
			for _, c := range doc.Script {
				if c["op"] == "say" {
					if _, has := c["text"]; has {
						inlineFound = true
					}
				}
			}
		}
		if inlineFound {
			t.Errorf("%s: a localized chapter still has inline say text (should be text_id only)", filepath.Base(proj))
		}
		// Catalog sidecars must be named <chapter>.<lang>.json and written.
		for _, cf := range res.Catalogs {
			if _, err := os.Stat(filepath.Join(out, filepath.FromSlash(cf.Rel))); err != nil {
				t.Errorf("catalog sidecar not written: %s", cf.Rel)
			}
		}
		t.Logf("%s: %d chapters, %d catalogs, lang=%s, %d merged strings",
			filepath.Base(proj), len(chaptersOf(res)), len(res.Catalogs), res.Lang, len(res.Catalog))
		return // one multi-chapter project is enough
	}
	t.Skip("no multi-chapter project found under ARTICY_PROJECTS")
}

func discoverProjects(root string) []string {
	var out []string
	_ = filepath.Walk(root, func(p string, info os.FileInfo, err error) error {
		if err != nil || !info.IsDir() {
			return nil
		}
		if info.Name() == "Partitions" {
			out = append(out, filepath.Dir(p))
		}
		return nil
	})
	return out
}

// chaptersOf returns the content-relative .lvn paths an import wrote.
func chaptersOf(res *Result) []string {
	var out []string
	for _, s := range res.Scripts {
		if filepath.Ext(s.Rel) == ".lvn" {
			out = append(out, s.Rel)
		}
	}
	if len(out) == 0 && res.ScriptRel != "" {
		out = append(out, res.ScriptRel)
	}
	return out
}

// ── .lvn reachability / completability check (mirrors the play graph) ──

type lvnCmd map[string]any
type lvnDoc struct {
	Script []lvnCmd `json:"script"`
}

func checkScript(t *testing.T, name string, data []byte) {
	var doc lvnDoc
	if err := json.Unmarshal(data, &doc); err != nil {
		t.Fatalf("%s: parse: %v", name, err)
	}
	s := doc.Script
	n := len(s)
	if n == 0 {
		t.Fatalf("%s: empty script", name)
	}
	lab := map[string]int{}
	for i, c := range s {
		if c["op"] == "label" {
			if id, _ := c["id"].(string); id != "" {
				lab[id] = i
			}
		}
	}
	succ := func(ip int) []int {
		c := s[ip]
		op, _ := c["op"].(string)
		at := func(l string) int {
			if l == "" || l == "__end" {
				return n
			}
			if j, ok := lab[l]; ok {
				return j
			}
			return n // unknown label → end
		}
		switch op {
		case "goto":
			l, _ := c["label"].(string)
			return []int{at(l)}
		case "if":
			th, _ := c["then"].(string)
			el, _ := c["else"].(string)
			return []int{at(th), at(el)}
		case "return":
			return []int{n}
		case "choice":
			opts, _ := c["options"].([]any)
			out := make([]int, 0, len(opts))
			maxOpts := 0
			for range opts {
				maxOpts++
			}
			if maxOpts > 8 {
				t.Errorf("%s: choice #%d has %d options (structural fan-out, not a menu)", name, ip, maxOpts)
			}
			for _, o := range opts {
				m, _ := o.(map[string]any)
				g, _ := m["goto"].(string)
				if g != "" {
					out = append(out, at(g))
					continue
				}
				if body, ok := m["body"].([]any); ok {
					dst := ip + 1
					for _, b := range body {
						bc, _ := b.(map[string]any)
						if bc["op"] == "goto" {
							if l, _ := bc["label"].(string); l != "" {
								dst = at(l)
							}
						}
					}
					out = append(out, dst)
					continue
				}
				out = append(out, ip+1)
			}
			return out
		default:
			return []int{ip + 1}
		}
	}
	// forward reach from entry (0)
	seen := make([]bool, n)
	stack := []int{0}
	seen[0] = true
	fwd := 0
	for len(stack) > 0 {
		x := stack[len(stack)-1]
		stack = stack[:len(stack)-1]
		fwd++
		for _, y := range succ(x) {
			if y >= 0 && y < n && !seen[y] {
				seen[y] = true
				stack = append(stack, y)
			}
		}
	}
	// reverse reach to END
	preds := make([][]int, n)
	term := make([]bool, n)
	for ip := 0; ip < n; ip++ {
		for _, y := range succ(ip) {
			if y >= n {
				term[ip] = true
			} else if y >= 0 {
				preds[y] = append(preds[y], ip)
			}
		}
	}
	canEnd := make([]bool, n)
	q := []int{}
	for ip := 0; ip < n; ip++ {
		if term[ip] {
			canEnd[ip] = true
			q = append(q, ip)
		}
	}
	for len(q) > 0 {
		x := q[0]
		q = q[1:]
		for _, p := range preds[x] {
			if !canEnd[p] {
				canEnd[p] = true
				q = append(q, p)
			}
		}
	}
	reachAndEnd := 0
	for ip := 0; ip < n; ip++ {
		if seen[ip] && canEnd[ip] {
			reachAndEnd++
		}
	}
	pct := 100 * float64(reachAndEnd) / float64(n)
	if pct < 99.5 {
		t.Errorf("%s: only %.1f%% of commands are reachable AND can reach the end (want ~100%%)", name, pct)
	}
}
