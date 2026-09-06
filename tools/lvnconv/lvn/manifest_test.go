package lvn

// ЧТО ЗДЕСЬ ЗАКРЕПЛЕНО.
//
// У манифеста до 31.08 гейта не было вовсе: скрипт проходил структурную
// проверку сервера, а manifest.json писали на диск после разбора JSON — и всё.
// А манифест — это весь облик приложения. Описка в имени поля не давала ни
// ошибки, ни строчки в логе: Newtonsoft молча пропускает незнакомое, экран
// брал умолчание, и автор искал причину глазами.
//
// Гейт появился, и теперь у него две обязанности, равные по важности:
// НАЗЫВАТЬ описку — и МОЛЧАТЬ на всём остальном. Гейт, который ругается на
// рабочий контент, хуже отсутствия гейта: его перестают читать. Поэтому
// половина тестов ниже проверяет не находки, а тишину.

import (
	"encoding/json"
	"os"
	"path/filepath"
	"reflect"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// manifestIssues прогоняет проверку по литералу манифеста.
func manifestIssues(t *testing.T, doc string) []Issue {
	t.Helper()
	return ValidateManifest([]byte(doc))
}

// mustBeQuiet требует ПОЛНОЙ тишины: ни одной находки. Это главная форма
// утверждения в этом файле — молчание проверяемо, а «не больше двух
// предупреждений» нет.
func mustBeQuiet(t *testing.T, doc string) {
	t.Helper()
	if got := manifestIssues(t, doc); len(got) != 0 {
		t.Fatalf("ожидалась тишина на %s, а гейт сказал: %v", doc, got)
	}
}

// ЗАБЫТЬ КЛАСС НА ВРЕМЯ ОДНОГО УТВЕРЖДЕНИЯ.
//
// Два правила гейта — «про неизвестный класс молчим» и «закрытое слово судится
// только на своём пути» — показывались на `assets`: его класс лежал в
// исходнике, который снимок не читал. 02.09 исходник добавили, и оба теста
// упали, хотя правила не изменились ни на букву.
//
// Правило, проверяемое ПОБОЧНЫМ ФАКТОМ («вот этого класса у нас случайно
// нет»), привязано к составу снимка и ломается от каждого его пополнения.
// Поэтому класс убираем сами — и возвращаем, чем бы утверждение ни кончилось.
func withoutClass(t *testing.T, name string) {
	t.Helper()
	was, had := manifestSchema[name]
	delete(manifestSchema, name)
	t.Cleanup(func() {
		if had {
			manifestSchema[name] = was
		}
	})
}

// ── ИМЕНА ПОЛЕЙ ──────────────────────────────────────────────────────────────

func TestОпечаткаВИмениПоляНазываетсяИПодсказывается(t *testing.T) {
	issues := manifestIssues(t, `{"ui":{"hud":{"bg_colour":"#101010"}}}`)
	if !hasWarn(issues, "ui.hud.bg_colour — такого поля нет") {
		t.Fatalf("описка в имени поля прошла молча: %v", issues)
	}
	// Подсказка — половина ценности: «нет такого поля» без «может быть
	// bg_color» заставляет автора листать DTO.
	if !hasWarn(issues, `может быть "bg_color"`) {
		t.Fatalf("нет подсказки на близкое имя: %v", issues)
	}
}

func TestЗаконноеИмяПоляНеТревожит(t *testing.T) {
	mustBeQuiet(t, `{"ui":{"hud":{"bg_color":"#101010","height":0.08,"show_progress":true}}}`)
}

// КАТАЛОГ ПРОВЕРЯЕТСЯ ТЕМ ЖЕ СНИМКОМ, ЧТО И ОБЛИК. Схема снимается с
// нескольких исходников (см. ManifestSchemaSources), и спуск идёт
// по ТИПАМ полей от корня LvnManifest, а не по одному поддереву `ui`. Для
// игрока это один файл, и описка в имени новеллы стоит ровно столько же.
func TestОпечаткаВКаталогеЛовитсяТакЖеКакВОблике(t *testing.T) {
	issues := manifestIssues(t, `{"titles":[{"id":"t","nmae":"Полночь"}]}`)
	if !hasWarn(issues, "titles[0].nmae — такого поля нет") {
		t.Fatalf("описка в каталоге прошла молча: %v", issues)
	}
	if !hasWarn(issues, `может быть "name"`) {
		t.Fatalf("нет подсказки на близкое имя: %v", issues)
	}
}

// ПРО ЧТО СНИМКА НЕТ — МОЛЧИМ, А НЕ ВРЁМ. Класс неизвестен — значит, имена
// внутри не проверяются вовсе: объявить чужое поле несуществующим хуже, чем
// промолчать.
//
// Пример здесь ПОДДЕЛАННЫЙ нарочно. Раньше правило показывали на `assets`:
// его класс лежал в исходнике, который снимок не читал. 02.09 исходник
// добавили — и тест упал, хотя правило не изменилось ни на букву. Проверять
// правило побочным фактом («вот этого класса у нас случайно нет») значит
// привязать его к составу снимка; убираем класс сами и спрашиваем гейт.
func TestПроНеизвестныйКлассИменаНеПроверяются(t *testing.T) {
	withoutClass(t, "LvnAssetMeta")
	mustBeQuiet(t, `{"assets":{"ui/logo.png":{"чего-то-эдакое":1,"bytes":10}}}`)
}

// КЛЮЧИ АВТОРСКОГО СЛОВАРЯ — НЕ ПОЛЯ. `sprites` — это Dictionary<string, …>,
// его ключи суть ИМЕНА ГЕРОЕВ. Судить их по схеме значит объявить
// несуществующими всех персонажей игры; проверять надо значения.
func TestКлючиАвторскогоСловаряНеСчитаютсяПолями(t *testing.T) {
	mustBeQuiet(t, `{"sprites":{"mira":{"name":"Мира"},"дорн":{"name":"Дорн"}}}`)
}

func TestВнутриАвторскогоСловаряЗначенияВсёЖеПроверяются(t *testing.T) {
	issues := manifestIssues(t, `{"sprites":{"mira":{"nmae":"Мира"}}}`)
	if !hasWarn(issues, "sprites.mira.nmae — такого поля нет") {
		t.Fatalf("значение словаря должно проверяться по схеме: %v", issues)
	}
}

// Заметка автора в JSON. Комментариев в языке нет, и их пишут ключом на `$`
// (та же конвенция, что в grammar.json). Поле это или заметка — видно по
// первому символу, и ругаться на неё гейт не вправе.
func TestЗаметкаНаДолларНеПоле(t *testing.T) {
	mustBeQuiet(t, `{"$note":"половина примера","ui":{"$why":"так надо","hud":{"height":0.08}}}`)
}

// ── ЦВЕТА ────────────────────────────────────────────────────────────────────

func TestЦветСловомИШестнадцатеричныйМолчат(t *testing.T) {
	mustBeQuiet(t, `{"ui":{"hud":{"bg_color":"accent"}}}`)
	mustBeQuiet(t, `{"ui":{"hud":{"bg_color":"#ff00aa"}}}`)
	mustBeQuiet(t, `{"ui":{"hud":{"bg_color":"#FF00AA80"}}}`)
	// Регистр и пробелы вокруг — не повод для находки.
	mustBeQuiet(t, `{"ui":{"hud":{"pill_text_color":" ACCENT "}}}`)
}

// Правило цвета читает не только хвост `_color`, но и поле, которое ЗОВЁТСЯ
// «color» целиком — так подписан цвет имени героя в каталоге спрайтов.
func TestПолеНазванноеПростоЦветомСудитсяТакЖе(t *testing.T) {
	mustBeQuiet(t, `{"sprites":{"mira":{"color":"#ffcc00"}}}`)
	if !hasWarn(manifestIssues(t, `{"sprites":{"mira":{"color":"мутный"}}}`), "не цвет") {
		t.Fatal("мусор в цвете имени героя прошёл молча")
	}
}

func TestМусорВЦветеЛовитсяСПодсказкой(t *testing.T) {
	issues := manifestIssues(t, `{"ui":{"hud":{"bg_color":"accnt"}}}`)
	if !hasWarn(issues, "не цвет") {
		t.Fatalf("мусор в цвете прошёл молча: %v", issues)
	}
	if !hasWarn(issues, `может быть "accent"`) {
		t.Fatalf("нет подсказки на близкое слово цвета: %v", issues)
	}
}

// НЕЗАКРЫТАЯ ПОДСТАНОВКА — НЕ ОПЕЧАТКА. `{theme.bg}` ещё не подставили; это
// шаблон, а не значение, и гейт обязан пропустить его молча.
func TestНеподставленнаяПодстановкаНеСчитаетсяОшибкой(t *testing.T) {
	mustBeQuiet(t, `{"ui":{"hud":{"bg_color":"{theme.bg}"}}}`)
}

// ── ЗАКРЫТЫЕ СЛОВА ───────────────────────────────────────────────────────────

func TestЗакрытоеСловоПоИмениПоля(t *testing.T) {
	issues := manifestIssues(t, `{"ui":{"browse":{"theme":"midnigt"}}}`)
	if !hasWarn(issues, `ui.browse.theme="midnigt" — такого значения нет`) {
		t.Fatalf("неизвестная тема прошла молча: %v", issues)
	}
	if !hasWarn(issues, `может быть "midnight"`) {
		t.Fatalf("нет подсказки на близкую тему: %v", issues)
	}
	mustBeQuiet(t, `{"ui":{"browse":{"theme":"cyber"}}}`)
}

// «mode» СЛИШКОМ ОБЩЕЕ ИМЯ, чтобы судить о нём по одному слову: оно бывает и у
// анимации. Поэтому закрытый список привязан к ПОЛНОМУ ПУТИ.
func TestЗакрытоеСловоПоПолномуПути(t *testing.T) {
	issues := manifestIssues(t, `{"ui":{"hud":{"mode":"иногда"}}}`)
	if !hasWarn(issues, `ui.hud.mode="иногда" — такого значения нет`) {
		t.Fatalf("неизвестный режим HUD прошёл молча: %v", issues)
	}
	if !hasWarn(issues, "известны: always, full, choices") {
		t.Fatalf("находка не перечисляет известные значения: %v", issues)
	}
	mustBeQuiet(t, `{"ui":{"hud":{"mode":"choices"}}}`)
}

// И обратное: то же слово «mode» ВНЕ этого пути не судится вовсе — иначе
// авторский `mode=queue` у анимации стал бы находкой на ровном месте.
func TestТоЖеИмяВнеПутиНеСудится(t *testing.T) {
	withoutClass(t, "LvnAssetMeta")
	mustBeQuiet(t, `{"assets":{"anim/idle.json":{"mode":"queue"}}}`)
}

func TestЗакрытыеСловаПоявленияИВспышки(t *testing.T) {
	mustBeQuiet(t, `{"ui":{"dialogue":{"appear":"rise"}}}`)
	if !hasWarn(manifestIssues(t, `{"ui":{"dialogue":{"appear":"взлёт"}}}`), "такого значения нет") {
		t.Fatal("неизвестное появление диалогового окна прошло молча")
	}
	mustBeQuiet(t, `{"ui":{"stage":{"tap_burst":"hearts","speaker_focus":"solo"}}}`)
	if !hasWarn(manifestIssues(t, `{"ui":{"stage":{"speaker_focus":"тускло"}}}`), "такого значения нет") {
		t.Fatal("неизвестная подсветка говорящего прошла молча")
	}
}

// ПОРЯДОК ДВУХ ПРОВЕРОК: имя судится ПЕРВЫМ. Значит, закрытый словарь имеет
// смысл только у поля, которое в схеме есть, — иначе автор получит «такого
// поля нет» и до разбора значения дело не дойдёт никогда. Тест держит эту
// связку: словарь без поля — правило, которое не может сработать.
func TestУКаждогоЗакрытогоСловаряЕстьПолеВСхеме(t *testing.T) {
	var s ManifestSchema
	if err := json.Unmarshal(manifestFieldsJSON, &s); err != nil {
		t.Fatalf("снимок схемы не разбирается: %v", err)
	}
	has := func(name string) bool {
		for _, fields := range s {
			if _, ok := fields[name]; ok {
				return true
			}
		}
		return false
	}
	// ИЗВЕСТНЫЙ МЁРТВЫЙ СЛОВАРЬ. `box_appear` не встречается НИГДЕ, кроме
	// самого правила: ни в DTO, ни в рантайме, ни в контенте, ни в документации
	// (появление диалогового окна автор пишет как `ui.dialogue.appear`).
	// Правило безвредно, но сработать не может — и, пока оно висит, читатель
	// думает, что такое поле бывает. Уберут его — тест потребует убрать и
	// строчку отсюда.
	dead := map[string]string{"box_appear": "нигде не встречается; появление окна — это ui.dialogue.appear"}
	for field := range ManifestWords {
		if has(field) {
			if why, ok := dead[field]; ok {
				t.Errorf("поле %q в схеме появилось (%s) — уберите его из списка мёртвых словарей", field, why)
			}
			continue
		}
		if _, known := dead[field]; known {
			continue
		}
		t.Errorf("закрытый словарь объявлен для поля %q, которого в снимке схемы нет. "+
			"Сработать он не может: имя проверяется раньше значения, и автор получит "+
			"«такого поля нет». Либо поле переименовали в DTO, либо словарь пора убрать.", field)
	}
	for path := range ManifestWordsByPath {
		seg := path[strings.LastIndex(path, ".")+1:]
		if !has(seg) {
			t.Errorf("закрытый словарь по пути %q ссылается на поле %q, которого в снимке схемы нет", path, seg)
		}
	}
}

// ── ФОРМА ВХОДА ──────────────────────────────────────────────────────────────

// КРИВОЙ JSON — ЭТО ОШИБКА, А НЕ ПРЕДУПРЕЖДЕНИЕ. Разница не косметическая:
// предупреждения гейт пропускает, ошибки блокируют. Манифест, который не
// разбирается, — это «приложение не откроется», и молча доехать до игрока он
// не должен.
func TestКривойJSONЭтоОшибкаАНеПредупреждение(t *testing.T) {
	issues := manifestIssues(t, `{"ui":{"hud":`)
	if len(issues) != 1 {
		t.Fatalf("на нечитаемом JSON ждали ровно одну находку, получили %d: %v", len(issues), issues)
	}
	if issues[0].Sev != SevError {
		t.Fatalf("нечитаемый JSON обязан быть ошибкой, а он %v", issues[0].Sev)
	}
	if !hasError(issues, "не разбирается как JSON") {
		t.Fatalf("находка не объясняет причину: %v", issues)
	}
}

// Пустое и отсутствующее не роняют проверку: манифест «ещё не наполнили» —
// законное состояние, а паника в гейте закрыла бы автору дорогу совсем.
func TestПустойИОтсутствующийМанифестНеРоняют(t *testing.T) {
	for _, doc := range []string{`{}`, `null`, `[]`, `{"ui":null}`, `{"titles":[]}`, `{"ui":{"hud":{}}}`} {
		if got := ValidateManifest([]byte(doc)); len(got) != 0 {
			t.Fatalf("на %s ждали тишину, получили %v", doc, got)
		}
	}
}

// ВЛОЖЕННОСТЬ И МАССИВЫ. Путь в находке — единственное, по чему автор найдёт
// место в файле на тысячу строк, поэтому индекс элемента в нём обязателен.
func TestВложенностьИМассивыОбходятсяСПолнымПутём(t *testing.T) {
	issues := manifestIssues(t, `{"titles":[
	  {"id":"a"},
	  {"id":"b","seasons":[{"chapters":[{"id":"c1"},{"id":"c2","bg_colour":"#101010"}]}]}
	]}`)
	if !hasWarn(issues, "titles[1].seasons[0].chapters[1].bg_colour — такого поля нет") {
		t.Fatalf("описка на глубине четырёх уровней не найдена или путь неполон: %v", issues)
	}
}

// Глубина ограничена сверху, и это не должно выглядеть как падение: очень
// вложенный (или зациклённый генератором) манифест обязан просто закончиться.
func TestОченьГлубокийМанифестНеВешаетПроверку(t *testing.T) {
	deep := strings.Repeat(`{"ui":`, 200) + `{}` + strings.Repeat(`}`, 200)
	_ = ValidateManifest([]byte(deep)) // важно только то, что вернулись
}

// ── СНИМОК СХЕМЫ ─────────────────────────────────────────────────────────────

// Разбор нарочно грубый: он обязан видеть ровно то, что видит Newtonsoft, —
// ПУБЛИЧНЫЕ ПОЛЯ ДАННЫХ. Статика, константы и readonly в манифест не попадают
// никогда, и попади они в схему — гейт разрешил бы автору писать то, чего
// рантайм не прочтёт.
func TestСнимокБерётПоляДанныхИНеБерётСтатику(t *testing.T) {
	src := `
    public sealed class LvnUiConfig
    {
        public Dictionary<string, CurrencyLook> currency_look;
        public string guest_name;
        public LvnUiConfig ui;
        public static bool Ready;
        public const int NoNumber = 0;
        public static readonly string Cached = "x";
    }`
	got := ScrapeManifestSchema(src)["LvnUiConfig"]
	want := map[string]string{
		"currency_look": "map:CurrencyLook",
		"guest_name":    "string",
		"ui":            "LvnUiConfig",
	}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("снимок разошёлся с DTO:\n получили %v\n ждали   %v", got, want)
	}
}

