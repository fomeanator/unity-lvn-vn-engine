using Lvn.Content;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// Per-title reading progress, shared by the play loop (auto-continue) and
    /// the carousel (the Continue label + the chapter picker):
    /// <list type="bullet">
    ///   <item><b>Current</b> — the chapter the player is in / left off in.
    ///   Moves freely (forward on chapter transitions, anywhere on a save load).</item>
    ///   <item><b>Reached</b> — the furthest chapter number ever STARTED. Only
    ///   ever goes up, so replaying an early chapter never re-locks later ones
    ///   in the picker.</item>
    /// </list>
    /// PlayerPrefs-backed, like the save slots.
    /// </summary>
    public static class LvnProgress
    {
        private static string CurKey(string titleId) => Lvn.LvnKeep.Scoped("lvn_chapter_", titleId);
        private static string CurNumKey(string titleId) => Lvn.LvnKeep.Scoped("lvn_chapter_num_", titleId);
        private static string ReachedKey(string titleId) => Lvn.LvnKeep.Scoped("lvn_reached_", titleId);
        private static string ReachedIdKey(string titleId) => Lvn.LvnKeep.Scoped("lvn_reached_id_", titleId);

        /// <summary>
        /// ПРОГРЕСС СДВИНУЛСЯ — знать об этом обязан не только тот, кто его
        /// сдвинул.
        ///
        /// <para>Точка продолжения живёт в ТРЁХ местах: записная книжка
        /// устройства, облачный свёрток и серверные переменные. Держать их в
        /// согласии полагалось звонящему: после каждого хода прогресса он
        /// должен был позвать сведение. Четверо звали (пауза, стирание, начало
        /// и конец главы), а ЭКРАНЫ — нет: игрок выбирал главу в списке или
        /// просил переиграть, приложение закрывали, и облачная копия оставалась
        /// со старой точкой. Восстановление возвращало игрока туда, откуда он
        /// уже ушёл.</para>
        ///
        /// <para>Список того, что надо не забыть, помнят не все — поэтому его
        /// здесь и нет: дом сам говорит, что сдвинулся, а слушает его один
        /// хост. Восстановление из свёртка (<see cref="RestoreMarker"/>) молчит
        /// намеренно: оно и есть тот, кто раскладывает свёрток обратно, и
        /// отвечать на себя ему незачем.</para>
        /// </summary>
        public static event System.Action Moved;

        private static void Announce()
        {
            try { Moved?.Invoke(); }
            catch (System.Exception e)
            {
                // Слушатель уронил ход прогресса — а прогресс уже записан.
                Debug.LogWarning("[lvn-progress] слушатель хода упал: " + e.Message);
            }
        }

        // ЗАКРЫТО НАРУЖУ НАМЕРЕННО: снаружи у каждого события прогресса свой
        // глагол (выбрал / переиграть / началась / дочитана / загрузил сейв).
        // Общий SetCurrent не говорит, ЧТО произошло, — а от этого зависит,
        // снимать ли ожидающий перезапуск и считать ли главу достигнутой.
        private static void SetCurrent(LvnTitle title, LvnChapter chapter)
        {
            if (title == null || chapter == null) return;
            using (LvnKeep.Batch())
            {
                LvnKeep.Put(CurKey(title.id), chapter.id);
                // The NUMBER rides along: a re-import that renames chapter ids must
                // not orphan the marker — position is the player's, ids are ours.
                LvnKeep.Put(CurNumKey(title.id), chapter.number);
                if (!HasReached(title) || chapter.number > Reached(title))
                {
                    LvnKeep.Put(ReachedKey(title.id), chapter.number);
                    // ИМЯ ЕДЕТ РЯДОМ С НОМЕРОМ — по той же причине, что и у
                    // точки продолжения строкой выше, только беда обратная.
                    // Точку убивает ПЕРЕИМЕНОВАНИЕ глав, потолок — ВСТАВКА:
                    // автор дописывает эпизод в середину вышедшего сезона,
                    // номера всех следующих съезжают на единицу, и число «5»
                    // начинает указывать на чужую главу. Замер 06.09: игрок,
                    // прочитавший четыре главы, видел четвёртую как
                    // непрочитанную. Имя от вставки не съезжает.
                    LvnKeep.Put(ReachedIdKey(title.id), chapter.id);
                }
            }
            Announce();
        }

        /// <summary>
        /// ИГРОК ВЫБРАЛ ГЛАВУ В СПИСКЕ — «продолжить» ведёт туда.
        ///
        /// <para>Два шага, и второй легко забыть: точка продолжения переезжает
        /// И отменяется ожидающий перезапуск, если он был. Оба решения — про
        /// прогресс, но принимали их ЭКРАНЫ: карусель делала эту пару своими
        /// руками, а карточка новеллы — свою, другую. Правило «сознательный
        /// выбор главы сильнее отложенного перезапуска» жило в обработчике
        /// кнопки.</para>
        /// </summary>
        public static void ChooseChapter(LvnTitle title, LvnChapter chapter)
        {
            if (title == null || chapter == null) return;
            SetCurrent(title, chapter);
            ClearRestart(title.id);
        }

        /// <summary>
        /// ИГРОК ПРОСИТ ПЕРЕИГРАТЬ ГЛАВУ С НАЧАЛА: точка продолжения переезжает
        /// на неё И ставится флаг перезапуска — цикл игры увидит его и сядет на
        /// входной чекпойнт вместо середины.
        ///
        /// <para>Автосейв здесь НЕ трогается намеренно: его сбрасывает сам цикл,
        /// когда перезапуск действительно начался. Иначе неудачная загрузка
        /// главы (нет сети, пропал скрипт) уничтожила бы позицию, которую игрок
        /// ещё держит.</para>
        /// </summary>
        public static void RestartChapter(LvnTitle title, LvnChapter chapter)
        {
            if (title == null || chapter == null) return;
            SetCurrent(title, chapter);
            RequestRestart(title.id, chapter.id);
        }

        /// <summary>ГЛАВА НАЧАЛАСЬ: «продолжить» ведёт в неё, и она же считается
        /// достигнутой. Отдельное имя рядом с «выбрал» и «переиграть» —
        /// чтобы у каждого события прогресса был свой глагол, а не общий
        /// SetCurrent, по которому не видно, что именно произошло.</summary>
        public static void StartChapter(LvnTitle title, LvnChapter chapter)
            => SetCurrent(title, chapter);

        /// <summary>ЗАГРУЖЕНО СОХРАНЕНИЕ ИЗ ДРУГОЙ ГЛАВЫ: «продолжить» едет за
        /// игроком туда, куда он прыгнул. Пятый повод сдвинуть точку — и
        /// единственный, который до сих пор двигал её мимо глаголов, прямым
        /// вызовом из загрузчика сохранений.</summary>
        public static void ResumeFromSave(LvnTitle title, LvnChapter chapter)
            => SetCurrent(title, chapter);

        /// <summary>
        /// ГЛАВА ДОЧИТАНА ДО КОНЦА. Прогресс двигает ИМЕННО ФИНАЛ, а не тап
        /// «Дальше»: выход через меню конца главы раньше оставлял точку на
        /// уже пройденной главе, и «Играть» переигрывал её сначала.
        ///
        /// <para>Есть следующая — точка переезжает на неё; нет — новелла
        /// пройдена, точка снимается совсем, и повтор начнётся с начала.</para>
        /// </summary>
        public static void FinishChapter(LvnTitle title, LvnChapter next)
        {
            if (title == null) return;
            if (next != null) SetCurrent(title, next);
            else ClearCurrent(title);
        }

        /// <summary>The chapter to continue from, or null to start fresh.</summary>
        public static LvnChapter Current(LvnTitle title)
        {
            if (title?.seasons == null) return null;
            var id = LvnKeep.Get(CurKey(title.id), "");
            if (string.IsNullOrEmpty(id)) return null;
            // Id пропал (переимпорт переименовал главы) — выручает номер, и
            // метку тут же лечим. Терять из-за переименования целое
            // прохождение — ровно та потеря прогресса, которую этот дом
            // обязан запрещать.
            //
            // ЛЕЧИМ ТОЛЬКО ИМЯ. Вопрос «где я остановился» задают из отрисовки
            // карточек — и он не имеет права двигать достигнутую главу: та же
            // запись поднимала бы `reached`, и после восстановления из волта с
            // заниженным значением список глав открылся бы шире, чем сказал
            // бэкап. Меняем ровно то, что протухло, — id.
            // Умолчание — НЕ ноль: ноль это законный номер главы (вводная
            // новелла начинается с него), см. LvnTitleExtensions.NoNumber.
            var found = title.ChapterByIdOrNumber(
                id, LvnKeep.Get(CurNumKey(title.id), LvnTitleExtensions.NoNumber));
            if (found != null && found.id != id) LvnKeep.Put(CurKey(title.id), found.id);
            return found; // null — прохождения и правда не было
        }

        /// <summary>
        /// ДОКУДА ДОШЁЛ — номером, но правду о нём знает ИМЯ.
        ///
        /// <para>Номер главы принадлежит каталогу, а не игроку: вставка эпизода
        /// в середину сезона двигает все последующие номера, и записанное вчера
        /// «5» назавтра указывает на другую главу. Поэтому рядом с числом лежит
        /// имя самой дальней достигнутой главы, и если оно ещё живёт в
        /// каталоге — число лечится по нему, ровно как <see cref="Current"/>
        /// лечит имя по числу при переименовании.</para>
        ///
        /// <para>Лечение ТОЛЬКО ВВЕРХ. Автор может и убрать главу из каталога
        /// (см. проверку про снятую с публикации): опустить потолок значило бы
        /// закрыть игроку то, что он уже прошёл, — а этого дом обязан не
        /// допускать.</para>
        /// </summary>
        public static int Reached(LvnTitle title)
        {
            if (title == null) return 0;
            // НЕТ ЗАПИСИ О ПОТОЛКЕ — НЕТ И ЛЕЧЕНИЯ. Имя достигнутой главы
            // переживает сброс новеллы дольше числа (и чистку в чужом стенде),
            // а лечение по нему на пустом месте выдаёт «дошёл до третьей» тому,
            // кто не открывал новеллу ни разу: непочатая новелла показывала
            // главы галочками, а «продолжать нечего» отвечало «продолжай с
            // третьей». Поймано соседними проверками в том же прогоне.
            if (!HasReached(title)) return 0;
            int number = LvnKeep.Get(ReachedKey(title.id), 0);
            var id = LvnKeep.Get(ReachedIdKey(title.id), "");
            if (string.IsNullOrEmpty(id)) return number;
            var chapter = title.ChapterByIdOrNumber(id, LvnTitleExtensions.NoNumber);
            if (chapter == null) return number;
            // Сравниваем ДВА ПОЛОЖЕНИЯ ОДНОЙ И ТОЙ ЖЕ главы — записанное вчера
            // и сегодняшнее, — а не «открыта ли глава»: это правило Швейцара, и
            // трогать его здесь нечем. Разница видна только вверх (см. выше).
            int сегодня = chapter.number;
            if (сегодня > number)
            {
                LvnKeep.Put(ReachedKey(title.id), сегодня); // каталог сдвинулся — чиним запись
                return сегодня;
            }
            return number;
        }

        /// <summary>
        /// НОВЕЛЛУ УЖЕ ОТКРЫВАЛИ — стоит ли в ней игрок сейчас ИЛИ доходил
        /// когда-нибудь.
        ///
        /// <para>По этому вопросу решают трое, и решают одно: показывать ли на
        /// карточке «Продолжить» и «Начать заново», и класть ли новеллу в
        /// облачный бэкап. Пройденная новелла — почата, хотя точки в ней уже
        /// нет: её снял финал. Непочатая — нет, хотя выглядит так же
        /// пусто.</para>
        ///
        /// <para>Складывать эти две половины у себя каждому звонящему значит
        /// заводить копию правила — а копия расходится. Ровно так и вышло:
        /// вторая половина писалась как «потолок больше нуля», и на новелле,
        /// чьи главы нумерованы с нуля, отвечала ложью во всех трёх
        /// местах.</para>
        /// </summary>
        public static bool Touched(LvnTitle title)
            => title != null && (Current(title) != null || HasReached(title));

        /// <summary>
        /// ДОШЁЛ ЛИ ХОТЬ ДО ЧЕГО-НИБУДЬ — вопрос отдельный от «докуда».
        ///
        /// <para>Потолок и «ничего не начинали» жили одним числом: ноль значил
        /// и то и другое. А НОЛЬ — ЗАКОННЫЙ НОМЕР ГЛАВЫ: вводная новелла
        /// начинается именно с него, и вся воронка приложения — тоже. Из-за
        /// совпадения потолок такой главы не записывался («ноль не больше
        /// нуля»), и новелла, целиком состоящая из пилота, НИКОГДА не
        /// становилась пройденной: игрок дочитал её до конца, а карточка
        /// показывает пустую полосу — и так каждый раз, сколько бы он её ни
        /// прошёл.</para>
        ///
        /// <para>Спрашиваем не значение, а НАЛИЧИЕ записи: её отсутствие и есть
        /// «ничего не начинали».</para>
        /// </summary>
        public static bool HasReached(LvnTitle title)
            => title != null && LvnKeep.Get(ReachedKey(title.id), int.MinValue) != int.MinValue;

        /// <summary>
        /// НОВЕЛЛА ПРОЙДЕНА? Дошли до её последней главы, и она закончилась:
        /// продолжения нет, а самая дальняя достигнутая — последняя.
        ///
        /// <para>Правило стояло в двух местах, и защиту имело только одно.
        /// «Не начата — значит не пройдена» пришлось дописать, когда новелла с
        /// первой главой под номером 0 объявлялась пройденной на ЧИСТОМ
        /// устройстве: «дошёл до 0» ≥ «последняя 0». Воронка тогда не включалась
        /// ни разу, и игрок сразу видел витрину. Второе место — список глав в
        /// карточке — этой защиты не получило и рисовало главы непочатой
        /// новеллы галочками «пройдено».</para>
        ///
        /// <para>Урок ровно тот же, что у имени актёра и у доступности главы:
        /// знание было В ДВИЖКЕ и не дошло до соседа, потому что правило
        /// пересказали, а не позвали.</para>
        /// </summary>
        public static bool Finished(LvnTitle title)
        {
            if (title == null) return false;
            var chapters = title.ChaptersOf();
            if (chapters.Count == 0) return false;
            if (!HasReached(title)) return false;
            return Current(title) == null
                && Reached(title) >= chapters[chapters.Count - 1].number;
        }

        /// <summary>
        /// СКОЛЬКО ГЛАВ ПРОЙДЕНО — честное число, а не оценка на глаз.
        ///
        /// <para>Достигнутая глава ещё не сыграна: игрок в ней сейчас. Поэтому
        /// пройденных на одну меньше — кроме случая, когда новелла закончена: там
        /// сыграны все. Ровно это правило профиль считал у себя, отдельной
        /// строкой с двумя зажимами.</para>
        /// </summary>
        public static int Done(LvnTitle title)
        {
            if (title == null) return 0;
            var chapters = title.ChaptersOf();
            if (chapters.Count == 0) return 0;
            if (Finished(title)) return chapters.Count;
            if (!HasReached(title)) return 0;
            int reached = Reached(title);
            // Считаем по СПИСКУ, а не по номеру: номера глав необязательно идут
            // с единицы и подряд (у импортированных новелл они бывают любыми).
            int done = 0;
            for (int i = 0; i < chapters.Count; i++)
                if (chapters[i].number < reached) done++;
            return Mathf.Clamp(done, 0, chapters.Count);
        }

        /// <summary>
        /// ДОЛЯ ПРОЙДЕННОГО, 0..1 — для полосы на карточке.
        ///
        /// <para>Полоса на карточке подборки рисовала ЗАШИТЫЕ 35% («demo
        /// progress»): одинаковые у непочатой новеллы и у почти пройденной, у
        /// всех игроков и во всех новеллах. Игрок читает такую полосу как
        /// сведения о себе, а это была заглушка, дожившая до продакшена.</para>
        /// </summary>
        public static float Fraction(LvnTitle title)
        {
            var chapters = title?.ChaptersOf();
            if (chapters == null || chapters.Count == 0) return 0f;
            return Mathf.Clamp01(Done(title) / (float)chapters.Count);
        }

        /// <summary>Forget the continue point (the novel was finished — replays
        /// start clean). Reached is kept so the picker stays unlocked.</summary>
        public static void ClearCurrent(LvnTitle title)
        {
            if (title == null) return;
            // Стирание фиксируется наравне с записью: без этого пройденная
            // новелла после краха снова открывалась «в середине».
            using (LvnKeep.Batch())
            {
                LvnKeep.Drop(CurKey(title.id));
                LvnKeep.Drop(CurNumKey(title.id));
            }
            Announce();
        }

        /// <summary>Vault restore: re-plant a marker recovered from the progress
        /// backup (id resolved against the live manifest by the caller).</summary>
        public static void RestoreMarker(string titleId, string chapterId, int number, int reached)
        {
            if (string.IsNullOrEmpty(titleId)) return;
            if (!string.IsNullOrEmpty(chapterId))
            {
                LvnKeep.Put(CurKey(titleId), chapterId);
                // Номера в свёртке могло и не быть: тогда метку не сопровождает
                // ЛОЖНЫЙ ноль, который потом сойдёт за нулевую главу.
                if (number != LvnTitleExtensions.NoNumber) LvnKeep.Put(CurNumKey(titleId), number);
                else LvnKeep.Drop(CurNumKey(titleId));
            }
            if (reached > LvnKeep.Get(ReachedKey(titleId), int.MinValue))
                LvnKeep.Put(ReachedKey(titleId), reached);
            // Свёрток хранит потолок числом и имени достигнутой главы не знает.
            // Оставлять СТАРОЕ имя рядом с новым числом нельзя: лечение по имени
            // потянуло бы потолок назад к тому, что было до восстановления.
            LvnKeep.Drop(ReachedIdKey(titleId));
        }

        // ── chapter-entry checkpoints ────────────────────────────────────────
        // The genre-standard restart semantics: "start from chapter N" resets the
        // variables to what they were when chapter N was FIRST entered on this
        // playthrough — not to whatever the player has accumulated since (stats
        // from the future would leak into the past and mis-gate choices). The
        // play loop snapshots the seed vars on every fresh chapter entry; the
        // picker requests a restart, and the loop seeds from the checkpoint.

        private static string EntryKey(string titleId) => Lvn.LvnKeep.Scoped("lvn_entry_", titleId);
        private static string RestartKey(string titleId) => Lvn.LvnKeep.Scoped("lvn_restart_", titleId);

        /// <summary>Snapshot the variables as they were entering a chapter.</summary>
        public static void SaveCheckpoint(string titleId, string chapterId, JObject vars)
        {
            if (string.IsNullOrEmpty(chapterId)) return;
            try
            {
                var all = ReadCheckpoints(titleId);
                all[chapterId] = vars ?? new JObject();
                LvnKeep.Put(EntryKey(titleId), all.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch { /* checkpoints are a comfort feature — never fatal */ }
        }

        /// <summary>The variables as of the chapter's first entry, or null when
        /// it was never entered (→ seed empty on a picked restart).</summary>
        public static JObject Checkpoint(string titleId, string chapterId)
        {
            if (string.IsNullOrEmpty(chapterId)) return null;
            return ReadCheckpoints(titleId)[chapterId] as JObject;
        }

        private static JObject ReadCheckpoints(string titleId)
        {
            try
            {
                var s = LvnKeep.Get(EntryKey(titleId), "");
                return string.IsNullOrEmpty(s) ? new JObject() : JObject.Parse(s);
            }
            catch { return new JObject(); }
        }

        /// <summary>The picker calls this: "the next entry into this chapter is an
        /// explicit RESTART — seed from its checkpoint, not the live state".</summary>
        public static void RequestRestart(string titleId, string chapterId)
        {
            LvnKeep.Put(RestartKey(titleId), chapterId ?? "");
        }

        /// <summary>Peek the pending restart target without consuming it — the
        /// shell checks whether the incoming play is an explicit from-the-top
        /// restart (which re-asks the player's name like any fresh start).</summary>
        public static string PendingRestart(string titleId) =>
            LvnKeep.Get(RestartKey(titleId), "");

        /// <summary>Withdraw a pending restart request — the player chose to
        /// continue their held position instead.</summary>
        public static void ClearRestart(string titleId) => LvnKeep.Drop(RestartKey(titleId));

        /// <summary>Consume the pending restart request (one-shot): whatever
        /// chapter enters FIRST after the pick clears the flag — a stale request
        /// left by a failed load must never fire on a later, unrelated chapter
        /// transition. Returns true only when the entering chapter IS the one
        /// that was picked.</summary>
        public static bool TakeRestart(string titleId, string chapterId)
        {
            var pending = LvnKeep.Get(RestartKey(titleId), "");
            if (string.IsNullOrEmpty(pending)) return false;
            // Гашение фиксируется: незафиксированное стирание воскрешало
            // залежавшийся запрос после краха — ровно то, чего требует
            // избежать комментарий выше.
            LvnKeep.Drop(RestartKey(titleId));
            return pending == chapterId;
        }

        /// <summary>
        /// КАК ИГРОК ВХОДИТ В НОВЕЛЛУ — три ответа, которые считаются вместе.
        /// </summary>
        public readonly struct Entry
        {
            /// <summary>С какой главы начинать: та, на которой остановились,
            /// иначе названная звонящим.</summary>
            public readonly LvnChapter Chapter;
            /// <summary>Заход С ЧИСТОГО ЛИСТА — первый в жизни или после
            /// финала. По нему первая глава заново спрашивает имя игрока.</summary>
            public readonly bool NovelFreshStart;
            /// <summary>Вход в эту главу УЖЕ ОПЛАЧЕН: брать плату второй раз
            /// нельзя.</summary>
            public readonly bool AlreadyPaid;

            public Entry(LvnChapter chapter, bool freshStart, bool alreadyPaid)
            { Chapter = chapter; NovelFreshStart = freshStart; AlreadyPaid = alreadyPaid; }
        }

        /// <summary>
        /// НАЧАТЬ ЗАХОД В НОВЕЛЛУ. Четыре правила, сплетённые между собой, —
        /// они стояли прямо в теле игрового цикла, и порядок между ними
        /// держался комментариями.
        ///
        /// <para>ПЕРВОЕ. «Чистый лист» считается ДО любой записи точки: иначе
        /// сама запись его и стирает, и первая глава не спросит имя.</para>
        ///
        /// <para>ВТОРОЕ. Пройденная новелла переигрывается НАЧИСТО. Точку на
        /// финале стирают, но переменные новеллы всё ещё держат всё
        /// прохождение — заход отправляется через перезапуск, чтобы первая
        /// глава села на свой нетронутый чекпойнт, а не на итоговые статы.</para>
        ///
        /// <para>ТРЕТЬЕ. Возврат в оплаченную главу не берёт плату второй раз.
        /// «Уже входили» — это ЕЁ автосейв (он пишется на входе), а не метка
        /// прогресса: конец главы двигает метку на СЛЕДУЮЩУЮ, за которую ещё
        /// не платили.</para>
        ///
        /// <para>ЧЕТВЁРТОЕ. Доигранный автосейв не считается оплаченным
        /// входом — иначе финал новеллы открывал бы её последнюю главу
        /// бесплатно раз за разом.</para>
        /// </summary>
        public static Entry BeginEntry(LvnTitle title, LvnChapter chapter)
        {
            var resume = Current(title);
            bool freshStart = resume == null;
            if (resume != null) chapter = resume;

            if (resume == null && HasReached(title) && chapter != null)
                RequestRestart(title?.id, chapter.id);

            var entrySlot = LvnSaveStore.Get(title?.id, LvnSaveStore.AutoSlot);
            bool alreadyPaid = resume != null && entrySlot?.Snap != null
                && Lvn.Content.LvnScriptRef.Same(entrySlot.Snap.ScriptUrl, resume.script_url)
                && !entrySlot.Snap.Finished;

            return new Entry(chapter, freshStart, alreadyPaid);
        }

        /// <summary>A full "restart the whole expedition" wipe: forget the continue
        /// point, the furthest-reached marker, every entry checkpoint and any pending
        /// restart request. The next play starts the title from chapter one, clean.
        /// (Persisted stats and save slots live elsewhere — the host clears those.)</summary>
        public static void ResetTitle(string titleId)
        {
            using (LvnKeep.Batch())
            {
                LvnKeep.Drop(CurKey(titleId));
                LvnKeep.Drop(CurNumKey(titleId));
                LvnKeep.Drop(ReachedKey(titleId));
                LvnKeep.Drop(ReachedIdKey(titleId)); // имя достигнутой — часть потолка
                LvnKeep.Drop(EntryKey(titleId));
                LvnKeep.Drop(RestartKey(titleId));
            }
            Announce();
        }
    }
}
