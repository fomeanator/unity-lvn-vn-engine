package main

// agent.go — «Коннект»: один файл, который делает ИИ работоспособным
// сотрудником этой студии за одну вставку в чат.
//
// Задача с натуры: ребёнок сидит в веб-IDE, жмёт одну кнопку, получает файл и
// кидает его ИИ. Дальше он говорит «сделай игру про драконов», а ИИ пишет и
// публикует её В ТУ ЖЕ студию, которую ребёнок видит на экране. Всё, что для
// этого нужно, лежит в одном файле: как устроен язык, куда стучаться и каким
// ключом.
//
// Отсюда два эндпойнта:
//
//	GET  /v1/admin/agent-bundle   — сам файл: живая шапка с адресом и ключом
//	                                ЭТОГО сервера + встроенная документация.
//	POST /v1/admin/agent/publish  — публикация .lvns одним вызовом: компиляция,
//	                                структурная проверка, запись, регистрация в
//	                                манифесте, ссылка на игру в ответе.
//
// Почему публикация именно ОДНИМ вызовом. Без неё файл-инструкция вынужден
// начинаться со слов «поставь Go, собери lvnconv, скомпилируй» — то есть с
// барьера, ради снятия которого он и существует. Ребёнок не поставит тулчейн, и
// чужой ИИ в браузерном чате тоже. Одна HTTP-ручка, принимающая ИСХОДНИК, —
// разница между «работает» и «инструкция к тому, чего нет».
//
// Про ключ в файле — прямо и без иллюзий: файл содержит админ-токен, потому что
// иначе он бесполезен, и это делает его СЕКРЕТОМ. Он открывает запись во весь
// контент студии. Отдавать его чужому чат-боту — осознанное решение владельца
// сервера; в шапке файла об этом сказано первым абзацем, а не мелким шрифтом.

import (
	"bytes"
	_ "embed"
	"encoding/json"
	"fmt"
	"log"
	"net/http"
	"os"
	"path"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"time"

	"github.com/fomeanator/elvin/tools/lvnconv/importer"
)

// agentBundleDocs — документация, собранная в один файл на этапе сборки
// (agent_bundle_gen.go делает сборку, TestAgentBundleIsUpToDate стережёт от
// расхождения). Встроена в бинарь: на проде из репозитория ничего нет, там
// только исполняемый файл и каталог контента.
//
//go:embed agent-bundle.md
var agentBundleDocs string

// handleAgentBundle отдаёт файл целиком: живая шапка + встроенные доки.
//
// Шапка генерируется НА ЗАПРОС, а не встроена: у каждого сервера свой адрес и
// свой ключ (у племянника будет отдельный инстанс на VPS), и зашитая шапка
// увела бы его ИИ на чужую студию.
func (s *server) handleAgentBundle(w http.ResponseWriter, r *http.Request) {
	// ВЛАДЕЛЕЦ, хотя это всего лишь чтение файла. Внутри файла — ключ студии,
	// а ключ открывает всё; отдать его смотрящему значит выдать ему права
	// владельца в обход ролей. Правило простое: где в ответе есть ключ, там
	// право не ниже, чем даёт сам ключ.
	if !adminAllowedRole(w, r, s.adminToken, RoleOwner) {
		return
	}
	base := requestBase(r)
	w.Header().Set("Content-Type", "text/markdown; charset=utf-8")
	w.Header().Set("Content-Disposition", `attachment; filename="elvin-agent.md"`)
	w.Header().Set("Cache-Control", "no-store") // содержит ключ
	_, _ = io_WriteString(w, agentHeader(base, s.adminToken)+agentBundleDocs)
}

// io_WriteString вынесен, чтобы не тащить io ради одной строки.
func io_WriteString(w http.ResponseWriter, s string) (int, error) { return w.Write([]byte(s)) }

