#!/usr/bin/env bash
# qa/run-all.sh — ОДНА команда после изменений: «ничего не сломалось?»
#
#   qa/run-all.sh            ОБА набора: EditMode (юнит+контракт+соук) и
#                            PlayMode (сцена, бут, смоук) — цикл после правок
#   qa/run-all.sh --editmode только EditMode: быстрая итерация, пока пишешь
#   qa/run-all.sh --playmode только PlayMode
#   qa/run-all.sh --device   + сборка dev-APK и смоук на эмуляторе против
#                            локального сервера :8099 (медленно, ~15 мин)
#   qa/run-all.sh --filter "Fixture1;Fixture2"   только выбранные фикстуры
#
# Выход 0 = зелёно. Отчёты в qa/reports/<штамп>-runall/.
#
# ОТКРЫТЫЙ РЕДАКТОР НЕ МЕШАЕТ: TestHost — отдельный проект, и игра, открытая
# в редакторе, батчу не помеха. Мешают только двое: другой batchmode на том же
# TestHost (его ждём) и редактор, открытый НА САМОМ TestHost (тогда выходим с
# объяснением — batchmode такой проект не возьмёт).
set -u -o pipefail

UNITY="/Applications/Unity/Hub/Editor/6000.4.5f1/Unity.app/Contents/MacOS/Unity"
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
STAMP="$(date +%Y%m%d-%H%M%S)"
OUT="$REPO_ROOT/qa/reports/$STAMP-runall"

# УБОРКА ЗА СОБОЙ. Один прогон оставляет ~130 МБ (лог PlayMode — почти весь
# объём), и за полгода их набралось 644 штуки на 9,5 ГБ: диск кончился прямо
# посреди работы. Отчёт нужен, пока разбираешь ПОСЛЕДНЮЮ поломку; десяти хватает
# с запасом. Чистим ДО прогона, чтобы место освободилось раньше, чем понадобится.
ls -t "$REPO_ROOT/qa/reports" 2>/dev/null | tail -n +11 | while read -r old_run; do
  rm -rf "$REPO_ROOT/qa/reports/${old_run:?}"
done
mkdir -p "$OUT"

DEVICE=0
FILTER=""
RUN_EDIT=1
RUN_PLAY=1
while [ $# -gt 0 ]; do
  case "$1" in
    --device) DEVICE=1; shift ;;
    --editmode) RUN_PLAY=0; shift ;;
    --playmode) RUN_EDIT=0; shift ;;
    --filter) FILTER="$2"; shift 2 ;;
    *) echo "неизвестный аргумент: $1"; exit 2 ;;
  esac
done

fail=0
log() { echo "[$(date +%H:%M:%S)] $*"; }

# Другой batchmode на TestHost — ждём его: прогон-в-прогон роняет оба.
waited=0
while pgrep -f "batchmode.*TestHost" >/dev/null 2>&1; do
  [ "$waited" = 0 ] && log "TestHost занят другим прогоном — жду…"
  sleep 5; waited=$((waited + 5))
  if [ "$waited" -ge 1800 ]; then
    echo "FAIL: TestHost занят полчаса — что-то повисло"; exit 1
  fi
done

# РЕДАКТОР, ОТКРЫТЫЙ ИМЕННО НА TestHost, — а не любой открытый Unity.
#
# Раньше здесь стояло «замок на месте И где-то запущен Unity». Обе половины
# врут по отдельности: замок остаётся лежать после batchmode-прогона, а Unity у
# автора почти всегда открыт — на ИГРЕ, а не на стенде. Вместе они давали
# отказ «закрой TestHost» человеку, у которого TestHost не открыт, и глушили
# прогоны на весь рабочий день.
#
# Спрашиваем прямо: есть ли процесс редактора, которому передан путь стенда.
if pgrep -f -- "-projectpath.*unity/TestHost" >/dev/null 2>&1 \
   || pgrep -f -- "-projectPath.*unity/TestHost" >/dev/null 2>&1; then
  if ! pgrep -f "batchmode.*TestHost" >/dev/null 2>&1; then
    echo "FAIL: TestHost открыт в редакторе — закрой ЕГО (игру можно не трогать)"; exit 1
  fi
fi

# ── 0a. СТРАЖИ ФОРМЫ И ЯЗЫКА (Go) ──────────────────────────────────────────
# Их десятки, и держат они то, чего Unity-прогон не видит вовсе: дубли в C#,
# согласие документации с кодом, единый диалект двух компиляторов, набор команд
# у двух рендереров браузера, читаемость диагностики. Цикл назывался «ничего не
# сломалось?» и при этом их не запускал — можно было увидеть зелёное, не
# проверив ни одного.
#
# LVN_REQUIRE_NODE: на машине с node стражам языка запрещено пропускаться
# молча; без node они честно скипнутся сами.
# СТРАЖИ ЧИТАЮТ ИСХОДНИКИ, А ИСХОДНИКИ ПРАВЯТ ПРЯМО СЕЙЧАС.
#
# Го-фаза идёт три с половиной минуты и всё это время читает .cs, .js и
# манифесты. Правка, попавшая в середину чтения, даёт красный прогон, который
# ничего не значит: страж увидел половину старого файла и половину нового.
# 01.09 так сгорело четыре прогона подряд — и каждый раз лечилось «не трогай
# репозиторий, пока идёт». Правило, которое надо помнить, — не механизм.
#
# Снимок рабочего дерева (только то, что видит git: отслеженное плюс новое,
# кроме игнорируемого) стоит две секунды и тридцать шесть мегабайт. Стражи
# ищут корень сами, вверх по дереву от своего файла, — в снимке они находят
# снимок и читают согласованное состояние, что бы ни делали снаружи.
#
# quotePath=false обязателен: по умолчанию git ЭКРАНИРУЕТ не-ASCII имена
# ("\320\236\320\264…"), rsync такого файла не находит, и снимок молча не
# выходит — прогон продолжается по живому дереву, то есть ровно без той защиты,
# ради которой заведён. Поймано 02.09 первым же файлом с кириллицей в имени.
GO_ROOT="$REPO_ROOT"
if command -v go >/dev/null 2>&1 && command -v rsync >/dev/null 2>&1; then
  GO_SNAP="$(mktemp -d)/repo"
  if mkdir -p "$GO_SNAP" \
     && (cd "$REPO_ROOT" && git -c core.quotePath=false ls-files -c -o --exclude-standard 2>/dev/null > "$GO_SNAP/../files.txt") \
     && [ -s "$GO_SNAP/../files.txt" ] \
     && rsync -a --files-from="$GO_SNAP/../files.txt" "$REPO_ROOT/" "$GO_SNAP" 2>/dev/null; then
    GO_ROOT="$GO_SNAP"
    log "go: снимок дерева ($(wc -l < "$GO_SNAP/../files.txt" | tr -d ' ') файлов) — правки во время прогона его не задевают"
  else
    log "go: снимок не вышел — читаем рабочее дерево как раньше"
  fi
