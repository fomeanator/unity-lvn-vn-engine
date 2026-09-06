#!/usr/bin/env bash
# ПРАВКА ОДНОЙ РЕПЛИКИ СТОИТ ОДНУ РЕПЛИКУ, А НЕ ВЕСЬ КАТАЛОГ.
#
# Живое обновление — обещание из главных: автор правит вышедшую главу, и
# правка доезжает до играющих без новой сборки. Цена этого обещания платится
# трафиком игрока, и платится она на КАЖДУЮ правку. Требование Ильи звучало
# прямо: качать только изменившееся, а не весь манифест.
#
# Дорога у клиента такая: дешёвый опрос версии → «что именно изменилось» →
# и только если сменился САМ каталог, поход за манифестом. Стенд проходит её
# целиком по-настоящему — сервер, файлы, байты — и меряет:
#
#   ТИШИНА       опрос при неизменном контенте: 304 и пустое тело;
#   РЕПЛИКА      правка одной строки главы: в разнице ровно её скрипт,
#                каталог не назван → манифест не качается вовсе;
#   КАТАЛОГ      добавлена глава: манифест назван, и вот теперь за ним идут;
#   УДАЛЕНИЕ     файл убран: он попадает в removed, а не остаётся в кэше;
#   ДОЛГИЙ СОН   версия выпала из кольца снимков: честное «забирай всё»;
#   ЦЕНА         сколько байт стоит правка реплики против прежнего пути
#                (карта версий + манифест) — числом, а не словом «дешевле».
#
#   qa/live-update-cost-check.sh [-bite]
#
# -bite правит ДВА файла вместо одного: стенд обязан увидеть в разнице два
# имени. Мерка, у которой всегда «ровно один», не заметила бы и пропажи.
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
for p in 8181 8183 8185 8187; do
  curl -fsS -m 1 "http://127.0.0.1:$p/healthz" >/dev/null 2>&1 || { PORT=$p; break; }
done
[ "$PORT" = "0" ] && { echo "порты заняты — пропускаю"; exit 0; }

# ── Каталог размером с настоящий ───────────────────────────────────────────
# Смысл замера — в отношении «правка» к «всё остальное», поэтому каталог
# должен быть не игрушечным: сотня ассетов даёт карту версий, сравнимую с
# живой (там она 282 КБ на несколько тысяч файлов).
C="$W/content"; mkdir -p "$C/scripts" "$C/bg" "$C/sprites"
python3 - "$C" <<'PY'
import json, sys, os
root = sys.argv[1]
chapters = []
for i in range(1, 4):
    p = os.path.join(root, "scripts", f"ch{i}.lvn")
    open(p, "w", encoding="utf-8").write(json.dumps({
        "scene": f"ch{i}",
        "script": [{"op": "say", "text": f"глава {i}, реплика первая"},
                   {"op": "say", "text": "реплика вторая"}],
    }, ensure_ascii=False))
    chapters.append({"id": f"ch{i}", "number": i, "title": f"Глава {i}",
                     "script_url": f"/content/scripts/ch{i}.lvn"})
for i in range(100):
    open(os.path.join(root, "bg" if i % 2 else "sprites", f"a{i:03d}.png"), "wb").write(os.urandom(2048))
json.dump({"titles": [{"id": "t", "name": "Проба", "seasons": [{"chapters": chapters}]}]},
          open(os.path.join(root, "manifest.json"), "w", encoding="utf-8"), ensure_ascii=False)
PY

"$W/lvnserver" -addr "127.0.0.1:$PORT" -content "$C" -admin-token "stand-$$" >"$W/server.log" 2>&1 &
PID=$!
for _ in $(seq 1 50); do
  curl -fsS -m 1 "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1 && break
  sleep 0.2
done
curl -fsS -m 2 "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1 \
  || { echo "сервер не поднялся:"; tail -5 "$W/server.log"; exit 1; }

B="http://127.0.0.1:$PORT"
bad=""; note() { bad="$bad\n  $1"; }

ver()   { curl -s "$B/v1/content/version" | python3 -c "import json,sys;print(json.load(sys.stdin).get('version',''))"; }
bytes() { curl -s -o /dev/null -w '%{size_download}' "$B$1"; }
delta() { curl -s "$B/v1/content/changes?since=$1"; }
field() { python3 -c "import json,sys;d=json.load(sys.stdin);print(d.get('$1',''))"; }
names() { python3 -c "import json,sys;print(' '.join(sorted((json.load(sys.stdin).get('changed') or {}).keys())))"; }
count() { python3 -c "import json,sys;print(len(json.load(sys.stdin).get('changed') or {}))"; }

# Смена файла обязана менять его хеш; сервер держит карту версий недолгим
# кэшем, поэтому между правкой и вопросом выжидаем его срок.
settle() { sleep 3; }

V0="$(ver)"
[ -n "$V0" ] || { echo "сервер не назвал версию контента"; exit 2; }
delta "$V0" >/dev/null   # версия попала в кольцо снимков

# ── 1. Тишина: опрос ничего не стоит ───────────────────────────────────────
etag="$(curl -s -D - -o /dev/null "$B/v1/content/version" | awk 'tolower($1)=="etag:"{print $2}' | tr -d '\r')"
quiet_code="$(curl -s -o /dev/null -w '%{http_code}' -H "If-None-Match: $etag" "$B/v1/content/version")"
quiet_body="$(curl -s -o /dev/null -w '%{size_download}' -H "If-None-Match: $etag" "$B/v1/content/version")"
[ "$quiet_code" = "304" ] || note "опрос при тишине ответил $quiet_code вместо 304"
[ "$quiet_body" = "0" ] || note "304 приехал с телом в $quiet_body байт"

