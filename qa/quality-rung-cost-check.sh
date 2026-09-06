#!/usr/bin/env bash
# «ПОМЕНЬШЕ» ОБЯЗАНО ЗНАЧИТЬ ДЕШЕВЛЕ — иначе это украшение в настройках.
#
# Ступень качества существует ради одного: игрок на слабом телефоне или дорогом
# интернете выбирает «поменьше» и платит меньше. Проверять это по коду нельзя —
# ответ виден только на проводе, в байтах, которые ушли с сервера.
#
# Соседние проверки берут другие половины: `art-rung` — что с провода уходит
# ИМЕННО выбранная ступень, `raw-art` — что сырое не отдаётся ни в одну дверь.
# Здесь третья, ради которой существуют обе: ЦЕНА главы целиком.
#
#   ЛЕСТНИЦА      1k дешевле 1440, 1440 дешевле 2k, 2k не дороже исходника;
#   ВЫГОДА        «поменьше» против исходника — во сколько раз, числом;
#   МЕЛОЧЬ        у маленькой картинки ступени нет — приходит исходник, 200,
#                 а не 404 и не пустое место (иначе настройка качества
#                 выглядит как сломанный движок);
#   ВЫДУМКА       ступень, которой в движке нет (@9k), НЕ обслуживается: сервер
#                 режет по четырём известным ступеням, а не по любому числу из
#                 запроса. Это защита, а не пробел — иначе один клиент заказал
#                 бы сотню размеров и занял машину пересборкой.
#
#   qa/quality-rung-cost-check.sh [-bite]
#
# -bite просит ВСЕ ступени по одному адресу (без суффикса): цена обязана
# перестать падать. Мерка, у которой «дешевле» получается всегда, не доказала бы
# ничего и про настоящие ступени.
set -uo pipefail
cd "$(dirname "$0")/.."
BITE=""; [ "${1:-}" = "-bite" ] && BITE=1

command -v go      >/dev/null 2>&1 || { echo "нет go — пропускаю"; exit 0; }
command -v curl    >/dev/null 2>&1 || { echo "нет curl — пропускаю"; exit 0; }
command -v python3 >/dev/null 2>&1 || { echo "нет python3 — пропускаю"; exit 0; }
python3 -c "import zlib" 2>/dev/null || { echo "нет zlib в python — пропускаю"; exit 0; }

W="$(mktemp -d)"; PID=""
cleanup() { [ -n "$PID" ] && kill "$PID" 2>/dev/null; rm -rf "$W"; }
trap cleanup EXIT

go build -C server -o "$W/lvnserver" . || { echo "сервер не собрался"; exit 1; }

# Свобода порта — попыткой на нём слушать, а не вопросом healthz: занять его
# может кто угодно, и стенд, падающий по случайности, обесценивает прогон.
can_bind() {
  python3 -c "
import socket, sys
s = socket.socket()
s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
try:
    s.bind(('127.0.0.1', int(sys.argv[1])))
except OSError:
    sys.exit(1)
finally:
    s.close()
" "$1"
}
PORT=0
for p in 8201 8203 8205 8207; do can_bind "$p" && { PORT=$p; break; }; done
[ "$PORT" = "0" ] && { echo "порты заняты — пропускаю"; exit 0; }

# ── Каталог: крупный арт (ступени имеют смысл) и мелочь (у неё их не бывает) ──
C="$W/content"; mkdir -p "$C/art"
python3 - "$C" <<'PY'
import json, os, struct, sys, zlib, random
root = sys.argv[1]

def png(path, w, h):
    # Шумная картинка: ровная заливка сжалась бы в килобайт, и разница между
    # ступенями утонула бы в постоянных расходах формата.
    random.seed(w * 7919 + h)
    raw = bytearray()
    for y in range(h):
        raw.append(0)
        for x in range(w):
            raw += bytes((random.randrange(256), random.randrange(256), random.randrange(256), 255))
    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xffffffff))
    ihdr = struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)
    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr)
                + chunk(b"IDAT", zlib.compress(bytes(raw), 6)) + chunk(b"IEND", b""))

крупные = [("hall", 2400, 1600), ("street", 2200, 1500), ("room", 2000, 1400)]
мелкие = [("icon", 220, 220), ("badge", 180, 120)]
for name, w, h in крупные + мелкие:
    png(os.path.join(root, "art", name + ".png"), w, h)
json.dump({"titles": []}, open(os.path.join(root, "manifest.json"), "w"))
print(" ".join(n for n, _, _ in крупные), "|", " ".join(n for n, _, _ in мелкие))
PY

