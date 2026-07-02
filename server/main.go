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
	"crypto/sha256"
	"crypto/subtle"
	"encoding/hex"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"sync"
	"time"
)

// idRe is the safe character set for a title id: it becomes a path segment
// (scripts/<id>.lvn, art/…) so anything outside this set could escape the
// content root or produce a surprising filename.
var idRe = regexp.MustCompile(`^[A-Za-z0-9_-]+$`)

func validID(id string) bool { return idRe.MatchString(id) }

// bearerOK compares the request's bearer token to the expected one in constant
// time, so a wrong token can't be recovered byte-by-byte via response timing.
func bearerOK(r *http.Request, token string) bool {
	got := strings.TrimPrefix(r.Header.Get("Authorization"), "Bearer ")
	return subtle.ConstantTimeCompare([]byte(got), []byte(token)) == 1
}

func main() {
	addr := flag.String("addr", ":8000", "listen address")
	contentDir := flag.String("content", "./content", "content directory (manifest.json + assets)")
	adminToken := flag.String("admin-token", "", "bearer token for /v1/admin/* (empty disables admin)")
	stateToken := flag.String("state-token", "", "bearer token required for /v1/state (empty = open; set in production)")
	importRoot := flag.String("import-root", "", "when set, JSON {dir} imports must live under this path (defence in depth)")
	templateDir := flag.String("template", "./sandbox", "Unity project template used by /v1/export")
	flag.Parse()

	if err := os.MkdirAll(*contentDir, 0o755); err != nil {
		log.Fatalf("content dir: %v", err)
	}
	srv := &server{
		content:     *contentDir,
		adminToken:  *adminToken,
		stateToken:  *stateToken,
		importRoot:  *importRoot,
		templateDir: *templateDir,
		state:       map[string]stateEntry{},
	}

	mux := http.NewServeMux()
	mux.HandleFunc("/healthz", func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
	})
	mux.HandleFunc("/v1/content/manifest", srv.handleManifest)
	// Content-version index (path -> sha256), computed live so cache-busting
	// works the moment a file changes. Registered before the static prefix so
	// the exact path wins.
	mux.HandleFunc("/content/asset-versions.json", srv.handleAssetVersions)
	mux.HandleFunc("/v1/content/version", srv.handleVersion)
	mux.Handle("/content/", srv.contentHandler(*contentDir))
	mux.HandleFunc("/v1/state", srv.handleState)
	mux.HandleFunc("/v1/admin/assets/", srv.handleAdminAsset)
	mux.HandleFunc("/v1/admin/import-articy", srv.handleImportArticy)
	mux.HandleFunc("/v1/export", srv.handleExport)

	// Serve the authoring panel (the lvns playground + reference + save-to-app)
	// at /panel; also kept at / for convenience.
	webDir := "./website"
	if _, err := os.Stat(webDir); os.IsNotExist(err) {
		webDir = "server/website"
	}
	site := http.FileServer(http.Dir(webDir))
	mux.Handle("/panel/", http.StripPrefix("/panel/", site))
	mux.HandleFunc("/panel", func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, "/panel/", http.StatusFound)
	})
	mux.Handle("/", site)

	log.Printf("LVN server on %s, content=%s, admin=%v, state-auth=%v", *addr, *contentDir, *adminToken != "", *stateToken != "")
	// Explicit timeouts so a slow/idle client (Slowloris) can't tie up a
	// connection indefinitely. WriteTimeout is left unset because /v1/export
	// streams a whole Unity project zip that can legitimately take minutes.
	httpSrv := &http.Server{
		Addr:              *addr,
		Handler:           withLog(mux),
		ReadHeaderTimeout: 15 * time.Second,
		ReadTimeout:       5 * time.Minute, // large multipart articy uploads
		IdleTimeout:       120 * time.Second,
	}
	log.Fatal(httpSrv.ListenAndServe())
}

// stateEntry is one player save plus its server-owned monotonic version. The
// version lets a client detect that another device wrote since it last read
// (optimistic concurrency) instead of silently last-write-wins clobbering.
type stateEntry struct {
	body    []byte
	version int64
}

type server struct {
	content     string
	adminToken  string
	stateToken  string
	importRoot  string
	templateDir string
	mu          sync.RWMutex
	state       map[string]stateEntry // user id -> save + version
	stateOrder  []string              // insertion order, for bounded eviction

	verMu    sync.Mutex
	verCache map[bool]verCacheEntry // includeManifest -> cached versions
}

