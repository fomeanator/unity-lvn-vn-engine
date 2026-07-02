// Export a ready-to-open Unity project as a zip.
//
// The sandbox/ project is the template: a clean Unity project that pulls the
// engine as a UPM package. Export copies it, swaps in a generated Boot.cs (the
// player's server URL + game name), patches the product name / bundle id, and
// streams a zip. The author opens it in Unity and hits Build — the game talks
// to the same server the IDE writes to (online mode).
//
//	POST /v1/export   body: {name, bundleId, company, serverUrl, askName}
package main

import (
	"archive/zip"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"regexp"
	"strings"
)

// directories inside the template that must never ship in an export.
var exportSkipDirs = map[string]bool{
	"Library": true, "Temp": true, "Logs": true, "obj": true, "Build": true,
	"Builds": true, "UserSettings": true, ".git": true, ".vs": true, ".idea": true,
}

type exportConfig struct {
	Name      string `json:"name"`
	BundleID  string `json:"bundleId"`
	Company   string `json:"company"`
	ServerURL string `json:"serverUrl"`
	AskName   bool   `json:"askName"`
	Offline   bool   `json:"offline"` // bundle content into StreamingAssets (no server needed)
}

// where bundled content lives inside the exported project, and the URL prefixes
// the engine asks for (mirrored under it so file:// reads resolve).
const bundleDir = "Assets/StreamingAssets/lvn"

// resolveTemplate finds the sandbox template dir, trying a few cwd-relative
// candidates so it works whether the server runs from the repo root or server/.
func resolveTemplate(flagDir string) string {
	for _, c := range []string{flagDir, "./sandbox", "sandbox", "../sandbox"} {
		if c == "" {
			continue
		}
		if fi, err := os.Stat(filepath.Join(c, "Assets")); err == nil && fi.IsDir() {
			return c
		}
	}
	return flagDir
}

func sanitizeName(s, fallback string) string {
	s = strings.TrimSpace(s)
	re := regexp.MustCompile(`[^A-Za-z0-9 _.-]+`)
	s = re.ReplaceAllString(s, "")
	s = strings.TrimSpace(s)
	if s == "" {
		return fallback
	}
	return s
}

