#!/usr/bin/env bash
# Сборка деплой-артефактов НА ДЕВ-МАШИНЕ и раскладка их в деплой-репо
# продукта. Слабый прод-бокс не выдерживает сборки (go build уводит его в
# своп-шторм, npm тем более — выяснено опытным путём), поэтому конвейер такой:
# этот скрипт собирает всё здесь → артефакты коммитятся в деплой-репо → push
# в main → раннер НА боксе раскладывает готовое (его deploy-local.sh).
#
#   deploy/build-artifacts.sh /path/to/deploy-repo
#   LVN_DEPLOY_REPO=/path/to/deploy-repo deploy/build-artifacts.sh
#   COMMIT=1 deploy/build-artifacts.sh /path/…      # + git commit в деплой-репо
#
# Путь к деплой-репо задаётся аргументом или LVN_DEPLOY_REPO: он относится к
# конкретному продукту, а движок публичный и о продуктах не знает.
#
# Кладёт: bin/lvn-server-linux-amd64, bin/lvnconv-linux-amd64 (нужен на
# проде для resync-lvns/детект-инструментов), website/ (Studio-панель,
# включает свежий lvns.wasm — тот же компилятор, что в CLI), VERSION.
set -euo pipefail

HERE="$(cd "$(dirname "$0")/.." && pwd)"   # корень движка
API_REPO="${1:-${LVN_DEPLOY_REPO:-}}"
log() { echo "[build-artifacts] $*"; }

[ -n "$API_REPO" ] || {
  echo "укажи деплой-репо: deploy/build-artifacts.sh /path/to/deploy-repo"
  echo "(или задай LVN_DEPLOY_REPO)"; exit 1; }
API_REPO="$(cd "$API_REPO" && pwd)"
[ -f "$API_REPO/deploy-local.sh" ] || { echo "не похоже на деплой-репо: $API_REPO"; exit 1; }

ENGINE_SHA="$(git -C "$HERE" rev-parse --short HEAD)"
if [ -n "$(git -C "$HERE" status --porcelain -- tools server panel 2>/dev/null)" ]; then
  log "ВНИМАНИЕ: в движке незакоммиченные изменения — артефакт будет помечен ${ENGINE_SHA}-dirty"
  ENGINE_SHA="${ENGINE_SHA}-dirty"
fi

log "движок @ $ENGINE_SHA"

# ── 1. Серверный бинарь (linux/amd64, стрипнутый — как ждёт deploy-local) ──
log "go build server → bin/lvn-server-linux-amd64"
mkdir -p "$API_REPO/bin"
(cd "$HERE/server" && CGO_ENABLED=0 GOOS=linux GOARCH=amd64 \
  go build -trimpath -ldflags='-s -w' -o "$API_REPO/bin/lvn-server-linux-amd64" .)

# ── 2. lvnconv CLI (resync-lvns, detect и прочие серверные операции) ───────
log "go build lvnconv → bin/lvnconv-linux-amd64"
(cd "$HERE/tools/lvnconv" && CGO_ENABLED=0 GOOS=linux GOARCH=amd64 \
  go build -trimpath -ldflags='-s -w' -o "$API_REPO/bin/lvnconv-linux-amd64" .)

# ── 3. Studio-панель (npm run deploy пересобирает и wasm-компилятор) ───────
#
# СНАЧАЛА ДОКУМЕНТАЦИЯ. `panel/public/docs/content` НЕ хранится в
# репозитории — его собирает build-content.sh из howto/ и docs/. Сборка
# панели про это не знала, и документация уезжала на прод только у того, у
# кого эти файлы случайно остались на диске от прошлого раза. На чистой
# копии (а именно так собирают в CI и на другой машине) артефакт молча
# выходил без единой страницы справки: 34 файла в никуда.
log "docs → panel/public/docs/content"
if [ -x "$HERE/panel/public/docs/build-content.sh" ]; then
  (cd "$HERE" && panel/public/docs/build-content.sh . panel/public/docs/content >/dev/null)
  docs_n="$(ls "$HERE/panel/public/docs/content"/*.md 2>/dev/null | wc -l | tr -d ' ')"
  [ "${docs_n:-0}" -gt 0 ] || { echo "документация не собралась — на проде пропала бы справка"; exit 1; }
  log "  страниц справки: $docs_n"
fi

log "panel build (npm run deploy)"
(cd "$HERE/panel" && npm run deploy >/dev/null)

# ЧТО СОБРАЛОСЬ — ПРОВЕРЯЕМ ДО НЕОБРАТИМОГО ШАГА.
#
# Ниже идёт `rsync --delete`: он приводит сайт на проде в точности к тому, что
# лежит здесь. Упавшая сборка сюда не дойдёт (set -e), но УСПЕШНАЯ может дать
# неполный результат — плагин не положил каталог, ассет не скопировался, — и
# тогда живой сайт заменяется огрызком. Обратного хода нет.
#
# Поэтому перечислено то, без чего это не сайт, а огрызок: панель (её точка
# входа), playground целиком (страница, плеер, вычислитель выражений) и
# компилятор в браузере. Список короткий намеренно — он про «сайт вообще
# собрался», а не про полноту содержимого.
site="$HERE/server/website"
for must in \
  "index.html" \
  "play/index.html" \
  "play/core.js" \
  "play/expr.js" \
  "play/lvns.wasm" \
  "play/content/bg/Autumn_street.jpg"; do
  [ -e "$site/$must" ] || {
    echo "[build-artifacts] сборка панели неполная: нет $must" >&2
    echo "  (rsync --delete заменил бы живой сайт этим; деплой остановлен)" >&2
    exit 1
  }
done
log "сайт собран: панель + playground + компилятор"

rsync -a --delete "$HERE/server/website/" "$API_REPO/website/"

# ── 4. Штамп версии — трассируемость «какой движок задеплоен» ──────────────
printf 'engine=%s\nbuilt=%s\n' "$ENGINE_SHA" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$API_REPO/VERSION"

log "готово:"
ls -la "$API_REPO/bin" | awk 'NR>1 {print "  " $5 "\t" $9}'
du -sh "$API_REPO/website" | awk '{print "  " $1 "\twebsite/"}'
cat "$API_REPO/VERSION" | sed 's/^/  /'

# ── 5. Опциональный коммит в api-репо ──────────────────────────────────────
if [ "${COMMIT:-0}" = "1" ]; then
  (cd "$API_REPO" && git add bin website VERSION && \
   git commit -m "Артефакты движка @ ${ENGINE_SHA}" && \
   log "закоммичено в api-репо — git push origin main запустит автодеплой")
fi
