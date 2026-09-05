package main

// Analytics service — an honest append-only event log. Clients batch events
// to /v1/analytics/events (anonymous or authenticated); each day lands in
// its own JSONL file, one event per line, ready for jq / DuckDB / anything.
// The files ARE the database at this scale.
//
// Reading them is the other half, and it lives in analytics_rollup.go (the
// incremental aggregates) and analytics_report.go (the cuts and the signals:
// title / author / chapter / day, the completion funnel and its drop-off
// points, and the technical health of the build in the field).

import (
	"encoding/json"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"regexp"
	"sync"
	"time"
)

type AnalyticsService struct {
	owners     *ownerIndex // title → author, for attribution stamping
	mu         sync.Mutex
	dir        string
	auth       *AuthService
	adminToken string

	rollups  *rollupStore  // derived aggregates over the day files
	chapters *chapterIndex // chapter ORDER, read from the same manifest players read
	// payments — ведомость покупок (кошелёк). Необязательна: движок без
	// монетизации остаётся движком, отчёт о деньгах тогда просто пуст.
	payments paymentsSource
}

func NewAnalyticsService(dir string, auth *AuthService, adminToken string, owners *ownerIndex) (*AnalyticsService, error) {
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return nil, err
	}
	s := &AnalyticsService{dir: dir, auth: auth, adminToken: adminToken, owners: owners,
		rollups: newRollupStore(dir)}
	if owners != nil {
		// Same manifest the owner index already watches — a funnel needs the
		// chapter ORDER, which only the manifest knows.
		s.chapters = newChapterIndex(owners.path)
	}
	return s, nil
}

func (s *AnalyticsService) Routes(mux *http.ServeMux) {
	mux.HandleFunc("/v1/analytics/events", s.handleEvents)
	mux.HandleFunc("/v1/analytics/summary", s.handleSummary)
	mux.HandleFunc("/v1/analytics/funnel", s.handleFunnel)
	// Где бросают главу и ЧТО было на экране — единственный отчёт, который
	// работает там, где выборов нет вовсе (analytics_exits.go).
	mux.HandleFunc("/v1/analytics/exits", s.handleExits)
	// Удержание — метрика номер один для покупки трафика (analytics_retention.go).
	mux.HandleFunc("/v1/analytics/retention", s.handleRetention)
	// Первая сессия: докуда доходит новичок и сколько ждёт загрузки.
	mux.HandleFunc("/v1/analytics/first-session", s.handleFirstSession)
	// Воронка ВНУТРИ главы: докуда дочитывают, на каких развилках уходят
	// (analytics_slides.go). Расширение воронки по главам, а не второй отчёт:
	// глава отвечает «где теряем», слайд — «на чём именно».
	mux.HandleFunc("/v1/analytics/slides", s.handleSlides)
	// Деньги: конверсия в платящего, ARPU, ARPPU (analytics_money.go).
	mux.HandleFunc("/v1/analytics/money", s.handleMoney)
	// Вариант А против Б: цель плюс предохранители плюс честная значимость.
	mux.HandleFunc("/v1/analytics/experiment", s.handleExperiment)
	// «Важные места»: воронка по меткам, которые расставил автор (track).
	mux.HandleFunc("/v1/analytics/marks", s.handleMarks)
	mux.HandleFunc("/v1/analytics/health", s.handleHealth)
}

func (s *AnalyticsService) adminOK(w http.ResponseWriter, r *http.Request) bool {
	return adminAllowed(w, r, s.adminToken)
}

type analyticsEvent struct {
	Name  string         `json:"name"`
	TS    string         `json:"ts,omitempty"`
	Props map[string]any `json:"props,omitempty"`
	User  string         `json:"user,omitempty"` // filled server-side, never trusted from the client
	// Attribution, stamped at write time (see attribution.go). Title is
	// client-reported — only the client knows what it is playing; Author is
	// resolved from the manifest server-side, so a client cannot name its own
	// payee. Neither can be reconstructed once the request is gone.
	Title  string `json:"title,omitempty"`
	Author string `json:"author,omitempty"`
	// The two other DIMENSIONS, normalized at write time for the same reason
	// the title is: the reader must not have to know that NovelApp spells it
	// "chapter" while the helper's own docstring spells it "ch", and a
	// dimension has to be length-capped once, here, not in every query.
	Chapter string `json:"chapter,omitempty"`
	SID     string `json:"sid,omitempty"` // session id — клиент штампует им каждое событие
}

var reDay = regexp.MustCompile(`^\d{4}-\d{2}-\d{2}$`)

// Analytics ingest is anonymous by design, which also makes it the cheapest
// disk-filling target on the server. Two honest caps instead of trust:
// a per-source token bucket (IP or user id) and a hard per-day file size.
const (
	analyticsBurst      = 30        // instant burst per source
	analyticsPerMinute  = 60        // sustained batches/min per source
	analyticsDayMaxSize = 256 << 20 // one day's JSONL hard cap (bytes)

	// Насколько далеко от серверного времени может отстоять ts, присланный
	// клиентом. Сутки — это офлайновая очередь, пролежавшая ночь в самолёте;
	// всё, что дальше, — сбитые или подделанные часы, и такому событию
	// ставится серверный штамп (см. handleEvents).
	clientClockWindow = 24 * time.Hour
)

// clientIP: the peer address without the port. Deliberately IGNORES
// X-Forwarded-For — a spoofable header would let one host mint unlimited
// rate-limit identities; behind a reverse proxy all traffic shares one
// bucket, which for an analytics firehose is an acceptable floor.
func clientIP(r *http.Request) string {
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		return r.RemoteAddr
	}
	return host
}

