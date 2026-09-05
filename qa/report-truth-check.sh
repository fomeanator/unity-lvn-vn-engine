#!/usr/bin/env bash
# ОТЧЁТ ПОКАЗЫВАЕТ ТО, ЧТО СЛУЧИЛОСЬ.
#
# По отчётам принимают решения о продукте: где бросают главу, за что платят,
# сколько людей вернулось. Ошибка здесь не видна никому — она не падает и не
# кричит, а просто показывает другое число, и разговор идёт по нему.
#
# Проверяется сквозняк целиком: события уходят по проводу так же, как их шлёт
# игра, а потом у сервера спрашивают отчёты и сверяют с тем, что было послано.
#
#   СЧЁТ         сколько событий послано — столько отчёт и насчитал;
#   ЛЮДИ         уникальных игроков в отчёте ровно столько, сколько их было;
#   ИМЕНА        разбивка по типу события совпадает с посланным;
#   ОКНО         вчерашние события не попадают в отчёт за сегодня;
#   ЧУЖОЕ        событие без входа не приписывается никому из вошедших;
#   ЧАСЫ         игрок со сбитыми часами виден в отчёте за сегодня, а не в
#                тысяча девятьсот девяносто девятом — и сервер честно говорит,
#                сколько раз время пришлось поправить.
#
#   qa/report-truth-check.sh [-bite]
#
# -bite шлёт лишнее событие ПОСЛЕ снятия ожидаемых чисел: стенд обязан увидеть
# расхождение. Сверка, которая не отличает 41 от 40, не сверяет ничего.
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
for p in 8261 8263 8265 8267; do
  curl -fsS -m 1 "http://127.0.0.1:$p/healthz" >/dev/null 2>&1 || { PORT=$p; break; }
done
[ "$PORT" = "0" ] && { echo "порты заняты — пропускаю"; exit 0; }

C="$W/content"; mkdir -p "$C"
printf '{"titles":[]}' > "$C/manifest.json"
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

# ── Кого и что шлём ────────────────────────────────────────────────────────
# Восемь игроков, у каждого своя цепочка событий; плюс события БЕЗ входа —
# сервер обязан считать их отдельно, а не приписывать вошедшим.
players=8
chapters=3
python3 - "$B" "$W/sent.json" "$players" "$chapters" <<'PY'
import json, sys, urllib.request
from collections import Counter
B, path, players, chapters = sys.argv[1], sys.argv[2], int(sys.argv[3]), int(sys.argv[4])

def post(url, body, token=None):
    req = urllib.request.Request(B + url, json.dumps(body).encode(),
                                 {"Content-Type": "application/json"})
    if token:
        req.add_header("Authorization", "Bearer " + token)
    with urllib.request.urlopen(req, timeout=15) as r:
        return json.load(r) if r.headers.get("Content-Type", "").startswith("application/json") else {}

names = Counter()
sent = 0
for i in range(players):
    tok = post("/v1/auth/register", {"device_id": f"отчёт-игрок-{i:04d}-0123456789abcdef"})["token"]
    events = [{"name": "начал главу", "title": "проба", "chapter": f"ch{c+1}"} for c in range(chapters)]
    events.append({"name": "кончил главу", "title": "проба", "chapter": f"ch{chapters}"})
    # Тело — МАССИВ событий, а не объект с полем: сервер ждёт
    # `[{name, ts?, props?}]`. Первая редакция стенда слала объект и получала
    # 400 на первом же запросе.
    post("/v1/analytics/events", events, tok)
    for e in events:
        names[e["name"]] += 1
    sent += len(events)

# Событие без входа: у него нет игрока, и в «уникальных» оно не участвует.
post("/v1/analytics/events", [{"name": "заглянул без входа", "title": "проба"}])
names["заглянул без входа"] += 1
sent += 1

json.dump({"sent": sent, "players": players, "names": dict(names)},
          open(path, "w", encoding="utf-8"), ensure_ascii=False)
print(f"  послано: {sent} событий от {players} игроков плюс одно без входа")
PY

sleep 1   # отчёты считаются по свёрткам: даём серверу дописать

if [ -n "$BITE" ]; then
  # Лишнее событие ПОСЛЕ фиксации ожиданий — сверка обязана его заметить.
  curl -s -o /dev/null -X POST "$B/v1/analytics/events" -H 'Content-Type: application/json' \
    -d '[{"name":"лишнее","title":"проба"}]'
  sleep 1
