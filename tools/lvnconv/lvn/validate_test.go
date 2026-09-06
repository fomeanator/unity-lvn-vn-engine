package lvn

import (
	"github.com/fomeanator/elvin/tools/lvnconv/internal/nearest"
	"strconv"
	"strings"
	"testing"
	"time"
)

func parse(t *testing.T, s string) *Doc {
	t.Helper()
	d, err := Parse([]byte(s))
	if err != nil {
		t.Fatalf("parse: %v", err)
	}
	return d
}

// hasError reports whether any error-severity issue mentions sub.
func hasError(issues []Issue, sub string) bool {
	for _, is := range issues {
		if is.Sev == SevError && contains(is.Msg, sub) {
			return true
		}
	}
	return false
}

// hasWarn reports whether any warning-severity issue mentions sub.
func hasWarn(issues []Issue, sub string) bool {
	for _, is := range issues {
		if is.Sev == SevWarning && contains(is.Msg, sub) {
			return true
		}
	}
	return false
}

func TestUnknownFieldWarned(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[{"op":"fade","too":"black","duration":0.5}]}`)
	issues := Validate(d)
	if !hasWarn(issues, `unknown field "too"`) {
		t.Fatalf("expected unknown-field warning, got %v", issues)
	}
	if !hasWarn(issues, `did you mean "to"`) {
		t.Fatalf("expected a 'to' suggestion, got %v", issues)
	}
}

func TestEnumValueWarned(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[{"op":"actor","id":"x","position":"lft","show":true}]}`)
	issues := Validate(d)
	if !hasWarn(issues, `position="lft" is not a known value`) {
		t.Fatalf("expected enum warning, got %v", issues)
	}
	if !hasWarn(issues, `did you mean "left"`) {
		t.Fatalf("expected a 'left' suggestion, got %v", issues)
	}
}

// ОТКРЫТЫЙ НАБОР ПОЛЕЙ — НЕ ПОВОД МОЛЧАТЬ О ЯВНОЙ ОПЕЧАТКЕ.
//
// У `actor`/`obj` набор закрыть нельзя: сверх грамматики там живут оси
// гардероба, которые называет автор (в живом контенте `outfit=` и `hair=`
// встречаются десятки тысяч раз). Из-за этого опечатка в САМОЙ ЧАСТОЙ команде
// языка не ловилась вовсе — компилировалась молча и молча же не действовала.
func TestActorTypoWarnedButWardrobeAxesAreNot(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[
	 {"op":"actor","id":"m","sprite_ulr":"/a.png","postion":"left"},
	 {"op":"actor","id":"m","outfit":"school","hair":"long","armor":"plate"}
	]}`)
	issues := Validate(d)

	if !hasWarn(issues, `"sprite_ulr"`) || !hasWarn(issues, `"postion"`) {
		t.Fatalf("описки в actor должны быть замечены: %+v", issues)
	}
	for _, axis := range []string{`"outfit"`, `"hair"`, `"armor"`} {
		if hasWarn(issues, axis) {
			t.Fatalf("ось гардероба %s — не опечатка, предупреждать о ней нельзя: %+v", axis, issues)
		}
	}
}

// Короткие имена не судим: у осей вроде `w`/`h` расстояние до `x`/`y` тоже
// единица, и подсказка была бы шумом ровно там, где автор прав.
func TestShortAxisNamesAreLeftAlone(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[{"op":"actor","id":"m","w":1,"h":2,"ear":"x"}]}`)
	for _, is := range Validate(d) {
		if contains(is.Msg, "looks like a typo") {
			t.Fatalf("короткое имя не должно давать подсказку: %s", is.Msg)
		}
	}
}

// Valid values and keys must NOT warn — the checks are only for typos.
func TestValidFieldsAndEnumsDoNotWarn(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[
	 {"op":"fade","to":"black","duration":0.5},
	 {"op":"actor","id":"x","position":"left","show":true,"emotion":"happy","enter":"fade"},
	 {"op":"audio","channel":"music","url":"/a.mp3","action":"play"},
	 {"op":"camera","action":"shake","duration":0.4},
	 {"op":"particles","type":"rain","on":true},
	 {"op":"set","key":"gold","value":5,"default":true},
	 {"op":"say","who":"X","text":"hi {gold}"}
	]}`)
	for _, is := range Validate(d) {
		if contains(is.Msg, "unknown field") || contains(is.Msg, "is not a known value") {
			t.Fatalf("false positive on valid content: %s", is.String())
		}
	}
}

func TestUndefinedVarTypoWarned(t *testing.T) {
	// score is set; scoore is read in an expr and an interpolation → both typos.
	d := parse(t, `{"scene":"t","script":[
	 {"op":"set","key":"score","value":0},
	 {"op":"if","expr":"scoore >= 10","then":"w","else":"l"},
	 {"op":"say","who":"X","text":"У тебя {scoore} очков"},
	 {"op":"label","id":"w"},{"op":"label","id":"l"}
	]}`)
	issues := Validate(d)
	if !hasWarn(issues, `variable "scoore" is read but never set`) {
		t.Fatalf("expected undefined-var warning, got %v", issues)
	}
	if !hasWarn(issues, `did you mean "score"`) {
		t.Fatalf("expected a 'score' suggestion, got %v", issues)
	}
}

// A variable that isn't a near-miss of any defined var is treated as seeded
// externally (carried from an earlier chapter / the host), not a typo.
func TestExternalVarNotFlagged(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[
	 {"op":"set","key":"gold","value":0},
	 {"op":"if","expr":"player_name_len > 3","then":"w","else":"w"},
	 {"op":"label","id":"w"}
	]}`)
	if hasWarn(Validate(d), "is read but never set") {
		t.Fatalf("a distinct external var must not be flagged as a typo")
	}
}

// String literals inside an expression are not variables and must not be flagged.
func TestStringLiteralNotFlaggedAsVar(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[
	 {"op":"set","key":"state","value":"idle"},
	 {"op":"if","expr":"state == \"stat\"","then":"w","else":"w"},
	 {"op":"label","id":"w"}
	]}`)
	// "stat" is a quoted literal that is a near-miss of "state" — but it's a
	// literal, so stripping quotes must prevent a false positive.
	if hasWarn(Validate(d), `variable "stat"`) {
		t.Fatalf("a string literal was mistaken for a variable: %v", Validate(d))
	}
}

// With no vars set at all, the doc is assumed to rely on external seeding and the
// typo check is skipped entirely (no noise).
func TestNoDefinedVarsSkipsCheck(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[
	 {"op":"if","expr":"anything > 1","then":"w","else":"w"},
	 {"op":"label","id":"w"}
	]}`)
	if hasWarn(Validate(d), "is read but never set") {
		t.Fatalf("undefined-var check should not run when nothing is set")
	}
}

