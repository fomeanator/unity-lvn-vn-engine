using System;

namespace Lvn.Content
{
    /// <summary>
    /// A content-fetch failure carrying the HTTP status and a short machine code
    /// (<c>"network"</c> for connectivity misses, <c>"http_NNN"</c> for bad
    /// responses). Retry loops branch on these: a <c>4xx</c> is permanent (give
    /// up), a <c>"network"</c> while offline is pointless to retry.
    /// </summary>
    public sealed class LvnFetchException : Exception
    {
        public int Status { get; }
        public string Code { get; }

        public LvnFetchException(int status, string code, string message)
            : base($"{code} ({status}): {message}")
        {
            Status = status;
            Code = code;
        }

        /// <summary>
        /// СЕРВЕР ОТВЕТИЛ «ЭТОГО НЕТ» — а не «я не дозвонился».
        ///
        /// <para>Разница нужна тому, кто объясняет игроку, почему глава не
        /// открылась: при мёртвой связи он идёт проверять вайфай, а при
        /// отсутствующем файле проверять нечего — виноват автор. Спрашивать
        /// это ДОМА, а не разбирать код строкой на месте: страж
        /// «адрес разбирает дом» справедливо покраснел на `Code.StartsWith
        /// ("http_4")` — по форме такая строка неотличима от разбора схемы
        /// адреса, и правил становится столько же, сколько мест.</para>
        /// </summary>
        public bool MissingOnServer => Status >= 400 && Status < 500;
    }

    /// <summary>
    /// КАК СКАЗАТЬ ИГРОКУ, ЧТО СЕТИ НЕТ.
    ///
    /// <para>Состояние одно, а формулировок было три: «нет сети —
    /// переподключение…» на бут-экране, «Нет сети — позже» на кнопке профиля,
    /// «Нет соединения» в заголовке отказа. Игрок читает их в одном сеансе и
    /// не должен гадать, три ли это разные беды.</para>
    ///
    /// <para>Строки, а не константы: их перекрывает локализация — и на лету,
    /// без пересборки.</para>
    /// </summary>
    public static class LvnOfflineText
    {
        // Слова про сеть — через СЛОВАРЬ: до 28.08 они лежали здесь русскими
        // строками, то есть любая другая новелла получала их насильно и не
        // могла переопределить (та же болезнь, что у валют и «Гостя»).
        // Умолчания движка английские, игра называет своё в ui.words.

        /// <summary>Внутри фразы, со строчной: «…{0} — переподключение…».</summary>
        public static string Word => LvnWords.Of("network.word", "no network");

        /// <summary>Заголовком сообщения.</summary>
        public static string Title => LvnWords.Of("network.title", "No connection");

        /// <summary>Пока пытаемся дотянуться до сервера.</summary>
        public static string Reconnecting => LvnWords.Of("network.reconnecting", "no network — reconnecting…");

        /// <summary>Действие не вышло и его стоит повторить позже.</summary>
        public static string TryLater => LvnWords.Of("network.try_later", "No network — later");

        /// <summary>Главу нельзя открыть без сети. Текст стоял ЗАШИТЫМ в хосте,
        /// мимо этого дома, — и потому не переводился вместе с остальными.</summary>
        public static string ChapterNeedsNetwork => LvnWords.Of("network.chapter_needs",
            "This chapter needs a connection. Check it and try again.");

        /// <summary>Сеть исправна, а главы на сервере нет: автор её ещё не
        /// выложил или удалил. Игроку незачем проверять вайфай — ему нечего
        /// чинить, и сказать об этом надо прямо.</summary>
        public static string ChapterMissingTitle => LvnWords.Of("chapter.missing_title", "Chapter isn't here yet");

        public static string ChapterMissing => LvnWords.Of("chapter.missing",
            "This chapter isn't published yet. Your progress is safe — try again later.");
    }

    /// <summary>
    /// Exponential backoff for retrying a failed fetch. Attempt 1 has no delay;
    /// every subsequent attempt doubles (capped) so a flaky link recovers
    /// quickly without hammering a dead one.
    /// </summary>
    public static class LvnBackoff
    {
        public const float DefaultCapSeconds = 30f;

        public static float DelaySeconds(int attempt, float capSeconds = DefaultCapSeconds)
        {
            if (attempt <= 1) return 0f;
            var exp = Math.Min(attempt - 2, 30);           // guard against overflow
            var delay = (float)Math.Pow(2d, exp);
            return Math.Min(capSeconds, delay);
        }
    }
}
