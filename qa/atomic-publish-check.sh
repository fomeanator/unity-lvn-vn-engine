#!/usr/bin/env bash
# ЧИТАТЕЛЬ НИКОГДА НЕ ПОЛУЧАЕТ ПОЛУФАЙЛ.
#
# Публикация идёт по живому: автор жмёт «сохранить», пока десятки игроков
# качают эту же главу. Если запись видна читателю частично, игрок получает
# оборванный JSON — и это худший вид беды, потому что чинится он не сам:
# обрывок ляжет в кэш и будет встречать игрока при каждом заходе, пока тот не
# сотрёт данные приложения.
#
# Стенд сталкивает записи и чтения лбами:
#
#   ГОНКА     автор публикует главу подряд много раз, читатели качают её же;
#   ЦЕЛОСТЬ   каждый ответ — валидный JSON ОДНОЙ из версий, не склейка двух;
#   ДЛИНА     тело ответа совпадает с заголовком Content-Length;
#   ВЕРСИЯ    в ответе видна ровно одна пометка редакции.
#
#   qa/atomic-publish-check.sh [-bite]
#
# -bite подменяет чтение на файл, склеенный из двух редакций: стенд обязан
# назвать его битым. Мерка, не отличающая склейку от целого файла, не
# доказывает и целости.
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
for p in 8231 8233 8235 8237; do
  curl -fsS -m 1 "http://127.0.0.1:$p/healthz" >/dev/null 2>&1 || { PORT=$p; break; }
done
[ "$PORT" = "0" ] && { echo "порты заняты — пропускаю"; exit 0; }

C="$W/content"; mkdir -p "$C/scripts"
printf '{"titles":[],"rev":1}' > "$C/manifest.json"
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

# Глава НЕ маленькая: файл в один пакет доезжает целым даже без атомарной
# записи, и стенд ничего бы не поймал.
python3 - "$C/scripts/гонка.lvn" 1 <<'PY'
import json, sys
путь, ред = sys.argv[1], int(sys.argv[2])
script = [{"op": "say", "text": f"редакция {ред}, реплика {i}"} for i in range(4000)]
open(путь, "w", encoding="utf-8").write(json.dumps({"scene": "гонка", "rev": ред, "script": script}, ensure_ascii=False))
PY
echo "  файл главы: $(wc -c < "$C/scripts/гонка.lvn" | tr -d ' ') байт"

if [ -n "$BITE" ]; then
  # Склейка двух редакций — ровно то, что увидел бы читатель при неатомарной
  # записи: голова одной, хвост другой.
  python3 - "$C/scripts/гонка.lvn" "$W/склейка.lvn" <<'PY'
import sys
целый = open(sys.argv[1], encoding="utf-8").read()
голова = целый[: len(целый) // 2]
open(sys.argv[2], "w", encoding="utf-8").write(голова + целый[len(целый) // 3 :])
PY
  broken=$(python3 - "$W/склейка.lvn" <<'PY'
import json, sys
try:
    json.load(open(sys.argv[1], encoding="utf-8")); print(0)
except Exception: print(1)
PY
)
  if [ "$broken" = "1" ]; then
    echo "укус чист: склейку двух редакций стенд назвал битой — мерка отличает целое от полуфайла"
    exit 0
  fi
  echo "СТЕНД СЛЕП: склеенный файл принят за целый — «полуфайлов нет» ничего не значило бы"
  exit 2
fi

# ── Гонка: автор пишет, читатели качают ────────────────────────────────────
python3 - "$B" "$TOKEN" "$C/scripts/гонка.lvn" <<'PY'
import json, sys, threading, time, urllib.request, urllib.error, urllib.parse
B, TOKEN, путь = sys.argv[1], sys.argv[2], sys.argv[3]
# Имя файла кириллическое НАМЕРЕННО: у авторов так и есть, и адрес обязан
# ехать закодированным — иначе стенд падает на своём же запросе, а не на
# сервере (первая редакция именно так и «нашла» дефект).
адрес = "/content/scripts/" + urllib.parse.quote("гонка.lvn")
адресЗаписи = "/v1/admin/assets/scripts/" + urllib.parse.quote("гонка.lvn")
редакций, читателей = 12, 16
плохо, ответов = [], [0]
замок = threading.Lock()

тело = json.load(open(путь, encoding="utf-8"))

def автор():
    for ред in range(2, редакций + 2):
        тело["rev"] = ред
        for i, c in enumerate(тело["script"]):
            c["text"] = f"редакция {ред}, реплика {i}"
        данные = json.dumps(тело, ensure_ascii=False).encode()
        req = urllib.request.Request(B + адресЗаписи, данные,
                                     {"Content-Type": "application/json",
                                      "Authorization": "Bearer " + TOKEN}, method="PUT")
        try: urllib.request.urlopen(req, timeout=20).read()
        except Exception as e:
            with замок: плохо.append(f"публикация {ред} не прошла: {e}")
        time.sleep(0.05)

def читатель():
    конец = time.time() + 6
    while time.time() < конец:
        try:
            with urllib.request.urlopen(B + адрес, timeout=20) as r:
                длина = r.headers.get("Content-Length")
                сырое = r.read()
        except Exception as e:
            with замок: плохо.append(f"чтение оборвалось: {e}")
            continue
        with замок: ответов[0] += 1
        if длина is not None and int(длина) != len(сырое):
            with замок: плохо.append(f"тело {len(сырое)} байт против Content-Length {длина}")
            continue
        try:
            doc = json.loads(сырое.decode("utf-8"))
        except Exception as e:
            with замок: плохо.append(f"ПОЛУФАЙЛ: {type(e).__name__} на {len(сырое)} байтах")
            continue
        реды = {c["text"].split(",")[0] for c in doc.get("script", [])}
        if len(реды) != 1:
            with замок: плохо.append(f"склейка редакций в одном ответе: {sorted(реды)[:3]}")

нити = [threading.Thread(target=автор)] + [threading.Thread(target=читатель) for _ in range(читателей)]
for н in нити: н.start()
for н in нити: н.join()

print(f"  гонка: {редакций} публикаций против {читателей} читателей, ответов {ответов[0]}")
if плохо:
    print("РВЁТСЯ:")
    for p in плохо[:5]: print("  " + p)
    raise SystemExit(1)
print(f"  полуфайлов: 0 на {ответов[0]} ответов")
PY
code=$?
[ "$code" = "0" ] || exit "$code"
echo "держит: публикация по живому не отдаёт читателю ни склейки, ни обрывка"
