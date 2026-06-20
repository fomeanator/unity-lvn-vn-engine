// LVN server template — a minimal, dependency-free content + state backend.
//
// It is deliberately small: enough to serve a game's content manifest, its
// .lvn scripts and assets, and per-player saves, with an optional token-gated
// admin upload that mirrors the lvnconv pipeline (compile a .lvn, PUT it, the
// client picks it up). Swap the in-memory store for a database when you grow.
//
//	go run . -content ./content -addr :8000 -admin-token secret
//
// Routes:
//
//	GET  /healthz                       liveness
//	GET  /v1/content/manifest           the content manifest (content/manifest.json)
//	GET  /content/<path>                static .lvn / art / audio
//	GET  /v1/state?user=<id>            player save (JSON; 404 if none)
//	PUT  /v1/state?user=<id>            store player save (body = JSON)
//	PUT  /v1/admin/assets/<path>        upload an asset/script (admin token)
package main

import (
	"encoding/json"
	"flag"
	"io"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"sync"
)

func main() {
	addr := flag.String("addr", ":8000", "listen address")
	contentDir := flag.String("content", "./content", "content directory (manifest.json + assets)")
	adminToken := flag.String("admin-token", "", "bearer token for /v1/admin/* (empty disables admin)")
	flag.Parse()

	if err := os.MkdirAll(*contentDir, 0o755); err != nil {
		log.Fatalf("content dir: %v", err)
	}
	srv := &server{content: *contentDir, adminToken: *adminToken, state: map[string][]byte{}}

	mux := http.NewServeMux()
	mux.HandleFunc("/healthz", func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
	})
	mux.HandleFunc("/v1/content/manifest", srv.handleManifest)
	mux.Handle("/content/", http.StripPrefix("/content/", http.FileServer(http.Dir(*contentDir))))
	mux.HandleFunc("/v1/state", srv.handleState)
	mux.HandleFunc("/v1/admin/assets/", srv.handleAdminAsset)

	// Serve the static documentation website
	webDir := "./website"
	if _, err := os.Stat(webDir); os.IsNotExist(err) {
		webDir = "server/website"
	}
	mux.Handle("/", http.FileServer(http.Dir(webDir)))

	log.Printf("LVN server on %s, content=%s, admin=%v", *addr, *contentDir, *adminToken != "")
	log.Fatal(http.ListenAndServe(*addr, withLog(mux)))
}

type server struct {
	content    string
	adminToken string
	mu         sync.RWMutex
	state      map[string][]byte // user id -> raw save JSON
}

func (s *server) handleManifest(w http.ResponseWriter, r *http.Request) {
	data, err := os.ReadFile(filepath.Join(s.content, "manifest.json"))
	if err != nil {
		// A fresh install has no manifest yet — return an empty one, not a 500.
		writeJSON(w, http.StatusOK, map[string]any{"novels": []any{}})
		return
	}
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store") // the manifest is the live index
	w.Write(data)
}

func (s *server) handleState(w http.ResponseWriter, r *http.Request) {
	user := r.URL.Query().Get("user")
	if user == "" {
		http.Error(w, "user query param required", http.StatusBadRequest)
		return
	}
	switch r.Method {
	case http.MethodGet:
		s.mu.RLock()
		data, ok := s.state[user]
		s.mu.RUnlock()
		if !ok {
			http.Error(w, "no save", http.StatusNotFound)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		w.Write(data)
	case http.MethodPut:
		body, err := io.ReadAll(io.LimitReader(r.Body, 1<<20))
		if err != nil {
			http.Error(w, "read body", http.StatusBadRequest)
			return
		}
		if !json.Valid(body) {
			http.Error(w, "body must be JSON", http.StatusBadRequest)
			return
		}
		s.mu.Lock()
		s.state[user] = body
		s.mu.Unlock()
		writeJSON(w, http.StatusOK, map[string]bool{"saved": true})
	default:
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
	}
}

func (s *server) handleAdminAsset(w http.ResponseWriter, r *http.Request) {
	if s.adminToken == "" {
		http.Error(w, "admin disabled", http.StatusForbidden)
		return
	}
	if r.Header.Get("Authorization") != "Bearer "+s.adminToken {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	if r.Method != http.MethodPut {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	rel := strings.TrimPrefix(r.URL.Path, "/v1/admin/assets/")
	if rel == "" || strings.Contains(rel, "..") {
		http.Error(w, "bad path", http.StatusBadRequest)
		return
	}
	dst := filepath.Join(s.content, filepath.Clean(rel))
	if err := os.MkdirAll(filepath.Dir(dst), 0o755); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	body, err := io.ReadAll(io.LimitReader(r.Body, 64<<20))
	if err != nil {
		http.Error(w, "read body", http.StatusBadRequest)
		return
	}
	if err := os.WriteFile(dst, body, 0o644); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"path": rel, "bytes": len(body)})
}

func writeJSON(w http.ResponseWriter, code int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(code)
	json.NewEncoder(w).Encode(v)
}

func withLog(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		log.Printf("%s %s", r.Method, r.URL.Path)
		next.ServeHTTP(w, r)
	})
}
