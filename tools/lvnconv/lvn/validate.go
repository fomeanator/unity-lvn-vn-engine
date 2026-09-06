package lvn

import (
	"fmt"
	"github.com/fomeanator/elvin/tools/lvnconv/internal/nearest"
	"regexp"
	"sort"
	"strings"
	"unicode"
)

// A narration line that starts with a word + an `=` looks like a command whose
// parameters didn't parse and fell through to dialogue text.
var reFailedOp = regexp.MustCompile(`^([a-z_]+)\b[^:]*=`)

// reCmdWord is the leading bare-lowercase word every command starts with.
var reCmdWord = regexp.MustCompile(`^([a-z_][a-z0-9_]*)\s`)

// commandLike returns the leading word of a narration line whose SHAPE is a
// command rather than prose, or "" when the line reads as prose. Three shapes
// qualify, and natural text produces none of them:
//
//	word … = …     a key=value slip           (`sett gold = 1`)
//	word … -> …    a jump; `->` is pure syntax (`iff gold > 1 -> rich`)
//	word … /path   a content url as an argument (`bbg /content/bg/x.jpg`)
//
// A positional-only slip (`shwo mara`, `hidee mara`) is deliberately NOT
// flagged: nothing in its shape separates it from stylised prose ("wave after
// wave" — `wave` is one edit from `save`), and in a pipeline whose gate is "0
// warnings" one false warning gets the whole check switched off.
//
// Measured before shipping: 373 narration lines across 109 local .lvn files
// (howto examples, the tour novella, demos) — zero false positives. Note the
// articy-imported corpus contributes nothing here, since every imported line
// carries a speaker and this check only looks at narration.
func commandLike(text string) string {
	if mm := reFailedOp.FindStringSubmatch(text); mm != nil {
		return mm[1]
	}
	mm := reCmdWord.FindStringSubmatch(text)
	if mm == nil || len(mm[1]) < 3 {
		return ""
	}
	if strings.Contains(text, "->") {
		return mm[1]
	}
	for _, w := range strings.Fields(text)[1:] {
		if strings.HasPrefix(w, "/") && len(w) > 1 {
			return mm[1]
		}
	}
	// ЧЕТВЁРТАЯ ФОРМА: известное имя команды плюс аргумент, похожий на
	// значение, а не на слово. `wait 0.5` вместо `wait ms=500`,
	// `text_pace 30` вместо `text_pace cps=30`, `preload bg/room.png` вместо
	// `preload url=…` — все три молча становились репликами и уезжали игроку.
	//
	// Почему это безопасно, хотя предыдущий абзац отказался ловить
	// позиционные промахи: там слово могло быть ЛЮБЫМ (`wave after wave`), а
	// здесь первое слово обязано быть ИЗВЕСТНОЙ операцией, и следом обязан
	// стоять токен, которого в прозе не бывает — число или путь со слэшем
	// внутри. «Clear skies ahead» не сработает: `skies` не число и не путь.
	if KnownOps[mm[1]] {
		for _, w := range strings.Fields(text)[1:] {
			if looksLikeValue(w) {
				return mm[1]
			}
		}
	}
	return ""
}

// cyrLookalike — кириллические буквы, неотличимые на вид от латинских. Строка
// с такой буквой выглядит как команда и командой не является: разбор её не
// узнаёт, и она уезжает игроку репликой.
var cyrLookalike = map[rune]rune{
	'а': 'a', 'в': 'b', 'е': 'e', 'ё': 'e', 'к': 'k', 'м': 'm', 'н': 'h',
	'о': 'o', 'р': 'p', 'с': 'c', 'т': 't', 'у': 'y', 'х': 'x',
	'і': 'i', 'ј': 'j', 'ѕ': 's', 'һ': 'h', 'ԁ': 'd',
}

// canonOpWord — слово, приведённое к тому виду, в каком пишутся имена команд:
// нижний регистр и латиница вместо кириллических двойников. Пустая строка
// значит «после приведения это всё равно не имя команды».
func canonOpWord(word string) string {
	var b strings.Builder
	for _, r := range strings.ToLower(word) {
		if l, ok := cyrLookalike[r]; ok {
			r = l
		}
		switch {
		case r >= 'a' && r <= 'z', r >= '0' && r <= '9', r == '_':
			b.WriteRune(r)
		default:
			return ""
		}
	}
	return b.String()
}

// languageKeywords — слова, которыми начинается КОНСТРУКЦИЯ языка, а не
// команда. Команды живут в KnownOps и проверяются иначе; здесь только то, что
// разворачивает компилятор .lvns.
var languageKeywords = []string{"if", "else", "elif", "while", "for", "func", "return", "include", "deps", "end"}

// danglingKeyword — служебное слово в начале строки, которая осталась
// повествованием. См. место применения: цена пропуска — конструкция, уехавшая
// игроку текстом.
func danglingKeyword(text string) string {
	t := strings.TrimSpace(text)
	for _, kw := range languageKeywords {
		if t == kw || strings.HasPrefix(t, kw+" ") {
			return kw
		}
	}
	return ""
}

// mistypedOp — строка, которая СТАЛА БЫ командой, напиши автор её первое слово
// строчной латиницей: `Actor мира emotion=happy`, `Bg /content/bg/room.png`,
// `сlear all=1` (здесь «с» кириллическая).
//
// Обе слепоты замерены на живом компиляторе: такие строки собираются в реплику
// и печатаются игроку, а валидатор молчит — распознаватель команд ищет
// строчное ASCII-слово и подобные строки не рассматривает вовсе. Ошибки эти
// делают не глядя: заглавную букву ставит автозамена редактора, кириллический
// двойник приезжает копипастой из переписки.
//
// Узость — предохранитель. Слово обязано ПОСЛЕ приведения точно совпасть с
// именем настоящей команды (близкие промахи оставлены commandLike, чтобы не
// ловить прозу), и строка обязана иметь ту же форму команды: присваивание,
// переход или путь. «Save the world» не подойдёт — формы нет.
func mistypedOp(text string) (raw, canon string) {
	fields := strings.Fields(text)
	if len(fields) < 2 {
		return "", ""
	}
	raw = fields[0]
	canon = canonOpWord(raw)
	if canon == "" || canon == raw || !KnownOps[canon] {
		return "", "" // либо не команда, либо уже написана правильно
	}
	rest := text[len(raw):]
	if strings.Contains(rest, "=") || strings.Contains(rest, "->") {
		return raw, canon
	}
	for _, w := range fields[1:] {
		if i := strings.IndexByte(w, '/'); i > 0 && i < len(w)-1 || strings.HasPrefix(w, "/") && len(w) > 1 {
			return raw, canon
		}
		if looksLikeValue(w) {
			return raw, canon
		}
	}
	return "", ""
}

// looksLikeValue: токен, которого в обычной фразе не встретишь — число
// (включая дробное и отрицательное) или путь со слэшем внутри слова.
func looksLikeValue(w string) bool {
	if w == "" {
		return false
	}
	if i := strings.IndexByte(w, '/'); i > 0 && i < len(w)-1 {
		return true
	}
	dot := false
	for i := 0; i < len(w); i++ {
		c := w[i]
		switch {
		case c >= '0' && c <= '9':
		case c == '.' && !dot && i > 0:
			dot = true
		case c == '-' && i == 0:
		default:
			return false
		}
	}
	return strings.ContainsAny(w, "0123456789")
}

// KnownOps is the registry of command ops the runtime understands. An op
// outside this set is a content error, not a silent no-op — the same hard
// rule the front-ends apply to staging tags, enforced here for any .lvn.
var KnownOps = map[string]bool{
	"say": true, "choice": true, "bg": true, "bg3d": true, "actor": true, "obj": true,
	// clear hides every actor and obj at once — the same removal `show=false`
	// performs, per character, which is what a scene change used to cost: one
	// line per body on stage, and a bug the day someone was added and the list
	// was not. It touches nothing else (backdrop, effects, HUD stay), and takes
	// no fields — `clear actors` / `clear all` would be a second decision the
	// author has to make every time, for a case that has not come up.
	"clear": true,
	"fade":  true, "dim": true, "flash": true, "tint": true, "blur": true,
	"camera": true, "particles": true,
	// Мультиэффект кадра и спрайтовые эффекты актёра (Canvas-путь движка).
	"fx": true, "sfx": true,
	// Створ портала — СЛОЙ сцены между фоном и актёрами (не постэффект: тот
	// живёт на камере, и лечь ПОД героиню не может в принципе). Движок умел
	// его с 27.08, а язык — нет: страж соответствия нашёл это расхождение в
	// тот же день, ради чего он и написан.
	"portal": true,
	// дерево интерфейса; поля лежат ВНУТРИ tree, поэтому набор верхнего
	// уровня открытый — закрывать его значило бы проверять дважды и разное
	"ui": true,
	// кадр без интерфейса: прячет реплику, выборы, метки, меню и деревья `ui`
	"cutscene": true,
	"audio":    true, "wait": true, "input": true, "preload": true, "text_pace": true,
	"text": true,               // reactive HUD/stat label
	"save": true, "load": true, // snapshot save/load
	"label": true, "goto": true, "if": true,
	// track "имя" — метка конверсии. Оп хостовый (сервисный слой), а не
	// движковый: он шлёт событие аналитики, о которой ядро не знает.
	"track": true,
	"set":   true, "inc": true, "hint": true,
	"call": true, "return": true,
	"anim": true, // script-driven tween (lvns `anim`/`move` compile to this)
	// wardrobe_show opens the in-story wardrobe for `char` — emitted by the
	// bundle importer's wardrobe-scene substitution, handled by NovelApp/
	// WardrobeSheet at runtime. Missing here, the validator (and the IDE's
	// Problems strip riding on it) red-flagged every bundle-imported chapter.
	"wardrobe_show": true,
}

