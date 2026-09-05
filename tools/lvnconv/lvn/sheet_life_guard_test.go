package lvn

import (
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// НАКЛАДНОЙ ЭКРАН ЗАКРЫВАЕТСЯ ВСЕГДА.
//
// Четыре экрана оболочки живут одним скелетом: «уже открыт — уйти», поднять
// флаг, дождаться затвора, и в `finally` — прибраться и флаг снять. Скелет
// один, а написан четырьмя копиями (`LvnOverlayScreen`, `CgGalleryScreen`,
// `PopupScreen`, `WardrobeSheet`), потому что общего предка у них нет: каждый
// сам себе `VisualElement`.
//
// Опасна в этом скелете ровно одна строка. Снять флаг НАДО в `finally`, а не
// после ожидания: отмена главы, закрытие приложения, исключение внутри — и
// экран остаётся «открытым» навсегда. Следующий вызов упрётся в собственную
// защиту от повторного входа и молча не откроется. Ни ошибки, ни лога:
// гардероб просто перестаёт открываться до перезахода (живой случай был).
//
// Сводить копии в один дом дороже, чем держать: тела у них разные по существу
// (проявление, кошелёк, просмотрщик), а совпадает именно скелет. Поэтому
// сторожим ИНВАРИАНТ, а не форму.
func TestSheetsAlwaysDropTheirOpenFlag(t *testing.T) {
	root := repoRoot(t)
	dir := filepath.Join(root, "unity/Packages/com.lvn.engine.shell/Runtime")
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatal(err)
	}
	// Тело метода от `_open = true` до конца — грубо, но достаточно: нас
	// интересует, встречается ли снятие внутри finally ниже по тексту.
	raise := regexp.MustCompile(`(?m)^\s*_open\s*=\s*true\s*;`)
	drop := regexp.MustCompile(`(?s)finally\s*\{[^}]*_open\s*=\s*false`)

	seen := 0
	var loose []string
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".cs") {
			continue
		}
		body := stripComments(string(mustRead(t, filepath.Join(dir, e.Name()))))
		locs := raise.FindAllStringIndex(body, -1)
		if len(locs) == 0 {
			continue
		}
		seen++
		for _, loc := range locs {
			tail := body[loc[1]:]
			if next := raise.FindStringIndex(tail); next != nil {
				tail = tail[:next[0]]
			}
			if !drop.MatchString(tail) {
				loose = append(loose, e.Name())
			}
		}
	}
	sawSources(t, seen, 3, "экранов с флагом открытия")

	sort.Strings(loose)
	if len(loose) > 0 {
		t.Errorf("флаг открытия снимается НЕ в finally: %s\n\n"+
			"Отмена, исключение или закрытие приложения посреди ожидания — и экран "+
			"остаётся «открытым» навсегда: следующий вызов упрётся в защиту от "+
			"повторного входа и молча не откроется.",
			strings.Join(loose, ", "))
	}
}

// «СКРЫТ ЛИ» — ВОПРОС К ДОМУ, А НЕ ПРИВЕДЕНИЕ ТИПА.
//
// Компилятор булевых значений НЕ приводит: `show=no` доезжает до рантайма
// СТРОКОЙ. Разбирает это дом `Lvn.LvnBool`; всякий, кто вместо него пишет
// `(bool?)c["show"]`, видит только настоящий `bool` и молча считает скрытую
// героиню видимой.
//
// Правило было написано СЕМЬ раз: движок (верно), повтор кадра на Go, три
// копии в веб-плеере — и ДВЕ в тестах. Последние опаснее всех: заглушка сцены
// и модель корпуса СЕРТИФИЦИРУЮТ поведение, и приведение заставляло их
// сертифицировать не то, что делает движок. Зелёный отчёт при разъехавшихся
// рантаймах — худшее, что может случиться со стражем.
func TestNobodyCastsShowToBool(t *testing.T) {
	root := repoRoot(t)
	bad := regexp.MustCompile(`\(bool\??\)\s*\w*\[?"?(show|hide|off)"?\]?`)
	seen := 0
	var strays []string
	err := filepath.Walk(filepath.Join(root, "unity", "Packages"),
		func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return err
			}
			seen++
			if strings.HasSuffix(path, "LvnBool.cs") {
				return nil // дом вправе приводить: он и есть разбор
			}
			for _, m := range bad.FindAllString(stripComments(string(mustRead(t, path))), -1) {
				strays = append(strays, filepath.Base(path)+": "+m)
			}
			return nil
		})
	if err != nil {
		t.Fatal(err)
	}
	sawSources(t, seen, 300, "файлов .cs")
	sort.Strings(strays)
	if len(strays) > 0 {
		t.Errorf("поле «да-нет» читают приведением, мимо словаря:\n  %s\n\n"+
			"Берите Lvn.LvnBool.Of(поле, умолчание): `show=no` приходит СТРОКОЙ, "+
			"и приведение молча оставляет скрытого на сцене.",
			strings.Join(strays, "\n  "))
	}
}

// КАРТИНКУ ИЗ БАЙТОВ ДЕЛАЕТ ДОМ.
//
// Обряд короткий: завести пустую текстуру и попросить её разобрать байты. Шага
// два, а мест было четыре — и одно из них на неудаче текстуру не уничтожало.
// Битый или неподдерживаемый файл оставлял пустую текстуру в памяти при каждой
// попытке: ошибки нет, лог молчит, память растёт ровно у тех, у кого контент
// побился, — то есть у самых невезучих игроков.
//
// Сторожим не «есть ли Destroy рядом», а сам вызов: `LoadImage` вне дома
// означает, что обряд снова расписали руками, а забыть уборку в нём проще, чем
// вспомнить.
func TestOnlyOneHomeDecodesImages(t *testing.T) {
	root := repoRoot(t)
	home := filepath.Join("Runtime", "Content", "AssetMemory.cs")
	seen := 0
	var strays []string
	err := filepath.Walk(filepath.Join(root, "unity", "Packages"),
		func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return err
			}
			seen++
			if strings.HasSuffix(path, home) || strings.Contains(path, "/Tests/") {
				return nil
			}
			body := stripComments(string(mustRead(t, path)))
			if strings.Contains(body, ".LoadImage(") {
				strays = append(strays, filepath.Base(path))
			}
			return nil
		})
	if err != nil {
		t.Fatal(err)
	}
	sawSources(t, seen, 300, "файлов .cs")
	sort.Strings(strays)
	if len(strays) > 0 {
		t.Errorf("картинку из байтов делают мимо дома: %s\n\n"+
			"Берите AssetMemory.Decode(bytes): он возвращает null и убирает за "+
			"собой, а расписанный руками обряд забывает уборку — битый файл "+
			"течёт пустой текстурой при каждой попытке.",
			strings.Join(strays, ", "))
	}
}

// ТИП ЗВУКОВОГО ДЕКОДЕРА БЕРУТ У ДОМА.
//
// Таблица «расширение → декодер Unity» стояла дважды, её свели в
// `DownloadPolicy.AudioTypeOf` — и ровно об этом написано в докблоке дома. А
// сетевой поставщик ходил мимо него ТРЕТЬИМ и слал `AudioType.UNKNOWN`.
//
// UNKNOWN — не «пусть Unity разберётся», а «разбирайся по адресу»: на ссылке
// без расширения или с хвостом версии разбираться не по чему, и клип не
// строится. Наружу это выглядит как «файл скачан, но не звучит» — без ошибки и
// без строки в логе, ровно тот случай, что назван в докблоке дома.
func TestAudioDecoderTypeComesFromTheHome(t *testing.T) {
	root := repoRoot(t)
	seen := 0
	var strays []string
	err := filepath.Walk(filepath.Join(root, "unity", "Packages"),
		func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return err
			}
			seen++
			if strings.Contains(path, "/Tests/") {
				return nil
			}
			body := stripComments(string(mustRead(t, path)))
			// Вложенная скобка обязательна в шаблоне: сам вызов дома —
			// AudioTypeOf(path) — стоит ВНУТРИ, и «до первой закрывающей»
			// обрывало бы совпадение ровно на правильном коде.
			for _, m := range regexp.MustCompile(`GetAudioClip\((?:[^()]|\([^()]*\))*\)`).FindAllString(body, -1) {
				if !strings.Contains(m, "AudioTypeOf") && !strings.Contains(m, "type") {
					strays = append(strays, filepath.Base(path)+": "+strings.Join(strings.Fields(m), " "))
				}
			}
			return nil
		})
	if err != nil {
		t.Fatal(err)
	}
	sawSources(t, seen, 300, "файлов .cs")
	sort.Strings(strays)
	if len(strays) > 0 {
		t.Errorf("звук просят без типа из дома:\n  %s\n\n"+
			"Берите DownloadPolicy.AudioTypeOf(url). UNKNOWN на адресе без "+
			"расширения даёт «скачано, но не звучит» — молча.",
			strings.Join(strays, "\n  "))
	}
}

// ПРАВИЛО, ПОЛОВИНЫ КОТОРОГО СВЕРЯЮТСЯ, ЧИТАЕТСЯ ИЗ ОДНОГО МЕСТА.
//
// Два правила сцены устроены одинаково опасно: их спрашивают ДВОЕ и делают
// разное, а ответ обязан совпасть до последнего знака.
//
// Темп строки: печать берёт скорость и печатает, а ОЦЕНКА берёт ту же скорость
// и говорит входящему актёру, когда осесть вместе с текстом. Разойдись они на
// строке с авторской скоростью — герой заканчивает движение в чужом ритме.
//
// Переход видимости: один спрашивает «есть ли зримый переход», другой «какой
// играть». Вопросы разные, выбор один, и живёт он у самой расстановки.
//
// Сторожим не поведение, а ЧИСЛО ЧТЕНИЙ: формула, написанная во второй раз, и
// есть начало расхождения.
func TestPairedRulesAreReadOnce(t *testing.T) {
	root := repoRoot(t)
	cases := []struct {
		file, needle, why string
		limit             int
	}{
		{"unity/Packages/com.lvn.engine/Runtime/UI/DialogueBox.Reveal.cs",
			"_theme.CharsPerSecond",
			"темп темы читают мимо PaceFor: печать и её оценка разойдутся на авторской скорости", 1},
		{"unity/Packages/com.lvn.engine/Runtime/UI/VnStage.Actors.Placement.cs",
			"p.Show ? p.EnterTransition",
			"выбор перехода пишут тернаркой мимо Placement.VisibilityTransition", 0},
	}
	seen := 0
	for _, c := range cases {
		body := stripComments(string(mustRead(t, filepath.Join(root, c.file))))
		seen++
		if n := strings.Count(body, c.needle); n > c.limit {
			t.Errorf("%s: «%s» встречается %d раз при пределе %d — %s",
				filepath.Base(c.file), c.needle, n, c.limit, c.why)
		}
	}
	sawSources(t, seen, 2, "парных правил")
}

