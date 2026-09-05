using System;
using UnityEngine;

namespace Lvn
{
    /// <summary>
    /// ЗАПИСНАЯ КНИЖКА УСТРОЙСТВА — всё, что игра помнит между запусками, и
    /// единственное место, которое знает, ЧЕМ она это помнит.
    ///
    /// <para>Замер до выделения: 166 обращений к <c>PlayerPrefs</c> в 20 файлах,
    /// 42 фиксации на 66 записей. Фиксацию звали «когда вспомнят», и это не
    /// придирка к стилю — записать без фиксации значит держать значение только
    /// в памяти процесса: при обычном выходе Unity его сохранит, при крахе или
    /// снятии приложения — нет.</para>
    ///
    /// <para>Хуже, что «нет фиксации» было НЕОТЛИЧИМО от «забыли фиксацию».
    /// В одном только учёте прогресса записи фиксировались, а удаления нет —
    /// и одноразовый флаг перезапуска гасился без фиксации, ровно вопреки
    /// комментарию строкой выше: «залежавшийся запрос не должен выстрелить на
    /// чужой главе». После краха он воскресал и выстреливал. Рядом же лежит
    /// пропуск НАМЕРЕННЫЙ и объяснённый: гардероб метит весь каталог разом, и
    /// полный флаш на каждый предмет складывался в длинный кадр перед подъёмом
    /// панели.</para>
    ///
    /// <para>Ответственность: хранить и решать вопрос фиксации ЯВНО. Отсюда два
    /// глагола вместо одного: <see cref="Put(string,string)"/> — записать и
    /// зафиксировать, <see cref="Jot(string,string)"/> — записать в карандаше,
    /// потому что путь горячий. Карандашное больше не теряется: книжка сама
    /// фиксирует его, когда приложение уходит в фон или закрывается.</para>
    ///
    /// <para>Что здесь НЕ живёт: смысл ключей. Какие имена у прогресса, что
    /// значит «дошёл» и когда его сбрасывать — дело владельцев (LvnProgress,
    /// LvnPrefs, кошелёк). Книжка не знает, что записывает.</para>
    /// </summary>
    public static class LvnKeep
    {

        /// <summary>
        /// КЛЮЧ ВЕЩИ, ПРИВЯЗАННОЙ К НОВЕЛЛЕ: приставка плюс её имя.
        ///
        /// <para>Хранилищ на новеллу несколько — сейвы, галерея, прочитанное,
        /// статы, — и каждое строило ключ само. Приставки у них разные и
        /// такими и останутся (сменить приставку значит потерять чужие
        /// сохранения), а вот «а если новеллы нет» имело ТРИ ответа: «default»
        /// у сейвов, пустая строка у статов и ключ с точкой на конце у галереи
        /// с прочитанным. Из-за последнего пустое имя и отсутствующее уезжали
        /// в РАЗНЫЕ ящики — одно и то же «нет новеллы», записанное дважды.</para>
        ///
        /// <para>Здесь ответ один: нет имени — «default».</para>
        /// </summary>
        public static string Scoped(string prefix, string id)
        {
            var name = string.IsNullOrEmpty(id) ? "default" : id;
            var первый = Get(POwner, "");
            // Пока играет ТОТ ЖЕ человек, ключи прежние — ни одна существующая
            // запись не переезжает и не теряется. Чужой аккаунт получает своё
            // пространство приставкой.
            var key = (string.IsNullOrEmpty(_owner) || _owner == первый)
                ? prefix + name
                : prefix + _owner + "." + name;
            Note(key);
            return key;
        }

        /// <summary>
        /// РЕЕСТР ЛИЧНЫХ КЛЮЧЕЙ — чтобы «удалите меня» можно было исполнить.
        ///
        /// <para>Записная книжка устройства не умеет перечислять то, что в ней
        /// лежит: ключи можно только спрашивать по имени. Пока это никого не
        /// беспокоило, но у кнопки «удалить аккаунт» половина работы — на
        /// телефоне: сохранения, прогресс, галерея, «прочитано». Замер 05.09:
        /// сервер стирал свою половину честно, а на устройстве оставалось всё.
        /// Поэтому книжка запоминает, какие личные ключи выдавала, — и по этому
        /// списку умеет забыть игрока целиком.</para>
        ///
        /// <para>Список хранится рядом с данными и переживает перезапуск: иначе
        /// удаление после перезапуска стирало бы только то, к чему игра успела
        /// обратиться в этой сессии.</para>
        /// </summary>
        private static void Note(string key)
        {
            if (_known == null) LoadKnown();
            if (!_known.Add(key)) return;
            Jot(PKeys, string.Join("\n", _known));
        }