// An unset/absent enum field (e.g. actor with no position) must not warn.
func TestAbsentEnumFieldDoesNotWarn(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[{"op":"actor","id":"x","show":true}]}`)
	for _, is := range Validate(d) {
		if contains(is.Msg, "is not a known value") {
			t.Fatalf("absent enum field must not warn: %s", is.String())
		}
	}
}

func contains(s, sub string) bool {
	return len(sub) == 0 || (len(s) >= len(sub) && indexOf(s, sub) >= 0)
}
func indexOf(s, sub string) int {
	for i := 0; i+len(sub) <= len(s); i++ {
		if s[i:i+len(sub)] == sub {
			return i
		}
	}
	return -1
}

func TestValidate_CleanDoc(t *testing.T) {
	d := parse(t, `{"script":[
		{"op":"label","id":"start"},
		{"op":"say","text":"hi"},
		{"op":"goto","label":"start"}
	]}`)
	for _, is := range Validate(d) {
		if is.Sev == SevError {
			t.Errorf("unexpected error issue: %s", is)
		}
	}
}

func TestValidate_DanglingGoto(t *testing.T) {
	d := parse(t, `{"script":[{"op":"goto","label":"nowhere"}]}`)
	if !hasError(Validate(d), "undefined label") {
		t.Fatal("expected dangling-goto error")
	}
}

func TestValidate_BuiltinEndIsFine(t *testing.T) {
	d := parse(t, `{"script":[{"op":"goto","label":"__end"}]}`)
	if hasError(Validate(d), "undefined label") {
		t.Fatal("__end must be an allowed builtin target")
	}
}

func TestValidate_DuplicateLabel(t *testing.T) {
	d := parse(t, `{"script":[
		{"op":"label","id":"a"},
		{"op":"label","id":"a"}
	]}`)
	if !hasError(Validate(d), "duplicate label") {
		t.Fatal("expected duplicate-label error")
	}
}

func TestValidate_UnknownOp(t *testing.T) {
	// Unknown ops are a WARNING, not an error: they may be host-defined
	// (authored via `ext`, handled by the game through LvnOps.Register).
	d := parse(t, `{"script":[{"op":"saay","text":"typo"}]}`)
	iss := Validate(d)
	if hasError(iss, "unknown op") {
		t.Fatal("unknown op must not be an error (host-defined ops are legal)")
	}
	if !hasWarn(iss, "unknown op") {
		t.Fatal("expected unknown-op warning")
	}
}

func TestValidate_DropMapTargets(t *testing.T) {
	// on_drop "target:label" pairs and on_drop_miss are jump references: a
	// typo must error, and a label reached only via a drop is NOT dead.
	d := parse(t, `{"script":[
		{"op":"obj","id":"apple","draggable":true,"on_drop":"bag:in_bag, box:nowhere","on_drop_miss":"missed"},
		{"op":"label","id":"in_bag"},
		{"op":"label","id":"missed"}
	]}`)
	iss := Validate(d)
	if !hasError(iss, `undefined label "nowhere"`) {
		t.Fatal("expected error for the typo'd drop label")
	}
	if hasWarn(iss, `"in_bag" is never targeted`) || hasWarn(iss, `"missed" is never targeted`) {
		t.Fatal("drop-reached labels must not read as dead")
	}
}

func TestValidate_MalformedDropPair(t *testing.T) {
	d := parse(t, `{"script":[
		{"op":"obj","id":"apple","draggable":true,"on_drop":"baglabel"}
	]}`)
	if !hasWarn(Validate(d), "not target:label") {
		t.Fatal("expected malformed-pair warning")
	}
}

func TestValidate_IfBranchTargets(t *testing.T) {
	d := parse(t, `{"script":[
		{"op":"if","cond":{},"then":"yes","else":"no"},
		{"op":"label","id":"yes"}
	]}`)
	if !hasError(Validate(d), `label "no"`) {
		t.Fatal("expected error for missing else target")
	}
}

func TestValidate_ChoiceOptionGoto(t *testing.T) {
	d := parse(t, `{"script":[
		{"op":"choice","options":[
			{"text":"go","goto":"missing"},
			{"text":"stay","goto":"here"}
		]},
		{"op":"label","id":"here"}
	]}`)
	if !hasError(Validate(d), `label "missing"`) {
		t.Fatal("expected error for missing choice target")
	}
}

func TestValidate_NestedOptionBody(t *testing.T) {
	d := parse(t, `{"script":[
		{"op":"choice","options":[
			{"text":"x","body":[{"op":"goto","label":"ghost"}]}
		]}
	]}`)
	if !hasError(Validate(d), `label "ghost"`) {
		t.Fatal("expected error for dangling goto inside option body")
	}
}

func TestValidate_FallThroughIntoJumpTarget(t *testing.T) {
	// The button-screen footgun: a say screen falls through into a label that is
	// also a jump target → tapping slides the chapter forward unexpectedly.
	d := parse(t, `{"script":[
		{"op":"say","text":"hub — tap a hotspot"},
		{"op":"label","id":"weather"},
		{"op":"say","text":"rain"},
		{"op":"goto","label":"weather"}
	]}`)
	iss := Validate(d)
	if !hasWarn(iss, "fall-through") {
		t.Fatal("expected a fall-through warning for ':weather'")
	}
	if hasError(iss, "fall-through") {
		t.Fatal("fall-through must be a warning, not an error")
	}
}

func TestValidate_NoFallThroughWarnAfterGoto(t *testing.T) {
	d := parse(t, `{"script":[
		{"op":"label","id":"a"},
		{"op":"say","text":"x"},
		{"op":"goto","label":"b"},
		{"op":"label","id":"b"},
		{"op":"say","text":"y"},
		{"op":"goto","label":"a"}
	]}`)
	if hasWarn(Validate(d), "fall-through") {
		t.Fatal("a label reached only after a goto must not warn")
	}
}

func TestValidate_UnbalancedBraces(t *testing.T) {
	d := parse(t, `{"script":[{"op":"say","text":"hello {name"}]}`)
	if !hasWarn(Validate(d), "unbalanced") {
		t.Fatal("expected unbalanced-brace warning")
	}
}

func TestValidate_EscapedBracesAreFine(t *testing.T) {
	d := parse(t, `{"script":[{"op":"say","text":"a {{literal}} and {name}"}]}`)
	if hasWarn(Validate(d), "unbalanced") {
		t.Fatal("escaped braces and a plain {var} must not warn")
	}
}

func TestValidate_ChoiceOptionWithoutTarget(t *testing.T) {
	d := parse(t, `{"script":[{"op":"choice","options":[{"text":"dead end"}]}]}`)
	if !hasWarn(Validate(d), "no goto and no body") {
		t.Fatal("expected warning for a choice option with no goto/body")
	}
}

func TestValidate_MissingScene(t *testing.T) {
	d := parse(t, `{"script":[{"op":"say","text":"hi"}]}`)
	iss := Validate(d)
	if !hasWarn(iss, "scene") {
		t.Fatal("expected a missing-scene warning")
	}
	if hasError(iss, "scene") {
		t.Fatal("missing scene is a warning, not an error")
	}
}

func TestValidate_ScenePresent_NoWarn(t *testing.T) {
	d := parse(t, `{"scene":"x","script":[{"op":"say","text":"hi"}]}`)
	if hasWarn(Validate(d), "no `scene`") {
		t.Fatal("a present scene header must not warn")
	}
}

func TestValidate_EmptyScript(t *testing.T) {
	d := parse(t, `{"scene":"x","script":[]}`)
	if !hasWarn(Validate(d), "empty") {
		t.Fatal("expected an empty-script warning")
	}
}

func TestValidate_FailedCommandAsNarration(t *testing.T) {
	d := parse(t, `{"scene":"x","script":[{"op":"say","text":"fade to=\"black\"3 duration=0.8"}]}`)
	// Ошибка, а не предупреждение: такая строка молча уезжает игроку.
	if !hasError(Validate(d), "не разобрался") {
		t.Fatal("строка-команда, ставшая репликой, обязана быть ОШИБКОЙ")
	}
}

func TestValidate_PlainNarration_NoFalsePositive(t *testing.T) {
	d := parse(t, `{"scene":"x","script":[{"op":"say","text":"She said hello."},{"op":"say","who":"Mara","text":"set the mood"}]}`)
	if hasError(Validate(d), "не разобрался") {
		t.Fatal("plain narration / dialogue must not warn")
	}
}

func TestValidate_SeverityClassification(t *testing.T) {
	d := parse(t, `{"script":[
		{"op":"saay","text":"typo"},
		{"op":"label","id":"orphan"}
	]}`)
	iss := Validate(d)
	if !hasWarn(iss, "unknown op") {
		t.Fatal("unknown op should be a warning (may be host-defined)")
	}
	if !hasWarn(iss, "never targeted") {
		t.Fatal("an untargeted label should be a warning")
	}
}

func TestValidate_InputNeedsVar(t *testing.T) {
	d := parse(t, `{"script":[{"op":"input","prompt":"Name?"}]}`)
	if !hasError(Validate(d), "input needs var") {
		t.Fatal("expected missing-var error")
	}
	d2 := parse(t, `{"script":[{"op":"input","var":"name","prompt":"Name?"}]}`)
	if hasError(Validate(d2), "input") {
		t.Fatal("valid input must pass")
	}
}

func TestValidate_TimedChoicePairing(t *testing.T) {
	// timeout_goto is a jump reference; either half without the other warns.
	d := parse(t, `{"script":[
		{"op":"choice","options":[{"text":"a","goto":"x"}],"timeout":5,"timeout_goto":"nowhere"},
		{"op":"label","id":"x"}
	]}`)
	if !hasError(Validate(d), `undefined label "nowhere"`) {
		t.Fatal("expected error for the typo'd timeout branch")
	}
	half := parse(t, `{"script":[
		{"op":"choice","options":[{"text":"a","goto":"x"}],"timeout":5},
		{"op":"label","id":"x"}
	]}`)
	if !hasWarn(Validate(half), "nowhere to go when time runs out") {
		t.Fatal("expected timeout-without-goto warning")
	}
}

// A call to a function no evaluator implements is the failure the runtime hides:
// the expression throws, the variable reads 0, nothing on screen says so. The
// validator has to be the one that speaks up — this is the check that would have
// caught `func` being a phantom feature years earlier.
func TestValidate_UnknownExprFunction(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[
		{"op":"set","key":"x","expr":"add(2,3)"},
		{"op":"if","expr":"flor(x) > 1","then":"a","else":"a"},
		{"op":"say","text":"you have {tally(x)} left"},
		{"op":"label","id":"a"}
	]}`)
	is := Validate(d)
	if !hasWarn(is, "unknown function add()") {
		t.Fatalf("expected a warning for add() in a set expr: %v", is)
	}
	if !hasWarn(is, "did you mean floor()") {
		t.Fatalf("expected a spelling suggestion for flor(): %v", is)
	}
	if !hasWarn(is, "unknown function tally()") {
		t.Fatalf("expected a warning for a call in an interpolation: %v", is)
	}
}

