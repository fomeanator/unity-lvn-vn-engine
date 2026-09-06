using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// СКЕЛЕТ НАЗЫВАЕТСЯ ОДНИМ АДРЕСОМ.
    ///
    /// <para>Spine — отраслевой стандарт с неизменным комплектом выгрузки:
    /// <c>имя.json</c>, <c>имя.atlas(.txt)</c> рядом и страницы, которые атлас
    /// называет сам. Раньше автор был обязан завести сущность в каталоге
    /// спрайтов и вписать туда четыре пути руками — плата за то, что выводится
    /// само. Теперь достаточно назвать файл скелета в самой команде.</para>
    ///
    /// <para>Проверяется вывод адресов: ошибка здесь означает 404 посреди
    /// сцены, а её автор увидит только на устройстве.</para>
    /// </summary>
    public class SpineOneUrlTests
    {
        [Test]
        public void ИзJsonВыводитсяАтласРядом()
        {
            var sp = LvnSpineRef.FromUrl("/content/spine/hero/hero.json");
            Assert.AreEqual("/content/spine/hero/hero.json", sp.json);
            Assert.AreEqual("/content/spine/hero/hero.atlas.txt", sp.atlas,
                "атлас обязан выводиться из имени скелета — иначе автор пишет его руками");
            // Запасное написание сцена выводит тем же домом адресов.
            Assert.AreEqual("/content/spine/hero/hero.atlas", Lvn.LvnUrl.Sibling(sp.atlas, ".atlas"),
                "второе написание атласа не выводится — комплект из Spine перестанет открываться");
        }

        [Test]
        public void ПапкаЗначитКомплектВнутриНеё()
        {
            var sp = LvnSpineRef.FromUrl("/content/spine/hero/");
            Assert.AreEqual("/content/spine/hero/hero.json", sp.json,
                "папка не развернулась в комплект — а так выгружают чаще всего");
            Assert.AreEqual("/content/spine/hero/hero.atlas.txt", sp.atlas);
        }

        [Test]
        public void ДвоичныйСкелетТожеЗнаетСвойАтлас()
        {
            foreach (var url in new[] { "/s/a.skel", "/s/a.skel.bytes" })
                Assert.AreEqual("/s/a.atlas.txt", LvnSpineRef.FromUrl(url).atlas,
                    $"для {url} атлас выведен неверно");
        }

        [Test]
        public void ПодложкаИПроигрышПередаютсяКакЕсть()
        {
            var sp = LvnSpineRef.FromUrl("/s/a.json", "/s/back.jpg", "idle");
            Assert.AreEqual("/s/back.jpg", sp.bg);
            Assert.AreEqual("idle", sp.auto, "названная анимация не доехала — фигура встанет неподвижной");
        }

        [Test]
        public void ПустойАдресНеСоздаётПустогоСкелета()
        {
            Assert.IsNull(LvnSpineRef.FromUrl(null));
            Assert.IsNull(LvnSpineRef.FromUrl("   "));
            Assert.IsNull(LvnSpineRef.FromUrl("/"), "корень — это не комплект, имени в нём нет");
        }
    }
}