fi

# ГЕЙТ СОДЕРЖИМОГО ЗОВЁТСЯ ОТСЮДА, а не живёт сам по себе. В нём компиляция,
# строгая проверка и обход всех ветвей раздаваемых образцов — и до этой ночи
# каждая из трёх стояла только в CI.
if [ -x "$REPO_ROOT/qa/lvns-gate.sh" ]; then
  gout_gate="$OUT/lvns-gate.log"
  if "$REPO_ROOT/qa/lvns-gate.sh" >"$gout_gate" 2>&1; then
    log "гейт содержимого: $(tail -2 "$gout_gate" | head -1)"
  else
    log "FAIL: гейт содержимого — подробности: $gout_gate"
    fail=1
  fi
fi

# ИМЯ ФАЙЛА НА ДВУХ МАШИНАХ. Стенд поднимает настоящий сервер над обычным и над
# регистрозависимым томом и публикует в оба одну главу: расхождение регистра
# обязано всплыть у автора, а не у игрока. Проверка живёт в цикле, а не в одной
# голове, — иначе через месяц ей же и не поверишь.
if [ -x "$REPO_ROOT/qa/asset-case-check.sh" ]; then
  gout_case="$OUT/asset-case.log"
  if "$REPO_ROOT/qa/asset-case-check.sh" >"$gout_case" 2>&1; then
    log "имя файла: $(tail -1 "$gout_case")"
  else
    log "FAIL: имя файла — подробности: $gout_case"
    fail=1
  fi
fi

# ТИХИЕ ОТКАЗЫ. Корпус того, чем язык болеет на стороне автора: строка, которую
# разбор не узнал, становится репликой и уезжает игроку. Стенд держит границу
# вслух — что ловится и что пока нет — и падает, когда ловившееся замолкает.
if [ -x "$REPO_ROOT/qa/silent-traps-check.sh" ]; then
  gout_traps="$OUT/silent-traps.log"
  if "$REPO_ROOT/qa/silent-traps-check.sh" >"$gout_traps" 2>&1; then
    log "тихие отказы: $(tail -1 "$gout_traps")"
  else
    log "FAIL: тихие отказы — подробности: $gout_traps"
    fail=1
  fi
fi

# ПОДПИСИ ИНТЕРФЕЙСА. Реестр ключей движка — шаблон словаря для автора; стенд
# проверяет, что отчёт «что останется английским» находит забытое и не
# выдумывает долга там, где подпись названа полем секции или тройкой чисел.
# ЭКСПОРТ ОТДАЁТ ИГРУ, А НЕ ЧУЖИЕ ДАННЫЕ. Внутрь архива уезжает весь каталог
# контента, а рядом с ним живут кошельки и сейвы: один неверный фильтр — и казна
# игроков в файле, который автор перешлёт кому угодно.
if [ -x "$REPO_ROOT/qa/export-check.sh" ]; then
  gout_ex="$OUT/export.log"
  if "$REPO_ROOT/qa/export-check.sh" >"$gout_ex" 2>&1; then
    log "экспорт: $(tail -1 "$gout_ex")"
  else
    log "FAIL: экспорт — подробности: $gout_ex"
    fail=1
  fi
fi

# ГРУППА ОПЫТА НЕ ПЛЫВЁТ. A/B идёт по живому, и доверие к делению — условие
# того, что отчёт сравнивает варианты, а не шум.
if [ -x "$REPO_ROOT/qa/experiment-assign-check.sh" ]; then
  gout_ea="$OUT/experiment-assign.log"
  if "$REPO_ROOT/qa/experiment-assign-check.sh" >"$gout_ea" 2>&1; then
    log "деление опыта: $(tail -1 "$gout_ea")"
  else
    log "FAIL: деление опыта — подробности: $gout_ea"
    fail=1
  fi
fi

# НАГРАДА ЗА ДЕНЬ, А НЕ ЗА НАЖАТИЕ. Двойное нажатие и двадцать одновременных
# запросов не должны размножать награду, а серия обязана считаться днями.
if [ -x "$REPO_ROOT/qa/daily-reward-check.sh" ]; then
  gout_dr="$OUT/daily-reward.log"
  if "$REPO_ROOT/qa/daily-reward-check.sh" >"$gout_dr" 2>&1; then
    log "награда дня: $(tail -1 "$gout_dr")"
  else
    log "FAIL: награда дня — подробности: $gout_dr"
    fail=1
  fi