// ЗЕРКАЛО ОСТАЁТСЯ ЗЕРКАЛОМ: что навязали при сборке — снимают при отпускании.
//
// Убранство сцены собирают дважды (рождение и смена темы), и обряд сведён в
// пару MakeChrome/DropChrome. Обещание пары записано в её докблоке словами:
// «подписка, которую забыли снять, переживает свой экземпляр». Слова — не
// проверка, а забыть тут легко ровно потому, что видно НИЧЕГО: старый
// экземпляр уже никому не нужен, и лишний обработчик просто тикает в пустоту,
// пока однажды не тикнет по живому.
//
// Сторожим состав: каждое `+=` в сборке обязано иметь парное `-=` в
// отпускании, и наоборот. Это дешевле теста на живой сцене (ей нужны панель и
// документ) и точнее по времени — ловит саму асимметрию, а не её последствие.
func TestChromeUnwiresWhatItWires(t *testing.T) {
	root := repoRoot(t)
	body := stripComments(string(mustRead(t, filepath.Join(root,
		"unity/Packages/com.lvn.engine/Runtime/UI/VnStage.Chrome.cs"))))

	cut := func(name string) string {
		at := strings.Index(body, "private void "+name+"(")
		if at < 0 {
			t.Fatalf("%s пропал — на паре держится сборка убранства", name)
		}
		end := strings.Index(body[at:], "\n        }")
		if end < 0 {
			t.Fatalf("не нашёл конца %s", name)
		}
		return body[at : at+end]
	}
	pick := func(src, sign string) map[string]bool {
		out := map[string]bool{}
		re := regexp.MustCompile(`(_\w+\.\w+)\s*` + regexp.QuoteMeta(sign) + `=\s*(\w+)`)
		for _, m := range re.FindAllStringSubmatch(src, -1) {
			out[m[1]+" → "+m[2]] = true
		}
		return out
	}
	made := pick(cut("MakeChrome"), "+")
	dropped := pick(cut("DropChrome"), "-")
	sawSources(t, len(made), 3, "подписок при сборке убранства")

	var lonely []string
	for k := range made {
		if !dropped[k] {
			lonely = append(lonely, "навязали, не снимают: "+k)
		}
	}
	for k := range dropped {
		if !made[k] {
			lonely = append(lonely, "снимают, не навязывали: "+k)
		}
	}
	sort.Strings(lonely)
	if len(lonely) > 0 {
		t.Errorf("пара сборки и отпускания разошлась (%d):\n  %s\n\n"+
			"Подписка, которую забыли снять, переживает свой экземпляр: старый "+
			"обработчик тикает в пустоту, пока однажды не тикнет по живому.",
			len(lonely), strings.Join(lonely, "\n  "))
	}
}

// ДИАГНОСТИКА ПОМЕЧАЕТСЯ ОДНИМ СПОСОБОМ.
//
// Тег стоит в самой строке (`[lvn-menu] …`), и по нему фильтруют консоль и
// ОТГРУЖАЕМЫЙ ЛОГ — тот, что приезжает с устройства игрока в админку. Правило
// записано в докблоке дома журнала.
//
// Соглашений при этом было ДВА: 166 строк с `[lvn-*]` и 98 с голым именем —
// `[novelapp]`, `[content]`, `[stage]`, да ещё вперемешку по регистру
// (`[LVN]`, `[LvnFx]`). Фильтр по `lvn-` не видел ТРЕТИ диагностики движка, и
// заметить это можно было только не найдя в поле того, что точно логируется.
func TestEveryDiagnosticTagIsNamespaced(t *testing.T) {
	root := repoRoot(t)
	tag := regexp.MustCompile(`(?:Debug\.Log\w*|LvnLog\.\w+)\(\s*\$?"(\[[a-zA-Z][\w-]*\])`)
	seen := 0
	var strays []string
	err := filepath.Walk(filepath.Join(root, "unity", "Packages"),
		func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return err
			}
			if strings.Contains(path, "/Tests/") || strings.Contains(path, "Samples~") {
				return nil
			}
			seen++
			for _, m := range tag.FindAllStringSubmatch(string(mustRead(t, path)), -1) {
				if !strings.HasPrefix(m[1], "[lvn") {
					strays = append(strays, filepath.Base(path)+": "+m[1])
				}
			}
			return nil
		})
	if err != nil {
		t.Fatal(err)
	}
	sawSources(t, seen, 200, "файлов .cs")
	sort.Strings(strays)
	if len(strays) > 0 {
		t.Errorf("теги диагностики мимо соглашения (%d):\n  %s\n\n"+
			"Тег обязан начинаться с «lvn»: по нему фильтруют отгружаемый лог, "+
			"и сообщение с чужим тегом в поле просто не находится.",
			len(strays), strings.Join(strays, "\n  "))
	}
}

// КАРТА КЭША И СЧЁТ БАЙТОВ МЕНЯЮТСЯ ВМЕСТЕ.
//
// Карта отвечает «что у нас есть», счётчик — «сколько это весит», а решение о
// вытеснении принимается по СЧЁТЧИКУ. Забудь вычесть — бюджет считает память
// занятой, и кэш выбрасывает живое; вычти дважды — считает свободной, и растёт
// до отказа. Ни то, ни другое не даёт ошибки: игра просто перезагружает
// картинки или падает по памяти.
//
// Обряд стоял тремя копиями и в разных порядках. Сторожим не порядок (он под
// одним замком безразличен), а само наличие рукописного снятия: `Remove` у
// карты законен только внутри дома.
func TestSpriteCacheDropsThroughOneDoor(t *testing.T) {
	root := repoRoot(t)
	body := stripComments(string(mustRead(t, filepath.Join(root,
		"unity/Packages/com.lvn.engine/Runtime/Content/ContentLoader.SpriteCache.cs"))))
	sawSources(t, len(body), 2000, "знаков в доме кэша спрайтов")

	// Внутри самого DropLocked — законно; всё остальное снятие идёт через него.
	at := strings.Index(body, "private void DropLocked(")
	if at < 0 {
		t.Fatal("DropLocked пропал — на нём держится согласие карты и счёта")
	}
	end := strings.Index(body[at:], "\n        }")
	home := body[at : at+end]
	rest := body[:at] + body[at+end:]

	if !strings.Contains(home, "_spriteCache.Remove(") || !strings.Contains(home, "_spriteBytes -=") {
		t.Error("дом больше не делает обе половины разом")
	}
	if n := strings.Count(rest, "_spriteCache.Remove("); n > 0 {
		t.Errorf("снятие из карты мимо дома: %d раз(а)\n\n"+
			"Берите DropLocked: карта и счёт байтов обязаны меняться вместе, "+
			"иначе бюджет вытеснения выбрасывает живое или растёт до отказа.", n)
	}
}

// ПИКСЕЛЬНЫЕ ТЕСТЫ ОБЯЗАНЫ ИМЕТЬ ЧЕМ РИСОВАТЬ.
//
// Пиксельные проверки сами себя пропускают, когда графики нет: на машине без
// неё «нет графики» — законная причина, и сообщение об этом честное. Но если
// графику отнимает САМ ПРОГОН (`-nographics`), пропуск становится вечным:
// девять проверок стекла, створа и переходов не выполнялись НИ РАЗУ, а отчёт
// был зелёный. Зелёное на непроверенном — худший вид зелёного.
//
// Сторожим флаг у PlayMode-запуска. EditMode графику не просит, и там флаг
// уместен — потому сторож смотрит не «есть ли -nographics в файле», а есть ли
// он в наборе доводов PlayMode.
func TestPlayModeRunHasGraphics(t *testing.T) {
	root := repoRoot(t)
	sh := string(mustRead(t, filepath.Join(root, "qa", "run-all.sh")))
	sawSources(t, len(sh), 3000, "знаков в прогоне")

	at := strings.Index(sh, "-testPlatform PlayMode")
	if at < 0 {
		t.Fatal("PlayMode-запуск пропал из прогона — целый класс регрессий виден только там")
	}
	// Набор доводов начинается выше по тексту: ищем ближайший `args=(`.
	start := strings.LastIndex(sh[:at], "args=(")
	if start < 0 {
		t.Fatal("не нашёл набор доводов PlayMode")
	}
	if strings.Contains(sh[start:at], "-nographics") {
		t.Error("PlayMode запускается с -nographics: пиксельные тесты будут " +
			"пропускаться ВСЕГДА, а отчёт останется зелёным")
	}
}

// УЙТИ С ЭКРАНА — ЧЕРЕЗ ОДНУ ДВЕРЬ.
//
// Уход поверхности — это отмена всего, чем показ был обставлен: `display`
// убирает из раскладки, `opacity` и `translate` возвращают на место то, что
// показ двигал и гасил. Правило открывали ТРИЖДЫ и каждый раз наполовину:
// накладной экран помнил смещение, панель истории — прозрачность рамки, бут и
// загрузка — свою прозрачность. Ни один не знал всего набора.
//
// Опасность несимметрична, и в этом всё дело. Забыть `display` видно сразу:
// экран остался на глазах. Забыть прозрачность или смещение не видно НИКОГДА:
// следующий показ ставит `display`, поверхность честно в дереве, ловит тапы,
// ждёт игрока — и невидима.
//
// Поэтому: `style.display = DisplayStyle.None` на СЕБЕ (без получателя) вне
// конструктора — ошибка. Рождение спрятанным разрешено: там уход не отменяют,
// там его ещё не было. Чужой элемент (`_frame.style.display`) не наш случай —
// его прячет тот, кто им владеет.
func TestLeavingTheScreenGoesThroughOneDoor(t *testing.T) {
	root := repoRoot(t)
	dirs := []string{
		"unity/Packages/com.lvn.engine.shell/Runtime",
		"unity/Packages/com.lvn.engine/Runtime/UI",
	}
	selfHide := regexp.MustCompile(`^\s+style\.display = DisplayStyle\.None;`)
	member := regexp.MustCompile(`^\s+(?:public|private|protected|internal)\s`)
	ctor := regexp.MustCompile(`^\s+(?:public|private|protected|internal)\s+\w+\s*\(`)

	seen := 0
	var bypass []string
	for _, d := range dirs {
		entries, err := os.ReadDir(filepath.Join(root, d))
		if err != nil {
			t.Fatal(err)
		}
		for _, e := range entries {
			if e.IsDir() || !strings.HasSuffix(e.Name(), ".cs") {
				continue
			}
			lines := strings.Split(string(mustRead(t, filepath.Join(root, d, e.Name()))), "\n")
			for i, l := range lines {
				if !selfHide.MatchString(l) {
					continue
				}
				seen++
				// Ближайшее объявление члена выше: конструктор — рождение,
				// всё остальное — уход.
				j := i
				for j > 0 {
					j--
					if member.MatchString(lines[j]) && strings.Contains(lines[j], "(") {
						break
					}
				}
				if ctor.MatchString(lines[j]) {
					continue
				}
				bypass = append(bypass, e.Name()+":"+itoa(i+1)+" ("+strings.TrimSpace(lines[j])+")")
			}
		}
	}
	sawSources(t, seen, 5, "мест, где поверхность прячет себя")
	sort.Strings(bypass)
	if len(bypass) > 0 {
		t.Errorf("уход с экрана мимо ScreenFx.PutAway (%d):\n  %s\n\n"+
			"PutAway отменяет ВЕСЬ показ: display, opacity, translate. Забытая "+
			"прозрачность или смещение не видны никогда — следующий показ даёт "+
			"поверхность, которая в дереве, ловит тапы, ждёт игрока и невидима.",
			len(bypass), strings.Join(bypass, "\n  "))
	}
}

