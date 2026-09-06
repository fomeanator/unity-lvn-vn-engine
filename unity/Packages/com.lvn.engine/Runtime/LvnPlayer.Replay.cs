using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Lvn
{
    /// <summary>
    /// СЛЕД ИСПОЛНЕНИЯ — как восстановить КАРТИНКУ на любом шаге истории.
    ///
    /// <para>Сохранение помнит, где игрок остановился, но не то, что при этом
    /// стояло на сцене: фон, люди, вуали — итог десятков команд, разбросанных
    /// по пути. Наивный ответ «проиграть главу с начала молча» ломается на
    /// ветвлениях: у одной и той же точки бывает несколько путей, и выбрать
    /// чужой значит показать чужую сцену.</para>
    ///
    /// <para>Поэтому путь ЗАПИСЫВАЕТСЯ по мере игры и сжимается: из следа
    /// выбрасывается всё, что перекрыто более поздней командой того же
    /// предмета. Возврат в игру — это повтор оставшегося, а не пересказ
    /// главы.</para>
    /// </summary>
    public sealed partial class LvnPlayer
    {
        public void ReplayVisuals(int upto)
        {
            if (_script == null) return;
            // ВОССТАНОВЛЕНИЕ — не игра: те же авторские команды, но игрок их не
            // видел, и Помрежу это различие нужно, чтобы объяснять поток в
            // журнале. Старшинство у истории и реплея одно: обе — голос автора.
            _replaying = true;
            try {
            // The truthful path: the ops the player ACTUALLY executed (recorded
            // by Advance, restored from the snapshot). Old saves / edited
            // scripts fall back to the linear prefix — branch-blind, but the
            // corrected merge semantics below still apply.
            IReadOnlyList<int> path;
            if (_trace != null && _trace.Count > 0) path = _trace;
            else
            {
                int end = System.Math.Min(upto, _script.Count);
                var lin = new List<int>(end);
                for (int i = 0; i < end; i++) lin.Add(i);
                path = lin;
            }
            ReplayPath(path);
            // Штамп восстановления: без него «персонаж пропал после возврата в
            // главу» невозможно отличить от «его и не ставили» — а это разные
            // поломки, в разных местах (репорт партнёра TR-60).
            Log?.Invoke($"replay: {(path == _trace ? "след" : "линейный проход")} " +
                        $"{path.Count} шаг(ов) → в кадре {_replayShown.Count} актёр(ов)" +
                        (_replayShown.Count > 0 ? ": " + string.Join(", ", _replayShown) : ""));
            }
            finally { _replaying = false; }
        }

        // Placement fields are STICKY live (the sticky merge in VnStage keeps an
        // actor where the last positioning op put her) — so a rebuild accumulates
        // them across the path. Everything else (axes, transitions, gestures) is
        // per-op live and must come from the LAST op only.
        private static readonly HashSet<string> StickyActorFields = new HashSet<string>
        {
            "position", "x", "y", "width", "height", "scale", "z",
            "flip", "mirror", "anchor", "opacity", "hover_opacity",
        };

        // Кого реплей вывел в кадр — для штампа восстановления.
        private readonly List<string> _replayShown = new List<string>();

        private void ReplayPath(IReadOnlyList<int> path)
        {
            _replayShown.Clear();
            // Three replay classes. Structural ops (bg/obj/anim/text) accumulate,
            // so they re-run in path order. FX/audio are stateful overlays where
            // only the LAST setting matters — they collapse to the final value per
            // state key and apply once at the end. ACTORS rebuild to their LIVE
            // final state: the LAST op's own fields (show semantics mirror the
            // stage: an op without `show` shows), sticky placement accumulated
            // across the path, transitions stripped (a rebuild snaps into place).
            var fx = new Dictionary<string, JObject>();
            var fxOrder = new List<string>();
            JObject bg = null;   // полотно: одно на кадр, копится слиянием
            void SetFx(string key, JObject cmd)
            {
                if (!fx.ContainsKey(key)) fxOrder.Add(key);
                fx[key] = cmd;
            }

            // Pass 1: per actor — sticky placement accumulation + last position in path.
            var actorSticky = new Dictionary<string, JObject>();
            var actorLastPos = new Dictionary<string, int>();
            for (int pi = 0; pi < path.Count; pi++)
            {
                int i = path[pi];
                if (i < 0 || i >= _script.Count) continue;
                if (!(_script[i] is JObject c)) continue;
                if ((string)c["op"] == "bg")
                {
                    if (bg == null) bg = new JObject { ["op"] = "bg" };
                    foreach (var prop in c.Properties())
                        if (prop.Name != "op") bg[prop.Name] = prop.Value.DeepClone();
                    continue;
                }
                if ((string)c["op"] != "actor") continue;
                var aid = (string)c["id"];
                if (string.IsNullOrEmpty(aid)) continue;
                if (!actorSticky.TryGetValue(aid, out var st)) { st = new JObject(); actorSticky[aid] = st; }
                foreach (var prop in c.Properties())
                    if (StickyActorFields.Contains(prop.Name))
                        st[prop.Name] = prop.Value.DeepClone();
                actorLastPos[aid] = pi;
            }

            // СНАЧАЛА МИР, ПОТОМ ЛЮДИ. Полотно ставится один раз и первым:
            // порядок кадра читается так же, как его писал автор, а игрок не
            // платит за девять комнат, из которых он давно ушёл.
            if (bg != null)
            {
                bg.Remove("fade");      // перестройка встаёт на место, а не проступает
                StageApply(bg);
            }

            // Pass 2: replay inline, in path order. An actor fires exactly once —
            // at its LAST occurrence — and only if it ends VISIBLE by the live
            // rule (`show` absent = show; a re-issue after a hide shows again).
            for (int pi = 0; pi < path.Count; pi++)
            {
                int i = path[pi];
                if (i < 0 || i >= _script.Count) continue;
                if (!(_script[i] is JObject c)) continue;
                var op = (string)c["op"];
                if (op == "actor")
                {
                    var aid = (string)c["id"];
                    if (string.IsNullOrEmpty(aid) || actorLastPos[aid] != pi) continue;
                    if (!BoolOr(c["show"], true)) continue; // ends hidden — skip entirely
                    var m = (JObject)c.DeepClone();
                    m["show"] = true;
                    m.Remove("enter"); m.Remove("exit"); m.Remove("play"); // no transitions on a rebuild
                    foreach (var prop in actorSticky[aid].Properties())
                        if (m[prop.Name] == null)
                            m[prop.Name] = prop.Value.DeepClone();
                    StageApply(m);
                    _replayShown.Add(aid);
                    continue;
                }
                // ПОЛОТНО ОДНО, И ПОБЕЖДАЕТ ПОСЛЕДНЕЕ.
                //
                // Раньше bg переигрывался наравне с прочими структурными — по
                // всему пути, командой за командой. Замер на живой главе:
                // 11 команд bg, 10 разных картинок; возврат в её конец гнал
                // через загрузчик все десять, из которых игрок увидит одну.
                // На слабом устройстве (замер того же дня: 80 МБ свободных при
                // 976 МБ памяти) это десятки мегабайт декода ради кадра,
                // который перекроется следующей строкой.
                //
                // Схлопываем СЛИЯНИЕМ, а не «берём последнюю команду»: у
                // полотна есть поля, которые автор задаёт врозь (картинку
                // одной командой, переезд камеры — другой, без url). Взяли бы
                // последнюю целиком — потеряли бы картинку.
                if (op == "bg") continue;   // уже поставлено выше, одним слиянием
                if (IsReapplyable(op)) { StageApply(c); continue; }
                switch (op)
                {
                    case "fade":
                    case "dim":
                    case "tint":
                    case "blur":
                        SetFx(op, c);
                        break;
                    case "particles":
                        SetFx("particles:" + ((string)c["type"] ?? ""), c);
                        break;
                    case "camera":
                        // zoom/pan persist; reset returns both to default (so drop
                        // them); shake is transient and never replayed.
                        var act = (string)c["action"];
                        if (act == "zoom" || act == "pan") SetFx("camera:" + act, c);
                        else if (act == "reset") { fx.Remove("camera:zoom"); fx.Remove("camera:pan"); }
                        break;
                    case "audio":
                        // The looping channels (music/ambient) resume their last
                        // track (or stay stopped if the last command was a stop);
                        // sfx one-shots don't replay.
                        var ch = (string)c["channel"] ?? "sfx";
                        if (ch != "sfx") SetFx("audio:" + ch, c);
                        break;
                }
            }
            foreach (var key in fxOrder)
                if (fx.TryGetValue(key, out var cmd))
                    StageApply(cmd);
        }

        // Record an executed visual op into the replay path. A looping script
        // must not grow the trace (and every snapshot's copy) unbounded.
        private const int TraceCap = 20000;

        private void RecordTrace(int index)
        {
            _trace.Add(index);
            if (_trace.Count > TraceCap) CompactTrace();
        }

        // COMPACT, don't truncate. Dropping the oldest half is what the cap used
        // to do, and it dropped the chapter's only `bg` along with it: a long
        // session in a loop saved and reloaded WITHOUT A BACKGROUND, silently.
        // The soak bot caught it on duel-online, seed 11, at index 1150.
        //
        // What goes is only what is provably overwritten: walking backwards, a
        // command survives if it still sets a field no later command with the
        // same key has already set. The survivors keep their original order, so
        // ReplayPath sees the same sequence minus steps that could never have
        // reached the screen anyway.
        private void CompactTrace()
        {
            var covered = new Dictionary<string, HashSet<string>>();
            var keep = new List<int>(_trace.Count);
            for (int i = _trace.Count - 1; i >= 0; i--)
            {
                int idx = _trace[i];
                if (idx < 0 || idx >= _script.Count || !(_script[idx] is JObject c)) continue;
                if (!IsReplayedOp(c)) continue;   // replay would skip it regardless
                string key = TraceKey(c);
                if (!covered.TryGetValue(key, out var seen))
                {
                    seen = new HashSet<string>();
                    covered[key] = seen;
                }
                // A command carrying nothing but op/id (a bare re-show) still
                // matters the first time its key is seen — hence firstOfKey.
                bool firstOfKey = seen.Count == 0;
                bool novel = false;
                foreach (var p in c.Properties())
                    if (p.Name != "op" && p.Name != "id" && seen.Add(p.Name)) novel = true;
                if (novel || firstOfKey)
                {
                    seen.Add("");   // the key has now been seen, fields or not
                    keep.Add(idx);
                }
            }
            keep.Reverse();
            _trace = keep;
            // Nothing compacted away — a path of genuinely distinct commands.
            // Fall back to the old truncation so the cap still holds.
            if (_trace.Count > TraceCap * 3 / 4) _trace.RemoveRange(0, _trace.Count / 2);
        }

        // Will ReplayPath do anything with this command? One-shots (sfx, camera
        // shake) and ops nobody replays are dead weight in the trace.
        private static bool IsReplayedOp(JObject c)
        {
            var op = (string)c["op"];
            if (op == "actor" || IsReapplyable(op)) return true;
            switch (op)
            {
                case "fade": case "dim": case "tint": case "blur": case "particles":
                    return true;
                case "camera":
                    var act = (string)c["action"];
                    return act == "zoom" || act == "pan" || act == "reset";
                case "audio":
                    return ((string)c["channel"] ?? "sfx") != "sfx";
            }
            return false;
        }

        // What this command competes for. Same key = later command's fields win;
        // the keys mirror ReplayPath's own collapse exactly, or compaction would
        // throw away something replay still needed.
        private static string TraceKey(JObject c)
        {
            var op = (string)c["op"] ?? "";
            switch (op)
            {
                case "bg": return "bg";
                case "particles": return "particles:" + ((string)c["type"] ?? "");
                case "camera": return "camera:" + ((string)c["action"] ?? "");
                case "audio": return "audio:" + ((string)c["channel"] ?? "sfx");
                case "fade": case "dim": case "tint": case "blur": return op;
                case "anim":
                    // An animation is identified by what it moves, not by a name.
                    return "anim:" + ((string)c["id"] ?? (string)c["target"] ?? "")
                         + ":" + ((string)c["prop"] ?? "") + ":" + ((string)c["channel"] ?? "");
            }
            return op + ":" + ((string)c["id"] ?? "");
        }

        // An op nobody claimed: no case in the switch above, no LvnOps handler,
        // and not one of the engine's own staging ops — so it is forwarded to a
        // stage that has no case for it and quietly does nothing.
        //
        // Counted every time, REPORTED once per op per session. That budget is
        // the whole design: `ext vibrate` inside a loop, or thirty chapters that
        // each carry a wardrobe_show, must cost one line — not one per command,
        // which would both drown the console and put string formatting into the
        // player's inner loop.
        private void NoteUnclaimed(string op)
        {
            if (_unclaimed.TryGetValue(op, out var seen)) { _unclaimed[op] = seen + 1; return; }
            _unclaimed[op] = 1;

            Log?.Invoke("    !! unclaimed op '" + op + "' — forwarded to a stage with no case for it");
            Warn?.Invoke(
                "[lvn] unclaimed op '" + op + "' at command #" + _ip +
                " of scene '" + (string.IsNullOrEmpty(_scene) ? "?" : _scene) + "': nothing in this build handles it, " +
                "so this command — and every later '" + op + "' — is IGNORED. The story keeps playing. " +
                "Reported once per op per session; see LvnPlayer.UnclaimedOps for the full count. Fix ONE of:\n" +
                "  1) the op belongs to a package this build did not install — conformance/ops-owners.json names its " +
                "owner (e.g. 'wardrobe_show' lives in com.lvn.engine.shell);\n" +
                "  2) the op is host-defined — call LvnOps.Register(\"" + op + "\", handler) in your game, and declare " +
                "it in ext-grammar.json so lvnconv validates its fields instead of warning 'unknown op';\n" +
                "  3) it IS registered, but too late — registration must happen before the first Advance(). Register " +
                "from [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)], as the " +
                "ExtensionPlugin sample does; a MonoBehaviour.Start races the runner's Start;\n" +
                "  4) it is simply a typo — lvnconv validate flags it as 'unknown op' at build time.");
        }
    }
}