// requestBase восстанавливает внешний адрес сервера так, как его видит браузер,
// включая обратный прокси (на проде сервер стоит за ним, и без X-Forwarded-Proto
// в файл уехал бы http://, по которому ИИ получил бы редирект вместо ответа).
func requestBase(r *http.Request) string {
	scheme := "http"
	if r.TLS != nil {
		scheme = "https"
	}
	if p := r.Header.Get("X-Forwarded-Proto"); p != "" {
		scheme = strings.Split(p, ",")[0]
	}
	host := r.Host
	if h := r.Header.Get("X-Forwarded-Host"); h != "" {
		host = strings.Split(h, ",")[0]
	}
	return scheme + "://" + strings.TrimSpace(host)
}

func agentHeader(base, token string) string {
	return `# Elvin — подключение и полный справочник по языку

Это ОДИН самодостаточный файл. В нём всё, что нужно, чтобы писать и публиковать
игры на движке Elvin: доступ к конкретной студии, формат публикации и полное
описание языка. Ничего доустанавливать не нужно — ни Go, ни Unity, ни SDK.

> **Внимание, это секрет.** Ниже настоящий ключ записи от студии. Он позволяет
> менять и удалять любой её контент. Файл нельзя выкладывать в открытый доступ
> и класть в публичный репозиторий. Если ключ утёк — владелец меняет его на
> сервере (флаг ` + "`-admin-token`" + `) и скачивает файл заново.

## Доступ

    Адрес студии:  ` + base + `
    Ключ записи:   ` + token + `

Все запросы к ` + "`/v1/admin/…`" + ` требуют заголовок:

    Authorization: Bearer ` + token + `

## Как опубликовать игру — один запрос

Пишешь исходник на ` + "`.lvns`" + ` (язык описан ниже) и отправляешь ЕГО. Сервер сам
скомпилирует, проверит структуру, сохранит и зарегистрирует главу в студии.

    POST ` + base + `/v1/admin/agent/publish
    Authorization: Bearer ` + token + `
    Content-Type: application/json

    {
      "id":      "dragons",
      "name":    "Драконы",
      "chapter": 1,
      "lvns":    "scene dragons\n\nТы просыпаешься в пещере.\n- Встать -> up\n\n:up\nПора.\n-> __end\n"
    }

Пример целиком:

    curl -X POST ` + base + `/v1/admin/agent/publish \
      -H "Authorization: Bearer ` + token + `" \
      -H "Content-Type: application/json" \
      -d @game.json

Ответ при успехе:

    {"ok":true,"id":"dragons","commands":6,"warnings":[],"play_url":"` + base + `/","script_url":"/content/scripts/dragons-ch01.lvn"}

Что означает ответ:

* ` + "`warnings`" + ` — ПУСТОЙ список это цель. Каждое предупреждение это реальный
  дефект: висячий переход, глава кончается молча, неизвестная команда. Если
  список не пуст — почини исходник и опубликуй снова, тем же ` + "`id`" + `.
* Ошибка компиляции возвращается кодом 400 с точным номером строки. Структурная
  ошибка — кодом 422, и в этом случае НИЧЕГО не записано: прежняя версия игры
  цела.
* Повторная публикация с тем же ` + "`id`" + ` и ` + "`chapter`" + ` заменяет главу; прошлая версия
  уходит в историю студии, её можно откатить из панели.
* ` + "`replaced`" + ` в ответе — под твоим текстом лежал ЧУЖОЙ, и он только что заменён
  (значение — время той редакции). Над новеллой работают и люди: если ты этой
  замены не планировал, скажи об этом человеку, прежняя редакция цела в истории
  студии. Поля нет — значит главы не было или текст совпал побайтово.

## Игра из нескольких глав: общий файл

Как только глав больше одной, таблицы, функции и пресеты выносят в ОБЩИЙ файл и
подключают его строкой ` + "`include \"механики.lvns\"`" + `. Такой файл публикуется
по имени, а не как глава — у него нет ` + "`scene`" + `, он не игра:

    POST ` + base + `/v1/admin/agent/publish
    {"path": "механики.lvns", "lvns": "сила = {}\nсила = put(сила, \"огонь\", 3)\n"}

Он сохраняется под своим именем и НЕ компилируется отдельно: его проверит первая
же глава, которая его подключит. Публикуй общий файл ДО глав, иначе глава не
найдёт того, что подключает. Имя — только имя файла, без каталогов.

Полезное рядом:

    GET ` + base + `/v1/content/manifest      — что уже опубликовано
    GET ` + base + `/content/scripts/<имя>.lvn — забрать скомпилированную главу

## Порядок работы

1. Прочитай раздел «Шпаргалка» ниже — это одна страница, её достаточно для
   первой игры.
2. Напиши ` + "`.lvns`" + ` и опубликуй. Смотри на ` + "`warnings`" + `.
3. Добивайся пустого списка предупреждений. Это и есть критерий готовности:
   он проверяет связность, а не орфографию.
4. Нужна деталь — ищи её в «Полном описании языка» и «Возможностях движка»
   ниже. Не выдумывай синтаксис: чего нет в этом файле, того нет в языке.

Дальше идёт документация движка целиком.

---

`
}