// СОЗДАННОЕ ТЕСТОМ УБИРАЮТ В TEARDOWN, А НЕ В КОНЦЕ УДАЧНОГО ПУТИ.
//
// Уборка последней строкой теста срабатывает ТОЛЬКО когда все утверждения
// прошли. Упади любое — объект переживает тест: в редакторе его никто не
// сносит, а сцена у тестов общая. Следующий тест находит чужого участника и
// падает не от своей причины; разбор при этом уходит не в тот файл, и это
// самая дорогая форма красноты.
//
// Сторожим форму: `DestroyImmediate` в теле [Test] — признак уборки на удачном
// пути. В `[TearDown]` он законен, там же живёт и дом `Мусор`.
func TestTestsCleanUpInTearDown(t *testing.T) {
	root := repoRoot(t)
	var loud []string
	seen := 0
	for _, rel := range []string{
		"unity/Packages/com.lvn.engine/Tests/Editor",
		"unity/Packages/com.lvn.engine/Tests/Runtime",
	} {
		entries, err := os.ReadDir(filepath.Join(root, rel))
		if err != nil {
			t.Fatal(err)
		}
		for _, e := range entries {
			if e.IsDir() || !strings.HasSuffix(e.Name(), ".cs") {
				continue
			}
			seen++
			body := stripComments(string(mustRead(t, filepath.Join(root, rel, e.Name()))))
			for _, block := range strings.Split(body, "[Test]")[1:] {
				// тело до следующего атрибута — грубо, но нам хватает
				if cut := strings.Index(block, "\n        ["); cut > 0 {
					block = block[:cut]
				}
				// Уборка в `finally` — ЗАКОННАЯ: она срабатывает и на
				// упавшем утверждении, то есть делает ровно то, ради чего
				// заведён [TearDown]. Страж, кусающий верное, хуже
				// отсутствующего: его выключают вместе с настоящими находками.
				at := strings.Index(block, "DestroyImmediate(")
				if at < 0 {
					continue
				}
				if fin := strings.Index(block, "finally"); fin >= 0 && fin < at {
					continue
				}
				// СНОС, ПОСЛЕ КОТОРОГО ЕЩЁ ПРОВЕРЯЮТ, — это СЦЕНАРИЙ, а не
				// уборка: «старый слой умер, родились два новых» — ровно то,
				// что тест и воспроизводит. Уборка стоит последней, после неё
				// утверждать уже нечего. Без этой оговорки сторож кусал верное,
				// а такой выключают вместе с настоящими находками.
				if strings.Contains(block[at:], "Assert") {
					continue
				}
				loud = append(loud, e.Name())
				break
			}
		}
	}
	sawSources(t, seen, 40, "файлов тестов")
	sort.Strings(loud)
	if len(loud) > 0 {
		t.Errorf("тестов, убирающих за собой на удачном пути: %d (их не должно быть):\n  %s\n\n"+
			"Берите Мусор + [TearDown]: упавшее утверждение оставляет объект жить, "+
			"и следующий тест падает не от своей причины.",
			len(loud), strings.Join(loud, "\n  "))
	}
}

// ЭКРАНЫ ГАСНУТ В ОДИН ТЕМП.
//
// Прайс-лист длительностей (`LvnMotion`) заведён ровно от этой болезни: «правка
// одного имени меняет ритм всей оболочки разом», а числа на местах вызова дают
// соседние элементы, движущиеся вразнобой. Самое крупное движение оболочки —
// гашение ЦЕЛОГО ЭКРАНА — в список не попало, и пять экранов держали свои
// числа: 0,18 у попапа, 0,25 у галереи и гардероба, 0,3 у входа. Решение «в
// темп актёров» (Илья 25.08) знал только накладной экран, где оно и записано.
//
// Поэтому: длительность в вызове `ScreenFx.Fade*` — не число. Имя (`FadeSeconds`,
// `HandOffSeconds`, `VeilFadeSeconds`), поле автора (`screen_fade`) или
// переменная — что угодно, у чего есть место, где решение записано ОДИН раз.
func TestScreensFadeAtOneTempo(t *testing.T) {
	root := repoRoot(t)
	dirs := []string{
		"unity/Packages/com.lvn.engine.shell/Runtime",
		"unity/Packages/com.lvn.engine/Runtime/UI",
	}
	// Довод длительности: у FadeAsync четвёртый, у FadeAwayAsync второй.
	call := regexp.MustCompile(`ScreenFx\.(FadeAsync|FadeAwayAsync)\(`)
	number := regexp.MustCompile(`^\d+(\.\d+)?f?$`)
	at := map[string]int{"FadeAsync": 3, "FadeAwayAsync": 1}

	seen := 0
	var literals []string
	for _, d := range dirs {
		entries, err := os.ReadDir(filepath.Join(root, d))
		if err != nil {
			t.Fatal(err)
		}
		for _, e := range entries {
			if e.IsDir() || !strings.HasSuffix(e.Name(), ".cs") {
				continue
			}
			src := stripComments(string(mustRead(t, filepath.Join(root, d, e.Name()))))
			for _, m := range call.FindAllStringSubmatchIndex(src, -1) {
				which := src[m[2]:m[3]]
				args, ok := topLevelArgs(src, m[1])
				if !ok {
					continue
				}
				seen++
				i := at[which]
				if i >= len(args) {
					continue
				}
				a := strings.TrimSpace(args[i])
				if number.MatchString(a) {
					line := strings.Count(src[:m[0]], "\n") + 1
					literals = append(literals, e.Name()+":"+itoa(line)+" → "+a)
				}
			}
		}
	}
	sawSources(t, seen, 8, "гашений экрана")
	sort.Strings(literals)
	if len(literals) > 0 {
		t.Errorf("длительность гашения задана числом на месте вызова (%d):\n  %s\n\n"+
			"Прайс-лист LvnMotion затем и заведён, чтобы ритм оболочки правился "+
			"одним именем. Общий темп экрана — ScreenFx.FadeSeconds; если этот "+
			"экран правда другой поступок, дайте числу имя с объяснением.",
			len(literals), strings.Join(literals, "\n  "))
	}
}

// Доводы вызова верхнего уровня: запятые внутри вложенных скобок и строк не
// делят. `from` — позиция сразу за открывающей скобкой.
func topLevelArgs(src string, from int) ([]string, bool) {
	depth, start := 1, from
	var args []string
	for i := from; i < len(src); i++ {
		switch src[i] {
		case '(', '[':
			depth++
		case ')', ']':
			depth--
			if depth == 0 {
				return append(args, src[start:i]), true
			}
		case ',':
			if depth == 1 {
				args = append(args, src[start:i])
				start = i + 1
			}
		case '"':
			for i++; i < len(src) && src[i] != '"'; i++ {
				if src[i] == '\\' {
					i++
				}
			}
		}
	}
	return nil, false
}

// НАСТРОЙКА ПРИМЕНЯЕТ СЕБЯ САМА.
//
// `LvnPrefs.Changed` летит на каждую запись, и на него подписаны те, кого она
// касается: панель (масштаб интерфейса), шрифты, темп движения, громкости,
// сцена. Экран настроек знает только «записать» — применение не его дело.
//
// Из 34 присваиваний настроек ручной толчок добавляли ДВА, оба безвредных: они
// звали `LvnPanel.ApplyUiScale()` после записи, которая и так его поднимала.
// Вред был не в этих двух вызовах, а в правиле, которому они учат: следующая
// настройка, применённая руками, обновит ТОТ экран, с которого её меняли, и не
// обновит второй — а экранов настроек два (меню сцены и оболочка).
func TestSettingsApplyThemselves(t *testing.T) {
	root := repoRoot(t)
	dirs := []string{
		"unity/Packages/com.lvn.engine.shell/Runtime",
		"unity/Packages/com.lvn.engine/Runtime/UI",
	}
	call := regexp.MustCompile(`\bApplyUiScale\s*\(`)
	seen := 0
	var outside []string
	for _, d := range dirs {
		entries, err := os.ReadDir(filepath.Join(root, d))
		if err != nil {
			t.Fatal(err)
		}
		for _, e := range entries {
			if e.IsDir() || !strings.HasSuffix(e.Name(), ".cs") {
				continue
			}
			// Дом применения — сам LvnPanel: там и вызов при заводе панели, и
			// подписка на событие.
			if e.Name() == "LvnPanel.cs" {
				seen += len(call.FindAllString(string(mustRead(t, filepath.Join(root, d, e.Name()))), -1))
				continue
			}
			src := stripComments(string(mustRead(t, filepath.Join(root, d, e.Name()))))
			for range call.FindAllString(src, -1) {
				seen++
				outside = append(outside, e.Name())
			}
		}
	}
	sawSources(t, seen, 2, "вызовов применения масштаба")
	sort.Strings(outside)
	if len(outside) > 0 {
		t.Errorf("масштаб интерфейса применяют руками мимо подписки: %s\n\n"+
			"LvnPrefs.Changed летит на каждую запись, и панель на него подписана. "+
			"Ручное применение обновит тот экран, с которого меняли, и не обновит "+
			"второй — экранов настроек два.", strings.Join(outside, ", "))
	}
}

// ПРОПУСКОВ НЕ СТАНОВИТСЯ БОЛЬШЕ.
//
// Тест, умеющий пропустить себя, — честный ответ на «среда не даёт проверить»:
// пропуск виден в отчёте, а зелёная проверка, ничего не проверяющая, не видна
// никак. Но у честности есть цена: каждый такой пропуск — дыра в покрытии,
// которую CI не показывает красным.
//
// Замерено 02.09: восемнадцать мест `Assert.Ignore` и один статический
// `[Ignore]`. Причины разные и все названы словом — нет графики, нет шейдера,
// панель UITK в безголовом прогоне не считает раскладку, сервер-смоук не
// собран. Храповик держит число: пропуск добавляется осознанно, а не потому,
// что «тест почему-то красный».
//
// Число может только УМЕНЬШАТЬСЯ. Выросло — значит либо среда стала хуже, либо
// пропуском лечат падение.
func TestSkipsDoNotMultiply(t *testing.T) {
	const budget = 35 // 34 динамических + 1 статический (06.09: двенадцать
	// проверок против НАСТОЯЩЕГО сервера — самолечение кэша, доставка событий,
	// защита каталога, смена аккаунта, удаление аккаунта, три подмены ответа
	// (чужой JSON, страница входа в сеть, обрыв на половине), забывчивый сервер,
	// вход после перезапуска и покупка без сети — пропускаются без собранного
	// бинаря, ровно как смоук рядом с ними; двенадцатая играет главу эталонным
	// движком и ждёт путь в LVN_PLAYTHROUGH_LVN — контента для неё в репозитории
	// нет и быть не должно.
	//
	// Три пропуска добавлены 06.09 вместе с проверками входа и офлайновой
	// покупки: у всех трёх причина одна и та же и названа словом — без
	// qa/bin/lvnserver-test проверять нечем, а поднимать сервер из теста
	// самому значило бы дублировать сборку, которую делает qa/run-all.sh.
	//
	// Ещё три — проверки медленной сети: им нужен python3 и qa/slow-server.py
	// (медленный сервер в трёх режимах). Причина названа словом в каждом
	// пропуске: без питона на машине воспроизвести тормозящую сеть нечем.
	//
	// И один — первый запуск без сети: ему нужен настоящий сервер, чтобы
	// поднять его ПОСРЕДИ ожидания и увидеть, как игра встаёт сама.

	root := repoRoot(t)
	skip := regexp.MustCompile(`Assert\.Ignore\(|\[\s*(?:UnityTest|Test)\s*,\s*Ignore\(|^\s*\[\s*Ignore\(`)
	seen, files := 0, 0
	where := map[string]int{}
	_ = filepath.Walk(filepath.Join(root, "unity/Packages"), func(p string, i os.FileInfo, err error) error {
		if err != nil || i.IsDir() || !strings.HasSuffix(p, ".cs") || !strings.Contains(p, "/Tests/") {
			return err
		}
		files++
		n := len(skip.FindAllString(string(mustRead(t, p)), -1))
		if n > 0 {
			seen += n
			where[filepath.Base(p)] = n
		}
		return nil
	})
	sawSources(t, files, 150, "файлов тестов")

	if seen > budget {
		var list []string
		for f, n := range where {
			list = append(list, f+"×"+itoa(n))
		}
		sort.Strings(list)
		t.Errorf("пропусков стало больше: %d при бюджете %d\n  %s\n\n"+
			"Каждый пропуск — дыра, которую CI не показывает красным. Если среда "+
			"правда не даёт проверить — назовите причину словом и поднимите бюджет "+
			"осознанно; если пропуском лечат падение — почините падение.",
			seen, budget, strings.Join(list, "\n  "))
	}
	if seen < budget {
		t.Logf("пропусков стало меньше (%d при бюджете %d) — опустите бюджет", seen, budget)
	}
}

