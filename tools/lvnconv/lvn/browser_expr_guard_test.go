package lvn

import (
	"bytes"
	"encoding/json"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strings"
	"testing"
)

// КОРПУС ПРОВЕРЯЕТ ВСЕХ, КТО ИГРАЕТ.
//
// Язык исполняют трое: C#-рантайм (приложение), Go (обход и проверки) и
// `expr.js` — вычислитель браузерного playground. Корпус `conformance/cases`
// объявлял себя только для C# (`"runtimes": ["csharp"]`), поэтому третья
// реализация не сверялась ни с кем: её держал в узде комментарий «the same
// surface the engine's LvnExpression covers».
//
// Сверка списков функций ничего не доказывает — их у обоих ровно тридцать, и
// имена совпадают. Расходятся не имена, а ПОВЕДЕНИЕ: что считать истиной, как
// сравнивать строку с числом, во что превращается неизвестная переменная. Для
// автора, который пробует новеллу в браузере, а потом играет её в приложении,
// это разница между «работает» и «у меня по-другому».
//
// Поэтому здесь корпус прогоняется через настоящий `expr.js` в node. Нет node —
// тест пропускается: это машина без браузерного тракта, а не поломка.
func TestBrowserExpressionsAgreeWithTheCorpus(t *testing.T) {
	node := requireNode(t, "браузерный вычислитель")
	root := repoRoot(t)
	// ИСХОДНИК, А НЕ СБОРКА. Вчерашняя версия смотрела в
	// `server/website/play/` — а эта папка целиком в .gitignore: она вывод
	// `npm run deploy`, и на чистой машине её нет. Страж молча пропускался бы
	// всегда, создавая ровно ту уверенность, ради борьбы с которой заведён.
	exprJS := filepath.Join(root, filepath.FromSlash("panel/public/play/expr.js"))
	if _, err := os.Stat(exprJS); err != nil {
		t.Fatalf("panel/public/play/expr.js не найден (%v) — браузерный вычислитель "+
			"ЕСТЬ третья реализация языка и обязан лежать в репозитории, а не только "+
			"в выводе сборки", err)
	}

	type expectation struct {
		Vars      map[string]any `json:"vars"`
		ExprTrue  []string       `json:"expr_true"`
		ExprFalse []string       `json:"expr_false"`
	}
	type doc struct {
		Script []map[string]any `json:"script"`
	}
	type kase struct {
		ID       string      `json:"id"`
		Runtimes []string    `json:"runtimes"`
		Doc      doc         `json:"doc"`
		Expect   expectation `json:"expect"`
	}

	files, err := filepath.Glob(filepath.Join(root, "conformance", "cases", "*.json"))
	if err != nil || len(files) == 0 {
		t.Fatalf("корпус не найден: %v", err)
	}

	// Состояние случая приходит ДВУМЯ путями: готовым набором `expect.vars`
	// или скриптом, который его создаёт (`set l expr="list(2,2,3)"`). Второй
	// путь пришлось учесть отдельно: подставив пустые переменные, тест сначала
	// «нашёл» двадцать пять расхождений там, где расходился он сам.
	type probe struct {
		Case   string           `json:"case"`
		Expr   string           `json:"expr"`
		Want   bool             `json:"want"`
		Vars   any              `json:"vars"`
		Script []map[string]any `json:"script"`
	}
	var probes []probe
	skipped := 0
	for _, f := range files {
		raw, err := os.ReadFile(f)
		if err != nil {
			continue
		}
		var k kase
		if json.Unmarshal(raw, &k) != nil {
			continue
		}
		vars := k.Expect.Vars
		if vars == nil {
			vars = map[string]any{}
		}
		// ЧТО ЭТОТ ТЕСТ МЕРЯЕТ — только вычисление выражений. Если состояние
		// случая рождается ПОТОКОМ (goto перепрыгивает часть set, call/return
		// уводят и возвращают) или зависит от чужих правил (`default` — это
		// Чтец «да-нет»), то, исполняя скрипт линейно, тест мерил бы СВОЮ
		// модель плеера, а не браузерный вычислитель. Такие случаи честнее
		// пропустить, чем зачесть расхождением: ровно на них первая версия
		// «нашла» десять несуществующих ошибок.
		// КОРПУС САМ ГОВОРИТ, КОГО ПРОВЕРЯЕТ. Случай, обязательный для
		// браузера, помечен «js» в runtimes; остальные тест не трогает — не
		// потому что они неважны, а потому что их состояние рождается потоком
		// (goto перепрыгивает set, call уводит и возвращает) или правилами
		// соседних домов (`default` — это Чтец «да-нет»). Исполняя такой
		// скрипт линейно, тест мерил бы СВОЮ модель плеера: ровно так первая
		// версия «нашла» десять несуществующих ошибок.
		declared := false
		for _, r := range k.Runtimes {
			if r == "js" {
				declared = true
			}
		}
		if !declared || scriptNeedsARealPlayer(k.Doc.Script) {
			skipped++
			continue
		}
		for _, e := range k.Expect.ExprTrue {
			probes = append(probes, probe{k.ID, e, true, vars, k.Doc.Script})
		}
		for _, e := range k.Expect.ExprFalse {
			probes = append(probes, probe{k.ID, e, false, vars, k.Doc.Script})
		}
	}
	if len(probes) == 0 {
		t.Fatal("в корпусе не нашлось ни одного выражения — разбор сломался, а не корпус")
	}

	payload, err := json.Marshal(probes)
	if err != nil {
		t.Fatal(err)
	}
	dir := t.TempDir()
	inPath := filepath.Join(dir, "probes.json")
	if err := os.WriteFile(inPath, payload, 0o644); err != nil {
		t.Fatal(err)
	}
	script := filepath.Join(dir, "run.mjs")
	src := fmt.Sprintf(`
import { readFileSync } from 'node:fs';
import { evalBool, evalExpr } from %q;
const probes = JSON.parse(readFileSync(%q, 'utf8'));

// Состояние случая: сперва объявленные переменные, потом то, что создаёт сам
// скрипт (set/inc) — ровно как их применяет плеер.
function stateOf(p) {
  const vars = JSON.parse(JSON.stringify(p.vars || {}));
  for (const c of (p.script || [])) {
    const op = c.op, key = c.key;
    if (!key || (op !== 'set' && op !== 'inc')) continue;
    if (op === 'set' && c.default && vars[key] !== undefined) continue;
    const value = op === 'inc'
      ? ((Number.isFinite(Number(vars[key])) ? Number(vars[key]) : 0)
         + (c.by === undefined ? 1 : Number(evalExpr(String(c.by), vars))))
      : (c.expr !== undefined ? evalExpr(String(c.expr), vars) : c.value);
    if (key.includes('.')) {
      const [root, ...rest] = key.split('.');
      let node = (vars[root] = vars[root] || {});
      while (rest.length > 1) node = (node[rest.shift()] ||= {});
      node[rest[0]] = value;
    } else {
      vars[key] = value;
    }
  }
  return vars;
}

const bad = [];
for (const p of probes) {
  let got, err = null;
  try { got = evalBool(p.expr, stateOf(p)); } catch (e) { err = String(e && e.message || e); }
  if (err !== null || got !== p.want) bad.push({ case: p.case, expr: p.expr, want: p.want, got: got ?? null, err });
}
process.stdout.write(JSON.stringify(bad));
`, exprJS, inPath)
	if err := os.WriteFile(script, []byte(src), 0o644); err != nil {
		t.Fatal(err)
	}

	out, err := exec.Command(node, script).CombinedOutput()
	if err != nil {
		t.Fatalf("node не смог прогнать корпус: %v\n%s", err, out)
	}
	var bad []struct {
		Case string `json:"case"`
		Expr string `json:"expr"`
		Want bool   `json:"want"`
		Got  *bool  `json:"got"`
		Err  string `json:"err"`
	}
	if err := json.Unmarshal(out, &bad); err != nil {
		t.Fatalf("непонятный ответ node: %v\n%s", err, out)
	}

	t.Logf("сверено %d выражений; %d случая(ев) пропущено — их состояние рождается потоком или правилами других домов",
		len(probes), skipped)

	if len(bad) > 0 {
		var lines []string
		for _, b := range bad {
			got := "ошибка: " + b.Err
			if b.Got != nil {
				got = fmt.Sprintf("%v", *b.Got)
			}
			lines = append(lines, fmt.Sprintf("%s: %q — корпус ждёт %v, браузер дал %s", b.Case, b.Expr, b.Want, got))
		}
		sort.Strings(lines)
		t.Fatalf("браузерный вычислитель разошёлся с корпусом (%d из %d):\n  %s\n\n"+
			"Автор пробует новеллу в playground, а играет её в приложении: расхождение здесь — "+
			"это «у меня работало по-другому». Правьте server/website/play/expr.js под корпус, "+
			"а не корпус под него: источник правды — язык, а не одна из его реализаций.",
			len(bad), len(probes), strings.Join(lines, "\n  "))
	}
}