        private static void LoadKnown()
        {
            _known = new System.Collections.Generic.HashSet<string>();
            var raw = Get(PKeys, "");
            if (string.IsNullOrEmpty(raw)) return;
            foreach (var k in raw.Split('\n'))
                if (!string.IsNullOrEmpty(k)) _known.Add(k);
        }

        /// <summary>Забыть ВСЕ личные данные, выданные книжкой: сохранения,
        /// прогресс, галерею, прочитанное, статы. Настройки устройства (язык,
        /// громкость, размер шрифта) не трогает — они не про игрока.</summary>
        public static void ForgetPlayerData()
        {
            if (_known == null) LoadKnown();
            using (Batch())
            {
                foreach (var k in _known) Drop(k);
                Drop(PKeys);
            }
            _known.Clear();
            // ЗАБВЕНИЕ ОБЯЗАНО ДОЙТИ И ДО ПАМЯТИ. Дома держат свои наборы в
            // оперативной памяти, чтобы не читать книжку на каждой реплике, —
            // и стёртый диск их не касается. Замер 05.09: после «забыть
            // игрока» прочитанное продолжало отвечать «читал», а первая же
            // запись нового игрока сохраняла старый набор обратно на диск.
            // Книжка не знает, кто её читает, поэтому просто объявляет: всё,
            // что вы помнили, недействительно.
            try { Wiped?.Invoke(); } catch { /* забвение не смеет ронять игру */ }
        }

        /// <summary>Личные данные стёрты — забудьте всё, что держите в памяти.
        /// Подписаны дома с наборами: прочитанное, галерея.</summary>
        public static event Action Wiped;

        private static System.Collections.Generic.HashSet<string> _known;
        private const string PKeys = "lvn.local.keys";

        /// <summary>
        /// ЧЬИ ЭТО ДАННЫЕ. Сохранения, прогресс, галерея, прочитанное и статы
        /// лежат на устройстве под именем новеллы — и только под ним.
        ///
        /// <para>Замер 05.09 на живом сервере: первый игрок сохранился, второй
        /// вошёл СВОИМ аккаунтом на том же телефоне — и увидел чужой сейв и
        /// чужое «прочитано» (кошелёк при этом сбросился правильно: у денег
        /// такое правило было, у прохождения — нет). Дальше он продолжает чужую
        /// главу и перезаписывает чужие слоты.</para>
        ///
        /// <para>Правило: ключи БЕЗ приставки принадлежат тому, кто вошёл здесь
        /// первым, — поэтому у всех, кто уже играет, ничего не меняется и
        /// ничего не переезжает. Любой другой аккаунт получает собственное
        /// пространство, а вернувшийся первый — снова своё.</para>
        /// </summary>
        public static void NoteOwner(string userId)
        {
            _owner = userId ?? "";
            if (string.IsNullOrEmpty(_owner)) return;
            if (!Has(POwner)) Put(POwner, _owner);   // первый забирает прежние ключи
        }

        /// <summary>
        /// ТОТ ЖЕ ЧЕЛОВЕК ПОД НОВЫМ НОМЕРОМ.
        ///
        /// <para>Номер игрока выдаёт сервер, и он не вечен: базу учёток
        /// восстановили из вчерашнего снимка, службу подняли на новом диске — и
        /// то же самое устройство получает ДРУГОЙ номер. Для <see
        /// cref="NoteOwner"/> это выглядит как «пришёл чужой», и все ключи
        /// уезжают за приставку: человек открывает игру, в которой никогда не
        /// играл, хотя его сохранения целы в сантиметре от него.</para>
        ///
        /// <para>Замер 06.09 в стенде: после потери базы «своё сохранение
        /// видно: False, прочитано видно: False». Отличить этот случай от
        /// настоящей смены игрока можно ровно по тому, КАК он вошёл: вход
        /// устройством — это то же устройство и тот же человек (метка
        /// устройства не менялась), вход учёткой — чужой аккаунт. Поэтому
        /// device-регистрация НАСЛЕДУЕТ владение, а платформенный вход — нет.
        /// </para>
        /// </summary>
        public static void InheritOwner(string userId)
        {
            if (string.IsNullOrEmpty(userId)) return;
            _owner = userId;
            if (Get(POwner, "") != userId) Put(POwner, userId);
        }

        /// <summary>Кому принадлежат данные прямо сейчас (пусто — игра без входа).</summary>
        public static string Owner => _owner;

        private static string _owner = "";
        private const string POwner = "lvn.local.owner";
        private static int _batch;      // глубина открытых пачек
        private static bool _pending;   // есть незафиксированное карандашное

