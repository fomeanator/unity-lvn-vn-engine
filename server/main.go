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
	"bytes"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/hex"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"io/fs"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
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
	adminUser := flag.String("admin-user", "", "завести/обновить учётку панели: логин:пароль[:роль] и выйти")
	iapDev := flag.Bool("iap-dev", false, "accept any IAP receipt (test builds only — never production)")
	walletEarn := flag.Bool("wallet-earn", true, "allow client-initiated POST /v1/wallet/earn (test-mode affordance; set to false before enabling real payments)")
	authDev := flag.Bool("auth-dev", false, "accept the 'dev' auth provider (test builds only — never production)")
	googleClientID := flag.String("google-client-id", "", "pin Google id_token audience for /v1/auth/link|login")
	appleBundleID := flag.String("apple-bundle-id", "", "pin Apple identity-token audience for /v1/auth/link|login")
	appleSharedSecret := flag.String("apple-shared-secret", "", "App Store shared secret for /v1/iap/verify receipt validation")
	stateToken := flag.String("state-token", "", "bearer token required for /v1/state (empty = open; set in production)")
	importRoot := flag.String("import-root", "", "when set, JSON {dir} imports must live under this path (defence in depth)")
	templateDir := flag.String("template", "./sandbox", "Unity project template used by /v1/export")
	studio := flag.Bool("studio", false, "serve the Elvin Studio web app (authoring IDE + admin UI) at /; without it the server is a pure game API (content, state, product services)")
	flag.Parse()

	// ЗАВЕДЕНИЕ ПЕРВОГО ЧЕЛОВЕКА. Панель с учётками нельзя открыть, пока в
	// ней никого нет, а завести первого через саму панель — значит оставить
	// дыру «кто первый пришёл, тот и владелец». Поэтому только так: рукой,
	// на машине, где стоит сервер.
	//
	//   lvn-server -content ./content -admin-user аня:пароль123:owner
	//
	if *adminUser != "" {
		parts := strings.SplitN(*adminUser, ":", 3)
		if len(parts) < 2 {
			log.Fatal("формат: -admin-user логин:пароль[:роль]")
		}
		role := RoleEditor
		if len(parts) == 3 && parts[2] != "" {
			role = parts[2]
		}
		users, err := NewAdminUsers(adminUsersDir(*contentDir))
		if err != nil {
			log.Fatalf("не открыть хранилище учёток: %v", err)
		}
		if err := users.SetUser(parts[0], parts[1], role); err != nil {
			log.Fatalf("не завести учётку: %v", err)
		}
		log.Printf("учётка «%s» (%s) готова — запустите сервер обычным образом", parts[0], role)
		return
	}

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
	srv.routesDocs(mux) // /docs (Swagger UI) + /openapi.yaml, embedded in the binary
	mux.HandleFunc("/v1/content/manifest", srv.handleManifest)
	// Content-version index (path -> sha256), computed live so cache-busting
	// works the moment a file changes. Registered before the static prefix so
	// the exact path wins.
	mux.HandleFunc("/content/asset-versions.json", srv.handleAssetVersions)
	mux.HandleFunc("/v1/content/version", srv.handleVersion)
	mux.HandleFunc("/v1/content/changes", srv.handleContentChanges)
	ds := newDownscaler() // shared: withDownscale + withKTX2 (@2k source materialization)
	mux.Handle("/content/", srv.withKTX2(ds, srv.withDownscale(ds, srv.contentHandler(*contentDir))))
	mux.HandleFunc("/v1/state", srv.handleState)

	// Product services — auth, wallet/IAP, analytics. Modular by design: each
	// owns its store under <content>/services and registers its own routes, so
	// promoting one to a separate process is a file move, not a rewrite.
	servicesDir := filepath.Join(*contentDir, "services")
	// База: аккаунты (и дальше кошельки). Файл рядом со служебными данными,
	// демона нет. Не открылась — это отказ старта, а не повод тихо уйти на
	// старый путь: молчаливый откат означал бы запись в два разных места.
	db, err := openStore(servicesDir)
	if err != nil {
		log.Fatalf("store: %v", err)
	}
	defer db.Close()
	authSvc, err := NewAuthServiceDB(servicesDir, db)
	if err != nil {
		log.Fatalf("auth service: %v", err)
	}
	authSvc.AuthDev = *authDev
	authSvc.GoogleClientID = *googleClientID
	authSvc.AppleBundleID = *appleBundleID
	// One shared title→author index: attribution is stamped at write time, and
	// both the wallet and analytics need the same answer to the same question.
	owners := newOwnerIndex(filepath.Join(*contentDir, "manifest.json"))
	walletSvc, err := NewWalletService(filepath.Join(servicesDir, "wallet"), db, authSvc,
		filepath.Join(*contentDir, "iap-catalog.json"), *iapDev, owners)
	if err != nil {
		log.Fatalf("wallet service: %v", err)
	}
	walletSvc.AppleSharedSecret = *appleSharedSecret
	walletSvc.AppleBundleID = *appleBundleID
	walletSvc.EarnDisabled = !*walletEarn
	// Production nudges: these pins are what stand between "verified" and
	// "any token/receipt from any app" — silence here has bitten people.
	// САМЫЕ ГРОМКИЕ РЕЖИМЫ БЫЛИ САМЫМИ ТИХИМИ. Про -wallet-earn сервер говорил,
	// про непривязанный bundle id говорил — а про два флага, которые отключают
	// проверку ЦЕЛИКОМ, молчал. Хуже: -auth-dev тут уже участвовал, но лишь
	// чтобы ПРИГЛУШИТЬ меньшее замечание строкой ниже.
	//
	// Замерено: с -iap-dev один и тот же чек, предъявленный трижды, начислил
	// втрое (500 → 1500) — защита от повтора стоит под условием, которое в этом
	// режиме не выполняется никогда. С -auth-dev любая строка становится
	// личностью, причём одна и та же строка — всегда одним и тем же игроком:
	// узнал чужую строку — стал этим человеком.
	if *iapDev {
		log.Printf("WARNING: -iap-dev is ON — ANY receipt is accepted and the same one can be replayed for unlimited grants. Never run production with it")
	}
	if *authDev {
		log.Printf("WARNING: -auth-dev is ON — ANY token is accepted as an identity, and the same string always maps to the same player. Never run production with it")
	}
	if *walletEarn {
		log.Printf("note: /v1/wallet/earn is OPEN — any signed-in device can credit itself (test mode). Set -wallet-earn=false before real IAP/ads payouts")
	}
	if *appleSharedSecret != "" && *appleBundleID == "" {
		log.Printf("WARNING: -apple-shared-secret is set but -apple-bundle-id is empty — receipts from OTHER apps would validate; set the bundle id in production")
	}
	if !*authDev && (*googleClientID == "" || *appleBundleID == "") {
		log.Printf("note: -google-client-id / -apple-bundle-id unset — platform token audience is not pinned (fine for dev, set both in production)")
	}
	analyticsSvc, err := NewAnalyticsService(filepath.Join(servicesDir, "analytics"), authSvc, *adminToken, owners)
	if err != nil {
		log.Fatalf("analytics service: %v", err)
	}
	// Отчёт о деньгах читает ведомость покупок из кошелька: активные игроки
	// живут в аналитике, а покупки — в кошельке, и без этой связки нельзя
	// посчитать ни конверсию, ни ARPU.
	analyticsSvc.payments = walletSvc
	dailySvc, err := NewDailyService(filepath.Join(servicesDir, "daily"), db, authSvc, walletSvc,
		filepath.Join(*contentDir, "daily-rewards.json"))
	if err != nil {
		log.Fatalf("daily service: %v", err)
	}
	lbSvc, err := NewLeaderboardService(filepath.Join(servicesDir, "leaderboards"), db, authSvc)
	if err != nil {
		log.Fatalf("leaderboard service: %v", err)
	}
	adsSvc, err := NewAdsService(filepath.Join(servicesDir, "ads"), db, authSvc, walletSvc,
		filepath.Join(*contentDir, "ads.json"))
	if err != nil {
		log.Fatalf("ads service: %v", err)
	}
	adminSvc := NewAdminService(*contentDir, *adminToken, authSvc, walletSvc)
	srv.admin = adminSvc // share the editorial write lock (see (*server).writeLock)
	clientLogSvc, err := NewClientLogService(filepath.Join(servicesDir, "client-logs"), *adminToken)
	if err != nil {
		log.Fatalf("client log service: %v", err)
	}
	clientLogSvc.Routes(mux)
	// Отзывы из игры: текст плюс контекст, который собирается сам (сборка,
	// глава, место в сценарии). Тестер в мессенджере ничего этого не назовёт.
	feedbackSvc, err := NewFeedbackService(filepath.Join(servicesDir, "feedback"),
		db, authSvc, *adminToken, analyticsSvc.chapters)
	if err != nil {
		log.Fatalf("feedback service: %v", err)
	}
	feedbackSvc.Routes(mux)
	// Эксперименты: развилка в сценарии, доля трафика и таргет — здесь.
	// Конфиг рядом с экономикой: и то и другое крутят, не пересобирая игру.
	expSvc := NewExperimentsService(filepath.Join(*contentDir, "experiments.json"), authSvc, *adminToken)
	expSvc.payments, expSvc.analytics = walletSvc, analyticsSvc
	expSvc.Routes(mux)
	lbSvc.Routes(mux)
	authSvc.Routes(mux)
	walletSvc.Routes(mux)
	analyticsSvc.Routes(mux)
	dailySvc.Routes(mux)
	adsSvc.Routes(mux)
	netSvc := NewNetService()
	netSvc.Routes(mux) // комнаты для игры вдвоём: сервер держит ящики, правила у клиентов
	adminSvc.Routes(mux)
	// «Удалить аккаунт» — стор-требование: игрок стирает свои данные сам.
	(&accountEraser{
		auth: authSvc,
		db:   db,
		userFileDirs: []string{
			filepath.Join(servicesDir, "wallet"),
			filepath.Join(servicesDir, "daily"),
			filepath.Join(servicesDir, "ads"),
			filepath.Join(servicesDir, "leaderboards"),
		},
		srv: srv,
	}).Routes(mux)
	mux.HandleFunc("/v1/admin/assets/", srv.handleAdminAsset)
	// «Что выглядит мылом»: разрешение арта по уже опубликованным главам.
	// Страж ловит это на входе, но прод набивался до стража.
	mux.HandleFunc("/v1/admin/art-quality", srv.handleArtQuality)
	// "Коннект": один самодостаточный файл для ИИ (доступ + весь язык) и
	// публикация .lvns одним запросом, чтобы для неё не требовался тулчейн.
	mux.HandleFunc("/v1/admin/agent-bundle", srv.handleAgentBundle)
	mux.HandleFunc("/v1/admin/agent/publish", srv.handleAgentPublish)
	// Пересборка глав, подключающих изменённый общий файл: без неё правка
	// механик не доезжала до игры — играется скомпилированный .lvn.
	mux.HandleFunc("/v1/admin/rebuild", srv.handleRebuild)
	mux.HandleFunc("/v1/admin/import-articy", srv.handleImportArticy)
	mux.HandleFunc("/v1/admin/import-bundle", srv.handleImportBundle)
	// The other half of a re-import: list what the three-way merge parked and
	// commit one side of it. Wired to adminSvc so a resolution takes the same
	// editorial write lock as a manifest/script save (import_conflicts.go).
	srv.routesImportConflicts(mux, adminSvc)
	mux.HandleFunc("/v1/admin/stage-extract", srv.handleStageExtract)
	mux.HandleFunc("/v1/admin/detect-roles", srv.handleDetectRoles)
	mux.HandleFunc("/v1/admin/staged-upload/", srv.handleStagedUpload)
	mux.HandleFunc("/v1/admin/spine", srv.handleAdminSpine)
	mux.HandleFunc("/v1/export", srv.handleExport)
	// Готовые сборки: команда забирает свежий APK из админки вместо пересылки
	// файла руками (builds.go).
	NewBuildsService(*contentDir, *adminToken).Routes(mux)

	// The Studio surface (authoring IDE + admin UI) is
	// opt-in: a game's production server is a pure API and has no reason to
	// expose an authoring web app. -studio serves it at / (and /panel).
	if *studio {
		webDir := "./website"
		if _, err := os.Stat(webDir); os.IsNotExist(err) {
			webDir = "server/website"
		}
		// The built app is a build artifact, not a tracked file (`server/website`
		// is gitignored), so a fresh clone has -studio on and nothing to serve.
		// Silence here cost real onboarding: the log said `studio=true`, and the
		// tutorial's very first step — open /panel — answered a bare 404 with no
		// hint that a build was missing. Say it in the log AND in the browser.
		studioBuilt := true
		if _, err := os.Stat(filepath.Join(webDir, "index.html")); err != nil {
			studioBuilt = false
			const remedy = "cd panel && npm ci && npm run deploy"
			log.Printf("WARNING: -studio is on but no built app at %s — /panel and /docs/ will not work. Build it: %s", webDir, remedy)
			studioMissing := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				w.Header().Set("Content-Type", "text/html; charset=utf-8")
				w.WriteHeader(http.StatusServiceUnavailable)
				fmt.Fprintf(w, `<!doctype html><meta charset=utf-8>
<title>Elvin Studio is not built</title>
<body style="font:16px/1.6 system-ui;max-width:42em;margin:12vh auto;padding:0 1em">
<h1>Elvin Studio is not built yet</h1>
<p>The server is running with <code>-studio</code>, but there is no built app at
<code>%s</code> — it is a build artifact and is not committed to the repo.</p>
<p>Build it from the repository root, then reload:</p>
<pre style="background:#f4f4f4;padding:.8em;border-radius:6px">%s</pre>
<p>The game API (<code>/v1/*</code>, <code>/content/*</code>) works regardless.</p>
`, webDir, remedy)
			})
			mux.Handle("/panel/", studioMissing)
			mux.Handle("/panel", studioMissing)
			mux.Handle("/", studioMissing)
		}
		if studioBuilt {
			rawSite := http.FileServer(http.Dir(webDir))
			site := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				switch {
				case isAppShell(r.URL.Path):
					// index.html — ОБОЛОЧКА приложения: она и только она знает
					// хэши свежих бандлов. Закэшированная, она грузит вчерашний
					// index-*.js, и деплой «не доезжает» — правка на сервере есть,
					// а в браузере старый код. Сами бандлы неизменяемы (хэш в
					// имени), их кэш не трогаем.
					w.Header().Set("Cache-Control", "no-store")
				}
				rawSite.ServeHTTP(w, r)
			})
			mux.Handle("/panel/", http.StripPrefix("/panel/", site))
			mux.HandleFunc("/panel", func(w http.ResponseWriter, r *http.Request) {
				http.Redirect(w, r, "/panel/", http.StatusFound)
			})
			mux.Handle("/", site)
		}
		// The vanilla /admin/ page retired into the React app's admin mode —
		// keep bookmarked URLs working.
		mux.HandleFunc("/admin/", func(w http.ResponseWriter, r *http.Request) {
			http.Redirect(w, r, "/?admin=1", http.StatusFound)
		})
	} else {
		mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
			if r.URL.Path != "/" {
				http.NotFound(w, r)
				return
			}
			w.Header().Set("Content-Type", "text/plain; charset=utf-8")
			fmt.Fprintln(w, "Elvin content server (game API). Start with -studio to serve the authoring app.")
		})
	}

	log.Printf("LVN server on %s, content=%s, admin=%v, state-auth=%v, studio=%v", *addr, *contentDir, *adminToken != "", *stateToken != "", *studio)
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
	// admin is wired in main() purely to share ONE editorial write lock across
	// every snapshot+write pair on content/ (see writeLock). May be nil in
	// tests and in a server built without the panel API.
	admin      *AdminService
	mu         sync.RWMutex
	state      map[string]stateEntry // user id -> save + version
	stateOrder []string              // insertion order, for bounded eviction

	verMu    sync.Mutex
	verCache map[bool]verCacheEntry // includeManifest -> cached versions
	// Кольцо последних состояний контента: по нему считается «что именно
	// изменилось» вместо «забирай всё заново» (см. content_delta.go).
	deltas deltaRing

	// hashMu — один обход дерева за раз и охрана hashCache: два одновременных
	// опроса версии делали бы одну и ту же работу дважды.
	hashMu    sync.Mutex
	hashCache map[string]hashEntry // rel → (размер, mtime, sha256) прошлого обхода
	hashReads int                  // сколько файлов ПРОЧИТАНО за жизнь процесса — для тестов

	userMu    sync.Mutex
	userLocks map[string]*sync.Mutex // per-user: serializes state PUT + key claim
}