// СЦЕНА, ГОВОРЯЩАЯ САМА С СОБОЙ, НАЗЫВАЕТСЯ.
//
// У команды сцены есть отправитель, и он решает не оформление, а ПАМЯТЬ:
// липкой (наследуемой следующей авторской командой) может быть только команда
// истории. Когда в память попадала команда витрины или гардероба, героиня
// выходила в главу стоящей по-менюшному — «не встраивается в игру, хотя её
// реплика». Ради этого липкость и заведена.
//
// Однорукая перегрузка `ApplyStage(cmd)` подставляет `LvnSender.Story`. Снаружи
// это правильное умолчание — зовущий и есть история. ИЗНУТРИ сцены это ложь:
// сцена не история, она пересылает чужую команду, и назваться историей значит
// подменить память.
//
// Живой случай 02.09: повтор недоехавшей фигуры (`RetryActorSoonAsync`) звал
// одноруко. Поза витрины, доехавшая со второй попытки, оседала в памяти как
// авторская — главный путь эту дыру закрыл, повтор ходил мимо.
func TestStageNamesItselfWhenItTalksToItself(t *testing.T) {
	root := repoRoot(t)
	dir := filepath.Join(root, "unity/Packages/com.lvn.engine/Runtime/UI")
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatal(err)
	}
	// Два входа в сцену с одинаковым правилом: и общая дверь, и путь актёра.
	call := regexp.MustCompile(`(?:ApplyStage|ApplyActorAsync)\(`)
	// Объявления перегрузок — не вызовы.
	decl := regexp.MustCompile(`(?:void|Task)\s+(?:ApplyStage|ApplyActorAsync)\(`)

	seen := 0
	var nameless []string
	for _, e := range entries {
		if e.IsDir() || !strings.HasPrefix(e.Name(), "VnStage") || !strings.HasSuffix(e.Name(), ".cs") {
			continue
		}
		src := stripComments(string(mustRead(t, filepath.Join(dir, e.Name()))))
		for _, m := range call.FindAllStringIndex(src, -1) {
			head := src[max0(m[0]-24):m[1]]
			if decl.MatchString(head) {
				continue
			}
			seen++
			args, ok := topLevelArgs(src, m[1])
			if !ok {
				continue
			}
			// Отправитель может ехать именованным доводом (`sender: sender`),
			// значением (`LvnSender.Wardrobe`) или через дом памяти
			// (`RememberedSender(id)`) — считать позиции нельзя: у пути актёра
			// между ними два признака гардероба. Ищем слово, а не место.
			named := false
			for _, a := range args {
				if strings.Contains(strings.ToLower(a), "sender") {
					named = true
					break
				}
			}
			if named {
				continue
			}
			nameless = append(nameless, e.Name()+":"+itoa(strings.Count(src[:m[0]], "\n")+1))
		}
	}
	sawSources(t, seen, 10, "вызовов сцены изнутри неё самой")
	sort.Strings(nameless)
	if len(nameless) > 0 {
		t.Errorf("сцена зовёт себя без отправителя (%d):\n  %s\n\n"+
			"Однорукая перегрузка подставляет LvnSender.Story, то есть ЛИПКИЙ. "+
			"Изнутри сцены это ложь: чужая команда осядет в памяти как авторская, "+
			"и героиня выйдет в главу стоящей по-менюшному.",
			len(nameless), strings.Join(nameless, "\n  "))
	}
}

// ПУБЛИЧНАЯ ДВЕРЬ, В КОТОРУЮ НИКТО НЕ ХОДИТ, ОБЪЯСНЯЕТ СЕБЯ.
//
// У движка-библиотеки два вида таких дверей, и различить их снаружи нельзя:
// ШОВ (её открывает встраивающая игра — привязка аккаунта, приём сообщения от
// хоста, режим бара) и НЕ ПОДКЛЮЧЁННОЕ (написано, но никем не позвано —
// пролёт вкладки, затвор прозрачности). Первое трогать нельзя, второе можно
// выкинуть — и решить это можно только по докблоку.
//
// Конвенция в движке уже была: `LvnMontage.Coalesce` («НЕ ПОДКЛЮЧЁН: ждёт
// второго заказчика»), `LvnIcons.Retarget`, `LvnSpriteFxDriver.ReleaseFade`,
// `LvnGlobalStats.SaveAsync`. Замерено 02.09: из 1203 публичных способов имя
// тринадцати не встречается в репозитории больше НИГДЕ, и семеро из них
// объяснялись, шестеро молчали.
//
// Ищем по имени целиком, а не по вызову со скобками: способ передают и
// группой (`Safe("последний кадр", VnStage.ForgetLastSceneBg)`) — на этом
// сито однажды чуть не объявило мёртвым живой стиратель следа игрока.
func TestUncalledPublicDoorsExplainThemselves(t *testing.T) {
	root := repoRoot(t)
	var files []string
	for _, d := range []string{
		"unity/Packages/com.lvn.engine/Runtime",
		"unity/Packages/com.lvn.engine.shell/Runtime",
		"unity/Packages/com.lvn.engine.services/Runtime",
	} {
		_ = filepath.Walk(filepath.Join(root, d), func(p string, i os.FileInfo, err error) error {
			if err == nil && !i.IsDir() && strings.HasSuffix(p, ".cs") {
				files = append(files, p)
			}
			return err
		})
	}
	// Слова считаем по всему движку и по тестам: позвали из теста — тоже позвали.
	var all []string
	seen := append([]string{}, files...)
	_ = filepath.Walk(filepath.Join(root, "unity/Packages"), func(p string, i os.FileInfo, err error) error {
		if err == nil && !i.IsDir() && strings.HasSuffix(p, ".cs") && strings.Contains(p, "/Tests/") {
			seen = append(seen, p)
		}
		return err
	})
	for _, p := range seen {
		all = append(all, string(mustRead(t, p)))
	}
	blob := strings.Join(all, "\n")
	word := regexp.MustCompile(`\w+`)
	uses := map[string]int{}
	for _, w := range word.FindAllString(blob, -1) {
		uses[w]++
	}

	member := regexp.MustCompile(`(?m)((?:[ \t]*///.*\n)*)[ \t]*public\s+(?:static\s+|async\s+|virtual\s+|sealed\s+|new\s+)*[\w<>\[\],\.\?]+\s+(\w+)\s*\(`)
	mark := regexp.MustCompile(`(?i)НЕ ПОДКЛЮЧ|ШОВ|встраива|хост|host|снаружи|UnitySendMessage`)
	skip := map[string]bool{"Equals": true, "GetHashCode": true, "ToString": true, "Dispose": true}

	doors, mute := 0, []string{}
	for _, p := range files {
		src := string(mustRead(t, p))
		for _, m := range member.FindAllStringSubmatch(src, -1) {
			name := m[2]
			if skip[name] || uses[name] > 1 {
				continue
			}
			doors++
			if !mark.MatchString(m[1]) {
				mute = append(mute, filepath.Base(p)+": "+name)
			}
		}
	}
	sawSources(t, len(files), 150, "файлов движка")
	sort.Strings(mute)
	if len(mute) > 0 {
		t.Errorf("публичные двери без объяснения (%d из %d непозванных):\n  %s\n\n"+
			"Снаружи шов и брошенный код выглядят одинаково. Напишите в докблоке "+
			"«ШОВ: кто открывает» или «НЕ ПОДКЛЮЧЁН: почему и что надо, чтобы ожил».",
			len(mute), doors, strings.Join(mute, "\n  "))
	}
}

// КОД, КОТОРЫЙ НИКТО НЕ КОМПИЛИРУЕТ, НЕ ОТСТАЁТ ОТ ДВИЖКА.
//
// Таких мест в репозитории два вида, и оба опасны одинаково.
//
// ШВЫ под чужие пакеты — `com.lvn.engine.spine` и
// `com.lvn.engine.addressables`. Их asmdef закрыт
// `defineConstraints`, а нужных зависимостей в тестовом хосте нет: **610 строк,
// которые не компилирует НИКТО и никогда**. Опечатка там всплывёт у того, кто
// поставит необязательный пакет, — то есть у чужого человека и не сегодня.
//
// ОБРАЗЦЫ пакетов (`Samples~`) — их Unity не видит вовсе: тильда в имени папки
// выводит её из проекта. Компилятор к ним не притрагивается НИКОГДА, а новый
// встраивающий копирует их ПЕРВЫМИ: сгнивший образец — это первое, что он
// увидит от движка.
//
// Компилировать их нам нечем (нет самих чужих сборок). Но самое вероятное
// гниение — не опечатка, а ПЕРЕИМЕНОВАНИЕ в движке: шов зовёт `LvnXxx.Член`,
// член переезжает, и шов остаётся звать пустоту. Это проверяется текстом.
//
// Сверка грубая (имена, а не типы), поэтому судит только про ОТСУТСТВИЕ имени
// целиком: если слова нет во всём движке — звать нечего.
func TestOptionalPackagesStillFitTheEngine(t *testing.T) {
	root := repoRoot(t)
	word := regexp.MustCompile(`\w+`)
	known := map[string]bool{}
	files := 0
	add := func(d string, declOnly bool) {
		_ = filepath.Walk(filepath.Join(root, d), func(p string, i os.FileInfo, err error) error {
			if err != nil || i.IsDir() || !strings.HasSuffix(p, ".cs") {
				return err
			}
			// ОБРАЗЦЫ ЛЕЖАТ ВНУТРИ ПАКЕТА, и обход движка забирал их слова в
			// словарь известных — образец подтверждал сам себя ровно так же,
			// как до этого шов. Их вклад добавляется отдельно и только
			// объявлениями.
			if !declOnly && strings.Contains(p, "Samples~") {
				return nil
			}
			files++
			src := string(mustRead(t, p))
			// У СВОИХ ФАЙЛОВ ШВА берём только ОБЪЯВЛЕНИЯ. Иначе шов
			// подтверждает сам себя: `LvnSpineBootstrap.TryFitZ` кладёт
			// «TryFitZ» в словарь известных, и проверка на него же и
			// соглашается. Употребления — это всё, что стоит после точки.
			if declOnly {
				src = regexp.MustCompile(`\.\s*\w+`).ReplaceAllString(src, ".")
			}
			for _, w := range word.FindAllString(src, -1) {
				known[w] = true
			}
			return nil
		})
	}
	add("unity/Packages/com.lvn.engine", false)
	add("unity/Packages/com.lvn.engine.services", false)
	add("unity/Packages/com.lvn.engine.spine", true)
	add("unity/Packages/com.lvn.engine.addressables", true)
	// Образцы пакетов Unity игнорирует по тильде в имени папки — их не
	// компилирует вообще ничто, а копирует их новый встраивающий ПЕРВЫМИ.
	add("unity/Packages/com.lvn.engine/Samples~", true)
	add("unity/Packages/com.lvn.engine.services/Samples~", true)
	sawSources(t, files, 200, "файлов движка и швов")

	ref := regexp.MustCompile(`\b(Lvn\w+|VnStage|WorldStage|NovelApp|NovelShell|ILvn\w+)\.(\w+)`)
	seen := 0
	var lost []string
	for _, d := range []string{
		"unity/Packages/com.lvn.engine.spine",
		"unity/Packages/com.lvn.engine.addressables",
		"unity/Packages/com.lvn.engine/Samples~",
		"unity/Packages/com.lvn.engine.services/Samples~",
	} {
		_ = filepath.Walk(filepath.Join(root, d), func(p string, i os.FileInfo, err error) error {
			if err != nil || i.IsDir() || !strings.HasSuffix(p, ".cs") {
				return err
			}
			src := stripComments(string(mustRead(t, p)))
			for _, m := range ref.FindAllStringSubmatch(src, -1) {
				seen++
				if !known[m[1]] {
					lost = append(lost, filepath.Base(p)+": типа "+m[1]+" в движке нет")
				} else if !known[m[2]] {
					lost = append(lost, filepath.Base(p)+": "+m[1]+"."+m[2]+" — члена в движке нет")
				}
			}
			return nil
		})
	}
	sawSources(t, seen, 12, "обращений швов и образцов к движку")
	sort.Strings(lost)
	if len(lost) > 0 {
		t.Errorf("некомпилируемый код зовёт то, чего в движке больше нет (%d):\n  %s\n\n"+
			"Его не компилирует ничто: ошибка всплывёт у того, кто поставит "+
			"spine-unity или Addressables либо скопирует образец, — у чужого "+
			"человека и не сегодня.",
			len(lost), strings.Join(lost, "\n  "))
	}
}

