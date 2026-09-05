using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Lvn.Services
{
    /// <summary>
    /// Offline-first wallet over a server-authoritative ledger (the Liminal
    /// model). ONLINE: every call goes to the backend and mirrors its answer.
    /// OFFLINE: earns and spends apply to a PERSISTED local mirror and join a
    /// replay queue; the next moment the network exists (any Refresh — chapter
    /// entry, opening the shop/wardrobe) the queue replays FIFO onto the
    /// server, then the server's truth overwrites the mirror. A spend the
    /// server ultimately refuses (the device overspent while offline) is
    /// dropped on replay and the refresh corrects the balance — the server
    /// stays the single source of truth, the player just never loses an
    /// offline session's honest earnings. IAP/ad rewards stay online-only by
    /// nature (they need the store / the ad network anyway).
    /// </summary>
    public static class LvnWallet
    {
        /// <summary>Last known state (server truth when online; the persisted
        /// local mirror while offline).</summary>
        public static IReadOnlyDictionary<string, long> Balances { get { EnsureLoaded(); return _balances; } }
        public static IReadOnlyDictionary<string, long> Inventory { get { EnsureLoaded(); return _inventory; } }

        /// <summary>
        /// СКОЛЬКО У ИГРОКА ЭТОЙ ВАЛЮТЫ — ноль, если он её ещё не видел.
        ///
        /// <para>Вопрос кошелька, а не поиск по карте. «Валюты нет в карте»
        /// значит «ноль», и это РЕШЕНИЕ: можно было бы считать неизвестную
        /// валюту ошибкой автора. Написанное хвостом <c>? v : 0</c> по месту,
        /// оно повторялось у витрины гардероба и у функции сценария
        /// <c>balance()</c> — и следующий, кому понадобится баланс, написал бы
        /// хвост в третий раз, уже, может быть, с другим ответом.</para>
        /// </summary>
        public static long Balance(string currency)
        {
            if (string.IsNullOrEmpty(currency)) return 0;
            return Balances.TryGetValue(currency, out var v) ? v : 0;
        }

        /// <summary>
        /// ЕСТЬ ЛИ ВЕЩЬ У ИГРОКА СЕЙЧАС — «штук больше нуля», а не «ключ
        /// присутствует».
        ///
        /// <para>Инвентарь считает ШТУКИ, и потраченная вещь остаётся ключом с
        /// нулём. Это знали: в языковой функции <c>has_item</c> так и написано
        /// комментарием. А гардероб рядом спрашивал <c>ContainsKey</c> — и
        /// потраченная вещь выглядела в нём купленной.</para>
        ///
        /// <para>Отличать от «встречал когда-либо»: для коллекции ключ с нулём
        /// — законное свидетельство, что вещь была. Это ДРУГОЙ вопрос, и
        /// отвечать на него этим методом нельзя.</para>
        /// </summary>
        public static bool Has(string sku)
            => !string.IsNullOrEmpty(sku) && Inventory.TryGetValue(sku, out var n) && n > 0;
        private static Dictionary<string, long> _balances = new Dictionary<string, long>();
        private static Dictionary<string, long> _inventory = new Dictionary<string, long>();

        /// <summary>Refill state for regenerating currencies (energy/lives): the
        /// server's computed cap and next-refill timestamp, for a HUD countdown.
        /// Only present for currencies the server marks as regenerating.</summary>
        public static IReadOnlyDictionary<string, RegenInfo> Regen { get { EnsureLoaded(); return _regen; } }
        private static Dictionary<string, RegenInfo> _regen = new Dictionary<string, RegenInfo>();

        /// <summary>
        /// СКОЛЬКО ЖДАТЬ ДО ВОСПОЛНЕНИЯ — ОДИН ОТВЕТ И НЕ ПО ЧАСАМ УСТРОЙСТВА.
        ///
        /// <para>Сервер присылает абсолютную метку следующего начисления, и
        /// «сколько осталось» считалось вычитанием ЛОКАЛЬНОГО «сейчас» — в двух
        /// местах интерфейса, каждое своей строкой. Часы устройства при этом
        /// принадлежат игроку: замер 05.09 на живом сервере — при часах,
        /// переведённых на сутки вперёд, счётчик показывает «готово», а сервер
        /// на трату отвечает отказом; при отставших часах игрок ждёт лишнее,
        /// хотя энергия уже начислена.</para>
        ///
        /// <para>Поправку берём из ответа сервера (поле <c>now</c>), а бег
        /// времени между ответами — из МОНОТОННЫХ часов приложения, чтобы
        /// перевод системных часов прямо во время игры тоже ничего не сдвинул.
        /// Нет поля (старый сервер) — честно падаем на прежнее поведение.</para>
        /// </summary>
        public static long SecondsUntilRefill(string currency)
        {
            if (!Regen.TryGetValue(currency ?? "", out var r) || r.NextRefillUnix <= 0) return 0;
            long left = r.NextRefillUnix - ServerNowUnix();
            return left > 0 ? left : 0;
        }

        /// <summary>Текущее время СЕРВЕРА, как его видит клиент.</summary>
        public static long ServerNowUnix()
        {
            if (_serverNow <= 0) return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return _serverNow + (long)(Lvn.LvnClock.Wall() - _serverNowAt);
        }

        private static long _serverNow;      // «сейчас» из последнего ответа
        private static float _serverNowAt;   // монотонная отметка того же мига

        /// <summary>Забыть поправку — для испытаний, где проверяется поведение
        /// клиента, НИКОГДА не видевшего серверного времени (старый сервер).
        /// В живой игре поправка живёт до конца процесса: часы серверов между
        /// собой согласованы, и терять её незачем.</summary>
        internal static void ForgetServerClock() { _serverNow = 0; _serverNowAt = 0f; }

        private static void NoteServerNow(long unix)
        {
            if (unix <= 0) return;
            _serverNow = unix;
            _serverNowAt = Lvn.LvnClock.Wall();
        }

        /// <summary>A regenerating currency's live refill state (from /v1/wallet's
        /// computed <c>regen</c> block).</summary>
        public struct RegenInfo
        {
            public long Balance;
            public long Cap;
            public long IntervalSeconds;
            public long NextRefillUnix; // 0 when at/above the cap
            /// <summary>At/above the free cap — no refill pending.</summary>
            public bool Full => NextRefillUnix <= 0 || Balance >= Cap;
        }

        /// <summary>
        /// КАК ПОКАЗАТЬ БАЛАНС ИГРОКУ. Обычная валюта — просто число с
        /// разрядами; восполняемая (энергия) — «есть/предел», пока она ниже
        /// потолка: игроку важно не сколько у него энергии, а сколько до
        /// полного бака.
        ///
        /// <para>Правило простое, и именно поэтому оно было написано четырьмя
        /// разными способами в четырёх экранах — строка кошелька выглядела
        /// чуть по-разному в зависимости от того, где на неё смотреть.</para>
        /// </summary>
        public static string Display(string currency)
        {
            long amount = Balances.TryGetValue(currency ?? "", out var b) ? b : 0;
            return BelowCap(currency)
                ? amount + "/" + Regen[currency ?? ""].Cap
                : Lvn.UI.LvnPriceTag.Amount(amount);   // через Ценник: сборка сервисов
                  // видит интерфейс, но не модель контента — а «как показать
                  // сумму» и есть работа Ценника.
        }

        /// <summary>
        /// ВАЛЮТА КОПИТСЯ — то есть у неё есть потолок и до него не добрали.
        ///
        /// <para>Один вопрос, два ответчика: показ суммы решал его у себя
        /// («3/5» вместо «3»), а пилюля кошелька — у себя, чтобы понять, вешать
        /// ли таймер до следующего начисления. Разойдись эти два места на строгом
        /// и нестрогом сравнении — и на полном кошельке рядом с «5/5» тикал бы
        /// счётчик до пополнения, которого не будет.</para>
        /// </summary>
        public static bool BelowCap(string currency)
        {
            var key = currency ?? "";
            return Regen.TryGetValue(key, out var r) && r.Cap > 0
                   && (Balances.TryGetValue(key, out var b) ? b : 0) < r.Cap;
        }

        /// <summary>Raised whenever the mirrored state changes.</summary>
        public static event Action Changed;

        /// <summary>Queued offline operations waiting for the network.</summary>
        public static int PendingCount { get { EnsureLoaded(); return _queue.Count; } }

        private const string PMirror = "lvn.wallet.mirror";
        private const string PQueue = "lvn.wallet.queue";
        private const string POwner = "lvn.wallet.owner";
        private static readonly List<JObject> _queue = new List<JObject>();
        private static bool _loaded;
        // ИДУЩАЯ ОТПРАВКА ОЧЕРЕДИ — не флаг, а сама работа.
        //
        // Флагом второй вызов узнавал, что отправка уже идёт, и... возвращался,
        // не дождавшись её. Своё «жду очередь» он тем самым выполнял за ноль
        // секунд и слал собственный запрос вперёд неё. Порядок, объявленный
        // строкой «FIFO holds even mid-chapter», держался только пока вызовы шли
        // по одному.
        private static Task _flush;

        /// <summary>Bind the local mirror/queue to an account. Called by
        /// LvnBackend whenever a sign-in lands: if the device switched to a
        /// DIFFERENT account (cross-device recovery via Google/Apple), the
        /// previous user's offline mirror and queued ops are dropped — they
        /// must never replay into someone else's wallet.</summary>
        internal static void NoteUser(string userId)
        {
            if (string.IsNullOrEmpty(userId)) return;
            var prev = LvnKeep.Get(POwner, "");
            if (prev == userId) return;
            if (!string.IsNullOrEmpty(prev))
            {
                LvnLog.Info($"[lvn-wallet] account switched ({prev} → {userId}) — local mirror and {_queue.Count} queued op(s) discarded");
                ResetLocal();
                Changed?.Invoke();
            }
            LvnKeep.Put(POwner, userId);
        }

        public static async Task<bool> RefreshAsync()
        {
            EnsureLoaded();
            await FlushAsync(); // offline earnings land BEFORE we read the truth
            _lastAsk = Lvn.LvnClock.Wall();
            var (code, body) = await LvnBackend.GetAsync("/v1/wallet");
            return LvnBackend.Ok(code) && Apply(body);
        }

        /// <summary>
        /// ОСВЕЖИТЬ ПРИ СЛУЧАЕ — но не чаще, чем имеет смысл.
        ///
        /// <para>Два разных повода спросить сервер путались в одном вызове.
        /// Первый — «игрок что-то сделал»: купил, получил награду, нажал
        /// «Восстановить». Такой запрос обязан уйти сразу, и его делает
        /// <see cref="RefreshAsync"/>. Второй — «экран показался»: открылся хаб,
        /// открылся гардероб, подошло время восполнения энергии. Их много, они
        /// случаются подряд, и каждый шёл на сервер отдельным запросом.</para>
        ///
        /// <para>Паузу между фоновыми запросами держала ПИЛЮЛЯ КОШЕЛЬКА —
        /// статическим полем внутри виджета. То есть правило «как часто
        /// спрашивать сервер о деньгах» жило в подписи на экране: нет пилюли —
        /// нет и правила, а хаб с гардеробом о нём и не знали вовсе.</para>
        ///
        /// <para>Время РЕАЛЬНОЕ, а не игровое: пауза — про сеть, а не про
        /// экран. Энергия восполняется, пока игра свёрнута, и отсчёт обязан
        /// идти там же.</para>
        /// </summary>
        public static Task NudgeAsync(float minGapSeconds = 15f)
        {
            float now = Lvn.LvnClock.Wall();
            if (now - _lastAsk < minGapSeconds) return Task.CompletedTask;
            _lastAsk = now;
            return RefreshAsync();
        }

        // Когда последний раз спрашивали сервер (реальные секунды с запуска).
        // Отрицательное начальное — первый же повод спрашивает сразу.
        private static float _lastAsk = -1000f;

        /// <summary>Server-side earn; offline it lands in the local mirror and
        /// the replay queue (still true — the earning is honest and durable).</summary>
        public static Task<bool> EarnAsync(string currency, long amount, string reason)
            => RunOpAsync("/v1/wallet/earn", "earn", currency, amount, reason, offlineNeedsBalance: false);

        /// <summary>Spend; false when refused (insufficient funds). Offline the
        /// PERSISTED local balance gates the spend, the op queues for replay —
        /// the wardrobe keeps working on a plane. Optional sku is granted into
        /// the inventory atomically.</summary>
        public static Task<bool> SpendAsync(string currency, long amount, string reason, string sku = null)
            => RunOpAsync("/v1/wallet/spend", "spend", currency, amount, reason, offlineNeedsBalance: true,
                          extra: p => { if (!string.IsNullOrEmpty(sku)) p["sku"] = sku; });

        /// <summary>ОДНА ОПЕРАЦИЯ КОШЕЛЬКА — порядок и три исхода, общие для всех.
        ///
        /// <para>Начисление и трата держали это тело каждое своей копией:
        /// девять строк из одиннадцати совпадали. В коде про деньги такая копия
        /// опаснее прочих — заведут третью операцию (перевод, возврат),
        /// скопируют ближайшую, и полугодовая правка про повторы уедет только в
        /// одну из трёх.</para>
        ///
        /// <para><b>Порядок.</b> Сначала очередь: накопленное офлайн обязано
        /// лечь ДО того, как сервер судит новую операцию, — иначе трата
        /// упрётся в баланс, который ещё не доехал. Очередь строго по времени,
        /// и середина главы её не откладывает.</para>
        ///
        /// <para><b>Метка операции рождается ВМЕСТЕ с ней</b> и едет с каждым
        /// повтором: сервер применяет ровно один раз, даже когда ответ потерян
        /// по дороге.</para>
        ///
        /// <para><b>Три исхода.</b> Сервер ответил согласием — его слово и
        /// берём. Сервер ответил отказом (нехватка, запрет) — это НЕ офлайн,
        /// и спорить не о чем. Сервера не слышно — пишем в очередь, а
        /// зеркало баланса правим следом: запись долговечна, зеркало из неё
        /// выводится, и порядок здесь не украшение — так переживается
        /// выключение питания между двумя строками.</para>
        ///
        /// <para><b>Чем операции вправе отличаться</b> — ровно двумя вещами.
        /// Первая: нужен ли офлайн запас (тратить можно только то, что есть;
        /// зарабатывать — всегда). Вторая: что дописать в тело операции, вроде
        /// покупаемого предмета.</para>
        /// </summary>
        /// <para><b>Адрес приходит доводом ЦЕЛИКОМ, а не склеивается тут из
        /// кусков.</b> Склеенный я и написал сначала — и страж «клиент зовёт
        /// адрес, которого сервер не отдаёт» покраснел: он читает исходники и
        /// видит только литералы. Путь, собранный из частей, для него исчезает,
        /// и сверка клиента с сервером перестаёт работать молча. Лишнее слово в
        /// вызове — цена того, что адрес остаётся видимым.</para>
        private static async Task<bool> RunOpAsync(string endpoint, string op, string currency,
                                                   long amount, string reason, bool offlineNeedsBalance,
                                                   System.Action<JObject> extra = null)
        {
            EnsureLoaded();
            await FlushAsync();
            var payload = new JObject { ["op"] = op, ["currency"] = currency, ["amount"] = amount,
                ["reason"] = reason, ["op_id"] = Lvn.LvnMark.Once() };
            extra?.Invoke(payload);
            var (code, body) = await LvnBackend.PostAsync(endpoint, payload.ToString());
            if (LvnBackend.Ok(code)) return Apply(body);
            if (code != 0) return false;
            if (offlineNeedsBalance && !CanApplyLocal(payload)) return false;
            Enqueue(payload);
            ApplyLocal(payload);
            return true;
        }

        /// <summary>Replay the offline queue FIFO onto the server. Called from
        /// every Refresh; safe to call any time. Stops at the first transport
        /// failure (still offline) and keeps the rest queued. A server 4xx
        /// (e.g. the overdraft finally caught) DROPS the op — truth wins.</summary>
        public static Task FlushAsync()
        {
            EnsureLoaded();
            // Отправка уже идёт — ЖДЁМ ЕЁ, а не проскакиваем мимо. Иначе спенд,
            // пущенный следом за возвратом сети, обгонял ещё не долетевший
            // офлайновый заработок, и сервер честно отказывал «не хватает» —
            // при достаточном балансе. Игроку это видно как отказ покупки сразу
            // после возвращения в сеть, который лечится повторным тапом.
            if (_flush != null && !_flush.IsCompleted) return _flush;
            if (_queue.Count == 0) return Task.CompletedTask;
            _flush = FlushQueueAsync();
            return _flush;
        }

        private static async Task FlushQueueAsync()
        {
            try
            {
                while (_queue.Count > 0)
                {
                    var op = _queue[0];
                    var path = (string)op["op"] == "earn" ? "/v1/wallet/earn" : "/v1/wallet/spend";
                    var body = new JObject(op);
                    body.Remove("op");
                    var (code, resp) = await LvnBackend.PostAsync(path, body.ToString());
                    if (LvnBackend.Offline(code)) return; // still offline — keep the queue
                    _queue.RemoveAt(0);
                    PersistQueue();
                    if (LvnBackend.Ok(code)) Apply(resp);
                    else UnityEngine.Debug.LogWarning(
                        $"[lvn-wallet] queued {op["op"]} {op["currency"]} {op["amount"]} rejected on sync ({code}) — server truth wins");
                }
            }
            finally { _flush = null; }
        }

        /// <summary>One purchasable pack from the server's IAP catalog — the
        /// store screen's card. Amount is the full grant (bonus included);
        /// Price is a display string, billing happens in the platform store.</summary>
        public sealed class IapPack
        {
            public string Sku;
            public string Currency;
            public long Amount;
            public string Title;   // optional; "" → the screen composes one
            public string Price;   // optional display price ("$4.99")
            public string Icon;    // optional content url
            public long Bonus;     // optional "+N bonus" share of Amount
            public string Section; // optional store section id ("currency1"/"currency2"/"bundles")
            // Набор: несколько валют одним паком (currency → amount). Сервер
            // начисляет по Grants, когда они есть; Currency/Amount — заголовок.
            public Dictionary<string, long> Grants;
        }

        /// <summary>The purchasable packs (GET /v1/iap/catalog, server-sorted).
        /// Null offline / when no server is configured.</summary>
        public static async Task<List<IapPack>> GetCatalogAsync()
        {
            var (code, body) = await LvnBackend.GetAsync("/v1/iap/catalog");
            return LvnBackend.Ok(code) ? ParseCatalog(body) : null;
        }

        /// <summary>Parse a /v1/iap/catalog response; null on garbage.</summary>
        public static List<IapPack> ParseCatalog(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var packs = new List<IapPack>();
                foreach (var t in JObject.Parse(json)["packs"] as JArray ?? new JArray())
                {
                    if (!(t is JObject o) || string.IsNullOrEmpty((string)o["sku"])) continue;
                    packs.Add(new IapPack
                    {
                        Sku = (string)o["sku"],
                        Currency = (string)o["currency"] ?? "",
                        Amount = (long?)o["amount"] ?? 0,
                        Title = (string)o["title"] ?? "",
                        Price = (string)o["price"] ?? "",
                        Icon = (string)o["icon"] ?? "",
                        Bonus = (long?)o["bonus"] ?? 0,
                        Section = (string)o["section"] ?? "",
                        Grants = ParseGrants(o["grants"] as JObject),
                    });
                }
                return packs;
            }
            catch { return null; }
        }

        private static Dictionary<string, long> ParseGrants(JObject g)
        {
            if (g == null) return null;
            var map = new Dictionary<string, long>();
            foreach (var kv in g)
                if (!string.IsNullOrEmpty(kv.Key)) map[kv.Key] = (long?)kv.Value ?? 0;
            return map.Count > 0 ? map : null;
        }

        /// <summary>Redeem a store purchase. The server validates the receipt
        /// (dev mode: -iap-dev) and grants from its catalog — the client never
        /// decides amounts.</summary>
        public static async Task<bool> VerifyPurchaseAsync(string platform, string sku, string receipt)
        {
            var (code, body) = await LvnBackend.PostAsync("/v1/iap/verify",
                new JObject { ["platform"] = platform, ["sku"] = sku, ["receipt"] = receipt }.ToString());
            return LvnBackend.Ok(code) && Apply(body);
        }

        internal static bool Apply(string json)
        {
            if (string.IsNullOrEmpty(json)) return false;
            EnsureLoaded(); // else a later first-read would resurrect the stale prefs mirror
            try
            {
                var doc = JObject.Parse(json);
                _balances = ToMap(doc["balances"] as JObject);
                _inventory = ToMap(doc["inventory"] as JObject);
                _regen = ParseRegen(doc["regen"] as JObject);
                NoteServerNow((long?)doc["now"] ?? 0);
                PersistMirror();
                Changed?.Invoke();
                return true;
            }
            catch { return false; }
        }

        // ── the offline half: persisted mirror + replay queue ───────────────

        /// <summary>Would this op fit the local mirror? (spend needs funds).
        /// Pure — tests drive it directly.</summary>
        internal static bool CanApplyLocal(JObject op)
        {
            if ((string)op["op"] != "spend") return true;
            var cur = (string)op["currency"] ?? "";
            _balances.TryGetValue(cur, out var have);
            return have >= ((long?)op["amount"] ?? 0);
        }

        /// <summary>Apply an op to the local mirror and persist it. Pure state
        /// change (no network) — the offline path and tests share it.</summary>
        internal static void ApplyLocal(JObject op)
        {
            var cur = (string)op["currency"] ?? "";
            long amount = (long?)op["amount"] ?? 0;
            _balances.TryGetValue(cur, out var have);
            _balances[cur] = (string)op["op"] == "earn" ? have + amount : have - amount;
            var sku = (string)op["sku"];
            if ((string)op["op"] == "spend" && !string.IsNullOrEmpty(sku))
            {
                _inventory.TryGetValue(sku, out var n);
                _inventory[sku] = n + 1;
            }
            PersistMirror();
            Changed?.Invoke();
        }

        private static void Enqueue(JObject op)
        {
            _queue.Add(op);
            PersistQueue();
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var mirror = LvnKeep.Get(PMirror, "");
                if (!string.IsNullOrEmpty(mirror))
                {
                    var doc = JObject.Parse(mirror);
                    _balances = ToMap(doc["balances"] as JObject);
                    _inventory = ToMap(doc["inventory"] as JObject);
                }
                var q = LvnKeep.Get(PQueue, "");
                if (!string.IsNullOrEmpty(q))
                    foreach (var t in JArray.Parse(q))
                        if (t is JObject o) _queue.Add(o);
            }
            catch { /* corrupt prefs → clean start; the next Refresh restores truth */ }
        }

        private static void PersistMirror()
        {
            var doc = new JObject { ["balances"] = ToJObject(_balances), ["inventory"] = ToJObject(_inventory) };
            LvnKeep.Put(PMirror, doc.ToString(Newtonsoft.Json.Formatting.None));
        }

        private static void PersistQueue()
        {
            LvnKeep.Put(PQueue, new JArray(_queue).ToString(Newtonsoft.Json.Formatting.None));
        }

        /// <summary>Полное локальное забвение при удалении аккаунта: зеркало,
        /// очередь И владелец. NoteUser("") здесь не годится — он намеренно
        /// игнорирует пустой uid.</summary>
        internal static void ForgetLocal()
        {
            ResetLocal();
            LvnKeep.Drop(POwner);
            Changed?.Invoke();
        }

        /// <summary>Забыть кошелёк ВМЕСТЕ С ХОЗЯИНОМ. ResetLocal чистит
        /// зеркало и очередь, но метка владельца оставалась: обряд забвения
        /// рапортовал об успехе, а на диске лежал баланс и чужой id — до
        /// первого входа под новым.</summary>
        public static void Forget()
        {
            ResetLocal();
            try { LvnKeep.Drop(POwner); } catch { /* уже нечего */ }
            Changed?.Invoke();
        }

        /// <summary>Перечитать кошелёк С ДИСКА — ровно так, как это делает
        /// свежий запуск игры. Нужно проверкам, которые спрашивают «а пережила
        /// ли очередь закрытие приложения»: без этого они спрашивали бы у
        /// оперативной памяти, которая ничего не теряла и в дефектном
        /// случае.</summary>
        internal static void ReloadLocal()
        {
            _balances = new Dictionary<string, long>();
            _inventory = new Dictionary<string, long>();
            _regen = new Dictionary<string, RegenInfo>();
            _queue.Clear();
            _loaded = false;
            EnsureLoaded();
        }

        internal static void ResetLocal()
        {
            _balances = new Dictionary<string, long>();
            _inventory = new Dictionary<string, long>();
            _regen = new Dictionary<string, RegenInfo>();
            _queue.Clear();
            _loaded = true;
            using (LvnKeep.Batch())
            {
                LvnKeep.Drop(PMirror);
                LvnKeep.Drop(PQueue);
            }
        }

        private static JObject ToJObject(Dictionary<string, long> map)
        {
            var o = new JObject();
            foreach (var kv in map) o[kv.Key] = kv.Value;
            return o;
        }

        private static Dictionary<string, long> ToMap(JObject o)
        {
            var map = new Dictionary<string, long>();
            if (o != null)
                foreach (var kv in o)
                    map[kv.Key] = (long?)kv.Value ?? 0;
            return map;
        }

        private static Dictionary<string, RegenInfo> ParseRegen(JObject o)
        {
            var map = new Dictionary<string, RegenInfo>();
            if (o != null)
                foreach (var p in o.Properties())
                    if (p.Value is JObject r)
                        map[p.Name] = new RegenInfo
                        {
                            Balance = (long?)r["balance"] ?? 0,
                            Cap = (long?)r["cap"] ?? 0,
                            IntervalSeconds = (long?)r["interval_seconds"] ?? 0,
                            NextRefillUnix = (long?)r["next_refill_unix"] ?? 0,
                        };
            return map;
        }
    }
}
