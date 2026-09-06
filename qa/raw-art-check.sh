#!/usr/bin/env bash
# СЫРОЕ НАРУЖУ НЕ ОТДАЁМ — замер по проводу.
#
# Художник сохраняет PNG без сжатия (deflate «store»: файл равен w×h×4), импорт
# копирует его вербатим, и игроку уезжает 9,95 МБ там, где тот же кадр без
# потерь весит 2,2 МБ. На проде 06.09 таких было 28 файлов на 238 МБ — больше
# половины всего арта. Хуже того, уезжали они и на ступени «2k»: исходник,
# который в бокс 2048 уже влезает, дверь вариантов отдавала как есть.
#
# Стенд кладёт сырой файл в контент, поднимает НАСТОЯЩИЙ сервер и спрашивает
# его в обе двери — по адресу без ступени и через «@2k». В обоих ответах
# требуется два свойства разом: файл полегчал хотя бы вдвое И его пиксели
# совпадают с исходником побитово. Первое без второго — потеря качества,
# второе без первого — сырое наружу.
#
# ПРОВЕРКА ОБЯЗАНА УМЕТЬ ЗАМЕЧАТЬ. -bite ставит на место сервера голую
# раздачу файлов (python http.server), которая правила не знает, и требует,
# чтобы стенд назвал сырое сырым: замер, который не краснеет на заведомо
# сырых байтах, не стоит ничего.
#
#   qa/raw-art-check.sh [-bite]
set -uo pipefail
cd "$(dirname "$0")/.."
BITE=""; [ "${1:-}" = "-bite" ] && BITE=1

command -v go      >/dev/null 2>&1 || { echo "нет go — пропускаю"; exit 0; }
command -v curl    >/dev/null 2>&1 || { echo "нет curl — пропускаю"; exit 0; }
command -v python3 >/dev/null 2>&1 || { echo "нет python3 — пропускаю"; exit 0; }

W="$(mktemp -d)"
PIDS=""
cleanup() {
  for p in $PIDS; do kill "$p" 2>/dev/null; done
  rm -rf "$W"
}
trap cleanup EXIT

go build -C server -o "$W/lvnserver" . || { echo "сервер не собрался"; exit 1; }

# Инструмент стенда: делает сырой PNG и сверяет два PNG пиксель в пиксель.
# Go, а не PIL: библиотека картинок есть не на каждой машине, а go уже нужен.
mkdir -p "$W/tool"
cat > "$W/tool/go.mod" <<'EOF'
module rawtool
go 1.22
EOF
cat > "$W/tool/main.go" <<'EOF'
package main

import (
	"bytes"
	"fmt"
	"image"
	"image/color"
	"image/png"
	"os"
)

func main() {
	switch os.Args[1] {
	case "make": // make <path> <w> <h> — облик с альфой, deflate «store»
		var w, h int
		fmt.Sscan(os.Args[3], &w)
		fmt.Sscan(os.Args[4], &h)
		img := image.NewNRGBA(image.Rect(0, 0, w, h))
		for y := 0; y < h; y++ {
			for x := 0; x < w; x++ {
				a := uint8(255)
				if x < w/8 {
					a = 0
				} else if x%3 == 0 {
					a = 200
				}
				img.SetNRGBA(x, y, color.NRGBA{R: uint8(x), G: uint8(y), B: uint8(x / 7), A: a})
			}
		}
		var buf bytes.Buffer
		enc := png.Encoder{CompressionLevel: png.NoCompression}
		if err := enc.Encode(&buf, img); err != nil {
			panic(err)
		}
		if err := os.WriteFile(os.Args[2], buf.Bytes(), 0o644); err != nil {
			panic(err)
		}
	case "same": // same <a.png> <b.png> — код 0, если пиксели совпадают
		a, err := decode(os.Args[2])
		if err != nil {
			fmt.Println("не декодируется:", err)
			os.Exit(3)
		}
		b, err := decode(os.Args[3])
		if err != nil {
			fmt.Println("не декодируется:", err)
			os.Exit(3)
		}
		if a.Bounds() != b.Bounds() {
			fmt.Println("разный размер")
			os.Exit(1)
		}
		r := a.Bounds()
		for y := r.Min.Y; y < r.Max.Y; y++ {
			for x := r.Min.X; x < r.Max.X; x++ {
				if a.At(x, y) != b.At(x, y) {
					fmt.Printf("расходятся в (%d,%d)\n", x, y)
					os.Exit(1)
				}
			}
		}
	}
}

