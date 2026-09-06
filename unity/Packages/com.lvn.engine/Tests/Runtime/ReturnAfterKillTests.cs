using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
    /// ИГРОК ЗАКРЫЛ ИГРУ ПОСРЕДИ СЦЕНЫ И ВЕРНУЛСЯ — КАДР ТОТ ЖЕ.
    ///
    /// <para>Условие, ради которого существует восстановление кадра: телефон
    /// убил приложение (звонок, память, свернул и забыл). Человек открывает
    /// заново и обязан увидеть ТУ ЖЕ КОМНАТУ и ТЕХ ЖЕ ЛЮДЕЙ, а не голый текст
    /// поверх пустоты и не начало главы.</para>
    ///
    /// <para>Соседние проверки берут план реплея на заглушке сцены: какие
    /// команды переигрываются и как схлопываются эффекты. Здесь сцена
    /// настоящая и УМИРАЕТ ЦЕЛИКОМ — объект уничтожается, как при закрытии
    /// приложения, — а вторая собирается с нуля и восстанавливается из слота,
    /// которым игра и пользуется.</para>
    ///
    /// <para>Смотрим на две вещи сразу: КТО в кадре (состав) и ЧТО ушло с
    /// провода (шпион). Второе отвечает на вопрос, который состав скрывает:
    /// не переигрывает ли возврат всю главу заново, таща по сети комнаты, из
    /// которых игрок давно ушёл.</para>
    /// </summary>
    public class ReturnAfterKillTests
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

        private const string Title = "проба-возврата";
        private const string Slot = "проба";

        private const string Doc = @"{""script"":[
            {""op"":""bg"",""sprite_url"":""bg/первая_комната.jpg""},
            {""op"":""actor"",""id"":""a"",""sprite_url"":""art/a.png"",""show"":true},
            {""op"":""say"",""text"":""первая""},
            {""op"":""actor"",""id"":""b"",""sprite_url"":""art/b.png"",""show"":true},
            {""op"":""say"",""text"":""вторая""},
            {""op"":""bg"",""sprite_url"":""bg/вторая_комната.jpg""},
            {""op"":""actor"",""id"":""a"",""show"":false},
            {""op"":""say"",""text"":""третья""},
            {""op"":""say"",""text"":""четвёртая""}
        ]}";

        private GameObject _go;
        private VnStage _stage;
        private UrlSpy _spy;

        private (GameObject, VnStage, UrlSpy) Сцена(string name)
        {
            var go = new GameObject(name, typeof(UIDocument));
            go.GetComponent<UIDocument>().panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            var spy = new UrlSpy();
            var stage = go.AddComponent<VnStage>();
            stage.Assets = spy;
            stage.SetSaveContext(Title, "ch", "/s.lvn");
            return (go, stage, spy);
        }

        [SetUp]
        public void SetUp() => LvnSaveStore.DeleteAll(Title);

        [TearDown]
        public void TearDown()
        {
            LvnSaveStore.DeleteAll(Title);
            if (_go != null) Object.Destroy(_go);
        }

        private List<string> Спрошено(UrlSpy spy)
        {
            lock (spy.Asked) return spy.Asked.ToList();
        }

        [UnityTest]
        public IEnumerator ПослеУбийстваПриложенияКадрСобираетсяТотЖе()
        {
            // ── жизнь первая: доигрываем до середины и сохраняемся ──────────
            var (go1, stage1, spy1) = Сцена("жизнь-первая");
            stage1.Play(Doc);
            yield return new WaitForSecondsRealtime(0.4f);
            for (int i = 0; i < 3 && !stage1.Player.AtChoice; i++)
            {
                stage1.Player.Advance();
                yield return new WaitForSecondsRealtime(0.2f);
            }

            var былоВКадре = stage1.ActorsInFrame().OrderBy(x => x).ToList();
            TestContext.WriteLine("до убийства в кадре: " + string.Join(", ", былоВКадре));
            Assert.IsTrue(stage1.SaveToSlot(Slot), "игра не смогла сохраниться — проверять нечего");
            CollectionAssert.Contains(былоВКадре, "b", "стенд встал не там, где задумано");
            CollectionAssert.DoesNotContain(былоВКадре, "a", "ушедший актёр остался в кадре ещё до убийства");

            // ── смерть: объект сцены уничтожается целиком ───────────────────
            Object.DestroyImmediate(go1);
            yield return null;

            // ── жизнь вторая: собираем с нуля и восстанавливаемся ───────────
            var (go2, stage2, spy2) = Сцена("жизнь-вторая");
            _go = go2; _stage = stage2; _spy = spy2;
            // ИМЕННО ТАК ВОЗВРАЩАЕТСЯ ИГРА. Второй довод не украшение:
            // `warmIntroSpine: false` значит «сейчас будет восстановление» —
            // вступление не проигрывается вовсе, иначе игрок успел бы увидеть
            // первую комнату главы вместо своей (NovelApp.Chronicle, ветка
            // resuming). Первая редакция этого стенда звала Play без довода и
            // объявила находкой ровно ту работу, которой игра не делает.
            stage2.Play(Doc, warmIntroSpine: false);
            yield return new WaitForSecondsRealtime(0.3f);
            Assert.IsTrue(stage2.LoadFromSlot(Slot), "слот не открылся во второй жизни");
            yield return new WaitForSecondsRealtime(0.6f);

            var сталоВКадре = stage2.ActorsInFrame().OrderBy(x => x).ToList();
            var спрошено = Спрошено(spy2);
            TestContext.WriteLine("после возврата в кадре: " + string.Join(", ", сталоВКадре));
            TestContext.WriteLine("вторая жизнь спросила: " + string.Join(", ", спрошено));

            CollectionAssert.AreEqual(былоВКадре, сталоВКадре,
                "состав кадра после возврата другой — игрок вернулся не в свою сцену");

            // Комната та, в которой игрок стоял.
            Assert.IsTrue(спрошено.Any(u => u.Contains("вторая_комната")),
                "фон текущей комнаты не запрошен — игрок вернулся на пустоту");

            // И НЕ та, из которой он давно ушёл. Проверяется здесь ДВЕ вещи
            // сразу: что возврат не проигрывает вступление (см. довод выше) и
            // что сам реплей схлопывает фоны, а не гонит их по сети чередой.
            Assert.IsFalse(спрошено.Any(u => u.Contains("первая_комната")),
                "возврат потащил комнату, из которой игрок ушёл — реплей проигрывает главу заново");

            // Ушедший актёр не воскресает: его слой не качается.
            Assert.IsFalse(спрошено.Any(u => u.Contains("art/a.png")),
                "актёр, ушедший до сохранения, вернулся на экран");
        }
    }
}
