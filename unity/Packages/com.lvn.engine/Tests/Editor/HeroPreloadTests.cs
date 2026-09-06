using System.Collections.Generic;
using System.Linq;
using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ГАРДЕРОБ НЕ ПУСТ, КОГДА ИГРОК В НЕГО ЗАХОДИТ.
    ///
    /// <para>Условие: человек поставил игру, прошёл вводную и открыл гардероб —
    /// посмотреть, кого ему дали. Это происходит в первые минуты, а не через
    /// час: гардероб живёт в меню и открывается в любую секунду.</para>
    ///
    /// <para>Раньше он видел пустые карточки, и причин было ДВЕ. Порядок:
    /// облик всего каста стоял на последней ступени лестницы — за спиной у
    /// всех глав всех новелл. И полнота: слои героини в каталоге записаны
    /// ШАБЛОНАМИ (<c>hero_{emotion}.png</c>), а прогрев шаблоны пропускает —
    /// значит база с эмоциями не качалась заранее НИКОГДА, только в момент
    /// показа.</para>
    /// </summary>
    public class HeroPreloadTests
    {
        private static LvnManifest Каталог()
        {
            var героиня = new LvnSpriteEntity
            {
                layers = new List<LvnLayer>
                {
                    new LvnLayer { url = "sprites/hero_body_{outfit}.png" },
                    new LvnLayer { url = "sprites/hero_face_{emotion}.png" },
                },
                defaults = new Dictionary<string, string> { ["emotion"] = "neutral", ["outfit"] = "casual" },
                axes = new Dictionary<string, List<string>>
                {
                    ["emotion"] = new List<string> { "neutral", "smile", "sad" },
                    ["outfit"] = new List<string> { "casual", "gala" },
                },
                wardrobe = new Dictionary<string, LvnWardrobeSlot>
                {
                    ["outfit"] = new LvnWardrobeSlot
                    {
                        icon = "ui/slot_outfit.png",
                        items = new List<LvnWardrobeItem>
                        {
                            new LvnWardrobeItem { value = "casual", icon = "ui/item_casual.png" },
                            new LvnWardrobeItem { value = "gala", icon = "ui/item_gala.png" },
                        },
                    },
                },
            };
            var статист = new LvnSpriteEntity
            {
                layers = new List<LvnLayer> { new LvnLayer { url = "sprites/extra_{emotion}.png" } },
                defaults = new Dictionary<string, string> { ["emotion"] = "neutral" },
                axes = new Dictionary<string, List<string>> { ["emotion"] = new List<string> { "neutral", "angry" } },
            };
            return new LvnManifest
            {
                sprites = new Dictionary<string, LvnSpriteEntity> { ["героиня"] = героиня, ["статист"] = статист },
                ui = new LvnUiConfig { wardrobe = new WardrobeConfig { entity = "героиня" } },
            };
        }

        [Test]
        public void ГероинюНазываетКаталог_АНеДогадкаДвижка()
        {
            Assert.AreEqual("героиня", LvnParts.HeroEntity(Каталог()),
                "героиню берут не из ui.wardrobe.entity");

            // Не назвали прямо — берём первую с гардеробом, как это делает сам
            // лист гардероба.
            var без = Каталог();
            без.ui.wardrobe.entity = null;
            Assert.AreEqual("героиня", LvnParts.HeroEntity(без),
                "без явного имени героиней должна стать первая сущность с гардеробом");
        }

        [Test]
        public void ОбликГероиниРазворачиваетсяПоОсям_АНеПропускаетсяКакШаблон()
        {
            var части = LvnParts.OfHero(Каталог()).Select(p => p.Url).ToList();
            TestContext.WriteLine("облик героини: " + string.Join(", ", части));

            // Ни одного шаблона: адрес с фигурными скобками сервер отдать не может.
            Assert.IsFalse(части.Any(u => u.Contains("{")),
                "в очередь попал шаблон — это гарантированный 404");

            // База (умолчания) и КАЖДОЕ значение каждой оси.
            foreach (var url in new[]
            {
                "sprites/hero_face_neutral.png", "sprites/hero_face_smile.png", "sprites/hero_face_sad.png",
                "sprites/hero_body_casual.png", "sprites/hero_body_gala.png",
            })
                Assert.Contains(url, части, $"облик героини не содержит {url} — эмоция или наряд не прогреются");

            // Гардероб — иконки слотов и предметов.
            foreach (var url in new[] { "ui/slot_outfit.png", "ui/item_casual.png", "ui/item_gala.png" })
                Assert.Contains(url, части, $"гардероб героини не содержит {url} — карточка будет пустой");

            // Чужого каста здесь нет: у него своя, последняя ступень.
            Assert.IsFalse(части.Any(u => u.Contains("extra_")),
                "в облик героини попал другой персонаж — приоритет перестал быть относительным");

            // СУММА, А НЕ ПРОИЗВЕДЕНИЕ. Разворот идёт по одной оси от умолчаний:
            // иначе три эмоции × два наряда дали бы шесть тел и шесть лиц, и
            // «приоритет героини» стал бы новым способом забить очередь.
            TestContext.WriteLine($"файлов в облике: {части.Count}");
            Assert.LessOrEqual(части.Count, 10,
                "разворот пошёл произведением осей — очередь героини раздулась");
        }

        // ЗАМЕР «ДО»: прежний прогрев брал каст целиком — и терял на этом всю
        // мимику. Слои записаны шаблонами, а шаблон качать нельзя: файла с
        // фигурными скобками нет. Значит эмоции не прогревались НИКОГДА, и
        // гардероб открывался с пустыми карточками ровно поэтому.
        [Test]
        public void ПрежнийПрогревКастаТерялВсюМимику()
        {
            var каталог = Каталог();
            var кастом = LvnParts.OfCast(каталог).Select(p => p.Url).ToList();
            var героиней = LvnParts.OfHero(каталог).Select(p => p.Url).ToList();

            int лицаВКасте = кастом.Count(u => u.Contains("hero_face_"));
            int лицаВОблике = героиней.Count(u => u.Contains("hero_face_"));
            TestContext.WriteLine($"эмоции героини: прежним прогревом каста {лицаВКасте}, "
                                + $"облик героини {лицаВОблике}");

            Assert.AreEqual(0, лицаВКасте,
                "стенд: прежний путь внезапно научился разворачивать шаблоны — "
              + "перепишите замер и запись в каноне");
            Assert.Greater(лицаВОблике, 0,
                "облик героини не развернул ни одной эмоции — гардероб останется пустым");
        }

        /// <summary>
        /// СПАЙНОВАЯ ГЕРОИНЯ — ТОЖЕ ГЕРОИНЯ.
        ///
        /// <para>Прогрев искал облик в <c>layers</c> и <c>wardrobe</c>. У
        /// спайновой сущности слоёв нет вовсе: её облик — скелет, атлас и
        /// страница атласа. Замер на живом каталоге 06.09: 14 спайновых
        /// сущностей, 48 файлов скелета, в ступенчатой очереди из них НОЛЬ.</para>
        ///
        /// <para>Внутри главы скелет греется look-ahead'ом за 25 команд до
        /// показа — это не про него. Он про меню и гардероб, где главы нет и
        /// look-ahead молчит: там спайновая героиня собиралась на глазах.</para>
        /// </summary>
        [Test]
        public void СкелетСпайновойГероиниЕдетВместеСОбликом()
        {
            var каталог = Каталог();
            каталог.sprites["кукла"] = new LvnSpriteEntity
            {
                kind = "spine",
                spine = new LvnSpineRef
                {
                    json = "spine/doll/doll.json",
                    atlas = "spine/doll/doll.atlas.txt",
                    texture = "spine/doll/doll.png",
                    bg = "spine/doll/back.jpg",
                },
                wardrobe = new Dictionary<string, LvnWardrobeSlot>
                {
                    ["outfit"] = new LvnWardrobeSlot { icon = "ui/doll_slot.png" },
                },
            };
            каталог.ui.wardrobe.entity = "кукла";

            var облик = LvnParts.OfHero(каталог).Select(p => p.Url).ToList();
            TestContext.WriteLine("облик спайновой героини: " + string.Join(", ", облик));
            foreach (var url in new[]
            {
                "spine/doll/doll.json", "spine/doll/doll.atlas.txt",
                "spine/doll/doll.png", "spine/doll/back.jpg",
            })
                Assert.Contains(url, облик,
                    $"облик спайновой героини не содержит {url} — скелет приедет в момент показа");

            // Разметка раньше страницы атласа: собирать скелет будет из чего к
            // тому мигу, когда доедут мегабайты картинки.
            Assert.Less(облик.IndexOf("spine/doll/doll.json"), облик.IndexOf("spine/doll/doll.png"),
                "тяжёлая страница атласа встала перед разметкой скелета");

            // Остальной спайн — тоже в очереди, но на своей последней ступени.
            var кастом = LvnParts.OfCast(каталог).Select(p => p.Url).ToList();
            Assert.Contains("spine/doll/doll.json", кастом,
                "скелеты каста не попали в прогрев вовсе");
        }

        [Test]
        public void ГероиняСтоитВышеБиблиотекиИНижеТекущейГлавы()
        {
            Assert.Less((int)LvnRung.CurrentChapter, (int)LvnRung.Hero,
                "героиня обогнала текущую главу — она отнимет полосу у сцены на экране");
            Assert.Less((int)LvnRung.Hero, (int)LvnRung.Library,
                "героиня осталась за библиотекой — гардероб снова будет пустым");
            Assert.Less((int)LvnRung.Library, (int)LvnRung.Spare,
                "остальной каст должен остаться последним");

            // Порядок соблюдается домом очереди, а не звонящим.
            var порядок = LvnPriority.ByRung(
                new[] { LvnRung.Spare, LvnRung.Library, LvnRung.Hero, LvnRung.FirstFrame },
                r => r).ToList();
            CollectionAssert.AreEqual(
                new[] { LvnRung.FirstFrame, LvnRung.Hero, LvnRung.Library, LvnRung.Spare }, порядок,
                "дом очереди перестал соблюдать лестницу");
        }
    }
}