type anaBucket struct {
	tokens float64
	last   time.Time
}

var (
	anaMu      sync.Mutex
	anaBuckets = map[string]*anaBucket{}
)

func analyticsAllow(source string, now time.Time) bool {
	anaMu.Lock()
	defer anaMu.Unlock()
	if len(anaBuckets) > 10000 { // transient counters; shed wholesale
		anaBuckets = map[string]*anaBucket{}
	}
	b, ok := anaBuckets[source]
	if !ok {
		b = &anaBucket{tokens: analyticsBurst, last: now}
		anaBuckets[source] = b
	}
	b.tokens += now.Sub(b.last).Minutes() * analyticsPerMinute
	if b.tokens > analyticsBurst {
		b.tokens = analyticsBurst
	}
	b.last = now
	if b.tokens < 1 {
		return false
	}
	b.tokens--
	return true
}

func (s *AnalyticsService) handleEvents(w http.ResponseWriter, r *http.Request) {
	if !onlyMethod(w, r, http.MethodPost) {
		return
	}
	source := s.auth.UserFromRequest(r)
	if source == "" {
		source = clientIP(r)
	}
	if !analyticsAllow(source, time.Now()) {
		http.Error(w, "rate limited", http.StatusTooManyRequests)
		return
	}
	var events []analyticsEvent
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, bodyDoc)).Decode(&events); err != nil {
		http.Error(w, "a JSON array of {name, ts?, props?} required", http.StatusBadRequest)
		return
	}
	if len(events) == 0 || len(events) > 100 {
		http.Error(w, "1..100 events per batch", http.StatusBadRequest)
		return
	}
	user := s.auth.UserFromRequest(r) // "" for anonymous — allowed by design
	now := time.Now().UTC()

	s.mu.Lock()
	defer s.mu.Unlock()
	path := filepath.Join(s.dir, now.Format("2006-01-02")+".jsonl")
	if st, err := os.Stat(path); err == nil && st.Size() > analyticsDayMaxSize {
		http.Error(w, "daily volume cap reached", http.StatusTooManyRequests)
		return
	}
	f, err := os.OpenFile(path, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o600)
	if err != nil {
		http.Error(w, "storage error", http.StatusInternalServerError)
		return
	}
	defer f.Close()
	accepted, skewed := 0, 0
	for _, ev := range events {
		if ev.Name == "" || len(ev.Name) > 64 {
			continue
		}
		// ВРЕМЯ СОБЫТИЯ — НЕ ПОКАЗАНИЯ ЧУЖИХ ЧАСОВ. Клиент вправе прислать
		// свой ts (событие могло случиться офлайн и ждать в очереди), но часы
		// на устройстве принадлежат игроку: они сбиваются после разряда, их
		// двигают руками, их подделывает кто угодно.
		//
		// Замер 05.09 на живом сервере: события с ts «2035-01-01» и
		// «1999-01-01» принимались как есть и ложились в файл сегодняшнего дня.
		// В сводке они считаются (она идёт по файлу), а всякий отчёт, который
		// группирует ПО ВРЕМЕНИ СОБЫТИЯ, их не видит — активность такого игрока
		// просто пропадает из статистики, и никто об этом не узнает.
		//
		// Правило: чужой ts принимается, пока он в разумном окне от серверного
		// времени; вышел за окно — событие штампуется серверным временем, а
		// расхождение считается отдельно (в сводке видно, сколько раз часы
		// разошлись). Окно щедрое: офлайновая очередь честно может пролежать
		// сутки, и терять её содержимое нельзя.
		if ev.TS == "" {
			ev.TS = now.Format(time.RFC3339)
		} else if t, err := time.Parse(time.RFC3339, ev.TS); err != nil ||
			t.Before(now.Add(-clientClockWindow)) || t.After(now.Add(clientClockWindow)) {
			ev.TS = now.Format(time.RFC3339)
			skewed++
		}
		ev.User = user
		// The client may also pass the title inside props (that is where the
		// Unity helper puts context today) — accept either spelling, then
		// resolve the author OURSELVES. A client-supplied author is discarded.
		if ev.Title == "" {
			if t, ok := ev.Props["title"].(string); ok {
				ev.Title = t
			}
		}
		if ev.Chapter == "" {
			if c, ok := ev.Props["chapter"].(string); ok {
				ev.Chapter = c
			} else if c, ok := ev.Props["ch"].(string); ok {
				ev.Chapter = c
			}
		}
		if ev.SID == "" {
			if v, ok := ev.Props["sid"].(string); ok {
				ev.SID = v
			} else if v, ok := ev.Props["session"].(string); ok {
				ev.SID = v
			}
		}
		ev.Title, ev.Chapter, ev.SID = clip(ev.Title, 64), clip(ev.Chapter, 64), clip(ev.SID, 64)
		ev.Author = s.owners.authorOf(ev.Title)
		line, _ := json.Marshal(ev)
		if _, err := f.Write(append(line, '\n')); err == nil {
			accepted++
		}
	}
	// skewed возвращается клиенту не для красоты: клиент видит, что его часы
	// разошлись с сервером, и может это показать или залогировать. Отчёты же
	// теперь считают по времени, которому можно верить.
	writeJSON(w, http.StatusOK, map[string]int{"accepted": accepted, "clock_skew": skewed})
}

// handleSummary lives in analytics_report.go — reading is a different job from
// writing, and it is the only one that needs to be clever.