// stateMemMax bounds how many player saves are held in RAM. Disk is the source
// of truth (every GET falls back to the on-disk mirror), so evicting the oldest
// in-memory entry is safe — it just reloads on next access. Prevents an
// unauthenticated PUT loop from exhausting the heap.
const stateMemMax = 2000

type verCacheEntry struct {
	versions map[string]string
	at       time.Time
}

// verCacheTTL bounds how often the whole content tree is walked+hashed. Many
// clients polling /v1/content/version within this window share one walk, so a
// poll storm can't amplify into repeated full-tree scans. A change is still
// visible within the TTL, which is well inside the client's poll cadence.
const verCacheTTL = 2 * time.Second

func (s *server) handleManifest(w http.ResponseWriter, r *http.Request) {
	data, err := os.ReadFile(filepath.Join(s.content, "manifest.json"))
	if err != nil {
		// A fresh install has no manifest yet — return an empty one, not a 500.
		writeJSON(w, http.StatusOK, map[string]any{"titles": []any{}})
		return
	}
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store") // the manifest is the live index
	w.Write(data)
}

// contentHandler serves static content with cache rules that match the engine's
// cache-busting design: .lvn scripts are live (no-store), every other asset is
// versioned (immutable, long-lived) — a changed asset gets a new ?v= and so a
// new URL, so it never serves stale.
func (s *server) contentHandler(dir string) http.Handler {
	fs := http.StripPrefix("/content/", http.FileServer(http.Dir(dir)))
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if strings.HasSuffix(strings.ToLower(r.URL.Path), ".lvn") {
			w.Header().Set("Cache-Control", "no-store")
		} else {
			w.Header().Set("Cache-Control", "public, max-age=604800, immutable")
		}
		fs.ServeHTTP(w, r)
	})
}

// computeVersions returns {content-relative-path: sha256} for every served
// file. includeManifest folds manifest.json into the map (used by the version
// endpoint so manifest edits register), otherwise it's left out (the asset
// index is for art/scripts; the manifest is fetched fresh, never versioned).
func (s *server) computeVersions(includeManifest bool) map[string]string {
	out := map[string]string{}
	_ = filepath.Walk(s.content, func(path string, info os.FileInfo, err error) error {
		if err != nil || info.IsDir() {
			return nil
		}
		rel, rerr := filepath.Rel(s.content, path)
		if rerr != nil {
			return nil
		}
		rel = filepath.ToSlash(rel)
		if rel == "asset-versions.json" || (rel == "manifest.json" && !includeManifest) {
			return nil
		}
		data, derr := os.ReadFile(path)
		if derr != nil {
			return nil
		}
		sum := sha256.Sum256(data)
		out[rel] = hex.EncodeToString(sum[:])
		return nil
	})
	return out
}

// computeVersionsCached memoises computeVersions for verCacheTTL so a burst of
// version polls collapses into a single tree walk. The returned map is shared
// and must be treated as read-only.
func (s *server) computeVersionsCached(includeManifest bool) map[string]string {
	s.verMu.Lock()
	defer s.verMu.Unlock()
	if s.verCache == nil {
		s.verCache = map[bool]verCacheEntry{}
	}
	if e, ok := s.verCache[includeManifest]; ok && time.Since(e.at) < verCacheTTL {
		return e.versions
	}
	v := s.computeVersions(includeManifest)
	s.verCache[includeManifest] = verCacheEntry{versions: v, at: time.Now()}
	return v
}

// handleAssetVersions returns {content-relative-path: sha256} for every served
// asset/script. The client folds these hashes into its disk cache key and the
// ?v= query, so re-uploaded content auto-invalidates.
func (s *server) handleAssetVersions(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	json.NewEncoder(w).Encode(s.computeVersionsCached(false))
}