// СЛОВАРЬ ПОМЕЧАЕТСЯ ОСОБО, СПИСОК — НЕТ. У списка элемент — объект схемы, у
// словаря ключ авторский. Пометка `map:` и есть та разница, из-за отсутствия
// которой гейт объявлял несуществующими имена героев.
func TestСловарьСводитсяКЗначениюИПомечаетсяАСписокНет(t *testing.T) {
	src := `
    public sealed class LvnManifest
    {
        public Dictionary<string, LvnAssetMeta> assets;
        public List<LvnTitle> titles;
        public List<string> languages;
    }`
	got := ScrapeManifestSchema(src)["LvnManifest"]
	want := map[string]string{
		"assets":    "map:LvnAssetMeta",
		"titles":    "LvnTitle",
		"languages": "string",
	}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("сведение обобщённых типов разошлось:\n получили %v\n ждали   %v", got, want)
	}
}

// Полное имя типа сводится к последнему сегменту: в снимке классы лежат под
// короткими именами, и `Lvn.Content.LvnCost` обязан найти там же, где `LvnCost`.
func TestВложенноеИмяТипаСводитсяКПоследнемуСегменту(t *testing.T) {
	src := `
    public sealed class LvnTitle
    {
        public Lvn.Content.LvnCost cost;
        public System.Collections.Generic.List<string> languages;
        public System.Collections.Generic.Dictionary<string, Lvn.Content.LvnChapter> chapters;
    }`
	got := ScrapeManifestSchema(src)["LvnTitle"]
	want := map[string]string{
		"cost":      "LvnCost",
		"languages": "string",
		"chapters":  "map:LvnChapter",
	}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("длинное имя типа не сведено:\n получили %v\n ждали   %v", got, want)
	}
}