// publishReq — то, что присылает ИИ. Намеренно минимально: заставлять его
// собирать манифест руками означало бы вернуть тот же барьер с другой стороны.
type publishReq struct {
	ID      string `json:"id"`
	Name    string `json:"name"`
	Chapter int    `json:"chapter"`
	Lvns    string `json:"lvns"`
	BgURL   string `json:"bg_url"`
	// Path публикует ОБЩИЙ ФАЙЛ, а не главу: имя берётся как есть, ничего не
	// компилируется и в манифест не попадает. Без него игра из нескольких глав
	// через API невыразима вовсе — публикация именует всё как <id>-chNN.lvns,
	// а `include "механики.lvns"` ждёт своё имя и не находит его. Библиотека
	// проверяется тогда, когда её подключает глава: только там сочетание
	// становится настоящим.
	Path string `json:"path"`
}

func (s *server) handleAgentPublish(w http.ResponseWriter, r *http.Request) {
	if !adminAllowed(w, r, s.adminToken) {
		return
	}
	if !onlyMethod(w, r, http.MethodPost) {
		return
	}
	var req publishReq
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, bodyBulk)).Decode(&req); err != nil {
		http.Error(w, "invalid JSON: "+err.Error(), http.StatusBadRequest)
		return
	}
	req.ID = strings.TrimSpace(req.ID)
	req.Path = strings.TrimSpace(req.Path)
	if strings.TrimSpace(req.Lvns) == "" {
		http.Error(w, "lvns is empty — send the .lvns source, not a file path", http.StatusBadRequest)
		return
	}
	if req.Path != "" {
		s.publishSharedFile(w, req)
		return
	}
	if !validID(req.ID) {
		http.Error(w, "id must match [A-Za-z0-9_-]+", http.StatusBadRequest)
		return
	}
	if req.Chapter <= 0 {
		req.Chapter = 1
	}
	if req.Name == "" {
		req.Name = req.ID
	}

	rel := fmt.Sprintf("scripts/%s-ch%02d.lvn", req.ID, req.Chapter)

	// 1. Компиляция. Ошибка здесь — ошибка автора, с номером строки; ничего
	// постоянного на диск не идёт.
	//
	// Компилируем ИЗ ВРЕМЕННОГО ФАЙЛА, лежащего в том же каталоге scripts/, а
	// не из строки в памяти. Причина — `include`: путь в нём резолвится
	// относительно подключающего ФАЙЛА, а у текста каталога нет. Игра больше
	// чем на одну главу всегда выносит общие механики в отдельный файл, то
	// есть первая же настоящая игра упирается в это. Каталог тот же, куда
	// ляжет результат, поэтому include видит ровно то, что увидит IDE.
	compiled, srcLine, err := s.compileInScripts(req)
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{
			"ok": false, "stage": "compile", "error": err.Error(),
		})
		return
	}

	// 2. Тот же структурный гейт, через который проходит любая запись скрипта
	// (lvnguard.go). Отказ — до единой записи на диск, поэтому неудачная
	// публикация оставляет прошлую версию игры нетронутой.
	findings := s.checkLvnAt(rel, compiled, srcLine)
	if findings.blocked() {
		writeJSON(w, http.StatusUnprocessableEntity, map[string]any{
			"ok": false, "stage": "check", "errors": orEmpty(findings.Errors),
			"warnings": orEmpty(findings.Warnings),
		})
		return
	}

	// 3. Запись. Исходник кладём рядом с результатом: он и есть то, что автор
	// (или ИИ) правит в следующий раз, и то, что открывает IDE.
	srcRel := strings.TrimSuffix(rel, ".lvn") + ".lvns"
	lk := s.writeLock()
	lk.Lock()
	// ПЕРЕЗАПИСЬ ЧУЖОЙ РАБОТЫ ОБЯЗАНА БЫТЬ ВИДНА. Агент версий не читает и
	// прислать их не может: он публикует главу целиком, не зная, правил ли её
	// кто-то с прошлого раза. Требовать от него If-Match значило бы сломать
	// тракт публикации; но промолчать о том, что под его текстом лежал ЧУЖОЙ,
	// нельзя — именно так работа исчезает незамеченной. Ответ и журнал говорят
	// «заменено», а прежняя редакция остаётся в .history.
	replaced := replacedSource(s.content, srcRel, req.Lvns)
	werr := s.writeContentFile(srcRel, []byte(req.Lvns))
	if werr == nil {
		werr = s.writeContentFile(rel, compiled)
	}
	var mErr error
	if werr == nil {
		mErr = s.registerChapter(req, rel)
	}
	lk.Unlock()
	if werr != nil {
		http.Error(w, "write failed: "+werr.Error(), http.StatusInternalServerError)
		return
	}
	if mErr != nil {
		// Скрипт уже лежит и играбелен по прямой ссылке — врать про полный
		// успех нельзя, но и терять сделанное незачем.
		half := map[string]any{
			"ok": false, "stage": "manifest", "error": mErr.Error(),
			"script_url": "/content/" + rel,
			"warnings":   orEmpty(findings.Warnings),
		}
		if replaced != "" {
			half["replaced"] = replaced
		}
		writeJSON(w, http.StatusOK, half)
		return
	}

	var doc struct {
		Script []any `json:"script"`
	}
	_ = json.Unmarshal(compiled, &doc)
	out := map[string]any{
		"ok": true, "id": req.ID, "chapter": req.Chapter,
		"commands": len(doc.Script), "warnings": orEmpty(findings.Warnings),
		"script_url": "/content/" + rel,
		"play_url":   requestBase(r) + "/",
	}
	if replaced != "" {
		out["replaced"] = replaced
		log.Printf("agent publish %s: заменена редакция от %s (прежняя — в .history)", srcRel, replaced)
	}
	writeJSON(w, http.StatusOK, out)
}