fi

# ЧУЖОЙ КОШЕЛЁК НЕ ОТКРЫТЬ. Единственное место в движке, где лежат настоящие
# деньги игрока: стенд заводит двоих и пробует добраться до чужих денег номером
# в запросе, в заголовке, в теле, склейкой пропуска и поддельным чеком.
if [ -x "$REPO_ROOT/qa/wallet-isolation-check.sh" ]; then
  gout_wi="$OUT/wallet-isolation.log"
  if "$REPO_ROOT/qa/wallet-isolation-check.sh" >"$gout_wi" 2>&1; then
    log "кошельки: $(tail -1 "$gout_wi")"
  else
    log "FAIL: кошельки — подробности: $gout_wi"
    fail=1
  fi
fi

# МУСОР НЕ РОНЯЕТ СЕРВЕР. Сервер один на всех: паника в обработчике — это игра,
# пропавшая у всех сразу. Стенд шлёт 150 кривых тел, странные пути, тело в 10 МБ,
# полсотни запросов разом и два десятка обрывов — и спрашивает «ты жив?».
if [ -x "$REPO_ROOT/qa/fuzz-check.sh" ]; then
  gout_fz="$OUT/fuzz.log"
  if "$REPO_ROOT/qa/fuzz-check.sh" >"$gout_fz" 2>&1; then
    log "мусор: $(tail -1 "$gout_fz")"
  else
    log "FAIL: мусор — подробности: $gout_fz"
    fail=1
  fi
fi

# АДМИНКА ЗАКРЫТА НА ЗАМОК. Список ручек берётся из кода — новая появляется в
# проверке сама. Стенд стучится с улицы в каждую, проверяет, что отказ означает
# бездействие, и что верный пропуск при этом пускает.
if [ -x "$REPO_ROOT/qa/admin-lock-check.sh" ]; then
  gout_al="$OUT/admin-lock.log"
  if "$REPO_ROOT/qa/admin-lock-check.sh" >"$gout_al" 2>&1; then
    log "замок админки: $(tail -1 "$gout_al")"
  else
    log "FAIL: замок админки — подробности: $gout_al"
    fail=1
  fi
fi

# ПРИВАТНОЕ НЕ УТЕКАЕТ. Каталог контента раздаётся статикой, а внутри него —
# база с аккаунтами, кошельки, сейвы и учётки панели. Стенд ломится к ним
# восемнадцатью способами и требует, чтобы публичное при этом отдавалось.
if [ -x "$REPO_ROOT/qa/private-paths-check.sh" ]; then
  gout_pp="$OUT/private-paths.log"
  if "$REPO_ROOT/qa/private-paths-check.sh" >"$gout_pp" 2>&1; then
    log "приватное: $(tail -1 "$gout_pp")"
  else
    log "FAIL: приватное — подробности: $gout_pp"
    fail=1
  fi
fi

# СНИМОК РАЗВОРАЧИВАЕТСЯ. Бэкап существует ради одного дня — проверяем не
# «архив создался», а «из него встаёт сервер и игрок находит себя».
if [ -x "$REPO_ROOT/qa/restore-check.sh" ]; then
  gout_rs="$OUT/restore.log"
  if "$REPO_ROOT/qa/restore-check.sh" >"$gout_rs" 2>&1; then
    log "снимок: $(tail -1 "$gout_rs")"
  else
    log "FAIL: снимок — подробности: $gout_rs"
    fail=1
  fi
fi

# КАТАЛОГ: «ИГР НЕТ» И «У МЕНЯ БЕДА» — РАЗНЫЕ ОТВЕТЫ. Нечитаемый манифест не
# должен уезжать игроку пустым каталогом: клиент такой ответ принимал и затирал
# офлайновую копию.
if [ -x "$REPO_ROOT/qa/empty-manifest-check.sh" ]; then
  gout_em="$OUT/empty-manifest.log"
  if "$REPO_ROOT/qa/empty-manifest-check.sh" >"$gout_em" 2>&1; then
    log "каталог: $(tail -1 "$gout_em")"
  else
    log "FAIL: каталог — подробности: $gout_em"
    fail=1
  fi
fi

# РАБОТА АВТОРА НЕ ОСТАНАВЛИВАЕТ ИГРУ. Студия и игра живут на одном сервере:
# экспорт проекта, перезаливка арта (после неё пересчитываются хэши всего
# дерева) и публикация глав пачкой идут на тех же ядрах, что отдают главы
# играющим. Стенд меряет время ответа игроку в покое и во время каждой работы.
if [ -x "$REPO_ROOT/qa/author-load-check.sh" ]; then
  gout_al="$OUT/author-load.log"
  if "$REPO_ROOT/qa/author-load-check.sh" >"$gout_al" 2>&1; then
    log "нагрузка автора: $(tail -1 "$gout_al")"
  else
    log "FAIL: нагрузка автора — подробности: $gout_al"
    fail=1
  fi
fi

# В ИГРУ УЕЗЖАЕТ ИГРА. В каталоге контента копится авторская кухня — исходники
# .lvns, бэкапы манифеста от деплоя, присланные архивы, черновики редактора, — и
# она попадала и в индекс версий (то есть тревожила каждого играющего сменой
# версии контента), и в офлайновый набор игры. Стенд проверяет обе стороны:
# кухни в наборе нет, игра в нём вся, а исходник по-прежнему отдаётся панели.
if [ -x "$REPO_ROOT/qa/game-only-check.sh" ]; then
  gout_go="$OUT/game-only.log"
  if "$REPO_ROOT/qa/game-only-check.sh" >"$gout_go" 2>&1; then
    log "набор игры: $(tail -1 "$gout_go")"
  else
    log "FAIL: набор игры — подробности: $gout_go"
    fail=1
  fi
