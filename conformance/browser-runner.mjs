// Прогон conformance-корпуса через НАСТОЯЩИЙ браузерный плеер (core.js).
// Правила те же, что у C#-прогона: `picks` — очередь выборов по индексу
// ПОКАЗАННЫХ вариантов, «остановка» — say/choice/input/end.
import { readFileSync } from "node:fs";

const [, , playerPath, casesJson] = process.argv;
const { Player, flag } = await import(playerPath);

// СВЁРТКА КАДРА — та же, что у C#-прогона (ConformanceCorpusTests.Run).
//
// Корпус описывает не только поток команд, но и КАДР, к которому он сводится:
// последний фон и кто в итоге на экране. Браузерная половина сверки этого не
// делала, и три случая про сцену (включая тот, где `show="no"` обязано убрать
// героиню) браузером не гонялись вовсе — а ведь именно там рантаймы и
// расходились: движок читает «no» словарём, а сырая истинность JS считает
// непустую строку правдой.
//
// Правила ровно живые: размещение ЛИПКОЕ (повторная команда без места
// возвращает актёра туда, где он стоял), `clear` снимает всех, но память о
// местах оставляет, `obj` идёт тем же трактом, что и `actor`.
function reduceScene(staged) {
  let bg = null;
  const actors = new Map();
  const sticky = (was, cmd) => {
    const next = { ...cmd };
    for (const keep of ["position", "x", "y"])
      if (next[keep] === undefined && was && was[keep] !== undefined) next[keep] = was[keep];
    return next;
  };
  for (const cmd of staged) {
    switch (cmd.op) {
      case "bg":
        bg = cmd.sprite_url ?? bg;
        break;
      case "clear":
        for (const [id, was] of actors) {
          const kept = {};
          for (const keep of ["position", "x", "y"])
            if (was[keep] !== undefined) kept[keep] = was[keep];
          kept.show = false;
          actors.set(id, kept);
        }
        break;
      case "obj":
      case "actor": {
        if (!cmd.id) break;
        actors.set(cmd.id, sticky(actors.get(cmd.id), cmd));
        break;
      }
    }
  }
  const visible = [];
  for (const [id, st] of actors) if (flag(st.show, true)) visible.push(id);
  visible.sort();
  return { bg, visible };
}
const cases = JSON.parse(readFileSync(casesJson, "utf8"));

const out = [];
for (const c of cases) {
  const picks = [...(c.picks || [])];
  const inputs = [...(c.inputs || [])];
  const stops = [];
  const staged = [];
  let player, guard = 0, fail = null;
  try {
    // Постановочные команды плеер не трактует — он их ПЕРЕСЫЛАЕТ, и корпус
    // проверяет именно поток пересланного (expect.stage).
    player = new Player(c.doc, { onStage: (cmd) => staged.push(cmd) });
    let ev = player.advance();
    while (guard++ < 5000) {
      if (ev.type === "say") {
        // Поля дублируются намеренно: корпус описывает реплику то строкой
        // (тогда сверяется `say`), то объектом {who, text} — тогда нужны имена
        // полей как в языке.
        stops.push({ say: ev.text, text: ev.text, who: ev.who ?? "" });
        ev = player.advance();
      } else if (ev.type === "choice") {
        // РАЗВОРАЧИВАЕМ СКЛЕЙКУ. Браузерный плеер намеренно отдаёт реплику
        // перед выбором ВМЕСТЕ с ним («a choice directly after shows together
        // with its prompt line») — это подача UI, а не другой язык: на одном
        // экране и вопрос, и варианты. Корпус описывает ЯЗЫК, где это две
        // остановки, поэтому здесь склейку раскрываем обратно.
        if (ev.text) stops.push({ say: ev.text, text: ev.text, who: ev.who ?? "" });
        stops.push({ choice: ev.options.map((o) => o.text) });
        if (!picks.length) { fail = "выборы кончились, а choice открыт"; break; }
        const p = picks.shift();
        if (p && typeof p === "object" && p.timeout) {
          // Метод называется timeoutChoice — «выбор истёк, идём по его ветке».
          ev = player.timeoutChoice();
        } else {
          if (p >= ev.options.length) { fail = `pick ${p} вне показанных (${ev.options.length})`; break; }
          ev = player.choose(ev.options[p].index);
        }
      } else if (ev.type === "input") {
        // Значение берёт СЛУЧАЙ (поле `inputs`), как и в C#-прогоне: игрок
        // печатает своё, а не соглашается с подсказкой. Подставив `default`,
        // прогонщик мерил бы не плеер, а собственную выдумку.
        stops.push({ input: ev.var });
        const typed = inputs.length ? inputs.shift() : (ev.default ?? "");
        ev = player.submitInput ? player.submitInput(typed) : player.advance();
      } else if (ev.type === "wait") {
        // Ожидание — тоже остановка языка: корпус пишет её как {wait:{ms}}.
        stops.push({ wait: { ms: ev.ms } });
        ev = player.advance();
      } else {
        stops.push({ end: true });
        break;
      }
    }
  } catch (e) {
    fail = "исключение: " + String((e && e.message) || e);
  }
  out.push({ id: c.id, stops, staged, scene: reduceScene(staged),
             vars: player ? player.vars : {}, fail });
}
process.stdout.write(JSON.stringify(out));
