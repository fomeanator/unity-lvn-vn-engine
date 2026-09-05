package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"
)

// НАД ОДНОЙ НОВЕЛЛОЙ РАБОТАЮТ НЕСКОЛЬКО РУК.
//
// Автор в панели, соавтор во второй вкладке, ИИ-агент по API — все пишут главу
// одной дверью. Пока сервер не спрашивал версию, побеждал последний
// записавший: оба получали 200, на диске оставалась одна правка, и потерявший
// узнавал об этом, читая свою главу назавтра. Снимок в .history делал потерю
// восстановимой, но не делал её видимой.

func putAssetVersioned(t *testing.T, s *server, rel, body, ifMatch string) (*httptest.ResponseRecorder, map[string]any) {
	t.Helper()
	req := httptest.NewRequest("PUT", "/v1/admin/assets/"+rel, strings.NewReader(body))
	req.Header.Set("Authorization", "Bearer t")
	if ifMatch != "" {
		req.Header.Set("If-Match", ifMatch)
	}
	rec := httptest.NewRecorder()
	s.handleAdminAsset(rec, req)
	var out map[string]any
	_ = json.Unmarshal(rec.Body.Bytes(), &out)
	return rec, out
}

func chapterOnDisk(t *testing.T, s *server, rel string) string {
	t.Helper()
	b, err := os.ReadFile(filepath.Join(s.content, rel))
	if err != nil {
		t.Fatalf("глава не читается: %v", err)
	}
	return string(b)
}

const relChapter = "scripts/ch1.lvns"

// Двое правили одну главу, оба сохранили — работа первого обязана уцелеть.
func TestSecondAuthorDoesNotOverwriteTheFirst(t *testing.T) {
	s := guardServer(t)
	putAssetVersioned(t, s, relChapter, "сцена ch1\n\n— исходная строка\n", "")
	общая := fileETag(s.content, relChapter)
	if общая == "" {
		t.Fatal("у файла нет версии — сверять нечего")
	}

	// Оба читали одно и то же и оба сохраняют СВОЁ.
	recA, outA := putAssetVersioned(t, s, relChapter, "сцена ch1\n\n— правка автора А\n", общая)
	if recA.Code != http.StatusOK {
		t.Fatalf("первому отказали: %d %v", recA.Code, outA)
	}
	// Ответ несёт новую версию: редактору незачем перечитывать то, что он сам
	// только что записал.
	if outA["etag"] == "" || outA["etag"] == nil {
		t.Fatalf("успешная запись не вернула версию: %v", outA)
	}

	recB, outB := putAssetVersioned(t, s, relChapter, "сцена ch1\n\n— правка автора Б\n", общая)
	if recB.Code != http.StatusConflict {
		t.Fatalf("второму ответили %d вместо 409: %v", recB.Code, outB)
	}
	if outB["conflict"] != true {
		t.Errorf("отказ не помечен признаком конфликта: %v", outB)
	}
	// В отказе — текущая версия, чтобы клиент мог перечитать ровно её.
	if outB["etag"] != fileETag(s.content, relChapter) {
		t.Errorf("в отказе не та версия: %v против %s", outB["etag"], fileETag(s.content, relChapter))
	}
	if got := chapterOnDisk(t, s, relChapter); !strings.Contains(got, "автора А") {
		t.Fatalf("работа первого потеряна, на диске: %q", got)
	}

	// Перечитал — сохранил. Отказ не запирает работу, он её упорядочивает.
	rec, out := putAssetVersioned(t, s, relChapter, "сцена ch1\n\n— правка А, дополненная Б\n",
		fileETag(s.content, relChapter))
	if rec.Code != http.StatusOK {
		t.Fatalf("после перечитывания отказали: %d %v", rec.Code, out)
	}
	if got := chapterOnDisk(t, s, relChapter); !strings.Contains(got, "дополненная") {
		t.Fatalf("вторая правка не легла: %q", got)
	}
}

// Скрипты сборки, CLI и агенты версий не читают. Требовать заголовок со всех
// значило бы сломать тракт публикации ради проверки, о которой он не просил.
func TestWriteWithoutVersionStillWorks(t *testing.T) {
	s := guardServer(t)
	putAssetVersioned(t, s, relChapter, "сцена ch1\n\n— первая\n", "")
	rec, out := putAssetVersioned(t, s, relChapter, "сцена ch1\n\n— из скрипта сборки\n", "")
	if rec.Code != http.StatusOK {
		t.Fatalf("запись без версии отклонена: %d %v", rec.Code, out)
	}
	if got := chapterOnDisk(t, s, relChapter); !strings.Contains(got, "скрипта сборки") {
		t.Fatalf("на диске не то: %q", got)
	}
}

// Файл удалили или переименовали, пока автор правил, — это тоже конфликт, а не
// «создам заново»: иначе сохранение воскрешает главу, которую только что
// осознанно убрали.
func TestVersionOfAVanishedFileIsAConflict(t *testing.T) {
	s := guardServer(t)
	putAssetVersioned(t, s, relChapter, "сцена ch1\n\n— была\n", "")
	было := fileETag(s.content, relChapter)
	if err := os.Remove(filepath.Join(s.content, relChapter)); err != nil {
		t.Fatal(err)
	}
	rec, out := putAssetVersioned(t, s, relChapter, "сцена ch1\n\n— вернулась\n", было)
	if rec.Code != http.StatusConflict {
		t.Fatalf("на исчезнувший файл ответили %d вместо 409: %v", rec.Code, out)
	}
	if _, err := os.Stat(filepath.Join(s.content, relChapter)); err == nil {
		t.Error("глава воскрешена сохранением поверх удаления")
	}
}