// OpenFieldHints — команды, у которых набор полей ОТКРЫТ (сверх грамматики
// автор пишет свои оси гардероба), но известные имена всё же перечислены: по
// ним ищется явная описка. Не путать с OpFields — там набор закрыт, и любое
// чужое поле ошибка; здесь чужое поле норма, а вот «почти известное» — нет.
var OpenFieldHints = map[string][]string{
	"actor": {"id", "sprite_url", "show", "position", "x", "y", "width", "height", "scale", "emotion", "play", "enter", "exit", "flip", "mirror", "rotation", "opacity", "z", "on_click", "draggable", "on_drop", "on_drop_miss", "loop", "drag_bounds"},
	"obj":   {"id", "sprite_url", "x", "y", "width", "height", "anchor", "on_click", "show", "opacity", "z", "enter", "exit", "draggable", "on_drop", "on_drop_miss", "loop", "play", "drag_bounds"},
}

// OpFields is the set of accepted top-level field keys per op, used to catch
// typo'd keys (e.g. `fade too=` instead of `to=`). Only ops with a CLOSED field
// set are listed: say/choice/actor/obj are intentionally omitted because they
// carry open-ended keys (catalog-defined emotion axes, a large placement
// vocabulary, localization ids), where strict checking would false-positive.
var OpFields = map[string][]string{
	"bg":            {"id", "sprite_url", "fade", "pan", "pan_to", "pan_dur"},
	"bg3d":          {"id", "prefab", "scene", "x", "y", "z", "pitch", "yaw", "fov", "dur", "off", "live"},
	"clear":         {}, // deliberately empty: any field on `clear` is a typo
	"fade":          {"to", "duration"},
	"dim":           {"alpha", "duration"},
	"flash":         {"color", "duration"},
	"tint":          {"color", "alpha", "duration"},
	"blur":          {"alpha", "duration"},
	"portal":        {"open", "x", "y", "radius", "color", "dur"},
	"camera":        {"action", "amplitude", "factor", "x", "y", "duration", "mode"},
	"particles":     {"type", "on"},
	"audio":         {"channel", "url", "action", "fade", "volume", "loop"},
	"wait":          {"ms"},
	"wardrobe_show": {"char"},
	"input":         {"var", "prompt", "default", "max"},
	"preload":       {"assets", "url", "kind"},
	"text_pace":     {"cps"},
	"goto":          {"label"},
	"if":            {"expr", "then", "else", "cond"},
	"set":           {"key", "value", "expr", "default"},
	"inc":           {"key", "by"},
	"hint":          {"text", "show", "duration"},
	"call":          {"label"},
	"return":        {},
	"label":         {"id"},
	"save":          {"slot"},
	"load":          {"slot"},
	"text":          {"id", "text", "hide", "x", "y", "anchor", "size", "color", "font"},
	"anim":          {"id", "anim", "stop", "channel", "mode"},
}

// EnumValues lists the CLOSED value sets per (op, field). A value outside the set
// is almost always a typo (`position="lft"`). Only fully-closed sets are here —
// colour names also accept hex, so they're deliberately excluded.
var EnumValues = map[string]map[string][]string{
	// `none` — синоним `clear`, и рантайм его понимает (VnStage.Effects).
	// Валидатор о нём не знал и ругался на законное слово.
	"fade":      {"to": {"black", "white", "clear", "none", ""}},
	"particles": {"type": {"rain", "snow"}},
	"audio":     {"channel": {"music", "ambient", "sfx"}, "action": {"play", "stop"}},
	"camera":    {"action": {"shake", "zoom", "pan", "reset"}},
	"sfx":       {"aura_style": AuraStyles},
	// Словарь мест — один (Placement.SlotNames в движке, enums.actor.position
	// в грамматике). center_left/center_right движок знал, а здесь их не было:
	// валидатор ругался на место, которое рантайм понимает.
	"actor": {"position": {"offscreen_left", "far_left", "left", "center_left", "center",
		"center_right", "right", "far_right", "offscreen_right"},
		// Как фигура ВХОДИТ и УХОДИТ. Словарь знал только рантайм
		// (VnStage.ParseTransition), и незнакомое слово давало не ошибку, а
		// TransitionType.None — то есть появление БЕЗ перехода, молча. Автор,
		// выучивший `slide_up` на панелях `ui`, писал его актёру и получал
		// мгновенное возникновение без единого слова: наборы у панели и у
		// фигуры РАЗНЫЕ, и разницу теперь видно.
		"enter": ActorTransitions, "exit": ActorTransitions},
	"obj": {"enter": ActorTransitions, "exit": ActorTransitions},
	"ui": {"layer": {"hud", "over"}, "when": {"always", "idle", "say", "choice"},
		"appear": {"fade", "rise", "pop", "slide_up", "slide_down", "slide_left", "slide_right", "drop", "unfold"}},
}

// UiNodeKinds — из чего собирают дерево `ui`. Словарь знал только рантайм, и
// неизвестный вид не давал ошибки: LvnUiLayer падал в `default` и делал ПУСТУЮ
// ПАНЕЛЬ. Опечатка «buton» превращала кнопку в невидимый прямоугольник —
// экран собирался, кнопки на нём не было, и в логе ни строчки.
//
// `panel`, `row` и `column` живут в том же `default` намеренно (это и есть
// контейнер), поэтому их не отличить по коду — они перечислены здесь.
var UiNodeKinds = []string{
	"panel", "row", "column",
	"text", "button", "bar", "icon", "image", "scroll",
}

// AuraStyles — стили ауры, ВМЕСТЕ С СИНОНИМАМИ. Синонимы знал только рантайм,
// и валидатор ругался на законное слово: `aura_style=ice` получал «is not a
// known value» да ещё и совет «may be fire?» — прямо противоположную стихию.
// Ложная тревога дороже молчания: автор идёт править работающий код.
var AuraStyles = []string{
	"basic", "neutral", "plain",
	"guard", "ward", "protection",
	"fire",
	"frost", "ice",
	"storm", "thunder",
	"shadow", "dark",
	"holy", "light",
	"space", "void",
	"distortion", "rift",
	"spirit", "soul", "aether",
	"ascendant", "monarch", "overlord",
}

// ActorTransitions — как фигура входит и уходит. Пустое значение законно:
// поле может стоять без слова. `sink`, `burn` и `side` — принятые синонимы
// (`rise`, `dissolve`, `drift`), они есть в рантайме и потому есть здесь.
//
// Набор НЕ совпадает с набором панелей `ui appear=`: у фигуры есть `dissolve`
// и `drift`, у панели — `slide_up`/`slide_down`. Это разные механизмы, и
// сводить их значило бы дописывать эффекты; но молчать о разнице нельзя.
var ActorTransitions = []string{
	"", "fade", "slide_left", "slide_right", "pop",
	"rise", "sink", "drop", "unfold", "dissolve", "burn", "drift", "side",
}

// AnimPropHints — прицельные подсказки для описок, до которых не дотягивается
// сравнение по буквам. Слова не похожи, а перепутать их естественно.
var AnimPropHints = map[string]string{
	"opacity": `прозрачность у трека зовётся "alpha" (это "opacity" у actor и obj — имена разные, увы)`,
	"fill":    `заполнения полосы движок не анимирует; тяните "scalex" у самой полосы`,
	"width":   `ширину тянут через "scalex"`,
	"height":  `высоту тянут через "scaley"`,
	"angle":   `поворот зовётся "rotation"`,
	"rot":     `поворот зовётся "rotation"`,
	"pos_x":   `по экрану двигает "screen_x", внутри места — "x"`,
	"pos_y":   `по экрану двигает "screen_y", внутри места — "y"`,
}

// AnimProps — что вообще можно анимировать. Словарь ЯЗЫКА, и жил он только в
// рантайме (Lvn.UI.LvnAnimProp.Known): ни компилятор, ни валидатор, ни
// подсказки редактора его не знали. Незнакомое имя не давало ошибки — рантайм
// молча пропускал трек, и автор видел неподвижную полосу без единой подсказки
// почему. «opacity» вместо «alpha» — каноническая описка, названная так в
// докблоке самого рантайма.
var AnimProps = append(append([]string{}, AnimPropsWhole...), "frame")

