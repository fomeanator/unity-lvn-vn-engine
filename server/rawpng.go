package main

// rawpng.go — СЫРОЕ НАРУЖУ НЕ ОТДАЁМ.
//
// Замер на проде 06.09: 28 исходников в art/ — 238 МБ из 424 — это PNG БЕЗ
// СЖАТИЯ. Размер файла равен w×h×4: инструмент художника сложил сырой массив
// пикселей в оболочку PNG (deflate «store»), импорт скопировал файл вербатим
// (importer/bundle_assets.go, copyFile), а раздача отдала как есть. Хуже
// того, отдавала его и дверь ВАРИАНТОВ: слой облика 1212×2048 в бокс «2k»
// уже влезает, и на «@2k.png» сервер честно отвечал исходником — 9 951 144
// байта там, где тот же кадр без потерь весит 2,2 МБ (замер: x4,5, пиксель в
// пиксель). Двадцать один такой файл у одного персонажа — 200 МБ трафика
// и кэша на ровном месте, при любой ступени качества.
//
// Правило владельца: оригинал игроку не уходит НИ ПРИ КАКОЙ настройке. Самый
// дешёвый способ его исполнить — не держать сырое на диске: сырой PNG
// лечится НА МЕСТЕ, пережатием без потерь. Пиксели сверяются декодом обратно,
// а не обещаются; подмена атомарная; mtime сохраняется, чтобы производные
// (@1k, @1440, .ktx2) не сочли исходник обновлённым — пиксели те же, значит и
// коды те же. Меняется только размер, и именно он двигает ETag и хэш версии:
// клиент перекачает файл один раз — маленьким.
//
// Три двери, через которые сырое могло выйти: статическая раздача, дверь
// вариантов (исходник, влезающий в бокс) и обход контента при старте. Лечение
// на здоровом файле стоит один stat и 33 байта заголовка — его можно звать
// на каждый запрос.