// replacedSource: если на месте уже лежал ДРУГОЙ текст, вернуть время его
// последней правки. Пустая строка — файла не было или он побайтово тот же
// (повторная публикация того же текста ничего не заменяет и молчать о ней
// правильно).
func replacedSource(content, rel, next string) string {
	b, err := os.ReadFile(filepath.Join(content, filepath.FromSlash(rel)))
	if err != nil || string(b) == next {
		return ""
	}
	info, err := os.Stat(filepath.Join(content, filepath.FromSlash(rel)))
	if err != nil {
		return ""
	}
	return info.ModTime().UTC().Format(time.RFC3339)
}

// sharedNameRe — имя общего файла: только само имя, без каталогов, и только
// .lvns. Свобода тут не нужна, а любой сегмент пути открыл бы запись куда
// угодно под контент-корнем.
var sharedNameRe = regexp.MustCompile(`^[A-Za-z0-9_-]+\.lvns$`)

// packagePathRe — файл ПАКЕТА: @scope/pkg/…/file.lvns (см. tools/lvnconv/
// internal/deps). Скоуп и имя пакета — строчные, сегменты без точек в начале,
// ".." не пройдёт по построению. Такие файлы ложатся в scripts/lvns_packages/,
// где их находит "@"-резолвер include (include.go, resolveDiskPath).
var packagePathRe = regexp.MustCompile(`^@[a-z0-9][a-z0-9._-]*/[a-z0-9][a-z0-9._-]*(/[A-Za-z0-9_][A-Za-z0-9_.-]*)+\.lvns$`)