// ПЕРЕСОБРАННЫЙ ХРОМ НАСЛЕДУЕТ СПРЯТАННОСТЬ.
//
// Решение «хром спрятан» и НАНЕСЕНИЕ этого решения — разные работы, и разошлись
// они на пересборке. Хром пересобирают три повода, и два из них асинхронные:
// доехал шрифт главы, доехали фоны темы. Свежие поверхности рождаются
// видимыми, а решение принято раньше — обработчик события сравнивает новое
// значение с прошлым и выходит сразу, ничего не нанося. Диалог всплывал поверх
// катсцены.
//
// Сторож держит именно связку: если пересборка перестанет наносить видимость,
// поймать это можно будет только глазами и только на живой катсцене с
// медленной сетью.
func TestRebuiltChromeInheritsHiding(t *testing.T) {
	root := repoRoot(t)
	dir := filepath.Join(root, "unity/Packages/com.lvn.engine/Runtime/UI")

	pointer := stripComments(string(mustRead(t, filepath.Join(dir, "VnStage.Pointer.cs"))))
	theme := stripComments(string(mustRead(t, filepath.Join(dir, "VnStage.Theme.cs"))))
	sawSources(t, len(pointer)+len(theme), 2000, "знаков в домах хрома")

	if !strings.Contains(pointer, "void PaintChromeVisibility(bool") {
		t.Error("нанесение видимости не отделено от решения: пока оно живёт внутри " +
			"обработчика события, пересобранные поверхности до него не доходят")
	}
	if !strings.Contains(theme, "PaintChromeVisibility(_chromeHidden)") {
		t.Error("пересборка хрома не наносит видимость на свежие поверхности — " +
			"догрузка шрифта или фонов темы посреди катсцены вернёт диалог на экран")
	}
}

// ПОМЕТКУ РЕЖИССЁРА ОТПУСКАЮТ ВМЕСТЕ С ПАНЕЛЬЮ.
//
// Пометка «поднят модальный экран» живёт у Режиссёра и гасит декор всей
// оболочки: баблики валют, кружок загрузок, верхнюю тап-зону. Пока экран
// открыт — правильно. Но если сам экран убрали из дерева, пока он открыт,
// закрывать пометку становится некому: оболочка остаётся подавленной НАВСЕГДА
// и без единого признака в логе.
//
// Правило несут общее окно истории и меню сцены. Попап его не нёс — типичное
// «правило написано у соседа, а тут его нет»: три дома одного слоя, у двух
// проверка есть.
func TestDirectorMarkFollowsThePanel(t *testing.T) {
	root := repoRoot(t)
	дома := map[string]string{
		"unity/Packages/com.lvn.engine/Runtime/UI/VnPanelHost.cs":    "StoryPanel",
		"unity/Packages/com.lvn.engine/Runtime/UI/StageMenu.cs":      "QuickMenu",
		"unity/Packages/com.lvn.engine.shell/Runtime/PopupScreen.cs": "Alert",
	}
	seen := 0
	var немые []string
	for p, метка := range дома {
		src := stripComments(string(mustRead(t, filepath.Join(root, p))))
		seen++
		есть := strings.Contains(src, "DetachFromPanelEvent") &&
			strings.Contains(src, "AttachToPanelEvent") &&
			strings.Contains(src, "Close(Lvn.UI.LvnScreenDirector."+метка) ||
			strings.Contains(src, "Close(LvnScreenDirector."+метка)
		if !есть || !strings.Contains(src, "AttachToPanelEvent") {
			немые = append(немые, filepath.Base(p)+" ("+метка+")")
		}
	}
	sawSources(t, seen, 3, "домов с пометкой Режиссёра")
	sort.Strings(немые)
	if len(немые) > 0 {
		t.Errorf("уход с панели не отпускает пометку Режиссёра: %s\n\n"+
			"Экран, убранный из дерева в открытом виде, оставит оболочку "+
			"подавленной навсегда — без единого признака в логе.",
			strings.Join(немые, ", "))
	}
}

// ПРАВИЛО ПРО ШАБЛОННЫЙ АДРЕС ЖИВЁТ В ОДНОМ МЕСТЕ.
//
// «Адрес с неподставленной осью — не адрес»: файла с фигурными скобками в имени
// нет ни на одном сервере, и каждый такой запрос — ожидание, круг по сети и
// гарантированный 404. Правило было записано в доме списков и применялось там
// же — в ОДНОМ списке из семи; а живой случай 02.09 рождается вообще после
// подстановки (гардероб подставлял одну ось из двух), и списками не ловится.
//
// Теперь правило одно, живёт у дома адресов, и спрашивают его у ДВЕРИ
// загрузчика. Сторож держит единственность: вторая проверка на «{» в тракте
// содержимого — это вторая правда, которая однажды разойдётся с первой.
func TestTemplateRuleHasOneHome(t *testing.T) {
	root := repoRoot(t)
	dir := filepath.Join(root, "unity/Packages/com.lvn.engine/Runtime/Content")
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatal(err)
	}
	brace := regexp.MustCompile(`IndexOf\('\{'\)|Contains\("\{"\)`)
	seen := 0
	var копии []string
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".cs") {
			continue
		}
		seen++
		src := stripComments(string(mustRead(t, filepath.Join(dir, e.Name()))))
		for range brace.FindAllString(src, -1) {
			if e.Name() == "DownloadPolicy.cs" {
				continue // единственный законный дом правила
			}
			копии = append(копии, e.Name())
		}
	}
	sawSources(t, seen, 20, "файлов тракта содержимого")
	sort.Strings(копии)
	if len(копии) > 0 {
		t.Errorf("правило про шаблонный адрес записано не только у дома: %s\n\n"+
			"Спрашивайте DownloadPolicy.IsTemplate. Вторая проверка на «{» — "+
			"вторая правда, и она однажды разойдётся с первой: список отфильтрует, "+
			"а собранный на лету адрес всё равно уйдёт в сеть.",
			strings.Join(копии, ", "))
	}
}

// ТЕРПЕНИЕ — ДОГАДКА, ПОГРУЗКА — ФАКТ.
//
// Лекарь отличает живую загрузку от настоящей поломки двумя способами. Первый
// — терпение: «крупный канвас декодится 0.6с, потерпим 2с». Это число про ЭТУ
// машину; на слабом телефоне и плохой сети картинку везут секунд десять,
// терпение кончается на второй, и лечение перебивает живую работу. У фона это
// не безобидно: он везётся с повторами и разрежением (до восьми попыток), а
// лечение забирает у него поколение и начинает лестницу с первой ступени —
// лекарь ломает ровно тот механизм, который должен был пережить обрыв.
//
// Второй способ — спросить того, кто везёт (`working:`). Это факт, и он не
// зависит ни от машины, ни от сети.
//
// Сторож держит правило: объявил терпение — назови и того, у кого спросить.
// Терпение остаётся (работа может ещё не начаться), но перестаёт быть
// ЕДИНСТВЕННЫМ доводом.
func TestPatienceNeverStandsAlone(t *testing.T) {
	root := repoRoot(t)
	var mute []string
	seen := 0
	for _, pkg := range []string{"com.lvn.engine", "com.lvn.engine.shell"} {
		dir := filepath.Join(root, "unity/Packages", pkg, "Runtime")
		_ = filepath.Walk(dir, func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return nil
			}
			src := stripComments(string(mustRead(t, path)))
			for at := 0; ; {
				i := strings.Index(src[at:], ".Watch(")
				if i < 0 {
					break
				}
				at += i + len(".Watch(")
				args, ok := topLevelArgs(src, at)
				if !ok {
					continue
				}
				seen++
				call := strings.Join(args, ",")
				if strings.Contains(call, "patience") && !strings.Contains(call, "working") {
					mute = append(mute, filepath.Base(path)+": "+strings.TrimSpace(args[0]))
				}
			}
			return nil
		})
	}
	sawSources(t, seen, 4, "недугов под наблюдением")
	sort.Strings(mute)
	if len(mute) > 0 {
		t.Errorf("терпение объявлено, а спросить «везут?» не у кого (%d):\n  %s\n\n"+
			"Число секунд описывает эту машину, а не работу. Назовите "+
			"`working:` — тот, кто везёт, отвечает фактом.",
			len(mute), strings.Join(mute, "\n  "))
	}
}

