package lvn

import (
	"encoding/json"
	"fmt"
	"github.com/fomeanator/elvin/tools/lvnconv/internal/nearest"
	"maps"
	"regexp"
	"slices"
	"strings"
)

// ПРОВЕРКА МАНИФЕСТА — по именам полей, а не по схеме.
//
// Скрипты проходят через структурный гейт сервера, манифест — нет: его пишут
// на диск после разбора JSON и всё. А это весь облик приложения: темы, цвета,
// экраны, подборки, оси гардероба. Опечатка в нём молча даёт умолчание, и
// автор видит не ошибку, а «почему-то не так».
//
// Схему манифеста перенести сюда нельзя — она живёт в C#-DTO (LvnUiConfig), и
// её дублирование стало бы очередным зеркалом, которое разойдётся. Но самые
// частые описки ловятся БЕЗ схемы, по КОНВЕНЦИИ ИМЁН: поле, кончающееся на
// `_color`, обязано быть цветом; поле `theme` — известной темой. Это ловит не
// всё, зато не врёт и не требует второй копии контракта.
//
// Всё здесь — ПРЕДУПРЕЖДЕНИЯ. Манифест с непонятным полем должен доехать до
// игрока (хост вправе класть туда своё), а вот молчать об этом не должен.

var reColorField = regexp.MustCompile(`_color$|^color$`)

// ColorWords — слова, которые понимает UiColor.Named в движке. Сверяется
// сторожем: словарь цвета один на весь язык, и проверка обязана знать тот же.
var ColorWords = []string{
	// токены темы
	"bg", "surface", "surface_hi", "panel", "text", "dim", "accent", "on_accent",
	"gold", "warn", "border", "veil", "clear",
	// имена движка
	"white", "black", "red", "blue", "green", "yellow", "cyan", "magenta",
	// мнемоники настроения
	"cold", "tint_cold", "warm", "tint_warm", "sepia",
}

// ManifestWords — закрытые словари полей манифеста. Ключ — ИМЯ ПОЛЯ, потому
// что схемы у нас нет; значения совпадают с тем, что читает рантайм через
// LvnAuthorWord.
var ManifestWords = map[string][]string{
	"theme":         {"midnight", "cyber", "cyberpunk", "romance"},
	"speaker_focus": {"dim", "solo"},
	"tap_burst":     {"hearts"},
	"appear":        appearWords,
}

// ServerAddedKeys — ключи, которые в манифест дописывает САМ СЕРВЕР, а не
// автор. `rev` он ставит на каждой записи и требует обратно на следующей
// (защита от гонки двух редакторов), но в DTO движка его нет и быть не
// должно. Без этого списка гейт выдавал находку на КАЖДОМ сохранении — ровно
// то, после чего проверку перестают читать.
var ServerAddedKeys = map[string]bool{"rev": true}

var appearWords = []string{"fade", "rise", "pop", "slide_up", "up", "slide_down", "down",
	"slide_left", "left", "slide_right", "right", "drop", "unfold"}

// ManifestWordsByPath — для имён, которые СЛИШКОМ ОБЩИЕ, чтобы судить о них по
// одному слову. «mode» бывает и у анимации (`mode=queue`), поэтому закрытый
// список привязан к полному пути, а не к имени поля.
var ManifestWordsByPath = map[string][]string{
	"ui.hud.mode": {"always", "full", "choices"},
}

// ValidateManifest проверяет то, что можно проверить без схемы.
func ValidateManifest(data []byte) []Issue {
	var root any
	if err := json.Unmarshal(data, &root); err != nil {
		return []Issue{{Index: -1, Op: "manifest", Sev: SevError, Msg: "не разбирается как JSON: " + err.Error()}}
	}
	var out []Issue
	var walk func(node any, path, class string, depth int)
	walk = func(node any, path, class string, depth int) {
		if depth > 24 {
			return
		}
		switch n := node.(type) {
		case map[string]any:
			// СЛОВАРЬ АВТОРА: ключи — его имена (герои, валюты, языки), а не
			// поля схемы. Проверяем значения, ключи не трогаем.
			if strings.HasPrefix(class, "map:") {
				inner := strings.TrimPrefix(class, "map:")
				for k, v := range n {
					here := k
					if path != "" {
						here = path + "." + k
					}
					walk(v, here, inner, depth+1)
				}
				return
			}
			fields := manifestSchema[class]
			for k, v := range n {
				here := k
				if path != "" {
					here = path + "." + k
				}
				// Ключ на `$` — заметка автора в JSON, а не поле: так пишут
				// комментарии там, где их нет в языке (см. grammar.json).
				if strings.HasPrefix(k, "$") {
					continue
				}
				// Служебное поле сервера — не авторское.
				if depth == 0 && ServerAddedKeys[k] {
					continue
				}
				// ИМЯ ПОЛЯ, КОТОРОГО НЕТ. Newtonsoft молча пропускает
				// незнакомое, поэтому `titel_color` не даёт ни ошибки, ни
				// строчки: цвет просто остаётся умолчанием, и автор ищет
				// причину глазами. Спрашиваем СНЯТУЮ схему — там, где класс
				// известен.
				next := ""
				if fields != nil {
					t, known := fields[k]
					if !known {
						msg := fmt.Sprintf("%s — такого поля нет, оно будет пропущено", here)
						if sg := nearest.Of(k, slices.Sorted(maps.Keys(fields)), 2); sg != "" {
							msg += fmt.Sprintf(" — может быть %q?", sg)
						}
						out = append(out, Issue{Index: -1, Op: "manifest", Sev: SevWarning, Msg: msg})
						continue
					}
					next = t
				}
				if s, ok := v.(string); ok && s != "" {
					checkManifestValue(&out, here, k, s)
				}
				walk(v, here, next, depth+1)
			}
		case []any:
			for i, v := range n {
				walk(v, fmt.Sprintf("%s[%d]", path, i), class, depth+1)
			}
		}
	}
	// Корень манифеста описан LvnManifest, поддерево `ui` — LvnUiConfig.
	// Спуск дальше идёт по ТИПАМ полей из снимка, так что и каталог, и облик
	// проверяются одинаково.
	walk(root, "", "LvnManifest", 0)
	out = append(out, duplicateIDs(root)...)
	return out
}

