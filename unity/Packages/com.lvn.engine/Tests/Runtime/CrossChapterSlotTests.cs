using System;
using System.Collections;
using System.Threading.Tasks;
using Lvn;
using Lvn.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// <summary>
    /// СОХРАНЕНИЕ ИЗ ДРУГОЙ ГЛАВЫ НЕ ИГРАЕТСЯ В ЭТОЙ.
    ///
    /// <para>Условие с натуры: в меню сохранений лежат слоты РАЗНЫХ глав —
    /// игрок сохранялся во второй, в пятой, в развилке третьей. Он открывает
    /// список посреди седьмой и жмёт слот второй главы.</para>
    ///
    /// <para>Снимок — это позиция в СВОЁМ скрипте: номер команды, стек, стопка
    /// переменных. Подставить его в чужую главу значит перепрыгнуть на команду
    /// с тем же номером в другом тексте: реплики чужие, ветки чужие, а игра
    /// при этом не падает — просто показывает не ту историю. Худший вид
    /// поломки: выглядит как игра, а не как ошибка.</para>
    /// </summary>
    public class CrossChapterSlotTests
    {
        private GameObject _go;
        private PanelSettings _panel;
        private VnStage _stage;

        private const string Новелла = "стенд-чужой-слот";
        private const string ГлаваA = @"{""scene"":""вторая"",""script"":[
            {""op"":""say"",""text"":""во второй главе один""},
            {""op"":""say"",""text"":""во второй главе два""},
            {""op"":""say"",""text"":""во второй главе три""}]}";
        private const string ГлаваB = @"{""scene"":""седьмая"",""script"":[
            {""op"":""say"",""text"":""в седьмой главе один""},
            {""op"":""say"",""text"":""в седьмой главе два""},
            {""op"":""say"",""text"":""в седьмой главе три""}]}";

        [UnitySetUp]
        public IEnumerator Стенд()
        {
            LvnSaveStore.DeleteAll(Новелла);
            _stage = TestStage.Panel("cross-chapter-stage", out _go, out _panel);
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator Уборка()
        {
            LvnSaveStore.DeleteAll(Новелла);
            LvnScreenDirector.Current.ShowChromeAll();
            // СНАЧАЛА СНЯТЬ СЦЕНУ, ПОТОМ СНОСИТЬ ОБЪЕКТЫ. Появление стопки и
            // карточки реплики живут отложенными вызовами в расписании панели;
            // уничтоженный объект они находят уже мёртвым, и ошибка всплывает
            // в ЖУРНАЛЕ СЛЕДУЮЩЕГО теста — соседняя проверка падает на чужом
            // «restore failed: object has been destroyed». Замерено в этом же
            // прогоне: так упал тест про подменённый ответ.
            _stage?.ClearStage();
            _stage = null;
            yield return null;
            if (_go != null) UnityEngine.Object.Destroy(_go);
            if (_panel != null) UnityEngine.Object.Destroy(_panel);
            yield return null;
        }

        private static IEnumerator Ждём(Func<bool> готово, float секунд)
        {
            float срок = Time.realtimeSinceStartup + секунд;
            while (Time.realtimeSinceStartup < срок && !готово()) yield return null;
        }

        private bool ВИстории(string текст)
        {
            foreach (var строка in _stage.Backlog)
                if (строка.text == текст) return true;
            return false;
        }

        [UnityTest]
        public IEnumerator СлотЧужойГлавыНеПодставляетсяВТекущую()
        {
            // ── игрок сохранился во второй главе ────────────────────────────
            _stage.SetSaveContext(Новелла, "ch2", "/content/scripts/проба-ch02.lvn");
            _stage.Play(ГлаваA);
            yield return null;
            _stage.Player.Advance();
            yield return null;
            Assert.IsTrue(_stage.SaveToSlot("1"), "стенд: сохранение во второй главе не записалось");
            int позицияВо2 = _stage.Player.Index;

            // ── и ушёл в седьмую ────────────────────────────────────────────
            _stage.SetSaveContext(Новелла, "ch7", "/content/scripts/проба-ch07.lvn");
            _stage.Play(ГлаваB);
            yield return null;
            _stage.Player.Advance();
            yield return null;
            yield return Ждём(() => ВИстории("в седьмой главе два"), 5f);

            // ── и жмёт слот второй главы ────────────────────────────────────
            bool взяли = _stage.LoadFromSlot("1");
            TestContext.WriteLine($"слот второй главы в седьмой: LoadFromSlot={взяли}, "
                                + $"CanLoadSlot={_stage.CanLoadSlot("1")} (переходника нет)");

            Assert.IsFalse(взяли,
                "снимок другой главы подставлен в текущую — игрок увидит чужие реплики "
              + "и чужие ветки, а игра при этом не упадёт");
            Assert.IsFalse(ВИстории("во второй главе один"),
                "в истории седьмой главы появились реплики второй");
            Assert.IsFalse(_stage.CanLoadSlot("1"),
                "меню предложит слот, который загрузить нечем — жать его игрок будет впустую");

            // ── а с переходником хоста слот открывается КАК СВОЙ ────────────
            // Хост знает про манифест: находит главу по адресу скрипта, грузит
            // её и восстанавливает снимок уже в ней.
            _stage.CrossChapterLoader = слот =>
            {
                _stage.SetSaveContext(Новелла, "ch2", "/content/scripts/проба-ch02.lvn");
                _stage.Play(ГлаваA);
                return Task.FromResult(_stage.LoadFromSlot("1"));
            };
            Assert.IsTrue(_stage.CanLoadSlot("1"), "с переходником слот обязан стать доступным");

            var задача = _stage.LoadFromSlotAsync("1");
            yield return Ждём(() => задача.IsCompleted, 8f);
            Assert.IsTrue(задача.IsCompleted && задача.Result,
                "переходник хоста не смог открыть слот другой главы");
            TestContext.WriteLine($"через переходник: позиция {_stage.Player.Index} "
                                + $"(сохраняли на {позицияВо2})");
            Assert.AreEqual(позицияВо2, _stage.Player.Index,
                "игрок вернулся не на ту позицию, где сохранялся");
        }
    }
}