func (s *server) handleExport(w http.ResponseWriter, r *http.Request) {
	// Export bundles the ENTIRE content directory — gate it behind the admin
	// token like every other privileged endpoint, or it leaks all content.
	if s.adminToken == "" {
		http.Error(w, "admin disabled", http.StatusForbidden)
		return
	}
	if !bearerOK(r, s.adminToken) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	var cfg exportConfig
	if err := json.NewDecoder(io.LimitReader(r.Body, 1<<20)).Decode(&cfg); err != nil && err != io.EOF {
		http.Error(w, "bad body", http.StatusBadRequest)
		return
	}
	if cfg.ServerURL == "" {
		// default to the host the request came in on, so the exported game points
		// back here unless the author overrides it.
		scheme := "http"
		if r.TLS != nil {
			scheme = "https"
		}
		cfg.ServerURL = scheme + "://" + r.Host
	}
	name := sanitizeName(cfg.Name, "LvnGame")
	folder := strings.ReplaceAll(name, " ", "")
	if folder == "" {
		folder = "LvnGame"
	}

	tmpl := resolveTemplate(s.templateDir)
	if _, err := os.Stat(filepath.Join(tmpl, "Assets")); err != nil {
		http.Error(w, "export template not found (sandbox project missing)", http.StatusInternalServerError)
		return
	}

	w.Header().Set("Content-Type", "application/zip")
	w.Header().Set("Content-Disposition", fmt.Sprintf(`attachment; filename="%s.zip"`, folder))
	zw := zip.NewWriter(w)
	defer zw.Close()

	bootRel := filepath.Join("Assets", "Sandbox", "Boot.cs")
	settingsRel := filepath.Join("ProjectSettings", "ProjectSettings.asset")
	manifestRel := filepath.Join("Packages", "manifest.json")
	lockRel := filepath.Join("Packages", "packages-lock.json")

	_ = filepath.Walk(tmpl, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			return nil
		}
		rel, rerr := filepath.Rel(tmpl, path)
		if rerr != nil {
			return nil
		}
		if rel == "." {
			return nil
		}
		// skip excluded top-level dirs (and everything under them)
		top := strings.SplitN(filepath.ToSlash(rel), "/", 2)[0]
		if info.IsDir() {
			if exportSkipDirs[top] {
				return filepath.SkipDir
			}
			return nil
		}
		if exportSkipDirs[top] {
			return nil
		}

		// the lock file pins the engine's local file: path — drop it so Unity
		// re-resolves against the git URL we write into the manifest.
		if rel == lockRel {
			return nil
		}

		var data []byte
		switch rel {
		case bootRel:
			data = []byte(bootSource(cfg))
		case settingsRel:
			raw, _ := os.ReadFile(path)
			data = patchProjectSettings(raw, cfg)
		case manifestRel:
			raw, _ := os.ReadFile(path)
			data = patchManifest(raw)
		default:
			raw, derr := os.ReadFile(path)
			if derr != nil {
				return nil
			}
			data = raw
		}

		zf, cerr := zw.Create(folder + "/" + filepath.ToSlash(rel))
		if cerr != nil {
			return nil
		}
		_, _ = zf.Write(data)
		return nil
	})

	// Offline build: bake the novel's content into StreamingAssets, mirroring the
	// server's URL paths so the engine reads it via file:// with no network.
	if cfg.Offline {
		s.bundleContent(zw, folder)
	}

	// a short README so the author knows what to do with the zip.
	if zf, err := zw.Create(folder + "/HOW_TO_BUILD.md"); err == nil {
		zf.Write([]byte(buildReadme(cfg, name)))
	}
}

// bundleContent copies the content dir into StreamingAssets and writes the
// manifest + version index at the exact paths the engine requests, so an
// offline build resolves everything locally.
func (s *server) bundleContent(zw *zip.Writer, folder string) {
	base := folder + "/" + bundleDir

	// every served file → StreamingAssets/lvn/content/<rel>
	_ = filepath.Walk(s.content, func(path string, info os.FileInfo, err error) error {
		if err != nil || info.IsDir() {
			return nil
		}
		rel, rerr := filepath.Rel(s.content, path)
		if rerr != nil {
			return nil
		}
		raw, derr := os.ReadFile(path)
		if derr != nil {
			return nil
		}
		if zf, cerr := zw.Create(base + "/content/" + filepath.ToSlash(rel)); cerr == nil {
			zf.Write(raw)
		}
		return nil
	})

	// the manifest at the engine's API path: GET /v1/content/manifest
	if raw, err := os.ReadFile(filepath.Join(s.content, "manifest.json")); err == nil {
		if zf, cerr := zw.Create(base + "/v1/content/manifest"); cerr == nil {
			zf.Write(raw)
		}
	}

	// the version index: GET /content/asset-versions.json (optional, but matches online behaviour)
	if data, err := json.Marshal(s.computeVersions(false)); err == nil {
		if zf, cerr := zw.Create(base + "/content/asset-versions.json"); cerr == nil {
			zf.Write(data)
		}
	}
}