import (
	"bytes"
	"encoding/binary"
	"fmt"
	"image"
	"image/png"
	"io"
	"io/fs"
	"log"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

// pngLooksRaw — сырой ли PNG, по одному заголовку: файл не меньше 95 % от
// w×h×(байт на пиксель) плюс байт фильтра на строку. Нормально сжатый арт
// лежит в разы ниже; выше этой планки бывает только deflate «store».
func pngLooksRaw(path string) bool {
	f, err := os.Open(path)
	if err != nil {
		return false
	}
	defer f.Close()
	var hdr [33]byte
	if _, err := io.ReadFull(f, hdr[:]); err != nil {
		return false
	}
	if string(hdr[:8]) != "\x89PNG\r\n\x1a\n" || string(hdr[12:16]) != "IHDR" {
		return false
	}
	w := int64(binary.BigEndian.Uint32(hdr[16:20]))
	h := int64(binary.BigEndian.Uint32(hdr[20:24]))
	depth, ctype := int64(hdr[24]), hdr[25]
	channels := map[byte]int64{0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[ctype]
	// Глубина меньше байта — палитра или битовая маска: такие файлы малы и
	// сырыми не бывают; считать их дробными байтами незачем.
	if channels == 0 || depth < 8 || w <= 0 || h <= 0 {
		return false
	}
	info, err := f.Stat()
	if err != nil {
		return false
	}
	expected := w*h*channels*depth/8 + h
	return info.Size()*100 >= expected*95
}

// samePixels — совпадают ли две картинки побитово. Быстрый путь по общему
// буферу, когда декодер дал один и тот же тип; иначе — по каждому пикселю.
func samePixels(a, b image.Image) bool {
	if a.Bounds() != b.Bounds() {
		return false
	}
	switch x := a.(type) {
	case *image.NRGBA:
		if y, ok := b.(*image.NRGBA); ok {
			return bytes.Equal(x.Pix, y.Pix)
		}
	case *image.RGBA:
		if y, ok := b.(*image.RGBA); ok {
			return bytes.Equal(x.Pix, y.Pix)
		}
	case *image.NRGBA64:
		if y, ok := b.(*image.NRGBA64); ok {
			return bytes.Equal(x.Pix, y.Pix)
		}
	case *image.Gray:
		if y, ok := b.(*image.Gray); ok {
			return bytes.Equal(x.Pix, y.Pix)
		}
	}
	r := a.Bounds()
	for y := r.Min.Y; y < r.Max.Y; y++ {
		for x := r.Min.X; x < r.Max.X; x++ {
			ar, ag, ab, aa := a.At(x, y).RGBA()
			br, bg, bb, ba := b.At(x, y).RGBA()
			if ar != br || ag != bg || ab != bb || aa != ba {
				return false
			}
		}
	}
	return true
}

// healRawPNG — пережать PNG без потерь на месте. Возвращает размер до и
// после; равные значат «не трогали». Уровень сжатия — стандартный: замер на
// живом файле 9,95 МБ дал 2,20 МБ за 0,9 с против 2,19 МБ за 2,2 с у
// максимального; запрос игрока ждёт этой работы, и лишняя секунда ради
// половины процента — не та цена.
func healRawPNG(path string) (before, after int64, err error) {
	info, err := os.Stat(path)
	if err != nil {
		return 0, 0, err
	}
	before = info.Size()
	data, err := os.ReadFile(path)
	if err != nil {
		return before, before, err
	}
	src, err := png.Decode(bytes.NewReader(data))
	if err != nil {
		return before, before, fmt.Errorf("%s: decode: %w", path, err)
	}
	var buf bytes.Buffer
	if err := png.Encode(&buf, src); err != nil {
		return before, before, fmt.Errorf("%s: encode: %w", path, err)
	}
	if int64(buf.Len()) >= before {
		return before, before, nil // файл не вырастет ни при каких условиях
	}
	// ПИКСЕЛЬ В ПИКСЕЛЬ — проверяется, а не обещается: кодировщик мог бы
	// сменить цветовую модель (серый с альфой у Go живёт как RGBA), и это
	// законно, пока каждый пиксель читается тем же самым.
	back, err := png.Decode(bytes.NewReader(buf.Bytes()))
	if err != nil || !samePixels(src, back) {
		return before, before, fmt.Errorf("%s: пережатие изменило пиксели — оставлен как есть", path)
	}
	// Имя с точки: такой сегмент раздача и обход версий считают служебным
	// (privateRel) — полуфайл не уйдёт наружу и не попадёт в хэш версии.
	dir, base := filepath.Split(path)
	tmp := filepath.Join(dir, fmt.Sprintf(".heal-%d-%s", time.Now().UnixNano(), base))
	if err := os.WriteFile(tmp, buf.Bytes(), 0o644); err != nil {
		return before, before, err
	}
	if err := os.Rename(tmp, path); err != nil {
		_ = os.Remove(tmp)
		return before, before, err
	}
	// Производные (@1k, .ktx2) сверяются с исходником по mtime. Пиксели не
	// изменились — значит, и они верны; новый mtime заставил бы сервер
	// перекодировать всё заново ради того же результата.
	_ = os.Chtimes(path, time.Now(), info.ModTime())
	return before, int64(buf.Len()), nil
}

// rawHealer — замок на путь и память о неизлечимых. Два запроса одного файла
// не должны пережимать его наперегонки, а файл, который не переписался (диск
// только для чтения, чужие права), не должен ронять в лог одну и ту же
// строку на каждый запрос.
type rawHealer struct {
	mu     sync.Mutex
	locks  map[string]*sync.Mutex
	failed map[string]bool
}

var raws = &rawHealer{locks: map[string]*sync.Mutex{}, failed: map[string]bool{}}

func (h *rawHealer) lockFor(path string) (*sync.Mutex, bool) {
	h.mu.Lock()
	defer h.mu.Unlock()
	if h.failed[path] {
		return nil, false
	}
	m, ok := h.locks[path]
	if !ok {
		m = &sync.Mutex{}
		h.locks[path] = m
	}
	return m, true
}

// heal — вылечить файл, если он сырой; иначе не стоит ничего, кроме stat и
// заголовка. Тяжёлая работа идёт под общим лимитом heavyGen — тем же, что у
// вариантов и кодов: декодированный кадр в памяти один и тот же.
func (h *rawHealer) heal(path string) (before, after int64) {
	if !strings.EqualFold(filepath.Ext(path), ".png") || !pngLooksRaw(path) {
		return 0, 0
	}
	lock, ok := h.lockFor(path)
	if !ok {
		return 0, 0
	}
	lock.Lock()
	defer lock.Unlock()
	if !pngLooksRaw(path) {
		return 0, 0 // сосед успел раньше
	}
	heavyGen <- struct{}{}
	before, after, err := healRawPNG(path)
	<-heavyGen
	if err != nil {
		h.mu.Lock()
		h.failed[path] = true
		h.mu.Unlock()
		log.Printf("raw-png: %v", err)
		return 0, 0
	}
	if after < before {
		log.Printf("raw-png: %s пережат без потерь %.2f → %.2f МБ (x%.1f)",
			filepath.Base(path), float64(before)/1e6, float64(after)/1e6, float64(before)/float64(after))
	}
	return before, after
}

// healRawTree — обход контента при старте: вылечить всё сырое до того, как
// первый игрок его спросит. Идёт в один поток, файл за файлом — это работа
// «когда-нибудь», а игра идёт сейчас. Служебные поддеревья не обходятся.
func healRawTree(root string) {
	var n int
	var was, now int64
	_ = filepath.WalkDir(root, func(path string, d fs.DirEntry, err error) error {
		if err != nil {
			return nil
		}
		rel, rerr := filepath.Rel(root, path)
		if rerr != nil {
			return nil
		}
		rel = filepath.ToSlash(rel)
		if d.IsDir() {
			if rel != "." && privateRel(rel+"/") {
				return filepath.SkipDir
			}
			return nil
		}
		if privateRel(rel) {
			return nil
		}
		before, after := raws.heal(path)
		if after > 0 && after < before {
			n++
			was += before
			now += after
		}
		return nil
	})
	if n > 0 {
		log.Printf("raw-png: при старте вылечено %d файлов: %.0f → %.0f МБ", n, float64(was)/1e6, float64(now)/1e6)
	}
}
