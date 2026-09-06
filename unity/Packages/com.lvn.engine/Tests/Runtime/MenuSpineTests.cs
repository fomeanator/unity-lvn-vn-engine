using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Lvn.Tests.Runtime
{
    /// <summary>
    /// ГЛАВНЫЙ ЭКРАН — ЖИВОЙ, А НЕ КАРТИНКА.
    ///
    /// <para>Условие: у соседей по цеху (7HS и прочие крупные мобильные
    /// новеллы) на главном экране стоит анимированный персонаж, а не
    /// статичный портрет. Вопрос, который стоит задать своему движку: если
    /// автор объявит героиню витрины СПАЙНОВОЙ, попадёт ли она на главный
    /// экран живой — или витрина умеет только слои.</para>
    ///
    /// <para>Замер идёт по тому, КУДА ушёл кадр витрины. Меню ставит куклу
    /// не своей рисовалкой, а сценическим актёром — той же дорогой, которой
    /// идут персонажи главы, — и на развилке «слои или скелет» спайновая
    /// сущность обязана уходить в скелет.</para>
    ///
    /// <para>ГРАНИЦА ЗАМЕРА, названная честно: сам скелет здесь не строится.
    /// Рендер требует официального рантайма spine-unity, а он лежит вне
    /// публичного репозитория (пакет опциональный, ставится проектом).
    /// Поэтому проверяется маршрут и отказ по делу: витрина уходит в
    /// спайновый путь и говорит «нет интеграции», вместо того чтобы молча
    /// рисовать пустоту или искать несуществующие слои. Сам рендер
    /// подтверждается в проекте, где spine-unity есть.</para>
    /// </summary>
    public class MenuSpineTests
    {
        private sealed class UrlSpy : ILvnAssets
        {
            public readonly List<string> Asked = new List<string>();
            public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct)
            {
                lock (Asked) Asked.Add(url);
                return Task.FromResult<Sprite>(null);
            }
            public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct) => Task.FromResult<AudioClip>(null);
            public Task PreloadAsync(IReadOnlyList<string> urls, string kind, CancellationToken ct)
            {
                if (urls != null) lock (Asked) Asked.AddRange(urls);
                return Task.CompletedTask;
            }
            public void Unload(string url) { }
            public void UnloadAll() { }
        }

        private GameObject _go;
        private VnStage _stage;
        private UrlSpy _spy;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("menu-stage", typeof(UIDocument));
            var doc = _go.GetComponent<UIDocument>();
            doc.panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            _spy = new UrlSpy();
            _stage = _go.AddComponent<VnStage>();
            _stage.Assets = _spy;
        }

        [TearDown]
        public void TearDown() { Object.Destroy(_go); }

        private void Каталог()
        {
            _stage.Catalog = new SpriteCatalog(new Dictionary<string, LvnSpriteEntity>
            {
                ["кукла"] = new LvnSpriteEntity
                {
                    kind = "spine",
                    spine = new LvnSpineRef
                    {
                        json = "spine/doll/doll.json",
                        atlas = "spine/doll/doll.atlas.txt",
                        texture = "spine/doll/doll.png",
                    },
                },
                ["слоёная"] = new LvnSpriteEntity
                {
                    layers = new List<LvnLayer> { new LvnLayer { url = "sprites/plain.png" } },
                },
            });
        }

        private static Newtonsoft.Json.Linq.JObject Поза(string id)
            => LvnPrima.Pose(id, "center", LvnMenuStage.DollWidth, LvnMenuStage.DollHeight, 0);

        [UnityTest]
        public IEnumerator ВитринаВедётСпайновуюГероинюСпайновымПутём()
        {
            Каталог();
            // Отказ по делу — признак того, что кадр витрины дошёл до развилки
            // и выбрал скелет. Без spine-unity дальше идти некуда, и движок
            // говорит об этом словами, а не пустым экраном.
            LogAssert.Expect(LogType.Warning, new Regex("kind:spine"));

            _stage.ShowMenuDoll("кукла", Поза("кукла"));
            yield return new WaitForSecondsRealtime(0.5f);

            List<string> спрошено;
            lock (_spy.Asked) спрошено = _spy.Asked.ToList();
            TestContext.WriteLine("витрина спросила: " + (спрошено.Count == 0 ? "—" : string.Join(", ", спрошено)));

            Assert.IsFalse(спрошено.Any(u => u.Contains("plain.png")),
                "витрина полезла за чужими слоями вместо скелета");
        }

        // УКУС: тот же путь со слоёвой героиней обязан дать слои. Мерка, не
        // видящая обычную куклу, ничего не доказывала бы и про спайновую.
        [UnityTest]
        public IEnumerator ТаЖеВитринаСоСлоёвойГероинейГрузитЕёСлои()
        {
            Каталог();
            _stage.ShowMenuDoll("слоёная", Поза("слоёная"));
            yield return new WaitForSecondsRealtime(0.5f);

            List<string> спрошено;
            lock (_spy.Asked) спрошено = _spy.Asked.ToList();
            Assert.IsTrue(спрошено.Any(u => u.Contains("plain.png")),
                "витрина не запросила слой обычной куклы — замер слеп, а не витрина цела");
        }
    }
}