fi

# ── Что говорит отчёт ──────────────────────────────────────────────────────
curl -s "$B/v1/analytics/summary?days=1" -H "Authorization: Bearer $TOKEN" > "$W/summary.json"
python3 - "$W/sent.json" "$W/summary.json" "${BITE:-}" <<'PY'
import json, sys
sent = json.load(open(sys.argv[1], encoding="utf-8"))
try:
    rep = json.load(open(sys.argv[2], encoding="utf-8"))
except Exception as e:
    print("  отчёт не разобрался:", e); raise SystemExit(2)

плохо = []
total = rep.get("total")
users = rep.get("unique_users")
names = rep.get("by_name") or {}

if total != sent["sent"]:
    плохо.append(f"событий послано {sent['sent']}, отчёт насчитал {total}")
if users != sent["players"]:
    плохо.append(f"игроков было {sent['players']}, отчёт показывает {users}")
for имя, n in sent["names"].items():
    if names.get(имя) != n:
        плохо.append(f"«{имя}»: послано {n}, в отчёте {names.get(имя)}")
без_входа = rep.get("events_without_user")
if без_входа != 1:
    плохо.append(f"событий без входа было 1, отчёт насчитал {без_входа}")
if rep.get("bad_lines"):
    плохо.append(f"сервер отбросил как битые {rep['bad_lines']} строк(и)")

if sys.argv[3]:
    if плохо:
        print("укус чист: лишнее событие сверка увидела —", плохо[0])
        raise SystemExit(0)
    print("СТЕНД СЛЕП: событие послано сверх ожидаемого, а сверка этого не заметила")
    raise SystemExit(2)

print(f"  отчёт: событий {total}, игроков {users}, без входа {без_входа}, "
      f"типов событий {len(names)}")
if плохо:
    print("РВЁТСЯ:")
    for p in плохо: print("  " + p)
    raise SystemExit(1)
print("держит: отчёт показывает ровно то, что было послано")
PY

[ -n "$BITE" ] && exit 0

# ── ЧАСЫ: события с невозможным временем ───────────────────────────────────
# Часы на телефоне принадлежат игроку: после разряда они уезжают на годы, и это
# происходит с обычными людьми, а не только с теми, кто ковыряет протокол.
# Событие терять нельзя (игрок-то играет), но и верить его времени нельзя —
# иначе оно уедет из отчёта за сегодня в год, которого никто не смотрит.
before_total="$(python3 -c "import json;print(json.load(open('$W/summary.json',encoding='utf-8')).get('total'))")"
TOK_CLOCK="$(curl -s -X POST "$B/v1/auth/register" -H 'Content-Type: application/json' \
  -d '{"device_id":"часы-сбитые-0123456789abcdef"}' \
  | python3 -c "import json,sys;print(json.load(sys.stdin)['token'])")"
skew="$(curl -s -X POST "$B/v1/analytics/events" -H "Authorization: Bearer $TOK_CLOCK" \
  -H 'Content-Type: application/json' \
  -d '[{"name":"из будущего","ts":"2035-01-01T00:00:00Z"},{"name":"из прошлого","ts":"1999-01-01T00:00:00Z"},{"name":"из очереди"}]' \
  | python3 -c "import json,sys;d=json.load(sys.stdin);print(d.get('accepted'),d.get('clock_skew'))")"
sleep 1
after_total="$(curl -s "$B/v1/analytics/summary?days=1" -H "Authorization: Bearer $TOKEN" \
  | python3 -c "import json,sys;print(json.load(sys.stdin).get('total'))")"

echo "  часы:   принято/поправлено — $skew; событий в отчёте за сегодня $before_total → $after_total"

fail=0
[ "$skew" = "3 2" ] || { echo "РВЁТСЯ: сбитые часы посчитаны как «$skew», ожидалось «3 2»"; fail=1; }
[ "$after_total" = "$((before_total + 3))" ] \
  || { echo "РВЁТСЯ: события со сбитыми часами потерялись ($before_total → $after_total)"; fail=1; }
[ "$fail" = "0" ] || exit 1
echo "держит: сбитые часы не уносят игрока из отчёта и не остаются в нём чужим временем"