// lockUser returns the mutex serializing writes for one user's state blob.
// The version check + file write + memory update must be one critical section —
// without it two concurrent PUTs both pass the OCC check and one update is lost.
func (s *server) lockUser(user string) *sync.Mutex {
	s.userMu.Lock()
	defer s.userMu.Unlock()
	if s.userLocks == nil {
		s.userLocks = make(map[string]*sync.Mutex)
	}
	m, ok := s.userLocks[user]
	if !ok {
		// Bound the map like stateMemMax bounds saves: drop it wholesale when it
		// grows unreasonably (locks are transient; a fresh one is fine once no
		// request holds the old — and holders keep their own pointer safely).
		if len(s.userLocks) > stateMemMax*2 {
			s.userLocks = make(map[string]*sync.Mutex)
		}
		m = &sync.Mutex{}
		s.userLocks[user] = m
	}
	return m
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
		// СВЕЖАЯ УСТАНОВКА И СЛОМАННАЯ ВЫКЛАДКА — РАЗНЫЕ ОТВЕТЫ.
		//
		// Пустой каталог законен ровно в одном случае: публиковать ещё нечего,
		// файла нет. Файл, который ЕСТЬ, но не читается (права, полудописанная
		// выкладка, сбой диска), — это наша беда, и отвечать на неё «игр нет»
		// значит рассказать игроку про пустую библиотеку. Замерено: до этой
		// правки оба случая давали 200 и пустой каталог.
		if os.IsNotExist(err) {
			writeJSON(w, http.StatusOK, map[string]any{"titles": []any{}})
			return
		}
		log.Printf("manifest unreadable: %v", err)
		http.Error(w, "manifest unavailable", http.StatusServiceUnavailable)
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
		// PRIVATE subtrees live under the content root for operational
		// convenience but are API-served, never static: player saves
		// (state/ — guarded by X-State-Key on /v1/state), wallets/users
		// (services/), edit history and the unpublished draft. Serving
		// them here would hand any visitor another player's wallet and
		// bypass the save-key check entirely.
		// A re-import conflict parks the incoming version BESIDE the live file
		// as <name>.incoming (importer/baseline.go), so it lands inside the
		// served tree — unlike the other private subtrees it has no directory
		// to hide behind. It is unpublished, unreviewed content the author has
		// not accepted yet; serving it would publish every rejected version.
		rel := strings.ToLower(strings.TrimPrefix(r.URL.Path, "/content/"))
		if privateRel(rel) {
			http.Error(w, "not found", http.StatusNotFound)
			return
		}
		// The manifest is the LIVE index of the whole game — a stale copy
		// keeps every player on yesterday's config. Scripts (.lvn) are live
		// for the same reason. Everything else is versioned (?v=) and safe
		// to cache forever.
		if strings.HasSuffix(strings.ToLower(r.URL.Path), ".lvn") || rel == "manifest.json" {
			w.Header().Set("Cache-Control", "no-store")
		} else {
			w.Header().Set("Cache-Control", "public, max-age=604800, immutable")
		}
		// ЭТАГ — РАДИ ДОКАЧКИ, А НЕ РАДИ КЭША.
		//
		// Оборванная загрузка продолжается запросом «Range: bytes=N-». Если
		// файл между двумя заходами заменили, клиент получает ХВОСТ НОВОЙ
		// версии и приклеивает его к голове старой: замерено на живом сервере
		// — 300 000 байт, из них 120 000 от прежней редакции и 180 000 от
		// новой, и ни одна проверка этого не видит (qa/resume-integrity-check.sh).
		//
		// Лечит это «If-Range»: сервер сам решает, отдать хвост или файл
		// целиком. Но условному запросу нужен СИЛЬНЫЙ валидатор, а FileServer
		// своего ETag не ставит вовсе — оставалась дата с секундной точностью,
		// которая двух правок в одну секунду не различает.
		if etag := fileETag(dir, strings.TrimPrefix(r.URL.Path, "/content/")); etag != "" {
			w.Header().Set("ETag", etag)
		}
		fs.ServeHTTP(w, r)
	})
}