// Every built-in, a bare variable, a nested built-in call and an ink-style text
// alternative must stay silent — a false positive here would break the 0-warning
// gate on real content.
func TestValidate_KnownExprFunctionsAreSilent(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[
		{"op":"set","key":"x","expr":"floor(sum(list(1,2,3)) / max(1, len(inv)))"},
		{"op":"set","key":"y","expr":"get(m, \"put(x)\", 0)"},
		{"op":"if","expr":"has(inv, \"key\") and chance(0.5)","then":"a","else":"a"},
		{"op":"say","text":"{keys(m)} and {mood: good|bad} and {a|b|c}"},
		{"op":"label","id":"a"}
	]}`)
	for _, is := range Validate(d) {
		if contains(is.Msg, "unknown function") {
			t.Fatalf("false positive: %s", is.String())
		}
	}
}

// Two statements sharing one line inside a block leave the second one INSIDE the
// first expression, where the evaluator throws and `set` swallows the throw — the
// variable keeps its old value and nothing on screen says so (the howto/rpg shop
// shipped like this and still gated at 0 warnings).
func TestValidate_StrayAssignInExpr(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[
		{"op":"set","key":"gold","expr":"gold - 5  potions = potions + 1"}
	]}`)
	if !hasWarn(Validate(d), "stray `=`") {
		t.Fatalf("expected a stray-assignment warning: %v", Validate(d))
	}
	clean := parse(t, `{"scene":"t","script":[
		{"op":"set","key":"a","expr":"gold >= 5 and hp != 0 and flag == 1"},
		{"op":"set","key":"b","expr":"get(m, \"k=v\", 0)"},
		{"op":"set","key":"c","expr":"put(m, \"k\", x <= 2)"}
	]}`)
	for _, is := range Validate(clean) {
		if contains(is.Msg, "stray") {
			t.Fatalf("false positive: %s", is.String())
		}
	}
}

// The op-typo lint: a mistyped command must not reach the player as dialogue.
// Shapes prose never has (`=`, `->`, a /path argument) are flagged; a
// positional-only slip is knowingly left alone — see commandLike.
//
// Это ОШИБКА, а не предупреждение: предупреждение через API публикации никто
// не читает, и глава с `bbg /content/…` уезжала игроку с ok:true.
func TestMistypedCommandLint(t *testing.T) {
	flagged := func(text string) bool {
		doc := &Doc{Script: []Cmd{{"op": "say", "text": text}, {"op": "goto", "label": "__end"}}}
		for _, is := range Validate(doc) {
			if is.Sev != SevError {
				continue
			}
			if strings.Contains(is.Msg, "опечаткой") || strings.Contains(is.Msg, "не разобрался") {
				return true
			}
		}
		return false
	}
	for _, tc := range []struct {
		text string
		want bool
		why  string
	}{
		{"sett gold = 1", true, "key=value slip"},
		{"iff gold > 1 -> rich", true, "swallows a whole branch, and no if op is left for the dangling-target check to see"},
		{"bbg /content/bg/x.jpg", true, "content url as an argument"},
		{"actro id=hill", true, "near-miss with key=value"},
		{"shwo mara", false, "positional-only: indistinguishable from prose, deliberately not flagged"},
		{"Она открыла дверь.", false, "ordinary narration"},
		{"wave after wave of them", false, "prose whose first word is one edit from an op"},
		{"Мы дошли до развилки — налево или направо?", false, "prose with a dash"},
	} {
		if got := flagged(tc.text); got != tc.want {
			t.Errorf("%q: flagged=%v, want %v (%s)", tc.text, got, tc.want, tc.why)
		}
	}
}

// ГОВОРЯЩИЙ, ПОХОЖИЙ НА ПРОЗУ, — это проза, разрезанная двоеточием.
//
// В языке `Имя: текст` — реплика, поэтому строка «Комната-побег. Кликай по
// предметам: осмотри стол» превращается в реплику говорящего «Комната-побег.
// Кликай по предметам». Тихо: на экране появляется подпись, которой автор не
// писал. В живом контенте таких нашлось шесть.
func TestProseSpeakerWarned(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[
	 {"op":"say","who":"Комната-побег. Кликай по предметам","text":"осмотри стол"},
	 {"op":"say","who":"Матвей и Валера","text":"мы вдвоём"},
	 {"op":"say","who":"...","text":"пауза"},
	 {"op":"say","who":"Анна","text":"привет"}
	]}`)
	issues := Validate(d)

	if !hasWarn(issues, "looks like prose cut by a colon") {
		t.Fatalf("проза, разрезанная двоеточием, должна быть замечена: %+v", issues)
	}
	for _, ok := range []string{"Матвей и Валера", "Анна", `"..."`} {
		for _, is := range issues {
			if contains(is.Msg, ok) && contains(is.Msg, "looks like prose") {
				t.Fatalf("законный говорящий %q не должен предупреждать: %s", ok, is.Msg)
			}
		}
	}
}

// ДИАГНОСТИКА ГОВОРИТ О ТОМ, ЧТО ПИСАЛ АВТОР.
//
// В импортированной главе метку получает КАЖДАЯ нода графа articy (форма
// `n17_000000`); в базе партнёрской новеллы таких 5329 против 38 авторских. Две
// проверки — «провал в метку» и «метка никем не нужна» — ругались на них
// 4343 раза, и пять настоящих находок тонули в четырёх с половиной тысячах
// строк вывода.
func TestGeneratedLabelsDoNotDrownTheRealFindings(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[
	 {"op":"say","text":"раз"},
	 {"op":"label","id":"n17_000000"},
	 {"op":"say","text":"два"},
	 {"op":"label","id":"развилка"},
	 {"op":"goto","label":"n17_000000"},
	 {"op":"goto","label":"развилка"}
	]}`)
	issues := Validate(d)

	// Молчать надо о ШУМЕ РАЗМЕТКИ — «провал в метку» и «метка никем не нужна».
	// Дефекты ПОТОКА (петля без выхода, недостижимость) машинная метка не
	// оправдывает: в главах Cold именно на таких метках замкнут гардероб, и
	// промолчать о софтлоке было бы хуже, чем шуметь.
	for _, is := range issues {
		if is.Sev != SevWarning || !contains(is.Msg, "n17_000000") {
			continue
		}
		if contains(is.Msg, "fall-through") || contains(is.Msg, "never targeted") {
			t.Fatalf("шум разметки на метке импортёра: %s", is.Msg)
		}
	}
	// Авторская метка с тем же провалом — предупреждение на месте.
	if !hasWarn(issues, `"развилка"`) {
		t.Fatalf("на авторской метке проверка обязана работать: %+v", issues)
	}
}

