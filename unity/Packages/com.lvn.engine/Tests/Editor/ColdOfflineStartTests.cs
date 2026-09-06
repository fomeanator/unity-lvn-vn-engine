using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Lvn.Content;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Lvn.Tests
{
    /// <summary>
    /// ИГРУ ЗАКРЫЛИ, СЕРВЕР ПОГАС, ИГРУ ОТКРЫЛИ СНОВА — И ОНА ИДЁТ.
    ///
    /// <para>Соседняя проверка (<c>OfflinePlaythroughTests</c>) гасит сервер
    /// ПОСРЕДИ сессии: глава доигрывается на том, что уже в памяти. Это честно,
    /// но это не тот случай, ради которого офлайн заводят. Настоящий случай —
    /// метро на следующее утро: приложение стартует С НУЛЯ, ничего в памяти
    /// нет, сети нет, и всё должно найтись на диске.</para>
    ///
    /// <para>Поэтому здесь второй загрузчик создаётся на том же каталоге кэша
    /// уже ПОСЛЕ гашения сервера — это и есть «открыли заново». Подмены сети
    /// нет нигде: сервер настоящий, и он действительно мёртв, что проверяется
    /// отдельно, иначе «работает офлайн» означало бы «работает, потому что
    /// втихую сходило в сеть».</para>
    /// </summary>
    public class ColdOfflineStartTests
    {
        private const string ScriptPath = "content/ch.lvn";
        private const string ArtPath = "content/bg/room.jpg";
        private const string Chapter = @"{""scene"":""cold"",""script"":[
            {""op"":""bg"",""sprite_url"":""/content/bg/room.jpg""},
            {""op"":""say"",""text"":""первая""}
        ]}";
        private const string Catalog = @"{""titles"":[{""id"":""t"",""name"":""Проба""}]}";

        private string _cache;
        private bool _wasOffline;

        [SetUp]
        public void SetUp()
        {
            _cache = Path.Combine(Path.GetTempPath(), "lvn-cold-" + Guid.NewGuid().ToString("N"));
            _wasOffline = !LvnNetworkStatus.IsOnline;
            LvnNetworkStatus.MarkOnline();
        }

        [TearDown]
        public void TearDown()
        {
            if (_wasOffline) LvnNetworkStatus.MarkOffline(); else LvnNetworkStatus.MarkOnline();
            if (Directory.Exists(_cache)) Directory.Delete(_cache, true);
        }

        private static IEnumerator Await(Task t)
        {
            while (!t.IsCompleted) yield return null;
            if (t.IsFaulted) throw t.Exception;
        }

        private static Dictionary<string, byte[]> Файлы() => new Dictionary<string, byte[]>
        {
            [ScriptPath] = Encoding.UTF8.GetBytes(Chapter),
            [ArtPath] = new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 },
            ["v1/content/manifest"] = Encoding.UTF8.GetBytes(Catalog),
        };

        [UnityTest]
        public IEnumerator ХолодныйЗапускБезСетиИдётНаТом_ЧтоЛежитНаДиске()
        {
            var файлы = Файлы();
            string root;

            // ── Вчера: сеть была, контент забран ────────────────────────────
            using (var srv = new TestHttpServer(файлы))
            {
                root = srv.Root;
                using var вчера = new ContentLoader(root, _cache);
                // ИМЕННО ЭТИМ методом игра берёт главу на воспроизведение:
                // DownloadScriptText нарочно всегда свежий и диск не трогает
                // (им берут манифест и опрос версии), а глава кладётся в кэш
                // под ключ версии — ради этого самого случая. Первая редакция
                // стенда звала не тот метод и объявила находкой отказ, который
                // в игре не случается.
                var скрипт = вчера.DownloadScriptCached("/" + ScriptPath);
                yield return Await(скрипт);
                StringAssert.Contains("первая", скрипт.Result, "глава не забралась, пока сеть была");
                var арт = вчера.DownloadAssetBytes("/" + ArtPath);
                yield return Await(арт);
                Assert.IsNotNull(арт.Result, "арт не забрался, пока сеть была");
            }
            // Сервер погашен ВМЕСТЕ с блоком using — дальше сети нет вовсе.

            // Проверяем это, а не верим: «работает офлайн» не должно означать
            // «работает, потому что втихую сходило в сеть».
            using (var проба = new ContentLoader(root, Path.Combine(_cache, "probe")))
            {
                var мимо = проба.DownloadScriptCached("/" + ScriptPath);
                yield return AwaitFailure(мимо);
                Assert.IsTrue(мимо.IsFaulted || string.IsNullOrEmpty(мимо.Result),
                    "сервер отвечает — стенд мерил бы не офлайн, а обычную сеть");
            }

            // ── Сегодня в метро: приложение открыли заново ──────────────────
            using var сегодня = new ContentLoader(root, _cache);
            var глава = сегодня.DownloadScriptCached("/" + ScriptPath);
            yield return Await(глава);
            StringAssert.Contains("первая", глава.Result,
                "глава не нашлась на диске — игрок в метро видит пустой экран");

            var картинка = сегодня.DownloadAssetBytes("/" + ArtPath);
            yield return Await(картинка);
            Assert.IsNotNull(картинка.Result, "арт не нашёлся на диске — глава пойдёт без картинок");
            Assert.AreEqual(8, картинка.Result.Length, "с диска пришло не то, что клали");
        }

        // ЧЕСТНАЯ ГРАНИЦА: чего не скачали — того нет, и это НЕ выдаётся за
        // поломку сети. Разница видна игроку: «эта глава не загружена» лечится
        // одним нажатием, «нет связи» — нет.
        [UnityTest]
        public IEnumerator НескачанноеОфлайнЧестноНеНаходится()
        {
            var файлы = Файлы();
            string root;
            using (var srv = new TestHttpServer(файлы))
            {
                root = srv.Root;
                using var вчера = new ContentLoader(root, _cache);
                var t = вчера.DownloadScriptCached("/" + ScriptPath);
                yield return Await(t);
            }

            using var сегодня = new ContentLoader(root, _cache);
            var чужая = сегодня.DownloadScriptCached("/content/never-fetched.lvn");
            yield return AwaitFailure(чужая);
            Assert.IsTrue(чужая.IsFaulted || string.IsNullOrEmpty(чужая.Result),
                "глава, которую никогда не качали, нашлась офлайн — значит кэш выдумывает содержимое");
        }

        private static IEnumerator AwaitFailure(Task t)
        {
            while (!t.IsCompleted) yield return null;
        }
    }
}