// fileETag — сильный валидатор файла: размер и время правки. Пустая строка,
// если пути нет или он ведёт наружу дерева контента (тогда ответит сам
// FileServer, и добавлять ему заголовок нечего).
//
// Сильный он потому, что этого требует «If-Range»: со слабым валидатором
// сервер обязан игнорировать условие и отдать хвост — то есть ровно то, от
// чего мы защищаемся.
func fileETag(dir, rel string) string {
	if rel == "" || hasDotSegment(rel) {
		return ""
	}
	clean := filepath.Clean("/" + filepath.FromSlash(rel))[1:]
	info, err := os.Stat(filepath.Join(dir, clean))
	if err != nil || info.IsDir() {
		return ""
	}
	return fmt.Sprintf("\"%x-%x\"", info.Size(), info.ModTime().UnixNano())
}

// etagMatches: совпадает ли ETag файла с тем, что прислали в If-Match.
// Заголовок допускает список через запятую и «*» (подходит любой существующий
// файл); слабые метки (W/"...") сравниваются по своей части — наш ETag всегда
// сильный, но клиент, прошедший через прокси, может вернуть слабую копию.
func etagMatches(ifMatch, current string) bool {
	if current == "" {
		return false
	}
	for _, part := range strings.Split(ifMatch, ",") {
		part = strings.TrimSpace(part)
		if part == "*" {
			return true
		}
		if strings.HasPrefix(part, "W/") {
			part = strings.TrimSpace(part[2:])
		}
		if part == current {
			return true
		}
	}
	return false
}

