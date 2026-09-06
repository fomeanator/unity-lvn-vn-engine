using UnityEngine;

namespace Lvn
{
    /// <summary>
    /// ПАСПОРТ УСТРОЙСТВА — одна точка правды о железе и системе (решение
    /// Ильи 25.08: «просмотр устройства выдели отдельным модулем»). Всё, что
    /// раньше решалось inline-эвристиками по месту (ступень арта, кап кадров,
    /// язык по системе), читает отсюда; хост дополнительно отправляет
    /// снимок в серверный профиль игрока — как это делают все крупные
    /// аналитики (Firebase/Amplitude шлют device model/os/screen автоматом):
    /// саппорт и сегменты видят, НА ЧЁМ играет человек.
    ///
    /// <para>Живёт в ЯДРЕ, а не в интерфейсном слое: про железо спрашивают все —
    /// кэш картинок считает бюджет по памяти, загрузчик спрашивает про формат
    /// текстур, отчёты называют модель и систему. Пока паспорт лежал в UI, до
    /// него не дотягивался слой контента, и он читал железо напрямую; отчёты,
    /// у которых не хватало пары полей (видеочип, номер устройства), заодно
    /// брали напрямую и всё остальное.</para>
    /// </summary>
    public static class LvnDeviceProfile
    {
        /// <summary>Большая сторона экрана в физических пикселях.</summary>
        public static int ScreenPx => Mathf.Max(Screen.width, Screen.height);

        public static int RamMb => SystemInfo.systemMemorySize;

        /// <summary>
        /// СКОЛЬКО ПАМЯТИ ОБЕЩАНО ПРИЛОЖЕНИЮ, А НЕ УСТРОЙСТВУ.
        ///
        /// <para>Вся память устройства — не наша: её делят система, магазин,
        /// лаунчер и всё, что крутится рядом. Замер на устройстве с 976 МБ:
        /// системе и сервисам ушло около полутысячи мегабайт, свободными
        /// оставались 80. Планировать кэш от ОБЩЕЙ памяти в такой обстановке
        /// значит планировать вдвое больше, чем существует.</para>
        ///
        /// <para>Android называет свою обещанную планку сам —
        /// <c>ActivityManager.getMemoryClass()</c>. Это тот размер, на который
        /// приложение вправе рассчитывать; всё сверх него живёт до первой
        /// нехватки. Ноль означает «спросить не у кого» (редактор, другая
        /// платформа) — тогда решение принимается по прежнему правилу.</para>
        /// </summary>
        public static int HeapLimitMb => _heapMb ??= AskHeapLimit();

        private static int? _heapMb;