// handleVersion returns a single content version hash that changes whenever ANY
// served file (manifest, scripts, assets) changes — the cheap poll the client
// uses to detect "something changed" before pulling the delta. Supports ETag /
// If-None-Match so an unchanged poll is a zero-body 304.
func versionHash(versions map[string]string) string {
	keys := make([]string, 0, len(versions))
	for k := range versions {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	h := sha256.New()
	for _, k := range keys {
		h.Write([]byte(k))
		h.Write([]byte{0})
		h.Write([]byte(versions[k]))
		h.Write([]byte{0})
	}
	return hex.EncodeToString(h.Sum(nil))
}

func (s *server) handleVersion(w http.ResponseWriter, r *http.Request) {
	sum := versionHash(s.computeVersionsCached(true))
	etag := `"` + sum + `"`
	w.Header().Set("ETag", etag)
	w.Header().Set("Cache-Control", "no-store")
	if r.Header.Get("If-None-Match") == etag {
		w.WriteHeader(http.StatusNotModified)
		return
	}
	writeJSON(w, http.StatusOK, map[string]string{"version": sum})
}

func (s *server) handleState(w http.ResponseWriter, r *http.Request) {
	// Optional shared-secret gate. Empty state-token keeps the template open for
	// local dev; production sets it so a stranger can't read/overwrite saves.
	if s.stateToken != "" && !bearerOK(r, s.stateToken) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	user := r.URL.Query().Get("user")
	if user == "" {
		http.Error(w, "user query param required", http.StatusBadRequest)
		return
	}
	// Per-blob key, trust-on-first-use: the first PUT that carries X-State-Key
	// claims the blob (its hash is stored beside the save); every later access
	// must present the same key. The user id travels in the URL — which proxies
	// and access logs record — so the id alone must not be enough to read or
	// overwrite a stranger's save. Unclaimed blobs stay open (legacy clients).
	key := r.Header.Get("X-State-Key")
	if !s.stateKeyOK(user, key, r.Method == http.MethodPut) {
		http.Error(w, "state key mismatch", http.StatusUnauthorized)
		return
	}
	switch r.Method {
	case http.MethodGet:
		entry, ok := s.loadState(user)
		if !ok {
			http.Error(w, "no save", http.StatusNotFound)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		w.Write(withVersion(entry.body, entry.version))
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
		// Optimistic concurrency: a client that sends its last-seen `_version`
		// gets a 409 (with the current doc) when another device wrote in between,
		// so it can merge instead of silently clobbering hours of progress. A
		// legacy client that sends no version keeps the old last-write-wins.
		clientVer, hasVer := extractVersion(body)
		cur, exists := s.loadState(user)
		if hasVer && exists && clientVer != cur.version {
			writeJSON409(w, cur)
			return
		}
		next := stateEntry{body: stripVersion(body), version: cur.version + 1}
		// Persist to disk FIRST (atomically) so a reported success is durable — the
		// client trusts {saved:true} and won't retry, so a failed write must 500.
		if err := s.writeStateFile(user, next); err != nil {
			fmt.Fprintf(os.Stderr, "state: persist %s: %v\n", user, err)
			http.Error(w, "persist failed", http.StatusInternalServerError)
			return
		}
		s.putState(user, next)
		writeJSON(w, http.StatusOK, map[string]any{"saved": true, "version": next.version})
	default:
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
	}
}

// stateKeyOK enforces the per-blob TOFU key. Rules:
//   - blob unclaimed (no .key sidecar): GET is open; a PUT WITH a key claims
//     the blob; a PUT without one stays legacy-open.
//   - blob claimed: both methods require the matching key.
func (s *server) stateKeyOK(user, key string, isPut bool) bool {
	keyPath := s.stateFile(user) + ".key"
	stored, err := os.ReadFile(keyPath)
	if err != nil { // unclaimed
		if isPut && key != "" {
			sum := sha256.Sum256([]byte(key))
			if werr := os.MkdirAll(filepath.Dir(keyPath), 0o755); werr == nil {
				_ = atomicWrite(keyPath, []byte(hex.EncodeToString(sum[:])), 0o600)
			}
		}
		return true
	}
	if key == "" {
		return false
	}
	sum := sha256.Sum256([]byte(key))
	return subtle.ConstantTimeCompare(stored, []byte(hex.EncodeToString(sum[:]))) == 1
}

// loadState returns a user's save from memory, falling back to the on-disk
// mirror (which survives a restart).
func (s *server) loadState(user string) (stateEntry, bool) {
	s.mu.RLock()
	entry, ok := s.state[user]
	s.mu.RUnlock()
	if ok {
		return entry, true
	}
	b, err := os.ReadFile(s.stateFile(user))
	if err != nil {
		return stateEntry{}, false
	}
	entry = decodeStateFile(b)
	s.putState(user, entry)
	return entry, true
}

// On disk a save is wrapped as {"__v":N,"doc":{…}} so the version survives a
// restart. Legacy files (raw client JSON) read as version 0.
type stateWrapper struct {
	V   int64           `json:"__v"`
	Doc json.RawMessage `json:"doc"`
}

func encodeStateFile(e stateEntry) []byte {
	b, _ := json.Marshal(stateWrapper{V: e.version, Doc: e.body})
	return b
}

func decodeStateFile(b []byte) stateEntry {
	var w stateWrapper
	if err := json.Unmarshal(b, &w); err == nil && len(w.Doc) > 0 {
		return stateEntry{body: w.Doc, version: w.V}
	}
	return stateEntry{body: b, version: 0} // legacy: the raw doc itself
}

// withVersion returns the doc with "_version" injected at the top level, so a
// GET hands the client the token it must echo on its next PUT.
func withVersion(doc []byte, version int64) []byte {
	var m map[string]any
	if err := json.Unmarshal(doc, &m); err != nil || m == nil {
		return doc // not an object — serve as-is (no version support)
	}
	m["_version"] = version
	out, err := json.Marshal(m)
	if err != nil {
		return doc
	}
	return out
}

// extractVersion pulls the client-echoed "_version" from a PUT body.
func extractVersion(body []byte) (int64, bool) {
	var m map[string]json.RawMessage
	if err := json.Unmarshal(body, &m); err != nil {
		return 0, false
	}
	raw, ok := m["_version"]
	if !ok {
		return 0, false
	}
	var v int64
	if err := json.Unmarshal(raw, &v); err != nil {
		return 0, false
	}
	return v, true
}

// stripVersion removes the transport-only "_version" field before storing.
func stripVersion(body []byte) []byte {
	var m map[string]json.RawMessage
	if err := json.Unmarshal(body, &m); err != nil {
		return body
	}
	if _, ok := m["_version"]; !ok {
		return body
	}
	delete(m, "_version")
	out, err := json.Marshal(m)
	if err != nil {
		return body
	}
	return out
}

// writeJSON409 answers a version conflict with the winning doc + its version,
// so the client can merge and retry without a second round-trip.
func writeJSON409(w http.ResponseWriter, cur stateEntry) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusConflict)
	resp := map[string]any{"error": "version_conflict", "version": cur.version}
	var doc any
	if err := json.Unmarshal(cur.body, &doc); err == nil {
		resp["doc"] = doc
	}
	json.NewEncoder(w).Encode(resp)
}