// Подавление объясняется вслух: иначе оно неотличимо от сломанной проверки.
// Заметка НЕ предупреждение — `-strict` валит сборку на любом предупреждении,
// и честное объяснение стало бы стеной.
func TestSuppressionIsSaidOutLoudAsANote(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[
	 {"op":"say","text":"раз"},
	 {"op":"label","id":"n1_000000"},
	 {"op":"label","id":"n2_000000"}
	]}`)
	issues := Validate(d)

	var note *Issue
	for i := range issues {
		if issues[i].Sev == SevNote {
			note = &issues[i]
		}
	}
	if note == nil {
		t.Fatalf("подавленное должно быть названо заметкой: %+v", issues)
	}
	if !contains(note.Msg, "generated label") {
		t.Fatalf("заметка должна объяснять, ЧТО именно не показано: %s", note.Msg)
	}
	if note.Sev == SevWarning || note.Sev == SevError {
		t.Fatal("заметка не должна валить -strict")
	}
}

// ПЕТЛЯ БЕЗ ВЫХОДА — игрок застрянет навсегда.
//
// Находка не теоретическая: в главах Cold так замкнут гардероб — открыть,
// поставить флаг, вернуться на метку, открыть снова (тридцать таких мест в
// восемнадцати файлах). Прежняя диагностика видела лишь СЛЕДСТВИЕ («дальше
// недостижимо»), да и то не всегда: если в хвост главы вёл ещё один переход,
// петля проходила молча — ровно так молчал cold-ch01.
func TestLoopWithoutExitWarned(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[
	 {"op":"label","id":"ловушка"},
	 {"op":"say","text":"крутимся"},
	 {"op":"goto","label":"ловушка"},
	 {"op":"say","text":"сюда не попасть"}
	]}`)
	if !hasWarn(Validate(d), "has no way out") {
		t.Fatalf("вечная петля должна быть замечена: %+v", Validate(d))
	}
}

// Игровой цикл — норма: из него уводит выбор, ветвление или возврат.
func TestGameLoopsAreNotFlagged(t *testing.T) {
	cases := map[string]string{
		"выбор уводит наружу": `{"scene":"t","script":[
		 {"op":"label","id":"loop"},
		 {"op":"choice","options":[{"text":"ещё","goto":"loop"},{"text":"хватит","goto":"end"}]},
		 {"op":"goto","label":"loop"},
		 {"op":"label","id":"end"},
		 {"op":"say","text":"конец"}]}`,
		"ветвление уводит наружу": `{"scene":"t","script":[
		 {"op":"label","id":"loop"},
		 {"op":"inc","key":"n"},
		 {"op":"if","expr":"n > 3","then":"end","else":"loop"},
		 {"op":"goto","label":"loop"},
		 {"op":"label","id":"end"},
		 {"op":"say","text":"конец"}]}`,
		"возврат из вызова": `{"scene":"t","script":[
		 {"op":"label","id":"sub"},
		 {"op":"say","text":"работа"},
		 {"op":"return"},
		 {"op":"goto","label":"sub"}]}`,
	}
	for name, src := range cases {
		d := parse(t, src)
		if hasWarn(Validate(d), "has no way out") {
			t.Fatalf("%s: законный цикл не должен предупреждать: %+v", name, Validate(d))
		}
	}
}

// ДЛИТЕЛЬНОСТЬ ЗОВУТ ДВУМЯ ИМЕНАМИ — и это раскол языка, а не описка автора.
//
// Старые команды ждут `duration`, добавленные позже — `dur`. Выучив одно, автор
// промахивается на другой команде, и обычные проверки тут бессильны: между
// словами пять правок (для подсказки «did you mean» слишком много), а у `fx` и
// `sfx` набор полей открытый — `fx duration=2` проходил МОЛЧА, эффект не
// применялся.
func TestDurationSpellingWarned(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[
	 {"op":"fade","to":"black","dur":1},
	 {"op":"fx","vignette":0.3,"duration":2},
	 {"op":"sfx","id":"a","glow":1,"duration":1}
	]}`)
	issues := Validate(d)
	for _, want := range []string{`op "fade" spells its duration "duration"`,
		`op "fx" spells its duration "dur"`, `op "sfx" spells its duration "dur"`} {
		if !hasWarn(issues, want) {
			t.Fatalf("не замечено: %s\n%+v", want, issues)
		}
	}
}

// Правильное написание молчит — иначе предупреждение стало бы фоном.
func TestCorrectDurationSpellingIsQuiet(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[
	 {"op":"fade","to":"black","duration":1},
	 {"op":"fx","vignette":0.3,"dur":2},
	 {"op":"portal","open":1,"dur":0.5}
	]}`)
	for _, is := range Validate(d) {
		if contains(is.Msg, "spells its duration") {
			t.Fatalf("верное написание не должно предупреждать: %s", is.Msg)
		}
	}
}

// ТЁЗКА: слово написано ВЕРНО, ошибочно место.
//
// Три слова языка работают и командой, и полем чужой команды: `fade` (поле у
// bg/audio — длительность перехода), `flash` и `tint` (поля у sfx/fx). Автор,
// знающий их как поля, пишет их полем и там, где поля нет. Подсказка по
// расстоянию тут бессильна — опечатки нет.
//
// Цена путаницы известна поимённо: тёзка `hint` (команда против поля опции)
// стоила двух руководств, годами учивших не применять рабочую команду.
func TestFieldNamedLikeACommandExplainsItself(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[
	 {"op":"bg","id":"hall","tint":"#800020"},
	 {"op":"audio","channel":"music","action":"play","url":"/a.ogg","flash":1}
	]}`)
	issues := Validate(d)
	for _, want := range []string{`"tint" is a COMMAND of its own`, `"flash" is a COMMAND of its own`} {
		if !hasWarn(issues, want) {
			t.Fatalf("не объяснено: %s\n%+v", want, issues)
		}
	}
}

// А там, где поле ЗАКОННО (tint у sfx, fade у bg), — молчание.
func TestLegitimateNamesakeFieldsAreQuiet(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[
	 {"op":"sfx","id":"a","tint":0.4,"flash":1},
	 {"op":"bg","id":"hall","fade":1.2},
	 {"op":"tint","color":"#800020","duration":0.5}
	]}`)
	for _, is := range Validate(d) {
		if contains(is.Msg, "is a COMMAND of its own") {
			t.Fatalf("законное поле не должно предупреждать: %s", is.Msg)
		}
	}
}

