#!/usr/bin/env bash
# ЗАПИСЬ СТАЛА НЕВОЗМОЖНА — РАЗДАЧА ПРОДОЛЖАЕТСЯ.
#
# Диск кончился, том смонтировали только для чтения, права на каталоге уехали
# после переезда — для сервера это один и тот же случай: публикация физически
# не может дописать файл. Игроков в этот момент много, и их вопрос простой:
# идёт ли игра.
#
# Плохих исходов два, и оба реальные. Первый: сервер отвечает автору «готово»,
# ничего не записав, — автор уходит спать, считая главу выложенной. Второй:
# неудачная запись оставляет вместо главы пустой или обрезанный файл, и игроки
# получают битую главу вместо прежней рабочей.
#
#   ОТКАЗ      PUT кончается ошибкой, а не «готово»;
#   ЦЕЛОСТЬ    прежняя глава на диске не тронута — байт в байт;
#   РАЗДАЧА    игроки продолжают получать её же по сети;
#   ВОЗВРАТ    права вернули — публикация снова работает.
#
#   qa/disk-readonly-check.sh [-bite]
#
# -bite НЕ отнимает права: стенд обязан увидеть, что запись прошла. Мерка,
# которая не отличает «записалось» от «не записалось», не доказывает и отказа.
set -uo pipefail
cd "$(dirname "$0")/.."
BITE=""; [ "${1:-}" = "-bite" ] && BITE=1

command -v go   >/dev/null 2>&1 || { echo "нет go — пропускаю"; exit 0; }
command -v curl >/dev/null 2>&1 || { echo "нет curl — пропускаю"; exit 0; }
[ "$(id -u)" = "0" ] && { echo "прогон от root: снятые права ему не помеха — пропускаю"; exit 0; }

W="$(mktemp -d)"; PID=""
cleanup() {
  [ -n "$PID" ] && kill "$PID" 2>/dev/null
  chmod -R u+w "$W" 2>/dev/null   # иначе не удалить то, что сами закрыли
  rm -rf "$W"
}
trap cleanup EXIT

go build -C server -o "$W/lvnserver" . || { echo "сервер не собрался"; exit 1; }

PORT=0
for p in 8241 8243 8245 8247; do
  curl -fsS -m 1 "http://127.0.0.1:$p/healthz" >/dev/null 2>&1 || { PORT=$p; break; }
done
[ "$PORT" = "0" ] && { echo "порты заняты — пропускаю"; exit 0; }

C="$W/content"; mkdir -p "$C/scripts"
printf '{"titles":[],"rev":1}' > "$C/manifest.json"
printf 'сцена глава\n\n— прежняя редакция, она работает\n' > "$C/scripts/живая.lvns"
printf '{"scene":"глава","script":[{"op":"say","text":"прежняя редакция, она работает"}]}' \
  > "$C/scripts/живая.lvn"
BEFORE="$(shasum "$C/scripts/живая.lvn" | awk '{print $1}')"

TOKEN="stand-$$"
B="http://127.0.0.1:$PORT"
"$W/lvnserver" -addr "127.0.0.1:$PORT" -content "$C" -admin-token "$TOKEN" >"$W/server.log" 2>&1 &
PID=$!
disown "$PID" 2>/dev/null || true
for _ in $(seq 1 50); do
  curl -fsS -m 1 "$B/healthz" >/dev/null 2>&1 && break
  sleep 0.2
done
curl -fsS -m 2 "$B/healthz" >/dev/null 2>&1 || { echo "сервер не поднялся:"; tail -5 "$W/server.log"; exit 1; }

REL="scripts/%D0%B6%D0%B8%D0%B2%D0%B0%D1%8F.lvn"   # «живая.lvn» в адресе
bad=""; note() { bad="$bad\n  $1"; }

# ── Диск закрыт на запись ──────────────────────────────────────────────────
[ -n "$BITE" ] || chmod a-w "$C/scripts"

code="$(curl -s -o "$W/resp.json" -w '%{http_code}' -X PUT "$B/v1/admin/assets/$REL" \
        -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
        -d '{"scene":"глава","script":[{"op":"say","text":"новая редакция, она не доехала"}]}')"
AFTER="$(shasum "$C/scripts/живая.lvn" | awk '{print $1}')"
served="$(curl -s "$B/content/$REL")"

chmod u+w "$C/scripts" 2>/dev/null || true

if [ -n "$BITE" ]; then
  if [ "$code" = "200" ] && [ "$BEFORE" != "$AFTER" ]; then
    echo "укус чист: при открытых правах запись прошла ($code, файл изменился) — мерка её видит"
    exit 0
  fi
  echo "СТЕНД СЛЕП: права не отнимали, а записи он не увидел (код $code) — отказ ничего не значил бы"
  exit 2
fi

echo "  публикация при закрытой записи: код $code"
echo "  файл на диске: $([ "$BEFORE" = "$AFTER" ] && echo "цел, байт в байт" || echo "ИЗМЕНИЛСЯ")"
echo "  раздача: $(printf '%s' "$served" | head -c 60)…"

case "$code" in
  5*|4*) ;;
  200) note "сервер ответил «готово», ничего не записав — автор уйдёт спать, считая главу выложенной";;
  *)   note "непонятный ответ на невозможную запись: $code";;
esac
[ "$BEFORE" = "$AFTER" ] || note "неудачная запись изменила файл — игроки получат обрывок вместо прежней главы"
case "$served" in
  *"прежняя редакция"*) ;;
  *) note "по сети отдаётся не прежняя глава: $(printf '%s' "$served" | head -c 80)";;
esac

# ── Права вернули ──────────────────────────────────────────────────────────
code2="$(curl -s -o /dev/null -w '%{http_code}' -X PUT "$B/v1/admin/assets/$REL" \
         -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
         -d '{"scene":"глава","script":[{"op":"say","text":"новая редакция, теперь доехала"}]}')"
[ "$code2" = "200" ] || note "после возврата прав публикация не заработала ($code2)"
echo "  после возврата прав: код $code2"

[ -z "$bad" ] || { echo "РВЁТСЯ:$(printf '%b' "$bad")"; exit 1; }
echo "держит: невозможная запись отказывает честно, прежняя глава цела и раздаётся"