        private static int AskHeapLimit()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using var am = activity.Call<AndroidJavaObject>("getSystemService", "activity");
                // Большая планка — та, что действует с android:largeHeap; без
                // него обе совпадают, поэтому меньшая из двух и есть правда.
                int normal = am.Call<int>("getMemoryClass");
                int large = am.Call<int>("getLargeMemoryClass");
                return Mathf.Max(normal, 0) == 0 ? 0 : Mathf.Min(Mathf.Max(normal, large), 4096);
            }
            catch { return 0; }   // спросить не удалось — молчим, решает прежнее правило
#else
            return 0;
#endif
        }

        public static float RefreshHz => (float)Screen.currentResolution.refreshRateRatio.value;

        public static string Model => SystemInfo.deviceModel;

        public static string Os => SystemInfo.operatingSystem;

        /// <summary>Видеочип — им объясняются «полосы на земле» и просевший
        /// кадр там, где на соседнем телефоне всё ровно.</summary>
        public static string Gpu => SystemInfo.graphicsDeviceName;

        /// <summary>Опознавательный номер устройства. Нужен отчётам, чтобы
        /// склеить между собой логи, жалобу и сессию одного человека.</summary>
        public static string DeviceId => SystemInfo.deviceUniqueIdentifier;


        /// <summary>Язык системы кодом ISO ("ru", "en", …); "" — не определён.</summary>
        public static string SystemLocale
        {
            get
            {
                switch (Application.systemLanguage)
                {
                    case SystemLanguage.Russian: return "ru";
                    case SystemLanguage.English: return "en";
                    case SystemLanguage.Ukrainian: return "uk";
                    case SystemLanguage.German: return "de";
                    case SystemLanguage.French: return "fr";
                    case SystemLanguage.Spanish: return "es";
                    case SystemLanguage.Portuguese: return "pt";
                    case SystemLanguage.Italian: return "it";
                    case SystemLanguage.Turkish: return "tr";
                    case SystemLanguage.Polish: return "pl";
                    case SystemLanguage.Japanese: return "ja";
                    case SystemLanguage.Korean: return "ko";
                    case SystemLanguage.Chinese:
                    case SystemLanguage.ChineseSimplified:
                    case SystemLanguage.ChineseTraditional: return "zh";
                    default: return "";
                }
            }
        }

        // КАКОМУ ТЕЛЕФОНУ КАКОЙ АРТ — продуктовое решение, а не деталь: от него
        // зависит, влезет ли игра в память дешёвого устройства и не будет ли
        // дорогое показывать мыло. Пороги стояли четырьмя безымянными числами
        // прямо в условии, и обсудить «а не поднять ли планку» было не с чем.
        //
        // Читаются они парами «экран И память»: крупный экран при малой памяти
        // не даёт права на крупный арт — именно такие устройства и падают на
        // распакованных текстурах.

        /// <summary>Планка крупного арта (@2k): флагманский экран и память,
        /// где полноразмерные текстуры живут спокойно.</summary>
        private const int HighScreenPx = 2000;
        private const int HighRamMb = 4096;

        /// <summary>Планка среднего арта (@1440): экран уже плотный, но памяти
        /// на @2k не хватит.</summary>
        private const int MidScreenPx = 1400;
        private const int MidRamMb = 3072;

        /// <summary>СОВЕТ, СНЯТЫЙ ОДИН РАЗ И С ГЛАВНОГО ПОТОКА.
        ///
        /// <para><c>Screen</c> и <c>SystemInfo</c> Unity отдаёт только главному
        /// потоку — с рабочего они бросают исключение. Совет же спрашивают там,
        /// где строят адреса ассетов, а адреса строит и фон.</para>
        ///
        /// <para>Сегодня это безопасно ТОЛЬКО ПО ПОРЯДКУ: первым спрашивает бут,
        /// он главный поток, и дальше все читают уже посчитанное. Порядок —
        /// плохая защита: он держится, пока никто не заведёт нового фонового
        /// читателя, и падение будет выглядеть случайным.</para>
        ///
        /// <para>Поэтому снимок берётся сам, из точки, которую платформа
        /// обещает выполнить на главном потоке до первой сцены. Читателю после
        /// этого всё равно, откуда он спрашивает.</para></summary>
        [UnityEngine.RuntimeInitializeOnLoadMethod(
            UnityEngine.RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void PrimeAdvice() { _advice = Advise(); _cores = SafeCores(); }

        /// <summary>Сколько потоков у устройства. Нужно тем, кто раздаёт
        /// параллельную работу: полоса шириной в три на двухъядерном телефоне
        /// отнимает процессор у главного потока, и загрузка ассетов начинает
        /// «лагать» именно тогда, когда игрок ждёт картинку.</summary>
        public static int Cores => _cores > 0 ? _cores : (_cores = SafeCores());

        private static int _cores;

        private static int SafeCores()
        {
            try { return Mathf.Max(1, SystemInfo.processorCount); }
            catch { return 0; }   // спросили не с главного потока — решит вызывающий
        }

        private static string _advice;

        /// <summary>Что советует устройство. Первый вопрос с главного потока
        /// запомнит ответ; заботиться об этом вызывающему не нужно.</summary>
        public static string RecommendedArtQuality() => _advice ??= Advise();

        private static string Advise()
        {
            if (ScreenPx >= HighScreenPx && RamMb >= HighRamMb) return "2k";
            if (ScreenPx >= MidScreenPx && RamMb >= MidRamMb) return "1440";
            return "1k";   // всё остальное, включая телефоны на 500–1000 МБ
        }

        /// <summary>Кап кадров по экрану: просить 60 у 30-герцовой панели
        /// бессмысленно.</summary>
        public static int FpsCap() => RefreshHz >= 59f ? 60 : 30;

        /// <summary>Снимок для серверного профиля/аналитики — плоские пары,
        /// готовые лечь в свойства события.</summary>
        public static (string key, object value)[] Snapshot() => new (string, object)[]
        {
            ("model", Model),
            ("os", Os),
            ("screen_px", ScreenPx),
            ("screen_w", Screen.width),
            ("screen_h", Screen.height),
            ("refresh_hz", Mathf.RoundToInt(RefreshHz)),
            ("ram_mb", RamMb),
            ("sys_locale", SystemLocale),
            ("rec_quality", RecommendedArtQuality()),
        };
    }
}
