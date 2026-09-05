#!/usr/bin/env bash
# РАБОТА ВТОРОГО АВТОРА НЕ ИСЧЕЗАЕТ МОЛЧА.
#
# Над одной новеллой работают несколько рук: автор в панели, соавтор во второй
# вкладке, ИИ-агент по API, скрипт сборки. Все пишут главу ОДНОЙ дверью
# (PUT /v1/admin/assets/...). Пока сервер не спрашивал, на какой версии правили,
# побеждал последний записавший: оба получали «сохранено», на диске оставалась
# одна правка, и потерявший узнавал об этом назавтра, читая свою главу.
#
# Стенд разыгрывает это по проводу на живом сервере:
#
#   ОБА ЧИТАЮТ   А и Б открыли одну главу и видят одну версию;
#   ПЕРВЫЙ       А сохраняет — принято, в ответе новая версия;
#   ВТОРОЙ       Б сохраняет на СТАРОЙ версии — отказ 409, работа А цела;
#   ПОСЛЕ        Б перечитал и сохранил — принято (отказ не запирает работу);
#   СБОРКА       запись без версии идёт как раньше — тракт публикации цел;
#   ГОНКА        десять сохранений одной версии — выигрывает ровно один;
#   ИСТОРИЯ      прежняя редакция лежит в .history и достаётся обратно.
#
#   qa/coauthor-check.sh [-bite]
#
# -bite сохраняет вторую правку БЕЗ версии, то есть заведомо затирает первую:
# стенд обязан увидеть подмену содержимого. Мерка, не замечающая затирания, не
# доказывает и сохранности.
set -uo pipefail
cd "$(dirname "$0")/.."
BITE=""; [ "${1:-}" = "-bite" ] && BITE=1

command -v go      >/dev/null 2>&1 || { echo "нет go — пропускаю"; exit 0; }
command -v curl    >/dev/null 2>&1 || { echo "нет curl — пропускаю"; exit 0; }
command -v python3 >/dev/null 2>&1 || { echo "нет python3 — пропускаю"; exit 0; }

W="$(mktemp -d)"; PID=""
cleanup() { [ -n "$PID" ] && kill "$PID" 2>/dev/null; rm -rf "$W"; }
trap cleanup EXIT

go build -C server -o "$W/lvnserver" . || { echo "сервер не собрался"; exit 1; }

PORT=0
for p in 8221 8223 8225 8227; do
  curl -fsS -m 1 "http://127.0.0.1:$p/healthz" >/dev/null 2>&1 || { PORT=$p; break; }
done
[ "$PORT" = "0" ] && { echo "порты заняты — пропускаю"; exit 0; }

C="$W/content"; mkdir -p "$C/scripts"
printf '{"titles":[],"rev":1}' > "$C/manifest.json"
printf 'сцена ch1\n\n— исходная строка\n' > "$C/scripts/ch1.lvns"
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

bad=""; note() { bad="$bad\n  $1"; }
REL="scripts/ch1.lvns"
version() { curl -sI "$B/content/$REL" | awk 'tolower($1)=="etag:"{print $2}' | tr -d '\r'; }
save() { # $1 = текст, $2 = версия («» — без заголовка) → код ответа
  if [ -n "$2" ]; then
    curl -s -o "$W/resp.json" -w '%{http_code}' -X PUT "$B/v1/admin/assets/$REL" \
      -H "Authorization: Bearer $TOKEN" -H "If-Match: $2" \
      -H 'Content-Type: text/plain; charset=utf-8' --data-binary "$1"
  else
    curl -s -o "$W/resp.json" -w '%{http_code}' -X PUT "$B/v1/admin/assets/$REL" \
      -H "Authorization: Bearer $TOKEN" \
      -H 'Content-Type: text/plain; charset=utf-8' --data-binary "$1"
  fi
}
on_disk() { cat "$C/$REL"; }

# ── Оба открыли главу ──────────────────────────────────────────────────────
V0="$(version)"
[ -n "$V0" ] || { echo "у файла нет версии — сверять нечего"; exit 2; }