// scriptNeedsARealPlayer: состояние такого случая нельзя воспроизвести линейным
// проходом по set/inc — нужен настоящий плеер с переходами либо правила
// соседних домов (Чтец «да-нет» для `default`).
func scriptNeedsARealPlayer(script []map[string]any) bool {
	for _, c := range script {
		switch fmt.Sprint(c["op"]) {
		case "goto", "call", "return", "if", "choice", "label":
			return true
		}
		if _, ok := c["default"]; ok {
			return true
		}
	}
	return false
}

// БРАУЗЕРНЫЙ ПЛЕЕР ИГРАЕТ КОРПУС ЦЕЛИКОМ, а не только считает выражения.
//
// `core.js` — «faithful JS mini-port of LvnPlayer»: поток, вызовы, выборы,
// ввод. Четвёртая реализация языка, и до сих пор её не проверял никто. Прогон
// корпуса НАСТОЯЩИМ плеером снимает то ограничение, из-за которого проверка
// выражений пропускала двадцать три случая: состояние теперь строит сам
// плеер — с переходами, вызовами и пропущенными ветками, — а не модель в тесте.
//
// Первый же прогон нашёл расхождение: `set flag=true` + `inc flag` давали в
// браузере 1 вместо 2 — логическое значение не считалось единицей, хотя язык
// (и Lvn.LvnNum.Value) говорит обратное.
func TestBrowserPlayerPlaysTheCorpus(t *testing.T) {
	node := requireNode(t, "браузерный плеер")
	root := repoRoot(t)
	core := filepath.Join(root, filepath.FromSlash("panel/public/play/core.js"))
	runner := filepath.Join(root, filepath.FromSlash("conformance/browser-runner.mjs"))
	for _, f := range []string{core, runner} {
		if _, err := os.Stat(f); err != nil {
			t.Fatalf("%s не найден (%v) — браузерный плеер это реализация языка, а не артефакт сборки", f, err)
		}
	}

	type kase struct {
		ID       string          `json:"id"`
		Runtimes []string        `json:"runtimes"`
		Picks    []any           `json:"picks"`
		Inputs   []string        `json:"inputs"`
		Doc      json.RawMessage `json:"doc"`
		Expect   struct {
			Stops []map[string]any `json:"stops"`
			Vars  map[string]any   `json:"vars"`
			Stage []map[string]any `json:"stage"`
			// КАДР, К КОТОРОМУ СВОДЯТСЯ КОМАНДЫ. Раньше здесь его не было, и
			// три случая про сцену браузером не гонялись вовсе — а расходятся
			// рантаймы именно на ней: движок читает «show=no» словарём, сырая
			// истинность JS считает непустую строку правдой, и героиня, ушедшая
			// в игре, оставалась стоять в песочнице.
			Scene *struct {
				Bg      *string  `json:"bg"`
				Visible []string `json:"visible"`
			} `json:"scene"`
		} `json:"expect"`
	}
	files, _ := filepath.Glob(filepath.Join(root, "conformance", "cases", "*.json"))
	var want []kase
	for _, f := range files {
		raw, err := os.ReadFile(f)
		if err != nil {
			continue
		}
		var k kase
		if json.Unmarshal(raw, &k) != nil {
			continue
		}
		for _, r := range k.Runtimes {
			if r == "js" {
				want = append(want, k)
			}
		}
	}
	if len(want) == 0 {
		t.Skip("ни один случай не объявлен обязательным для браузера")
	}

	payload, err := json.Marshal(want)
	if err != nil {
		t.Fatal(err)
	}
	casesPath := filepath.Join(t.TempDir(), "cases.json")
	if err := os.WriteFile(casesPath, payload, 0o644); err != nil {
		t.Fatal(err)
	}
	// ОТВЕТ ЧИТАЕМ СО СТАНДАРТНОГО ВЫВОДА, А ЖАЛОБЫ — ОТДЕЛЬНО. Плеер вправе
	// предупреждать (например «в этом выборе ни один вариант не доступен —
	// иду дальше»), и такие строки идут в stderr. Смешанный поток ломал разбор
	// JSON, то есть законное предупреждение роняло сверку — а показать его при
	// разборе всё равно надо, иначе искать причину не по чему.
	cmd := exec.Command(node, runner, core, casesPath)
	var errBuf bytes.Buffer
	cmd.Stderr = &errBuf
	out, err := cmd.Output()
	if err != nil {
		t.Fatalf("node не смог прогнать корпус: %v\n%s\n%s", err, out, errBuf.String())
	}
	var got []struct {
		ID     string           `json:"id"`
		Vars   map[string]any   `json:"vars"`
		Fail   string           `json:"fail"`
		Stops  []map[string]any `json:"stops"`
		Staged []map[string]any `json:"staged"`
		Scene  struct {
			Bg      *string  `json:"bg"`
			Visible []string `json:"visible"`
		} `json:"scene"`
	}
	if err := json.Unmarshal(out, &got); err != nil {
		t.Fatalf("непонятный ответ node: %v\n%s", err, out)
	}

	byID := map[string]int{}
	for i, k := range want {
		byID[k.ID] = i
	}
	var bad []string
	for _, g := range got {
		k := want[byID[g.ID]]
		if g.Fail != "" {
			bad = append(bad, fmt.Sprintf("%s: %s", g.ID, g.Fail))
			continue
		}
		// СЛЕД ОСТАНОВОК: какие остановки, в каком порядке и с каким текстом.
		// Правило то же, что у C#-прогона: сперва ФОРМА (вид остановок подряд),
		// потому что разошедшийся след нечитаем как «поле в середине не то»;
		// затем подробности, и только те, что случай назвал.
		if len(k.Expect.Stops) > 0 {
			wantKinds, gotKinds := stopKinds(k.Expect.Stops), stopKinds(g.Stops)
			if strings.Join(wantKinds, "→") != strings.Join(gotKinds, "→") {
				bad = append(bad, fmt.Sprintf("%s: след остановок разошёлся\n      корпус: %s\n      браузер: %s",
					g.ID, strings.Join(wantKinds, " → "), strings.Join(gotKinds, " → ")))
			} else {
				for i, wantStop := range k.Expect.Stops {
					// Корпус пишет реплику ДВУМЯ формами: строкой (только текст)
					// или объектом {who, text} — когда важно и кто говорит.
					wantVal, ok := wantStop["say"]
					if !ok {
						continue // подробности прочих остановок здесь не сверяем
					}
					gotStop := g.Stops[i]
					switch w := wantVal.(type) {
					case map[string]any:
						for field, v := range w {
							if fmt.Sprint(v) != fmt.Sprint(gotStop[field]) {
								bad = append(bad, fmt.Sprintf("%s: остановка #%d, %s = %v, корпус ждёт %v",
									g.ID, i, field, gotStop[field], v))
							}
						}
					default:
						if fmt.Sprint(w) != fmt.Sprint(gotStop["say"]) {
							bad = append(bad, fmt.Sprintf("%s: остановка #%d — реплика %v, корпус ждёт %v",
								g.ID, i, gotStop["say"], w))
						}
					}
				}
			}
		}

		// ПОСТАНОВОЧНЫЙ ПОТОК: плеер эти команды не трактует, он их пересылает
		// — и корпус проверяет ровно порядок и поля пересланного. Правило то
		// же, что у C#-прогона: длина совпадает, поля сверяются те, что назвал
		// корпус (остальные — дело реализации).
		if len(k.Expect.Stage) > 0 {
			if len(g.Staged) != len(k.Expect.Stage) {
				bad = append(bad, fmt.Sprintf("%s: сцене ушло %d команд, корпус ждёт %d",
					g.ID, len(g.Staged), len(k.Expect.Stage)))
			} else {
				for i, wantCmd := range k.Expect.Stage {
					for field, wantVal := range wantCmd {
						if fmt.Sprint(wantVal) != fmt.Sprint(g.Staged[i][field]) {
							bad = append(bad, fmt.Sprintf("%s: команда #%d, поле %s = %v, корпус ждёт %v",
								g.ID, i, field, g.Staged[i][field], wantVal))
						}
					}
				}
			}
		}
		// КАДР: кто в итоге на экране и какой фон. Сверяется по тем же
		// правилам, что у C#-прогона (липкое размещение, clear снимает всех,
		// obj идёт трактом актёра) — иначе «одна новелла, два рантайма»
		// проверялось бы только на потоке команд, а не на его результате.
		if k.Expect.Scene != nil {
			if k.Expect.Scene.Bg != nil && fmt.Sprint(*k.Expect.Scene.Bg) != fmt.Sprint(deref(g.Scene.Bg)) {
				bad = append(bad, fmt.Sprintf("%s: фон кадра %q, корпус ждёт %q",
					g.ID, deref(g.Scene.Bg), *k.Expect.Scene.Bg))
			}
			if k.Expect.Scene.Visible != nil {
				want := append([]string(nil), k.Expect.Scene.Visible...)
				got := append([]string(nil), g.Scene.Visible...)
				sort.Strings(want)
				sort.Strings(got)
				if strings.Join(want, ",") != strings.Join(got, ",") {
					bad = append(bad, fmt.Sprintf("%s: на экране [%s], корпус ждёт [%s]",
						g.ID, strings.Join(got, ", "), strings.Join(want, ", ")))
				}
			}
		}

		for name, wantVal := range k.Expect.Vars {
			gotVal, ok := g.Vars[name]
			if !ok {
				bad = append(bad, fmt.Sprintf("%s: переменной %q нет вовсе", g.ID, name))
				continue
			}
			// Сравниваем по печатному виду: JSON-числа приезжают float64 с
			// обеих сторон, а корпус пишет их как есть.
			if fmt.Sprint(wantVal) != fmt.Sprint(gotVal) {
				bad = append(bad, fmt.Sprintf("%s: %s = %v, корпус ждёт %v", g.ID, name, gotVal, wantVal))
			}
		}
	}
	sort.Strings(bad)

	if len(bad) > 0 {
		t.Fatalf("браузерный плеер разошёлся с корпусом (%d):\n  %s\n\n"+
			"Автор пробует новеллу в playground, а играет её в приложении. "+
			"Правьте panel/public/play/core.js под корпус: источник правды — язык.",
			len(bad), strings.Join(bad, "\n  "))
	}
	t.Logf("браузерный плеер сыграл %d случая(ев) корпуса без расхождений", len(got))
}