fi

# КАЖДАЯ ГЛАВА ПРОХОДИТСЯ ДО КОНЦА. Обход по структуре (walk) отвечает на
# вопрос «есть ли путь» по схеме; петлю, из которой поток не выходит, видно
# только исполнением. Стенд играет каждую живую главу настоящим плеером
# песочницы, выбирая случайный вариант, и требует, чтобы история закончилась
# сама. Границы названы: витрины без финала и жанры на кликах он не играет.
if [ -x "$REPO_ROOT/qa/playthrough-check.sh" ]; then
  gout_pt="$OUT/playthrough.log"
  if "$REPO_ROOT/qa/playthrough-check.sh" >"$gout_pt" 2>&1; then
    log "прохождение глав: $(tail -1 "$gout_pt")"
  else
    log "FAIL: прохождение глав — подробности: $gout_pt"
    fail=1
  fi
fi

# ОТЧЁТ ПОКАЗЫВАЕТ ТО, ЧТО СЛУЧИЛОСЬ. По отчётам принимают решения о продукте,
# и ошибка в них не падает и не кричит — просто показывает другое число, и
# разговор идёт по нему. Стенд шлёт события так же, как их шлёт игра, и требует
# от сервера тех же цифр обратно.
if [ -x "$REPO_ROOT/qa/report-truth-check.sh" ]; then
  gout_rt="$OUT/report-truth.log"
  if "$REPO_ROOT/qa/report-truth-check.sh" >"$gout_rt" 2>&1; then
    log "правда отчёта: $(tail -1 "$gout_rt")"
  else
    log "FAIL: правда отчёта — подробности: $gout_rt"
    fail=1
  fi
fi

# НАД ОДНОЙ НОВЕЛЛОЙ РАБОТАЮТ НЕСКОЛЬКО РУК: автор в панели, соавтор во второй
# вкладке, ИИ-агент по API. Все пишут главу одной дверью, и пока сервер не
# спрашивал версию, побеждал последний записавший — молча. Стенд разыгрывает
# двоих авторов по проводу и требует, чтобы работа первого уцелела.
if [ -x "$REPO_ROOT/qa/coauthor-check.sh" ]; then
  gout_ca="$OUT/coauthor.log"
  if "$REPO_ROOT/qa/coauthor-check.sh" >"$gout_ca" 2>&1; then
    log "двое авторов: $(tail -1 "$gout_ca")"
  else
    log "FAIL: двое авторов — подробности: $gout_ca"
    fail=1
  fi
fi

# ЖЁСТКОЕ ВЫКЛЮЧЕНИЕ И ДЕНЬГИ. Сервер не всегда останавливают вежливо: OOM,
# перезагрузка хостинга, kill -9 в разгар распродажи. В этот момент идут деньги
# игроков, и каждая операция — несколько записей в базе. Стенд убивает сервер
# невежливо посреди потока начислений и проверяет, что после подъёма нет ни
# одной полуоперации, подтверждённое на месте, а повтор не удваивает.
if [ -x "$REPO_ROOT/qa/crash-durability-check.sh" ]; then
  gout_cd="$OUT/crash-durability.log"
  if "$REPO_ROOT/qa/crash-durability-check.sh" >"$gout_cd" 2>&1; then
    log "жёсткое выключение: $(tail -1 "$gout_cd")"
  else
    log "FAIL: жёсткое выключение — подробности: $gout_cd"
    fail=1
  fi
fi

# ЦЕНА ПЕРЕИЗДАНИЯ. Публикация, ничего не изменившая, не должна стоить
# играющим ничего: их клиент решает «мир изменился» по общей версии контента, а
# та двигалась от любой записи манифеста — включая холостую. Стенд проверяет и
# опору под этим: компилятор обязан давать одинаковые байты из разных каталогов
# и в разных процессах, иначе перевыпуск переливает игру целиком.
if [ -x "$REPO_ROOT/qa/republish-cost-check.sh" ]; then
  gout_rp="$OUT/republish-cost.log"
  if "$REPO_ROOT/qa/republish-cost-check.sh" >"$gout_rp" 2>&1; then
    log "цена переиздания: $(tail -1 "$gout_rp")"
  else
    log "FAIL: цена переиздания — подробности: $gout_rp"
    fail=1
  fi
fi

# ЦЕЛОСТНОСТЬ ДОКАЧКИ. Оборванная загрузка продолжается «Range: bytes=N-», и
# если файл между заходами заменили, к голове прежней редакции приклеивается
# хвост новой. Стенд поднимает настоящий сервер и проверяет, что условие
# If-Range это ловит (а ETag для условия вообще ставится).
if [ -x "$REPO_ROOT/qa/resume-integrity-check.sh" ]; then
  gout_rz="$OUT/resume-integrity.log"
  if "$REPO_ROOT/qa/resume-integrity-check.sh" >"$gout_rz" 2>&1; then
    log "докачка: $(tail -1 "$gout_rz")"
  else
    log "FAIL: докачка — подробности: $gout_rz"
    fail=1
  fi
fi