// hasDotSegment reports whether any path segment starts with a dot. Nothing a
// player fetches ever does, while everything editorial under the content root
// does: .history/, .lvn-import/, and the short-lived .publish-*.lvns a publish
// compiles from (it has to live in scripts/ so `include` resolves against its
// neighbours). Listing those one by one was a standing invitation to add a
// fourth and forget — and http.FileServer serves dotfiles perfectly happily,
// so "it starts with a dot" hides nothing on its own.
func hasDotSegment(rel string) bool {
	for _, seg := range strings.Split(rel, "/") {
		if strings.HasPrefix(seg, ".") {
			return true
		}
	}
	return false
}

// privateRel — путь под корнем контента, который НИКОГДА не уходит игроку и
// не пишется общей дверью ассетов: служебные данные (кошельки, база, сейвы),
// редакторская кухня (история, базлайн импорта, припаркованные версии,
// черновик манифеста) и учётки админки. Вход — путь относительно корня
// контента, в любом регистре, с прямыми слэшами.
//
// Правило стояло тремя копиями — у раздачи, у индекса версий и у офлайн-
// экспорта — и они разошлись: раздача знала про state/, экспорт не знал ни
// про что, а про учётки админки не знал никто. Соли и хэши паролей четырёх
// владельцев раздавались с прода как картинка (аудит 03.09.2026, проверено
// снаружи). Ответ на вопрос «это служебное?» — один, и живёт здесь.
func privateRel(rel string) bool {
	rel = strings.ToLower(strings.TrimLeft(rel, "/"))
	if strings.HasPrefix(rel, "services/") || strings.HasPrefix(rel, "state/") ||
		hasDotSegment(rel) || strings.HasSuffix(rel, ".incoming") ||
		rel == "manifest.draft.json" {
		return true
	}
	base := rel
	if i := strings.LastIndex(rel, "/"); i >= 0 {
		base = rel[i+1:]
	}
	// ИМЯ ЗАКРЫТО ВМЕСТЕ С ЕГО КОПИЯМИ. Точное сравнение оставляло открытым
	// всё, что рядом: `admin-users.json` отвечал 404, а `admin-users.json.bak-…`
	// — двумя сотнями и хэшами паролей внутри (замер 04.09, по проводу). Копии
	// заводят руками перед правкой ролей, редакторы оставляют `~`-файлы, деплой
	// кладёт `.bak` с меткой времени — и любая из них открывала то, что закрыт
	// оригинал. То же для черновика каталога.
	return strings.HasPrefix(base, adminUsersFile) || strings.HasPrefix(base, "manifest.draft.json")
}

// toolingRel — файл АВТОРСКОЙ КУХНИ, а не игры.
//
// В каталоге контента живут не только скрипты и арт. Рядом с ними копятся
// исходники (`.lvns`, из которых компилируются главы), бэкапы манифеста, что
// делает деплой (`manifest.json.bak-предеплой-…`, по полмегабайта штука),
// присланные архивы с ассетами, заметки и черновики редактора (`…lvn~`).
// Замер 04.09 на живом каталоге: 143 исходника, девять бэкапов манифеста и два
// архива — и все они попадали и в индекс версий, и в офлайновый набор игры.
//
// Цена — не столько байты (на живом каталоге это около процента), сколько
// ТРЕВОГА: индекс версий сворачивается в общую версию контента, по которой
// играющий клиент решает, что мир изменился. Каждый бэкап от деплоя и каждая
// правка авторского комментария в исходнике заставляли всех читающих идти за
// разницей и перечитывать открытую главу мимо кэша — за файл, которого игрок
// никогда не увидит.
//
// Правило применяется в ДВУХ местах и записано здесь одно: индекс версий
// (computeVersions) и офлайновый набор экспорта. Раздачу по /content/ оно не
// трогает: панель читает `.lvns` прямо оттуда, а автору может понадобиться
// забрать свой архив.
func toolingRel(rel string) bool {
	base := strings.ToLower(filepath.Base(strings.ReplaceAll(rel, "\\", "/")))
	if base == "" || base == "." {
		return false
	}
	// Черновики редакторов: «файл~», «файл.orig», «файл.rej».
	if strings.HasSuffix(base, "~") || base == ".ds_store" {
		return true
	}
	// Бэкап узнаётся по «.bak» ГДЕ УГОДНО в имени: их делают с меткой времени
	// на хвосте (manifest.json.bak-predeploy-014142), и расширением это не
	// ловится.
	if strings.Contains(base, ".bak") {
		return true
	}
	switch filepath.Ext(base) {
	case ".lvns", // исходник главы: игра исполняет .lvn, скомпилированный из него
		".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", // присланные архивы
		".psd", ".xcf", ".ai", ".aseprite", ".blend", ".kra", // исходники графики
		".md",                           // заметки студии
		".orig", ".rej", ".tmp", ".swp": // следы правок и слияний
		return true
	}
	return false
}