// publishSharedFile кладёт общий файл (тот, который подключают через include)
// под его собственным именем. Не компилирует и не регистрирует: у библиотеки
// нет `scene`, отдельная компиляция дала бы .lvn, который никто не играет, и
// предупреждение о недостающем заголовке. Её проверит первая же глава, которая
// её подключит, — и это правильный момент.
func (s *server) publishSharedFile(w http.ResponseWriter, req publishReq) {
	var rel string
	switch {
	case sharedNameRe.MatchString(req.Path):
		rel = "scripts/" + req.Path
	case packagePathRe.MatchString(req.Path):
		// Файл пакета: главы подключают его полным путём
		// `include "@scope/pkg/file.lvns"`.
		rel = "scripts/lvns_packages/" + req.Path
	default:
		http.Error(w, `path must be a bare file name like "mechanics.lvns" or a package path like "@scope/pkg/file.lvns"`, http.StatusBadRequest)
		return
	}
	lk := s.writeLock()
	lk.Lock()
	err := s.writeContentFile(rel, []byte(req.Lvns))
	lk.Unlock()
	if err != nil {
		http.Error(w, "write failed: "+err.Error(), http.StatusInternalServerError)
		return
	}
	// Правка общего файла ОБЯЗАНА пересобрать главы, которые его подключают.
	// Иначе получается вранье: автор поправил боевую формулу, студия сказала
	// «сохранено», а на телефоне прежняя игра — потому что играется
	// СКОМПИЛИРОВАННЫЙ .lvn, а он остался вчерашним. И заметно это не сразу, а
	// когда перестаёшь понимать, почему правки «не работают».
	rebuilt, failed := s.rebuildDependents(req.Path)
	writeJSON(w, http.StatusOK, map[string]any{
		"ok": true, "kind": "shared", "path": rel,
		"rebuilt": rebuilt, "failed": failed,
		"note": "общий файл сохранён; главы, которые его подключают, пересобраны",
	})
}

// handleRebuild — та же пересборка, вызываемая явно: студия сохраняет общий файл
// обычной ручкой ассетов (PUT /v1/admin/assets/), а не через publish, и без
// этого вызова правка механик не доезжала до игры.
func (s *server) handleRebuild(w http.ResponseWriter, r *http.Request) {
	if !adminAllowed(w, r, s.adminToken) {
		return
	}
	var req struct {
		Path string `json:"path"`
	}
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, bodySmall)).Decode(&req); err != nil {
		http.Error(w, "invalid JSON: "+err.Error(), http.StatusBadRequest)
		return
	}
	name := strings.TrimSpace(req.Path)
	shown := "scripts/" + name
	switch {
	case strings.HasPrefix(name, "@"):
		if !packagePathRe.MatchString(name) {
			http.Error(w, `package path must look like "@scope/pkg/file.lvns"`, http.StatusBadRequest)
			return
		}
		shown = "scripts/lvns_packages/" + name
	default:
		name = path.Base(name)
		if !sharedNameRe.MatchString(name) {
			http.Error(w, `path must be a .lvns file name`, http.StatusBadRequest)
			return
		}
		shown = "scripts/" + name
	}
	rebuilt, failed := s.rebuildDependents(name)
	writeJSON(w, http.StatusOK, map[string]any{
		"ok": len(failed) == 0, "path": shown,
		"rebuilt": rebuilt, "failed": failed,
	})
}

// reIncludeLine ловит директиву подключения в исходнике главы. Кавычки
// обязательны — так же, как в самом компиляторе.
var reIncludeLine = regexp.MustCompile(`(?m)^[ \t]*include[ \t]+"([^"]+)"[ \t]*$`)

