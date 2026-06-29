# 🎬 Кинетическая новелла

Кинетическая новелла — это история без единого выбора: игрок только проматывает реплики, а всю драматургию несёт постановка, и здесь движок работает не как ветвление, а как режиссёр.

## Что делает пример

Ночной вокзал под дождём: с первого кадра играет музыка, льёт дождь, а сцена мягко проявляется из прозрачности. Слева въезжает актриса Эмбер, останавливается и «дышит» — еле заметно пульсирует. На воспоминании камера вздрагивает и бьёт вспышка. Затем кадр сменяется: затемнение, новый фон с рассветом, дождь и музыка выключаются, сцена проявляется обратно. В финале короткий «поп» масштабом ставит точку, и всё уходит в чёрный.

## Движок как режиссёр

В кинетической новелле у тебя нет развилок — зато есть полный набор постановочных инструментов, и каждый из них задействован в примере:

- **Фон** — `bg /content/bg/night_station.jpg` задаёт кадр, смена `bg` меняет «локацию».
- **Переходы кадра** — `fade` (полноэкранное затемнение в `black`/`white`/`clear`), `dim` (притушить сцену для фокуса, `alpha=0` вернуть), `flash` (короткая вспышка).
- **Камера** — `camera action=shake …` добавляет физический акцент.
- **Частицы** — `particles type=rain on=true` кладёт слой дождя, `on=false` его снимает.
- **Звук** — `audio channel=music action=play …` запускает музыку, `action=stop` глушит канал.
- **Анимация актёра** — `anim` и `move` оживляют Эмбер: выход по экрану, «дыхание», покачивание, финальный акцент.

## Анимация: одно правило и две формы записи

Правило ровно одно: **разные каналы идут параллельно, а ключи внутри одного канала — по очереди.** «Дыхание» (канал `scale`) и покачивание (канал `rotation`) — это две разные `anim`-строки, и они играют одновременно поверх друг друга. А вот несколько ключей внутри `scale` отыгрываются последовательно.

Записать анимацию можно тремя способами:

1. **One-liner `to=`** — твин от текущего значения к цели одной строкой:
   `anim amber scale to=1.08 dur=0.4 ease=outBack`
2. **Bracket-список `[…]`** — набор значений, растянутый по длительности:
   `anim amber scale [1 1.03 1] 3s yoyo`
3. **Ключевые кадры `keys=`/`path=`** — пары «время:значение»:
   `anim id=amber prop=rotation keys="0:0 1:3 2:-3 3:0" loop=yoyo ease=inOutSine`

**КРИТИЧЕСКИ важное правило кавычек** (самая частая ошибка): значения со **пробелами** внутри кавычек — `keys="…"` и `path="…"` — требуют **legacy-формы** с `id=`/`prop=`. Терс-форма ломается на пробелах. А вот bracket-список `[…]` и one-liner `to=` отлично работают и в терс-форме (`anim amber scale …`).

Сравни две реальные строки из примера. Покачивание головы — ключи с пробелами, поэтому legacy-форма:

```
anim id=amber prop=rotation keys="0:0 1:3 2:-3 3:0" loop=yoyo ease=inOutSine
```

А выход актрисы — путь с пробелами, тоже legacy `id=`/`path=`:

```
move id=amber path="-0.2,0.5 0.28,0.5" dur=1.2 ease=outCubic
```

«Дыхание» же — bracket-список без пробелов в кавычках, поэтому терс-форма работает: `anim amber scale [1 1.03 1] 3s yoyo`.

## Возможности движка, которые тут задействованы

Всё цитатами из `kinetic-novel.lvns`:

- **Звук:** `audio channel=music action=play url="/content/audio/rain_theme.ogg"` и `audio channel=music action=stop`
- **Частицы:** `particles type=rain on=true` / `particles type=rain on=false`
- **Проявление:** `fade to="clear" duration=1.2` и финальное `fade to="black" duration=1.5`
- **Движение по экрану:** `move id=amber path="-0.2,0.5 0.28,0.5" dur=1.2 ease=outCubic`
- **«Дыхание» (bracket + yoyo):** `anim amber scale [1 1.03 1] 3s yoyo`
- **Покачивание (legacy keys + rotation):** `anim id=amber prop=rotation keys="0:0 1:3 2:-3 3:0" loop=yoyo ease=inOutSine`
- **Тряска камеры:** `camera action=shake amplitude=0.02 duration=0.4`
- **Вспышка:** `flash to="white" duration=0.3`
- **Притушить/вернуть:** `dim alpha=0.6 duration=0.5` … `dim alpha=0 duration=0.8`
- **Финальный «поп» (one-liner to= + outBack):** `anim amber scale to=1.08 dur=0.4 ease=outBack`

## Разбор по шагам

1. **Атмосфера.** Сразу ставим фон, музыку и дождь, а потом мягко проявляем кадр:
   ```
   bg /content/bg/night_station.jpg
   audio channel=music action=play url="/content/audio/rain_theme.ogg"
   particles type=rain on=true
   fade to="clear" duration=1.2
   ```
2. **Выход актрисы + «дыхание».** Объявляем актёра, въезжаем им по экрану (`move`) и запускаем зацикленное дыхание (`anim scale … yoyo`) — это два параллельных канала:
   ```
   actor amber left neutral
   move id=amber path="-0.2,0.5 0.28,0.5" dur=1.2 ease=outCubic
   anim amber scale [1 1.03 1] 3s yoyo
   ```
3. **Покачивание.** Добавляем третий канал — `rotation` через ключи. Он играет поверх «дыхания», не отменяя его: `anim id=amber prop=rotation keys="0:0 1:3 2:-3 3:0" loop=yoyo ease=inOutSine`
4. **Акцент памяти.** На реплике из прошлого встряхиваем камеру и бьём вспышкой:
   ```
   camera action=shake amplitude=0.02 duration=0.4
   flash to="white" duration=0.3
   ```
5. **Смена кадра.** Классическая связка «затемнили → поменяли → проявили»: притушили сцену, сменили фон, выключили дождь и музыку, вернули свет:
   ```
   dim alpha=0.6 duration=0.5
   bg /content/bg/empty_platform_dawn.jpg
   particles type=rain on=false
   audio channel=music action=stop
   dim alpha=0 duration=0.8
   ```
6. **Финальный «поп» и финал.** Короткое увеличение масштабом как точка в сцене, затем уход в чёрный:
   ```
   actor amber center smile
   anim amber scale to=1.08 dur=0.4 ease=outBack
   fade to="black" duration=1.5
   -> __end
   ```

## Запуск и проверка

```sh
# собрать транскодер
cd tools/lvnconv && go build -o /tmp/lvnconv .

# скомпилировать .lvns → .lvn
/tmp/lvnconv convert -i howto/kinetic-novel/kinetic-novel.lvns -o /tmp/kn.lvn

# структурная проверка
/tmp/lvnconv validate /tmp/kn.lvn
```

Цель — **0 warning(s)**. Важный момент: неверная форма `anim`/`move` (например, `keys=`/`path=` с пробелами без legacy `id=`/`prop=`) даёт ошибку компиляции, а не молчаливый пропуск. Это и есть защита — транскодер не пропустит сломанную постановку в контейнер.

## Сделай своим

- Поиграй с easing и ключами: смени `ease=outBack` на `outCubic`, добавь больше ключей в `keys="…"` для сложного покачивания.
- Запусти параллельные каналы: две `anim`-строки (например, `scale` и `alpha`) на одном актёре сыграют одновременно.
- Собери цепочку кадров: несколько связок `dim → bg → dim alpha=0`, чтобы провести героя по локациям.
- Подбери другие `particles` (`snow`) и движения `camera` (`zoom`/`pan`) под настроение сцены.
- Добавь звуковые акценты через `audio channel=sfx action=play …` на ключевых репликах.

## Дальше

- [Справочник языка](../LANGUAGE.md)
- [Система анимации](../../docs/animation-system.md)
- [Книга рецептов](../recipes.md)
- [Все жанры](../README.md)