// AnimPropsWhole — что можно тянуть у ФИГУРЫ ЦЕЛИКОМ (трек без слоя).
var AnimPropsWhole = []string{
	"x", "y", // смещение от места, в долях кадра
	"screen_x", "screen_y", // движение самого места по экрану
	"scale", "scalex", "scaley",
	"rotation",
	"alpha",
}

// AnimPropsLayered — что можно тянуть у ОДНОГО СЛОЯ куклы (трек со слоем).
//
// Экранного места у слоя нет: по экрану ходит фигура, а не её рукав. Зато у
// слоя есть КАДР. Рантайм это знал (LvnAnimProp.Whole/Layered), а проверка
// оставалась плоской — и `prop=frame` без слоя или `screen_x` со слоем
// проходили молча, чтобы потом молча же ничего не сыграть.
var AnimPropsLayered = []string{
	"x", "y",
	"scale", "scalex", "scaley",
	"rotation",
	"alpha",
	"frame", // подмена кадра слоя (кукла, спрайтовый лист)
}

// bodySafeOps are the ops that survive a choice option's body: pure state plus
// the jump. Everything else is replayed from the execution trace, which indexes
// the SCRIPT — and a body command is not in the script.
var bodySafeOps = map[string]bool{"set": true, "inc": true, "goto": true}

// Builtin labels are resolved by the runtime and need no definition.
var builtinLabels = map[string]bool{"__end": true}

// ExprFuncs is the CLOSED set of functions the expression evaluator implements
// (LvnExpression.CallFunc in C#, FUNCS in the web player). The language has no
// user-defined expression functions at runtime: `func f(a){ return … }` in .lvns
// is inlined by the compiler, so a call to anything outside this set can only
// evaluate to nothing. Keep in sync with docs/CHEATSHEET's function table.
var ExprFuncs = map[string]bool{
	"rand": true, "chance": true, "min": true, "max": true, "abs": true,
	"floor": true, "round": true,
	"len": true, "has": true, "get": true, "indexof": true, "count": true,
	"sum": true, "first": true, "last": true, "keys": true, "vals": true,
	"list": true, "push": true, "pop": true, "removeat": true, "remove": true,
	"slice": true, "concat": true, "put": true, "del": true,
	// Функции ХОЗЯИНА — см. HostExprFuncs ниже. Они здесь ради проверки
	// выражений (вызов существует), но реализует их не движок.
	"has_item": true, "balance": true, "worn": true, "abtest": true,
}

// HostExprFuncs — функции, которые даёт ХОЗЯИН, а не движок. Их значения
// приходят снаружи новеллы: из кошелька, гардероба, из деления игроков на
// группы. Компилятор их не вычисляет, а только признаёт существующими.
//
// Разделение не косметическое. Регистрирует их пакет shell (NovelApp), и в
// ЧИСТОМ com.lvn.engine — а это публичный путь установки из README — их нет:
// `has_item("зонт")` там бросит «unknown function». В плеере с сервисами они
// возвращают безопасный пустой ответ (нет вещи, ноль на счету, ничего не
// надето); `abtest("имя")` делит детерминированно, по хешу имени и id игрока,
// поэтому одна и та же ветка у одного и того же человека всегда.
//
// Проверяется сторожем: всё, что в ExprFuncs и НЕ встроено в LvnExpression,
// обязано быть перечислено здесь — иначе список встроенных тихо разойдётся с
// движком, как уже разошлись золотые эталоны.
var HostExprFuncs = map[string]bool{
	"has_item": true, "balance": true, "worn": true, "abtest": true,
}

var exprFuncNames = func() []string {
	out := make([]string, 0, len(ExprFuncs))
	for k := range ExprFuncs {
		out = append(out, k)
	}
	sort.Strings(out)
	return out
}()

// fallThroughTerminators are ops after which control never slides into the
// following command: they transfer the cursor elsewhere (goto/return/if) or
// pause for a deliberate branch (choice). Any OTHER op "falls through" into the
// next command — which, when the next command is a jump-target label, means the
// block is entered both by a jump AND by the cursor walking in from above. That
// is the classic button-screen footgun: a player taps the dialogue instead of a
// hotspot and slides into a section meant to be reached only by a click, running
// the chapter forward until it unexpectedly ends.
var fallThroughTerminators = map[string]bool{
	"goto": true, "return": true, "if": true, "choice": true,
}

// Severity grades a finding. Errors must gate a build; warnings are advisory.
type Severity int

const (
	SevWarning Severity = iota
	SevError
	// SevNote — сказанное для полноты картины, а не для исправления: сюда
	// уходит то, что проверка НАМЕРЕННО не считает находкой. Без него
	// подавление шума неотличимо от сломанной проверки, а превращать заметку в
	// предупреждение нельзя: `-strict` валит сборку на любом предупреждении, и
	// честная заметка стала бы стеной.
	SevNote
)

func (s Severity) String() string {
	switch s {
	case SevError:
		return "error"
	case SevNote:
		return "note"
	default:
		return "warning"
	}
}

// Issue is a single validation finding.
type Issue struct {
	Index int      // command index in script, or -1 for document-level
	Op    string   // op of the offending command, if any
	Msg   string   // human-readable description
	Sev   Severity // error (build-gating) or warning (advisory)
}

func (i Issue) String() string {
	if i.Index < 0 {
		return "doc: " + i.Msg
	}
	return fmt.Sprintf("script[%d] %s: %s", i.Index, i.Op, i.Msg)
}

// Validate runs the source-agnostic structural checks a build must pass. It
// classifies every finding as an error or a warning so the authoring tools can
// surface both:
//
// Errors (a build must not ship these):
//   - a command with no op, or an op outside KnownOps;
//   - a label with no id, or a duplicate label id;
//   - any jump target (goto/if/choice/call/on_click) that resolves to no label.
//
// Warnings (advisory — likely-unintended, the cause of "the game ends early"):
//   - a jump-target label that is ALSO reachable by fall-through (tap-advance
//     slides into a section meant to be reached only by a jump);
//   - a label defined but never targeted and not reachable by fall-through (dead);
//   - unbalanced braces in say/who text (interpolation will misrender);
//   - a choice option with neither a goto nor a body (silently falls through).
func Validate(d *Doc) []Issue { return ValidateExt(d, nil) }