# ЧЕСТНОСТЬ ИНДИКАТОРА ЗАГРУЗКИ. Доля считается планом, а не догадкой о весе
# непочатых файлов; «осталось» — тем же планом; вставшая загрузка называется
# вставшей. Стенд считает обе формулы на одном наборе файлов — Unity не нужен.
if [ -x "$REPO_ROOT/qa/download-progress-check.sh" ]; then
  gout_dl="$OUT/download-progress.log"
  if "$REPO_ROOT/qa/download-progress-check.sh" >"$gout_dl" 2>&1; then
    log "индикатор загрузки: $(tail -1 "$gout_dl")"
  else
    log "FAIL: индикатор загрузки — подробности: $gout_dl"
    fail=1
  fi
fi

if [ -x "$REPO_ROOT/qa/ui-words-check.sh" ]; then
  gout_words="$OUT/ui-words.log"
  if "$REPO_ROOT/qa/ui-words-check.sh" >"$gout_words" 2>&1; then
    log "подписи интерфейса: $(tail -1 "$gout_words")"
  else
    log "FAIL: подписи интерфейса — подробности: $gout_words"
    fail=1
  fi
fi

# ПОЛ ПОКРЫТИЯ Go — та же мера, что у Unity, и по той же причине. Фаза Go
# смотрела только на провалы: удалённый файл стражей или пакет, выпавший из
# `./...`, не падает — его просто нет, и прогон остаётся зелёным. Числа
# сняты 02.09 (пропущенные тесты считаются: пол — про СУЩЕСТВОВАНИЕ проверки,
# а не про её исполнение). Числа могут только расти.
FLOOR_GO_tools_lvnconv=708
FLOOR_GO_server=312
FLOOR_NODE_PANEL=110
FLOOR_NODE_GRAMMAR=31

# Один судья на все полы: фаз четыре, и каждая считала по-своему ровно до тех
# пор, пока их было две.
floor_check() { # $1 = имя, $2 = сколько прошло, $3 = пол
  [ -n "$3" ] || return 0
  if [ "$2" -lt "$3" ]; then
    log "  $1: ТЕСТОВ МЕНЬШЕ ПОЛА: $2 при $3 — проверки не упали, а ИСЧЕЗЛИ"
    fail=1
  elif [ "$2" -gt "$3" ]; then
    log "  $1: $2 тестов (пол $3 — поднимите)"
  fi
}

# ФОРМАТ ПРОВЕРЯЕТСЯ ЗДЕСЬ, А НЕ ТОЛЬКО В CI.
#
# gofmt стоял в ci.yml и нигде больше. За одну ночь два файла разъехались и
# никто этого не заметил: локальный цикл формат не смотрел, а включён ли
# workflow на площадке — отсюда не видно (см. таблицу решений в
# docs/world-position.md). Проверка стоит миллисекунды, а её отсутствие
# означает красный CI на ровном месте.
#
# СУДИТ ВЫВОД, А НЕ КОД ВОЗВРАТА: `gofmt -l` выходит нулём и когда чисто, и
# когда нет. На этом я попался в тот же вечер.
if command -v gofmt >/dev/null 2>&1; then
  fmt_out="$(cd "$GO_ROOT" && gofmt -l server tools/lvnconv 2>/dev/null)"
  if [ -n "$fmt_out" ]; then
    log "gofmt: НЕ ОТФОРМАТИРОВАНО — $(echo "$fmt_out" | tr '\n' ' ')"
    fail=1
  else
    log "gofmt: чисто"
  fi
fi

if command -v go >/dev/null 2>&1; then
  for mod in tools/lvnconv server; do
    # -count=1 обязателен: стражи читают C#, JS и манифесты — файлы, которых
    # кэш go test не видит. Без флага правка в Unity ломает инвариант, а прогон
    # отвечает «ok (cached)»: страж молчит ровно тогда, когда должен кричать.
    # ВЕТ БЫЛ ТОЛЬКО В CI. Он ловит то, что компилятор пропускает: неверный
    # Printf, копию мьютекса, недостижимый код. Проверка стоит секунды, а её
    # отсутствие означает красный CI на ровном месте — если он вообще включён,
    # чего отсюда не видно.
    vout="$(cd "$GO_ROOT/$mod" && go vet ./... 2>&1)"
    if [ -n "$vout" ]; then
      log "go vet $mod: ЗАМЕЧАНИЯ — $(echo "$vout" | head -3 | tr '\n' ' ')"
      fail=1
    else
      log "go vet $mod: чисто"
    fi
    log "go test $mod"
    gout="$OUT/go-$(echo "$mod" | tr / -).log"
    if command -v node >/dev/null 2>&1; then
      (cd "$GO_ROOT/$mod" && LVN_REQUIRE_NODE=1 go test -count=1 -v ./... >"$gout" 2>&1) \
        || { log "FAIL: go test $mod — подробности: (cd $mod && go test ./...)"; fail=1; }
    else
      (cd "$GO_ROOT/$mod" && go test -count=1 -v ./... >"$gout" 2>&1) \
        || { log "FAIL: go test $mod — подробности: (cd $mod && go test ./...)"; fail=1; }
    fi
    ran=$(grep -c '^--- \(PASS\|FAIL\|SKIP\)' "$gout" 2>/dev/null || echo 0)
    eval "floor=\${FLOOR_GO_$(echo "$mod" | tr /. __)}"
    floor_check "$mod" "$ran" "$floor"
  done
else
  log "WARN: go не найден — стражи формы и языка не проверены"
fi

