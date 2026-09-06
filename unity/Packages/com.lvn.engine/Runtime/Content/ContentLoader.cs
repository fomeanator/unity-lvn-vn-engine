using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Lvn.Content
{
    /// <summary>
    /// Downloads <c>.lvn</c> scripts and asset bytes, caches them on disk, and
    /// returns local data on subsequent reads. Cache key = <c>sha1(url@version)</c>
    /// — a re-uploaded asset (new hash in the version index) maps to a NEW cache
    /// file and is re-downloaded automatically, while the old file stays as an
    /// offline fallback.
    ///
    /// This is the low-level fetch/decode/cache engine ported from a shipping
    /// visual-novel client: disk cache, an in-memory sprite cache, dedup of
    /// in-flight fetches, a global HTTP/2 download semaphore, resumable retries
    /// with exponential backoff, content-version cache-busting, and byte-level
    /// progress for a loading HUD. <see cref="AssetScheduler"/> sits on top to
    /// prioritize a chapter's release set; <c>NetworkAssets</c> adapts it to the
    /// engine's <c>ILvnAssets</c> seam.
    /// </summary>
    public partial class ContentLoader : System.IDisposable, ILvnPinLedger
    {
        private readonly string _baseUrl;
        // True when the content origin is a local bundle (file:// on desktop, or
        // jar:file:// for Android StreamingAssets). Local reads are always
        // available — they skip the offline gate and the ?v= cache-buster (which
        // would corrupt a file path), so an exported game plays with no server.
        private readonly bool _local;
        private readonly string _cacheRoot;
        private readonly string _scriptCacheDir;
        private readonly string _assetCacheDir;

        // Content-version index: path → sha256, fetched from
        // /content/asset-versions.json. Folded into the disk-cache key so a
        // re-uploaded asset (new hash) maps to a NEW cache file and is
        // re-downloaded automatically, while the old file stays as an offline
        // fallback. Empty until LoadAssetVersionsAsync runs; an unknown asset
        // falls back to the legacy url-only key (still works, just not auto-busted).
        private Dictionary<string, string> _versions = new();
        private readonly object _versionsLock = new();
        private static readonly string VersionsPath = LvnAssetPath.Under("asset-versions.json");


        // Срок ответа — из общего дома: «сколько игра ждёт сеть» спрашивают
        // ещё двое (хранилище состояния и сервисный клиент), и раньше каждый
        // отвечал своим числом. Объяснение живёт там же, при числе.
        private const int RequestTimeoutSeconds = Lvn.LvnNetPatience.RequestSeconds;

        // Срок молчания в передаче — оттуда же (см. LvnNetPatience.StallSeconds).
        private const int StallTimeoutSeconds = Lvn.LvnNetPatience.StallSeconds;

        // Classify a failed request: only a connect-level failure (no bytes,
        // not a timeout/abort) means THE NETWORK is gone and pins the global
        // offline flag. A transfer that died mid-body or timed out is
        // congestion — this fetch failed, the network may be fine; the caller's
        // retry layer handles it without dragging the whole app offline.
        /// <summary>
        /// Ассет не доехал: адрес и код ответа. Движок сам никуда это не шлёт —
        /// он не знает про продуктовую аналитику и не должен, — но и молчать не
        /// вправе: для игрока это пропавшая картинка или тишина вместо музыки, а
        /// снаружи оно выглядит как «игра кривая». Подписывается оболочка.
        /// </summary>
        public static event Action<string, long> AssetFailed;

        /// <summary>
        /// Файл ДОЕХАЛ, но картинкой не стал: битый png, формат без поддержки на
        /// этом устройстве, транскод, которого нет в сборке.
        ///
        /// <para>Отдельно от <see cref="AssetFailed"/>, потому что причина
        /// другая и лечится по-другому: там сеть или адрес, здесь сам файл. А
        /// сообщать надо так же — для игрока это ровно та же пропавшая героиня.
        /// Раньше этот случай не сообщался НИКАК: показ ловил исключение,
        /// оставлял силуэт и шёл дальше, а в логе и в отчёте не оставалось
        /// ничего. «Игра кривая» — и никаких следов, почему.</para>
        ///
        /// <para>Код ответа здесь нулевой: HTTP тут ни при чём. Оболочка шлёт
        /// это тем же событием, что и недоехавший ассет, — для отчёта разница
        /// не в причине, а в том, что игрок недосчитался картинки.</para>
        /// </summary>
        public static event Action<string, string> AssetUnusable;

        /// <summary>Сказать, что файл не стал картинкой. Зовут показ актёра и
        /// фон — те, кто первым это обнаруживает.</summary>
        public static void NoteAssetUnusable(string url, string why)
        {
            if (string.IsNullOrEmpty(url)) return;
            // Не веха, а происшествие: файл доехал, а картинкой не стал —
            // игрок увидит пустое место. Прежде здесь стояло оправдание обхода
            // («журнал живёт в Lvn.UI, а контент его не видит — и правильно,
            // содержимое не должно зависеть от интерфейса»). Половина верная:
            // зависеть не должно. Но вывод из неё — что журнал НЕ интерфейсный
            // дом, а общий; он и переехал в ядро.
            LvnLog.Warn($"[lvn-content] файл есть, картинкой не стал: {url} — {why}");
            try { AssetUnusable?.Invoke(url, why ?? ""); }
            catch { /* диагностика не смеет ронять кадр */ }
        }





        // 1 while the background recovery probe is running (guards against starting
        // a second one on every subsequent failed fetch).
        private int _recovering;





        // Dedup tracker for in-flight fetches. Key = url, value = the running
        // task. Lets two callers (a preload + a later regular download) await the
        // same fetch instead of re-issuing it. Tasks self-evict on completion so
        // the dictionary doesn't leak.

        // Batch counters used by a network-progress HUD. A "batch" is the
        // contiguous run of fetches between idle moments — once every queued task
        // finishes, the counters reset to zero so the next batch starts clean.
        public int BatchTotal { get; private set; }
        public int BatchDone { get; private set; }

        /// <summary>
        /// БАЙТЫ ВСЕГО ПАКЕТА, А НЕ ТОЛЬКО ТЕХ, ЧТО В ПОЛЁТЕ.
        ///
        /// <para>Кольцо индикатора считалось как «принято / ожидается», где оба
        /// числа — сумма по файлам В ПОЛЁТЕ. Пока обоз шёл по одному файлу, это
        /// случайно совпадало с правдой: один файл и был всей видимой работой.
        /// Как только он пошёл в десять полос, кольцо начало заполняться и
        /// откатываться на каждой десятке из сотни — игрок видит не очередь, а
        /// текущую горсть.</para>
        ///
        /// <para>Здесь копится ЗАКРЫТОЕ: байты файлов, которые пакет уже
        /// дописал. К ним прибавляется принятое в полёте — и получается доля
        /// всей очереди, которая только растёт.</para>
        /// </summary>
        public long BatchClosedBytes { get; private set; }

        /// <summary>Вес всего пакета по манифесту, байт (0 — размеров не дали).
        /// План известен ЗАРАНЕЕ, поэтому доля считается им, а не догадкой о
        /// непочатых файлах.</summary>
        public long BatchPlannedBytes { get; private set; }

        public string LastStartedUrl { get; private set; }
        public bool BatchActive => BatchTotal > 0 && BatchDone < BatchTotal;

        // True while VerifyAsync is scanning local cache — the HUD shows a
        // "verifying files" state instead of a filename.
        public bool IsVerifying { get; private set; }

        // Retry count per url (1 = first try, 2+ = previous attempts failed).
        // Surfaced to the HUD so it can show "attempt N" on a flaky network.

        // Session-scoped sprite cache. Sprites are keyed by URL so the same
        // background or portrait is decoded once — and BOUNDED: full-res RGBA32
        // decodes are big (a 1080p background ≈ 8 MB), so an unbounded cache is an
        // OOM on a large title. Over budget, the least-recently-requested entries
        // are destroyed — except anything touched within the grace window, which
        // is how "probably still on screen" art is protected without a pin API.
        private sealed class SpriteEntry
        {
            public Sprite Sprite;
            public long Bytes;
            public long Seq;   // request recency (monotonic)
            public float At;   // request time (realtime seconds)
            public int Pins;   // >0 ⇒ a live consumer (e.g. a built Spine skeleton
                               // whose atlas references this texture) forbids eviction
        }

        private long _spriteSeq;
        private long _spriteBytes;

        /// <summary>Decoded-sprite memory budget. Over it, the least-recently-used
        /// sprites are evicted (grace-protected — see <see cref="SpriteEvictionGraceSeconds"/>).
        /// Tune down for low-memory targets.</summary>
        public static long SpriteCacheBudgetBytes = 384L << 20;

        /// <summary>Запись, запрошенную недавно, не вытесняем: только что
        /// затребованный арт почти наверняка ещё на экране.</summary>
        public static float SpriteEvictionGraceSeconds = 60f;

        // Author-supplied display labels for urls, persistent across the session.
        private readonly Dictionary<string, string> _aliases = new();
        public void RegisterAlias(string url, string alias)
        {
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(alias)) return;
            lock (_aliases) _aliases[url] = alias;
        }
        public string AliasOf(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            lock (_aliases)
            {
                if (_aliases.TryGetValue(url, out var a)) return a;
                // Aliases are stored as relative paths (/content/...) but url may
                // be an absolute URL — try matching by path only.
                try
                {
                    var path = new System.Uri(url).AbsolutePath;
                    return _aliases.TryGetValue(path, out var b) ? b : null;
                }
                catch { return null; }
            }
        }

        // Per-url byte progress, updated each frame while a fetch runs. Lets the
        // HUD show byte-level progress instead of file-count progress, which
        // feels stuck when a single file downloads for many seconds.
        /// <summary>
        /// КАК ИДЁТ ОДНА ЗАГРУЗКА — одна запись на адрес.
        ///
        /// <para>Имя «Underway», а не «Fetch»: «Fetch» в этом же классе уже
        /// занято способом «сходи и принеси». Тип и глагол с одним именем
        /// компилятор не пускает, и правильно — читателю они тоже мешали бы.</para>
        ///
        /// <para>Памятей было четыре, и все под одним замком, названным именем
        /// одной из них (<c>_inflight</c>):
        /// сколько байт ждём, сколько получили, какая попытка. Замок, названный
        /// именем ОДНОЙ из них, и есть признак — их три, а факт один.</para>
        ///
        /// <para>Разъезд уже случился и стоил вранья на экране. Очистка написана
        /// в двух местах и очищает РАЗНОЕ: сброс итогов пакета убирает все три,
        /// старт пакета — только байты. А до того «мусор одиночных закачек
        /// въезжал в прогресс батча» и давал «Скачано 131 из 135» при пустой
        /// очереди — починили добавлением ещё одной очистки, то есть залатали
        /// МЕСТО, а не форму.</para>
        ///
        /// <para>«Ноль» и «записи нет» здесь теперь одно и то же для байтов
        /// (сумма от этого не меняется) и РАЗНОЕ для попытки: ноль означает
        /// «эта закачка ещё не начинала считать повторы».</para>
        ///
        /// <para><b>Четвёртая память переехала сюда 01.09.</b> Сама задача жила
        /// в отдельном словаре, и замок носил ЕГО имя — <c>lock (_underway)</c>
        /// вокруг полей, которые к нему не относятся. Замок, названный именем
        /// одной из охраняемых памятей, и есть признак: их было четыре, а факт
        /// один — «вот эта загрузка идёт вот так».</para>
        /// </summary>
        private sealed class Underway
        {
            public long Received;
            public long Expected;
            public int  Attempt;   // 0 — повторов ещё не считали
            public Task Work;      // идёт прямо сейчас; null — уже нет
            public bool Bundle;    // это не файл, а весь пакет главы
        }

        /// <summary>Все идущие и недавние загрузки. ОН ЖЕ ЗАМОК: караулить
        /// память её собственным именем честнее, чем именем соседа.</summary>
        private readonly Dictionary<string, Underway> _underway = new();

        /// <summary>Запись о загрузке по адресу; заводится по первому
        /// обращению. ЗВАТЬ ТОЛЬКО ПОД <c>lock (_underway)</c>.</summary>
        private Underway Progress(string url)
        {
            if (!_underway.TryGetValue(url, out var f)) _underway[url] = f = new Underway();
            return f;
        }

        /// <summary>Работа кончилась. Запись остаётся, пока ей есть что
        /// сказать (байты нужны итогам пакета), и уходит, когда сказать нечего.
        /// ЗВАТЬ ТОЛЬКО ПОД <c>lock (_underway)</c>.</summary>
        private void WorkDone(string url)
        {
            if (!_underway.TryGetValue(url, out var f)) return;
            f.Work = null;
            if (f.Received == 0 && f.Expected == 0 && f.Attempt == 0) _underway.Remove(url);
        }

        /// <summary>Сколько байт числится за адресом сейчас (0, если о нём
        /// ещё ничего не знают).</summary>
        private long ExpectedOf(string url)
            => _underway.TryGetValue(url, out var f) ? f.Expected : 0L;

        // Label of the file currently being fetched (alias or short name).
        public string CurrentFileLabel { get; private set; }
        public long BatchBytesExpected
        {
            get { lock (_underway) { long s = 0; foreach (var f in _underway.Values) s += f.Expected; return s; } }
        }
        public long BatchBytesReceived
        {
            get { lock (_underway) { long s = 0; foreach (var f in _underway.Values) s += f.Received; return s; } }
        }

        /// <summary>Единый снимок сетевой активности для глобального индикатора
        /// («что сейчас качается» поверх всей оболочки): файлы в полёте,
        /// счётчики батча и суммарные байты — всё под одним замком, чтобы
        /// пилюля не ловила рассинхронные числа. Механика загрузки размазана
        /// по фазам (скачать-всё, прелоад главы, стриминг) — здесь их общее
        /// окно.</summary>
        public TransferSnapshot Transfers()
        {
            lock (_underway)
            {
                int n = 0, unknown = 0, retrying = 0;
                string firstUrl = null;
                long rec = 0, exp = 0;
                foreach (var kv in _underway)
                {
                    var f = kv.Value;
                    rec += f.Received;
                    exp += f.Expected;
                    // ПАКЕТ — НЕ ФАЙЛ. Он и раньше исключался, но по точному
                    // равенству с «__preload_batch__», а настоящий ключ несёт
                    // ещё и отпечаток списка — условие не срабатывало НИ РАЗУ.
                    // Пакет считался файлом в полёте и мог стать именем на
                    // карточке индикатора: игрок видел служебный ключ вместо
                    // названия. Признак теперь у записи, а не у формы ключа.
                    if (f.Bundle || f.Work == null) continue;
                    n++;
                    if (f.Expected <= 0) unknown++;   // заголовок с длиной ещё не пришёл
                    // ПОВТОР — ЭТО ОБЪЯСНЕНИЕ ОСТАНОВКИ. Файл, ушедший на
                    // вторую попытку, ждёт сети (пауза перед повтором длится,
                    // пока сеть не вернётся), и снаружи это выглядит как
                    // «зависло». Сказать «повтор» дешевле, чем заставлять
                    // игрока гадать.
                    if (f.Attempt > 1) retrying++;
                    firstUrl ??= kv.Key;
                }
                // Имя для полной карточки индикатора: алиас текущего файла
                // батча, иначе короткое имя первого файла в полёте.
                string label = CurrentFileLabel;
                if (string.IsNullOrEmpty(label) && firstUrl != null)
                {
                    label = AliasOf(firstUrl);
                    if (string.IsNullOrEmpty(label))
                    {
                        var bare = LvnUrl.Bare(firstUrl);
                        int slash = bare.LastIndexOf('/');
                        label = slash >= 0 ? bare.Substring(slash + 1) : bare;
                    }
                }
                // ВЕСЬ ПАКЕТ, А НЕ ГОРСТЬ В ПОЛЁТЕ.
                //
                // До сюда rec/exp — сумма по файлам, которые едут ПРЯМО
                // СЕЙЧАС. Пока обоз шёл по одному, это случайно совпадало с
                // правдой. В десять полос кольцо стало показывать десятку из
                // сотни: заполнялось и откатывалось на каждой смене десятки.
                //
                // К принятому прибавляются байты уже ЗАКРЫТЫХ файлов пакета —
                // и число перестаёт падать вовсе. Это же чинит скорость: она
                // считается разностью принятого, а на границе файлов сумма
                // проваливалась в ноль, и показатель мигал прочерком.
                //
                // Вес непочатых файлов неизвестен — за них берётся скромная
                // оценка (та же, что у полос загрузки). Занизить лучше, чем
                // завысить: прогресс, который к концу замедляется, игрок
                // прощает — прыгнувший к завершению читается как обман.
                if (BatchTotal > 0)
                {
                    rec += BatchClosedBytes;
                    if (BatchPlannedBytes > 0)
                    {
                        // ПЛАН, А НЕ ДОГАДКА. Веса файлов манифест назвал ещё до
                        // первого байта, и знаменатель — их сумма. Догадка ниже
                        // остаётся ровно для пакетов, чьих весов никто не знает
                        // (проверка версий, разовый фетч): её цена замерена —
                        // при реальных 5 % она рисует 36 %.
                        exp = BatchPlannedBytes;
                    }
                    else
                    {
                        exp += BatchClosedBytes;
                        int untouched = Math.Max(0, BatchTotal - BatchDone - n);
                        exp += (unknown + untouched) * DownloadPolicy.UnknownSizeBytes;
                    }
                    // Принято больше плана — так бывает: манифест назвал вес
                    // прежней редакции файла, а приехала новая. Кольцо не имеет
                    // права показать больше единицы.
                    if (exp < rec) exp = rec;
                }
                return new TransferSnapshot(n, BatchTotal, BatchDone, rec, exp, BatchPlannedBytes, retrying, label);
            }
        }

        public ContentLoader(string baseUrl, string cacheRoot = null)
        {
            _baseUrl = LvnUrl.Base(baseUrl);
            _local = LvnUrl.Local(_baseUrl);
            cacheRoot ??= Path.Combine(Application.persistentDataPath, "cache");
            _cacheRoot = cacheRoot;
            _scriptCacheDir = Path.Combine(cacheRoot, "scripts");
            _assetCacheDir = Path.Combine(cacheRoot, "assets");
            Directory.CreateDirectory(_scriptCacheDir);
            Directory.CreateDirectory(_assetCacheDir);
            SweepStaleParts();
            TuneBudgetForDevice();
            Application.lowMemory += OnLowMemory;
        }

        /// <summary>
        /// ОТПУСТИТЬ ЗАГРУЗЧИК — обязательно для ВРЕМЕННОГО.
        ///
        /// <para>Конструктор подписывается на <c>Application.lowMemory</c>, а
        /// это событие ЖИВЁТ ВСЮ ИГРУ: не сняв подписку, объект нельзя собрать
        /// никогда, и на нехватку памяти отзовётся в том числе десяток мёртвых
        /// загрузчиков — каждый со своим (пустым) кэшем и своей строкой в
        /// журнале. Живой случай: экран выбора сервера заводил загрузчик НА
        /// КАЖДУЮ пробу адреса, чтобы один раз позвать healthz.</para>
        ///
        /// <para>Хозяйскому загрузчику звать это не нужно — он живёт столько
        /// же, сколько игра. Дом нужен как раз тем, кто живёт меньше.</para>
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;   // «отпустить дважды» бывает штатно
            _disposed = true;
            Application.lowMemory -= OnLowMemory;
        }

        private bool _disposed;

        // Бюджет по УСТРОЙСТВУ, а не константа: 384 МБ декода на телефоне с
        // 512 МБ RAM — это смертный приговор от системы. Шестая часть RAM в
        // коридоре 96..384 МБ; выполняется один раз (владелец budget-поля —
        // хост, повторные лоадеры не перетирают ручную настройку).
        private static bool _budgetTuned;


        // Resume files (.part) enable interrupted downloads to continue — but one
        // abandoned mid-download (its version has moved on, so its cache key will
        // never be requested again) would sit on disk forever. Sweep any not
        // touched for a week; a live download re-creates its .part instantly.
        private void SweepStaleParts()
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddDays(-7);
                foreach (var f in new DirectoryInfo(_assetCacheDir).GetFiles("*.part"))
                    if (f.LastWriteTimeUtc < cutoff)
                        LvnQuiet.Try(f.Delete);
            }
            catch { /* best-effort housekeeping */ }
        }


        /// <summary>
        /// НАЛОЖИТЬ РАЗНИЦУ ВМЕСТО ПЕРЕКАЧКИ КАРТЫ.
        ///
        /// <para>Карта версий на живом проекте — 282 КБ, и менялась она целиком
        /// от правки одной реплики: хеш её скрипта другой, значит и общая
        /// версия другая, значит забирай всё заново. Сервер умеет назвать
        /// именно изменившееся (<c>/v1/content/changes</c>); здесь это
        /// накладывается на месте.</para>
        ///
        /// <para>Устаревшие спрайты вычищаются тем же правилом, что и при
        /// полной загрузке карты: иначе в памяти остался бы декодированный
        /// вчерашний арт, а на диске лежал бы сегодняшний — и какой из них
        /// увидит игрок, зависело бы от того, что раньше вытеснится.</para>
        /// </summary>
        /// <returns>Сколько записей реально изменилось.</returns>
        public int ApplyVersionDelta(IReadOnlyDictionary<string, string> changed,
                                     IReadOnlyCollection<string> removed)
        {
            if ((changed == null || changed.Count == 0) &&
                (removed == null || removed.Count == 0)) return 0;

            Dictionary<string, string> prev, next;
            lock (_versionsLock)
            {
                prev = _versions;
                next = new Dictionary<string, string>(prev);
                if (changed != null)
                    foreach (var kv in changed) next[kv.Key] = kv.Value;
                if (removed != null)
                    foreach (var path in removed) next.Remove(path);
                _versions = next;
            }
            EvictStaleSprites(prev, next);
            return (changed?.Count ?? 0) + (removed?.Count ?? 0);
        }

        /// <summary>Fetches the server's content-version index (path → sha256) and
        /// folds it into the disk-cache key, so changed assets auto-invalidate.
        /// Call once early in boot, before the verify/preload pass. Always fetched
        /// fresh (never disk-cached) and mirrored to disk so a later offline
        /// launch can still resolve versioned cache keys. Network failure is
        /// non-fatal: fall back to the last persisted index, else legacy url-only
        /// keys.</summary>
        public async Task LoadAssetVersionsAsync(CancellationToken ct = default)
        {
            var persistPath = Path.Combine(_cacheRoot, "asset-versions.json");
            try
            {
                // Single attempt (not Fetch's retry-with-backoff): if we're
                // offline this fails fast on host-resolve instead of stalling
                // boot, and we immediately fall back to the disk mirror.
                var bytes = await FetchOnce(VersionsPath, ct);
                var map = ParseVersions(Encoding.UTF8.GetString(bytes));
                if (map.Count > 0)
                {
                    Dictionary<string, string> prev;
                    lock (_versionsLock) { prev = _versions; _versions = map; }
                    EvictStaleSprites(prev, map);
                    try { await WriteAllBytesAsync(persistPath, bytes, ct); } catch { /* mirror is best-effort */ }
                    return;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* offline / 404 — fall back to last-known index below */ }

            try
            {
                if (File.Exists(persistPath))
                {
                    var json = Encoding.UTF8.GetString(await ReadAllBytesAsync(persistPath, ct));
                    var map = ParseVersions(json);
                    if (map.Count > 0) lock (_versionsLock) _versions = map;
                }
            }
            catch { /* no usable index — legacy url-only keys */ }
        }

        private static Dictionary<string, string> ParseVersions(string json)
        {
            try
            {
                var map = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                return map ?? new Dictionary<string, string>();
            }
            catch { return new Dictionary<string, string>(); }
        }

        // sha256 for a content url from the version index, or null if unknown.
        // Index keys are content-relative paths ("bg/ch1/x.png"); urls arrive as
        // "/content/bg/ch1/x.png" (or absolute urls). Try both the post-/content/
        // form and the raw-with-"content/" form.
        private string VersionFor(string url)
        {
            Dictionary<string, string> map;
            lock (_versionsLock) map = _versions;
            var v = Lookup(map, url);
            // Транскод наследует версию ИСХОДНИКА — значит перекодировка на
            // сервере (починенный кодировщик, выброшенный битый файл) для
            // клиента невидима: картинка-источник не менялась, ключ прежний, и
            // старый .ktx2 едет из кэша вечно. Живой случай 26.08: слои
            // Виктории лежали закодированными из давно заменённого арта —
            // сжатые по горизонтали и с раскрошенной мелкой деталью
            // («пиксели на украшении»). Поколение в ключе — способ разом
            // объявить все прежние транскоды недействительными; поднимать
            // всякий раз, когда меняется контракт кодирования.
            if (v != null && IsTranscodeUrl(url)) return v + "+k" + Ktx2CacheEpoch;
            return v;
        }

        private const int Ktx2CacheEpoch = 2;

        private static bool IsTranscodeUrl(string url) =>
            !string.IsNullOrEmpty(url) &&
            url.EndsWith(".ktx2", StringComparison.OrdinalIgnoreCase);

        // Version for INTEGRITY checks: exact index entries only. A derived
        // variant inherits its source's version (see Lookup) — right for cache
        // keys and ?v= busting, fatally wrong for integrity: an encode's bytes
        // never hash to the source image's sha256, so checking them against it
        // refetches the file forever.
        private string IntegrityVersionFor(string url)
        {
            Dictionary<string, string> map;
            lock (_versionsLock) map = _versions;
            return Lookup(map, url, allowDerived: false);
        }

        internal static string Lookup(Dictionary<string, string> map, string url, bool allowDerived = true)
        {
            if (string.IsNullOrEmpty(url) || map == null || map.Count == 0) return null;
            var path = url;
            if (LvnUrl.Remote(path))
            {
                path = LvnQuiet.Try(() => new System.Uri(path).AbsolutePath, path);
            }
            var p = path.TrimStart('/');                                  // content/bg/... or bg/...
            var afterContent = LvnAssetPath.Relative(p);                  // bg/...
            if (map.TryGetValue(afterContent, out var v)) return v;
            if (map.TryGetValue(p, out var v2)) return v2;
            // Derived display variants ("X@2k.png" downscales, "X.ktx2" codes) are deliberately absent from the index: they appear on
            // the server lazily and versioning them made first visits reload
            // chapters mid-play. They must inherit the version OF THE SOURCE
            // IMAGE instead — a versionless variant gets a permanent cache key,
            // and the encode of a photo the author has since replaced would be
            // served from cache forever (live-hit: the heroine stayed a blurry
            // thumbnail through three art replacements).
            if (allowDerived)
                foreach (var candidate in SourceCandidates(afterContent))
                    if (map.TryGetValue(candidate, out var sv)) return sv;
            return null;
        }

        /// <summary>Index paths a derived variant may have been produced from,
        /// in probe order. Empty for a path that isn't a variant. Pure —
        /// internal for tests.</summary>
        internal static IEnumerable<string> SourceCandidates(string path)
        {
            // Через дом: рукописный разбор не знал, что точка в имени ПАПКИ
            // расширением не считается.
            var (stem, extRaw) = Lvn.LvnUrl.SplitExtension(path);
            if (extRaw.Length == 0) yield break;
            var ext = extRaw.ToLowerInvariant();
            bool transcoded = ext == ".ktx2";
            bool downscaled = stem.EndsWith(DownloadPolicy.DisplayVariant, StringComparison.Ordinal);
            if (!transcoded && !downscaled) yield break;
            if (downscaled) stem = stem.Substring(0, stem.Length - DownloadPolicy.DisplayVariant.Length);
            if (!transcoded) { yield return stem + ext; yield break; }
            // A transcode hides the source's extension — try the same set the
            // server's encoder probes (server/derived.go sourceExts).
            yield return stem + ".png";
            yield return stem + ".jpg";
            yield return stem + ".jpeg";
        }


        // Scripts ship from the server and change often — skip the on-disk cache
        // and refetch every time (a few KB, cheap; stale copies cause "why is the
        // old version playing" bugs). `singleAttempt` skips the retry/backoff loop
        // — use it for non-critical boot fetches so an offline launch fails fast.
        public Task<byte[]> DownloadAssetBytes(string assetUrl, CancellationToken ct = default) =>
            DownloadBytes(assetUrl, _assetCacheDir, ct);


        /// <summary>The longest texture side kept on mobile. Art above it is
        /// GPU-resampled once at load; typical phone screens are ≤ ~2400 px, so
        /// visually this is lossless while a 4K background drops 4× in memory.</summary>
        internal const int MobileMaxTextureSize = 2560;

        /// <summary>The longest texture side kept everywhere ELSE (editor,
        /// desktop, WebGL). Looser than mobile — desktop GPUs have the VRAM for
        /// 4K art — but still a hard ceiling: raw Spine page exports run
        /// 7708×8252 (254 MB of RGBA), and before this cap the non-mobile
        /// platforms uploaded that whole thing (the mobile-only check silently
        /// exempted the shipping WebGL build, the worst place to spend it).</summary>
        internal const int DesktopMaxTextureSize = 4096;















        /// <summary>Downloads an audio asset through UnityWebRequestMultimedia so
        /// the engine decodes the format streaming-style on the main thread (the
        /// correct path — never hand-roll a PCM parser). Caches the raw bytes on
        /// disk, then loads the clip from the cached file.</summary>
        public async Task<AudioClip> DownloadAudioClipAsync(string url, CancellationToken ct = default)
        {
            var path = CachePath(_assetCacheDir, url, ".audio");
            if (!File.Exists(path))
            {
                var bytes = await Fetch(url, ct);
                await WriteAllBytesAsync(path, bytes, ct);
            }
            var type = Lvn.Content.DownloadPolicy.AudioTypeOf(url);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var req = UnityWebRequestMultimedia.GetAudioClip("file://" + path, type);
                await AwaitRequest(req, req.SendWebRequest(), ct);
                if (!LvnNetWait.Failed(req)) return DownloadHandlerAudioClip.GetContent(req);
                // Звук из кэша не читается — там мусор. Та же болезнь, что у
                // картинок: файл остаётся лежать, и сцена молчит при каждом
                // показе. Выбрасываем и качаем заново ровно один раз.
                if (attempt == 0)
                {
                    LvnLog.Warn($"[lvn-content] {url}: звук из кэша не читается — выбрасываю и качаю заново");
                    LvnQuiet.Try(() => File.Delete(path));
                    var again = await Fetch(url, ct);
                    if (again == null || again.Length == 0) return null;
                    await WriteAllBytesAsync(path, again, ct);
                }
            }
            return null;
        }


        /// <summary>Kicks off a background fetch for <paramref name="url"/> with
        /// the given <paramref name="kind"/> ("sprite"|"audio"|"script"|"bin").
        /// Idempotent — if the same url is already being prefetched (or cached on
        /// disk) this is essentially a no-op. Returns the underlying task so
        /// callers can await it.</summary>
        private readonly Dictionary<string, float> _notFound = new();
        private const float NotFoundTtlSeconds = 120f;

        // ── сид первого входа (StreamingAssets/lvn-seed) ─────────────────────
        // APK везёт критичные файлы вводной: первый запуск одевает первую
        // сцену БЕЗ сети вообще. Индекс (index.json со списком rel-путей)
        // читается один раз; промах мимо индекса не стоит ни одного запроса.
        // Сид хранит ОРИГИНАЛЫ: запрос @2k-варианта нормализуется к базе, а
        // потолок декода (MobileMaxTextureSize) ужимает картинку сам.
        private string _seedBase;
        private HashSet<string> _seedIndex;
        private Task _seedLoad;

        /// <summary>Включить сид-источник (jar:file://…/lvn-seed или file://…).
        /// Безопасно звать всегда: без index.json сид просто молчит.</summary>
        private string ResolveUrl(string url)
        {
            if (LvnUrl.Local(url)) return url;
            string full;
            if (LvnUrl.Remote(url))
                full = EncodeUrlPath(url);
            else
            {
                if (!url.StartsWith("/")) url = "/" + url;
                // Кодируем ТОЛЬКО сетевой адрес. У офлайн-сборки база — file://
                // или jar:file://, и оттуда путь уходит в File.Exists и в чтение
                // с диска: закодированный «%20» там означал бы файл, которого
                // нет, и офлайн-игра осталась бы без картинок ради починки
                // сетевого случая.
                full = _local ? _baseUrl + url : _baseUrl + EncodeUrlPath(url);
            }
            // A local bundle reads files by path — a ?v= query would corrupt it.
            if (_local) return full;
            // Append the content version as a query param so the device's HTTP
            // cache treats each asset version as a distinct immutable resource.
            var ver = VersionFor(url);
            if (ver != null)
            {
                var sep = full.Contains('?') ? '&' : '?';
                full += sep + "v=" + ver.Substring(0, Math.Min(12, ver.Length));
            }
            return full;
        }

        /// <summary>
        /// Проценты-кодирование пути URL, посегментно.
        ///
        /// <para>Художник приносит файлы с пробелами, скобками и кириллицей в
        /// именах — «Снимок экрана 2025-01-21.png», «cover (1).jpg», — и в
        /// манифест они попадают как есть. UnityWebRequest такой адрес НЕ
        /// экранирует: на одной платформе запрос уходит сырым и сервер отвечает
        /// 400, на другой — падает разбор Uri. Промах при этом выглядит как
        /// «пропала картинка», и ищут его в контенте, а не в транспорте.</para>
        ///
        /// <para>Кодируем именно сегменты: «/» обязан остаться разделителем, а
        /// query (?v=…) мы дописываем сами и трогать его нельзя. Сегмент,
        /// который уже закодирован, пропускаем — иначе %20 превратится в %2520
        /// и сломается ровно то, что работало.</para>
        /// </summary>
        /// <remarks>Публичный, потому что тем же адресом ходит слой UI
        /// (Lvn.Engine.UI — отдельная сборка) и встраивающий хост: две копии
        /// этого кодирования разъехались бы, а промах выглядит как «пропала
        /// картинка».</remarks>
        public static string EncodeUrlPath(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            // Локальные адреса не кодируем НИКОГДА: за file:// и jar:file://
            // стоит чтение с диска (File.Exists, распаковка из APK), и «%20»
            // там означает файл, которого нет. Проверка живёт здесь, а не у
            // вызывающего: метод публичный, и хост позовёт его как придётся.
            if (LvnUrl.Local(url)) return url;

            // Схема и хост остаются как есть: кодировать надо путь, а не адрес.
            int start = 0;
            int scheme = url.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0)
            {
                int slash = url.IndexOf('/', scheme + 3);
                if (slash < 0) return url; // адрес без пути
                start = slash;
            }
            // Хвост с query/фрагментом не наш — оставляем нетронутым.
            int cut = url.IndexOfAny(new[] { '?', '#' }, start);
            string head = url.Substring(0, start);
            string path = cut < 0 ? url.Substring(start) : url.Substring(start, cut - start);
            string tail = cut < 0 ? "" : url.Substring(cut);

            var parts = path.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0 || AlreadyEncoded(parts[i])) continue;
                parts[i] = Uri.EscapeDataString(parts[i]);
            }
            return head + string.Join("/", parts) + tail;
        }

        /// Сегмент считается закодированным, если в нём есть «%XX» — второй
        /// проход по такому сегменту только испортил бы его.
        private static bool AlreadyEncoded(string segment)
        {
            for (int i = 0; i + 2 < segment.Length; i++)
            {
                if (segment[i] != '%') continue;
                if (Uri.IsHexDigit(segment[i + 1]) && Uri.IsHexDigit(segment[i + 2])) return true;
            }
            return false;
        }

        // On-disk cache file for a content URL: sha1(url@version) hex + ext, where
        // `version` is the asset's sha256 from the version index. Folding the
        // version into the key means a re-uploaded asset gets a fresh cache file
        // (auto-invalidation) without clobbering the old one. Unknown/unversioned
        // assets fall back to sha1(url) — legacy behaviour.
        /// <summary>Ключи кэша, под которыми может лежать этот url: сам файл и
        /// все его варианты (@2k, @mini, .ktx2) — их и держит уборка живыми.</summary>
        public void AddLiveKeysFor(string url, HashSet<string> into)
        {
            if (string.IsNullOrEmpty(url) || into == null) return;
            void Add(string u)
            {
                if (!string.IsNullOrEmpty(u)) into.Add(HashKey(u, VersionFor(u)));
            }
            Add(url);
            var v = DownloadPolicy.DownscaleVariant(url);
            if (v != null)
            {
                // Все ступени живые: игрок может переключать «Качество арта».
                Add(v.Replace(DownloadPolicy.PreferredSuffix, DownloadPolicy.DisplayVariant));
                Add(v.Replace(DownloadPolicy.PreferredSuffix, DownloadPolicy.Q1440));
                Add(v.Replace(DownloadPolicy.PreferredSuffix, DownloadPolicy.Q1k));
                Add(v.Replace(DownloadPolicy.PreferredSuffix, DownloadPolicy.QMini));
                Add(Ktx2UrlFor(url));
            }
        }

        /// <summary>Удалить один закэшированный ассет (и его ktx2-транскод) с
        /// диска — чистка противоположного бокса при смене «Качества арта».</summary>
        public bool DeleteCachedAsset(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            bool any = false;
            try
            {
                var path = CachePath(_assetCacheDir, url, ".bin");
                if (File.Exists(path)) { File.Delete(path); any = true; }
                var k = Ktx2UrlFor(url);
                if (k != null)
                {
                    var kp = CachePath(_assetCacheDir, k, ".bin");
                    if (File.Exists(kp)) { File.Delete(kp); any = true; }
                }
            }
            catch { } // файл держит другой процесс или его уже нет — забыть
            return any;
        }

        /// <summary>Занято дисковым кэшем ассетов (байты), на пуле потоков.</summary>
        public Task<long> AssetCacheDiskUsageAsync() => Task.Run(() =>
        {
            long total = 0;
            try
            {
                foreach (var f in new DirectoryInfo(_assetCacheDir).GetFiles("*.bin"))
                    total += f.Length;
            }
            catch { } // папки ещё нет или её читают — покажем ноль, это лишь справка
            return total;
        });

        /// <summary>Стереть скачанное («Удалить загруженное»): весь дисковый кэш
        /// ассетов. Сид в APK цел, RAM-кэш доигрывает своё — дальше стриминг
        /// пересоберёт нужное. На пуле потоков.</summary>
        public Task<long> ClearAssetCacheAsync() => Task.Run(() =>
        {
            long freed = 0;
            try
            {
                foreach (var f in new DirectoryInfo(_assetCacheDir).GetFiles())
                {
                    long sz = f.Length;
                    // Занятый файл пропускаем: чистка — не транзакция, что
                    // не удалилось сейчас, удалится в следующий раз.
                    try { f.Delete(); freed += sz; } catch { }   // файл держит кто-то ещё — уберём в следующую уборку
                }
            }
            catch { } // папки нет — стирать нечего
            return freed;
        });

        /// <summary>Убрать из дискового кэша ассетов мёртвое и, над квотой,
        /// давнее. Файловый IO — на пуле потоков; спрайт-кэш RAM не трогается.</summary>
        public Task<(int removed, long freed)> SweepAssetCacheAsync(
            HashSet<string> liveKeys, HashSet<string> protectedKeys, long quotaBytes)
            => Task.Run(() =>
            {
                int removed = 0; long freed = 0;
                try
                {
                    var files = new DirectoryInfo(_assetCacheDir).GetFiles("*.bin");
                    var list = new List<(string key, long size, double mtime)>(files.Length);
                    var byKey = new Dictionary<string, FileInfo>(files.Length);
                    foreach (var f in files)
                    {
                        var key = Path.GetFileNameWithoutExtension(f.Name);
                        list.Add((key, f.Length, f.LastWriteTimeUtc.Ticks / (double)TimeSpan.TicksPerSecond));
                        byKey[key] = f;
                    }
                    foreach (var key in PickCacheVictims(list, liveKeys, protectedKeys, quotaBytes))
                        if (byKey.TryGetValue(key, out var f))
                        {
                            long sz = f.Length;
                            try { f.Delete(); removed++; freed += sz; } catch { } // занят — оставим до следующей уборки
                        }
                }
                catch { /* уборка — сервис, не условие */ }
                return (removed, freed);
            });

        /// <summary>Чистая политика уборки, exposed for tests: мёртвые ключи —
        /// всегда; над квотой — старейшие из живых незащищённых.</summary>
        internal static List<string> PickCacheVictims(
            List<(string key, long size, double mtime)> files,
            HashSet<string> liveKeys, HashSet<string> protectedKeys, long quotaBytes)
        {
            var victims = new List<string>();
            long total = 0;
            foreach (var f in files) total += f.size;
            foreach (var f in files)
                if (liveKeys == null || !liveKeys.Contains(f.key))
                {
                    victims.Add(f.key);
                    total -= f.size;
                }
            if (total > quotaBytes)
            {
                var live = new List<(string key, long size, double mtime)>();
                foreach (var f in files)
                    if (liveKeys != null && liveKeys.Contains(f.key)
                        && (protectedKeys == null || !protectedKeys.Contains(f.key)))
                        live.Add(f);
                live.Sort((a, b) => a.mtime.CompareTo(b.mtime)); // давние первыми
                foreach (var f in live)
                {
                    if (total <= quotaBytes) break;
                    victims.Add(f.key);
                    total -= f.size;
                }
            }
            return victims;
        }

        internal static string HashKey(string url, string version)
        {
            var key = version == null ? url : url + "@" + version;
            using var sha = SHA1.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static async Task<string> ReadAllTextAsync(string path, CancellationToken ct)
        {
#if UNITY_2021_2_OR_NEWER
            return await File.ReadAllTextAsync(path, ct);
#else
            return await Task.Run(() => File.ReadAllText(path), ct);
#endif
        }

        private static async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct)
        {
#if UNITY_2021_2_OR_NEWER
            return await File.ReadAllBytesAsync(path, ct);
#else
            return await Task.Run(() => File.ReadAllBytes(path), ct);
#endif
        }

        private static async Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken ct)
        {
            // Always atomic — a half-written cache file is worse than none (File.Exists
            // would treat the truncated file as valid on the next run).
            await Task.Run(() => AtomicWriteAllBytes(path, bytes), ct);
        }

        // Atomic write: stage to a unique temp file in the same directory, then move
        // it into place (mirrors the .part → File.Move pattern DownloadBytes uses).
        // The destination path therefore only ever holds a complete file — never a
        // truncated one from an interrupted write.
        public static void AtomicWriteAllBytes(string path, byte[] bytes)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp-" + Lvn.LvnMark.Once();
            try
            {
                File.WriteAllBytes(tmp, bytes);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch
            {
                LvnQuiet.Try(() => { if (File.Exists(tmp)) File.Delete(tmp); });
                throw;
            }
        }

        /// <summary>
        /// То же для текста. Заведено потому, что своя копия этой записи уже
        /// нашлась у хранилища прогресса — и была БЕДНЕЕ: без создания каталога,
        /// с постоянным именем временного файла (две записи подряд наступают
        /// друг на друга) и без уборки этого файла, если запись сорвалась.
        /// </summary>
        public static void AtomicWriteAllText(string path, string text)
            => AtomicWriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(text ?? ""));
    }

    /// <summary>
    /// СНИМОК СЕТИ ОДНИМ ЗНАЧЕНИЕМ — то, что индикатор спрашивает у загрузчика
    /// каждые 300 мс.
    ///
    /// <para>Здесь стоял кортеж из шести безымянных полей, и читать его на
    /// стороне индикатора приходилось по порядку: `t.expected` — это план или
    /// догадка? Ответ жил в другом файле, и от него зависело, что показать
    /// игроку: долю или спиннер. Имя поля дешевле такого путешествия.</para>
    /// </summary>
    public readonly struct TransferSnapshot
    {
        /// <summary>Файлов в полёте прямо сейчас.</summary>
        public readonly int Inflight;
        /// <summary>Файлов в пакете и сколько из них закрыто.</summary>
        public readonly int BatchTotal, BatchDone;
        /// <summary>Принято байт и сколько их ожидается.</summary>
        public readonly long Received, Expected;
        /// <summary>Вес пакета по манифесту, 0 — весов не дали. Отличает
        /// ЗНАНИЕ от догадки: без плана индикатор обязан крутить спиннер, а не
        /// рисовать почти полное кольцо.</summary>
        public readonly long PlannedBytes;
        /// <summary>Файлов, ушедших на повторную попытку. Пауза перед
        /// повтором длится, пока не вернётся сеть, — снаружи это и есть
        /// «зависло», и назвать его надо словом.</summary>
        public readonly int Retrying;
        /// <summary>Человеческое имя текущего файла (алиас или короткое имя).</summary>
        public readonly string Label;

        public TransferSnapshot(int inflight, int batchTotal, int batchDone,
            long received, long expected, long plannedBytes, int retrying, string label)
        {
            Inflight = inflight; BatchTotal = batchTotal; BatchDone = batchDone;
            Received = received; Expected = expected; PlannedBytes = plannedBytes;
            Retrying = retrying; Label = label;
        }

        /// <summary>Есть ли работа: файл в полёте или незакрытый пакет.</summary>
        public bool Working => Inflight > 0 || (BatchTotal > 0 && BatchDone < BatchTotal);

        /// <summary>Известен ли вес работы — тем и отличается доля от догадки.</summary>
        public bool PlanKnown => PlannedBytes > 0;
    }

    /// <summary>Lightweight descriptor for a single preload batch entry.</summary>
    public sealed class PreloadItem
    {
        public string Url;
        public string Kind;
        public string Alias;
        /// <summary>Вес файла по манифесту, байт (0 — неизвестен).
        ///
        /// <para>Ради него всё и заведено: пока план пакета не знал размеров,
        /// доля считалась «принято / (принято + 64 КБ × непочатые)», а 64 КБ
        /// против настоящей медианы в четверть мегабайта — занижение на
        /// порядок. Кольцо от этого уходило почти в полное на первых процентах
        /// и там стояло: при реальных 5 % оно показывало 36 %, при 26 % — 72 %
        /// (замер qa/download-progress-check.sh). Размеры лежат в манифесте
        /// рядом с адресами и раньше просто терялись по дороге сюда.</para></summary>
        public long Size;

        /// <summary>
        /// ЗАПИСЬ ОБОЗА ИЗ ЧАСТИ КОНТЕНТА — с той ступенью, которую покажут.
        ///
        /// <para>Обоз качает ровно тот адрес, что ему дали: ни ступени
        /// качества, ни кода он не подставляет — это правило
        /// <see cref="DownloadPolicy.Effective"/>, и центр загрузок
        /// («скачать всё», перекачка в новом качестве) через него проходит.
        /// Фоновый прогрев библиотеки клал в обоз адрес части КАК ЕСТЬ —
        /// тремя вызовами подряд (глава, героиня, запасной облик), — и тянул
        /// ИСХОДНИКИ крупного арта при любой настройке качества; показ потом
        /// просил «@1k» и шёл в сеть за той же картинкой снова.</para>
        ///
        /// <para>Ошибка была не в прогреве, а в форме: собрать запись из
        /// части можно было только руками, и третий вызывающий правило не
        /// вспомнил. Теперь запись собирается здесь, и вспоминать нечего.</para>
        /// </summary>
        public static PreloadItem Of(LvnPart part) => new PreloadItem
        {
            Url = DownloadPolicy.Effective(part.Kind, part.Url),
            Kind = part.Kind,
            Size = part.Size,
        };
    }

    internal static class TaskExtensions
    {
        /// <summary>Adds cancellation support to any Task. Wraps it in WhenAny with
        /// a CT-driven completion source so awaiting can throw on shutdown even if
        /// the underlying task ignores the token.</summary>
        public static async Task WithCancellation(this Task task, CancellationToken ct)
        {
            if (!ct.CanBeCanceled) { await task; return; }
            var tcs = new TaskCompletionSource<bool>();
            using (ct.Register(s => ((TaskCompletionSource<bool>)s).TrySetResult(true), tcs))
            {
                if (task != await Task.WhenAny(task, tcs.Task))
                    throw new OperationCanceledException(ct);
            }
            await task; // surface exceptions
        }
    }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
    internal sealed class AcceptAllCertificates : UnityEngine.Networking.CertificateHandler
    {
        protected override bool ValidateCertificate(byte[] certificateData) => true;
    }
#endif
}