// ValidateExt is Validate plus a project's ext-grammar: host ops declared
// there validate like built-ins (closed fields, enums, required), while an
// UNdeclared unknown op keeps the advisory warning. A nil grammar is exactly
// Validate.
func ValidateExt(d *Doc, ext *ExtGrammar) []Issue {
	var issues []Issue
	addErr := func(i int, op, msg string) { issues = append(issues, Issue{i, op, msg, SevError}) }
	addWarn := func(i int, op, msg string) { issues = append(issues, Issue{i, op, msg, SevWarning}) }
	addNote := func(msg string) { issues = append(issues, Issue{-1, "label", msg, SevNote}) }

	// Pass 0: required document-level blocks.
	if d.Scene == "" {
		addWarn(-1, "scene", "no `scene` header — add `scene <name>` at the top of the chapter")
	}
	if len(d.Script) == 0 {
		addWarn(-1, "", "the script is empty — there are no commands to play")
	}

	// Pass 1: collect defined labels (detect duplicates).
	defined := map[string]bool{}
	for i, c := range d.Script {
		if c.Op() != "label" {
			continue
		}
		id := c.Str("id")
		if id == "" {
			addErr(i, "label", "label has no id")
			continue
		}
		if defined[id] {
			addErr(i, "label", fmt.Sprintf("duplicate label %q", id))
		}
		defined[id] = true
	}

	// ДЛИННАЯ ГЛАВА БЕЗ ЕДИНОЙ МЕТКИ — ПОТЕРЯ ПРОГРЕССА ПРИ ПЕРВОЙ ЖЕ ПРАВКЕ.
	//
	// Игрок продолжает главу по ЯКОРЮ: сохранение помнит ближайшую метку и
	// сколько шагов от неё пройдено, и после правки текста плеер садится
	// рядом. Меток нет — якорю не за что зацепиться, и изменившаяся длина
	// возвращает читающих В НАЧАЛО главы. Замер 06.09 (тракт целиком: правка
	// .lvns → компиляция → вход из старого сейва): сохранение на восьмой
	// команде из десяти, автор вырезал половину — позиция 0, восемь
	// прочитанных реплик читаются заново.
	//
	// Автору это не видно ниоткуда: глава компилируется, играется и выглядит
	// здоровой. Метки ставит выбор, а кинетическая новелла обходится без
	// выборов вовсе — потому предупреждение и адресовано именно ей.
	//
	// Порог: короткую главу перечитать не жалко, и сыпать предупреждениями на
	// каждый экранный этюд незачем.
	if len(defined) == 0 && len(d.Script) >= 40 {
		addWarn(-1, "label", fmt.Sprintf(
			"в главе %d команд и ни одной метки: если вы её поправите, все, кто читает её сейчас, "+
				"вернутся в начало — поставьте `label` перед крупными сценами, и продолжение переживёт правку",
			len(d.Script)))
	}

	// Pass 2: walk commands — check ops, jump targets, text, choices.
	targeted := map[string]bool{}
	ref := func(i int, op, target string) {
		if target == "" {
			return
		}
		targeted[target] = true
		if !defined[target] && !builtinLabels[target] {
			addErr(i, op, fmt.Sprintf("jump to undefined label %q", target))
		}
	}

	var walk func(i int, c Cmd)
	walk = func(i int, c Cmd) {
		op := c.Op()
		if op == "" {
			addErr(i, "", "command has no op")
			return
		}
		if !KnownOps[op] {
			// Not an engine op. Declared in the project's ext-grammar → a real
			// host op: validate its fields like a built-in's. Undeclared →
			// either a typo or a host op nobody declared; the runtime ignores
			// it when unhandled, so it stays a warning, not an error.
			if ext != nil {
				if spec, ok := ext.Ops[op]; ok {
					checkExtOp(i, c, op, spec, addErr, addWarn, ref)
					return
				}
			}
			msg := fmt.Sprintf("unknown op %q — a typo, or host-defined (needs LvnOps.Register in the game; declare it in ext-grammar.json to validate its fields)", op)
			if s := nearest.Of(op, ext.OpNames(), 2); s != "" {
				msg = fmt.Sprintf("unknown op %q — did you mean the declared host op %q?", op, s)
			}
			addWarn(i, op, msg)
			return
		}
		// Unknown-key check: a typo'd key (e.g. `fade too=`) compiles clean and
		// then silently no-ops at runtime. Only ops with a closed field set are
		// checked (see OpFields).
		if fields, ok := OpFields[op]; ok {
			allowed := map[string]bool{"op": true}
			for _, f := range fields {
				allowed[f] = true
			}
			var bad []string
			for k := range c {
				if !allowed[k] {
					bad = append(bad, k)
				}
			}
			sort.Strings(bad)
			for _, k := range bad {
				msg := fmt.Sprintf("unknown field %q for op %q", k, op)
				if s := nearest.Of(k, fields, 2); s != "" {
					msg += fmt.Sprintf(" — did you mean %q?", s)
				} else if KnownOps[k] {
					// ТЁЗКА. Три слова языка работают и командой, и полем чужой
					// команды: `fade` (поле у bg/audio), `flash` и `tint` (поля
					// у sfx/fx). Автор, знающий их как поля, пишет их полем и
					// там, где поля нет — и наоборот. Расстояние тут не при чём,
					// слово написано ВЕРНО, ошибочно место; поэтому подсказка не
					// про опечатку, а про смысл.
					//
					// Цена такой путаницы уже известна: тёзка `hint` (команда
					// против поля опции) стоила двух руководств, годами учивших
					// не применять рабочую команду.
					msg += fmt.Sprintf(" — %q is a COMMAND of its own; write it on its own line "+
						"instead of as a field here", k)
				}
				addWarn(i, op, msg)
			}
		} else if fields, ok := OpenFieldHints[op]; ok {
			// ОТКРЫТЫЙ НАБОР — НЕ ПОВОД МОЛЧАТЬ О ЯВНОЙ ОПЕЧАТКЕ.
			//
			// У `actor` и `obj` поля закрыть нельзя: сверх грамматики там живут
			// ОСИ ГАРДЕРОБА (`outfit=`, `hair=` — в живом контенте их десятки
			// тысяч), и объявить их заранее движок не может, имена придумывает
			// автор. Поэтому опечатка в самой частой команде языка не ловилась
			// вовсе: `sprite_ulr=` и `postion=` компилировались молча и молча
			// же не действовали.
			//
			// Средний путь: незнакомое поле, ПОХОЖЕЕ на известное, — почти
			// наверняка описка, а не ось. Порог узкий намеренно: имя короче
			// четырёх букв не судим (у осей вроде `w`/`h` расстояние до `x`/`y`
			// тоже единица), и подсказку даём только при расстоянии ≤ 2.
			known := map[string]bool{"op": true}
			for _, f := range fields {
				known[f] = true
			}
			var suspect []string
			for k := range c {
				if known[k] || len([]rune(k)) < 4 {
					continue
				}
				if s := nearest.Of(k, fields, 2); s != "" {
					suspect = append(suspect, k+"\x00"+s)
				}
			}
			sort.Strings(suspect)
			for _, pair := range suspect {
				k, s, _ := strings.Cut(pair, "\x00")
				addWarn(i, op, fmt.Sprintf(
					"field %q on op %q looks like a typo of %q — the runtime ignores unknown fields silently (a wardrobe axis of your own naming is fine and needs no change)",
					k, op, s))
			}
		}
		// ДЛИТЕЛЬНОСТЬ ЗОВУТ ДВУМЯ ИМЕНАМИ, и это раскол языка, а не описка
		// автора: старые команды (fade/dim/flash/tint/blur/camera/hint) ждут
		// `duration`, новые (fx/sfx/portal/bg3d/cutscene) — `dur`. Выучив одно,
		// автор промахивается на другой команде.
		//
		// Обычные проверки тут не спасают: расстояние между словами — пять
		// правок, для подсказки «did you mean» слишком много, а у `fx` и `sfx`
		// набор полей вообще открытый, и `fx duration=2` проходил МОЛЧА —
		// эффект не применялся, сообщения не было.
		//
		// Поэтому пара названа явно: не догадка, а знание.
		if want, ok := durationSpelling[op]; ok {
			other := "dur"
			if want == "dur" {
				other = "duration"
			}
			if c[other] != nil && c[want] == nil {
				addWarn(i, op, fmt.Sprintf(
					"op %q spells its duration %q, not %q — the value is ignored as written "+
						"(the language is split: fade/dim/flash/tint/blur/camera/hint take `duration`, "+
						"fx/sfx/portal/bg3d/cutscene take `dur`)",
					op, want, other))
			}
		}

		// Enumerated-value check: a value outside a closed set (e.g.
		// `position="lft"`) is almost always a typo. Only present string fields
		// with a fully-closed value set are checked (see EnumValues).
		if enums, ok := EnumValues[op]; ok {
			for field, allowed := range enums {
				raw, present := c[field]
				if !present {
					continue
				}
				val, isStr := raw.(string)
				if !isStr {
					continue
				}
				if !inSet(allowed, val) {
					msg := fmt.Sprintf("%s=%q is not a known value (expected: %s)", field, val, strings.Join(nonEmpty(allowed), ", "))
					if s := nearest.Of(val, allowed, 2); s != "" {
						msg += fmt.Sprintf(" — did you mean %q?", s)
					}
					addWarn(i, op, msg)
				}
			}
		}
		// ВИД УЗЛА ДЕРЕВА `ui` — тоже закрытый словарь рантайма. Неизвестный
		// вид становится пустой панелью: экран собирается, кнопки на нём нет,
		// в логе ни строчки.
		if op == "ui" {
			var walk func(any, int)
			walk = func(n any, depth int) {
				node, ok := n.(map[string]any)
				if !ok {
					return
				}
				if depth > 16 {
					// Молчаливый предел — та же болезнь, что и молчаливый
					// default: ниже мы не смотрели, и опечатка вида там снова
					// станет невидимой. Скажем об этом хотя бы раз.
					addWarn(i, op, "дерево глубже 17 уровней — дальше виды узлов не проверены; "+
						"если это сгенерированное дерево, проверьте его отдельно")
					return
				}
				if kind, _ := node["kind"].(string); kind != "" && !inSet(UiNodeKinds, kind) {
					msg := fmt.Sprintf("узел kind=%q — такого вида нет, будет пустая панель (есть: %s)",
						kind, strings.Join(UiNodeKinds, ", "))
					if sg := nearest.Of(kind, UiNodeKinds, 2); sg != "" {
						msg += fmt.Sprintf(" — может быть %q?", sg)
					}
					addWarn(i, op, msg)
				}
				if kids, ok := node["children"].([]any); ok {
					for _, k := range kids {
						walk(k, depth+1)
					}
				}
			}
			walk(c["tree"], 0)
		}

		// СВОЙСТВО ТРЕКА — закрытый словарь, и знал его только рантайм.
		// Компилятор `prop=` не смотрел вовсе, валидатор тоже, а грамматика
		// его даже не подсказывала: автор писал естественное «opacity» вместо
		// «alpha», сборка молчала, а рантайм один раз писал строку в лог и
		// ПРОПУСКАЛ трек. Полоса не двигалась, и искать было нечего.
		if op == "anim" {
			if a, ok := c["anim"].(map[string]any); ok {
				if tracks, ok := a["tracks"].([]any); ok {
					for _, t := range tracks {
						tr, ok := t.(map[string]any)
						if !ok {
							continue
						}
						prop, _ := tr["prop"].(string)
						if prop == "" {
							continue
						}
						// Словарь КОНТЕКСТНЫЙ, как и исполнитель.
						layer, _ := tr["layer"].(string)
						here := AnimPropsWhole
						if layer != "" {
							here = AnimPropsLayered
						}
						if inSet(here, prop) {
							continue
						}
						// Знакомое имя не в своём месте — отдельный разговор:
						// «движок не знает» на слове, которое он отлично знает,
						// только сбивает.
						if inSet(AnimProps, prop) {
							where := "оно принадлежит СЛОЮ — добавьте layer="
							if layer != "" {
								where = fmt.Sprintf("у слоя %q экранного места нет — уберите layer= или тяните x/y", layer)
							}
							addWarn(i, op, fmt.Sprintf("prop=%q здесь не играет: %s. Здесь можно: %s",
								prop, where, strings.Join(here, ", ")))
							continue
						}
						msg := fmt.Sprintf("prop=%q — такого свойства движок не знает, трек будет пропущен (здесь можно: %s)",
							prop, strings.Join(here, ", "))
						// Прицельная подсказка там, где расстояние между словами
						// её не даёт. «opacity» — не случайная описка: у actor и
						// obj прозрачность ТАК И НАЗЫВАЕТСЯ (`opacity=0.4`), а у
						// трека она `alpha`. Описка следует из самого языка, и
						// перечень из десяти имён ей не помогает.
						if hint, ok := AnimPropHints[prop]; ok {
							msg += fmt.Sprintf(" — %s", hint)
						} else if sg := nearest.Of(prop, AnimProps, 2); sg != "" {
							msg += fmt.Sprintf(" — может быть %q?", sg)
						}
						addWarn(i, op, msg)
					}
				}
			}
		}
		switch op {
		case "goto", "call":
			ref(i, op, c.Str("label"))
		case "if":
			ref(i, op, c.Str("then"))
			ref(i, op, c.Str("else"))
		case "inc":
			// `by` is coerced as a NUMBER by both runtimes (a string falls back
			// to 1), so an expression here is silently wrong rather than
			// computed — exactly the shape that used to differ between the app
			// and the browser player. Say so instead of stepping by one.
			if by, ok := c["by"]; ok {
				switch by.(type) {
				case float64, int, bool, nil:
				default:
					addWarn(i, op, fmt.Sprintf("by=%v is not a number — it is not evaluated, the step falls back to 1; compute the value into a variable with `set` first", by))
				}
			}
		case "say":
			if msg := braceIssue(c.Str("text")); msg != "" {
				addWarn(i, op, msg)
			}
			if msg := braceIssue(c.Str("who")); msg != "" {
				addWarn(i, op, "speaker name: "+msg)
			}
			// ГОВОРЯЩИЙ, ПОХОЖИЙ НА ПРОЗУ, — это почти всегда проза, которую
			// разрезало двоеточие. В языке `Имя: текст` — реплика, и строка
			// «Это тестовая глава: проверяем показ» превращается в реплику
			// говорящего «Это тестовая глава». Тихо: на экране появляется
			// подпись, которой автор не писал.
			//
			// В живом контенте таких набралось шесть штук — «Комната-побег.
			// Кликай по предметам», «Код 451 подходит. В сейфе — записка».
			// Лечится кавычками: «…» вокруг всей строки (с 28.08 они защищают
			// двоеточие).
			if who := c.Str("who"); looksLikeProse(who) {
				addWarn(i, op, fmt.Sprintf(
					"speaker %q looks like prose cut by a colon — if this is narration, wrap the whole line in «…»; a real name goes before the colon",
					who))
			}
			// Narration shaped like a command is almost always a command with a
			// syntax slip that silently fell through to dialogue — the failure
			// mode authors lose hours to, because nothing errors and the typo
			// simply appears on screen. See commandLike for which shapes count.
			// ОШИБКА, а не предупреждение. Строка, которая по форме команда,
			// молча становится репликой и уезжает игроку — автор узнаёт об
			// этом из скриншота, а не из сборки. Предупреждение здесь не
			// работает: через API публикации его никто не читает (проверено —
			// глава с `bbg /content/…` публиковалась с ok:true). Форма
			// проверяется консервативно (см. commandLike: три формы, ни одной
			// на 373 строках нарратива и ни одной на всём живом контенте —
			// 91 глава плюс примеры), поэтому цена ложного срабатывания
			// близка к нулю, а цена пропуска — сцена с мусором в диалоге.
			if c.Str("who") == "" {
				// СЛУЖЕБНОЕ СЛОВО, НЕ СТАВШЕЕ КОНСТРУКЦИЕЙ. Условие пишется
				// либо однострочником (`if сила > 5 -> метка`), либо блоком
				// (`if сила > 5 {`); строка без того и другого не разбирается
				// вовсе и молча превращается в реплику. Замер 05.09:
				// `if сила > ` уехал игроку текстом «if сила >», и ни
				// компилятор, ни проверка слова не сказали.
				//
				// Форма узкая и потому безопасная: слово стоит ПЕРВЫМ, строчной
				// латиницей, а реплика — это нарратив без говорящего. Замерено
				// на живом корпусе: 102 файла репозитория (70 126 реплик) плюс
				// 144 главы живой студии (112 331 реплика) — НИ ОДНОГО
				// совпадения. Проза так не начинается: русская — никогда,
				// английская — с заглавной.
				if kw := danglingKeyword(c.Str("text")); kw != "" {
					addErr(i, op, fmt.Sprintf(
						"строка начинается со служебного слова %q, но конструкция не разобралась — "+
							"нужен переход (`-> метка`) или блок (`{`); сейчас она стала репликой и покажется игроку", kw))
					break
				}
				word := commandLike(c.Str("text"))
				if word != "" && KnownOps[word] {
					addErr(i, op, fmt.Sprintf("это команда %q, но её синтаксис не разобрался — строка стала репликой и покажется игроку", word))
				} else if raw, canon := mistypedOp(c.Str("text")); canon != "" {
					// Та же беда, что строкой выше, и та же цена — только слово
					// написано так, что распознаватель его не увидел.
					addErr(i, op, fmt.Sprintf(
						"это команда %q, написанная как %q (заглавные буквы или буква другого алфавита) — строка стала репликой и покажется игроку",
						canon, raw))
				} else if len(word) >= 3 {
					// A near-miss of a real op name (`actro id=…`, `bbg /x.jpg`).
					// Порог длины — от подсказки, а не от находки: слово короче
					// трёх букв отстоит на два шага от половины словаря, и совет
					// выходил наугад («s» — может быть, «bg»?).
					if s := nearest.Of(word, knownOpNames(), 2); s != "" {
						addErr(i, op, fmt.Sprintf("похоже на команду с опечаткой: %q — может быть, %q? (строка стала репликой и покажется игроку)", word, s))
					}
				}
			}
		case "obj", "actor", "ui":
			// A clickable hotspot jumps to a label, either directly
			// ("on_click": "label") or via an object ("on_click": {"goto": "label"}).
			// Кнопки внутри дерева `ui` — то же самое, только на глубине.
			switch v := c["on_click"].(type) {
			case string:
				ref(i, op, v)
			case map[string]any:
				ref(i, op, Cmd(v).Str("goto"))
			}
			// Кнопки внутри дерева `ui` — такие же переходы, только на
			// глубине. Опечатка в метке кнопки иначе прошла бы молча, а
			// нажатие в игре не привело бы никуда.
			if tree, ok := c["tree"].(map[string]any); ok {
				for _, t := range uiClickTargets(tree) {
					ref(i, op, t)
				}
			}
			// Drag & drop branches jump too: on_drop is "target:label" pairs
			// (space/comma separated — the runtime's ParseDropMap syntax),
			// on_drop_miss a plain label. Typos here used to slip through and
			// the labels they reach read as dead.
			if raw := c.Str("on_drop"); raw != "" {
				for _, pair := range strings.FieldsFunc(raw, func(r rune) bool { return r == ' ' || r == ',' }) {
					if k := strings.Index(pair, ":"); k > 0 && k < len(pair)-1 {
						ref(i, op, pair[k+1:])
					} else {
						addWarn(i, op, fmt.Sprintf("on_drop pair %q is not target:label", pair))
					}
				}
			}
			ref(i, op, c.Str("on_drop_miss"))
		case "input":
			// Text input writes the player's string into a variable — without
			// the variable the whole stop is pointless.
			if c.Str("var") == "" {
				addErr(i, op, "input needs var= (the variable that receives the text)")
			}
		case "choice":
			opts, _ := c["options"].([]any)
			if len(opts) == 0 {
				addWarn(i, op, "choice has no options")
			}
			// A timed choice: timeout seconds + the branch taken on expiry.
			// Either half alone is an authoring mistake.
			if tg := c.Str("timeout_goto"); tg != "" {
				ref(i, op, tg)
				if c["timeout"] == nil {
					addWarn(i, op, "timeout_goto without timeout= — the timer never starts")
				}
			} else if c["timeout"] != nil {
				addWarn(i, op, "timeout without timeout_goto — nowhere to go when time runs out")
			}
			// ВЫБОР, У КОТОРОГО ВСЕ ВАРИАНТЫ УСЛОВНЫЕ. Порог стата и выражение
			// прячут вариант целиком — не «серым», а совсем, — и если условны
			// ВСЕ, найдётся набор статов, при котором игроку не покажут ни
			// одной кнопки. Рантайм с 05.09 в такой выбор не встаёт (идёт
			// дальше и пишет в журнал), но для автора это всё равно дыра в
			// истории: развилка, которая молча исчезает. Единственный
			// безусловный вариант — «Уйти», «Промолчать» — закрывает её.
			gated := 0
			for _, o := range opts {
				om, ok := o.(map[string]any)
				if !ok {
					continue
				}
				oc := Cmd(om)
				if oc.Str("requires_stat") != "" || oc.Str("expr") != "" {
					gated++
				}
			}
			if len(opts) > 0 && gated == len(opts) {
				addWarn(i, op, fmt.Sprintf(
					"все %d варианта(ов) закрыты условием — при неподходящих статах игрок не увидит ни одной кнопки; "+
						"добавьте безусловный вариант («Уйти», «Промолчать»)", len(opts)))
			}
			for oi, o := range opts {
				om, ok := o.(map[string]any)
				if !ok {
					continue
				}
				oc := Cmd(om)
				_, hasBody := oc["body"].([]any)
				if oc.Str("goto") == "" && !hasBody {
					addWarn(i, op, fmt.Sprintf("option %d has no goto and no body (falls through)", oi))
				}
				ref(i, "choice", oc.Str("goto"))
				if body, ok := oc["body"].([]any); ok {
					for _, b := range body {
						bm, ok := b.(map[string]any)
						if !ok {
							continue
						}
						bc := Cmd(bm)
						// A body command carries no script index, and the
						// resume trace is a list of INDICES — so anything in a
						// body that touches the stage plays live and then
						// vanishes on save/restore: the scene rebuilds without
						// it. `set`/`inc`/`goto` survive because they are state,
						// not scenery. Both CAPABILITIES §8 and LANGUAGE §5
						// advertised staging here as supported; it never was.
						if bop := bc.Str("op"); bop != "" && !bodySafeOps[bop] {
							addWarn(i, op, fmt.Sprintf(
								"option %d body runs %q — a body command has no script index, so it is LOST on save/restore "+
									"(the scene rebuilds without it). Keep bodies to set/inc/goto and move staging to a label.",
								oi, bop))
						}
						walk(i, bc)
					}
				}
			}
		}
	}
	for i, c := range d.Script {
		walk(i, c)
	}

	// call/return sanity: a `return` in a script with no `call` at all pops an
	// empty stack at runtime (the player jumps to end-of-chapter) — always an
	// authoring slip, usually a subroutine label entered by goto instead of call.
	calls, returns, firstReturn := 0, 0, -1
	for i, c := range d.Script {
		switch c.Op() {
		case "call":
			calls++
		case "return":
			returns++
			if firstReturn < 0 {
				firstReturn = i
			}
		}
	}
	if returns > 0 && calls == 0 {
		addWarn(firstReturn, "return", "script has `return` but no `call` — an empty call stack returns to END OF CHAPTER; did you mean goto, or is a call missing?")
	}

	// Pass 3: fall-through into a jump-target label. A label entered BOTH by a
	// jump and by the cursor sliding in from a non-terminating command above is
	// the "chapter ends unexpectedly" footgun.
	for i, c := range d.Script {
		if i == 0 || c.Op() != "label" {
			continue
		}
		id := c.Str("id")
		if id == "" || !targeted[id] {
			continue // unreachable-by-jump labels are plain linear flow, not a trap
		}
		if !authoredLabel(id) {
			continue // метку писал не автор — см. authoredLabel
		}
		prev := d.Script[i-1].Op()
		if !fallThroughTerminators[prev] {
			addWarn(i, "label", fmt.Sprintf(
				"label %q is a jump target but also reached by fall-through from %q above — "+
					"add a `goto` (e.g. goto __end) before it if the block above should stop here",
				id, prev))
		}
	}

	// Pass 3b: ПЕТЛЯ БЕЗ ВЫХОДА — игрок застрянет в ней навсегда.
	//
	// `goto` назад сам по себе нормален: на нём держатся игровые циклы
	// (кликер, разбор комнаты). Ловушка — когда между меткой и возвратом нет
	// НИ ОДНОГО пути наружу: ни ветвления, ни выбора, ни возврата из вызова.
	// Тогда поток крутится вечно, и всё, что написано дальше, не сыграет
	// никогда.
	//
	// Находка не теоретическая: в главах Cold так замкнут гардероб — открыть,
	// поставить флаг, вернуться на метку, открыть снова. Прежняя диагностика
	// видела лишь СЛЕДСТВИЕ («дальше недостижимо»), да и то не всегда: если в
	// хвост главы вёл ещё один переход, петля проходила молча.
	{
		labelAt := map[string]int{}
		for i, c := range d.Script {
			if c.Op() == "label" {
				if id := c.Str("id"); id != "" {
					labelAt[id] = i
				}
			}
		}
		for i, c := range d.Script {
			if c.Op() != "goto" {
				continue
			}
			lo, ok := labelAt[c.Str("label")]
			if !ok || lo > i {
				continue // прыжок вперёд или в никуда — не петля
			}
			if !loopHasExit(d.Script, labelAt, lo, i) {
				addWarn(i, "goto", fmt.Sprintf(
					"loop back to %q has no way out: nothing between the label and this jump can leave it "+
						"(no if/choice/return, no jump outside) — the player is stuck here forever and everything "+
						"after never plays. %d command(s) in the loop.",
					c.Str("label"), i-lo+1))
			}
		}
	}

	// Pass 4: lint — labels defined but never targeted. Fall-through-reachable
	// labels are legitimate linear flow, so this is only a warning when the label
	// is also the first command or sits after a terminator (truly unreachable).
	var unused []string
	machineUnused := 0
	for id := range defined {
		if targeted[id] {
			continue
		}
		if !authoredLabel(id) {
			machineUnused++ // импортёр метит КАЖДУЮ ноду графа; это не находка
			continue
		}
		unused = append(unused, id)
	}
	if machineUnused > 0 {
		// Сказать вслух, что проверка отработала, а не промолчала: иначе
		// подавление неотличимо от поломки.
		addNote(fmt.Sprintf("%d generated label(s) are never targeted — normal for an imported chapter, not reported", machineUnused))
	}
	sort.Strings(unused)
	for _, id := range unused {
		addWarn(-1, "label", fmt.Sprintf("label %q is never targeted (dead, or fall-through only)", id))
	}

	// Pass 4b: calls to functions no evaluator has. The expression language has a
	// CLOSED set of built-ins (ExprFuncs) and no user-defined ones: a `func` in
	// .lvns is either inlined at compile time or lowered to call/return, so any
	// call left in an expression here resolves to nothing. The runtime degrades a
	// bad expression softly (the variable reads 0, the {span} prints verbatim), so
	// without this check the failure is invisible — which is exactly how `func`
	// stayed a phantom feature for so long.
	for i, c := range d.Script {
		var exprs []string
		switch c.Op() {
		case "if":
			exprs = append(exprs, c.Str("expr"))
		case "set":
			exprs = append(exprs, c.Str("expr"))
			// The expression language has no assignment operator, so a bare `=`
			// inside one means the line carried a SECOND statement that got
			// swallowed (`{ gold = gold - 5  potions = potions + 1 }` on one line)
			// or a comparison was mistyped. The evaluator throws, `set` swallows
			// the throw, and the variable silently keeps its old value.
			if at := strayAssign(c.Str("expr")); at >= 0 {
				addWarn(i, "set", fmt.Sprintf("expression %q contains a stray `=` — an expression cannot assign; put each statement on its own line, or use `==` to compare", c.Str("expr")))
			}
		case "say", "text":
			exprs = append(exprs, interpolationExprs(c.Str("text"))...)
		case "choice":
			if opts, ok := c["options"].([]any); ok {
				for _, o := range opts {
					if om, ok := o.(map[string]any); ok {
						exprs = append(exprs, Cmd(om).Str("expr"))
						exprs = append(exprs, interpolationExprs(Cmd(om).Str("text"))...)
					}
				}
			}
		}
		for _, e := range exprs {
			// СРАВНЕНИЕ СТРОК ПОРЯДКОМ НИЧЕГО НЕ СРАВНИВАЕТ. Операторы `<`, `>`,
			// `<=`, `>=` в этом языке ЧИСЛОВЫЕ — оба вычислителя (движок и
			// браузерный) приводят операнды к числу, и строка становится нулём.
			// `name < "М"` — не «до буквы М», а `0 < 0`, то есть всегда false, и
			// молча: ни ошибки, ни предупреждения не было ни от кого.
			//
			// Отказ тихий вдвойне, потому что выражение ВЫГЛЯДИТ рабочим и в
			// половине случаев даже даёт ожидаемый ответ (false там, где автор
			// его и ждал). Находят такое на живой новелле, через недели.
			if lit := orderComparesString(e); lit != "" {
				addWarn(i, c.Op(), fmt.Sprintf(
					"expression %q orders a string literal (%s) — `<` `>` `<=` `>=` compare NUMBERS here, "+
						"and a string becomes 0, so this is always the same answer; compare with == or keep a numeric field",
					e, lit))
			}
			for _, fn := range exprCalls(e) {
				if ExprFuncs[fn] {
					continue
				}
				if s := nearest.Of(fn, exprFuncNames, 2); s != "" {
					addWarn(i, c.Op(), fmt.Sprintf("unknown function %s() in expression — did you mean %s()?", fn, s))
					continue
				}
				addWarn(i, c.Op(), fmt.Sprintf("unknown function %s() in expression — expressions know only the built-ins; declare it as `func %s(…) { return … }` in .lvns, or fix the spelling", fn, fn))
			}
		}
	}

	// Pass 5: likely-typo variable reads. A variable read in an expression or a
	// {interpolation} that is never set — AND is a near-miss of a variable that IS
	// set — is almost always a typo (`if expr="scoore>=1"` when `score` is set).
	// We deliberately only flag close typos of defined vars, never every unknown
	// name: a novel legitimately reads vars seeded from an earlier chapter or the
	// host, so flagging all unknowns would be noise. Only runs when the doc sets
	// at least one var of its own.
	setVars := collectDefinedVars(d.Script)
	// Тело функции, которую в ЭТОМ документе никто не зовёт, — библиотека:
	// её параметры получат значения у вызывающей главы, и здесь их `set`
	// действительно нет. Ругаться на них значит утопить настоящие находки в
	// шуме (`battle(бой_враг, …)` — 37 предупреждений на ровном месте).
	uncalled := uncalledFuncBodies(d.Script)
	if len(setVars) > 0 {
		definedList := make([]string, 0, len(setVars))
		for k := range setVars {
			definedList = append(definedList, k)
		}
		sort.Strings(definedList)
		var checkExpr func(i int, op, expr string)
		checkExpr = func(i int, op, expr string) {
			if uncalled[i] {
				return
			}
			for _, id := range exprIdents(expr) {
				if setVars[id] {
					continue
				}
				if s := nearest.Of(id, definedList, 2); s != "" && len(s) >= 4 && s != id {
					addWarn(i, op, fmt.Sprintf("variable %q is read but never set — did you mean %q?", id, s))
				}
			}
		}
		for i, c := range d.Script {
			switch c.Op() {
			case "if":
				checkExpr(i, "if", c.Str("expr"))
			case "set":
				checkExpr(i, "set", c.Str("expr"))
			case "say", "text":
				for _, in := range interpolationExprs(c.Str("text")) {
					checkExpr(i, c.Op(), in)
				}
			case "choice":
				if opts, ok := c["options"].([]any); ok {
					for _, o := range opts {
						if om, ok := o.(map[string]any); ok {
							checkExpr(i, "choice", Cmd(om).Str("expr"))
						}
					}
				}
			}
		}
	}

	// ── Достижимость ────────────────────────────────────────────────────────
	// Ссылки целы, ops известны, а пути к куску скрипта всё равно нет: это
	// класс, который проверка «по одной команде» пропускает по построению, и
	// именно им терялись механики целиком — аура, не срабатывавшая ни разу,
	// отправка результата за безусловным переходом, функция эффектов, которую
	// никто не звал. Обход играет все ветки и приносит ровно недостижимое.
	//
	// Предупреждение, а не ошибка: писать главу часто начинают с куска, к
	// которому ещё не привели переход, и запрещать сохранять черновик — значит
	// заставить автора обходить проверку. Гейт публикации ужесточает это сам
	// (см. publishStrict на сервере).
	for _, blk := range Reach(d, DefaultReachDepth).Blocks {
		what := blk.Sample
		if len(blk.Labels) > 0 {
			what += " [метки: " + strings.Join(blk.Labels, ", ") + "]"
		}
		addWarn(blk.Start, "reach", fmt.Sprintf(
			"недостижимо: до этого места не ведёт ни один путь (%d команд(ы), по #%d) — %s",
			blk.Len, blk.End, what))
	}

	return issues
}