// hashEntry — что индекс версий помнит о файле между обходами. Совпали
// размер и mtime — файл не перечитывается: его sha256 уже посчитан.
type hashEntry struct {
	size int64
	mod  time.Time
	sum  string
}

// computeVersions returns {content-relative-path: sha256} for every served
// file. includeManifest folds manifest.json into the map (used by the version
// endpoint so manifest edits register), otherwise it's left out (the asset
// index is for art/scripts; the manifest is fetched fresh, never versioned).
//
// ЧИТАЕТСЯ ТОЛЬКО ИЗМЕНИВШЕЕСЯ. Раньше каждый обход читал и хэшировал КАЖДЫЙ
// байт дерева, а обход идёт на каждый опрос версии (кэш — две секунды,
// клиент спрашивает каждые две). На проде это 1,8 с одного ядра из каждых
// двух, на dev-контенте с .git внутри — девять секунд на ответ (замер
// 03.09.2026). Теперь обход — это stat каждого файла; байты читаются, когда
// у файла сменились размер или mtime. Записи сервер делает атомарным
// переименованием, так что новый файл всегда приходит с новым mtime.
func (s *server) computeVersions(includeManifest bool) map[string]string {
	s.hashMu.Lock()
	defer s.hashMu.Unlock()
	prev := s.hashCache
	next := make(map[string]hashEntry, len(prev))
	out := map[string]string{}
	_ = filepath.Walk(s.content, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			return nil
		}
		rel, rerr := filepath.Rel(s.content, path)
		if rerr != nil {
			return nil
		}
		rel = filepath.ToSlash(rel)
		if info.IsDir() {
			// Служебные поддеревья не обходятся вовсе: в .git dev-контента
			// лежат сотни мегабайт, и они хэшировались на каждый опрос.
			if rel != "." && privateRel(rel+"/") {
				return filepath.SkipDir
			}
			return nil
		}
		if rel == "asset-versions.json" || (rel == "manifest.json" && !includeManifest) {
			return nil
		}
		// Derived, regenerable artifacts (downscale.go's @2k variants,
		// ktx2.go's codes — plus .astc leftovers from the format we dropped)
		// must NOT fold into the content version: they appear on
		// disk lazily as clients request them, and counting them made every
		// first visit to a scene bump the version — the client's ContentSync
		// then "detected a content change" and reloaded the chapter MID-PLAY
		// (multi-second freeze + the story jumping a beat forward off the
		// autosave). The source images they derive from are versioned already.
		base := filepath.Base(rel)
		if strings.HasSuffix(base, ".astc") || strings.HasSuffix(base, ".ktx2") || strings.Contains(base, downscaleSuffix+".") {
			return nil
		}
		// Авторская кухня в индекс не входит — см. toolingRel.
		if toolingRel(rel) {
			return nil
		}
		// Runtime state (player saves under state/, analytics/wallets under
		// services/) mutates DURING play — every autosave was bumping the
		// content version, which the client answered with a full chapter
		// reload: save → version change → reload → resume → save → … a loop
		// of multi-second freezes. This state is API-served, never a
		// cacheable asset, so it has no business in the version index.
		// Editorial plumbing, not content: history snapshots, the re-import
		// baseline, parked conflict versions and the unpublished manifest draft
		// must never bump the version (a backup would otherwise reload every
		// client). The .incoming case is the live one: an import that ends in
		// conflicts parks a file next to each live chapter, so a routine
		// re-import would reload every player mid-chapter over content nobody
		// has accepted yet. Всё это — одно правило со статикой (privateRel).
		if privateRel(rel) {
			return nil
		}
		// СИМВОЛЬНАЯ ССЫЛКА: Walk даёт Lstat, то есть размер и время САМОЙ
		// ссылки, а читаем мы её цель. Ключ по ссылке не меняется никогда —
		// правка цели не была бы замечена до перезапуска, и клиент навсегда
		// остался бы на старом файле. Спрашиваем цель.
		if info.Mode()&os.ModeSymlink != 0 {
			st, serr := os.Stat(path)
			if serr != nil {
				return nil
			}
			info = st
		}
		if e, ok := prev[rel]; ok && e.size == info.Size() && e.mod.Equal(info.ModTime()) {
			next[rel] = e
			out[rel] = e.sum
			return nil
		}
		data, derr := os.ReadFile(path)
		if derr != nil {
			return nil
		}
		sum := sha256.Sum256(data)
		e := hashEntry{size: info.Size(), mod: info.ModTime(), sum: hex.EncodeToString(sum[:])}
		next[rel] = e
		out[rel] = e.sum
		s.hashReads++
		return nil
	})
	s.hashCache = next
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
	cur := s.computeVersionsCached(true)
	sum := versionHash(cur)
	// Кольцо наполняет САМ ОПРОС: он идёт постоянно, а за разницей приходят
	// редко и уже после смены версии. Запоминать только в обработчике разницы
	// значило бы не помнить состояние ДО неё — то есть то единственное, от
	// чего разницу и считают.
	s.deltas.remember(sum, cur)
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
	// One writer per user: the TOFU key claim below and the OCC version
	// check+write in PUT are check-then-act sequences — serialize them.
	if r.Method == http.MethodPut {
		lk := s.lockUser(user)
		lk.Lock()
		defer lk.Unlock()
	}
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
		body, err := io.ReadAll(io.LimitReader(r.Body, bodyDoc))
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