// putState caches a save in memory with a bounded size, evicting the oldest
// entry when full. Disk remains authoritative, so eviction only costs a reload.
func (s *server) putState(user string, entry stateEntry) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if _, exists := s.state[user]; !exists {
		s.stateOrder = append(s.stateOrder, user)
		for len(s.stateOrder) > stateMemMax {
			oldest := s.stateOrder[0]
			s.stateOrder = s.stateOrder[1:]
			delete(s.state, oldest)
		}
	}
	s.state[user] = entry
}

// stateFile is the on-disk mirror path for a user's save, under <content>/state/.
// The user key (may carry a "<uid>__<title>" composite) is sanitised into a safe
// filename.
func (s *server) stateFile(user string) string {
	safe := make([]rune, 0, len(user))
	for _, r := range user {
		switch {
		case r == '-' || r == '_' || r == '.' ||
			(r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') || (r >= '0' && r <= '9'):
			safe = append(safe, r)
		default:
			safe = append(safe, '_')
		}
	}
	return filepath.Join(s.content, "state", string(safe)+".json")
}

func (s *server) writeStateFile(user string, entry stateEntry) error {
	p := s.stateFile(user)
	if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
		return err
	}
	return atomicWrite(p, encodeStateFile(entry), 0o644)
}

func (s *server) handleAdminAsset(w http.ResponseWriter, r *http.Request) {
	if s.adminToken == "" {
		http.Error(w, "admin disabled", http.StatusForbidden)
		return
	}
	if !bearerOK(r, s.adminToken) {
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
	if err := atomicWrite(dst, body, 0o644); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"path": rel, "bytes": len(body)})
}

// atomicWrite writes via a temp file in the same directory then renames, so a
// concurrent reader (e.g. computeVersions hashing for cache-busting) never sees
// a half-written or zero-byte file. Rename is atomic on the same filesystem.
func atomicWrite(dst string, body []byte, perm os.FileMode) error {
	tmp, err := os.CreateTemp(filepath.Dir(dst), ".tmp-*")
	if err != nil {
		return err
	}
	tmpName := tmp.Name()
	if _, err := tmp.Write(body); err != nil {
		tmp.Close()
		os.Remove(tmpName)
		return err
	}
	if err := tmp.Close(); err != nil {
		os.Remove(tmpName)
		return err
	}
	if err := os.Chmod(tmpName, perm); err != nil {
		os.Remove(tmpName)
		return err
	}
	if err := os.Rename(tmpName, dst); err != nil {
		os.Remove(tmpName)
		return err
	}
	return nil
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