// uncalledFuncBodies отмечает индексы команд, лежащих в теле функции, которую
// в этом документе никто не вызывает. Компилятор кладёт функцию между метками
// `__fn_<имя>` и `__fnskip_<имя>`, а вызов — это `call __fn_<имя>`.
func uncalledFuncBodies(script []Cmd) map[int]bool {
	called := map[string]bool{}
	for _, c := range script {
		if c.Op() == "call" {
			called[c.Str("label")] = true
		}
	}
	out := map[int]bool{}
	for i, c := range script {
		if c.Op() != "label" {
			continue
		}
		id := c.Str("id")
		if !strings.HasPrefix(id, "__fn_") || called[id] {
			continue
		}
		end := "__fnskip_" + strings.TrimPrefix(id, "__fn_")
		for j := i + 1; j < len(script); j++ {
			if script[j].Op() == "label" && script[j].Str("id") == end {
				break
			}
			out[j] = true
		}
	}
	return out
}

// collectDefinedVars gathers every variable the document assigns: set/inc keys,
// on_click set-maps, and set/inc inside choice option bodies.
func collectDefinedVars(script []Cmd) map[string]bool {
	defined := map[string]bool{}
	var visit func(cmds []Cmd)
	visit = func(cmds []Cmd) {
		for _, c := range cmds {
			switch c.Op() {
			case "set", "inc":
				if k := c.Str("key"); k != "" {
					defined[k] = true
				}
			}
			if oc, ok := c["on_click"].(map[string]any); ok {
				if setm, ok := oc["set"].(map[string]any); ok {
					for k := range setm {
						defined[k] = true
					}
				}
			}
			if c.Op() == "choice" {
				if opts, ok := c["options"].([]any); ok {
					for _, o := range opts {
						om, ok := o.(map[string]any)
						if !ok {
							continue
						}
						if body, ok := om["body"].([]any); ok {
							var bcmds []Cmd
							for _, b := range body {
								if bm, ok := b.(map[string]any); ok {
									bcmds = append(bcmds, Cmd(bm))
								}
							}
							visit(bcmds)
						}
					}
				}
			}
		}
	}
	visit(script)
	return defined
}