func decode(p string) (image.Image, error) {
	data, err := os.ReadFile(p)
	if err != nil {
		return nil, err
	}
	return png.Decode(bytes.NewReader(data))
}
EOF
( cd "$W/tool" && go build -o "$W/rawtool" . ) || { echo "инструмент стенда не собрался"; exit 1; }

# Контент: один сырой слой облика. 900×1600 влезает в бокс 2048 — ровно тот
# случай, когда «@2k» отдавал исходник как есть.
mkdir -p "$W/content/art"
printf '{"titles":[]}' > "$W/content/manifest.json"
"$W/rawtool" make "$W/content/art/hero.png" 900 1600
cp "$W/content/art/hero.png" "$W/hero.orig.png"
RAW=$(stat -f%z "$W/hero.orig.png" 2>/dev/null || stat -c%s "$W/hero.orig.png")
echo "сырой слой: $RAW байт (900×1600×4 = $((900*1600*4)))"

free_port() {
  for p in "$@"; do
    curl -fsS -m 1 "http://127.0.0.1:$p/healthz" >/dev/null 2>&1 || { echo "$p"; return 0; }
  done
  echo 0
}
PORT="$(free_port 8086 8087 8088 8089)"
[ "$PORT" = "0" ] && { echo "порты заняты — пропускаю"; exit 0; }

wait_up() {
  for _ in $(seq 1 50); do
    curl -fsS -m 1 "$1" >/dev/null 2>&1 && return 0
    sleep 0.2
  done
  return 1
}

if [ -n "$BITE" ]; then
  # Голая раздача: правила «сырое наружу не отдаём» у неё нет по определению.
  ( cd "$W" && python3 -m http.server "$PORT" --bind 127.0.0.1 >"$W/plain.log" 2>&1 ) &
  PIDS="$PIDS $!"
  wait_up "http://127.0.0.1:$PORT/content/manifest.json" || { echo "голая раздача не поднялась"; exit 2; }
  URLS="/content/art/hero.png"
else
  "$W/lvnserver" -addr "127.0.0.1:$PORT" -content "$W/content" -admin-token "stand-$$" >"$W/server.log" 2>&1 &
  PIDS="$PIDS $!"
  wait_up "http://127.0.0.1:$PORT/healthz" || { echo "сервер не поднялся:"; tail -3 "$W/server.log"; exit 2; }
  URLS="/content/art/hero.png /content/art/hero@2k.png"
fi

# Замер одной двери: сколько байт ушло и те ли это пиксели.
raw_seen=""; broken=""
for u in $URLS; do
  out="$W/got-$(echo "$u" | tr '/@' '__')"
  code="$(curl -s -o "$out" -w '%{http_code}' "http://127.0.0.1:$PORT$u")"
  size=$(stat -f%z "$out" 2>/dev/null || stat -c%s "$out")
  verdict="пережат"
  if [ "$code" != "200" ]; then verdict="код $code"; broken=1
  elif [ $((size * 2)) -gt "$RAW" ]; then verdict="СЫРОЕ"; raw_seen=1
  fi
  same="пиксели те же"
  if [ "$code" = "200" ] && ! "$W/rawtool" same "$W/hero.orig.png" "$out" >/dev/null; then same="ПИКСЕЛИ ДРУГИЕ"; broken=1; fi
  printf "  %-28s %9s байт  %s, %s\n" "$u" "$size" "$verdict" "$same"
done

if [ -n "$BITE" ]; then
  if [ -n "$raw_seen" ]; then
    echo "укус чист: голая раздача отдала сырое, и стенд назвал это сырым — его «держит» чего-то стоит"
    exit 0
  fi
  echo "СТЕНД ВРЁТ: заведомо сырые байты прошли за пережатые — такому замеру верить нельзя"
  exit 2
fi

if [ -n "$broken" ]; then
  echo "РВЁТСЯ: дверь не ответила или отдала не те пиксели — это уже не «без потерь»"
  exit 1
fi
if [ -n "$raw_seen" ]; then
  echo "РВЁТСЯ: оригинал ушёл наружу — игрок качает несжатый арт при любой ступени"
  exit 1
fi
# Лечение — на месте: файл на диске обязан полегчать сам, а не только ответ.
disk=$(stat -f%z "$W/content/art/hero.png" 2>/dev/null || stat -c%s "$W/content/art/hero.png")
if [ $((disk * 2)) -gt "$RAW" ]; then
  echo "РВЁТСЯ: ответ пережат, а на диске по-прежнему сырое ($disk байт) — экспорт и сид унесут оригинал"
  exit 1
fi
echo "держит: в обе двери уходит пережатый файл ($disk байт против $RAW), пиксель в пиксель"
