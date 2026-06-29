# AGENTS.md — ориентир для ИИ-агента в репозитории LVN

Это репозиторий движка **LVN** («ffmpeg для нарративных игр»). Игры пишутся на
языке **`.lvns`**, компилируются транскодером `lvnconv` в контейнер `.lvn` и
проигрываются Unity-рантаймом `com.lvn.engine`.

## Тебя попросили сделать игру? Иди сюда:

➡ **[`howto/AGENTS.md`](howto/AGENTS.md)** — точка входа: ментальная модель,
рабочий цикл, карта документации и частые ошибки. После неё можно собрать игру
любого поддерживаемого жанра **не читая исходный код движка**.

Ключевые документы в [`howto/`](howto/):

| Файл | Зачем |
|---|---|
| [`howto/AGENTS.md`](howto/AGENTS.md) | Старт: модель, цикл, ошибки |
| [`howto/CHEATSHEET.md`](howto/CHEATSHEET.md) | Весь синтаксис на один экран |
| [`howto/LANGUAGE.md`](howto/LANGUAGE.md) | Полный справочник языка `.lvns` |
| [`howto/CAPABILITIES.md`](howto/CAPABILITIES.md) | **Что движок умеет и чего НЕ умеет** |
| [`howto/recipes.md`](howto/recipes.md) | Переиспользуемые паттерны |
| [`howto/README.md`](howto/README.md) | 12 жанровых гайдов + рабочие примеры |

## Минимальный рабочий цикл

```sh
cd tools/lvnconv && go build -o /tmp/lvnconv .
/tmp/lvnconv convert -i путь/game.lvns -o /tmp/game.lvn
/tmp/lvnconv validate /tmp/game.lvn        # цель: OK ... 0 warning(s)
```

## Три вещи, которые экономят сессию

1. **Каст (персонажей) в `.lvns` определить нельзя** — он живёт в `manifest.json`
   (`sprites`); в скрипте только `actor <id> …`. Подробно — `howto/CAPABILITIES.md` §7.
2. **Нет реалтайм-таймера и ввода текста** — время мерь ходами, ввод делай через
   `choice`/клики по `obj on_click`. Полный список лимитов — `howto/CAPABILITIES.md` §8.
3. **Всегда валидируй** до `0 warning(s)` — это и есть проверка корректности игры
   без запуска движка.

(Этот файл — лишь указатель. Существенное содержание — в `howto/`.)