var (
	// \p{L}, не [A-Za-z]: имена в наших новеллах кириллические, и латинский
	// класс делал ВСЮ проверку опечаток слепой к ним. Поймано переводом дуэли
	// на английский: семь подписей в дереве умений читали переменную, которую
	// никто не задаёт, и валидатор молчал про это полгода.
	identRe  = regexp.MustCompile(`[\p{L}_][\p{L}\p{N}_.]*`)
	strLitRe = regexp.MustCompile(`"[^"]*"|'[^']*'`)
	// keywords/operators an expression may contain that are not variables.
	exprKeywords = map[string]bool{
		"true": true, "false": true, "null": true, "nil": true,
		"and": true, "or": true, "not": true, "mod": true,
	}
)

// eachIdent — обойти ИМЕНА в выражении, сообщая для каждого, вызов это или
// переменная.
//
// Различие между «переменной» и «вызовом» — одно: стоит ли за именем открытая
// скобка, с поправкой на пробелы между ними. Прежде это решали два тела,
// одинаковых на четырнадцать строк из шестнадцати, и обе повторяли ещё и
// затирание строковых литералов: имя внутри кавычек — часть текста, а не
// переменная.
//
// Разойдись затирание — и одна половина проверок начнёт видеть переменные там,
// где их нет: автор получит предупреждение о необъявленном имени, которого он
// не писал, и пойдёт искать его в скрипте.
func eachIdent(expr string, visit func(name string, isCall bool)) {
	if expr == "" {
		return
	}
	expr = strLitRe.ReplaceAllString(expr, " ") // литерал в кавычках — не переменная
	for _, m := range identRe.FindAllStringIndex(expr, -1) {
		j := m[1]
		for j < len(expr) && (expr[j] == ' ' || expr[j] == '\t') {
			j++
		}
		visit(expr[m[0]:m[1]], j < len(expr) && expr[j] == '(')
	}
}