// «ПОЛОЖЕН ЛИ КОД» — ОДИН СПИСОК НА ДВЕ СТОРОНЫ.
//
// Часть арта живёт растром НАМЕРЕННО: пиксель-арт и обшивка интерфейса
// (блочное сжатие размажет сетку и тонкие линии) и крошка-заготовка @mini —
// её показывают, пока едет крупный.
//
// Список нужен обеим сторонам, и врозь они опасны НЕСИММЕТРИЧНО. Сервер, не
// знающий исключения, потратит процессор на лишний код. Клиент, не знающий
// его, попросит код, которого не собирают нигде, — и, раз растрового пути у
// арта истории нет, будет ждать 7.5 с, а потом не покажет НИЧЕГО. Ровно это и
// случилось 02.09 с крошкой: правило было записано у сервера, а на клиенте
// наследовалось от уменьшителя — и ушло вместе с ним, когда отображение
// адреса в код перестало через уменьшитель ходить.
func TestCodedArtIsOneList(t *testing.T) {
	root := repoRoot(t)
	cs := stripComments(string(mustRead(t,
		filepath.Join(root, "unity/Packages/com.lvn.engine/Runtime/Content/DownloadPolicy.cs"))))
	go_ := stripComments(string(mustRead(t, filepath.Join(root, "server/ktx2.go"))))

	body, ok := ruleBody(cs, "public static bool CodedArt(string url)")
	if !ok {
		t.Fatal("DownloadPolicy.CodedArt не найден — правило «положен ли код» " +
			"на клиенте снова живёт по месту")
	}
	goBody, ok := ruleBody(go_, "func ktx2Coded(lowPath string) bool {")
	if !ok {
		t.Fatal("server: ktx2Coded не найден — правило «положен ли код» снова " +
			"живёт внутри прогрева и ленивому тракту неизвестно")
	}

	// Крошка на сервере названа константой (miniSuffix), на клиенте — своей
	// (QMini). Сверяем ЗНАЧЕНИЯ, а не написание.
	// ОБШИВКА ИНТЕРФЕЙСА ИЗ ЭТОГО СПИСКА УШЛА 02.09, и это тоже правило.
	// Папку исключали целиком — верно для кнопок и рамок, неверно для полотна
	// витрины (тот же /ui/, но 2000×1500 на весь экран): его распаковка
	// заняла 3334 мс, вуаль снялась без него, и первое, что видел игрок, был
	// пустой экран. Теперь код за неё СПРАШИВАЮТ, а решает сервер по размеру;
	// растр ей при этом разрешён — см. TestRasterIsForbiddenOnlyForStoryArt.
	for _, ex := range []struct{ what, client, server string }{
		{"пиксель-арт", "/pixel/", "/pixel/"},
		{"крошка-заготовка", "QMini", "miniSuffix"},
	} {
		if !strings.Contains(body, ex.client) {
			t.Errorf("клиент не исключает %s из кодов (%s): попросит код, "+
				"которого не собирают, и не покажет ничего", ex.what, ex.client)
		}
		if !strings.Contains(goBody, ex.server) {
			t.Errorf("сервер не исключает %s из кодов (%s)", ex.what, ex.server)
		}
	}

	// Ленивый тракт спрашивает тот же дом, а не свой список.
	lazy, ok := ruleBody(go_, "func (s *server) withKTX2(")
	if !ok {
		t.Fatal("server: withKTX2 не найден")
	}
	if !strings.Contains(lazy, "ktx2Coded(") {
		t.Error("ленивый обработчик .ktx2 не спрашивает «положен ли код»: " +
			"один запрос отменяет намеренное решение «живёт растром»")
	}
}

// Тело правила: от заголовка до закрывающей скобки того же уровня; понимает и
// стрелочное тело C#. Отдельно от methodBody конформанса НАРОЧНО: тот стирает
// строковые литералы, а здесь сверяются как раз они.
func ruleBody(src, header string) (string, bool) {
	i := strings.Index(src, header)
	if i < 0 {
		return "", false
	}
	open := strings.Index(src[i:], "{")
	if open < 0 {
		// C#-выражение вместо тела: `=> …;`
		if arrow := strings.Index(src[i:], "=>"); arrow >= 0 {
			end := strings.Index(src[i+arrow:], ";")
			if end >= 0 {
				return src[i+arrow : i+arrow+end], true
			}
		}
		return "", false
	}
	// Стрелочное тело раньше первой скобки — оно и есть тело.
	if arrow := strings.Index(src[i:], "=>"); arrow >= 0 && arrow < open {
		end := strings.Index(src[i+arrow:], ";")
		if end >= 0 {
			return src[i+arrow : i+arrow+end], true
		}
	}
	depth := 0
	for j := i + open; j < len(src); j++ {
		switch src[j] {
		case '{':
			depth++
		case '}':
			depth--
			if depth == 0 {
				return src[i+open : j], true
			}
		}
	}
	return "", false
}

// КРУГ — ОДНО РЕШЕНИЕ, А НЕ ТРИ ЧИСЛА.
//
// Круглых элементов в оболочке восемь: точка непрочитанного, кольцо аватара,
// медаль места, логотип, ореол под значком, ползунок переключателя, свотч
// цвета. Каждый собирался руками — ширина, высота и скругление тремя
// строками, — и связь между ними нигде не была записана.
//
// Половину размера писали по-разному: «wide ? 39f : 30f» при коробке 78/60,
// «(avatar + 12f) / 2f» при коробке «avatar + (first ? 12 : 8)» (радиус БОЛЬШЕ
// половины — спасал зажим UITK, а не расчёт), «EmoBarWidth * 0.5f». А чаще
// радиус брали ТОКЕНОМ ТЕМЫ, и круг выходил по совпадению: свотч 56 px с
// RadiusLg = 28 — ровно половина, но лишь потому, что два независимых числа
// темы сегодня так соотносятся.
//
// Сторож ловит арифметику: аргумент скругления, целиком равный половине
// чего-то, — это круг или пилюля, и у них есть дом. Промежуточные значения
// (морф капсулы: Mathf.Lerp(MiniSize * 0.5f, 22f, k)) не трогаются — там
// половина лишь один конец пути.
func TestHalfSizeRadiusHasAHome(t *testing.T) {
	root := repoRoot(t)
	half := regexp.MustCompile(`^\s*\(?[\w.\[\]+ ]+\)?\s*(?:\*\s*0\.5f|/\s*2(?:\.0)?f?)\s*$`)
	var hand []string
	seen := 0
	for _, rel := range []string{
		"unity/Packages/com.lvn.engine.shell/Runtime",
		"unity/Packages/com.lvn.engine/Runtime/UI",
	} {
		_ = filepath.Walk(filepath.Join(root, rel), func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return nil
			}
			src := stripComments(string(mustRead(t, path)))
			for at := 0; ; {
				i := strings.Index(src[at:], "LvnChrome.Round(")
				if i < 0 {
					break
				}
				at += i + len("LvnChrome.Round(")
				args, ok := topLevelArgs(src, at)
				if !ok || len(args) != 2 {
					continue
				}
				seen++
				if half.MatchString(args[1]) {
					hand = append(hand, filepath.Base(path)+": Round(…,"+strings.TrimSpace(args[1])+")")
				}
			}
			return nil
		})
	}
	sawSources(t, seen, 30, "скруглений оболочки")
	sort.Strings(hand)
	if len(hand) > 0 {
		t.Errorf("половина размера посчитана на месте вызова (%d):\n  %s\n\n"+
			"Круг — LvnChrome.Circle(el, диаметр), полоса — LvnChrome.Pill(el, толщина). "+
			"Сосчитанная руками половина живёт отдельно от размера и переживает "+
			"его правку: размер поменяют, радиус забудут, и круг станет "+
			"квадратом со скруглением — молча.",
			len(hand), strings.Join(hand, "\n  "))
	}
}

// У СТОПКИ ВЫБОРА ОДНА РУЧКА.
//
// Гасят её две независимые причины: нажатие в обработке (идёт оплата или
// доигрывается такт) и незакончившаяся хореография (актёр ещё входит в кадр).
// Каждая держала `SetEnabled` сама, парами «выключил — включил» по девяти
// местам, и вторая снимала первую: пока шла ОПЛАТА, конец входа актёра зажигал
// стопку обратно и заново запускал отсчёт выбора.
//
// Второе нажатие ловил отдельный флаг, поэтому дважды не платили. Но кнопки
// светились живыми, а срок тикал — и увидеть это можно было только глазами, в
// узком окне, на медленной сети.
//
// Теперь причины считает LvnReasons, а включает и гасит ОДНО место.
func TestChoiceStackHasOneSwitch(t *testing.T) {
	root := repoRoot(t)
	dir := filepath.Join(root, "unity/Packages/com.lvn.engine/Runtime/UI")
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatal(err)
	}
	seen := 0
	var extra []string
	for _, e := range entries {
		if e.IsDir() || !strings.HasPrefix(e.Name(), "VnStage") {
			continue
		}
		seen++
		src := stripComments(string(mustRead(t, filepath.Join(dir, e.Name()))))
		for _, line := range strings.Split(src, "\n") {
			if !strings.Contains(line, "SetEnabled") || !strings.Contains(line, "_choices") {
				continue
			}
			if strings.Contains(line, "PaintChoiceEnabled") {
				continue // сам выключатель
			}
			extra = append(extra, e.Name()+": "+strings.TrimSpace(line))
		}
	}
	sawSources(t, seen, 10, "частей сцены")
	sort.Strings(extra)
	if len(extra) > 0 {
		t.Errorf("стопку выбора гасят мимо выключателя (%d):\n  %s\n\n"+
			"Возьмите причину (_choiceLocks.Hold/Drop) и позовите "+
			"PaintChoiceEnabled(). Пара «выключил — включил», написанная одной "+
			"причиной, снимает чужую: так оплата теряла свой замок на входе "+
			"актёра.",
			len(extra), strings.Join(extra, "\n  "))
	}
}

// СЧЁТ ПРИЧИН — ОДИН НА ДВИЖОК.
//
// Форма «держат, пока держит хоть одна причина» жила двумя экземплярами:
// у режиссёра экрана (скрытый интерфейс) и у стопки выбора. Второй экземпляр
// той же логики расходится молча — и расходится в ту же сторону, что и флаг
// до него: снимут одну причину, отпустят все.
func TestReasonCountingHasOneHome(t *testing.T) {
	root := repoRoot(t)
	own := regexp.MustCompile(`HashSet<string>\s+_(?:hidden|held|locks|reasons|holders)\b`)
	seen := 0
	var copies []string
	for _, rel := range []string{
		"unity/Packages/com.lvn.engine/Runtime/UI",
		"unity/Packages/com.lvn.engine.shell/Runtime",
	} {
		_ = filepath.Walk(filepath.Join(root, rel), func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return nil
			}
			seen++
			if filepath.Base(path) == "LvnReasons.cs" {
				return nil // единственный законный дом
			}
			if own.MatchString(stripComments(string(mustRead(t, path)))) {
				copies = append(copies, filepath.Base(path))
			}
			return nil
		})
	}
	sawSources(t, seen, 60, "файлов слоя интерфейса")
	sort.Strings(copies)
	if len(copies) > 0 {
		t.Errorf("счёт причин заведён своим множеством (%d): %s\n\n"+
			"Есть LvnReasons: Hold/Drop возвращают «состояние перевернулось», "+
			"а Journal отвечает на вопрос «почему оно до сих пор выключено» — "+
			"на который у флага ответа нет вовсе.",
			len(copies), strings.Join(copies, ", "))
	}
}

// ВЗЯЛ НОМЕР — СПРОСИ, НЕ ОБОГНАЛИ ЛИ.
//
// Половины неравны по громкости, и это главное. Забыть ВЗЯТЬ номер видно
// сразу: чужая работа не отменяется, и опоздавший рисует поверх нового.
// Забыть СПРОСИТЬ не видно никогда: код отработает до конца и тихо поставит
// своё — старое. Ни исключения, ни строки в логе.
//
// Поэтому файл, который берёт номер, обязан в том же файле его и сверять.
func TestClaimingATicketMeansCheckingIt(t *testing.T) {
	root := repoRoot(t)
	claim := regexp.MustCompile(`\.Claim\(`)
	check := regexp.MustCompile(`\.Mine\(|\.IsNewest\(|\.MayTouch\(`)
	seen := 0
	var mute []string
	for _, rel := range []string{
		"unity/Packages/com.lvn.engine.shell/Runtime",
		"unity/Packages/com.lvn.engine/Runtime/UI",
	} {
		_ = filepath.Walk(filepath.Join(root, rel), func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return nil
			}
			src := stripComments(string(mustRead(t, path)))
			if !claim.MatchString(src) {
				return nil
			}
			seen++
			if !check.MatchString(src) {
				mute = append(mute, filepath.Base(path))
			}
			return nil
		})
	}
	sawSources(t, seen, 5, "мест, берущих номер в очереди")
	sort.Strings(mute)
	if len(mute) > 0 {
		t.Errorf("номер берут, а не обогнали ли — не спрашивают (%d): %s\n\n"+
			"Взятый и не спрошенный номер хуже, чем никакого: он создаёт "+
			"впечатление защиты. Спросите Mine/IsNewest/MayTouch после КАЖДОГО "+
			"ожидания.",
			len(mute), strings.Join(mute, ", "))
	}
}

