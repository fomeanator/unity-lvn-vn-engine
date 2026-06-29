# Книга рецептов `.lvns`

Короткие переиспользуемые куски, из которых собирается почти любая механика.
Все паттерны взяты из проверенных примеров в этой папке — копируй и подгоняй.
Справочник по любому элементу — [`LANGUAGE.md`](LANGUAGE.md).

---

## Счётчик / накопление

```
score = 0
score = score + 1          // прибавить
score = score - 1          // убавить
hp = min(maxhp, hp + 10)   // прибавить, но не выше потолка
gold = max(0, gold - 5)    // убавить, но не ниже нуля
```

## Реактивная панель (HUD)

`text` с шаблоном в `«…»` пересчитывается сам (~5 раз в секунду) — идеально для
очков, здоровья, ресурсов.

```
text hud x=4 y=8 size=42 color=#f1e4c9 «♥{hp}/{maxhp}   💰{gold}   ур.{level}»
text hud hide              // спрятать панель
```

## Развилка по условию

```
if gold >= 100 -> rich      // истина → прыжок; иначе падаем дальше
if hp <= 0 -> dead
-> normal                   // ветка «по умолчанию»
```

## Блок if / else

```
if has(inv, "ключ") {
  Дверь открывается ключом.
  -> next_room
} else {
  Заперто. Нужен ключ.
}
```

## Меню выбора

```
- Сражаться -> fight
- Бежать -> flee
- Поговорить -> talk cost="потратишь ход"
```

## Скрытый/заблокированный вариант

Вариант появляется в меню только когда `expr` истинно.

```
- Применить заклинание -> cast expr="mana >= 10"
- Открыть дверь ключом -> open expr="has(inv, \"ключ\")"
```
> Кавычки внутри `expr=` экранируй как `\"`.

## Инвентарь (список)

```
inv = []                            // создать
inv = push(inv, "зелье")            // добавить
if has(inv, "зелье") -> use_potion  // проверить наличие
inv = removeat(inv, indexof(inv, "зелье"))   // выбросить одну штуку
Предметов в сумке: {len(inv)}.      // счётчик
for it in inv {                      // перебрать
  - {it}
}
```

## Лавка (покупка с проверкой денег)

```
:buy_sword
if gold >= 12 {
  gold = gold - 12
  atk = atk + 3
  Куплен меч (+3 к атаке).
} else {
  Не хватает золота.
}
-> shop
```

## Бросок кости / случайность

```
r = rand(1, 6)              // целое 1..6 включительно
if chance(0.7) -> success   // 70% шанс
crit = rand(0, 3)           // 0..3 (разброс урона)
loot = rand(8, 20)
```

## Случайное событие (взвешенный выбор ветки)

```
roll = rand(1, 10)
if roll <= 4 -> common      // 40%
if roll <= 7 -> uncommon    // 30%
if roll <= 9 -> rare        // 20%
-> jackpot                  // 10%
```

## Метр отношений / репутации

```
affection = 0
affection = affection + 2          // удачная реплика
affection = affection - 1          // промах
text hud «❤ {affection}»
// финальный роут открывается по порогу:
- Признаться -> confession expr="affection >= 5"
```

## Цикл-сцена (опрашиваемый экран)

Хаб, куда возвращаешься после каждого действия. Ставь `-> hub` перед меткой,
чтобы не было предупреждения о fall-through.

```
-> hub
:hub
Что будешь делать?
- Осмотреться -> look
- Идти дальше -> leave
:look
Ты осматриваешься...
-> hub
```

## Кликабельная комната (point-and-click)

```
:room
obj id=door sprite_url="/ui/door.png" x=0.8 y=0.5 anchor="0.5,0.5" on_click="door"
obj id=key  sprite_url="/ui/key.png"  x=0.2 y=0.7 anchor="0.5,0.5" on_click="take_key"
Осмотри комнату.
-> room                     // пауза-экран держит хотспоты
:take_key
has_key = 1
Ты подобрал ключ.
-> room
```

## Кодовый/логический замок (без ввода текста)

Состояние держим в переменных, проверяем условием.

```
:check
if a == 1 -> ck2
-> wrong
:ck2
if b == 0 -> ck3
-> wrong
:ck3
if c == 1 -> solved
-> wrong
:wrong
Комбинация неверна.
-> panel
```

## «Таймер» через ходы (без реального времени)

Отдельной команды-таймера пока нет — отмеряй время **ходами/днями** в цикле.

```
day = 1
days = 5
:turn
if day > days -> finale     // время вышло
// ... действия дня ...
day = day + 1
-> turn
```

## Подпрограмма (один код для многих вызовов)

```
// вызов из разных мест:
ename = "Волк"  ehp = 12  eatk = 5
call fight
// ...
ename = "Орк"   ehp = 40  eatk = 9
call fight

:fight                      // общий боевой движок
{ename} нападает!
// ...
return                      // вернётся туда, откуда позвали
```

## Функция с возвратом

```
func roll_dmg(base) {
  return base + rand(0, 3)
}
dmg = roll_dmg(atk)
```

## Повышение уровня (срабатывает сколько нужно раз)

```
:levelup
while xp >= need {
  xp = xp - need
  level = level + 1
  need = floor(need * 1.5)
  ✨ Уровень {level}!
}
return
```

## Сохранение и загрузка

```
- Сохранить -> dosave
- Загрузить -> doload
:dosave
save
Путь записан.
-> menu
:doload
load
```

## Несколько концовок

```
if score == 3 -> end_perfect
if score >= 1 -> end_ok
-> end_fail
:end_perfect
🏆 Идеально!
-> __end
:end_ok
Неплохо.
-> __end
:end_fail
В другой раз.
-> __end
```

## Постановка кадра (атмосфера)

```
bg /content/bg/night.jpg
audio channel=music action=play url="/content/audio/theme.ogg"
particles type=rain on=true
fade to="clear" duration=1.2
actor mara left sad
anim mara scale [1 1.03 1] 3s yoyo     // лёгкое «дыхание»
camera action=shake amplitude=0.02 duration=0.4
flash to="white" duration=0.3
```
