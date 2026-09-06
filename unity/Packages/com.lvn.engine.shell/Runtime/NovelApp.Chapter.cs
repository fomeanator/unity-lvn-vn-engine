using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ИГРАЕМ ГЛАВУ — часть <see cref="NovelApp"/>: вход в главу и всё, что он
    /// за собой тянет — доставка скрипта, прогрев библиотеки, тема новеллы,
    /// сейв, возврат в меню.
    ///
    /// <para>Самая длинная процедура приложения: она трогает сцену, кошелёк,
    /// хранилище, оболочку и аналитику разом — и именно поэтому её незачем
    /// держать посреди всего остального.</para>
    /// </summary>
    public sealed partial class NovelApp
    {
        // Play a title from its entry point and KEEP GOING: when a chapter finishes,
        // the next one (by number) follows seamlessly — the player reads the whole
        // novel without bouncing off the carousel between episodes. A progress
        // marker remembers the furthest chapter started, so re-entering the title
        // continues there (and the in-chapter autosave restores the exact line);
        // finishing the last chapter clears it so a replay starts clean.
        private async Task PlayChapterAsync(LvnTitle title, LvnChapter chapter, string playerName)
        {
            // ЧЕМ ЭТОТ ЗАХОД ЯВЛЯЕТСЯ — спрашиваем у дома прогресса: с какой
            // главы, впервые ли, и оплачен ли уже вход. Четыре сплетённых
            // правила стояли здесь же, и порядок между ними держался
            // комментариями (см. LvnProgress.BeginEntry).
            var entry = LvnProgress.BeginEntry(title, chapter);
            chapter = entry.Chapter;
            bool novelFreshStart = entry.NovelFreshStart;
            bool alreadyEntered = entry.AlreadyPaid;
            while (chapter != null)
            {
                // The script must be REACHABLE before anything is charged — an
                // offline entry used to burn the energy and silently bounce to
                // the menu (and charge AGAIN on the retry).
                if (!await EnsureChapterScriptAsync(chapter))
                {
                    var eco = _manifest?.economy;
                    await _shell.AlertAsync(
                        _chapterMissingOnServer ? LvnOfflineText.ChapterMissingTitle
                                                : (eco?.gate_title ?? LvnOfflineText.Title),
                        _chapterMissingOnServer ? LvnOfflineText.ChapterMissing
                                                : LvnOfflineText.ChapterNeedsNetwork);
                    break;
                }
                if (!alreadyEntered && !await ChargeChapterEntryAsync(chapter))
                    break; // couldn't/wouldn't pay the entry cost → back to the carousel
                alreadyEntered = false;
                // Stream this chapter's asset plan. The FIRST chapter's plan was
                // started under the loading screen (BeginChapterLoading); a resume
                // into a later chapter, or a seamless next chapter, starts its own
                // here — critical assets first, deferred during play.
                if (_downloads != null && !ReferenceEquals(chapter, _preparedChapter))
                    _chapterSched = _downloads.BeginChapter(chapter, _quitting);
                _preparedChapter = null;
                AnnounceChapterStart(title, chapter);
                var finished = await PlayOneChapterAsync(title, chapter, playerName, novelFreshStart);
                novelFreshStart = false; // only the entry chapter of this run counts
                if (finished == null)
                {
                    AnnounceChapterAbandon(title, chapter);
                    break; // → carousel
                }
                AnnounceChapterFinish(title, finished);
                // A cross-chapter save load can land the player in another title —
                // continue along whichever title the finished chapter belongs to.
                var (owner, _) = FindChapterByScriptUrl(finished.script_url);
                if (owner != null) title = owner;
                var next = NextChapterOf(title, finished);
                // The FINISH is what advances progress — not the «Дальше» tap.
                // Leaving via the chapter-end menu used to strand the marker on
                // the finished chapter, and «Играть» replayed it from the top.
                LvnProgress.FinishChapter(title, next);
                if (next == null)
                {
                    // ВОРОНКА ПРОЙДЕНА — ПРЯМО ЗДЕСЬ, ФАКТОМ ФИНАЛА. Ворота в
                    // оболочке выводили это из reached/Current и на живом
                    // устройстве промахивались — партнёр получил «пролог по
                    // кругу» на чистой установке. Финал последней главы
                    // вводной — единственный надёжный свидетель.
                    Lvn.UI.Screens.LvnIntro.NoteFinished(title);
                }
                SyncProgressVault();
                // Between-chapters screen (ui.chapter_end): "Конец главы" with
                // continue/menu. Without it chapters flow seamlessly, as before.
                // ВВОДНАЯ НЕ СПРАШИВАЕТ «в меню?». Ей некуда больше вести:
                // пролог кончился, витрина открылась, и кнопка между ними —
                // лишний щелчок на месте перехода, который должен быть
                // непрерывным (героиня выходит из главы прямо в меню).
                bool intro = Lvn.UI.Screens.LvnIntro.Is(title);
                if (_shell?.ChapterEnd != null && !(intro && next == null))
                {
                    bool goNext = await _shell.ChapterEnd.ShowAsync(finished.name, hasNext: next != null);
                    if (!goNext || next == null) break;
                }
                else if (next == null) break;
                chapter = next;
            }
            // Новелла отыграна (или игрок ушёл) — кадр переходит меню тем же
            // непрерывным движением, что и по кнопке выхода: героиня
            // возвращается с миссии, а не появляется заново на пустой сцене.
            await ReturnToMenuAsync();
            // Back to the menu — stop the chapter scheduler so its deferred
            // downloads don't keep competing with the menu's own refresh.
            _downloads?.EndChapter();
            _chapterSched = null;
            // Вышли из новеллы: события меню не должны числиться за историей,
            // из которой игрок уже ушёл.
            LeaveChapterContext();
            // A chapter's worth of remote sprites fragments the panel's dynamic
            // atlas (freed regions rarely fit the next tenant); rebuild it clean
            // at this natural boundary.
            try
            {
                var panel = Stage != null
                    ? Stage.GetComponent<UIDocument>()?.rootVisualElement?.panel : null;
                if (panel != null) RuntimePanelUtils.ResetDynamicAtlas(panel);
            }
            catch { /* atlas reset is an optimization, never a failure */ }
        }

        /// <summary>
        /// ГЛАВА НАЧАЛАСЬ — обряд из пяти шагов, который обязан пройти ЦЕЛИКОМ.
        ///
        /// <para>Точка прогресса едет, объявляется КОНТЕКСТ (пока игрок внутри
        /// новеллы, каждое событие обязано знать, в какой именно — без этого
        /// сбой не отнести к истории, а таких событий в отчёте больше
        /// половины), обнуляется счёт воронки (она считается ПО ГЛАВЕ), свёрток
        /// прогресса догоняет все три хранилища, и только потом о начале
        /// узнают хост и аналитика.</para>
        ///
        /// <para>Шаги стояли в теле игрового цикла подряд, и порядок между ними
        /// держался соседством строк. Забыть один — значит получить события без
        /// адреса, воронку, склеенную из двух глав, или расхождение свёртка.</para>
        /// </summary>
        private void AnnounceChapterStart(LvnTitle title, LvnChapter chapter)
        {
            LvnProgress.StartChapter(title, chapter);   // ход прогресса сводит свёрток сам
            EnterChapterContext(title, chapter);
            lock (_reachedLabels) _reachedLabels.Clear();
            ChapterStarted?.Invoke(title, chapter);
            Lvn.Services.LvnAnalytics.Track(Lvn.Services.LvnEvents.ChapterStart,
                ("title", title?.id), ("chapter", chapter.id));
        }

        /// <summary>ГЛАВА КОНЧИЛАСЬ — обратная сторона того же обряда: хост,
        /// аналитика и слив незнакомых команд. Порядок тот же, что у начала:
        /// сперва хост, потом отчёт.</summary>
        private void AnnounceChapterFinish(LvnTitle title, LvnChapter finished)
        {
            ChapterFinished?.Invoke(title, finished);
            // ПЛАВНОСТЬ — ВЕЛИЧИНА, А НЕ ОЩУЩЕНИЕ. Счёт запинок уходит вместе с
            // концом главы: по нему видно, стало ли лучше после правки, — раньше
            // это можно было только почувствовать.
            var (hitches, worstMs) = Lvn.LvnFrameWatch.Take();
            // ЖИВЫХ ВХОДОВ В ПОЛОСУ — столько, сколько актёров и фонов было на
            // экране. Число заметно больше означает, что фоновая работа ходит
            // по сети как живая: ступень объявлена не тому, кто её спросит.
            LvnLog.Trace(Lvn.Content.LvnLaneWatch.Report());
            var (liveEnters, worstWaitMs, bgEnters, yields) = Lvn.Content.LvnLaneWatch.Take();
            Lvn.Services.LvnAnalytics.Track(Lvn.Services.LvnEvents.ChapterFinish,
                ("title", title?.id), ("chapter", finished.id),
                ("hitches", hitches), ("worst_ms", worstMs),
                ("lane_live", liveEnters), ("lane_wait_ms", worstWaitMs),
                ("lane_bg", bgEnters), ("lane_yields", yields));
            FlushUnknownOps(title, finished);
        }

        /// <summary>
        /// УХОД ИЗ СЕРЕДИНЫ ГЛАВЫ — третья сторона того же обряда, рядом с
        /// началом и концом.
        ///
        /// <para>Без этого события потеря внутри главы выводилась вычитанием
        /// (start минус finish), и в одно число сливались крах, гибель,
        /// упёршийся в энергию и просто заскучавший. Позиция говорит, ГДЕ
        /// бросили: у «дочитал до середины и вышел» и «вылетело на первом
        /// кадре» разные причины и разные починки.</para>
        ///
        /// <para>Контекст КАДРА, а не только позиции. «Ушли на команде 137» не
        /// отвечает ни на что: половина глав вообще без выборов, и бросают там
        /// не из-за развилки, а из-за того, ЧТО на экране — плохой спрайт, не
        /// тот фон, зависшая сцена. Метка, фон и кто на сцене дают место,
        /// которое можно открыть и посмотреть глазами.</para>
        ///
        /// <para>Стояло это всё прямо в цикле — и там же разошлось с концом
        /// главы: конец ЗАБИРАЕТ счёт запинок (Take, со сбросом), а уход читал
        /// те же счётчики, не сбрасывая. Запинки брошенной главы утекали в
        /// следующую и портили её число — ту самую величину, ради которой счёт
        /// и заведён.</para>
        /// </summary>
        private void AnnounceChapterAbandon(LvnTitle title, LvnChapter chapter)
        {
            var snap = Stage?.Player?.Save();
            // Брошенная глава — самый интересный случай для плавности: уходят
            // чаще всего оттуда, где дёргается.
            var (hitches, worstMs) = Lvn.LvnFrameWatch.Take();
            Lvn.Services.LvnAnalytics.Track(Lvn.Services.LvnEvents.ChapterAbandon,
                ("title", title?.id), ("chapter", chapter?.id),
                ("at", Stage?.Player?.Index ?? -1),
                ("label", snap?.AnchorStableLabel ?? snap?.AnchorLabel),
                ("bg", Lvn.UI.VnStage.LastSceneBgUrl),
                ("actors", Stage?.ActorsOnStage()),
                ("hitches", hitches), ("worst_ms", worstMs));
            FlushUnknownOps(title, chapter);
        }

        // Preflight: make the chapter's script locally available (cache hit or
        // a live fetch) BEFORE the entry charge — money never burns on a
        // chapter that can't start. The later fetch inside PlayOneChapterAsync
        // then hits the cache.
        // ПОЧЕМУ ГЛАВА НЕ ОТКРЫЛАСЬ — вопрос игрока, а не наш. «Нет сети» и
        // «главы нет на сервере» выглядят одинаково только изнутри: снаружи
        // первый идёт проверять вайфай, а второму проверять нечего, потому что
        // виноват автор. Причина последней неудачи живёт здесь, чтобы
        // сообщение называло её словом.
        private bool _chapterMissingOnServer;

        private async Task<bool> EnsureChapterScriptAsync(LvnChapter chapter)
        {
            _chapterMissingOnServer = false;
            if (chapter == null || string.IsNullOrEmpty(chapter.script_url)) return false;
            if (_assets.Loader.IsScriptCached(chapter.script_url)) return true;
            try
            {
                var json = await _assets.Loader.DownloadScriptCached(chapter.script_url);
                return !string.IsNullOrEmpty(json);
            }
            catch (Lvn.Content.LvnFetchException e)
            {
                // 404 — файла нет на сервере: сеть исправна, глава не выложена.
                // Спрашиваем ДОМ (см. LvnFetchException.MissingOnServer), а не
                // разбираем код строкой здесь.
                _chapterMissingOnServer = e.MissingOnServer;
                return false;
            }
            catch { return false; }
        }

        // Background full-library warm: чей-то экран загрузки всегда важнее —
        // the loop parks while a chapter scheduler is actively gating.
        private async Task WarmLibraryAsync(LvnManifest manifest, System.Threading.CancellationToken ct)
        {
            try
            {
                await Task.Delay(3000, ct); // let the boot/menu settle first
                int warmed = 0, skipped = 0;

                // СОГРЕТЬ ОДИН ФАЙЛ — правила общие для всего, что греется в
                // фоне: уступить активной главе, уступить живой поверхности,
                // переждать офлайн, не качать лежащее. Тело было вписано в
                // цикл глав, и второму месту (арт каста) пришлось бы его
                // скопировать — а разойдись копии, одна очередь начала бы
                // отбирать полосу у кадра, который игрок видит прямо сейчас.
                async Task<bool> WaitForQuietAsync()
                {
                    while (_chapterSched != null && !_chapterSched.AllDone && !ct.IsCancellationRequested)
                        await Task.Delay(500, ct);
                    while (_assets.LivePressure > 0 && !ct.IsCancellationRequested)
                        await Task.Delay(150, ct);
                    if (Lvn.LvnNetworkStatus.IsOffline) { await Task.Delay(3000, ct); return false; }
                    return !ct.IsCancellationRequested;
                }

                // СОГРЕТЬ ПАЧКУ — ОБОЗОМ, А НЕ ПО ОДНОМУ.
                //
                // Раньше здесь стоял `await` на КАЖДЫЙ файл: две с половиной
                // тысячи файлов ехали строго друг за другом, и полоса сети
                // шириной двенадцать всё это время держала одиннадцать мест
                // пустыми. На мобильной сети цена файла — не байты, а круговой
                // рейс; последовательный обход платит его две с половиной
                // тысячи раз подряд.
                //
                // Обоз умеет то же самое в несколько полос и — что не менее
                // важно — ВЕДЁТ СЧЁТ: сколько в пачке, сколько закрыто, сколько
                // байт. Без него индикатор видел единственный файл в полёте и
                // говорил игроку «в очереди: файлов 1» при сотне оставшихся,
                // «Скачано 0 МБ» при работающей загрузке и ронял скорость в
                // ноль на каждой границе файлов (живой скрин 04.09).
                async Task WarmBatch(System.Collections.Generic.List<Lvn.Content.PreloadItem> pack)
                {
                    if (pack.Count == 0) return;
                    if (!await WaitForQuietAsync()) return;
                    int missing = 0;
                    foreach (var it in pack)
                        if (_assets.Loader.IsAssetCached(it.Url)) skipped++; else missing++;
                    if (missing == 0) return;
                    try { await _assets.Loader.StartPreloadBatch(pack, ct); warmed += missing; }
                    catch (System.OperationCanceledException) { throw; }
                    catch { /* самолечение закроет отдельные файлы */ }
                }

                // ПОРЯДОК — ЧАСТЬ РАБОТЫ. Очередь без порядка забивается, и
                // первым не доезжает как раз то, чего игрок ждёт: вводная
                // (глава ноль) с агентом и фаворитами стояла бы за спиной у
                // всей библиотеки просто потому, что в манифесте она не первая.
                // Лестницу называет дом приоритетов; здесь — её верхняя
                // ступень: сперва вводная, потом остальные.
                var order = new System.Collections.Generic.List<LvnTitle>();
                if (manifest?.titles != null)
                {
                    foreach (var t in manifest.titles)
                        if (t != null && LvnIntro.Is(t)) order.Add(t);
                    foreach (var t in manifest.titles)
                        if (t != null && !LvnIntro.Is(t)) order.Add(t);
                }
                // СТУПЕНЬ ОБЪЯВЛЕНА ВСЛУХ, И ЭТО ВТОРОЙ ЭТАЖ ЗАЩИТЫ. Гейт
                // выше («ждать, пока живое не отпустит») — политика: библиотека
                // вообще не качается под игрой. Ступень — пол под политикой:
                // даже проскочив гейт, эти файлы не займут места, оставленные
                // в полосе живому. Политику можно смягчить, пол останется.
                // ГЕРОИНЯ ИДЁТ СРАЗУ ЗА ВВОДНОЙ, А НЕ ЗА ВСЕЙ БИБЛИОТЕКОЙ.
                //
                // Гардероб открывают из меню в любую минуту, и первый раз —
                // обычно в первые же минуты: посмотреть, кого дали. Пока её
                // облик стоял на последней ступени, игрок в этот момент видел
                // пустые карточки: ни скинов, ни базы с эмоциями (репорт
                // владельца 06.09). Причин было две, и обе здесь закрыты:
                // порядок (последняя ступень) и полнота (слои — шаблоны,
                // прогрев их пропускал; теперь они разворачиваются по осям).
                //
                // Вводная качается первой — она в начале `order`; героиня
                // становится в очередь СРАЗУ ПОСЛЕ неё, до остальных новелл.
                var героиня = new System.Collections.Generic.List<Lvn.Content.PreloadItem>();
                foreach (var part in Lvn.Content.LvnParts.OfHero(manifest))
                    if (!string.IsNullOrEmpty(part.Url))
                        героиня.Add(new Lvn.Content.PreloadItem { Url = part.Url, Kind = part.Kind, Size = part.Size });

                bool героиняЖдёт = героиня.Count > 0;
                async Task ГероиняЕсли(bool пора)
                {
                    if (!пора || !героиняЖдёт) return;
                    героиняЖдёт = false;
                    using (Lvn.Content.LvnRungScope.At(Lvn.Content.LvnRung.Hero))
                        await WarmBatch(героиня);
                }

                using (Lvn.Content.LvnRungScope.At(Lvn.Content.LvnRung.Library))
                if (order.Count > 0)
                    foreach (var t in order)
                    {
                        // Вводная прогрета — очередь героини открывается.
                        if (!LvnIntro.Is(t)) await ГероиняЕсли(true);
                        if (t?.seasons == null) continue;
                        foreach (var se in t.seasons)
                        {
                            if (se?.chapters == null) continue;
                            foreach (var ch in se.chapters)
                            {
                                if (ch == null) continue;
                                if (!string.IsNullOrEmpty(ch.script_url) && !_assets.Loader.IsScriptCached(ch.script_url))
                                    try { await _assets.Loader.DownloadScriptCached(ch.script_url); } catch { /* прогрев — оптимизация: не доехало сейчас — доедет самолечением */ }
                                if (ch.assets == null) continue;
                                // Внутри главы — по ступеням: критичное (то,
                                // что рисует первый кадр) раньше прочего.
                                // «Критичность» ставит автор: движок не знает,
                                // какая поза откроет сцену.
                                // Порядок внутри главы сохранён: пачка едет в
                                // том же порядке ступеней, просто не по одному.
                                var pack = new System.Collections.Generic.List<Lvn.Content.PreloadItem>();
                                foreach (var part in Lvn.Content.LvnPriority.ByRung(
                                             Lvn.Content.LvnParts.OfChapter(ch),
                                             pt => Lvn.Content.LvnPriority.OfChapterPart(pt, current: true)))
                                    if (!string.IsNullOrEmpty(part.Url))
                                        pack.Add(new Lvn.Content.PreloadItem { Url = part.Url, Kind = part.Kind, Size = part.Size });
                                if (ct.IsCancellationRequested) return;
                                await WarmBatch(pack);
                            }
                        }
                    }
                // Библиотеки могло и не быть (одна вводная новелла) — тогда
                // очередь героини не открылась в цикле; открываем здесь.
                await ГероиняЕсли(true);

                // ОБЛИК ПРО ЗАПАС — последняя ступень лестницы: позы и наряды
                // ОСТАЛЬНОГО каста, которых сюжет пока не просил. Гардероб открывают из меню, то
                // есть в любую минуту, и ждать там сети нечему.
                //
                // Стоит это ПОСЛЕ библиотеки и только теперь: 01.09 тот же
                // список, поставленный в общую очередь, задавил первый запуск —
                // не потому, что был лишним, а потому, что гейт «живого» считал
                // две двери из семи и не видел, как вводная ждёт свой СКРИПТ.
                // Гейт починен; порядок назван лестницей; полоса у каста своя —
                // последняя.
                using (Lvn.Content.LvnRungScope.At(Lvn.Content.LvnRung.Spare))
                {
                    var spare = new System.Collections.Generic.List<Lvn.Content.PreloadItem>();
                    foreach (var part in Lvn.Content.LvnParts.OfCast(manifest))
                        if (!string.IsNullOrEmpty(part.Url))
                            spare.Add(new Lvn.Content.PreloadItem { Url = part.Url, Kind = part.Kind, Size = part.Size });
                    if (ct.IsCancellationRequested) return;
                    await WarmBatch(spare);
                }

                LvnLog.Trace($"[lvn-warm] library fully cached ({warmed} fetched, {skipped} already local)");
            }
            catch (System.OperationCanceledException) { /* teardown */ }
        }
    }
}
