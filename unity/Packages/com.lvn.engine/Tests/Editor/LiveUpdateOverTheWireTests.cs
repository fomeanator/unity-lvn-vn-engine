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
    /// ПРАВКА ДОЕЗЖАЕТ ДО ИГРАЮЩЕГО, И СТОИТ ОНА ПРАВКУ.
    ///
    /// <para>Условие: автор поправил одну реплику в вышедшей главе. Игрок,
    /// который эту главу читает, обязан увидеть новый текст — и заплатить за
    /// это ценой реплики, а не ценой каталога. Требование записано Ильёй
    /// прямо: качать только изменившееся, а не весь манифест.</para>
    ///
    /// <para>Серверную половину меряет <c>qa/live-update-cost-check.sh</c> на
    /// настоящем сервере. Здесь вторая половина, которую стенду не видно:
    /// КЛИЕНТ. Двойник записывает каждый спрошенный путь, поэтому «не пошёл за
    /// картой версий» и «не пошёл за манифестом» — наблюдаемые факты, а не
    /// намерения кода.</para>
    /// </summary>
    public class LiveUpdateOverTheWireTests
    {
        private string _cache;

        [SetUp]
        public void SetUp()
            => _cache = Path.Combine(Path.GetTempPath(), "lvn-live-" + Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_cache)) Directory.Delete(_cache, true);
        }

        private static IEnumerator Await(Task t)
        {
            while (!t.IsCompleted) yield return null;
            if (t.IsFaulted) throw t.Exception;
        }

        private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

        private const string Script = "content/scripts/ch2.lvn";
        private const string Было = "{\"scene\":\"ch2\",\"script\":[{\"op\":\"say\",\"text\":\"было\"}]}";
        private const string Стало = "{\"scene\":\"ch2\",\"script\":[{\"op\":\"say\",\"text\":\"стало\"}]}";

        private static Dictionary<string, byte[]> Каталог()
            => new Dictionary<string, byte[]>
            {
                ["v1/content/version"] = B("{\"version\":\"v1\"}"),
                ["content/asset-versions.json"] = B("{\"scripts/ch2.lvn\":\"h1\",\"bg/room.jpg\":\"hb\"}"),
                ["v1/content/manifest"] = B("{\"titles\":[]}"),
                [Script] = B(Было),
                ["content/bg/room.jpg"] = new byte[] { 1, 2, 3, 4 },
            };

        // Автор правит реплику: меняется файл и общая версия, разница называет
        // ровно один путь и НЕ называет каталог.
        private static void ПравкаРеплики(Dictionary<string, byte[]> файлы)
        {
            файлы[Script] = B(Стало);
            файлы["v1/content/version"] = B("{\"version\":\"v2\"}");
            файлы["v1/content/changes"] =
                B("{\"since\":\"v1\",\"version\":\"v2\",\"changed\":{\"scripts/ch2.lvn\":\"h2\"},\"removed\":[]}");
        }

        [UnityTest]
        public IEnumerator ПравкаРепликиДоезжает_АКаталогНеКачается()
        {
            var файлы = Каталог();
            using var srv = new TestHttpServer(файлы);
            using var loader = new ContentLoader(srv.Root, _cache);
            var sync = new ContentSync(loader);

            // Запуск: карта версий забрана один раз, глава прочитана.
            yield return Await(loader.LoadAssetVersionsAsync());
            var первое = loader.DownloadScriptText("/" + Script);
            yield return Await(первое);
            StringAssert.Contains("было", первое.Result, "глава не прочиталась вовсе");

            ПравкаРеплики(файлы);

            // Клиент спрашивает РАЗНИЦУ, а не забирает всё.
            var разница = sync.FetchDeltaAsync("v1");
            yield return Await(разница);
            var d = разница.Result;
            Assert.IsNotNull(d, "клиент не смог спросить разницу — живое обновление пойдёт дорогой дорогой");
            Assert.IsFalse(d.Full, "сервер назвал изменения поимённо, а клиент понял это как «забирай всё»");
            Assert.AreEqual(1, d.Changed.Count, "в разнице не один путь: " + string.Join(", ", d.Changed.Keys));
            Assert.IsFalse(d.ManifestChanged,
                "правка реплики принята за смену каталога — клиент пойдёт за манифестом зря");

            Assert.AreEqual(1, loader.ApplyVersionDelta(d.Changed, d.Removed),
                "разница не наложилась на карту версий");

            // ГЛАВНОЕ: игрок видит новый текст.
            var второе = loader.DownloadScriptText("/" + Script);
            yield return Await(второе);
            StringAssert.Contains("стало", второе.Result,
                "после правки игроку достался вчерашний текст — обновление не доехало");

            // И ЦЕНА: карту версий забрали один раз (на запуске), за каталогом
            // не ходили вовсе.
            int картаРаз = 0;
            lock (srv.Asked)
                foreach (var p in srv.Asked)
                    if (p == "content/asset-versions.json") картаРаз++;
            TestContext.WriteLine("клиент спросил: " + string.Join(", ", srv.Asked));
            Assert.AreEqual(1, картаРаз,
                $"карта версий забрана {картаРаз} раз(а) — правка реплики стоила целой карты");
            Assert.IsFalse(srv.WasAsked("v1/content/manifest"),
                "каталог не менялся, а клиент за ним сходил — это и есть «качаем весь манифест»");
        }

        // ВТОРАЯ ПОЛОВИНА ДОГОВОРА: каталог МЕНЯЛСЯ — тогда за ним и надо идти.
        // Без этой проверки «экономия» означала бы, что новая глава не появится
        // у игрока никогда.
        [UnityTest]
        public IEnumerator СменаКаталогаНазываетсяСменойКаталога()
        {
            var файлы = Каталог();
            using var srv = new TestHttpServer(файлы);
            using var loader = new ContentLoader(srv.Root, _cache);
            var sync = new ContentSync(loader);

            файлы["v1/content/changes"] = B("{\"since\":\"v1\",\"version\":\"v2\",\"changed\":"
                + "{\"manifest.json\":\"m2\",\"scripts/ch4.lvn\":\"h4\"},\"removed\":[]}");

            var разница = sync.FetchDeltaAsync("v1");
            yield return Await(разница);
            Assert.IsTrue(разница.Result.ManifestChanged,
                "добавили главу, а клиент решил, что каталог тот же — новой главы игрок не увидит");
        }

        // ТРЕТЬЯ: сервер не помнит названной версии (игрок спал неделю).
        // Честный ответ — «забирай всё», и клиент обязан понять его так же.
        [UnityTest]
        public IEnumerator ЗабытуюВерсиюКлиентПониМаетКакПолнуюЗагрузку()
        {
            var файлы = Каталог();
            using var srv = new TestHttpServer(файлы);
            using var loader = new ContentLoader(srv.Root, _cache);
            var sync = new ContentSync(loader);

            файлы["v1/content/changes"] = B("{\"since\":\"древняя\",\"version\":\"v2\",\"full\":true,"
                + "\"changed\":{},\"removed\":[]}");

            var разница = sync.FetchDeltaAsync("древняя");
            yield return Await(разница);
            Assert.IsTrue(разница.Result.Full, "«забирай всё» прочитано как «ничего не менялось»");
            Assert.IsTrue(разница.Result.ManifestChanged,
                "полная загрузка обязана включать каталог — иначе он останется вчерашним");
        }

        // ЧЕТВЁРТАЯ: удалённый файл назван удалённым и уходит из карты версий,
        // а не живёт в кэше игрока вечно.
        [UnityTest]
        public IEnumerator УдалённыйФайлУходитИзКарты()
        {
            var файлы = Каталог();
            using var srv = new TestHttpServer(файлы);
            using var loader = new ContentLoader(srv.Root, _cache);

            yield return Await(loader.LoadAssetVersionsAsync());
            Assert.AreEqual(1, loader.ApplyVersionDelta(null, new[] { "bg/room.jpg" }),
                "удаление не наложилось на карту версий");
        }
    }
}