// depID — идентификатор узла, на который ссылается include из источника src.
// "@"-путь — узел пакета как есть; относительный путь ИЗ пакета — файл того же
// пакета (include в пакете резолвится относительно подключающего файла);
// относительный путь из плоского скрипта — голое имя соседа.
func depID(src, arg string) string {
	if strings.HasPrefix(arg, "@") {
		return path.Clean(arg)
	}
	if strings.Contains(src, "/") {
		return path.Clean(path.Join(path.Dir(src), arg))
	}
	return path.Base(arg)
}

// reSceneLine отличает ГЛАВУ от библиотеки: главa объявляет сцену, библиотека
// только даёт таблицы и функции.
var reSceneLine = regexp.MustCompile(`(?m)^[ \t]*scene[ :][ \t]*\S`)

// rebuildDependents перекомпилирует каждую главу, которая ПРЯМО ИЛИ ЧЕРЕЗ ЦЕПОЧКУ
// подключает изменённый файл, и переписывает её .lvn.
//
// Транзитивность здесь не роскошь: общий файл сам может подключать другой
// (механики → таблицы), и пересборка «только прямых» тихо оставила бы половину
// глав на старом коде.
//
// Глава, которая перестала компилироваться, НЕ переписывается: на диске остаётся
// последняя рабочая версия, а имя и ошибка уезжают в ответ. Иначе одна опечатка
// в общем файле обрушила бы сразу всю игру, и играть стало бы нечем.
func (s *server) rebuildDependents(changed string) ([]string, map[string]string) {
	dir := filepath.Join(s.content, "scripts")
	entries, err := os.ReadDir(dir)
	if err != nil {
		return nil, nil
	}

	// Кто что подключает, и кто из узлов — ГЛАВА. Идентификатор узла:
	// плоский общий файл — голое имя ("mechanics.lvns"), файл пакета — его
	// полный @-путь ("@scope/pkg/duel.lvns"), ровно как автор пишет в include.
	includes := map[string][]string{}
	isChapter := map[string]bool{}
	var sources []string
	addSource := func(id string, raw []byte) {
		sources = append(sources, id)
		// Глава объявляет сцену; библиотека — нет. Пересобирать библиотеку
		// отдельно бессмысленно и вредно: у неё нет `scene`, компиляция даёт
		// .lvn, который никто не играет, и мусор в каталоге контента. Первая
		// версия так и делала — поймал тест на цепочке включений.
		if reSceneLine.MatchString(string(raw)) {
			isChapter[id] = true
		}
		for _, m := range reIncludeLine.FindAllStringSubmatch(string(raw), -1) {
			includes[id] = append(includes[id], depID(id, m[1]))
		}
	}
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".lvns") || strings.HasPrefix(e.Name(), ".") {
			continue
		}
		raw, rerr := os.ReadFile(filepath.Join(dir, e.Name()))
		if rerr != nil {
			continue
		}
		addSource(e.Name(), raw)
	}
	// Файлы пакетов — рёбра графа: правка пакета обязана пересобрать главы,
	// которые тянут его напрямую или через другой пакет.
	pkgRoot := filepath.Join(dir, "lvns_packages")
	_ = filepath.Walk(pkgRoot, func(p string, info os.FileInfo, err error) error {
		if err != nil || info.IsDir() || !strings.HasSuffix(p, ".lvns") {
			return nil
		}
		raw, rerr := os.ReadFile(p)
		if rerr != nil {
			return nil
		}
		relp, rerr2 := filepath.Rel(pkgRoot, p)
		if rerr2 != nil {
			return nil
		}
		addSource(filepath.ToSlash(relp), raw)
		return nil
	})

	// Транзитивное «зависит от changed».
	changedID := path.Base(changed)
	if strings.HasPrefix(changed, "@") {
		changedID = path.Clean(changed)
	}
	affected := map[string]bool{changedID: true}
	for range sources { // граница: длиннее цепочки быть не может
		grew := false
		for f, deps := range includes {
			if affected[f] {
				continue
			}
			for _, d := range deps {
				if affected[d] {
					affected[f] = true
					grew = true
					break
				}
			}
		}
		if !grew {
			break
		}
	}

	var rebuilt []string
	failed := map[string]string{}
	sort.Strings(sources)
	for _, f := range sources {
		// Пакетные узлы (id с "/") — только рёбра графа: даже пакет со `scene`
		// не глава студии, его .lvn никто не играет.
		if f == changedID || !affected[f] || !isChapter[f] || strings.Contains(f, "/") {
			continue
		}
		src := filepath.Join(dir, f)
		compiled, cerr := importer.CompileLvnsFile(src)
		if cerr != nil {
			failed[f] = cerr.Error()
			continue
		}
		rel := "scripts/" + strings.TrimSuffix(f, ".lvns") + ".lvn"
		if fnd := s.checkLvn(rel, compiled); fnd.blocked() {
			failed[f] = strings.Join(fnd.Errors, "; ")
			continue
		}
		lk := s.writeLock()
		lk.Lock()
		werr := s.writeContentFile(rel, compiled)
		lk.Unlock()
		if werr != nil {
			failed[f] = werr.Error()
			continue
		}
		rebuilt = append(rebuilt, rel)
	}
	if len(failed) == 0 {
		failed = nil
	}
	return rebuilt, failed
}