// bootSource renders the standalone Boot.cs with the author's settings.
func bootSource(cfg exportConfig) string {
	ask := "false"
	if cfg.AskName {
		ask = "true"
	}
	offline := "false"
	if cfg.Offline {
		offline = "true"
	}
	// Emit the URL as a JSON string literal: json.Marshal escapes ", \ and
	// control chars, which are all valid C# string escapes too — so a crafted
	// serverUrl can't break out of the literal and inject code into Boot.cs.
	urlLit, _ := json.Marshal(cfg.ServerURL)
	return `using UnityEngine;
using UnityEngine.EventSystems;
using Lvn.UI.Screens;

namespace Game
{
    // Generated by the ELVIN IDE export. Boots the novel against the
    // configured server. Build from Unity (File ▸ Build Settings ▸ Build).
    public static class Boot
    {
        public const string ServerUrl = ` + string(urlLit) + `;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Run()
        {
            if (Object.FindFirstObjectByType<NovelApp>() != null) return;

            if (Object.FindFirstObjectByType<Camera>() == null)
            {
                var camGo = new GameObject("Main Camera");
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                camGo.tag = "MainCamera";
                Object.DontDestroyOnLoad(camGo);
            }

            if (Object.FindFirstObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
                Object.DontDestroyOnLoad(es);
            }

            var go = new GameObject("NovelApp");
            var app = go.AddComponent<NovelApp>();
            app.ServerUrl = ServerUrl;
            app.OfflineBundled = ` + offline + `;
            app.AskName = ` + ask + `;
            app.SyncInterval = 30f;
            app.ThemeResourcePath = "UI/AppLoading/UnityDefaultRuntimeTheme";
            Object.DontDestroyOnLoad(go);
        }
    }
}
`
}

// patchProjectSettings rewrites product/company name and bundle id in the
// ProjectSettings.asset YAML.
func patchProjectSettings(raw []byte, cfg exportConfig) []byte {
	out := string(raw)
	name := sanitizeName(cfg.Name, "LvnGame")
	company := sanitizeName(cfg.Company, "LvnStudio")
	out = regexp.MustCompile(`(?m)^  productName:.*$`).ReplaceAllString(out, "  productName: "+yamlScalar(name))
	out = regexp.MustCompile(`(?m)^  companyName:.*$`).ReplaceAllString(out, "  companyName: "+yamlScalar(company))
	if cfg.BundleID != "" {
		id := sanitizeName(cfg.BundleID, "")
		if id != "" {
			repl := "  applicationIdentifier:\n    Standalone: " + yamlScalar(id)
			out = regexp.MustCompile(`(?m)^  applicationIdentifier:.*$`).ReplaceAllString(out, repl)
		}
	}
	return []byte(out)
}

// engineGitURL is the public UPM git reference for the engine package, used in
// exports so the project resolves on any machine (the sandbox uses a local
// file: path that only works inside this repo).
const engineGitURL = "https://github.com/fomeanator/unity-lvn-vn-engine.git?path=/unity/Packages/com.lvn.engine"

// patchManifest swaps the engine's local file: dependency for the public git
// URL so a downloaded project resolves the package on open.
func patchManifest(raw []byte) []byte {
	re := regexp.MustCompile(`"com\.lvn\.engine"\s*:\s*"[^"]*"`)
	out := re.ReplaceAllString(string(raw), `"com.lvn.engine": "`+engineGitURL+`"`)
	return []byte(out)
}

func yamlScalar(s string) string {
	if strings.ContainsAny(s, ":#") {
		return `"` + strings.ReplaceAll(s, `"`, `\"`) + `"`
	}
	return s
}

func buildReadme(cfg exportConfig, name string) string {
	head := "# " + name + "\n\n" +
		"Exported from ELVIN IDE. This is a complete Unity project.\n\n" +
		"## Build\n" +
		"1. Open this folder in Unity (the engine package is pulled automatically).\n" +
		"2. File ▸ Build Settings ▸ pick a platform ▸ Build.\n\n"
	if cfg.Offline {
		return head + "## Content\n" +
			"The novel is bundled inside the game (StreamingAssets). It runs fully\n" +
			"offline — no server needed. Re-export to update the content.\n"
	}
	return head + "## Content\n" +
		"The game loads its novel from your server at:\n\n    " + cfg.ServerURL + "\n\n" +
		"Keep that server running (and reachable) for players. Edit chapters in the\n" +
		"authoring panel and they update live.\n"
}