// exprIdents — переменные выражения: имена, за которыми НЕ стоит скобка.
func exprIdents(expr string) []string {
	var out []string
	eachIdent(expr, func(name string, isCall bool) {
		if isCall || exprKeywords[strings.ToLower(name)] {
			return
		}
		out = append(out, name)
	})
	return out
}

// orderComparesString: рядом с `<`/`>`/`<=`/`>=` стоит строковый литерал.
// Возвращает сам литерал (для сообщения) или пустую строку.
//
// Ищем ровно литерал, а не переменную: тип переменной статически неизвестен, и
// предупреждать на каждое `hp < max` значило бы утопить настоящие находки в
// шуме — та же причина, по которой здесь не проверяют вызовы функций подряд.
var orderVsLiteral = regexp.MustCompile(`(?:<=|>=|<|>)\s*("[^"]*")|("[^"]*")\s*(?:<=|>=|<|>)`)

func orderComparesString(e string) string {
	m := orderVsLiteral.FindStringSubmatch(e)
	if m == nil {
		return ""
	}
	if m[1] != "" {
		return m[1]
	}
	return m[2]
}

// strayAssign returns the index of a top-level `=` that is not part of a
// comparison operator (`==`, `!=`, `>=`, `<=`), or -1. Quoted literals, brackets
// and «…» are skipped, so `get(m, "a=b", 0)` is clean.
func strayAssign(expr string) int {
	var inStr rune
	chev, depth := 0, 0
	rs := []rune(expr)
	for i := 0; i < len(rs); i++ {
		c := rs[i]
		if inStr != 0 {
			if c == inStr {
				inStr = 0
			}
			continue
		}
		switch {
		case c == '«':
			chev++
		case c == '»':
			if chev > 0 {
				chev--
			}
		case chev > 0:
		case c == '"' || c == '\'':
			inStr = c
		case c == '(' || c == '[' || c == '{':
			depth++
		case c == ')' || c == ']' || c == '}':
			depth--
		case c == '=' && depth == 0:
			if i+1 < len(rs) && rs[i+1] == '=' {
				i++ // `==`
				continue
			}
			if i > 0 && strings.ContainsRune("=!<>", rs[i-1]) {
				continue // the tail of `!=` / `>=` / `<=`
			}
			return i
		}
	}
	return -1
}