// ИМЯ В JSON БЫВАЕТ НЕ ИМЕНЕМ ПОЛЯ. `var` — ключевое слово C#, и в DTO оно
// объявлено как storyVar с псевдонимом. Схема обязана знать то имя, которое
// пишет АВТОР: иначе гейт объявит несуществующим поле из живого манифеста.
func TestПсевдонимJsonИмениПопадаетВСнимокВместоИмениПоля(t *testing.T) {
	src := `
    public sealed class LvnWardrobeSlot
    {
        [Newtonsoft.Json.JsonProperty("var")]
        public string storyVar;
        public string name;
    }`
	got := ScrapeManifestSchema(src)["LvnWardrobeSlot"]
	if got["var"] != "string" {
		t.Fatalf("псевдоним JSON-имени не попал в снимок: %v", got)
	}
	if _, ok := got["storyVar"]; ok {
		t.Fatalf("имя поля C# не должно подменять авторское имя: %v", got)
	}
	// Псевдоним действует ровно на одно поле, а не на все следующие.
	if got["name"] != "string" {
		t.Fatalf("поле после псевдонима потерялось: %v", got)
	}
}

// Поле с умолчанием — обычное поле данных. Пока разбор о нём не знал, `duration`
// анимации выглядела несуществующей.
func TestПолеСУмолчаниемНеТеряется(t *testing.T) {
	src := `
    public sealed class LvnAnim
    {
        public float duration = 1f;
        public bool loop = true;
    }`
	got := ScrapeManifestSchema(src)["LvnAnim"]
	want := map[string]string{"duration": "float", "loop": "bool"}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("поле с умолчанием потеряно:\n получили %v\n ждали   %v", got, want)
	}
}

