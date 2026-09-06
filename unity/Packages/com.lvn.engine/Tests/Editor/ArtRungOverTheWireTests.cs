using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Lvn.Content;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Lvn.Tests
{
    /// <summary>
    /// СТУПЕНЬ, КОТОРУЮ ВЫБРАЛ ИГРОК, — ЭТО ТО, ЧТО УХОДИТ С ПРОВОДА.
    ///
    /// <para>Соседняя проверка сторожит СПИСКИ ступеней (что настройки, движок и
    /// совет устройства называют одно и то же). Она не отвечает на вопрос,
    /// ради которого ступени существуют: экономит ли выбор «поменьше» реальный
    /// трафик. Ответ виден только на проводе — сервер записывает, что у него
    /// спросили.</para>
    ///
    /// <para>Вторая половина важнее первой: ступени может НЕ БЫТЬ. Сервер
    /// отдаёт варианты по требованию, и на маленькой картинке уменьшенного
    /// просто нет. Тогда игрок обязан увидеть исходник, а не пустое место —
    /// «арт не качается» из-за настройки качества выглядит как поломка
    /// движка.</para>
    /// </summary>
    public class ArtRungOverTheWireTests
    {
        private string _cache;
        private string _suffixWas;

        [SetUp]
        public void SetUp()
        {
            _cache = Path.Combine(Path.GetTempPath(), "lvn-rung-" + Guid.NewGuid().ToString("N"));
            _suffixWas = DownloadPolicy.PreferredSuffix;
        }

        [TearDown]
        public void TearDown()
        {
            DownloadPolicy.PreferredSuffix = _suffixWas;
            if (Directory.Exists(_cache)) Directory.Delete(_cache, true);
        }

        private static IEnumerator Await(Task t)
        {
            while (!t.IsCompleted) yield return null;
            if (t.IsFaulted) throw t.Exception;
        }

        private static Dictionary<string, byte[]> Art(params string[] paths)
        {
            var d = new Dictionary<string, byte[]>();
            foreach (var p in paths) d[p] = new byte[] { 7, 7, 7, 7 };
            return d;
        }

        // Игрок выбрал «поменьше» — с провода обязана уйти именно эта ступень.
        [UnityTest]
        public IEnumerator ВыбраннаяСтупеньУходитСПровода()
        {
            using var srv = new TestHttpServer(Art("content/bg/room@2k.jpg", "content/bg/room@1k.jpg"));
            using var loader = new ContentLoader(srv.Root, _cache);

            DownloadPolicy.PreferredSuffix = DownloadPolicy.Q1k;
            var url = DownloadPolicy.DownscaleVariant("/content/bg/room.jpg");
            Assert.AreEqual("/content/bg/room@1k.jpg", url, "выбор ступени не дошёл до адреса");

            var t = loader.DownloadAssetBytes(url);
            yield return Await(t);

            Assert.IsTrue(srv.WasAsked("content/bg/room@1k.jpg"),
                "игрок выбрал 1k, а с провода ушло другое: " + string.Join(", ", srv.Asked));
            Assert.IsFalse(srv.WasAsked("content/bg/room@2k.jpg"),
                "утянули и крупную ступень тоже — выбор «поменьше» не экономит ничего");
        }

        // Смена ступени меняет то, что просят. Без этого настройка была бы
        // украшением: переключил — и ничего не изменилось.
        [UnityTest]
        public IEnumerator СменаСтупениМеняетЗапрос()
        {
            using var srv = new TestHttpServer(Art("content/bg/room@2k.jpg", "content/bg/room@1k.jpg"));

            DownloadPolicy.PreferredSuffix = DownloadPolicy.Q1k;
            using (var a = new ContentLoader(srv.Root, Path.Combine(_cache, "a")))
            {
                var t1 = a.DownloadAssetBytes(DownloadPolicy.DownscaleVariant("/content/bg/room.jpg"));
                yield return Await(t1);
            }

            DownloadPolicy.PreferredSuffix = DownloadPolicy.Q2k;
            using (var b = new ContentLoader(srv.Root, Path.Combine(_cache, "b")))
            {
                var t2 = b.DownloadAssetBytes(DownloadPolicy.DownscaleVariant("/content/bg/room.jpg"));
                yield return Await(t2);
            }

            Assert.IsTrue(srv.WasAsked("content/bg/room@1k.jpg") && srv.WasAsked("content/bg/room@2k.jpg"),
                "после смены ступени спросили то же самое: " + string.Join(", ", srv.Asked));
        }

        // СТУПЕНИ МОЖЕТ НЕ БЫТЬ — И ЭТО НЕ ПОЛОМКА. Сервер режет варианты по
        // требованию, и у маленькой картинки уменьшенного не существует.
        // Игрок обязан увидеть исходник: пустое место из-за настройки качества
        // читается как сломанный движок.
        [UnityTest]
        public IEnumerator ОтсутствующаяСтупеньОткатываетсяНаИсходник()
        {
            using var srv = new TestHttpServer(Art("content/bg/tiny.jpg"));   // вариантов нет
            using var loader = new ContentLoader(srv.Root, _cache);

            DownloadPolicy.PreferredSuffix = DownloadPolicy.Q1k;
            var variant = DownloadPolicy.DownscaleVariant("/content/bg/tiny.jpg");

            bool variantFailed = false;
            var t = loader.DownloadAssetBytes(variant);
            while (!t.IsCompleted) yield return null;
            if (t.IsFaulted || t.Result == null) variantFailed = true;
            Assert.IsTrue(variantFailed, "стенд не ставит задачу: уменьшенный вариант нашёлся");

            var orig = loader.DownloadAssetBytes("/content/bg/tiny.jpg");
            yield return Await(orig);
            Assert.AreEqual(4, orig.Result.Length,
                "исходник не поднялся после промаха по ступени — игрок увидит пустое место");
        }

        // ОБОЗ ТОЖЕ ПРОСИТ СТУПЕНЬ. Одиночная загрузка выше идёт через
        // DownscaleVariant руками; фоновый прогрев библиотеки собирает записи
        // обоза из частей контента — и три его очереди клали адрес как есть,
        // утаскивая исходник (9,95 МБ) вместо «@1k» (470 КБ) при любой
        // настройке. Запись обязана собираться домом, который ступень знает.
        [UnityTest]
        public IEnumerator ОбозПроситВыбраннуюСтупеньАНеИсходник()
        {
            using var srv = new TestHttpServer(Art("content/art/hero@1k.png", "content/art/hero.png"));
            using var loader = new ContentLoader(srv.Root, _cache);

            DownloadPolicy.PreferredSuffix = DownloadPolicy.Q1k;
            var item = PreloadItem.Of(new LvnPart("/content/art/hero.png", LvnParts.Sprite, 123));
            Assert.AreEqual("/content/art/hero@1k.png", item.Url, "запись обоза не подставила ступень");
            Assert.AreEqual(123, item.Size, "вес по манифесту потерялся по дороге в обоз");

            var t = loader.StartPreloadBatch(new[] { item }, default);
            yield return Await(t);

            Assert.IsTrue(srv.WasAsked("content/art/hero@1k.png"),
                "обоз не спросил выбранную ступень: " + string.Join(", ", srv.Asked));
            Assert.IsFalse(srv.WasAsked("content/art/hero.png"),
                "обоз утянул исходник — прогрев библиотеки тащит полноразмерный арт при любой настройке");
        }

        // Звук и сценарий ступеней не имеют — запись обоза оставляет их адрес
        // нетронутым, иначе прогрев звука ушёл бы в гарантированный 404.
        [Test]
        public void ЗаписьОбозаНеТрогаетНеКартинки()
        {
            DownloadPolicy.PreferredSuffix = DownloadPolicy.Q1k;
            Assert.AreEqual("/content/audio/theme.ogg",
                PreloadItem.Of(new LvnPart("/content/audio/theme.ogg", LvnParts.Audio)).Url);
            Assert.AreEqual("/content/scripts/ch1.lvn",
                PreloadItem.Of(new LvnPart("/content/scripts/ch1.lvn", LvnParts.Script)).Url);
            Assert.AreEqual("/content/ui/frame.png",
                PreloadItem.Of(new LvnPart("/content/ui/frame.png", LvnParts.Sprite)).Url,
                "обшивке интерфейса ступеней не положено — у рамок и значков вариантов нет");
        }
    }
}