# ── 2. Цена прежнего пути ──────────────────────────────────────────────────
map_bytes="$(bytes /content/asset-versions.json)"
man_bytes="$(bytes /v1/content/manifest)"
old_cost=$(( map_bytes + man_bytes ))

# ── 3. Правка одной реплики ────────────────────────────────────────────────
python3 - "$C/scripts/ch2.lvn" <<'PY'
import json, sys
p = sys.argv[1]
d = json.load(open(p, encoding="utf-8"))
d["script"][0]["text"] = "глава 2, реплика первая — поправлена автором"
json.dump(d, open(p, "w", encoding="utf-8"), ensure_ascii=False)
PY
if [ -n "$BITE" ]; then
  python3 - "$C/scripts/ch3.lvn" <<'PY'
import json, sys
p = sys.argv[1]
d = json.load(open(p, encoding="utf-8"))
d["script"][0]["text"] = "и третья глава тоже"
json.dump(d, open(p, "w", encoding="utf-8"), ensure_ascii=False)
PY
fi
settle

V1="$(ver)"
[ "$V1" != "$V0" ] || note "правка реплики не изменила версию контента — обновление не доедет вовсе"
D1="$(delta "$V0")"
changed1="$(printf '%s' "$D1" | names)"
n1="$(printf '%s' "$D1" | count)"
full1="$(printf '%s' "$D1" | field full)"
delta_bytes="$(printf '%s' "$D1" | wc -c | tr -d ' ')"
script_bytes="$(bytes /content/scripts/ch2.lvn)"
new_cost=$(( delta_bytes + script_bytes ))

if [ -n "$BITE" ]; then
  if [ "$n1" = "2" ]; then
    echo "укус чист: правку двух файлов стенд увидел двумя ($changed1)"
    exit 0
  fi
  echo "СТЕНД СЛЕП: правили два файла, в разнице $n1 ($changed1) — пропажи он тоже не заметил бы"
  exit 2
fi

[ "$n1" = "1" ] || note "правка одной реплики дала $n1 изменени(й): $changed1"
case "$changed1" in
  *scripts/ch2.lvn*) ;;
  *) note "в разнице нет поправленного скрипта: «$changed1»";;
esac
case "$changed1" in
  *manifest.json*) note "правка реплики назвала манифест изменившимся — клиент пойдёт за 400 КБ зря";;
esac
[ "$full1" != "True" ] || note "на правку реплики сервер попросил забрать всё"

# ── 4. Каталог менялся — за манифестом идти надо ───────────────────────────
python3 - "$C" <<'PY'
import json, os, sys
root = sys.argv[1]
open(os.path.join(root, "scripts", "ch4.lvn"), "w", encoding="utf-8").write(
    json.dumps({"scene": "ch4", "script": [{"op": "say", "text": "новая глава"}]}, ensure_ascii=False))
p = os.path.join(root, "manifest.json")
m = json.load(open(p, encoding="utf-8"))
m["titles"][0]["seasons"][0]["chapters"].append(
    {"id": "ch4", "number": 4, "title": "Глава 4", "script_url": "/content/scripts/ch4.lvn"})
json.dump(m, open(p, "w", encoding="utf-8"), ensure_ascii=False)
PY
settle
V2="$(ver)"
D2="$(delta "$V1")"
changed2="$(printf '%s' "$D2" | names)"
case "$changed2" in
  *manifest.json*) ;;
  *) note "добавили главу, а манифест в разнице не назван: «$changed2» — новая глава не появится";;
esac

# ── 5. Удаление: файл назван удалённым, а не забыт ─────────────────────────
rm -f "$C/scripts/ch4.lvn"
settle
D3="$(delta "$V2")"
removed3="$(printf '%s' "$D3" | python3 -c "import json,sys;print(' '.join(json.load(sys.stdin).get('removed') or []))")"
case "$removed3" in
  *scripts/ch4.lvn*) ;;
  *) note "удалённый файл не назван удалённым: «$removed3» — он останется в кэше игрока навсегда";;
esac

# ── 6. Долгий сон: версия выпала из кольца ─────────────────────────────────
D4="$(delta "версия-которой-никогда-не-было")"
full4="$(printf '%s' "$D4" | field full)"
[ "$full4" = "True" ] || note "незнакомую версию сервер не признал незнакомой — выдумал разницу"

echo "  тишина:   опрос $quiet_code, тело $quiet_body байт"
echo "  реплика:  изменений $n1 ($changed1)"
echo "  каталог:  после новой главы в разнице: $changed2"
echo "  удаление: removed: ${removed3:-—}"
echo "  цена:     прежний путь $old_cost байт (карта $map_bytes + манифест $man_bytes)"
echo "            новый путь  $new_cost байт (разница $delta_bytes + скрипт $script_bytes)"
if [ "$new_cost" -gt 0 ] && [ "$old_cost" -gt 0 ]; then
  echo "            дешевле в $(python3 -c "print(f'{$old_cost/$new_cost:.1f}')") раз(а)"
fi

[ -z "$bad" ] || { echo "РВЁТСЯ:$(printf '%b' "$bad")"; exit 1; }
echo "держит: правка реплики стоит реплику, каталог качается только когда он и менялся"