// ── СКЛЕЙКА ОБЪЯВЛЕНИЙ: ДВЕ МОЛЧАЛИВЫЕ БЕДЫ ──────────────────────────────────
//
// Обе не роняли ничего и не писали ни строчки — просто гейт переставал знать
// про часть манифеста и объявлял живые поля несуществующими.
//
//   - ОБЪЯВЛЕНИЕ В ДВЕ СТРОКИ. `words_locales` — словарь словарей, его тип не
//     влезает в строку. Построчный разбор терял поле целиком, и гейт ругался на
//     словарь переводов оболочки, который автор деплоит на каждой правке.
//   - СКЛЕЙКА, ДОБАВЛЕННАЯ РАДИ ПЕРВОЙ, СЪЕДАЛА КЛАССЫ. Строка
//     `public string url; // адрес` кончается КОММЕНТАРИЕМ, а не точкой с
//     запятой: склейка считала объявление незакрытым и ехала вперёд до
//     следующей `;`, проглатывая по дороге объявление класса. Так пропало
//     СЕМНАДЦАТЬ классов из сорока пяти — без единого слова.

// Объявление, растянутое на две строки, — обычное поле.
func TestМногострочноеОбъявлениеСобираетсяВОдноПоле(t *testing.T) {
	// Форма — дословно из LvnUiConfig.cs.
	src := `
    public sealed class LvnUiConfig
    {
        public System.Collections.Generic.Dictionary<string,
            System.Collections.Generic.Dictionary<string, string>> words_locales;
        public string player_name_var;
    }`
	got := ScrapeManifestSchema(src)["LvnUiConfig"]
	if got["words_locales"] != "map:string" {
		t.Fatalf("поле, объявленное в две строки, потеряно (%v) — гейт объявит несуществующим "+
			"словарь переводов оболочки", got)
	}
	// И следующее поле не съедено склейкой.
	if got["player_name_var"] != "string" {
		t.Fatalf("поле после многострочного объявления пропало: %v", got)
	}
}

