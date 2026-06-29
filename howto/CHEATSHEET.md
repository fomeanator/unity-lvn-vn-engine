# Шпаргалка `.lvns` (одна страница)

Плотная карта синтаксиса. Подробности — [`LANGUAGE.md`](LANGUAGE.md);
возможности/лимиты — [`CAPABILITIES.md`](CAPABILITIES.md).

```
scene my-game                  // (рекомендуется) заголовок главы
// комментарий
```

## Текст
```
Просто строка.                 // нарратив
Мара: Реплика.                 // говорящий: текст
Мара [smile]: Реплика.         // + эмоция каста (ось emotion)
actor_map Мара=mara            // связать Имя ↔ id каста
«Длинный текст,                // французские кавычки = многострочно
 на пару строк.»
Золота: {gold}, атк {atk+5}.   // интерполяция {выражение}
{{ и }}                        // литеральные фигурные скобки
```

## Выбор и переходы
```
- Вариант А -> labelA          // меню (подряд идущие строки с «- »)
- Вариант Б -> labelB cost="3 хода"
- Скрытый -> labelC expr="gold >= 5"      // вариант скрыт, если ложно
-> label                        // безусловный прыжок (goto)
:label                          // метка-цель
-> __end                        // встроенный конец
```

## Условия и состояние
```
gold = 12                       // присвоить (объявление = мутация)
gold = gold - 6
name = "Мара"   inv = []        // строка, пустой список
if gold >= 10 -> rich           // истина → прыжок; иначе дальше
if has(inv,"ключ") {            // блок if/else
  ...
} else {
  ...
}
```

## Циклы, подпрограммы, функции
```
for it in inv { Предмет: {it}. }
while xp >= need { xp = xp - need  level = level + 1 }
call fight                      // прыжок с возвратом
return                          // вернуться после call
func add(a,b){ return a + b }   // функция (сахар над call/return)
s = add(2,3)                    // вызов с возвратом значения
save                            // снимок (слот по умолч.)
load
```

## Постановка
```
bg /content/bg/room.jpg                 // фон
actor mara left smile                   // персонаж: id, позиция, эмоция/поза
actor hero center w=.5 h=.6 x=.5 armor={arm}
actor mara hide                         // спрятать
obj id=key sprite_url="/ui/key.png" x=.2 y=.7 anchor="0.5,0.5" on_click="take"
text hud x=4 y=8 size=42 color=#f1e4c9 «♥{hp}/{maxhp}  💰{gold}»   // реактивный HUD (200мс)
text hud hide
```
Позиции: `far_left left center_left center center_right right far_right`.
Поля: `w`(width) `h`(height) `x` `y` `scale` `anchor="ax,ay"` `z` `flip` `rotation` `opacity` `on_click`.

## Эффекты / звук / тайминг
```
fade to="black" duration=0.8     dim alpha=0.6 duration=0.5     flash to="white" duration=0.3
tint ...    blur ...
camera action=shake amplitude=0.02 duration=0.4      // shake/zoom/pan/reset
particles type=rain on=true                          // rain/snow
audio channel=music action=play url="/a.ogg"         // music/sfx/ambient; play/stop
wait ms=500
```

## Анимация (каналы || параллельно, ключи в канале — по очереди)
```
anim mara scale to=1.1 dur=0.4 ease=outBack          // one-liner (терс ок)
anim mara scale [1 1.03 1] 3s yoyo                   // bracket-список (терс ок)
anim id=mara prop=rotation keys="0:0 1:8 2:-8 3:0" loop=yoyo ease=inOutSine  // keys= → ТОЛЬКО legacy id=/prop=
move id=mara path="-0.2,0.5 0.28,0.5" dur=1.2 ease=outCubic                  // path= → ТОЛЬКО legacy id=
anim mara stop
```
props: `x y screen_x screen_y scale scalex scaley rotation alpha frame` · ease: `linear inOutSine outCubic outBack inBack` · loop: `once|restart|yoyo`.

## Встроенные функции (выражения)
```
rand() rand(n) rand(a,b)  chance(p)  min(a,b) max(a,b)  abs floor round   // ceil НЕТ
len(x) has(coll,x) get(coll,k[,def]) indexof(arr,x) count(arr,x) sum(arr) first(arr) last(arr) keys(o) vals(o)
list(...) push(arr,x) pop(arr) removeat(arr,i) remove(arr,x) slice(arr,s[,e]) concat(...) put(m,k,v) del(m,k)
```
Операторы: `+ - * /` · `== != > >= < <=` · `&& || !`. Незаданная переменная = `0`/`""`/`false`.

## Сборка / проверка
```
cd tools/lvnconv && go build -o /tmp/lvnconv .
/tmp/lvnconv convert -i game.lvns -o /tmp/game.lvn
/tmp/lvnconv validate /tmp/game.lvn        # цель: OK ... 0 warning(s)
```

## ⚠ Лимиты-ловушки
- Каст определяется в `manifest.json`/`cast`-блоке, **не в `.lvns`** (там только `actor <id>`).
- `keys=`/`path=` со пробелами → форма `id=`/`prop=` (иначе ошибка компиляции).
- `hint` — no-op (не рисуется). `cost`/`requires_stat` сами ресурсы не списывают.
- Нет таймера реального времени (мерь ходами), нет ввода текста (только `choice`/клики), нет `ceil`.
- Перед меткой-целью прыжка, если в неё «проваливаются» сверху, ставь `-> __end`/`-> метка`.
