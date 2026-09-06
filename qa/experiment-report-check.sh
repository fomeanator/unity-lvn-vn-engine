#!/usr/bin/env bash
# ОПЫТ ДОВОДИТСЯ ДО РЕШЕНИЯ, А НЕ ДО КРАСИВОЙ СТРЕЛОЧКИ.
#
# A/B существует ради одного: выбрать вариант и не ошибиться. Соседний стенд
# проверяет НАЗНАЧЕНИЕ (группа стойкая, доли соблюдаются, выключатель работает).
# Здесь вторая половина, без которой первая бесполезна: две группы сыграли
# по-разному — и отчёт обязан это показать так, чтобы по нему можно было
# принять решение.
#
# Дорога проходится целиком и по-настоящему: игроки регистрируются, СПРАШИВАЮТ
# У СЕРВЕРА свою группу, шлют события с этой группой в props (ab_<имя>) — ровно
# как это делает игра, — а потом у отчёта спрашивают итог.
#
#   РАЗДЕЛЕНИЕ   игроки разошлись по группам, и в отчёте они все;
#   ПОКАЗАТЕЛИ   дочитывание считается по ИГРОКАМ и совпадает с посланным;
#   РАЗНИЦА      настоящая разница названа значимой;
#   ШУМ          одинаковое поведение НЕ объявляется победой, и отчёт говорит,
#                сколько игроков нужно, чтобы разницу такого размера заметить.
#
#   qa/experiment-report-check.sh [-bite]
#
# -bite делает обе группы ОДИНАКОВЫМИ там, где стенд ждёт разницу: мерка обязана
# перестать видеть значимость. Отчёт, который «находит» победителя в шуме, хуже
# отсутствия отчёта — по нему принимают решения.
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
for p in 8191 8193 8195 8197; do
  curl -fsS -m 1 "http://127.0.0.1:$p/healthz" >/dev/null 2>&1 || { PORT=$p; break; }
done
[ "$PORT" = "0" ] && { echo "порты заняты — пропускаю"; exit 0; }

C="$W/content"; mkdir -p "$C"
printf '{"titles":[]}' > "$C/manifest.json"
# Два опыта: по первому группы играют по-разному, по второму — одинаково.
cat > "$C/experiments.json" <<'JSON'
[{"name":"цена","variants":[{"id":"a","weight":50},{"id":"b","weight":50}],"layer":"первый","version":1,"enabled":true},
 {"name":"шум","variants":[{"id":"a","weight":50},{"id":"b","weight":50}],"layer":"второй","version":1,"enabled":true}]
JSON

TOKEN="stand-$$"
"$W/lvnserver" -addr "127.0.0.1:$PORT" -content "$C" -admin-token "$TOKEN" >"$W/server.log" 2>&1 &
PID=$!
for _ in $(seq 1 50); do
  curl -fsS -m 1 "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1 && break
  sleep 0.2
done
curl -fsS -m 2 "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1 \
  || { echo "сервер не поднялся:"; tail -5 "$W/server.log"; exit 1; }

B="http://127.0.0.1:$PORT"
bad=""; note() { bad="$bad\n  $1"; }

# ── Игра: N игроков спрашивают свою группу и играют по её правилам ──────────
# Доли дочитывания заданы стендом: вариант «b» дочитывают чаще. Разница в
# тридцать пунктов на двух сотнях игроков обязана быть названа значимой.
PLAYERS=200
python3 - "$B" "${BITE:-}" "$PLAYERS" > "$W/sent.json" <<'PY'
import json, sys, urllib.request, concurrent.futures
from collections import Counter
B, bite, n = sys.argv[1], sys.argv[2], int(sys.argv[3])

def post(path, body, token=None):
    data = json.dumps(body).encode()
    headers = {"Content-Type": "application/json"}
    if token: headers["Authorization"] = "Bearer " + token
    req = urllib.request.Request(B + path, data, headers)
    raw = urllib.request.urlopen(req, timeout=20).read()
    return json.loads(raw) if raw.strip() else {}

def get(path, token):
    req = urllib.request.Request(B + path, headers={"Authorization": "Bearer " + token})
    return json.load(urllib.request.urlopen(req, timeout=20))

# Доля дочитавших на группу. Укус равняет группы: значимости взяться неоткуда.
finish_rate = {"a": 0.5, "b": 0.5 if bite else 0.8}

def one(i):
    tok = post("/v1/auth/register", {"device_id": f"опыт-{i:06d}-abcdefgh"})["token"]
    groups = get("/v1/experiments", tok)["assignments"]
    price, noise = groups.get("цена", ""), groups.get("шум", "")
    props = {"ab_цена": price, "ab_шум": noise}
    events = [{"name": "chapter_start", "props": props}]
    # Дочитывание: по первому опыту — разная доля, по второму — одинаковая.
    if (i % 100) / 100.0 < finish_rate.get(price, 0.5):
        events.append({"name": "chapter_finish", "props": props})
    post("/v1/analytics/events", events, tok)
    return price, noise, len(events) > 1

