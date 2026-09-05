package main

import (
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"testing"
	"time"
)

// ЧАСЫ УСТРОЙСТВА — НЕ ИСТОЧНИК ПРАВДЫ ДЛЯ ОТЧЁТОВ.
//
// Клиент присылает время события сам: событие могло случиться офлайн и
// пролежать в очереди до следующего подключения, и терять такую очередь
// нельзя. Но часы на телефоне принадлежат игроку — они сбиваются после
// разряда, их двигают руками, их подделывает кто угодно с curl.
//
// Пара тестов держит границу с двух сторон: первый показывает, что сервер
// делает с невозможным временем, второй — сколько стоило бы его принять.

func clockRig(t *testing.T, pay *fakePayments) (*http.ServeMux, string) {
	t.Helper()
	svc, mux, dir := analyticsRig(t)
	svc.payments = pay
	return mux, dir
}

func regDev(t *testing.T, mux *http.ServeMux, id string) string {
	t.Helper()
	rec, out := call(t, mux, "POST", "/v1/auth/register", "", map[string]string{"device_id": id})
	if rec.Code != 200 {
		t.Fatalf("регистрация %s: %d %s", id, rec.Code, rec.Body)
	}
	return out["token"].(string)
}

// Невозможное время заменяется серверным, честная офлайновая очередь — нет.
func TestClientClockOutsideWindowIsStamped(t *testing.T) {
	mux, dir := clockRig(t, nil)
	tok := regDev(t, mux, "chasy-shtamp-0123456789ab")

	now := time.Now().UTC()
	вОчереди := now.Add(-6 * time.Hour).Format(time.RFC3339)
	rec, out := call(t, mux, "POST", "/v1/analytics/events", tok, []map[string]any{
		{"name": "будущее", "ts": now.Add(3 * 365 * 24 * time.Hour).Format(time.RFC3339)},
		{"name": "прошлое", "ts": now.Add(-10 * 365 * 24 * time.Hour).Format(time.RFC3339)},
		{"name": "мусор", "ts": "вчера вечером"},
		{"name": "очередь", "ts": вОчереди},
		{"name": "молча", "ts": ""},
	})
	if rec.Code != 200 || out["accepted"].(float64) != 5 {
		t.Fatalf("приём: %d %v", rec.Code, out)
	}
	// Ни одно событие не потеряно — их штампуют, а не выбрасывают: игрок с
	// битыми часами всё равно играет, и его активность обязана быть видна.
	if out["clock_skew"].(float64) != 3 {
		t.Fatalf("расхождений часов %v, ожидалось 3", out["clock_skew"])
	}

	lines := readJSONL(t, dir)
	if len(lines) != 5 {
		t.Fatalf("строк %d, ожидалось 5", len(lines))
	}
	byName := map[string]string{}
	for _, l := range lines {
		byName[l["name"].(string)], _ = l["ts"].(string)
	}
	for _, имя := range []string{"будущее", "прошлое", "мусор", "молча"} {
		ts, err := time.Parse(time.RFC3339, byName[имя])
		if err != nil {
			t.Fatalf("«%s»: время нечитаемо (%q)", имя, byName[имя])
		}
		if d := now.Sub(ts); d < -time.Minute || d > time.Minute {
			t.Errorf("«%s» осталось с чужим временем %s (расхождение %s)", имя, byName[имя], d)
		}
	}
	if byName["очередь"] != вОчереди {
		t.Errorf("офлайновая очередь переписана: %s вместо %s", byName["очередь"], вОчереди)
	}
}

// Цена приёма чужих часов, измеренная на отчёте о деньгах: «время до первой
// покупки» считается от первого события игрока, и одно событие с датой
// годичной давности утаскивает P90 из часов в тысячи часов. Магазин по такому
// числу показывают не тогда, когда надо.
func TestSkewedClockWouldStretchTimeToFirstPurchase(t *testing.T) {
	now := time.Now().UTC()
	день := now.Format("2006-01-02")
	покупка := now.Add(30 * time.Minute)
	pay := &fakePayments{
		buys: []walletPurchase{
			{User: "честный", TS: покупка.Format(time.RFC3339), SKU: "gold_100"},
			{User: "сбитый", TS: покупка.Format(time.RFC3339), SKU: "gold_100"},
		},
		prices: map[string]struct {
			v   float64
			cur string
		}{"gold_100": {0.99, "USD"}},
	}

	// Как было бы БЕЗ окна доверия: строки пишутся в журнал напрямую, минуя
	// приём — ровно то, что попадало в файл до 05.09.2026.
	dir := t.TempDir()
	события := `{"name":"boot","ts":"` + now.Format(time.RFC3339) + `","user":"честный"}` + "\n" +
		`{"name":"boot","ts":"` + now.Add(-365*24*time.Hour).Format(time.RFC3339) + `","user":"сбитый"}` + "\n"
	if err := os.WriteFile(filepath.Join(dir, день+".jsonl"), []byte(события), 0o644); err != nil {
		t.Fatal(err)
	}
	s := &AnalyticsService{dir: dir, rollups: newRollupStore(dir), adminToken: "t", payments: pay}
	mux := http.NewServeMux()
	s.Routes(mux)
	rec, _ := call(t, mux, "GET", "/v1/analytics/money?from="+день+"&to="+день, "t", nil)
	var сырой moneyReport
	if err := json.Unmarshal(rec.Body.Bytes(), &сырой); err != nil {
		t.Fatal(err)
	}
	if сырой.ToFirstPurchase == nil || сырой.ToFirstPurchase.P90Hours < 1000 {
		t.Fatalf("замер цены не удался: без окна P90 должен был улететь, а он %+v", сырой.ToFirstPurchase)
	}

	// А теперь то же самое ЧЕРЕЗ ПРИЁМ, с настоящими пропусками игроков.
	mux2, _ := clockRig(t, pay)
	честный := regDev(t, mux2, "chasy-chestnyj-0123456789ab")
	сбитый := regDev(t, mux2, "chasy-sbityj-0123456789abc")
	uid := func(tok string) string {
		_, out := call(t, mux2, "GET", "/v1/auth/me", tok, nil)
		return out["user_id"].(string)
	}
	pay.buys = []walletPurchase{
		{User: uid(честный), TS: покупка.Format(time.RFC3339), SKU: "gold_100"},
		{User: uid(сбитый), TS: покупка.Format(time.RFC3339), SKU: "gold_100"},
	}
	call(t, mux2, "POST", "/v1/analytics/events", честный, []map[string]any{{"name": "boot"}})
	call(t, mux2, "POST", "/v1/analytics/events", сбитый, []map[string]any{
		{"name": "boot", "ts": now.Add(-365 * 24 * time.Hour).Format(time.RFC3339)}})

	rec2, _ := call(t, mux2, "GET", "/v1/analytics/money?from="+день+"&to="+день, "admintok", nil)
	var честно moneyReport
	if err := json.Unmarshal(rec2.Body.Bytes(), &честно); err != nil {
		t.Fatal(err)
	}
	if честно.ToFirstPurchase == nil || честно.ToFirstPurchase.N != 2 {
		t.Fatalf("оба игрока должны попасть в замер: %+v", честно.ToFirstPurchase)
	}
	if честно.ToFirstPurchase.P90Hours > 2 {
		t.Errorf("сбитые часы всё ещё растягивают отчёт: P90 %v ч (было %v ч без окна)",
			честно.ToFirstPurchase.P90Hours, сырой.ToFirstPurchase.P90Hours)
	}
}