# ── 0b. СТРАЖИ ВЕБ-ПОЛОВИНЫ (node) ─────────────────────────────────────────
# Веб-плеер и экспортированная игра — такие же рантаймы языка, как движок, и
# их правила надо проверять тем же циклом. Жили эти тесты только в CI-джобе
# панели, а она до них не доходила: перед ними стоит линтер, и он красный.
# Проверка, которую никто не гоняет, — не проверка.
if command -v node >/dev/null 2>&1; then
  log "node: упаковка экспорта"
  out=$(node "$REPO_ROOT/conformance/export-check.mjs" "$REPO_ROOT/panel/public/play" 2>&1) \
    && [ "$out" = "[]" ] \
    || { log "FAIL: упаковка экспорта — $out"; fail=1; }
  if [ -d "$REPO_ROOT/panel/node_modules" ]; then
    log "node: тесты панели"
    nout="$OUT/node-panel.log"
    # ЛИНТ ПАНЕЛИ ТОЖЕ ЖИЛ ТОЛЬКО В CI — и был КРАСЕН: 128 ошибок, из них 116
    # от разбора сгенерированной Go прослойки, которую исключение обходило по
    # старому пути. Гейт, красный по построению, не охраняет ничего; за ним и
    # спрятался живой дефект (рекурсивный report в ScriptSection).
    #
    # Сборка панели (npm run build) остаётся ТОЛЬКО в CI намеренно: она тянет
    # компиляцию wasm, а это минуты на каждый прогон. Исключение названо здесь,
    # чтобы оно было решением, а не забывчивостью.
    lout="$OUT/node-panel-lint.log"
    (cd "$REPO_ROOT/panel" && npm run lint --silent >"$lout" 2>&1) \
      || { log "FAIL: линт панели — подробности: $lout"; fail=1; }
    (cd "$REPO_ROOT/panel" && npm test --silent >"$nout" 2>&1) \
      || { log "FAIL: npm test в panel/ — подробности: (cd panel && npm test)"; fail=1; }
    ran=$(sed -n 's/.*Tests  *\([0-9][0-9]*\) passed.*/\1/p' "$nout" | tail -1)
    floor_check "панель" "${ran:-0}" "$FLOOR_NODE_PANEL"
  else
    log "WARN: panel/node_modules нет — тесты панели пропущены (npm i --prefix panel)"
  fi
  # СИНХРОН ГРАММАТИКИ БЫЛ ТОЛЬКО В CI — последний из четырёх пробелов,
  # найденных сверкой шагов workflow с фазами цикла.
  #
  # Договор языка живёт в ОДНОМ файле — tools/lvn-lang/src/grammar.json, — а
  # grammar.js из него генерируется. Отстань генерат, и редактор подсвечивает
  # один язык, а компилятор разбирает другой.
  #
  # ГЕНЕРИРУЕМ В СТОРОНУ, А НЕ В ДЕРЕВО. Приём «сгенерировать поверх и сравнить»
  # (так делает CI, где дерево одноразовое) здесь ВРЕДЕН дважды: он оставляет
  # правки в рабочем дереве и, главное, ЧИНИТ вшитую копию расширения — а её
  # побайтово сторожит отдельный тест. Запусти генератор до него, и страж
  # увидит согласие, которое сам же прогон и навёл. Замерено: испорченная на
  # один пробел копия после генератора стала неотличима от источника.
  if [ -f "$REPO_ROOT/tools/lvn-lang/scripts/gen.mjs" ] && command -v node >/dev/null 2>&1; then
    GTMP="$(mktemp -d)"
    mkdir -p "$GTMP/tools/vscode-lvn/lib"
    cp -R "$REPO_ROOT/tools/lvn-lang" "$GTMP/tools/lvn-lang"
    cp -R "$REPO_ROOT/tools/vscode-lvn/lib/lvn-lang" "$GTMP/tools/vscode-lvn/lib/lvn-lang"
    if (cd "$GTMP/tools/lvn-lang" && node scripts/gen.mjs >/dev/null 2>&1); then
      gdiff=""
      for rel in tools/lvn-lang/src/grammar.js tools/vscode-lvn/lib/lvn-lang/grammar.js \
                 tools/vscode-lvn/lib/lvn-lang/grammar.json; do
        cmp -s "$GTMP/$rel" "$REPO_ROOT/$rel" || gdiff="$gdiff $rel"
      done
      if [ -z "$gdiff" ]; then
        log "грамматика: генерат в синхроне с источником"
      else
        log "FAIL: генерат грамматики отстал —$gdiff — (cd tools/lvn-lang && npm run gen) и закоммитьте"
        fail=1
      fi
    else
      log "WARN: генератор грамматики не отработал — синхрон не проверен"
    fi
    rm -rf "$GTMP"
  fi

  log "node: грамматика"
  gram="$OUT/node-grammar.log"
  (cd "$REPO_ROOT/tools/lvn-lang" && node --test >"$gram" 2>&1) \
    || { log "FAIL: node --test в tools/lvn-lang"; fail=1; }
  ran=$(sed -n 's/^# pass \([0-9][0-9]*\)$/\1/p' "$gram" | tail -1)
  floor_check "грамматика" "${ran:-0}" "$FLOOR_NODE_GRAMMAR"
else
  log "WARN: node не найден — веб-половина не проверена"
fi

# ── 0c. C# СОБИРАЕТСЯ (без Unity) ──────────────────────────────────────────
# Опечатка в рантайме до сих пор находилась ЗАПУСКОМ Unity — то есть через
# минуты, а иногда и не находилась вовсе (редактор занят, прогон отложен).
# Компилятор при этом лежит внутри самого редактора, и сборки движка на нём
# проверяются за секунды. Ступень стоит ПЕРЕД тестами намеренно: несобираемый
# код валит EditMode невнятной кучей, а здесь называет файл и строку.
log "C#: сборка пакетов"
cs="$OUT/csharp-check.log"
bash "$REPO_ROOT/qa/csharp-check.sh" >"$cs" 2>&1 \
  || { log "FAIL: C# не собирается — $(grep -m3 'error CS' "$cs" | head -3)"; fail=1; }