// ЭКРАН, КОТОРЫЙ ЖДЁТ ИГРОКА, — НЕ ЛОВУШКА.
//
// Новелла с собственным интерфейсом держит поток на метке ожидания
// («label idle / wait / goto idle»), а уводит его КНОПКА дерева `ui` или
// кликабельный предмет сцены. Для скрипта это вечная петля, для игрока —
// экран, на котором он выбирает, куда пойти.
//
// Пока проверка про это не знала, она кричала на каждой такой новелле — 55
// раз на витринах движка, — и настоящие ловушки (замкнутый гардероб в главах
// Cold) тонули в шуме, ради которого проверка и заводилась. Диагностика,
// которую перестают читать, не работает вовсе.
func TestPlayerDrivenLoopIsNotATrap(t *testing.T) {
	cases := map[string]string{
		"кнопка интерфейса": `{"scene":"t","script":[
		 {"op":"ui","id":"hud","tree":{"kind":"panel","children":[
		   {"kind":"button","text":"дальше","on_click":"finish"}]}},
		 {"op":"label","id":"idle"},
		 {"op":"wait","seconds":1},
		 {"op":"goto","label":"idle"},
		 {"op":"label","id":"finish"},
		 {"op":"say","text":"конец"}]}`,
		"предмет сцены с кликом": `{"scene":"t","script":[
		 {"op":"label","id":"room"},
		 {"op":"obj","id":"drawer","on_click":"drawer","sprite_url":"/a.png"},
		 {"op":"say","text":"кликай по предметам"},
		 {"op":"goto","label":"room"},
		 {"op":"label","id":"drawer"},
		 {"op":"say","text":"ключ!"}]}`,
		"кнопка интерфейса объектной записью": `{"scene":"t","script":[
		 {"op":"ui","id":"hud","tree":{"kind":"panel","children":[
		   {"kind":"button","text":"дальше","on_click":{"goto":"finish","set":{"seen":true}}}]}},
		 {"op":"label","id":"idle"},
		 {"op":"wait","seconds":1},
		 {"op":"goto","label":"idle"},
		 {"op":"label","id":"finish"},
		 {"op":"say","text":"конец"}]}`,
		"предмет, который перетаскивают": `{"scene":"t","script":[
		 {"op":"obj","id":"apple","draggable":true,"on_drop":"bag:in_bag","sprite_url":"/a.png"},
		 {"op":"label","id":"room"},
		 {"op":"say","text":"перетащи яблоко"},
		 {"op":"goto","label":"room"},
		 {"op":"label","id":"in_bag"},
		 {"op":"say","text":"хоп"}]}`,
	}
	for name, src := range cases {
		d := parse(t, src)
		if hasWarn(Validate(d), "has no way out") {
			t.Errorf("%s: поток ждёт ИГРОКА, а не крутится сам — это не ловушка", name)
		}
	}
}

// МЕТКА, К КОТОРОЙ ВЕДЁТ КНОПКА ДЕРЕВА, ДОСТИЖИМА — обеими записями.
//
// Рантайм понимает `on_click` двумя способами везде: меткой одним словом и
// объектом `{goto, set}`. Обход дерева знал только первый — и метка, к которой
// ведёт единственная кнопка меню, объявлялась мёртвой. Дальше это тонет: автор
// перестаёт читать предупреждение «метка ни от кого не достижима», потому что
// оно врёт на его собственном меню.
func TestObjectClickInsideUiTreeReachesItsLabel(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[
	 {"op":"ui","id":"menu","tree":{"kind":"panel","children":[
	   {"kind":"button","text":"в сад","on_click":{"goto":"сад","set":{"был":true}}}]}},
	 {"op":"label","id":"idle"},
	 {"op":"wait","seconds":1},
	 {"op":"goto","label":"idle"},
	 {"op":"label","id":"сад"},
	 {"op":"say","text":"яблони"}]}`)
	for _, is := range Validate(d) {
		if strings.Contains(is.Msg, "сад") {
			t.Errorf("метка, к которой ведёт кнопка объектной записью, объявлена недостижимой: %s", is.Msg)
		}
	}
}

// …и обратное: интерфейс без единого отклика ловушку не оправдывает.
func TestSilentUiDoesNotExcuseATrap(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[
	 {"op":"ui","id":"hud","tree":{"kind":"panel","children":[
	   {"kind":"text","text":"счёт: {n}"}]}},
	 {"op":"label","id":"ловушка"},
	 {"op":"say","text":"крутимся"},
	 {"op":"goto","label":"ловушка"},
	 {"op":"say","text":"сюда не попасть"}]}`)
	if !hasWarn(Validate(d), "has no way out") {
		t.Fatal("дерево без кнопок ничего игроку не даёт — ловушка осталась ловушкой")
	}
}

// ── ЧТО МОЖНО АНИМИРОВАТЬ ───────────────────────────────────────────────────
//
// Словарь свойств жил ТОЛЬКО в рантайме (Lvn.UI.LvnAnimProp.Known).
// Компилятор `prop=` не разбирал, валидатор внутрь треков не смотрел, редактор
// не подсказывал — и естественное «opacity» вместо «alpha» проходило всю
// дорогу до игры, где трек молча пропускался. Полоса не двигалась, в сборке ни
// слова, искать было нечего. Отсюда — проверка в валидаторе; тесты ниже держат
// её обещания.

// anim с одним треком заданного свойства — самая короткая форма, какая доходит
// до проверки.
func animOneProp(t *testing.T, prop string) []Issue {
	t.Helper()
	return Validate(parse(t, `{"scene":"t","script":[{"op":"anim","id":"bar","anim":{"duration":0.2,
	 "tracks":[{"prop":`+`"`+prop+`"`+`,"keys":[[0,1],[0.2,0.5]]}]}}]}`))
}

func TestНезнакомоеСвойствоАнимацииЗамечено(t *testing.T) {
	issues := animOneProp(t, "opacity")
	if !hasWarn(issues, `prop="opacity"`) {
		t.Fatalf("описка в prop должна быть замечена: %+v", issues)
	}
	// Автору мало «не знаю»: он не догадается, что правильное имя — alpha.
	// Жалоба обязана назвать словарь — но ТОТ, что действует ЗДЕСЬ: трек без
	// слоя, значит `frame` тут не при чём и предлагать его было бы враньём.
	for _, known := range AnimPropsWhole {
		if !hasWarn(issues, known) {
			t.Fatalf("в предупреждении нет известного имени %q — автору неоткуда узнать словарь: %+v", known, issues)
		}
	}
	if hasWarn(issues, "frame") {
		t.Fatalf("перечень предлагает %q, которое у трека БЕЗ слоя не играет: %+v", "frame", issues)
	}
	if !hasWarn(issues, "трек будет пропущен") {
		t.Fatalf("предупреждение должно объяснять последствие — трек не сыграет: %+v", issues)
	}
}

// Разница между «пожаловаться» и «отказать»: анимация — украшение, и валить
// сборку главы из-за неё нельзя. Это предупреждение, а не ошибка.
func TestНезнакомоеСвойствоАнимацииНеОшибка(t *testing.T) {
	for _, is := range animOneProp(t, "opacity") {
		if is.Sev == SevError && contains(is.Msg, "prop=") {
			t.Fatalf("описка в анимации не должна валить главу: %s", is.String())
		}
	}
}

// Близкая описка получает подсказку. `scale_x` — не выдумка: ровно так было
// написано в фикстуре компилятора, лежавшей в репозитории.
func TestБлизкаяОпискаСвойстваПолучаетПодсказку(t *testing.T) {
	if !hasWarn(animOneProp(t, "scale_x"), `может быть "scalex"?`) {
		t.Fatalf("для scale_x должна быть подсказка scalex: %+v", animOneProp(t, "scale_x"))
	}
}

func TestВсеИзвестныеСвойстваАнимацииМолчат(t *testing.T) {
	if len(AnimProps) != 10 {
		t.Fatalf("в словаре %d имён, ожидалось 10 — обнови и рантайм, и этот тест", len(AnimProps))
	}
	// Каждое имя молчит В СВОЁМ месте: у фигуры целиком — свой набор, у слоя
	// куклы свой. Раньше проверка была плоской, и `frame` без слоя проходил
	// молча, чтобы потом молча же ничего не сыграть.
	for _, prop := range AnimPropsWhole {
		for _, is := range animOneProp(t, prop) {
			if contains(is.Msg, "prop=") {
				t.Fatalf("свойство %q у фигуры целиком не должно давать жалобу: %s", prop, is.String())
			}
		}
	}
	for _, prop := range AnimPropsLayered {
		for _, is := range animLayeredProp(t, prop) {
			if contains(is.Msg, "prop=") {
				t.Fatalf("свойство %q у слоя не должно давать жалобу: %s", prop, is.String())
			}
		}
	}
	// И жалуется в ЧУЖОМ — иначе контекст был бы украшением.
	if !hasWarn(animOneProp(t, "frame"), "принадлежит СЛОЮ") {
		t.Fatal("`frame` без слоя не играет — об этом надо сказать")
	}
	if !hasWarn(animLayeredProp(t, "screen_x"), "экранного места нет") {
		t.Fatal("`screen_x` у слоя не играет — об этом надо сказать")
	}
}