// Гонка: десять сохранений с ОДНОЙ И ТОЙ ЖЕ версией. Проверка живёт внутри
// того же замка, что снимок и запись, — значит выиграть обязан ровно один.
func TestOnlyOneWriterWinsTheSameVersion(t *testing.T) {
	s := guardServer(t)
	putAssetVersioned(t, s, relChapter, "сцена ch1\n\n— исходная\n", "")
	общая := fileETag(s.content, relChapter)

	var mu sync.Mutex
	коды := map[int]int{}
	var wg sync.WaitGroup
	for i := 0; i < 10; i++ {
		wg.Add(1)
		go func(n int) {
			defer wg.Done()
			rec, _ := putAssetVersioned(t, s, relChapter, "сцена ch1\n\n— писал номер "+string(rune('А'+n))+"\n", общая)
			mu.Lock()
			коды[rec.Code]++
			mu.Unlock()
		}(i)
	}
	wg.Wait()
	if коды[http.StatusOK] != 1 {
		t.Fatalf("одну версию приняли %d раз(а), ожидался ровно один: %v", коды[http.StatusOK], коды)
	}
	if коды[http.StatusConflict] != 9 {
		t.Errorf("отказов 409: %d, ожидалось 9 (%v)", коды[http.StatusConflict], коды)
	}
}

// Заголовок допускает список и «*» — правила заголовка, а не наши.
func TestIfMatchHeaderRules(t *testing.T) {
	s := guardServer(t)
	putAssetVersioned(t, s, relChapter, "сцена ch1\n\n— строка\n", "")
	есть := fileETag(s.content, relChapter)

	if rec, _ := putAssetVersioned(t, s, relChapter, "сцена ch1\n\n— по звёздочке\n", "*"); rec.Code != http.StatusOK {
		t.Errorf("«*» на существующий файл отклонена: %d", rec.Code)
	}
	есть = fileETag(s.content, relChapter)
	список := `"чужая-1", ` + есть + `, "чужая-2"`
	if rec, _ := putAssetVersioned(t, s, relChapter, "сцена ch1\n\n— по списку\n", список); rec.Code != http.StatusOK {
		t.Errorf("версия в списке не найдена: %d", rec.Code)
	}
	слабая := "W/" + fileETag(s.content, relChapter)
	if rec, _ := putAssetVersioned(t, s, relChapter, "сцена ch1\n\n— по слабой\n", слабая); rec.Code != http.StatusOK {
		t.Errorf("слабая метка отклонена: %d", rec.Code)
	}
}

// Агент публикует главу целиком и версий не читает: требовать от него If-Match
// значило бы сломать тракт публикации. Но перезапись чужого текста обязана быть
// ВИДНА — иначе работа исчезает незамеченной ровно так же, как исчезала через
// дверь ассетов.
func TestAgentPublishSaysWhatItReplaced(t *testing.T) {
	s := guardServer(t)
	if err := os.WriteFile(filepath.Join(s.content, "manifest.json"), []byte(`{"titles":[]}`), 0o644); err != nil {
		t.Fatal(err)
	}
	publish := func(text string) map[string]any {
		t.Helper()
		body, _ := json.Marshal(map[string]any{
			"id": "proba", "name": "Проба", "chapter": 1, "lvns": text,
		})
		req := httptest.NewRequest("POST", "/v1/admin/agent/publish", strings.NewReader(string(body)))
		req.Header.Set("Authorization", "Bearer t")
		rec := httptest.NewRecorder()
		s.handleAgentPublish(rec, req)
		var out map[string]any
		_ = json.Unmarshal(rec.Body.Bytes(), &out)
		if rec.Code != http.StatusOK {
			t.Fatalf("публикация не прошла: %d %v", rec.Code, out)
		}
		return out
	}

	if out := publish("scene ch1\n\n— первая редакция\n"); out["replaced"] != nil {
		t.Errorf("новая глава объявлена заменой чужой работы: %v", out["replaced"])
	}
	// Тот же текст ещё раз — ничего не заменено, и молчать об этом правильно.
	if out := publish("scene ch1\n\n— первая редакция\n"); out["replaced"] != nil {
		t.Errorf("повторная публикация того же текста считается заменой: %v", out["replaced"])
	}
	// А вот теперь под агентом лежал ЧУЖОЙ текст.
	out := publish("scene ch1\n\n— редакция агента\n")
	when, ok := out["replaced"].(string)
	if !ok || when == "" {
		t.Fatalf("замена чужой работы прошла молча: %v", out)
	}
	if _, err := time.Parse(time.RFC3339, when); err != nil {
		t.Errorf("время прежней редакции нечитаемо: %q", when)
	}
	// Прежняя редакция достаётся обратно — потеря видима И восстановима.
	found := false
	_ = filepath.Walk(filepath.Join(s.content, ".history"), func(p string, info os.FileInfo, err error) error {
		if err == nil && info != nil && !info.IsDir() {
			if b, e := os.ReadFile(p); e == nil && strings.Contains(string(b), "первая редакция") {
				found = true
			}
		}
		return nil
	})
	if !found {
		t.Error("прежней редакции нет в .history — заменённое не вернуть")
	}
}