// Хвостовой комментарий не делает объявление незакрытым — и не съедает
// следующий за ним класс.
func TestХвостовойКомментарийНеСъедаетСледующийКласс(t *testing.T) {
	src := `
    public sealed class LvnStoreLook
    {
        public string currency; // e.g. "crystals"
        public int? amount;     // default 100
    }

    public sealed class LvnGateWords
    {
        public string gate_title;
    }`
	got := ScrapeManifestSchema(src)
	if _, ok := got["LvnGateWords"]; !ok {
		t.Fatalf("класс после поля с хвостовым комментарием проглочен склейкой — "+
			"так молча пропало 17 классов из 45; снялось: %v", keysOf2(got))
	}
	if got["LvnStoreLook"]["amount"] != "int" {
		t.Fatalf("поле с хвостовым комментарием разобрано неверно: %v", got["LvnStoreLook"])
	}
	if got["LvnGateWords"]["gate_title"] != "string" {
		t.Fatalf("поля съеденного класса не разобраны: %v", got["LvnGateWords"])
	}
}

// Объявление класса не приклеивается к ПРЕДЫДУЩЕМУ полю: между ними бывает
// пусто, бывает комментарий, а поле бывает и многострочным.
func TestОбъявлениеКлассаНеСъедаетсяПредыдущимПолем(t *testing.T) {
	src := `
    public sealed class LvnFirst
    {
        public System.Collections.Generic.List<
            LvnTitle> titles;
    }
    // Каталог новеллы.
    public sealed class LvnSecond
    {
        public string id;
    }`
	got := ScrapeManifestSchema(src)
	if _, ok := got["LvnSecond"]; !ok {
		t.Fatalf("класс после многострочного поля потерян: %v", keysOf2(got))
	}
	if got["LvnFirst"]["titles"] != "LvnTitle" {
		t.Fatalf("многострочный список разобран неверно: %v", got["LvnFirst"])
	}
	// Обратное тоже верно: поля второго класса не приписаны первому.
	if _, wrong := got["LvnFirst"]["id"]; wrong {
		t.Fatalf("поле второго класса приписано первому: %v", got["LvnFirst"])
	}
}