        static LvnKeep()
        {
            // Карандаш обязан пережить уход в фон: на телефоне это и есть самый
            // частый способ закрыть игру. Оба события статические — книжке не
            // нужен объект на сцене, а значит и порядок его создания.
            Application.focusChanged += focused => { if (!focused) Flush(); };
            Application.quitting += Flush;
        }

        // ── чтение ───────────────────────────────────────────────────────────

        /// <summary>Строка. Запасное значение — то, что «ничего не записано».</summary>
        public static string Get(string key, string fallback = "")
            => string.IsNullOrEmpty(key) ? fallback : PlayerPrefs.GetString(key, fallback);

        /// <summary>Целое.</summary>
        public static int Get(string key, int fallback)
            => string.IsNullOrEmpty(key) ? fallback : PlayerPrefs.GetInt(key, fallback);

        /// <summary>Дробное.</summary>
        public static float Get(string key, float fallback)
            => string.IsNullOrEmpty(key) ? fallback : PlayerPrefs.GetFloat(key, fallback);

        /// <summary>Записано ли хоть что-нибудь под этим ключом.</summary>
        public static bool Has(string key)
            => !string.IsNullOrEmpty(key) && PlayerPrefs.HasKey(key);

        // ── запись: фиксируется сразу ────────────────────────────────────────

        /// <summary>ЗАПИСАТЬ НАБЕЛО — значение переживёт краш и снятие
        /// приложения. Умолчание для всего, что игрок сочтёт потерянным.</summary>
        public static void Put(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;
            PlayerPrefs.SetString(key, value ?? "");
            Settle();
        }

        /// <summary>Целое, набело.</summary>
        public static void Put(string key, int value)
        {
            if (string.IsNullOrEmpty(key)) return;
            PlayerPrefs.SetInt(key, value);
            Settle();
        }

        /// <summary>Дробное, набело.</summary>
        public static void Put(string key, float value)
        {
            if (string.IsNullOrEmpty(key)) return;
            PlayerPrefs.SetFloat(key, value);
            Settle();
        }

        /// <summary>СТЕРЕТЬ — тоже запись, и фиксации требует ровно так же.
        /// Незафиксированное стирание воскрешает то, что игра уже посчитала
        /// забытым: одноразовый флаг, пройденную точку продолжения.</summary>
        public static void Drop(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            PlayerPrefs.DeleteKey(key);
            Settle();
        }

        // ── запись в карандаше: для горячих путей ────────────────────────────

        /// <summary>
        /// ЗАПИСАТЬ В КАРАНДАШЕ — когда путь горячий и полный флаш на каждое
        /// значение виден игроку кадром.
        ///
        /// <para>Это ЗАЯВЛЕНИЕ, а не пропуск: карандашное фиксируется при уходе
        /// в фон, при закрытии и по первому же <see cref="Flush"/> или
        /// <see cref="Put(string,string)"/> рядом.</para>
        /// </summary>
        public static void Jot(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;
            PlayerPrefs.SetString(key, value ?? "");
            _pending = true;
        }

        /// <summary>Целое, в карандаше.</summary>
        public static void Jot(string key, int value)
        {
            if (string.IsNullOrEmpty(key)) return;
            PlayerPrefs.SetInt(key, value);
            _pending = true;
        }

        /// <summary>Стереть в карандаше.</summary>
        public static void JotDrop(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            PlayerPrefs.DeleteKey(key);
            _pending = true;
        }

        // ── пачка ────────────────────────────────────────────────────────────

        /// <summary>
        /// ПАЧКА — много записей, одна фиксация в конце.
        ///
        /// <para><c>using (LvnKeep.Batch()) { … }</c>. Внутри и <c>Put</c> ведёт
        /// себя как карандаш: гардероб метит каталог целиком, кошелёк пишет
        /// зеркало и очередь — фиксировать каждое значение отдельно значит
        /// платить кадром за чужую аккуратность.</para>
        /// </summary>
        public static IDisposable Batch() => new Sheet();

        /// <summary>Зафиксировать карандашное. Вне пачки и при наличии
        /// незаписанного — иначе бесплатно.</summary>
        public static void Flush()
        {
            if (_batch > 0 || !_pending) return;
            _pending = false;
            PlayerPrefs.Save();
        }

        private static void Settle()
        {
            if (_batch > 0) { _pending = true; return; }
            _pending = false;
            PlayerPrefs.Save();
        }

        private sealed class Sheet : IDisposable
        {
            public Sheet() => _batch++;

            public void Dispose()
            {
                if (_batch > 0) _batch--;
                Flush();
            }
        }
    }
}