# ── Первый сохраняет ───────────────────────────────────────────────────────
c_a="$(save 'сцена ch1

— правка автора А
' "$V0")"
[ "$c_a" = "200" ] || note "первому автору отказали ($c_a)"
etag_a="$(python3 -c "import json;print(json.load(open('$W/resp.json',encoding='utf-8')).get('etag',''))" 2>/dev/null)"
[ -n "$etag_a" ] || note "успешная запись не вернула версию — редактору придётся перечитывать себя же"

# ── Второй сохраняет на старой версии ──────────────────────────────────────
if [ -n "$BITE" ]; then
  c_b="$(save 'сцена ch1

— правка автора Б
' "")"          # укус: пишем БЕЗ версии, то есть заведомо затираем
else
  c_b="$(save 'сцена ch1

— правка автора Б
' "$V0")"
fi
conflict="$(python3 -c "import json;print(json.load(open('$W/resp.json',encoding='utf-8')).get('conflict',False))" 2>/dev/null)"

if [ -n "$BITE" ]; then
  if ! on_disk | grep -q "автора А"; then
    echo "укус чист: затирание видно — на диске «$(on_disk | tr '\n' ' ' | tr -s ' ')»"
    exit 0
  fi
  echo "СТЕНД СЛЕП: правка легла поверх, а он этого не заметил"
  exit 2
fi

[ "$c_b" = "409" ] || note "второму ответили $c_b вместо 409 — работа первого под угрозой"
[ "$conflict" = "True" ] || note "отказ не помечен признаком конфликта: интерфейсу придётся разбирать текст"
on_disk | grep -q "автора А" || note "работа первого автора потеряна: на диске «$(on_disk | tr '\n' ' ')»"

# ── Второй перечитал и сохранил ────────────────────────────────────────────
c_b2="$(save 'сцена ch1

— правка А, дополненная Б
' "$(version)")"
[ "$c_b2" = "200" ] || note "после перечитывания второму снова отказали ($c_b2)"
on_disk | grep -q "дополненная" || note "вторая правка не легла после перечитывания"

# ── Скрипт сборки: версий не читает ────────────────────────────────────────
c_cli="$(save 'сцена ch1

— из скрипта сборки
' "")"
[ "$c_cli" = "200" ] || note "запись без версии отклонена ($c_cli) — тракт публикации сломан"

# ── Гонка: десять с одной версией ──────────────────────────────────────────
VR="$(version)"
codes="$(seq 1 10 | xargs -P 10 -I{} curl -s -o /dev/null -w '%{http_code}\n' \
        -X PUT "$B/v1/admin/assets/$REL" -H "Authorization: Bearer $TOKEN" \
        -H "If-Match: $VR" -H 'Content-Type: text/plain; charset=utf-8' \
        --data-binary 'сцена ch1

— гонка {}
' | sort | uniq -c | tr '\n' ' ')"
won="$(printf '%s' "$codes" | grep -o '[0-9]* 200' | awk '{print $1}')"
[ "${won:-0}" = "1" ] || note "одну версию приняли ${won:-0} раз(а) — замок не держит: $codes"

# ── История ────────────────────────────────────────────────────────────────
snaps="$(find "$C/.history" -type f -name '*.bak' 2>/dev/null | wc -l | tr -d ' ')"
[ "$snaps" -ge 3 ] || note "снимков в истории $snaps — прежние редакции не сохраняются"

echo "  первый:  $c_a, версия в ответе есть"
echo "  второй:  $c_b на старой версии, после перечитывания $c_b2"
echo "  сборка:  без версии $c_cli"
echo "  гонка:   $codes"
echo "  история: снимков $snaps"

[ -z "$bad" ] || { echo "РВЁТСЯ:$(printf '%b' "$bad")"; exit 1; }
echo "держит: чужую правку не затереть молча, отказ не запирает работу, сборка пишет как раньше"