// decodeStateFile читает файл состояния: обёртку или, если файл старый, голый
// документ.
//
// ОБЁРТКУ УЗНАЮТ ПО ОБЁРТКЕ, а не по наличию поля с подходящим именем. Раньше
// хватало одного `doc` — и состояние игрока `{"doc":…, "score":5}` (автор
// вправе завести переменную `doc`: это обычное слово) читалось как обёртка,
// теряя ВСЁ, кроме одного поля. Ни ошибки, ни строки в логе: игрок просто
// возвращался с обнулённым прогрессом.
//
// Признак обёртки — РОВНО два поля, `__v` и `doc`, и ничего больше: так её
// пишет encodeStateFile и никак иначе.
func decodeStateFile(b []byte) stateEntry {
	var probe map[string]json.RawMessage
	if err := json.Unmarshal(b, &probe); err == nil && len(probe) == 2 {
		if _, hasV := probe["__v"]; hasV {
			if doc, hasDoc := probe["doc"]; hasDoc && len(doc) > 0 {
				var w stateWrapper
				if json.Unmarshal(b, &w) == nil {
					return stateEntry{body: w.Doc, version: w.V}
				}
			}
		}
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

// writeLock returns the ONE mutex that serialises snapshot+write pairs on
// content/. admin.go documents the contract ("two parallel panel saves can't
// lose a history revision"), and every AdminService write honours it — but the
// asset endpoint below, which the panel's script editor and every CLI upload
// go through, took no lock at all. Two saves of the same script racing meant
// snapshotHistory writing the same .bak name twice: one revision lost, and the
// version the author expected to roll back to simply not there. Falls back to
// the same package-level mutex conflict resolution uses when no AdminService
// was wired in (tests) — serialising against nothing still beats not
// serialising.
func (s *server) writeLock() *sync.Mutex {
	if s.admin != nil {
		return &s.admin.writeMu
	}
	return &fallbackWriteMu
}

func (s *server) handleAdminAsset(w http.ResponseWriter, r *http.Request) {
	if !adminAllowed(w, r, s.adminToken) {
		return
	}
	rel := strings.TrimPrefix(r.URL.Path, "/v1/admin/assets/")
	if rel == "" || strings.Contains(rel, "..") {
		http.Error(w, "bad path", http.StatusBadRequest)
		return
	}
	// Служебные пути пишутся своими дверями (учётки — /v1/admin/people,
	// черновик — /v1/admin/manifest?draft=1, конфликты — их резолвер), а
	// кошельки, база и сейвы руками не пишутся вовсе. Через общую дверь
	// ассетов редактор переписывал admin-users.json и становился владельцем,
	// а заодно рисовал себе любой баланс (аудит 03.09.2026, проверено
	// живьём). Отказ — всем ролям и ключу машины: у владельца для этого есть
	// ssh, а не дверь, которая одинаково открыта редактору.
	if privateRel(rel) {
		http.Error(w, "служебный путь: у него своя дверь", http.StatusForbidden)
		return
	}
	dst := filepath.Join(s.content, filepath.Clean(rel))
	switch r.Method {
	case http.MethodPut:
		ifMatch := strings.TrimSpace(r.Header.Get("If-Match"))
		body, err := io.ReadAll(io.LimitReader(r.Body, bodyHuge))
		if err != nil {
			http.Error(w, "read body", http.StatusBadRequest)
			return
		}
		// THE gate: a compiled script is structurally checked BEFORE anything
		// touches the disk — no mkdir, no .history snapshot, no atomic write —
		// so a rejected save leaves the previous version, and the author's
		// history, exactly as they were. content/ is served to players with
		// no-store, i.e. a bad save is live instantly; this is the only place
		// on the write path where that can still be stopped. Errors block,
		// warnings (unknown/host ops above all) ride along in the response.
		var findings lvnFindings
		if isManifestPath(rel) {
			// Манифест через гейт не проходил вовсе — а это весь облик
			// приложения. Только предупреждения: схемы у нас нет, судим по
			// конвенции имён (см. checkManifest).
			findings = s.checkManifest(body)
			// В ЛОГ ТОЖЕ. Находка, уехавшая только в ответ, видна автору в
			// панели и невидима оператору — а он единственный, кто смотрит на
			// прод целиком.
			if len(findings.Warnings) > 0 {
				log.Printf("manifest PUT %s: %d warning(s): %s", rel, len(findings.Warnings), strings.Join(findings.Warnings, "; "))
			}
		}
		if isLvnPath(rel) {
			findings = s.checkLvn(rel, body)
			if findings.blocked() {
				log.Printf("asset PUT rejected %s: %d error(s): %s", rel, len(findings.Errors), strings.Join(findings.Errors, "; "))
				writeJSON(w, http.StatusUnprocessableEntity, map[string]any{
					"path": rel, "rejected": true,
					"errors": orEmpty(findings.Errors), "warnings": orEmpty(findings.Warnings),
				})
				return
			}
			if len(findings.Warnings) > 0 {
				log.Printf("asset PUT %s: %d warning(s): %s", rel, len(findings.Warnings), strings.Join(findings.Warnings, "; "))
			}
		}
		// Оптимистическая блокировка манифеста: его правят несколько агентов,
		// и PUT устаревшей копии молча стирал чужие тайтлы и спрайты («куда
		// делась игра»). Правило: rev в теле обязан совпадать с rev на диске;
		// сервер инкрементирует rev сам при каждой записи — вперёд и только
		// вперёд. Старая копия получает 409 с инструкцией, а не тихую победу.
		// Тот же предикат, что и у гейта выше: два правила «это манифест»,
		// записанные по-разному в двадцати строках друг от друга, — дверь, про
		// которую однажды забудут.
		if isManifestPath(rel) {
			newBody, code, msg := s.manifestRevGate(body)
			if code != 0 {
				log.Printf("manifest PUT rejected: %s", msg)
				writeJSON(w, code, map[string]any{"error": msg, "rejected": true})
				return
			}
			// Гейт вернул пустое тело: присланный каталог совпадает с тем, что
			// на диске. Отвечаем успехом, но на диск не ходим — иначе холостое
			// сохранение стоило бы перезагрузки каталога всем играющим.
			if newBody == nil {
				writeJSON(w, http.StatusOK, map[string]any{
					"path": rel, "bytes": len(body), "unchanged": true,
					"warnings": orEmpty(findings.Warnings),
				})
				return
			}
			body = newBody
		}
		if err := os.MkdirAll(filepath.Dir(dst), 0o755); err != nil {
			http.Error(w, err.Error(), http.StatusInternalServerError)
			return
		}
		// Snapshot and write are ONE indivisible step: the .bak name is derived
		// from the clock, so two concurrent saves of the same file otherwise
		// collide on it and the older revision is gone.
		lk := s.writeLock()
		lk.Lock()
		// УСЛОВНАЯ ЗАПИСЬ. Над одной новеллой работают несколько рук: автор в
		// панели, редактор в другой вкладке, ИИ-агент по API. Все они пишут
		// главу одной и той же дверью, и до 05.09.2026 последний записавший
		// молча выигрывал: оба получали 200, на диске оставалась одна правка,
		// и потерявший узнавал об этом (если вообще узнавал) через день, читая
		// свою главу заново. Снимок в .history делал потерю ВОССТАНОВИМОЙ, но
		// не делал её ВИДИМОЙ, а это разные вещи.
		//
		// Правило то же, что у манифеста и у прогресса игрока: кто прислал
		// версию, на которой правил, тот и проверяется. If-Match с ETag
		// прочитанного файла → запись идёт, только если на диске всё ещё он.
		// Заголовка нет — пишем как раньше (скрипты сборки, CLI и агенты
		// версии не читают, и запрещать им запись значило бы сломать тракт
		// публикации ради проверки, которой они не просили).
		//
		// Проверка живёт ВНУТРИ замка, вместе со снимком и записью: снаружи
		// две сохранённые копии успевали пройти её обе и снова затереть друг
		// друга — окно узкое, но оно и есть тот случай, ради которого пишут
		// оптимистическую блокировку.
		current := fileETag(s.content, rel)
		if ifMatch != "" && !etagMatches(ifMatch, current) {
			lk.Unlock()
			was := "файл изменился на сервере, пока вы правили"
			if current == "" {
				was = "файла на сервере больше нет — его удалили или переименовали, пока вы правили"
			}
			log.Printf("asset PUT conflict %s: If-Match %s, на диске %s", rel, ifMatch, current)
			writeJSON(w, http.StatusConflict, map[string]any{
				"path": rel, "conflict": true, "etag": current,
				"error": was + ": перечитайте главу и сохраните заново, ваш текст цел в редакторе",
			})
			return
		}
		snapshotHistory(s.content, rel) // scripts keep their past versions
		err = atomicWrite(dst, body, 0o644)
		etag := fileETag(s.content, rel)
		lk.Unlock()
		if err != nil {
			http.Error(w, err.Error(), http.StatusInternalServerError)
			return
		}
		// ETag ответа — чтобы редактор мог сохранять дальше, не перечитывая
		// только что написанный им же файл.
		if etag != "" {
			w.Header().Set("ETag", etag)
		}
		writeJSON(w, http.StatusOK, map[string]any{
			"path": rel, "bytes": len(body), "etag": etag,
			"warnings": orEmpty(findings.Warnings),
		})
	case http.MethodDelete:
		// Same pair, same lock: a delete that races a save must not lose the
		// snapshot that makes the delete undoable.
		lk := s.writeLock()
		lk.Lock()
		snapshotHistory(s.content, rel)
		err := os.Remove(dst)
		lk.Unlock()
		if err != nil {
			http.Error(w, "not found", http.StatusNotFound)
			return
		}
		writeJSON(w, http.StatusOK, map[string]any{"deleted": rel})
	default:
		http.Error(w, "PUT or DELETE", http.StatusMethodNotAllowed)
	}
}

// atomicWrite writes via a temp file in the same directory then renames, so a
// concurrent reader (e.g. computeVersions hashing for cache-busting) never sees
// a half-written or zero-byte file. Rename is atomic on the same filesystem,
// and the explicit fsyncs extend the guarantee from "survives a process crash"
// to "survives a power cut": the data hits stable storage before the rename
// publishes it, and the directory entry itself is flushed after.
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
	if err := tmp.Sync(); err != nil {
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
	// Flush the rename itself. Best-effort: not every filesystem supports
	// fsync on a directory, and the write is already durable content-wise.
	if d, err := os.Open(filepath.Dir(dst)); err == nil {
		_ = d.Sync()
		_ = d.Close()
	}
	return nil
}

// ОТСУТСТВИЕ ФАЙЛА И ЕГО НЕДОСТУПНОСТЬ — РАЗНЫЕ ОТВЕТЫ.
//
// Хранилища на диске читались тремя разными правилами. Кошелёк различал:
// `fs.ErrNotExist` — новый игрок, всё остальное — отказ. Ежедневная награда и
// таблица лидеров писали `if err == nil` — и любая ошибка (файла нет, диск
// занят, права слетели, JSON побит) молча превращалась в ПУСТОЙ документ.
//
// Пустой документ опаснее ошибки, потому что он выглядит законно: игрок теряет
// серию входов, сервер сохраняет ноль ПОВЕРХ уцелевшего файла, и восстановить
// уже нечего. Ошибку чтения нельзя лечить забвением: единственное безопасное
// поведение — отказать и не писать.
//
// Возвращает: true — прочитали; false, nil — файла ещё нет (законное начало);
// false, err — файл есть и не читается, ПЕРЕЗАПИСЫВАТЬ ЕГО НЕЛЬЗЯ.
func readJSONFile(path string, v any) (bool, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		if errors.Is(err, fs.ErrNotExist) {
			return false, nil
		}
		return false, err
	}
	if err := json.Unmarshal(data, v); err != nil {
		return false, fmt.Errorf("%s повреждён: %w", filepath.Base(path), err)
	}
	return true, nil
}

// «УЗНАЙ ИЛИ ОТКАЖИ» — один дом на все защищённые обработчики.
//
// Ритуал повторялся шестью копиями: спросить пользователя, сравнить с пустой
// строкой, ответить 401 и выйти. Текст отказа, к счастью, не разошёлся — но
// заголовок `WWW-Authenticate` не ставила ни одна из шести. По HTTP ответ 401
// без него неполон: клиент не знает, КАКОЙ способ входа от него ждут. Тот же
// пробел, что с `Allow` у 405 (роль 184), и по той же причине — правило жило
// копиями, и неполнота копировалась вместе с ним.
//
// Пользователя обработчик добывает сам (у сервисов разные приёмники), а дом
// решает единственный вопрос: пускать или нет.
func requireUser(w http.ResponseWriter, userID string) bool {
	if userID != "" {
		return true
	}
	w.Header().Set("WWW-Authenticate", `Bearer realm="lvn"`)
	http.Error(w, "unauthorized", http.StatusUnauthorized)
	return false
}

// СКОЛЬКО ТЕЛА МЫ ГОТОВЫ ПРОЧИТАТЬ — именованные ступени, а не числа по месту.
//
// Предел стоял у всех тридцати четырёх чтений (это хорошо: без него один запрос
// съедает память сервера), но записан был двенадцатью разными способами — и
// дважды ОДНО И ТО ЖЕ число разными: `4096` в восьми местах и `4<<10` в трёх.
// Разнобой здесь не косметика: чтобы понять, «много ли это», приходится считать
// в уме, а решение «сколько можно» принимается заново на каждом обработчике,
// без памяти о соседях.
//
// Ступени названы по СМЫСЛУ запроса, а не по числу: маленькая форма, документ,
// пачка событий, загрузка файла. Новый обработчик выбирает ступень, а не
// придумывает константу.
const (
	bodyTiny  = 4 << 10  // короткая форма: логин, отметка, один идентификатор
	bodySmall = 64 << 10 // запись с полями: профиль, заказ, настройка
	bodyDoc   = 1 << 20  // документ: манифест главы, отчёт, пачка событий
	bodyBulk  = 8 << 20  // выгрузка: импорт новеллы, набор ассетов
	bodyHuge  = 64 << 20 // архив целиком — единственный обработчик, который его ждёт
)

// МЕТОД ЗАПРОСА ПРОВЕРЯЕТ ОДИН ДОМ.
//
// Ритуал «не тот метод — 405 и выход» был написан двадцать восемь раз, и уже
// разошёлся в словах: «POST only» в семнадцати местах, «method not allowed» в
// шестнадцати. Код одинаковый (405), поэтому клиенту всё равно — а вот тому,
// кто читает лог сервера, приходится держать в голове, что это одно и то же.
//
// Возвращает true, если можно работать дальше: `if !onlyMethod(w, r, http.MethodPost) { return }`.
// Заголовок Allow ставится по правилам HTTP — без него 405 формально неполон, и
// ни одно из двадцати восьми мест его не ставило.
func onlyMethod(w http.ResponseWriter, r *http.Request, method string) bool {
	if r.Method == method {
		return true
	}
	w.Header().Set("Allow", method)
	http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
	return false
}

// ЧИСЛО ИЗ ЗАПРОСА ЧИТАЕТ ОДИН ДОМ — и, главное, ОДИНАКОВО отвечает на перегиб.
//
// Восемь обработчиков разбирали числовой параметр сами, и предел стоял у всех
// восьми (это хорошо), но вели они себя при перегибе ПЯТЬЮ разными способами:
// окно отчёта отвечало ошибкой, ожидание в комнате молча срезалось до потолка,
// длина выгрузки — тоже до потолка, а вот `limit` у топа, `n` у логов и `n` у
// лидеров молча падали в УМОЛЧАНИЕ.
//
// Разница видна снаружи и обидна: клиент просит тысячу строк лога, надеясь
// получить «сколько можно», и получает двести — меньше, чем если бы просил
// вдумчиво. Ошибки в ответе при этом нет, поэтому автор клиента не узнаёт, что
// его число вообще не приняли, и чинит не то.
//
// Правил ровно два, и выбор между ними — про СМЫСЛ, а не про вкус:
//
//   - qtyParam — число меняет только ОБЪЁМ ответа (сколько строк, сколько
//     секунд ждать). Перегиб срезается до потолка молча: клиент просил больше,
//     чем есть, и получает всё, что есть.
//   - spanParam — число меняет СМЫСЛ ответа (за какой период отчёт). Перегиб —
//     ошибка: молча подменённый период превращает отчёт про деньги в отчёт про
//     другой отрезок времени, и заметить это по цифрам нельзя.
//
// Пустое и нечитаемое значение — всегда умолчание: параметра просто нет.
func qtyParam(r *http.Request, name string, def, max int) int {
	v := strings.TrimSpace(r.URL.Query().Get(name))
	if v == "" {
		return def
	}
	n, err := strconv.Atoi(v)
	if err != nil || n <= 0 {
		return def
	}
	if n > max {
		return max
	}
	return n
}

// spanParam — как qtyParam, но перегиб честно назван ошибкой: ok=false.
func spanParam(r *http.Request, name string, def, max int) (int, bool) {
	v := strings.TrimSpace(r.URL.Query().Get(name))
	if v == "" {
		return def, true
	}
	n, err := strconv.Atoi(v)
	if err != nil || n <= 0 {
		return def, true
	}
	if n > max {
		return 0, false
	}
	return n, true
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

// manifestRevGate — оптимистическая блокировка manifest.json (см. PUT выше).
// Возвращает тело с уже увеличенным rev, либо (код ≠ 0) HTTP-статус и понятное
// сообщение, либо ПУСТОЕ тело при нулевом коде — «писать нечего, каталог тот
// же». Манифест без rev принимается один раз — как миграция
// (на диске rev тоже отсутствует), после этого rev обязателен.
func (s *server) manifestRevGate(body []byte) ([]byte, int, string) {
	var incoming map[string]any
	if err := json.Unmarshal(body, &incoming); err != nil {
		return nil, http.StatusBadRequest, "manifest.json: тело не является валидным JSON: " + err.Error()
	}
	curRev := 0
	var cur map[string]any
	if raw, err := os.ReadFile(filepath.Join(s.content, "manifest.json")); err == nil {
		if json.Unmarshal(raw, &cur) == nil {
			if v, ok := cur["rev"].(float64); ok {
				curRev = int(v)
			}
		} else {
			cur = nil
		}
	}
	bodyRev := -1
	if v, ok := incoming["rev"].(float64); ok {
		bodyRev = int(v)
	}
	// Миграция: ни на диске, ни в теле rev ещё нет — принять и завести rev=1.
	if curRev == 0 && bodyRev == -1 {
		incoming["rev"] = 1
	} else if bodyRev != curRev {
		have := "отсутствует"
		if bodyRev >= 0 {
			have = fmt.Sprintf("rev %d", bodyRev)
		}
		return nil, http.StatusConflict, fmt.Sprintf(
			"манифест устарел: на сервере rev %d, в вашей копии %s. "+
				"Обновите манифест перед изменениями: GET /content/manifest.json, "+
				"внесите правки поверх свежей копии и повторите PUT — rev двигается "+
				"только вперёд, сервер увеличит его сам.", curRev, have)
	} else {
		// СОХРАНЕНИЕ БЕЗ ПРАВОК НЕ ЗАПИСЬ. Открыли каталог в панели, ничего не
		// тронули, нажали «Сохранить» — и rev уезжал вперёд, а с ним общая
		// версия контента: каждый играющий шёл за каталогом (в живой студии это
		// 436 КБ), перечитывал открытую главу мимо кэша и пересобирал фигуры на
		// сцене. Ради нуля новостей. Сравниваем содержание при выровненном
		// счётчике; совпало — писать нечего, и rev остаётся прежним: чужая
		// копия от этого не устареет, потому что ничего не изменилось.
		if cur != nil {
			aligned := make(map[string]any, len(incoming))
			for k, v := range incoming {
				aligned[k] = v
			}
			aligned["rev"] = float64(curRev)
			mine, e1 := json.Marshal(aligned)
			theirs, e2 := json.Marshal(cur)
			if e1 == nil && e2 == nil && bytes.Equal(mine, theirs) {
				return nil, 0, ""
			}
		}
		incoming["rev"] = curRev + 1
	}
	out, err := json.MarshalIndent(incoming, "", "  ")
	if err != nil {
		return nil, http.StatusInternalServerError, err.Error()
	}
	return append(out, '\n'), 0, ""
}

// isAppShell — путь, по которому отдаётся index.html: корень, каталог со
// слэшем на конце и сам файл. Именно эти ответы обязаны быть свежими.
func isAppShell(p string) bool {
	return p == "" || p == "/" || strings.HasSuffix(p, "/") || strings.HasSuffix(p, "/index.html") || p == "index.html"
}