// Тот же трек, но ПРИВЯЗАННЫЙ К СЛОЮ куклы: словарь там другой.
func animLayeredProp(t *testing.T, prop string) []Issue {
	t.Helper()
	return Validate(parse(t, `{"scene":"t","script":[{"op":"anim","id":"bar","anim":{"duration":0.2,
	 "tracks":[{"prop":`+`"`+prop+`"`+`,"layer":"рукав","keys":[[0,1],[0.2,0.5]]}]}}]}`))
}

// Трек без свойства до рантайма не доходит: `.lvns` на нём отказывает
// («anim: prop required»), а сырой `.lvn` рантайм отбрасывает раньше проверки.
// Жаловаться не на что, и придумывать вторую жалобу здесь нельзя.
func TestТрекБезСвойстваМолчит(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[{"op":"anim","id":"bar","anim":{"duration":0.2,
	 "tracks":[{"keys":[[0,1],[0.2,0.5]]},{"prop":"","keys":[[0,1]]}]}}]}`)
	for _, is := range Validate(d) {
		if contains(is.Msg, "prop=") {
			t.Fatalf("трек без свойства жалобы не заслужил: %s", is.String())
		}
	}
}

// `move` разворачивается в ДВА синхронных трека, а сложная анимация — в
// сколько угодно. Проверять только первый значило бы ловить половину описок:
// у второго трека тот же исход — молча не сыграет.
func TestКаждыйТрекПроверяетсяОтдельно(t *testing.T) {
	d := parse(t, `{"scene":"t","script":[{"op":"anim","id":"bar","anim":{"duration":1,"tracks":[
	 {"prop":"screen_x","keys":[[0,0],[1,1]]},
	 {"prop":"screen_y","keys":[[0,0],[1,1]]},
	 {"prop":"opacity","keys":[[0,1],[1,0]]},
	 {"prop":"rot","keys":[[0,0],[1,90]]}]}}]}`)
	issues := Validate(d)
	if !hasWarn(issues, `prop="opacity"`) || !hasWarn(issues, `prop="rot"`) {
		t.Fatalf("описка не в первом треке тоже должна быть замечена: %+v", issues)
	}
	// Соседи по команде верны — жалоба на них была бы шумом. (Сверяем именно
	// форму `prop="screen_x"`: само слово стоит и в перечне известных имён
	// внутри чужой жалобы.)
	for _, is := range issues {
		if contains(is.Msg, `prop="screen_x"`) || contains(is.Msg, `prop="screen_y"`) {
			t.Fatalf("верный сосед не должен попадать под жалобу: %s", is.String())
		}
	}
}

// Груз `anim` приходит из чужих рук — импорта, ручного `.lvn`, чужого
// редактора — и бывает какой угодно формы. Валидатор зовут ради предупреждений,
// и падать на кривом грузе он не имеет права: паника унесёт с собой ВСЕ
// находки по файлу, а не только эту.
func TestКривойГрузАнимацииНеРоняетВалидатор(t *testing.T) {
	for _, script := range []string{
		`{"op":"anim","id":"bar"}`,                                      // вложенного объекта нет вовсе
		`{"op":"anim","id":"bar","anim":null}`,                          // он есть, но пустой
		`{"op":"anim","id":"bar","anim":"stop"}`,                        // он есть, но не объект
		`{"op":"anim","id":"bar","anim":{"duration":1}}`,                // объект без треков
		`{"op":"anim","id":"bar","anim":{"tracks":null}}`,               // треки есть, но пустые
		`{"op":"anim","id":"bar","anim":{"tracks":"opacity"}}`,          // треки не список
		`{"op":"anim","id":"bar","anim":{"tracks":[null,7,"opacity"]}}`, // элементы не объекты
		`{"op":"anim","id":"bar","anim":{"tracks":[{"prop":42}]}}`,      // свойство не строка
		`{"op":"anim","id":"bar","stop":"all"}`,                         // форма остановки
	} {
		d := parse(t, `{"scene":"t","script":[`+script+`]}`)
		Validate(d) // не паникует — этого и добиваемся
	}
}

// ── СТИЛИ АУРЫ: СИНОНИМ — НЕ ОПЕЧАТКА ───────────────────────────────────────
//
// Синонимы (`ice`, `thunder`, `dark`, `void`…) знал только рантайм. Валидатор
// держал одиннадцать канонических имён и на законное слово отвечал «is not a
// known value», да ещё и советовал «may be fire?» — ПРОТИВОПОЛОЖНУЮ стихию.
// Ложная тревога дороже молчания: автор идёт править работающий код и по
// совету валидатора меняет лёд на огонь.

// sfx с одним полем стиля — самая короткая форма, какая доходит до проверки.
func auraOneStyle(t *testing.T, style string) []Issue {
	t.Helper()
	return Validate(parse(t, `{"scene":"t","script":[{"op":"sfx","id":"aura","aura_style":"`+style+`"}]}`))
}

func TestВсеСтилиАурыВключаяСинонимыМолчат(t *testing.T) {
	// Одиннадцать канонических плюс четырнадцать синонимов. Число зашито
	// намеренно: словарь пополняют в рантайме, и молчаливое расхождение с
	// валидатором — ровно тот отказ, ради которого этот тест написан.
	if len(AuraStyles) != 25 {
		t.Fatalf("в словаре %d стилей, ожидалось 25 — сверь с разбором в LvnSpriteFxDriver", len(AuraStyles))
	}
	for _, style := range AuraStyles {
		for _, is := range auraOneStyle(t, style) {
			if contains(is.Msg, "aura_style") {
				t.Fatalf("стиль %q рантайм понимает — жалоба на него зовёт править работающий код: %s",
					style, is.String())
			}
		}
	}
}

// Отдельно и поимённо — та самая пара, на которой всё и вскрылось. Общий
// перебор выше упал бы вместе с ней, но не сказал бы, что именно случилось.
func TestСинонимСтихииНеПолучаетСоветВзятьДругую(t *testing.T) {
	for _, is := range auraOneStyle(t, "ice") {
		if contains(is.Msg, "aura_style") {
			t.Fatalf("`ice` — законный синоним `frost`; жалоба на него советовала брать `fire`, "+
				"то есть менять лёд на огонь: %s", is.String())
		}
	}
}

func TestВыдуманныйСтильАурыЗамечен(t *testing.T) {
	issues := auraOneStyle(t, "lava")
	if !hasWarn(issues, `aura_style="lava"`) {
		t.Fatalf("выдуманный стиль должен быть замечен — рантайм молча возьмёт basic: %+v", issues)
	}
	// Жалоба обязана назвать ВЕСЬ словарь, включая синонимы: иначе автор,
	// написавший `lava`, узнает про `frost` и не узнает про `ice` — и придёт
	// со вторым вопросом туда же.
	for _, style := range AuraStyles {
		if !hasWarn(issues, style) {
			t.Fatalf("в перечне известных нет стиля %q — автору неоткуда узнать словарь: %+v", style, issues)
		}
	}
}

// ── ВИДЫ УЗЛОВ ДЕРЕВА `ui` ──────────────────────────────────────────────────
//
// Неизвестный вид не давал ошибки: LvnUiLayer падал в `default` и делал ПУСТУЮ
// ПАНЕЛЬ. Опечатка «buton» превращала кнопку в невидимый прямоугольник — экран
// собирался, кнопки на нём не было, и в логе ни строчки. Игрок упирается в
// мёртвый экран, автор ищет ошибку в логике.

func uiTree(t *testing.T, tree string) []Issue {
	t.Helper()
	return Validate(parse(t, `{"scene":"t","script":[{"op":"ui","id":"hud","tree":`+tree+`}]}`))
}

func TestОпечаткаВВидеУзлаКорняЗамечена(t *testing.T) {
	issues := uiTree(t, `{"kind":"buton","text":"Дальше"}`)
	if !hasWarn(issues, `kind="buton"`) {
		t.Fatalf("опечатка в виде узла должна быть замечена: %+v", issues)
	}
	if !hasWarn(issues, "пустая панель") {
		t.Fatalf("жалоба обязана назвать последствие — кнопки на экране не будет: %+v", issues)
	}
	if !hasWarn(issues, `может быть "button"?`) {
		t.Fatalf("подсказка должна назвать близкое имя: %+v", issues)
	}
	// Перечень видов целиком: подсказка угадывает не всегда, а собрать дерево
	// автору надо в любом случае.
	for _, kind := range UiNodeKinds {
		if !hasWarn(issues, kind) {
			t.Fatalf("в перечне известных нет вида %q — автору неоткуда узнать словарь: %+v", kind, issues)
		}
	}
}

// Дерево `ui` — именно ДЕРЕВО, и опечатка живёт не в корне, а в третьем ряду
// кнопок. Проверять только вершину значило бы ловить самый редкий случай.
func TestОпечаткаВГлубинеДереваUiЗамечена(t *testing.T) {
	issues := uiTree(t, `{"kind":"panel","children":[
	 {"kind":"row","children":[
	  {"kind":"column","children":[
	   {"kind":"colunm","children":[{"kind":"text","text":"внутри"}]}]}]}]}`)
	if !hasWarn(issues, `kind="colunm"`) {
		t.Fatalf("опечатка на четвёртом уровне тоже должна быть замечена: %+v", issues)
	}
	if !hasWarn(issues, `может быть "column"?`) {
		t.Fatalf("подсказка должна назвать близкое имя: %+v", issues)
	}
	// Здоровые предки жалобы не заслужили — иначе одна описка красит всё
	// дерево и читать выдачу невозможно.
	for _, is := range issues {
		for _, ok := range []string{`kind="panel"`, `kind="row"`, `kind="column"`, `kind="text"`} {
			if contains(is.Msg, ok) {
				t.Fatalf("верный узел не должен попадать под жалобу: %s", is.String())
			}
		}
	}
}

func TestВсеЗаконныеВидыУзловДереваUiМолчат(t *testing.T) {
	if len(UiNodeKinds) != 9 {
		t.Fatalf("в словаре %d видов, ожидалось 9 — сверь с разбором в LvnUiLayer", len(UiNodeKinds))
	}
	for _, kind := range UiNodeKinds {
		for _, is := range uiTree(t, `{"kind":"`+kind+`"}`) {
			if contains(is.Msg, "kind=") {
				t.Fatalf("законный вид %q не должен давать жалобу: %s", kind, is.String())
			}
		}
	}
}

// Дерево приходит из чужих рук — импорта, ручного `.lvn`, чужого редактора —
// и бывает какой угодно формы. Валидатор зовут ради предупреждений, и падать
// на кривом грузе он не имеет права: паника унесёт с собой ВСЕ находки по
// файлу, а не только эту.
func TestКривоеДеревоUiНеРоняетВалидатор(t *testing.T) {
	for _, tree := range []string{
		`null`,                              // дерева нет вовсе
		`"panel"`,                           // дерево не объект
		`[]`,                                // дерево — список
		`{}`,                                // узел без kind: рантайм читает его как panel
		`{"kind":7}`,                        // вид не строка
		`{"kind":""}`,                       // вид пустой
		`{"kind":"panel","children":"row"}`, // дети не список
		`{"kind":"panel","children":null}`,
		`{"kind":"panel","children":[null,7,"text"]}`, // дети не объекты
		`{"kind":"panel","children":[{"kind":"row","children":[{}]}]}`,
	} {
		d := parse(t, `{"scene":"t","script":[{"op":"ui","id":"hud","tree":`+tree+`}]}`)
		Validate(d) // не паникует — этого и добиваемся
	}
	// Узел без вида — не опечатка: рантайм читает его как panel, и это самая
	// частая запись в живых деревьях. Жалоба на неё была бы чистым шумом.
	for _, is := range uiTree(t, `{"children":[{"text":"без вида"}]}`) {
		if contains(is.Msg, "kind=") {
			t.Fatalf("узел без вида законен (рантайм берёт panel) — жаловаться не на что: %s", is.String())
		}
	}
}

// Глубина дерева ничем не ограничена ни в языке, ни в импорте. Обход — рекурсия,
// и без предела достаточно одного глубокого дерева, чтобы валидатор ушёл в стек
// и унёс с собой публикацию главы.
func TestОченьГлубокоеДеревоUiНеУходитВБесконечность(t *testing.T) {
	const глубина = 5000
	tree := `{"kind":"text"}`
	for i := 0; i < глубина; i++ {
		tree = `{"kind":"panel","children":[` + tree + `]}`
	}
	готово := make(chan int, 1)
	go func() {
		defer func() {
			if r := recover(); r != nil {
				готово <- -1
			}
		}()
		d, err := Parse([]byte(`{"scene":"t","script":[{"op":"ui","id":"hud","tree":` + tree + `}]}`))
		if err != nil {
			готово <- -2
			return
		}
		готово <- len(Validate(d))
	}()
	select {
	case n := <-готово:
		if n == -1 {
			t.Fatal("обход дерева паникует на глубине — валидатор уносит с собой всю выдачу по файлу")
		}
	case <-time.After(20 * time.Second):
		t.Fatal("обход глубокого дерева не кончился за 20с — похоже, предел глубины пропал")
	}
}

// ── ПРИЦЕЛЬНЫЕ ПОДСКАЗКИ АНИМАЦИИ ───────────────────────────────────────────
//
// Сравнение по буквам находит описку («scale_x» → «scalex»), но не находит
// ПУТАНИЦУ: «opacity» и «alpha» не похожи ни одной буквой, а перепутать их
// естественно — у actor и obj прозрачность так и называется, `opacity=0.4`.
// Такие пары названы поимённо, иначе автор получает перечень из десяти имён и
// выбирает из него наугад.

func TestПрицельнаяПодсказкаТамГдеБуквыМолчат(t *testing.T) {
	для := map[string]string{
		"opacity": `"alpha"`,
		"fill":    `"scalex"`,
	}
	for промах, ждём := range для {
		issues := animOneProp(t, промах)
		if !hasWarn(issues, ждём) {
			t.Fatalf("описка %q должна получить прицельную подсказку про %s: %+v", промах, ждём, issues)
		}
	}
}

// Условие, при котором прицельная подсказка вообще нужна: буквенное сравнение
// на этих словах МОЛЧИТ. Если оно заговорит, автор получит два совета разом —
// и «может быть», и прицельный, — а это хуже одного.
func TestПрицельнаяПодсказкаНеСпоритСБуквенной(t *testing.T) {
	for промах := range AnimPropHints {
		if inSet(AnimProps, промах) {
			t.Fatalf("%q стоит и в подсказках, и в словаре свойств — жалобы не будет вовсе, "+
				"а подсказка станет мёртвой", промах)
		}
		if s := nearest.Of(промах, AnimProps, 2); s != "" {
			t.Fatalf("для %q буквенное сравнение уже советует %q — прицельная подсказка спорит с ним; "+
				"оставь что-то одно", промах, s)
		}
		for _, is := range animOneProp(t, промах) {
			if contains(is.Msg, "может быть") {
				t.Fatalf("на %q не должно быть двух советов сразу: %s", промах, is.String())
			}
		}
	}
}

// А обычная подсказка на близкой описке продолжает работать: прицельный список
// её не вытеснил. `scale_x` — не выдумка, ровно так было написано в фикстуре
// компилятора этого репозитория.
func TestБлизкаяОпискаСохранилаОбычнуюПодсказку(t *testing.T) {
	issues := animOneProp(t, "scale_x")
	if !hasWarn(issues, `может быть "scalex"?`) {
		t.Fatalf("для scale_x должна остаться буквенная подсказка scalex: %+v", issues)
	}
}

// Предел глубины у обхода есть, и он не должен быть тесным. Живые деревья
// импорта вкладывают панель в строку, строку в колонку и так далее — если
// предел ужать, опечатка в глубоком дереве снова станет невидимой, причём
// молча: жалобы не будет ни на узел, ни на сам предел.
func TestОпечаткаВОбычноГлубокомДеревеЛовится(t *testing.T) {
	const этажей = 16
	tree := `{"kind":"buton","text":"Дальше"}`
	for i := 0; i < этажей; i++ {
		tree = `{"kind":"panel","children":[` + tree + `]}`
	}
	if !hasWarn(uiTree(t, tree), `kind="buton"`) {
		t.Fatalf("на %d-м этаже опечатка перестала ловиться — предел обхода ужали", этажей)
	}
}

// КОМАНДА, НАПИСАННАЯ С ЗАГЛАВНОЙ ИЛИ ЧУЖОЙ БУКВОЙ, — ВСЁ РАВНО КОМАНДА.
//
// Замер на живом компиляторе: `Actor мира emotion=happy`, `ACTOR …`, `Bg /путь`
// и `сlear all=1` (кириллическая «с») собираются в РЕПЛИКУ и печатаются
// игроку, а валидатор молчал — распознаватель ищет строчное ASCII-слово и
// такие строки не рассматривал вовсе. Заглавную ставит автозамена редактора,
// кириллический двойник приезжает копипастой из переписки; цена у обеих одна.
func TestValidate_MistypedOpCaseAndAlphabet(t *testing.T) {
	for _, tc := range []struct{ text, want string }{
		{"Actor мира emotion=happy", "actor"},
		{"ACTOR мира emotion=happy", "actor"},
		{"\u0430ctor мира emotion=happy", "actor"}, // кириллическая «а» первой
		{"\u0441lear all=1", "clear"},              // кириллическая «с»
		{"Bg /content/bg/room.png", "bg"},
	} {
		d := parse(t, `{"script":[{"op":"say","text":`+strconv.Quote(tc.text)+`}]}`)
		iss := Validate(d)
		if !hasError(iss, "написанная как") {
			t.Errorf("%q: строка-команда прошла молча — она уедет игроку репликой: %v", tc.text, iss)
			continue
		}
		if !hasError(iss, tc.want) {
			t.Errorf("%q: не названа настоящая команда %q: %v", tc.text, tc.want, iss)
		}
	}
}

// ПРОЗА НЕ ДОЛЖНА СТАНОВИТЬСЯ НАХОДКОЙ. Правило узкое намеренно: слово обязано
// ПОСЛЕ приведения точно совпасть с именем команды И строка обязана иметь форму
// команды. Ложная ошибка здесь дороже пропуска — гейт публикации отказывает по
// ошибкам, то есть ложное срабатывание запирает автору сохранение.
func TestValidate_ProseNearOpNamesStaysQuiet(t *testing.T) {
	for _, text := range []string{
		"Save the world",  // имя команды, но нет формы команды
		"Загрузка 5 из 7", // кириллица, не приводится к имени
		"Text me later",   // форма отсутствует
		"\u041a\u043b\u0430\u0440\u0430 = \u0441\u0435\u0441\u0442\u0440\u0430", // «Клара = сестра»: форма есть, слово не команда
	} {
		d := parse(t, `{"script":[{"op":"say","text":`+strconv.Quote(text)+`}]}`)
		if iss := Validate(d); hasError(iss, "написанная как") {
			t.Errorf("%q: проза принята за команду: %v", text, iss)
		}
	}
}

// СЛУЖЕБНОЕ СЛОВО, НЕ СТАВШЕЕ КОНСТРУКЦИЕЙ, НЕ УЕЗЖАЕТ ИГРОКУ.
//
// Условие пишется однострочником (`if сила > 5 -> метка`) или блоком
// (`if сила > 5 {`). Строка без того и другого не разбирается вовсе и молча
// превращается в реплику: замер 05.09 — `if сила > ` уехал текстом «if сила >»,
// и ни компилятор, ни проверка слова этого не сказали.
//
// Проверка узкая намеренно: слово стоит первым и строчной латиницей. Замерено
// на живом корпусе — 102 файла репозитория (70 126 реплик) и 144 главы живой
// студии (112 331 реплика), ни одного совпадения.
func TestDanglingKeywordIsAnError(t *testing.T) {
	висячие := []string{
		"if сила > ",
		"if gold > 5",
		"else",
		"end",
		"while запас > 0",
		"for предмет в сумке",
		"func считать(",
		"return",
		"include \"механики.lvns\"",
	}
	for _, line := range висячие {
		if danglingKeyword(line) == "" {
			t.Errorf("%q не опознана как повисшая конструкция — уедет игроку репликой", line)
		}
	}

	проза := []string{
		"Ифигения молчала.",
		"If she comes, we leave.",    // англ. проза начинается с заглавной
		"Endless night над городом.", // слово длиннее ключевого, без пробела
		"иначе он бы не пришёл",
		"Форма отпечаталась на снегу.", // кириллическое «Фор», не latin for
		"returned to the city",         // не ровно ключевое слово
		"— И что теперь?",
		"end.", // точка сразу — это уже не конструкция
	}
	for _, line := range проза {
		if kw := danglingKeyword(line); kw != "" {
			t.Errorf("проза %q принята за конструкцию %q — ложная тревога дороже пропуска: "+
				"конвейер с гейтом «0 предупреждений» выключат целиком", line, kw)
		}
	}
}

// ГЛАВА БЕЗ ЕДИНОЙ МЕТКИ — предупреждение, а не молчание.
//
// Продолжение главы после правки держится на якоре: сохранение помнит
// ближайшую метку и шаги от неё. Меток нет — якорю не за что зацепиться, и
// правка возвращает читающих в начало. Автор об этом не узнает ниоткуда:
// глава компилируется и играется, потеря видна только игроку и только потом.
func TestLongChapterWithoutLabelsIsWarned(t *testing.T) {
	длинная := &Doc{Scene: "глава"}
	for i := 0; i < 60; i++ {
		длинная.Script = append(длинная.Script, Cmd{"op": "say", "text": "реплика"})
	}
	var нашли string
	for _, is := range Validate(длинная) {
		if is.Sev == SevWarning && strings.Contains(is.Msg, "ни одной метки") {
			нашли = is.Msg
		}
	}
	if нашли == "" {
		t.Error("длинная глава без меток прошла молча — автор узнает о потере от игроков")
	}

	// Одна метка — и якорю есть за что держаться: предупреждать не о чем.
	сМеткой := &Doc{Scene: "глава", Script: append(
		[]Cmd{{"op": "label", "id": "сцена1"}}, длинная.Script...)}
	for _, is := range Validate(сМеткой) {
		if is.Sev == SevWarning && strings.Contains(is.Msg, "ни одной метки") {
			t.Errorf("глава с меткой получила предупреждение о метках: %s", is.Msg)
		}
	}

	// Короткую главу перечитать не жалко — молчим.
	короткая := &Doc{Scene: "глава"}
	for i := 0; i < 10; i++ {
		короткая.Script = append(короткая.Script, Cmd{"op": "say", "text": "реплика"})
	}
	for _, is := range Validate(короткая) {
		if is.Sev == SevWarning && strings.Contains(is.Msg, "ни одной метки") {
			t.Errorf("короткая глава получила предупреждение: %s", is.Msg)
		}
	}
}