// ПРИЁМКА СВЕЖЕГО КАТАЛОГА СТОИТ В ОЧЕРЕДИ.
//
// У неё ДВА повода начаться — «сервер сказал, что контент сменился» и «запуск
// догнал сеть», — и между записью офлайновой копии и обновлением экранов лежит
// ожидание (байты меню). Две приёмки, начатые подряд, приходят в любом порядке:
// сеть очерёдности не обещает. Победивший последним ставил СВОЁ — и это старое.
//
// Правило было записано у соседа: смена языка сверяет выбор игрока после
// КАЖДОГО ожидания и объясняет, почему. Здесь его не было — при том, что
// сюжет тот же, а цена выше: приложение оставалось на отменённом каталоге, и
// офлайновая копия записывалась вчерашней.
func TestManifestAdoptionStandsInLine(t *testing.T) {
	root := repoRoot(t)
	src := stripComments(string(mustRead(t,
		filepath.Join(root, "unity/Packages/com.lvn.engine.shell/Runtime/NovelApp.Boot.cs"))))
	body, ok := ruleBody(src, "private async Task<bool> AdoptManifestAsync(")
	if !ok {
		t.Fatal("AdoptManifestAsync не найден или больше не отвечает, приняли ли " +
			"его каталог: без ответа зовущий не узнает, что его обогнали, и " +
			"перезагрузит открытую главу по отменённому каталогу")
	}
	if !strings.Contains(body, ".Claim(") {
		t.Error("приёмка каталога не берёт номер: две приёмки разъедутся молча")
	}
	if !strings.Contains(body, ".Mine(") {
		t.Error("приёмка каталога не спрашивает, не обогнали ли её ПОСЛЕ ожидания")
	}
	at := strings.Index(body, "await")
	mine := strings.Index(body, ".Mine(")
	if at >= 0 && mine >= 0 && mine < at {
		t.Error("сверка стоит ДО ожидания — она отвечает на вопрос, который " +
			"ещё не задан: обгоняют нас именно в ожидании")
	}
}

// РАСТР ЗАПРЕЩЁН ТОЛЬКО АРТУ ИСТОРИИ.
//
// «Положен ли код» и «можно ли показать растром» — разные вопросы, и пока они
// были одним, обшивка интерфейса выпадала из кодов ЦЕЛИКОМ, по папке. Правило
// верное для кнопок и рамок (блочное сжатие размажет пиксельную сетку) и
// неверное для полотна витрины: тот же `/ui/`, но 2000×1500 на весь экран.
// Живой лог 02.09: 3334 мс процессорной распаковки, вуаль снялась без полотна,
// игрок увидел пустой экран.
//
// Строгость остаётся там, где заведена, — на арте истории: там растровый
// запасной путь полгода прятал поломку кодов. Обшивке растр разрешён, потому
// что у неё он объявленный путь, а не подмена медленным.
//
// Сторож держит разделение: строгий гейт спрашивает RasterForbidden, а не
// CodedArt. Слить их обратно — значит либо вернуть 3334 мс на первый экран,
// либо оставить арт истории без показа, когда код не собрался.
func TestRasterIsForbiddenOnlyForStoryArt(t *testing.T) {
	root := repoRoot(t)
	policy := stripComments(string(mustRead(t,
		filepath.Join(root, "unity/Packages/com.lvn.engine/Runtime/Content/DownloadPolicy.cs"))))
	sprites := stripComments(string(mustRead(t,
		filepath.Join(root, "unity/Packages/com.lvn.engine/Runtime/Content/ContentLoader.Sprites.cs"))))

	forbid, ok := ruleBody(policy, "public static bool RasterForbidden(string url)")
	if !ok {
		t.Fatal("DownloadPolicy.RasterForbidden не найден: строгий гейт снова " +
			"судит по «положен ли код», и обшивка интерфейса выпадает из кодов целиком")
	}
	if !strings.Contains(forbid, "/ui/") {
		t.Error("запрет растра не отличает обшивку интерфейса: ей растр — " +
			"объявленный путь, а не подмена медленным")
	}
	coded, ok := ruleBody(policy, "public static bool CodedArt(string url)")
	if !ok {
		t.Fatal("DownloadPolicy.CodedArt не найден")
	}
	if strings.Contains(coded, "/ui/") {
		t.Error("«положен ли код» снова отказывает обшивке целиком по папке: " +
			"полотно витрины 2000×1500 лежит там же, где кнопки, и платит " +
			"за это тремя секундами процессорной распаковки")
	}
	if !strings.Contains(sprites, "RasterForbidden(") {
		t.Error("строгий гейт показа не спрашивает RasterForbidden")
	}
}

// ПРОПАЖА КОДА — НЕ ВЕЧНАЯ.
//
// На запрос кода сервер отвечает 404 И СТАВИТ ЕГО В ОЧЕРЕДЬ: кодирование
// крупного файла занимает секунды, держать ради него запрос никто не станет.
// То есть 404 на .ktx2 значит «зайдите позже».
//
// Память о пропаже живёт две минуты, а ждёт показ семь секунд — и все пять
// заходов «зайдите позже» били в эту память, не доходя до сервера. Живой лог
// 02.09: семь секунд ожидания ВПУСТУЮ, после чего фон и ядро портала не
// показаны вовсе, хотя код к третьей секунде уже лежал на диске сервера.
//
// У кодов своя память с правильным смыслом (счёт промахов на адрес, чужой
// успех будит холодных); общая им только мешает.
func TestMissingCodeIsNotForever(t *testing.T) {
	root := repoRoot(t)
	src := stripComments(string(mustRead(t,
		filepath.Join(root, "unity/Packages/com.lvn.engine/Runtime/Content/ContentLoader.Fetch.cs"))))
	body, ok := ruleBody(src, "private void RememberMissing(string url, int status)")
	if !ok {
		t.Fatal("RememberMissing не найден")
	}
	if !strings.Contains(body, ".ktx2") {
		t.Error("память о пропаже снова забирает адреса кодов: ожидание кода " +
			"будет бить в неё вместо сервера, и все семь секунд пройдут впустую — " +
			"а показ после них не состоится вовсе")
	}
}

// КРОШКА-ЗАГОТОВКА НЕ ЖДЁТ ГРАНИЦЫ КАДРА.
//
// Распаковка крупного арта идёт хитростью: Unity расшифровывает картинку на
// своём рабочем потоке, и главный поток не платит сотнями миллисекунд. Обмен
// выгодный — но у него есть цена: событие о готовности Unity поднимает НА
// ГЛАВНОМ ПОТОКЕ, в покадровой обработке, и между «работа кончилась» и «мы
// узнали» лежит остаток кадра.
//
// Для 200×256 это разорительно: сама распаковка — единицы миллисекунд, а
// платим границей кадра. Живой лог 02.09: пять крошек одного размера в одном
// такте дали 31, 32, 498, 506 и 1006 мс.
//
// И бьёт это там, где крошка нужнее всего. Весь её смысл — появиться
// мгновенно, пока едет крупный арт; заложник кадра появляется вместе с ним,
// то есть не делает ничего. А тяжёлые кадры — это ровно бут.
func TestThumbnailDoesNotWaitForAFrame(t *testing.T) {
	root := repoRoot(t)
	src := stripComments(string(mustRead(t,
		filepath.Join(root, "unity/Packages/com.lvn.engine/Runtime/Content/ContentLoader.Sprites.cs"))))
	body, ok := ruleBody(src,
		"private async Task<(Texture2D tex, long queueMs)> DecodeTextureOffThreadAsync(")
	if !ok {
		t.Fatal("DecodeTextureOffThreadAsync не найден")
	}
	if !strings.Contains(body, "QMini") {
		t.Error("крошка снова идёт покадровой дорогой: её распаковка — единицы " +
			"миллисекунд, а ждать она будет остаток кадра, и хуже всего — на " +
			"буте, ради которого заготовка и заведена")
	}
	// Отказ должен стоять ДО сетевой части: иначе мы уже заплатили за запрос.
	at := strings.Index(body, "QMini")
	web := strings.Index(body, "UnityWebRequestTexture")
	if at >= 0 && web >= 0 && at > web {
		t.Error("отказ крошке стоит ПОСЛЕ запроса — платим ровно то, чего избегаем")
	}
}

// СЕРВЕР БЕЗ КОДИРОВЩИКА ГОВОРИТ ОБ ЭТОМ ВСЛУХ.
//
// Раньше `warmAll` при отсутствии basisu молча возвращался с припиской «видно
// по первому же запросу». Неправда: запрос отвечает 404, а 404 на код значит
// «зайдите позже» — отличить «ещё кодирую» от «кодировать нечем и не будет»
// по нему нельзя ни клиенту, ни человеку. Клиент честно ждёт свои секунды,
// потом не показывает арт истории вовсе, и в ЕГО логе написано «проверьте
// basisu на сервере» — а в логе сервера про это нет ни строки.
//
// Молчала именно та половина, которая знает ответ.
func TestServerSaysWhenItCannotEncode(t *testing.T) {
	root := repoRoot(t)
	src := stripComments(string(mustRead(t, filepath.Join(root, "server/ktx2.go"))))
	body, ok := ruleBody(src, "func (t *ktx2Transcoder) warmAll(contentRoot string) {")
	if !ok {
		t.Fatal("warmAll не найден")
	}
	head := body
	if i := strings.Index(body, "go func()"); i > 0 {
		head = body[:i]
	}
	if !strings.Contains(head, "log.Printf") {
		t.Error("сервер без basisu снова молчит: отличить «ещё кодирую» от " +
			"«кодировать нечем» по 404 невозможно, и разбираться человек " +
			"пойдёт не туда")
	}
}

// «КОМУ ПОЛОЖЕН КОД» — ТЕПЕРЬ И У СКРИПТА ПРОГРЕВА.
//
// Список жил уже четырьмя копиями: клиент (DownloadPolicy.CodedArt), сервер
// (ktx2Coded + ktx2WorthCoding) и tools/warm-ktx2.sh. Четвёртая отстала:
// скрипт не заходил в `/ui/` вовсе, и полотно витрины 2000×1500 оставалось
// без кода НИГДЕ — сервер его собрать мог не успеть или не смочь, а скрипт
// не пробовал. Игрок платил тремя секундами на первом экране.
//
// Сторож держит две вещи: скрипт заходит в /ui/ и судит там по размеру тем же
// порогом, что сервер.
func TestWarmScriptKnowsTheSameList(t *testing.T) {
	root := repoRoot(t)
	sh := string(mustRead(t, filepath.Join(root, "tools/warm-ktx2.sh")))
	if !strings.Contains(sh, "-path '*/ui/*'") {
		t.Error("прогрев снова не заходит в /ui/: полотно витрины останется " +
			"без кода нигде")
	}
	if !strings.Contains(sh, "1024") {
		t.Error("прогрев не судит обшивку по размеру — либо пропустит полотно, " +
			"либо размажет блочным сжатием кнопки и рамки")
	}
	if !strings.Contains(sh, "*/pixel/*") || !strings.Contains(sh, "@mini") {
		t.Error("прогрев потерял исключение, которое есть у клиента и сервера")
	}
	// Порог — тот же, что у сервера. Разъедутся — часть картинок получит код
	// у одного и не получит у другого, и разница будет видна только на глаз.
	goSrc := stripComments(string(mustRead(t, filepath.Join(root, "server/ktx2.go"))))
	if !strings.Contains(goSrc, "ktx2ChromeBox = 1024") {
		t.Error("порог обшивки у сервера больше не 1024 — поправьте и скрипт " +
			"(tools/warm-ktx2.sh), они обязаны совпадать")
	}
}