// `enum` — не класс: у него значения, а не поля, и объявить его классом значило
// бы завести в схеме пустышку с чужим именем.
func TestEnumНеПутаетсяСКлассом(t *testing.T) {
	src := `
    public enum ChapterEntryMode
    {
        Free,
        Energy,
    }

    public sealed class LvnEntry
    {
        public string mode;
    }`
	got := ScrapeManifestSchema(src)
	if _, ok := got["ChapterEntryMode"]; ok {
		t.Fatalf("enum попал в схему классом: %v", keysOf2(got))
	}
	if got["LvnEntry"]["mode"] != "string" {
		t.Fatalf("класс после enum разобран неверно: %v", got["LvnEntry"])
	}
}

// САМОЕ ВАЖНОЕ ЗДЕСЬ: СКОЛЬКО КЛАССОВ ДОЛЖНО БЫТЬ — СЧИТАЕМ САМИ.
//
// Страж свежести (TestСхемаМанифестаНеОтстаётОтDTO) сверяет снимок С ТЕМ ЖЕ
// СКРАПЕРОМ: когда врёт скрапер, обе стороны врут одинаково, и страж зелёный.
// Ровно так семнадцать пропавших классов и прожили незамеченными.
//
// Поэтому здесь число берётся НЕЗАВИСИМЫМ способом — простым подсчётом строк
// `public sealed class` в обоих исходниках, без единой строчки разбора полей.
func TestЧислоКлассовВСнимкеСовпадаетСПодсчётомВИсходниках(t *testing.T) {
	root := repoRoot(t)
	reSealed := regexp.MustCompile(`(?m)^\s*public sealed class \w+`)
	rePlain := regexp.MustCompile(`(?m)^\s*public class \w+`)
	declared := 0
	for _, name := range ManifestSchemaSources {
		raw, err := os.ReadFile(filepath.Join(root, "unity", "Packages", "com.lvn.engine",
			"Runtime", "Content", name))
		if err != nil {
			t.Fatal(err)
		}
		// Счёт держится на том, что классы DTO объявлены sealed — так и есть.
		// Появился НЕзапечатанный: скрапер его подберёт, а этот счётчик нет,
		// и расхождение было бы ложной тревогой. Лучше сказать прямо.
		if n := len(rePlain.FindAllString(string(raw), -1)); n > 0 {
			t.Fatalf("в %s появилось %d незапечатанных `public class` — объявите их sealed "+
				"или поправьте счётчик этого теста", name, n)
		}
		declared += len(reSealed.FindAllString(string(raw), -1))
	}
	if declared < 30 {
		t.Fatalf("в исходниках насчитано всего %d классов — проверьте пути, тест перестал "+
			"что-либо доказывать", declared)
	}
	var stored ManifestSchema
	if err := json.Unmarshal(manifestFieldsJSON, &stored); err != nil {
		t.Fatalf("снимок схемы не разбирается: %v", err)
	}
	if len(stored) != declared {
		t.Fatalf("в исходниках %d классов, в снимке %d — %d потерялось молча.\n"+
			"Страж свежести этого не поймает: он сверяет снимок с тем же скрапером, "+
			"и когда врёт скрапер, обе стороны врут одинаково.\n"+
			"Чините разбор (ScrapeManifestSchema), потом перегенерируйте:\n"+
			"  (cd tools/lvnconv && go run ./cmd/lvn-genschema)",
			declared, len(stored), declared-len(stored))
	}
}

// keysOf2 — имена классов снимка, для внятного сообщения об ошибке.
func keysOf2(s ManifestSchema) []string {
	out := make([]string, 0, len(s))
	for k := range s {
		out = append(out, k)
	}
	sort.Strings(out)
	return out
}

// ── СЛУЖЕБНЫЕ КЛЮЧИ СЕРВЕРА ──────────────────────────────────────────────────
//
// `rev` в манифест дописывает САМ СЕРВЕР (защита от гонки двух редакторов) и
// требует обратно при следующем PUT; в DTO движка его нет и быть не должно.
// Без списка ServerAddedKeys гейт выдавал находку на КАЖДОМ сохранении из
// панели — ровно то, после чего проверку перестают читать.

func TestСлужебныйКлючСервераВКорнеМолчит(t *testing.T) {
	mustBeQuiet(t, `{"rev":17,"ui":{"hud":{"height":0.08}}}`)
}

