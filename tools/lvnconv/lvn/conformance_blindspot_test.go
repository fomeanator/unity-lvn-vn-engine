package lvn

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// ЗЕЛЁНЫЙ ЦВЕТ НЕ ДОЛЖЕН ВРАТЬ — правило то же, факт под ним другой.
//
// Прежде браузерный прогон блок `expect.scene` не смотрел вовсе, и страж
// ЗАПРЕЩАЛ сценическому случаю объявлять «js»: он прошёл бы, ничего не
// проверив. 06.09 слепота закрыта — browser-runner.mjs сворачивает поток
// команд в кадр, а страж корпуса сверяет фон и состав экрана.
//
// Поэтому опасность сменила сторону. Теперь врёт не зелёный «js», а его
// ОТСУТСТВИЕ: случай про кадр, не объявивший браузер, молча проверяет один
// рантайм из двух — ровно там, где они и расходились (слова-флаги
// `show="no"`: движок читает словарём, сырая истинность JS считает непустую
// строку правдой).
//
// Страж держит обе стороны:
//  1. свёртка кадра в раннере на месте — иначе «js» снова стал бы пустым;
//  2. сценический случай либо объявляет «js», либо ОБЪЯСНЯЕТ, почему нет
//     (Unity-only команды: объёмная подложка, камера, частицы, анимация).
func TestСценическийСлучайНеОбъявляетБраузер(t *testing.T) {
	dir := filepath.Join(repoRoot(t), "conformance", "cases")
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatalf("корпус не читается: %v", err)
	}

	checked := 0
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".json") {
			continue
		}
		raw, err := os.ReadFile(filepath.Join(dir, e.Name()))
		if err != nil {
			t.Fatalf("%s: %v", e.Name(), err)
		}
		var one map[string]any
		var many []map[string]any
		if err := json.Unmarshal(raw, &many); err != nil {
			if err := json.Unmarshal(raw, &one); err != nil {
				t.Fatalf("%s не разбирается: %v", e.Name(), err)
			}
			many = []map[string]any{one}
		}
		for _, c := range many {
			checked++
			if !expectsScene(c) {
				continue
			}
			if declaresRuntime(c, "js") {
				continue // кадр сверяется обоими рантаймами — то, что и нужно
			}
			// Не объявил браузер — обязан сказать почему. Свободная строка
			// («why» или «note») здесь не украшение: она отличает осознанную
			// границу от забытого случая, а забытый случай — это половина
			// сверки, потерянная молча.
			if reason(c) == "" {
				t.Errorf("%s: случай проверяет кадр, но браузером не играется и причины не называет.\n"+
					"Браузерный прогон кадр СВЕРЯЕТ (см. conformance/browser-runner.mjs, reduceScene) —\n"+
					"либо добавьте «js» в runtimes, либо напишите в «why», чего браузеру там не хватает.", e.Name())
			}
		}
	}
	if checked == 0 {
		t.Fatal("не прочитано ни одного случая — страж потерял корпус и молчит впустую")
	}

	// ВТОРАЯ СТОРОНА: сама свёртка кадра. Уберут её из раннера — «js» на
	// сценических случаях снова станет пустым обещанием, и этот страж, стоящий
	// на её существовании, обязан покраснеть первым.
	runner := filepath.Join(repoRoot(t), "conformance", "browser-runner.mjs")
	src, err := os.ReadFile(runner)
	if err != nil {
		t.Fatalf("браузерный прогонщик не читается: %v", err)
	}
	for _, must := range []string{"function reduceScene", "visible"} {
		if !strings.Contains(string(src), must) {
			t.Fatalf("в browser-runner.mjs нет «%s» — кадр он больше не сворачивает,\n"+
				"а корпус по-прежнему объявляет сценические случаи браузерными", must)
		}
	}
}

// Причина, по которой случай не играется браузером, — своими словами.
func reason(c map[string]any) string {
	for _, key := range []string{"why", "note", "comment"} {
		if s, _ := c[key].(string); strings.TrimSpace(s) != "" {
			return s
		}
	}
	return ""
}

func declaresRuntime(c map[string]any, want string) bool {
	rts, _ := c["runtimes"].([]any)
	for _, r := range rts {
		if s, _ := r.(string); s == want {
			return true
		}
	}
	return false
}

func expectsScene(c map[string]any) bool {
	exp, _ := c["expect"].(map[string]any)
	if exp == nil {
		return false
	}
	_, ok := exp["scene"]
	return ok
}