// exprCalls — вызовы выражения: имена, за которыми стоит скобка (зеркало
// exprIdents, который их отбрасывает).
//
// String literals are blanked first (eachIdent) so a name inside a quoted string
// is never taken for a call. A span carrying `|` is an Ink-style text alternative
// ({a|b|c}, {cond: yes|no}) whose branches are prose, not expressions, so it
// yields nothing.
func exprCalls(expr string) []string {
	// Ранний выход не украшение: без скобки вызовов нет вовсе, а вертикальная
	// черта — чужой синтаксис, разбирать который здесь не наше дело.
	if !strings.Contains(expr, "(") || strings.Contains(expr, "|") {
		return nil
	}
	var out []string
	eachIdent(expr, func(name string, isCall bool) {
		if isCall {
			out = append(out, name)
		}
	})
	return out
}

// interpolationExprs returns the contents of each {…} span in text ({{ and }} are
// literal-brace escapes), for variable-read checking.
func interpolationExprs(s string) []string {
	var out []string
	for i := 0; i < len(s); i++ {
		if s[i] != '{' {
			continue
		}
		if i+1 < len(s) && s[i+1] == '{' { // literal "{{"
			i++
			continue
		}
		end := strings.IndexByte(s[i+1:], '}')
		if end < 0 {
			break
		}
		out = append(out, s[i+1:i+1+end])
		i += end + 1
	}
	return out
}

func inSet(set []string, v string) bool {
	for _, s := range set {
		if s == v {
			return true
		}
	}
	return false
}

// nonEmpty drops the "" sentinel (used to mean "absent is fine") from an enum
// set so it doesn't show up in the human-readable "expected:" hint.
func nonEmpty(set []string) []string {
	out := make([]string, 0, len(set))
	for _, s := range set {
		if s != "" {
			out = append(out, s)
		}
	}
	return out
}

// knownOpNames: KnownOps' keys, for typo suggestions.
func knownOpNames() []string {
	names := make([]string, 0, len(KnownOps))
	for op := range KnownOps {
		names = append(names, op)
	}
	return names
}

// braceIssue reports an unbalanced-brace problem in interpolated text, or "" if
// the braces are balanced. `{{` and `}}` are literal-brace escapes.
func braceIssue(s string) string {
	depth := 0
	for i := 0; i < len(s); i++ {
		switch s[i] {
		case '{':
			if i+1 < len(s) && s[i+1] == '{' {
				i++
				continue
			}
			depth++
		case '}':
			if i+1 < len(s) && s[i+1] == '}' {
				i++
				continue
			}
			depth--
			if depth < 0 {
				return "unbalanced '}' in text"
			}
		}
	}
	if depth > 0 {
		return "unbalanced '{' in text (missing '}')"
	}
	return ""
}

// looksLikeProse: «говорящий» с точкой, восклицанием или вопросом внутри, либо
// длиной от пяти слов. Имена людей так не выглядят, а разрезанная двоеточием
// фраза — выглядит именно так.
//
// Безымянные подписи из одних знаков («...», «???») пропускаем: это намеренный
// приём, а не описка.
func looksLikeProse(who string) bool {
	if who == "" {
		return false
	}
	hasLetter := false
	for _, r := range who {
		if unicode.IsLetter(r) {
			hasLetter = true
			break
		}
	}
	if !hasLetter {
		return false
	}
	if strings.ContainsAny(who, ".!?") {
		return true
	}
	return len(strings.Fields(who)) >= 5
}

// authoredLabel: метку написал АВТОР, а не машина.
//
// Диагностика имеет смысл, только если говорит о том, что автор писал. В
// импортированной главе метку получает КАЖДАЯ нода графа articy — их формы
// `n17_000000`, и в базе партнёрской новеллы таких 5329 против 38 авторских. Две
// проверки («провал в метку» и «метка никем не нужна») ругались на них
// 4343 раза, и полезный сигнал тонул в шуме сто к одному: пять настоящих
// находок на четыре с половиной тысячи строк.
//
// Машинные метки бывают двух видов: `n<номер>_<шесть цифр>` от импортёра и
// `__…` от самого компилятора (циклы, ветвления, точки возврата).
func authoredLabel(id string) bool {
	if id == "" || strings.HasPrefix(id, "__") {
		return false
	}
	return !importedLabel.MatchString(id)
}

var importedLabel = regexp.MustCompile(`^n\d+_\d{6}$`)

// loopHasExit: есть ли из отрезка [lo, hi] хоть один путь наружу.
//
// Наружу ведут: `return` (выход из вызова), а также любой переход — goto, call,
// обе ветви `if`, вариант выбора — чья цель лежит вне отрезка либо неизвестна
// (на неизвестную метку валидатор ругается отдельно, но считать её ловушкой
// нельзя).
func loopHasExit(script []Cmd, labelAt map[string]int, lo, hi int) bool {
	outside := func(target string) bool {
		if target == "" {
			return false
		}
		p, ok := labelAt[target]
		return !ok || p < lo || p > hi
	}
	// ВЫХОД БЫВАЕТ И НЕ В СКРИПТЕ. Новелла с собственным интерфейсом (`ui`)
	// держит поток на метке ожидания — `label idle / wait / goto idle`, — а
	// уводит его КНОПКА дерева: `on_click` с именем метки. Для скрипта это
	// вечная петля, для игрока — экран, на котором он выбирает, куда пойти.
	//
	// Без этого знания предупреждение срабатывало на каждой такой новелле
	// (55 раз на витринах движка) — и настоящие ловушки тонули в шуме, ради
	// которого проверка и заводилась.
	//
	// Считается дерево, поставленное ДО конца петли: позже — уже не поможет.
	for j := 0; j <= hi; j++ {
		// Предмет сцены с откликом — тот же интерфейс, только нарисованный
		// в кадре: комод, картина, дверь. Игра «кликай по предметам» вся
		// стоит на метке-круге, из которого уводит ИМЕННО клик.
		// …и перетаскивание туда же: «брось яблоко в сумку» уводит с метки
		// ожидания ровно так же, как клик, только жестом (`on_drop`).
		if script[j].Op() == "obj" &&
			(script[j].Str("on_click") != "" || script[j].Str("on_drop") != "") {
			return true
		}
		if script[j].Op() != "ui" {
			continue
		}
		tree, _ := script[j]["tree"].(map[string]any)
		if tree == nil {
			continue
		}
		// ЛЮБАЯ кнопка, а не только уводящая наружу. Смысл проверки — «игрок
		// ничего не может сделать»; экран с кнопками он как раз может, и
		// поток на метке ожидания ждёт ЕГО — ровно как ждёт выбор. Куда
		// кнопка ведёт, вопрос сценария, а не ловушки: интерфейсная новелла
		// целиком живёт внутри одного такого круга.
		if len(uiClickTargets(tree)) > 0 {
			return true
		}
	}
	for j := lo; j <= hi; j++ {
		c := script[j]
		switch c.Op() {
		case "return":
			return true
		case "goto", "call":
			if outside(c.Str("label")) {
				return true
			}
		case "if":
			if outside(c.Str("then")) || outside(c.Str("else")) {
				return true
			}
		case "choice":
			opts, _ := c["options"].([]any)
			for _, o := range opts {
				om, _ := o.(map[string]any)
				oc := Cmd(om)
				// Вариант уводит своим `goto`, а если его нет — переходом
				// внутри тела (та же логика, что у обхода достижимости).
				target := oc.Str("goto")
				if target == "" {
					target = bodyGoto(oc)
				}
				if outside(target) {
					return true
				}
				if target == "" {
					return true // вариант проваливается дальше — из петли есть ход
				}
			}
		}
	}
	return false
}

// durationSpelling — как ИМЕННО эта команда зовёт длительность. Раскол
// исторический: у старых команд `duration`, у добавленных позже `dur`.
// Перечислено явно, потому что подсказка по расстоянию тут бессильна — между
// словами пять правок.
var durationSpelling = map[string]string{
	"fade": "duration", "dim": "duration", "flash": "duration", "tint": "duration",
	"blur": "duration", "camera": "duration", "hint": "duration",
	"fx": "dur", "sfx": "dur", "portal": "dur", "bg3d": "dur", "cutscene": "dur",
}