// ОСЛАБЛЕНИЕ РОВНО НА ОДИН ЭТАЖ. Тот же `rev` внутри вложенного объекта — уже
// не подпись сервера, а настоящая описка автора, и молчать о ней нельзя:
// иначе освобождение от шума превратилось бы в слепое пятно на всю глубину.
func TestТотЖеКлючВнутриВложенногоОбъектаЛовится(t *testing.T) {
	issues := manifestIssues(t, `{"ui":{"rev":17}}`)
	if !hasWarn(issues, "ui.rev — такого поля нет") {
		t.Fatalf("`rev` внутри ui прошёл молча — служебный ключ сервера освободил от проверки "+
			"весь манифест, а не только его корень: %v", issues)
	}
	issues = manifestIssues(t, `{"titles":[{"id":"t","rev":3}]}`)
	if !hasWarn(issues, "titles[0].rev — такого поля нет") {
		t.Fatalf("`rev` в карточке новеллы прошёл молча: %v", issues)
	}
}

// ── ГЛАВНОЕ: ТИШИНА НА ЖИВОМ КОНТЕНТЕ ────────────────────────────────────────

// ГЕЙТ, КОТОРЫЙ РУГАЕТСЯ НА РАБОЧИЙ КОНТЕНТ, ХУЖЕ ОТСУТСТВИЯ ГЕЙТА: его
// перестают читать, и вместе с шумом теряется настоящая находка. Поэтому
// проверка обязана молчать на манифестах, которые мы САМИ показываем авторам
// как образец.
//
// Что в наборе: manifest.json под howto/, packages/ и sandbox/content/ — то,
// что мы сами показываем авторам как образец.
//
// Чего в наборе НЕТ и почему:
//
//   - `*/Packages/manifest.json` — это файл Unity (список UPM-зависимостей).
//     Имя совпало, содержимое чужое; сервер его и не видит — isManifestPath
//     узнаёт только manifest.json в корне контента.
//   - `server/content/manifest.json` — прод-снимок. Он разбирается отдельно,
//     соседним тестом со СПИСКОМ известных находок: часть из них — мёртвые
//     данные в проде, а часть — настоящая ложная тревога гейта, и смешивать их
//     с образцами для авторов нельзя. Когда список опустеет, файл вернётся
//     сюда.
//   - `.history/` и `node_modules/` — прошлые версии и чужой код.
func TestГейтМолчитНаАвторскихПримерах(t *testing.T) {
	root := repoRoot(t)
	var checked int
	for _, sub := range []string{"howto", "packages", filepath.Join("sandbox", "content")} {
		base := filepath.Join(root, sub)
		if _, err := os.Stat(base); err != nil {
			continue
		}
		err := filepath.Walk(base, func(p string, info os.FileInfo, err error) error {
			if err != nil {
				return nil
			}
			if info.IsDir() {
				switch info.Name() {
				case ".history", "node_modules", ".git", "Library":
					return filepath.SkipDir
				}
				return nil
			}
			if info.Name() != "manifest.json" {
				return nil
			}
			// Тёзка из Unity — не наш манифест.
			if filepath.Base(filepath.Dir(p)) == "Packages" {
				return nil
			}
			blob, rerr := os.ReadFile(p)
			if rerr != nil {
				t.Errorf("%s не читается: %v", p, rerr)
				return nil
			}
			checked++
			rel, _ := filepath.Rel(root, p)
			for _, is := range ValidateManifest(blob) {
				t.Errorf("гейт ругается на авторский пример %s: %s\n"+
					"Ложная тревога на образце опаснее пропуска: по этим файлам "+
					"учатся, и «у меня в примере тоже ругается» отучает читать гейт.",
					rel, is.Msg)
			}
			return nil
		})
		if err != nil {
			t.Fatalf("обход %s: %v", sub, err)
		}
	}
	// Набор, который случайно опустел, зелёный всегда и не проверяет ничего.
	if checked == 0 {
		t.Fatal("не нашлось ни одного авторского manifest.json — тест стал пустым, поправьте корни обхода")
	}
}

// Прод-манифест ЧИСТ, и это утверждение, а не наблюдение.
//
// Гейт нашёл в нём пять видов мёртвых полей: имена сезонов (движок знает у
// сезона только главы), описание новеллы не там, где его читает карточка,
// фоновая картинка ГЛАВЫ, положенная на новеллу, и список моделей 3D-набора,
// у которого в репозитории нет ни писателя, ни читателя. Владелец решил
// править на актуальный вид, а не подстраивать движок под лежащее.
//
// Теперь прод сверяется наравне с авторскими примерами: ноль находок. Если
// появится новая — это либо описка автора, либо поле, которое движок потерял;
// и то и другое стоит увидеть в тот же день.
func TestПродМанифестЧист(t *testing.T) {
	root := repoRoot(t)
	p := filepath.Join(root, "server", "content", "manifest.json")
	blob, err := os.ReadFile(p)
	if err != nil {
		t.Skipf("прод-снимка нет рядом (%v) — сверять нечего", err)
	}
	issues := ValidateManifest(blob)
	if len(issues) > 0 {
		var lines []string
		for _, is := range issues {
			lines = append(lines, "  "+is.Msg)
		}
		t.Fatalf("в прод-манифесте снова есть находки:\n%s", strings.Join(lines, "\n"))
	}
}