// compileInScripts компилирует присланный исходник так, как его увидит студия:
// из файла в каталоге scripts/, чтобы `include` резолвился относительно
// соседей. Временный файл удаляется всегда — и на успехе, и на ошибке, — а имя
// начинается с точки: статика отказывает любому пути с сегментом на точку
// (hasDotSegment), поэтому даже в окне между записью и удалением исходник
// нельзя скачать.
func (s *server) compileInScripts(req publishReq) ([]byte, []int, error) {
	dir := filepath.Join(s.content, "scripts")
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return nil, nil, err
	}
	tmp, err := os.CreateTemp(dir, fmt.Sprintf(".publish-%s-*.lvns", req.ID))
	if err != nil {
		return nil, nil, err
	}
	defer os.Remove(tmp.Name())
	if _, err := tmp.WriteString(req.Lvns); err != nil {
		tmp.Close()
		return nil, nil, err
	}
	if err := tmp.Close(); err != nil {
		return nil, nil, err
	}
	return importer.CompileLvnsFileWithLines(tmp.Name())
}

// writeContentFile пишет файл под контент-корнем со снапшотом в историю.
// Вызывается ПОД замком записи (см. (*server).writeLock).
func (s *server) writeContentFile(rel string, data []byte) error {
	dst := filepath.Join(s.content, filepath.FromSlash(rel))
	if err := os.MkdirAll(filepath.Dir(dst), 0o755); err != nil {
		return err
	}
	// ТЕ ЖЕ БАЙТЫ — НЕ ЗАПИСЬ. Повторная публикация того же текста иначе
	// оставляет новый mtime и снимок в истории: история студии засоряется
	// одинаковыми редакциями, а откат «на предыдущую» возвращает ту же самую.
	// Содержание файла от пропуска не меняется — на диске уже ровно оно.
	if old, err := os.ReadFile(dst); err == nil && bytes.Equal(old, data) {
		return nil
	}
	snapshotHistory(s.content, rel)
	return atomicWrite(dst, data, 0o644)
}