// ЗАМЕР НАЗЫВАЕТ ТО, ЧТО ИЗМЕРИЛ.
//
// Число, полученное ожиданием события Unity, включает остаток кадра: о
// готовности мы узнаём на главном потоке, в покадровой обработке. Подпись
// «decode … (worker thread)» обещала стоимость работы — и врала ровно там, где
// эти числа смотрят: на буте, где кадры по полсекунды.
//
// 02.09 это стоило полдня: пять разных файлов дали 917, 925, 929, 932 и 936 мс
// при кадре в 955 мс, и читалось как «декодер стал в 24 раза медленнее».
// Работа так не совпадает — совпадает ожидание.
//
// Сторож держит две вещи: покадровая дорога зовётся `wall`, а не `decode`, и
// рядом стоит длина кадра, чтобы число объясняло себя само.
func TestWallTimeIsNotCalledDecode(t *testing.T) {
	root := repoRoot(t)
	src := stripComments(string(mustRead(t,
		filepath.Join(root, "unity/Packages/com.lvn.engine/Runtime/Content/ContentLoader.Sprites.cs"))))
	if strings.Contains(src, `decode={decodeMs - queueMs}ms{(offThread`) {
		t.Error("ожидание снова названо распаковкой: на буте это число про " +
			"длину кадра, и читатель пойдёт чинить декодер")
	}
	if !strings.Contains(src, "wall=") {
		t.Error("у покадровой дороги нет честного имени (wall=)")
	}
	if !strings.Contains(src, "LvnFrameWatch.LastFrameMs") {
		t.Error("рядом с ожиданием не стоит длина кадра — число нечем " +
			"проверить на месте")
	}
	// ТА ЖЕ БОЛЕЗНЬ У СОСЕДА. Расшифровка кода меряется тем же способом —
	// секундомер вокруг ожидания события Unity, — и именно её строка ввела в
	// заблуждение 02.09. Правило, записанное в одном файле и отсутствующее в
	// соседнем, живёт ровно до следующего чтения лога.
	ktx := stripComments(string(mustRead(t,
		filepath.Join(root, "unity/Packages/com.lvn.engine/Runtime/Content/ContentLoader.Ktx2.cs"))))
	if strings.Contains(ktx, "ktx2 transcode {ktx2Url}: {sw.ElapsedMilliseconds}ms") {
		t.Error("расшифровка кода снова названа работой: это ожидание, и на " +
			"буте оно про длину кадра")
	}
	if !strings.Contains(ktx, "LvnFrameWatch.LastFrameMs") {
		t.Error("у расшифровки кода нет длины кадра рядом — число не проверить")
	}
	watch := stripComments(string(mustRead(t,
		filepath.Join(root, "unity/Packages/com.lvn.engine/Runtime/LvnFrameWatch.cs"))))
	body, ok := ruleBody(watch, "public static void Frame(float dt, int frameCount")
	if !ok {
		t.Fatal("LvnFrameWatch.Frame не найден")
	}
	// Длина кадра обязана писаться ДО раннего выхода: обычный кадр запинкой
	// не считается, но длину имеет — а замер спрашивает именно обычные.
	set := strings.Index(body, "LastFrameMs =")
	ret := strings.Index(body, "return")
	if set < 0 || (ret >= 0 && set > ret) {
		t.Error("длина кадра пишется после раннего выхода — значит на обычных " +
			"кадрах она врёт последним рывком, и замер станет ещё запутаннее")
	}
}

// ДЛИНА КАДРА МЕРЯЕТСЯ С ПЕРВОГО КАДРА, А НЕ С ПЕРВОЙ СЦЕНЫ.
//
// Замер ожидания («wall=… кадр N мс») ставит рядом с числом длину последнего
// кадра, чтобы число объясняло себя. Но кадры считала только сцена
// (VnStage.Update): на витрине, где грузится полотно и где живёт вся
// проблема первого экрана, счётчик стоял, и «кадр 0 мс» рядом с «wall=6451»
// читалось как «ждали не кадра — значит, декодер». Проверка, отвечающая на
// другой вопрос: число было верным, но про другое время.
//
// Дом покадрового пульса оболочки — наблюдатель панели (LvnPanelWatcher): он
// рождается вместе с общими настройками панели, то есть с вуалью, и живёт
// до конца. Сторож держит: (1) счётчик крутит ровно одно место, и это
// наблюдатель панели; (2) тик стоит ДО раннего выхода «размер экрана не
// менялся»; (3) пояснение сцены («чем занят движок») не потерялось — сцена
// отдаёт его счётчику через Busy.
func TestFrameLengthIsMeasuredFromBoot(t *testing.T) {
	root := repoRoot(t)
	rt := filepath.Join(root, "unity/Packages/com.lvn.engine/Runtime")
	var callers []string
	_ = filepath.Walk(rt, func(path string, info os.FileInfo, err error) error {
		if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
			return nil
		}
		src := stripComments(string(mustRead(t, path)))
		if strings.Contains(src, "LvnFrameWatch.Frame(") {
			callers = append(callers, strings.TrimPrefix(path, rt+"/"))
		}
		return nil
	})
	if len(callers) != 1 || callers[0] != "UI/LvnPanel.cs" {
		t.Fatalf("длину кадра считает не наблюдатель панели, а %v: счётчик "+
			"должен крутить ровно одно место, живущее с первого кадра", callers)
	}
	panel := stripComments(string(mustRead(t, filepath.Join(rt, "UI/LvnPanel.cs"))))
	at := strings.Index(panel, "class LvnPanelWatcher")
	if at < 0 {
		t.Fatal("LvnPanelWatcher не найден")
	}
	watcher := classBody(panel, at+len("class LvnPanelWatcher"))
	body, ok := ruleBody(watcher, "private void Update()")
	if !ok {
		t.Fatal("у LvnPanelWatcher нет Update()")
	}
	tick := strings.Index(body, "LvnFrameWatch.Frame(")
	ret := strings.Index(body, "return")
	if tick < 0 || (ret >= 0 && tick > ret) {
		t.Error("тик длины кадра стоит после раннего выхода «размер экрана не " +
			"менялся» — то есть почти никогда")
	}
	stage := stripComments(string(mustRead(t, filepath.Join(rt, "UI/VnStage.cs"))))
	// Именно ОТДАЁТ, а не снимает: «Busy = null» в OnDisable — тоже
	// присваивание, и первая версия стража на нём успокаивалась.
	gives := false
	for _, m := range regexp.MustCompile(`LvnFrameWatch\.Busy\s*=\s*([A-Za-z_]\w*)`).FindAllStringSubmatch(stage, -1) {
		if m[1] != "null" {
			gives = true
		}
	}
	if !gives {
		t.Error("сцена больше не рассказывает счётчику, чем занят движок — " +
			"строка FRAME HITCH потеряла «spine builds in flight»")
	}
}

// «ЧТО ТАКОЕ КРУПНЫЙ АРТ ИСТОРИИ» — ОДИН СПИСОК НА ТРИ ЯЗЫКА.
//
// Вопрос «стоит ли этим файлом заниматься» — уменьшать до ступени, собирать
// код для видеокарты, убирать из памяти на выходе из главы — один, а списков
// папок под него было три, и два разошлись: уборка главы не знала /spine/
// (страницы атласа до 8K переживали главу целиком) и сверяла папки с учётом
// регистра. Ровно так же она однажды уже не знала /sprites/ — и тогда
// починили одну строку списка вместо того, чтобы завести списку дом.
//
// Дом — DownloadPolicy.LargeStoryArt. Сторож держит: (1) в C# папки арта
// перечислены только там (файл, называющий две и больше из четырёх папок
// строками, — вторая копия); (2) у сервера (ktx2LargeArt) и у скрипта
// прогрева (tools/warm-ktx2.sh) те же четыре папки — другой язык, та же
// правда.
func TestLargeStoryArtIsOneList(t *testing.T) {
	root := repoRoot(t)
	folders := []string{"/bg/", "/art/", "/sprites/", "/spine/"}
	var copies []string
	seen := 0
	for _, pkg := range []string{"com.lvn.engine", "com.lvn.engine.shell"} {
		dir := filepath.Join(root, "unity/Packages", pkg, "Runtime")
		_ = filepath.Walk(dir, func(path string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() || !strings.HasSuffix(path, ".cs") {
				return nil
			}
			seen++
			src := stripComments(string(mustRead(t, path)))
			n := 0
			for _, f := range folders {
				if strings.Contains(src, `"`+f+`"`) {
					n++
				}
			}
			if n >= 2 && filepath.Base(path) != "DownloadPolicy.cs" {
				copies = append(copies, filepath.Base(path))
			}
			return nil
		})
	}
	sawSources(t, seen, 200, "файлов движка и оболочки")
	if len(copies) > 0 {
		sort.Strings(copies)
		t.Errorf("список папок арта истории записан не только у дома: %s\n\n"+
			"Спрашивайте DownloadPolicy.LargeStoryArt — вторая копия однажды "+
			"не узнает новую папку, и её арт переживёт главу или пойдёт мимо ступеней",
			strings.Join(copies, ", "))
	}

	home := stripComments(string(mustRead(t,
		filepath.Join(root, "unity/Packages/com.lvn.engine/Runtime/Content/DownloadPolicy.cs"))))
	body, ok := ruleBody(home, "public static bool LargeStoryArt(string url)")
	if !ok {
		t.Fatal("DownloadPolicy.LargeStoryArt не найден — у списка нет дома")
	}
	goSrc := stripComments(string(mustRead(t, filepath.Join(root, "server/ktx2.go"))))
	goBody, ok := ruleBody(goSrc, "func ktx2LargeArt(lowPath string) bool")
	if !ok {
		t.Fatal("ktx2LargeArt не найден в server/ktx2.go")
	}
	sh := string(mustRead(t, filepath.Join(root, "tools/warm-ktx2.sh")))
	for _, f := range folders {
		if !strings.Contains(body, `"`+f+`"`) {
			t.Errorf("дом (DownloadPolicy.LargeStoryArt) не знает %s", f)
		}
		if !strings.Contains(goBody, `"`+f+`"`) {
			t.Errorf("сервер (ktx2LargeArt) не знает %s — арт этой папки останется без кода", f)
		}
		if !strings.Contains(sh, "-path '*"+f+"*'") {
			t.Errorf("скрипт прогрева не заходит в %s", f)
		}
	}
	if !strings.Contains(body, "HasFolder(") {
		t.Error("дом сверяет папки не через HasFolder — то есть с учётом регистра: " +
			"«/Art/Hero.PNG» пойдёт мимо ступеней и переживёт главу")
	}
}