// Снимок, лежащий рядом данными, должен ОСТАВАТЬСЯ данными: если он перестанет
// разбираться, гейт молча потеряет все имена и начнёт пропускать любые описки.
func TestСнимокСхемыЧитаетсяИНеПуст(t *testing.T) {
	var s ManifestSchema
	if err := json.Unmarshal(manifestFieldsJSON, &s); err != nil {
		t.Fatalf("снимок схемы не разбирается: %v", err)
	}
	if len(s) < 15 {
		t.Fatalf("в снимке всего %d классов — гейт почти ничего не проверяет", len(s))
	}
	if len(s["LvnUiConfig"]) == 0 || len(s["LvnManifest"]) == 0 {
		t.Fatal("в снимке нет корневых классов LvnManifest/LvnUiConfig — проверять будет нечего")
	}
}

// ОДИНАКОВЫЕ ИМЕНА В КАТАЛОГЕ — прогресс игрока ключуется именем новеллы, и
// дубль означает общее прохождение у двух разных историй.
func TestDuplicateIDsAreWarned(t *testing.T) {
	дубли := []byte(`{"titles":[
		{"id":"проба","name":"Первая","seasons":[{"chapters":[{"id":"ch1","number":1},{"id":"ch1","number":2}]}]},
		{"id":"проба","name":"Копия","seasons":[{"chapters":[]}]}]}`)

	var новелла, глава bool
	for _, is := range ValidateManifest(дубли) {
		if is.Sev != SevWarning {
			continue
		}
		if strings.Contains(is.Msg, "встречается дважды") && strings.Contains(is.Msg, "новелла") {
			новелла = true
		}
		if strings.Contains(is.Msg, "глава") && strings.Contains(is.Msg, "встречается дважды") {
			глава = true
		}
	}
	if !новелла {
		t.Error("две новеллы с одним именем прошли молча — они разделят одно прохождение")
	}
	if !глава {
		t.Error("две главы с одним именем прошли молча — точка продолжения станет неоднозначной")
	}

	// Здоровый каталог молчит: предупреждение, которое звучит всегда, не
	// значит ничего.
	чистый := []byte(`{"titles":[
		{"id":"первая","name":"Первая","seasons":[{"chapters":[{"id":"ch1","number":1},{"id":"ch2","number":2}]}]},
		{"id":"вторая","name":"Вторая","seasons":[{"chapters":[{"id":"ch1","number":1}]}]}]}`)
	for _, is := range ValidateManifest(чистый) {
		if strings.Contains(is.Msg, "встречается дважды") {
			t.Errorf("здоровый каталог получил предупреждение о дублях: %s", is.Msg)
		}
	}
}

// ГЕРОИНЯ, КОТОРОЙ НЕТ. Имя в ui.wardrobe.entity пишут руками, а сущности
// переименовывают при переимпорте: промах тихий — гардероб откроет первую
// попавшуюся, и её же будет греть загрузка.
func TestDanglingWardrobeEntityIsWarned(t *testing.T) {
	каталог := []byte(`{"ui":{"wardrobe":{"entity":"Гeроиня"}},
		"sprites":{"Героиня":{"layers":["hero.png"]},"Статист":{"layers":["extra.png"]}}}`)

	var найдено string
	for _, is := range ValidateManifest(каталог) {
		if is.Sev == SevWarning && strings.Contains(is.Msg, "ui.wardrobe.entity") {
			найдено = is.Msg
		}
	}
	if найдено == "" {
		t.Fatal("ссылка на несуществующую сущность прошла молча — гардероб откроет чужого")
	}
	// Подсказка обязана назвать похожее имя: промах здесь чаще всего опечатка
	// или латиница вместо кириллицы, и без подсказки автор ищет глазами.
	if !strings.Contains(найдено, "Героиня") {
		t.Errorf("предупреждение не подсказало ближайшее имя: %s", найдено)
	}

	// Правильная ссылка молчит.
	верный := []byte(`{"ui":{"wardrobe":{"entity":"Героиня"}},
		"sprites":{"Героиня":{"layers":["hero.png"]}}}`)
	for _, is := range ValidateManifest(верный) {
		if strings.Contains(is.Msg, "ui.wardrobe.entity") {
			t.Errorf("верная ссылка получила предупреждение: %s", is.Msg)
		}
	}

	// Имени нет вовсе — это законно (героиню выберут по гардеробу), молчим.
	без := []byte(`{"sprites":{"Героиня":{"layers":["hero.png"]}}}`)
	for _, is := range ValidateManifest(без) {
		if strings.Contains(is.Msg, "ui.wardrobe.entity") {
			t.Errorf("отсутствие имени принято за ошибку: %s", is.Msg)
		}
	}
}