// registerChapter вписывает главу в манифест, создавая титул при первой
// публикации и ЗАМЕНЯЯ запись при повторной. Вызывается под замком записи.
//
// Манифест правится как обычный JSON-объект, а не через типизированную
// структуру: у титулов есть поля, о которых этот код не знает (обложки,
// экономика, разблокировки), и разбор в структуру потерял бы их при первой же
// публикации ребёнка.
func (s *server) registerChapter(req publishReq, scriptRel string) error {
	path := filepath.Join(s.content, "manifest.json")
	raw, err := os.ReadFile(path)
	if err != nil {
		return err
	}
	var m map[string]any
	if err := json.Unmarshal(raw, &m); err != nil {
		return fmt.Errorf("manifest.json is not valid JSON: %w", err)
	}
	// СНИМОК ДО ПРАВОК — чтобы узнать, изменила ли публикация каталог вообще.
	// Сериализуется ЗДЕСЬ, пока rev ещё прежний: сравнивать надо содержание, а
	// не счётчик редакций. Почему это важно — у записи в конце функции.
	before, err := json.Marshal(m)
	if err != nil {
		return err
	}
	titles, _ := m["titles"].([]any)

	var title map[string]any
	for _, t := range titles {
		if tm, ok := t.(map[string]any); ok && tm["id"] == req.ID {
			title = tm
			break
		}
	}
	if title == nil {
		title = map[string]any{"id": req.ID, "name": req.Name, "subtitle": ""}
		titles = append(titles, title)
	} else if req.Name != req.ID {
		title["name"] = req.Name // переименование при повторной публикации
	}

	seasons, _ := title["seasons"].([]any)
	if len(seasons) == 0 {
		seasons = []any{map[string]any{"chapters": []any{}}}
	}
	season, _ := seasons[0].(map[string]any)
	if season == nil {
		season = map[string]any{"chapters": []any{}}
		seasons[0] = season
	}
	chapters, _ := season["chapters"].([]any)

	entry := map[string]any{
		"id":         fmt.Sprintf("%s-ch%02d", req.ID, req.Chapter),
		"name":       fmt.Sprintf("%02d", req.Chapter),
		"number":     req.Chapter,
		"script_url": "/content/" + scriptRel,
	}
	if req.BgURL != "" {
		entry["bg_url"] = req.BgURL
	}
	replaced := false
	for i, c := range chapters {
		if cm, ok := c.(map[string]any); ok && cm["id"] == entry["id"] {
			// Сохраняем поля, которые проставили в панели руками (обложка,
			// имя главы) — публикация меняет скрипт, а не оформление.
			for k, v := range cm {
				if _, taken := entry[k]; !taken {
					entry[k] = v
				}
			}
			if nm, ok := cm["name"].(string); ok && nm != "" {
				entry["name"] = nm
			}
			chapters[i] = entry
			replaced = true
			break
		}
	}
	if !replaced {
		chapters = append(chapters, entry)
	}
	season["chapters"] = chapters
	seasons[0] = season
	title["seasons"] = seasons
	m["titles"] = titles

	// КАТАЛОГ НЕ ИЗМЕНИЛСЯ — ЕГО НЕ ТРОГАЮТ.
	//
	// Публикация той же главы тем же текстом (повтор после оборванного ответа,
	// «выложить всё» из панели, переиздание из CI) оставляет манифест ровно
	// таким же — кроме rev. А rev входит в общую версию контента, по которой
	// клиент решает, что мир изменился: замер 04.09 на живом сервере показал,
	// что холостое переиздание меняет версию, и КАЖДЫЙ играющий идёт за
	// манифестом (на проде 435 КБ), перечитывает открытую главу мимо кэша и
	// пересобирает фигуры на сцене — ради нуля новостей. Тот же rev тянулся и
	// за обычной правкой реплики: клиент умеет «каталог тот же — за ним не
	// ходим», но сервер не давал ему такого случая ни разу.
	//
	// Отсюда правило: сравниваем содержание, и если публикация ничего в нём не
	// изменила — не пишем ни манифест, ни снимок в историю, ни rev. Кто читал
	// манифест до этой публикации, не устарел: он и не изменился.
	after, err := json.Marshal(m)
	if err != nil {
		return err
	}
	if bytes.Equal(before, after) {
		return nil
	}
	// Серверная запись манифеста двигает rev вперёд: агент, читавший манифест
	// до этой публикации, обязан перечитать его перед своим PUT.
	if v, ok := m["rev"].(float64); ok {
		m["rev"] = int(v) + 1
	} else {
		m["rev"] = 1
	}
	out, err := json.MarshalIndent(m, "", "  ")
	if err != nil {
		return err
	}
	snapshotHistory(s.content, "manifest.json")
	return atomicWrite(path, append(out, '\n'), 0o644)
}