grep -c '^·' "$cs" >/dev/null 2>&1 && grep '^·' "$cs" | while read -r l; do log "  $l"; done

# ── 0d. ДВА КОМПИЛЯТОРА ЯЗЫКА СОБИРАЮТ ОДИНАКОВО ───────────────────────────
# Реализаций языка несколько, и расхождение между ними — главный структурный
# риск: оно не падает и не логируется, глава просто ведёт себя иначе у автора
# и у игрока. Прежние стражи ЧИТАЛИ исходник C# регулярками — то есть мерили
# похожесть текста. Здесь оба компилятора запускаются на живом корпусе, и
# сравнивается вывод. Первый же прогон нашёл 28 тихих расхождений в опциях
# выбора, включая цену и прибавку к отношениям.
log "сверка портов компилятора"
par="$OUT/compiler-parity.log"
bash "$REPO_ROOT/qa/compiler-parity.sh" >"$par" 2>&1 \
  || { log "FAIL: порты компилятора разошлись — подробности: $par"; fail=1; }
log "  $(head -1 "$par")"

# ── 0. Go-сервер для PlayMode-смоука (BootSmokeTests поднимает его сам) ─────
mkdir -p "$REPO_ROOT/qa/bin"
if command -v go >/dev/null 2>&1; then
  go build -o "$REPO_ROOT/qa/bin/lvnserver-test" "$REPO_ROOT/server" \
    || { log "WARN: go build сервера не удался — PlayMode-смоук скипнется"; }
fi

# ПОЛ ПОКРЫТИЯ: сколько тестов ОБЯЗАНО быть. Число может только расти.
#
# 02.09: тесты оболочки получили свою сборку — и перестали запускаться, потому
# что пакет не был объявлен `testables` в манифесте хоста. Пятьдесят проверок
# исчезли, а прогон остался ЗЕЛЁНЫМ: он смотрит на провалы, а не на состав.
# Исчезнувший тест не падает — его просто нет, и это худший вид красноты,
# потому что он выглядит как её отсутствие.
FLOOR_EDITMODE=2137
FLOOR_PLAYMODE=96

report_platform() { # $1 = имя, $2 = xml, $3 = пол
python3 - "$2" "$1" "$3" <<'PY'
import sys, xml.etree.ElementTree as ET
try:
    r = ET.parse(sys.argv[1]).getroot()
except Exception as e:
    print(f"  {sys.argv[2]}: нет результатов ({e})"); sys.exit(1)
total, passed, failed = r.get('total'), r.get('passed'), r.get('failed')
# ПРОПУСК — НЕ УСПЕХ, а отсутствие ответа. Тест, который «зелёный» только
# потому, что раскладки не хватило (нет Unity-пакетов, нет node, нет
# server/content), сообщает ровно ноль — а выглядит как проверенный. Считаем
# и НАЗЫВАЕМ их: пока их число видно, никто не примет тишину за подтверждение.
skipped = [tc for tc in r.iter('test-case') if tc.get('result') == 'Skipped']
tail = f", {len(skipped)} skipped" if skipped else ""
print(f"  {sys.argv[2]}: {passed}/{total} passed, {failed} failed{tail}")
for tc in skipped[:10]:
    why = (tc.findtext('reason/message') or '').strip().splitlines()
    print("    skipped:", tc.get('name'), "—", (why[0] if why else "причина не названа")[:80])
if len(skipped) > 10:
    print(f"    … и ещё {len(skipped) - 10}")
for tc in r.iter('test-case'):
    if tc.get('result') not in (None, 'Passed', 'Skipped'):
        print("   ", tc.get('result'), tc.get('fullname'))
floor = int(sys.argv[3]) if len(sys.argv) > 3 and sys.argv[3] else 0
if floor and int(total) < floor:
    print(f"    ТЕСТОВ МЕНЬШЕ ПОЛА: {total} при {floor} — проверки не упали, а ИСЧЕЗЛИ")
    sys.exit(1)
if floor and int(total) > floor:
    print(f"    (тестов стало больше: {total} при поле {floor} — поднимите пол)")
sys.exit(0 if failed == '0' else 1)
PY
}

# ── 1. EditMode: вся пирамида (юнит + контракт + соук) ──────────────────────
if [ "$RUN_EDIT" = 1 ]; then
log "EditMode-прогон…"
args=(-batchmode -nographics -projectPath "$REPO_ROOT/unity/TestHost"
      -runTests -testPlatform EditMode
      -testResults "$OUT/editmode.xml" -logFile "$OUT/editmode.log")
[ -n "$FILTER" ] && args+=(-testFilter "$FILTER")
"$UNITY" "${args[@]}" >/dev/null 2>&1
report_platform editmode "$OUT/editmode.xml" "$FLOOR_EDITMODE" || fail=1
fi

# ── 1b. PlayMode: интеграция (сцена, бут NovelApp против живого сервера) ─────
# НЕ опционально и не «когда вспомним»: EditMode не поднимает ни сцену, ни
# UI-панель, поэтому целый класс регрессий виден только здесь. Красный
# PlayMode-тест простоял незамеченным ровно потому, что цикл его не гонял.
if [ "$RUN_PLAY" = 1 ]; then
log "PlayMode-прогон…"
# БЕЗ -nographics, и это НЕ упущение. Флаг заставляет Unity поднять пустой
# графический слой (GraphicsDeviceType.Null), а пиксельные тесты сами себя
# пропускают, когда рисовать нечем, — и пропускали ВСЕГДА: девять проверок
# стекла, створа и переходов не выполнялись ни разу, а отчёт при этом был
# зелёный. Проверено 02.09: с графикой 73 проходят, 0 падают, пропущены двое
# по ДРУГОЙ причине (шейдер не поддержан) — её код отличает намеренно.
#
# EditMode графику не просит и запускается без неё: ему нечего рисовать.
args=(-batchmode -projectPath "$REPO_ROOT/unity/TestHost"
      -runTests -testPlatform PlayMode
      -testResults "$OUT/playmode.xml" -logFile "$OUT/playmode.log")