starts, finishes = Counter(), Counter()
with concurrent.futures.ThreadPoolExecutor(max_workers=16) as ex:
    for price, noise, finished in ex.map(one, range(n)):
        starts[price] += 1
        if finished: finishes[price] += 1
json.dump({"starts": dict(starts), "finishes": dict(finishes)}, sys.stdout, ensure_ascii=False)
PY

sent_a="$(python3 -c "import json;d=json.load(open('$W/sent.json'));print(d['starts'].get('a',0))")"
sent_b="$(python3 -c "import json;d=json.load(open('$W/sent.json'));print(d['starts'].get('b',0))")"
fin_a="$(python3 -c "import json;d=json.load(open('$W/sent.json'));print(d['finishes'].get('a',0))")"
fin_b="$(python3 -c "import json;d=json.load(open('$W/sent.json'));print(d['finishes'].get('b',0))")"
[ "$sent_a" -gt 0 ] && [ "$sent_b" -gt 0 ] || { echo "игроки не разошлись по группам — проверять нечего"; exit 2; }

sleep 2   # своды пишутся не в тот же миг, что и приём события

report() { curl -s "$B/v1/analytics/experiment?name=$1&days=2" -H "Authorization: Bearer $TOKEN"; }

# ── 1. Разделение и показатели ─────────────────────────────────────────────
R1="$(report "%D1%86%D0%B5%D0%BD%D0%B0")"
read -r got_a got_b comp_a comp_b < <(python3 - "$R1" <<'PY'
import json, sys
d = json.loads(sys.argv[1])
v = {x["variant"]: x for x in d.get("variants") or []}
a, b = v.get("a", {}), v.get("b", {})
print(a.get("players", 0), b.get("players", 0),
      round(a.get("completion", 0), 3), round(b.get("completion", 0), 3))
PY
)
[ "$got_a" = "$sent_a" ] || note "в группе «a» отчёт видит $got_a игрок(ов) вместо $sent_a"
[ "$got_b" = "$sent_b" ] || note "в группе «b» отчёт видит $got_b игрок(ов) вместо $sent_b"

# Дочитывание сверяем с посланным, а не с задуманным: округление долей на
# конкретном числе игроков — не повод считать отчёт неверным.
want_a="$(python3 -c "print(round($fin_a/max($sent_a,1),3))")"
want_b="$(python3 -c "print(round($fin_b/max($sent_b,1),3))")"
python3 -c "
import sys
sys.exit(0 if abs($comp_a-$want_a) <= 0.02 and abs($comp_b-$want_b) <= 0.02 else 1)" \
  || note "дочитывание в отчёте ($comp_a / $comp_b) разошлось с посланным ($want_a / $want_b)"

# ── 2. Разница названа значимой / шум не назван победой ────────────────────
read -r sig1 need1 text1 < <(python3 - "$R1" <<'PY'
import json, sys
d = json.loads(sys.argv[1])
line = next((x for x in d.get("verdict") or [] if "дочит" in x.get("metric", "").lower()
             or "completion" in x.get("metric", "").lower()), None)
if line is None:
    line = (d.get("verdict") or [{}])[0]
print(str(line.get("significant", False)).lower(), line.get("need_players", 0),
      (line.get("text", "") or "—").replace(" ", "_")[:60])
PY
)

R2="$(report "%D1%88%D1%83%D0%BC")"
read -r sig2 need2 < <(python3 - "$R2" <<'PY'
import json, sys
d = json.loads(sys.argv[1])
lines = d.get("verdict") or []
sig = any(x.get("significant") for x in lines)
need = max((x.get("need_players", 0) or 0) for x in lines) if lines else 0
print(str(sig).lower(), need)
PY
)

if [ -n "$BITE" ]; then
  if [ "$sig1" = "false" ]; then
    echo "укус чист: группы сравняли — значимость пропала ($comp_a против $comp_b)"
    exit 0
  fi
  echo "СТЕНД СЛЕП: группы играли одинаково, а отчёт всё равно объявил разницу значимой"
  exit 2
fi

echo "  разделение: «a» $got_a игрок(ов), «b» $got_b"
echo "  дочитывание: $comp_a против $comp_b (послано $want_a / $want_b)"
echo "  разница:    значима=$sig1, нужно игроков=$need1"
echo "  шум:        значима=$sig2, нужно игроков=$need2"

[ "$sig1" = "true" ] || note "настоящая разница в тридцать пунктов не названа значимой — решение по такому отчёту не примешь"
[ "$sig2" = "false" ] || note "одинаковое поведение групп объявлено значимой разницей — отчёт находит победителя в шуме"
[ "${need2:-0}" -gt 0 ] || note "по шумной разнице отчёт не сказал, сколько игроков нужно — читатель останется наедине со стрелочкой"

[ -z "$bad" ] || { echo "РВЁТСЯ:$(printf '%b' "$bad")"; exit 1; }
echo "держит: группы разделены, показатели совпадают с посланным, разница названа, шум — нет"
