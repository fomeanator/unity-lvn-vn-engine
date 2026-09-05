using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lvn.Content;
using UnityEngine;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// ИГРОК — часть <see cref="NovelApp"/>: кто он для сервера, что показывает
    /// его профиль (отношения, кошелёк, пройденное) и как он уходит навсегда.
    ///
    /// <para>Данные для профиля собираются из трёх мест сразу — статы новелл,
    /// кошелёк, прогресс, — и это единственная причина, по которой экран
    /// профиля вообще что-то знает о новеллах.</para>
    /// </summary>
    public sealed partial class NovelApp
    {
        // The debug faucet's grant: credit the wallet (EarnAsync fires
        // LvnWallet.Changed — the shell's HUD pill updates itself) and
        // reconcile with the server so the balance survives restarts.
        private async Task GrantFaucetAsync(string currency, int amount)
        {
            if (!await Lvn.Services.LvnWallet.EarnAsync(currency, amount, "debug_faucet"))
                // Кран нужен для проверки экономики: молча не начислить —
                // значит подсунуть проверяющему ложный результат опыта.
                Debug.LogWarning($"[lvn] отладочный кран не начислил {amount} {currency} — " +
                                 "отказ сервера или нет сети; баланс остался прежним.");
            await Lvn.Services.LvnWallet.RefreshAsync();
        }

        // Имя метки игрока. Оно же имя файла-дублёра (lvn_user.id) — менять
        // нельзя: у живых игроков метка лежит именно под ним.
        private const string PlayerMark = "lvn_user";

        // The save identity for /v1/state. An explicit UserId (an account) wins; else
        // a per-device id generated once and kept in PlayerPrefs.
        private string ResolveUserId()
        {
            if (!string.IsNullOrEmpty(UserId)) return UserId;
            // Метку выдаёт ПАСПОРТИСТ: два дома и починка уцелевшим — его
            // правило, а не забота экрана профиля. Здесь остаётся только
            // выбор «учётка хоста или метка этого устройства».
            return LvnMark.Steady(PlayerMark);
        }

        // Забыть идентификатор — ОБА его дома: он ключ ко всему серверному, и
        // переживший файл вернул бы игрока в удалённую учётку следующим стартом.
        private static void ForgetUserId()
        {
            // Поле UserId экземпляра не трогаем: это НАСТРОЙКА хоста (заданная
            // в инспекторе учётка), а не то, что игра узнала об игроке.
            LvnMark.Forget(PlayerMark);
        }

        // Seed the rich detail page with the real title (name/art/synopsis/cost),
        // then its player-facing stat vars, before showing it — so "Твои статы"
        // reads live numbers instead of the placeholder the screen falls back to
        // when nothing has seeded it. The stats fetch never blocks the open on a
        // slow/offline state store — it's best-effort, empty vars just read as 0.
        // Профиль без фейка (живой репорт): отношения с фаворитами — из
        // РЕАЛЬНЫХ статов. По каждому тайтлу с relationship-статами читаем
        // сохранённые переменные и превращаем в полосы «имя → доля от max».
        // Пустой прогресс честно прячет секцию — рисованных процентов нет.
        private async Task OpenProfileWithRelationsAsync()
        {
            var p = _shell?.Profile;
            if (p == null) return;
            var rel = new List<Lvn.UI.Screens.ProfileScreen.Relation>();
            var titles = _manifest?.titles;
            if (titles != null)
            {
                foreach (var t in titles)
                {
                    if (t?.stats == null || t.id == null) continue;
                    Newtonsoft.Json.Linq.JObject vars = null;
                    foreach (var s in t.stats)
                    {
                        if (s == null || !s.relationship || string.IsNullOrEmpty(s.key)) continue;
                        if (vars == null)
                        {
                            try { vars = await LoadScopedVarsAsync(t.id); }
                            catch { vars = new Newtonsoft.Json.Linq.JObject(); }
                        }
                        float val = 0f;
                        // Путь статы может не существовать или указывать на объект —
                // тогда стата просто показывает ноль, а не роняет экран.
                try { val = (float?)vars?.SelectToken(s.key) ?? 0f; } catch { }
                        if (val <= 0f) continue; // не начатые романы полку не занимают
                        float max = s.max > 0 ? s.max : 20f;
                        rel.Add(new Lvn.UI.Screens.ProfileScreen.Relation(
                            string.IsNullOrEmpty(s.label) ? s.key : s.label,
                            Mathf.Clamp01(val / max)));
                    }
                }
            }
            rel.Sort((a, b) => b.Affection.CompareTo(a.Affection));
            p.Relations = rel;
            // Честная цифра прогресса: пройденные главы по всем историям.
            // Считает ПРОГРЕСС — здесь стояла своя формула с двумя зажимами, и
            // она считала достигнутую главу пройденной, хотя игрок в ней сейчас.
            int done = 0;
            if (titles != null)
                foreach (var t in titles)
                    if (t != null) done += LvnProgress.Done(t);
            p.ChaptersDone = done;
            // Профиль — дом данных ИГРОКА: настоящие имя и ID (в экране зашиты
            // демо-заглушки), живой кошелёк, удаление аккаунта. Жалоба-ориентир:
            // «в настройках больше данных для профиля, чем в профиле».
            // Имя экрану больше не толкают: он спрашивает его у роли сам.
            var uid = Lvn.Services.LvnBackend.UserId;
            if (!string.IsNullOrEmpty(uid)) p.Uid = uid;
            p.Wallet = BuildWalletTiles();
            p.OnDeleteAccount = DeleteAccountAndForgetAsync;
            // Выход возвращает телефон к его собственной учётке. Нужен ровно
            // с того дня, как вход перестал слетать сам при перезапуске.
            p.OnSignOut = Lvn.Services.LvnBackend.SignOutAsync;
            p.OnOpenSettings = () => LvnAsync.Fire(_shell.OpenSettingsAsync(), "OpenSettings");
            await _shell.TabGoTo(LvnTabs.Profile); // вкладка ленты, не модалка
        }

        // Единственная правда о валютах игры: ui.browse.currencies, дефолт —
        // прежняя пара. И шапка хаба, и кошелёк профиля идут отсюда.
        private List<string> HubCurrencies()
        {
            var cfg = _manifest?.ui?.browse?.currencies;
            return cfg != null && cfg.Count > 0 ? cfg : new List<string> { "energy", "gold" };
        }

        // Плитки кошелька для профиля: те же валюты, что в шапке хаба, подписи
        // из ui.store.currency_names (данные, не хардкод).
        private List<Lvn.UI.Screens.ProfileScreen.Stat> BuildWalletTiles()
        {
            var tiles = new List<Lvn.UI.Screens.ProfileScreen.Stat>();
            var names = _manifest?.ui?.store?.currency_names;
            foreach (var cur in HubCurrencies())
            {
                string value = Lvn.Services.LvnWallet.Display(cur);
                string caption = names != null && names.TryGetValue(cur, out var n) && !string.IsNullOrEmpty(n)
                    ? n : cur;
                tiles.Add(new Lvn.UI.Screens.ProfileScreen.Stat(value, caption));
            }
            return tiles;
        }

        // «Удалить аккаунт»: сервер стирает учётку/кошелёк/сейвы (LvnBackend),
        // затем локальное забвение — прогресс и статы всех историй, имя,
        // пройденность воронки. Порядок важен: локальное трём только после
        // успешного ответа сервера, иначе отказ сети выглядел бы как удаление.
        private async Task<bool> DeleteAccountAndForgetAsync()
        {
            // СПИСОК ТОГО, ЧТО ЗАБЫВАТЬ, ПРИХОДИТ ИЗ КАТАЛОГА — и без него
            // забвение выйдет ПОЛОВИНЧАТЫМ: уйдут статы, метки, имя и флаги, а
            // сейвы, галерея, прочитанное и наряды останутся лежать. Причём
            // учётка на сервере к тому моменту уже удалена (порядок здесь
            // обратный, см. канон), и вернуть её, чтобы доудалить локальное,
            // нельзя. Игрок будет уверен, что данных нет, а они есть.
            //
            // Проверяем ДО обращения к серверу: отказ до первого шага честнее
            // необратимой половины.
            if (_manifest?.titles == null)
            {
                Debug.LogWarning("[lvn-app] удаление аккаунта отложено: каталог не загружен, "
                                 + "локальное забвение вышло бы неполным");
                return false;
            }
            bool ok = await Lvn.Services.LvnBackend.DeleteAccountAsync();
            if (!ok) return false;
            var titles = _manifest?.titles;
            if (titles != null)
                foreach (var t in titles)
                    await WipeServerVarsAsync(t?.id);
            // Всё локальное — одним обрядом: перечисление здесь знало сейвы,
            // прогресс, имя и два флага, но не галерею, прочитанное, гардероб,
            // миниатюры и статы игрока.
            Lvn.UI.LvnForget.All(TitleIds(), WardrobeEntities());
            LvnLog.Info("[lvn-app] аккаунт удалён — сервер и локальные данные стёрты");
            return true;
        }

        // Кого забывать: все новеллы каталога и всех, кого игрок мог одевать.
        // Хранилища личного адресуются по ключу и списка своих ключей не ведут —
        // перечень приходит из манифеста, единственного, кто знает состав.
        private List<string> TitleIds()
        {
            var ids = new List<string>();
            var titles = _manifest?.titles;
            if (titles != null)
                foreach (var t in titles)
                    if (!string.IsNullOrEmpty(t?.id)) ids.Add(t.id);
            return ids;
        }

        private List<string> WardrobeEntities()
        {
            var ids = new List<string>();
            var sprites = _manifest?.sprites;
            if (sprites != null)
                foreach (var kv in sprites)
                    if (kv.Value?.wardrobe != null && kv.Value.wardrobe.Count > 0)
                        ids.Add(kv.Key);
            return ids;
        }

        private async Task<bool> OpenDetailWithStatsAsync(LvnTitle t)
        {
            if (_shell.Detail != null)
            {
                // КАРТОЧКЕ ДАЮТ НОВЕЛЛУ, а не её разобранные поля: имя,
                // обложку, синопсис и цену она достаёт из неё сама. Раньше
                // здесь стояли четыре присваивания рядом с этой же строкой —
                // и держались на памяти того, кто их пишет.
                _shell.Detail.Title = t;
                // …и экономика новеллы: ценник на кнопке считает ту же цену,
                // что спишет кассир (гейт главы, бесплатные главы).
                _shell.Detail.Economy = _manifest?.economy;
                _shell.Detail.OnResetProgress = ResetTitleProgressAsync;
                Newtonsoft.Json.Linq.JObject vars = null;
                if (t?.id != null)
                {
                    try { vars = await LoadScopedVarsAsync(t.id); }
                    catch (Exception e) { Debug.LogWarning($"[lvn-app] stat vars load failed: {e.Message}"); }
                }
                _shell.Detail.StatVars = vars ?? new Newtonsoft.Json.Linq.JObject();
                _shell.Detail.Rebuild();
            }
            return await _shell.OpenDetailAsync();
        }
    }
}
