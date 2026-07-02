package importer

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
)

// WriteToContentDir lands an import Result into a content root: the .lvn under
// scripts/, every resolved asset under art//bg/, and the title spliced into
// manifest.json (replacing any existing title with the same id, preserving the
// rest of the manifest). After this the content server serves the new title and
// the IDE/game see it on their next manifest poll — no restart needed.
func WriteToContentDir(contentDir string, res *Result) error {
	root := filepath.Clean(contentDir)
	write := func(rel string, data []byte) error {
		dst := filepath.Clean(filepath.Join(root, filepath.FromSlash(rel)))
		// Defence in depth: a crafted rel (e.g. an id of "../../etc/x") must never
		// escape the content root. filepath.Join resolves "..", so verify the
		// cleaned destination still lives under root before touching the disk.
		if dst != root && !strings.HasPrefix(dst, root+string(os.PathSeparator)) {
			return fmt.Errorf("refusing to write outside content root: %s", rel)
		}
		if err := os.MkdirAll(filepath.Dir(dst), 0o755); err != nil {
			return err
		}
		return atomicWrite(dst, data, 0o644)
	}

	// Multi-chapter import: every chapter's .lvn/.lvns. Single-chapter: ScriptRel/Lvn.
	for _, sc := range res.Scripts {
		if err := write(sc.Rel, sc.Data); err != nil {
			return fmt.Errorf("write %s: %w", sc.Rel, err)
		}
	}
	if res.ScriptRel != "" {
		if err := write(res.ScriptRel, res.Lvn); err != nil {
			return fmt.Errorf("write script: %w", err)
		}
	}
	if res.LvnsRel != "" && len(res.Lvns) > 0 {
		if err := write(res.LvnsRel, res.Lvns); err != nil {
			return fmt.Errorf("write lvns: %w", err)
		}
	}
	if res.CatalogRel != "" && len(res.Catalog) > 0 {
		cat, err := json.MarshalIndent(res.Catalog, "", " ")
		if err != nil {
			return err
		}
		if err := write(res.CatalogRel, cat); err != nil {
			return fmt.Errorf("write catalog: %w", err)
		}
	}
	// Multi-chapter localized import: one catalog sidecar per chapter.
	for _, cf := range res.Catalogs {
		if err := write(cf.Rel, cf.Data); err != nil {
			return fmt.Errorf("write catalog %s: %w", cf.Rel, err)
		}
	}
	for _, a := range res.Art {
		if err := write(a.Rel, a.Data); err != nil {
			return fmt.Errorf("write %s: %w", a.Rel, err)
		}
	}
	if err := MergeTitleIntoManifest(filepath.Join(contentDir, "manifest.json"), res.Title); err != nil {
		return fmt.Errorf("manifest: %w", err)
	}
	if len(res.Sprites) > 0 {
		if err := MergeSpritesIntoManifest(filepath.Join(contentDir, "manifest.json"), res.Sprites); err != nil {
			return fmt.Errorf("manifest sprites: %w", err)
		}
	}
	return nil
}

// MergeSpritesIntoManifest splices auto-built cast entities into manifest.sprites
// by id (replace-or-add), leaving hand-authored entities and every other field
// untouched. A missing manifest is treated as empty.
func MergeSpritesIntoManifest(manifestPath string, sprites map[string]any) error {
	manifest := map[string]any{}
	if data, err := os.ReadFile(manifestPath); err == nil && len(data) > 0 {
		if err := json.Unmarshal(data, &manifest); err != nil {
			return fmt.Errorf("parse existing manifest: %w", err)
		}
	}
	existing, _ := manifest["sprites"].(map[string]any)
	if existing == nil {
		existing = map[string]any{}
	}
	for id, ent := range sprites {
		existing[id] = ent
	}
	manifest["sprites"] = existing

	out, err := json.MarshalIndent(manifest, "", "  ")
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(manifestPath), 0o755); err != nil {
		return err
	}
	return atomicWrite(manifestPath, out, 0o644)
}

// MergeTitleIntoManifest splices a title into manifest.json by id (replace-or-
// append), leaving every other field (ui, sprites, other titles) untouched. A
// missing or empty manifest is treated as {"titles":[]}.
func MergeTitleIntoManifest(manifestPath string, title Title) error {
	manifest := map[string]any{}
	if data, err := os.ReadFile(manifestPath); err == nil && len(data) > 0 {
		if err := json.Unmarshal(data, &manifest); err != nil {
			return fmt.Errorf("parse existing manifest: %w", err)
		}
	}

	var titles []any
	if t, ok := manifest["titles"].([]any); ok {
		titles = t
	}

	// Round-trip the typed title through JSON so it merges as a plain object.
	tb, err := json.Marshal(title)
	if err != nil {
		return err
	}
	var titleObj map[string]any
	if err := json.Unmarshal(tb, &titleObj); err != nil {
		return err
	}

	replaced := false
	for i, raw := range titles {
		if m, ok := raw.(map[string]any); ok {
			if id, _ := m["id"].(string); id == title.ID {
				titles[i] = titleObj
				replaced = true
				break
			}
		}
	}
	if !replaced {
		titles = append(titles, titleObj)
	}
	manifest["titles"] = titles

	out, err := json.MarshalIndent(manifest, "", "  ")
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(manifestPath), 0o755); err != nil {
		return err
	}
	return atomicWrite(manifestPath, out, 0o644)
}

// atomicWrite writes via a temp file in the same directory then renames, so a
// concurrent reader (the server hashing files for cache-busting) never sees a
// half-written or zero-byte file.
func atomicWrite(dst string, data []byte, perm os.FileMode) error {
	tmp, err := os.CreateTemp(filepath.Dir(dst), ".tmp-*")
	if err != nil {
		return err
	}
	name := tmp.Name()
	if _, err := tmp.Write(data); err != nil {
		tmp.Close()
		os.Remove(name)
		return err
	}
	if err := tmp.Close(); err != nil {
		os.Remove(name)
		return err
	}
	if err := os.Chmod(name, perm); err != nil {
		os.Remove(name)
		return err
	}
	if err := os.Rename(name, dst); err != nil {
		os.Remove(name)
		return err
	}
	return nil
}