[ -n "$FILTER" ] && args+=(-testFilter "$FILTER")
"$UNITY" "${args[@]}" >/dev/null 2>&1
report_platform playmode "$OUT/playmode.xml" "$FLOOR_PLAYMODE" || fail=1
fi

# ── 2. Девайс-смоук (опционально) ───────────────────────────────────────────
if [ "$DEVICE" = 1 ]; then
  APK="${LVN_QA_APK:-$REPO_ROOT/qa/bin/sandbox-qa-dev.apk}"
  if [ ! -f "$APK" ]; then
    log "Собираю dev-APK (LVN_BUILD_DEV=1)…"
    LVN_BUILD_OUT="$APK" LVN_BUILD_DEV=1 \
      "$UNITY" -batchmode -nographics -projectPath "$REPO_ROOT/sandbox" \
        -buildTarget Android -executeMethod Lvn.EditorTools.CliBuild.Android \
        -quit -logFile "$OUT/apk-build.log" >/dev/null 2>&1 || { log "FAIL: сборка APK"; fail=1; }
  fi
  if [ -f "$APK" ]; then
    log "Поднимаю тестовый сервер :8099…"
    go build -o "$OUT/lvnserver" "$REPO_ROOT/server" || { log "FAIL: go build server"; fail=1; }
    "$OUT/lvnserver" -addr :8099 -content "$REPO_ROOT/server/content" >"$OUT/server.log" 2>&1 &
    SRV=$!
    trap '[ -n "${SRV:-}" ] && kill $SRV 2>/dev/null' EXIT
    sleep 1
    log "Смоук APK на эмуляторе…"
    # КОД ВОЗВРАТА СНИМАЕТСЯ ДО КОНВЕЙЕРА, А НЕ ПОСЛЕ НЕГО.
    #
    # Здесь стояло `monkey.sh … | tee … | tail -3 || fail=1`, и «||» ловил код
    # ПОСЛЕДНЕЙ команды конвейера — tail, который успешен почти всегда. Смоук
    # мог падать сколько угодно: прогон оставался зелёным. Проверено на двух
    # строках: `( exit 3 ) | tee /dev/null | tail -1 || fail=1` даёт fail=0.
    #
    # Единственное такое место из восемнадцати: остальные fail=1 стоят в
    # обычных if-блоках текущей оболочки и доходят до итога.
    if ! "$REPO_ROOT/qa/monkey.sh" "$APK" --server http://127.0.0.1:8099 \
         >"$OUT/device-smoke.log" 2>&1; then
      log "FAIL: смоук на устройстве — подробности: $OUT/device-smoke.log"
      fail=1
    fi
    tail -3 "$OUT/device-smoke.log" | sed 's/^/  /'

    kill $SRV 2>/dev/null; SRV=""
  fi
fi

# ВЕРДИКТ ЛЕЖИТ В ОТЧЁТЕ, А НЕ ТОЛЬКО В ВЫВОДЕ.
#
# Итог писался единственной строкой в stdout. При фоновом запуске вывод обычно
# уходит в /dev/null, и тогда узнать «цел ли проект» можно было только разбирая
# логи по частям — а разбор частей отвечает на другой вопрос: «прошли ли МОИ
# тесты». 04.09 на этом обманулся автор этих строк: смотрел editmode.xml,
# отчитывался зелёным, а Go-половина была красной несколько прогонов подряд.
#
# Теперь вердикт — файл в каталоге отчёта. Прочитать отчёт и узнать итог стало
# одним действием, а не двумя, из которых второе можно забыть.
{
  if [ "$fail" = 0 ]; then echo "PASS"; else echo "FAIL"; fi
  echo "stamp: $STAMP"
  for part in "$OUT"/*.log; do
    [ -e "$part" ] || continue
    # `grep -c` ПЕЧАТАЕТ ноль и ВЫХОДИТ С ЕДИНИЦЕЙ, когда совпадений нет.
    # С `|| echo 0` получалось два нуля в одной переменной, сравнение с нулём
    # не срабатывало — и вердикт объявлял красными ВСЕ части при зелёном
    # прогоне. Починка против «тишины, принятой за успех» сама выдавала успех
    # за провал.
    n=$(grep -cE "^--- FAIL|^FAIL" "$part" 2>/dev/null || true)
    [ "${n:-0}" -gt 0 ] 2>/dev/null && echo "красный: $(basename "$part") ($n)"
  done
  for x in editmode playmode; do
    [ -f "$OUT/$x.xml" ] || continue
    sed -n 's/.*total="\([0-9]*\)"[^>]*passed="\([0-9]*\)"[^>]*failed="\([0-9]*\)".*/'"$x"': всего \1, прошло \2, упало \3/p' "$OUT/$x.xml" | head -1
  done
} > "$OUT/VERDICT.txt"

if [ "$fail" = 0 ]; then log "RUN-ALL PASS — отчёты: $OUT"; else log "RUN-ALL FAIL — отчёты: $OUT"; fi
log "вердикт одной строкой: $OUT/VERDICT.txt"
exit $fail