// ОДИНАКОВЫЕ ИМЕНА В КАТАЛОГЕ — ТИХАЯ ПОТЕРЯ ПРОГРЕССА.
//
// Прогресс игрока ключуется ИМЕНЕМ новеллы: «докуда дошёл», точка
// продолжения, отметки прочитанного, сейвы — всё под `id`. Две новеллы с
// одним именем делят одно прохождение: игрок открывает вторую и видит своё
// место из первой, а сохранившись — затирает его. Две главы с одним именем
// внутри новеллы ломают продолжение: «где я остановился» указывает на первую
// попавшуюся, и игрок либо перечитывает, либо перескакивает.
//
// Замер 06.09: манифест с двумя новеллами `проба` и двумя главами `ch1`
// принимался БЕЗ ЕДИНОГО СЛОВА. Способ появления самый обычный — автор
// копирует новеллу «сделать похожую» и забывает сменить имя, или импорт
// приносит одинаковые имена глав.
//
// Предупреждение, а не ошибка: заблокировать публикацию значило бы запереть
// живой каталог, в котором дубль уже есть, — и автор не смог бы выложить
// ничего, пока не почистит. Слово в ответе он видит сразу.
func duplicateIDs(root any) []Issue {
	m, ok := root.(map[string]any)
	if !ok {
		return nil
	}
	titles, _ := m["titles"].([]any)
	var out []Issue
	seenTitle := map[string]bool{}
	for _, t := range titles {
		tm, ok := t.(map[string]any)
		if !ok {
			continue
		}
		id, _ := tm["id"].(string)
		if id != "" {
			if seenTitle[id] {
				out = append(out, Issue{Index: -1, Op: "manifest", Sev: SevWarning,
					Msg: fmt.Sprintf("новелла %q встречается дважды: прогресс игрока ключуется именем, "+
						"и обе будут делить одно прохождение — дайте второй своё имя", id)})
			}
			seenTitle[id] = true
		}
		seasons, _ := tm["seasons"].([]any)
		seenChapter := map[string]bool{}
		for _, se := range seasons {
			sm, ok := se.(map[string]any)
			if !ok {
				continue
			}
			chapters, _ := sm["chapters"].([]any)
			for _, c := range chapters {
				cm, ok := c.(map[string]any)
				if !ok {
					continue
				}
				cid, _ := cm["id"].(string)
				if cid == "" {
					continue
				}
				if seenChapter[cid] {
					out = append(out, Issue{Index: -1, Op: "manifest", Sev: SevWarning,
						Msg: fmt.Sprintf("в новелле %q глава %q встречается дважды: точка продолжения "+
							"укажет на первую попавшуюся — переименуйте одну", id, cid)})
				}
				seenChapter[cid] = true
			}
		}
	}
	return out
}

var reHex = regexp.MustCompile(`^#?[0-9a-fA-F]{3,8}$`)

func checkManifestValue(out *[]Issue, path, field, value string) {
	v := strings.TrimSpace(strings.ToLower(value))
	// Незакрытая подстановка — не опечатка: её ещё не подставили.
	if strings.Contains(value, "{") {
		return
	}
	if reColorField.MatchString(field) {
		if inSet(ColorWords, v) || reHex.MatchString(v) {
			return
		}
		msg := fmt.Sprintf("%s=%q — не цвет: ни слово словаря, ни шестнадцатеричная запись; экран возьмёт умолчание", path, value)
		if sg := nearest.Of(v, ColorWords, 2); sg != "" {
			msg += fmt.Sprintf(" — может быть %q?", sg)
		}
		*out = append(*out, Issue{Index: -1, Op: "manifest", Sev: SevWarning, Msg: msg})
		return
	}
	known, ok := ManifestWordsByPath[path]
	if !ok {
		known, ok = ManifestWords[field]
	}
	if ok {
		if inSet(known, v) {
			return
		}
		msg := fmt.Sprintf("%s=%q — такого значения нет, будет умолчание (известны: %s)",
			path, value, strings.Join(known, ", "))
		if sg := nearest.Of(v, known, 2); sg != "" {
			msg += fmt.Sprintf(" — может быть %q?", sg)
		}
		*out = append(*out, Issue{Index: -1, Op: "manifest", Sev: SevWarning, Msg: msg})
	}
}
