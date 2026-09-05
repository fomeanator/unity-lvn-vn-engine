using System;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace Lvn.Services
{
    /// <summary>
    /// The device-account session against the LVN product services (auth /
    /// wallet / analytics). Anonymous, mobile-style: a random device secret is
    /// minted once and kept in PlayerPrefs; <see cref="EnsureRegisteredAsync"/>
    /// exchanges it for a bearer token (idempotent — the same device always
    /// gets the same account back, e.g. after a reinstall-with-backup).
    /// Everything is optional: a game that never calls this plays fully
    /// offline, exactly as before.
    /// </summary>
    public static class LvnBackend
    {
        private const string PDevice = "lvn.svc.device";
        private const string PToken = "lvn.svc.token";
        private const string PUser = "lvn.svc.user";
        private const string PName = "lvn.svc.name";

        /// <summary>Server base url, e.g. "http://127.0.0.1:8077". The host sets
        /// it once at boot (NovelApp's ServerUrl is the usual source).</summary>
        public static string BaseUrl = "";

        public static string UserId => LvnKeep.Get(PUser, "");
        public static string Token => LvnKeep.Get(PToken, "");
        public static bool SignedIn => !string.IsNullOrEmpty(Token);

        /// <summary>Raised after a successful (re-)registration. ШОВ: внутри
        /// движка на него никто не подписан — это дверь встраивающей игре,
        /// которой надо знать про вход и выход.</summary>
        public static event Action<string> SignedInChanged;

        /// <summary>
        /// Вход при запуске: подтвердить свой пропуск, а если сервер его не
        /// признал — представиться устройством. Безопасно звать каждый старт,
        /// офлайн ничего не трогает.
        ///
        /// <para>РАНЬШЕ ЗДЕСЬ БЫЛА ТОЛЬКО РЕГИСТРАЦИЯ УСТРОЙСТВОМ, и это
        /// отменяло вход игрока при каждом запуске. Замер 06.09 на живом
        /// сервере: устройство u_9eeb…, игрок вошёл своим аккаунтом u_fb58…,
        /// перезапуск — снова u_9eeb…. То есть «войти через Google» ради своих
        /// покупок и облачного прогресса работало ровно до закрытия игры, а
        /// потом человек молча оказывался в чужой пустой учётке.</para>
        ///
        /// <para>Правило: есть пропуск — спрашиваем сервер, он ли ещё нас
        /// узнаёт (<c>GET /v1/auth/me</c>). Узнаёт — не трогаем НИЧЕГО.
        /// Не узнаёт (401 после разворота базы, отзыва, чистого сервера) —
        /// представляемся устройством, как при первом запуске. Сервер молчит
        /// (офлайн) — тоже не трогаем: пропуск на устройстве всё ещё наш.</para>
        /// </summary>
        public static async Task<bool> EnsureRegisteredAsync()
        {
            if (string.IsNullOrEmpty(BaseUrl)) return SignedIn;
            if (SignedIn)
            {
                var (mine, _) = await GetAsync("/v1/auth/me");
                if (Ok(mine))
                {
                    // Сервер узнаёт этот пропуск — он и есть вход. Владельца
                    // локальных данных подтверждаем на случай, если процесс
                    // только что запустился и о нём ещё никто не объявлял.
                    Lvn.LvnKeep.NoteOwner(UserId);
                    LvnWallet.NoteUser(UserId);
                    return true;
                }
                if (mine == 0) return SignedIn; // сети нет — пропуск остаётся нашим
            }
            // Метка устройства — у ПАСПОРТИСТА: её потеря регистрирует НОВУЮ
            // учётку, то есть отнимает кошелёк и покупки, поэтому дома два.
            var device = LvnMark.Steady(PDevice);
            var body = JsonUtility.ToJson(new RegisterReq { device_id = device });
            var (code, json) = await PostAsync("/v1/auth/register", body, auth: false);
            if (!Ok(code) || string.IsNullOrEmpty(json)) return SignedIn;
            var resp = JsonUtility.FromJson<RegisterResp>(json);
            if (string.IsNullOrEmpty(resp?.token)) return SignedIn;
            using (LvnKeep.Batch())
            {
                LvnKeep.Put(PToken, resp.token);
                LvnKeep.Put(PUser, resp.user_id);
            }
            LvnWallet.NoteUser(resp.user_id); // bind (or reset) the offline wallet to this account
            // ВХОД УСТРОЙСТВОМ — ЭТО ТОТ ЖЕ ЧЕЛОВЕК. Метка устройства не
            // менялась; если номер игрока стал другим, то не потому, что
            // телефон сменил хозяина, а потому что сервер потерял свою базу.
            // Прохождение на устройстве остаётся его — см. LvnKeep.InheritOwner.
            Lvn.LvnKeep.InheritOwner(resp.user_id);
            SignedInChanged?.Invoke(resp.user_id);
            return true;
        }

        /// <summary>
        /// ВЫЙТИ ИЗ УЧЁТКИ — осознанно и явно: пропуск игрока забывается, игра
        /// возвращается к учётке устройства. До 06.09 выхода не было вовсе, и
        /// его роль случайно исполняла регистрация при старте — то есть выход
        /// происходил сам, без спроса, у каждого вошедшего.
        ///
        /// <para>Метку устройства не трогаем: она — паспорт телефона, а не
        /// игрока, и её потеря завела бы новую пустую учётку («удалить
        /// аккаунт» — другая дверь, см. <see cref="DeleteAccountAsync"/>).
        /// Локальные данные тоже остаются на месте: их владельца объявит
        /// регистрация устройства, и хозяин телефона застанет своё.</para>
        /// </summary>
        public static async Task<bool> SignOutAsync()
        {
            using (LvnKeep.Batch())
            {
                LvnKeep.Drop(PToken);
                LvnKeep.Drop(PUser);
            }
            Lvn.LvnKeep.NoteOwner("");
            SignedInChanged?.Invoke("");
            return await EnsureRegisteredAsync();
        }

        [Serializable] private class RegisterReq { public string device_id; }
        [Serializable] private class RegisterResp { public string user_id; public string token; }

        /// <summary>The profile display name — local-first (kept in PlayerPrefs
        /// even offline), synced to the account when a server is reachable.</summary>
        public static string DisplayName => LvnKeep.Get(PName, "");

        /// <summary>Save the display name locally and push it to the account
        /// (POST /v1/auth/profile). Offline the local copy still sticks — the
        /// next successful call syncs it.</summary>
        public static async Task<bool> SetDisplayNameAsync(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return false;
            LvnKeep.Put(PName, name);
            var (code, _) = await PostAsync("/v1/auth/profile", JsonUtility.ToJson(new ProfileReq { name = name }));
            return Ok(code);
        }

        [Serializable] private class ProfileReq { public string name; }

        /// <summary>Sign in with a verified platform identity (POST
        /// /v1/auth/login) — cross-device recovery: a known identity returns
        /// its account and this device switches to it (token + user id are
        /// replaced); an unknown identity gets a fresh account.</summary>
        public static async Task<bool> LoginWithProviderAsync(string provider, string token)
        {
            var body = JsonUtility.ToJson(new ProviderReq { provider = provider, token = token });
            var (code, json) = await PostAsync("/v1/auth/login", body, auth: false);
            if (!Ok(code) || string.IsNullOrEmpty(json)) return false;
            var resp = JsonUtility.FromJson<LoginResp>(json);
            if (string.IsNullOrEmpty(resp?.token)) return false;
            using (LvnKeep.Batch())
            {
                LvnKeep.Put(PToken, resp.token);
                LvnKeep.Put(PUser, resp.user_id);
                if (!string.IsNullOrEmpty(resp.name)) LvnKeep.Put(PName, resp.name);
            }
            // Cross-device recovery may have switched ACCOUNTS on this device —
            // the previous user's offline wallet must not leak into this one.
            LvnWallet.NoteUser(resp.user_id);
            Lvn.LvnKeep.NoteOwner(resp.user_id); // чужие сейвы и «прочитано» этому игроку не принадлежат
            SignedInChanged?.Invoke(resp.user_id);
            return true;
        }

        /// <summary>Attach a platform identity to the current account (POST
        /// /v1/auth/link) so it becomes recoverable from any device.</summary>
        public static async Task<LvnPlatformAuth.LinkResult> LinkProviderAsync(string provider, string token)
        {
            var body = JsonUtility.ToJson(new ProviderReq { provider = provider, token = token });
            var (code, _) = await PostAsync("/v1/auth/link", body);
            if (Ok(code)) return LvnPlatformAuth.LinkResult.Linked;
            if (code == 409) return LvnPlatformAuth.LinkResult.Conflict;
            return LvnPlatformAuth.LinkResult.Failed;
        }

        [Serializable] private class ProviderReq { public string provider; public string token; }
        [Serializable] private class LoginResp { public string user_id; public string token; public string name; }

        /// <summary>«Удалить аккаунт» (стор-требование): сервер стирает учётку,
        /// кошелёк и сейвы; локально сбрасываются токен, имя И device-секрет —
        /// иначе следующий /v1/auth/register с тем же device_id завёл бы
        /// «тот же» аккаунт заново, а игрок просил забыть его совсем.
        /// false = сервер недоступен или отказал; локально ничего не трогаем.</summary>
        public static async Task<bool> DeleteAccountAsync()
        {
            var (code, _) = await PostAsync("/v1/account/delete", "{\"confirm\":\"DELETE\"}");
            if (!Ok(code)) return false;
            using (LvnKeep.Batch())
            {
                LvnKeep.Drop(PToken);
                LvnKeep.Drop(PUser);
                LvnKeep.Drop(PName);
                LvnKeep.Drop(PDevice);
            }
            LvnWallet.ForgetLocal(); // офлайн-кошелёк не должен пережить владельца
            // И ВСЁ ОСТАЛЬНОЕ, ЧТО ЛЕЖИТ НА ТЕЛЕФОНЕ. Сервер стирает свою
            // половину (учётка, кошелёк, журнал, рекорды, сейвы-блобы), но
            // сохранения, прогресс глав, галерея и «прочитано» живут здесь.
            // Замер 05.09: после удаления они оставались — «забудьте меня»
            // исполнялось наполовину, и следующий человек с этим телефоном
            // открывал чужую историю. Настройки устройства (язык, громкость,
            // размер шрифта) не трогаем: они про телефон, а не про игрока.
            Lvn.LvnKeep.ForgetPlayerData();
            Lvn.LvnKeep.NoteOwner("");
            SignedInChanged?.Invoke("");
            return true;
        }

        /// <summary>УДАЧНЫЙ ОТВЕТ — одно правило на всех: весь второй разряд,
        /// а не «ровно 200». Дом правила сам же его и нарушал в шести местах, и
        /// это не мелочь: сервер вправе ответить 201 или 204 (а прокси —
        /// нормализовать), и тогда удача читалась бы как отказ. Дороже всего
        /// это стоило бы привязке аккаунта: «уже привязан» вернулось бы игроку
        /// как «не вышло».</summary>
        public static bool Ok(long code) => code >= 200 && code < 300;

        /// <summary>Запрос не дошёл вовсе: сети нет, сервер не отвечает, DNS
        /// молчит. Не то же самое, что отказ сервера — см. <see cref="Ok"/>.</summary>
        public static bool Offline(long code) => code == 0;

        /// <summary>
        /// ОТВЕТ, ПРИГОДНЫЙ К ЧТЕНИЮ, — или ничего.
        ///
        /// <para>Ответ читают, только если он ВЕСЬ в порядке: код успешный,
        /// тело есть и это разбираемый JSON. Разнобой хоть в одном из трёх
        /// разбирают уже как данные — и падают на пустой строке или молча берут
        /// поля из тела ошибки.</para>
        ///
        /// <para>Три проверки стояли врозь в пяти службах: код с телом в одном
        /// условии, разбор — в собственном try у каждой. Здесь они одно
        /// действие: null значит «читать нечего», и звонящий возвращает свой
        /// пустой ответ.</para>
        /// </summary>
        public static JObject Json(long code, string body)
        {
            if (!Ok(code) || string.IsNullOrEmpty(body)) return null;
            try { return JObject.Parse(body); } catch { return null; }
        }

        /// <summary>POST json; returns (status, body). 0 = transport error
        /// (offline). Attaches the bearer token unless auth=false.</summary>
        public static Task<(long code, string body)> PostAsync(string path, string json, bool auth = true)
            => SendAsync("POST", path, json, auth);

        /// <summary>
        /// ОДИН ЗАПРОС НА ВСЕ СЛУЖБЫ: адрес, токен, терпение, ожидание ответа и
        /// правило «транспорт не дошёл» (код 0).
        ///
        /// <para>Тел было два — почти одинаковых, отличавшихся глаголом и телом
        /// письма. Разошлись они уже: заголовок авторизации POST ставил по
        /// параметру, GET — всегда; добавить общий заголовок или заменить
        /// правило отказа значило бы вспомнить про оба.</para>
        /// </summary>
        private static async Task<(long code, string body)> SendAsync(string method, string path, string json, bool auth)
        {
            if (string.IsNullOrEmpty(BaseUrl)) return (0, null);
            using var req = new UnityWebRequest(BaseUrl + path, method);
            if (json != null || method == "POST")
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json ?? "{}"));
                req.SetRequestHeader("Content-Type", "application/json");
            }
            req.downloadHandler = new DownloadHandlerBuffer();
            if (auth && SignedIn) req.SetRequestHeader("Authorization", "Bearer " + Token);
            req.timeout = Lvn.LvnNetPatience.RequestSeconds;
            // ПО СОБЫТИЮ, А НЕ ОПРОСОМ. Запрос службы короткий, прогресса у
            // него никто не показывает — каждый оборот «пока не готово —
            // уступи кадр» это потраченный кадр. Дом ожидания жил в сборке
            // контента, которой службы не видят; после переезда в ядро он им
            // доступен, и своя копия цикла больше не нужна.
            var op = req.SendWebRequest();
            await Lvn.LvnNetWait.CompletedAsync(req, op, default);
            bool reached = req.result == UnityWebRequest.Result.Success || req.responseCode != 0;
            // СВЯЗЬ — ФАКТ, А НЕ МНЕНИЕ, и знает его тот, кто только что ходил
            // на сервер. Продуктовые службы ходят на ТОТ ЖЕ адрес, что и
            // контент, значит их ответ говорит о связи ровно то же. Раньше
            // здесь стоял ШОВ — делегат, который ставила оболочка, потому что
            // дом признака жил в чужой сборке; не поставлен — службы молчали.
            // Дом переехал в ядро, и шов вместе с кварталом ушёл.
            var why = "services " + method + " " + path;
            if (reached) Lvn.LvnNetworkStatus.MarkOnline(why);
            else Lvn.LvnNetworkStatus.MarkOffline(why);
            if (!reached) return (0, null);
            return (req.responseCode, req.downloadHandler.text);
        }

        [Serializable] private class MeResp { public string user_id; public string[] providers; }

        /// <summary>The platform providers this account is linked to
        /// (<c>"google"</c>, <c>"apple"</c>); empty for a device-only account,
        /// null when offline. The settings screen shows "signed in via …" from
        /// this (GET /v1/auth/me).</summary>
        public static async Task<string[]> GetProvidersAsync()
        {
            var (code, json) = await GetAsync("/v1/auth/me");
            if (!Ok(code) || string.IsNullOrEmpty(json)) return null;
            try { return JsonUtility.FromJson<MeResp>(json)?.providers ?? Array.Empty<string>(); }
            catch { return Array.Empty<string>(); }
        }

        /// <summary>GET json with the bearer token; same contract as PostAsync.</summary>
        public static Task<(long code, string body)> GetAsync(string path)
            => SendAsync("GET", path, null, auth: true);
    }
}