"$W/lvnserver" -addr "127.0.0.1:$PORT" -content "$C" -admin-token "stand-$$" >"$W/server.log" 2>&1 &
PID=$!
for _ in $(seq 1 60); do
  curl -fsS -m 1 "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1 && break
  sleep 0.2
done
curl -fsS -m 2 "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1 \
  || { echo "сервер не поднялся:"; tail -5 "$W/server.log"; exit 1; }

B="http://127.0.0.1:$PORT"
bad=""; note() { bad="$bad\n  $1"; }
BIG="hall street room"

# Цена главы на одной ступени: сумма байтов всех её картинок.
cost() { # $1 = суффикс ступени ("" = исходник)
  local sfx="$1" total=0 n
  for n in $BIG; do
    [ -n "$BITE" ] && sfx=""      # укус: спрашиваем один и тот же адрес
    n="$(curl -s -o /dev/null -w '%{size_download}' "$B/content/art/$n$sfx.png")"
    total=$((total + n))
  done
  echo "$total"
}

orig="$(cost "")"
eco="$(cost "@1k")"
mid="$(cost "@1440")"
big="$(cost "@2k")"
[ "$orig" -gt 0 ] || { echo "исходники не отдаются — проверять нечего"; exit 2; }

if [ -n "$BITE" ]; then
  if [ "$eco" = "$orig" ]; then
    echo "укус чист: спросили один адрес — цена не изменилась ($eco = $orig), мерка это видит"
    exit 0
  fi
  echo "СТЕНД СЛЕП: адрес один, а цена почему-то разная ($eco против $orig)"
  exit 2
fi

# ── Мелочь: ступени у неё нет, и это не поломка ────────────────────────────
small_code=""; small_size=""
for n in icon badge; do
  read -r code size < <(curl -s -o /dev/null -w '%{http_code} %{size_download}' "$B/content/art/$n@1k.png" | awk '{print $1, $2}')
  small_code="$small_code $code"; small_size="$small_size $size"
  [ "$code" = "200" ] || note "у маленькой картинки ($n) ступень 1k ответила $code — игрок увидит дыру вместо арта"
  [ "${size:-0}" -gt 0 ] || note "маленькая картинка ($n) пришла пустой"
done

# ── Выдуманная ступень: сервер не изобретает несуществующее ────────────────
# ВЫДУМАННАЯ СТУПЕНЬ. Первая редакция стенда ждала здесь исходник и объявила
# отказ дырой. Проверка кода сняла обвинение: сервер знает ровно четыре
# ступени (@mini, @1k, @1440, @2k) и по ним же режет; всё остальное — не
# вариант, а просто файл, которого нет. Отдавать на такой запрос исходник
# значило бы раздавать полноразмерный арт по любому неверному адресу, а
# делать новый размер по требованию — открыть дверь для нагрузки.
read -r fake_code fake_size < <(curl -s -o /dev/null -w '%{http_code} %{size_download}' "$B/content/art/hall@9k.png" | awk '{print $1, $2}')

echo "  цена главы:  исходник $orig б | 2k $big | 1440 $mid | 1k $eco"
echo "  мелочь:      коды$small_code, байты$small_size"
echo "  выдумка:     @9k → код $fake_code (ступени нет — и не должно быть)"
if [ "$eco" -gt 0 ]; then
  echo "  выгода:      «поменьше» дешевле исходника в $(python3 -c "print(f'{$orig/$eco:.1f}')") раз(а)"
fi

[ "$eco" -lt "$mid" ] || note "1k ($eco) не дешевле 1440 ($mid) — лестница не лестница"
[ "$mid" -lt "$big" ] || note "1440 ($mid) не дешевле 2k ($big)"
[ "$big" -le "$orig" ] || note "2k ($big) дороже исходника ($orig)"
python3 -c "import sys; sys.exit(0 if $orig >= $eco * 3 else 1)" \
  || note "«поменьше» экономит меньше чем втрое ($orig против $eco) — настройка не стоит своего места"
[ "$fake_code" = "404" ] \
  || note "несуществующая ступень @9k ответила $fake_code — сервер делает размеры по чужому запросу, а это дверь для нагрузки"

[ -z "$bad" ] || { echo "РВЁТСЯ:$(printf '%b' "$bad")"; exit 1; }
echo "держит: лестница дешевеет вниз, мелочь отдаётся как есть, чужие размеры не изготавливаются"