// deref: пустая строка вместо отсутствующего фона — корпус пишет его строкой.
func deref(s *string) string {
	if s == nil {
		return ""
	}
	return *s
}

// stopKinds: вид каждой остановки по порядку — say / choice / input / end.
func stopKinds(stops []map[string]any) []string {
	out := make([]string, 0, len(stops))
	for _, s := range stops {
		for _, kind := range []string{"say", "choice", "input", "end"} {
			if _, ok := s[kind]; ok {
				out = append(out, kind)
				break
			}
		}
	}
	return out
}

// ЭКСПОРТ — ПУТЬ ДОСТАВКИ, А НЕ ЕЩЁ ОДИН ЯЗЫК.
//
// `export.js` собирает из новеллы самостоятельный .html: внутрь попадают тот же
// `core.js` и тот же `expr.js`, только со снятыми строками import/export.
// Значит экспортированная игра обязана играть ровно так же, как песочница — и
// доказывается это не прогоном (в готовом файле нет модулей, его не
// импортируешь), а тем, что упакованный код ПОСТРОЧНО совпадает с исходником.
//
// Правило снятия — `^export ` — вырезает СЛОВО, а не строку. Разница между
// `/^export /` и `/^export .*$/` не видна глазом и стоит всей игры: во втором
// случае из упаковки исчезают объявления функций, и экспортированный файл
// падает у игрока, а в песочнице всё работает.
func TestExportPacksTheSameLanguage(t *testing.T) {
	node := requireNode(t, "упаковка экспорта")
	root := repoRoot(t)
	checker := filepath.Join(root, filepath.FromSlash("conformance/export-check.mjs"))
	playDir := filepath.Join(root, filepath.FromSlash("panel/public/play"))
	for _, f := range []string{checker, filepath.Join(playDir, "export.js")} {
		if _, err := os.Stat(f); err != nil {
			t.Fatalf("%s не найден: %v", f, err)
		}
	}

	out, err := exec.Command(node, checker, playDir).CombinedOutput()
	if err != nil {
		t.Fatalf("node не смог собрать экспорт: %v\n%s", err, out)
	}
	var problems []string
	if err := json.Unmarshal(out, &problems); err != nil {
		t.Fatalf("непонятный ответ node: %v\n%s", err, out)
	}
	if len(problems) > 0 {
		sort.Strings(problems)
		t.Fatalf("упаковка изменила язык (%d):\n  %s\n\n"+
			"Экспортированная игра обязана быть тем же плеером. Смотрите strip в export.js: "+
			"он снимает СЛОВО `export`, а не строку целиком.",
			len(problems), strings.Join(problems, "\n  "))
	}
}

// requireNode: где node есть — проверяем, где нет — пропускаем. НО НЕ НА CI.
//
// Пропуск без node честен на чужой машине и опасен на сборочной: тест
// «проходит», и три стража языка молчат, создавая ровно ту уверенность, ради
// борьбы с которой они заведены. Так и было — в Go-джобе node не стоял вовсе.
// Теперь он там есть, а переменная LVN_REQUIRE_NODE делает его пропажу
// красной: если окружение объявило себя обязанным проверять, молчать нельзя.
func requireNode(t *testing.T, what string) string {
	t.Helper()
	node, err := exec.LookPath("node")
	if err == nil {
		return node
	}
	if os.Getenv("LVN_REQUIRE_NODE") != "" {
		t.Fatalf("node не найден, а окружение требует проверки (%s)", what)
	}
	t.Skipf("node не установлен — %s не проверяется на этой машине", what)
	return ""
}
