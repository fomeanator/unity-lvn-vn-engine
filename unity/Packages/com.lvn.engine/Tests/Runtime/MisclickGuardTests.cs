using System;
using System.Collections;
using Lvn;
using Lvn.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// <summary>
    /// РАЗВИЛКА, ПОЯВИВШАЯСЯ ПОД ПАЛЬЦЕМ, НЕ ВЫБИРАЕТ ЗА ИГРОКА.
    ///
    /// <para>Условие с натуры и очень частое: человек читает, тапая по экрану
    /// в своём ритме — тап, тап, тап. Следующий тап уже летит, когда на месте
    /// текста вырастает стопка вариантов. Если кнопка живая в тот же миг,
    /// выбор сделан пальцем, а не человеком — и в новелле это не мелочь:
    /// решения необратимы, а откат есть не везде и не всегда очевиден.</para>
    ///
    /// <para>Меряется ОКНО: сколько времени проходит от появления вариантов на
    /// экране до мгновения, когда их можно нажать. Ноль означает, что защиты
    /// нет вовсе.</para>
    /// </summary>
    public class MisclickGuardTests
    {
        private GameObject _go;
        private PanelSettings _panel;
        private VnStage _stage;

        private const string Глава = @"{
          ""scene"": ""развилка"",
          ""script"": [
            { ""op"": ""say"", ""text"": ""реплика перед выбором"" },
            { ""op"": ""choice"", ""options"": [
              { ""text"": ""налево"", ""goto"": ""Л"" },
              { ""text"": ""направо"", ""goto"": ""П"" } ] },
            { ""op"": ""label"", ""id"": ""Л"" },
            { ""op"": ""say"", ""text"": ""левая ветка"" },
            { ""op"": ""goto"", ""label"": ""КОНЕЦ"" },
            { ""op"": ""label"", ""id"": ""П"" },
            { ""op"": ""say"", ""text"": ""правая ветка"" },
            { ""op"": ""label"", ""id"": ""КОНЕЦ"" }
          ]
        }";

        [UnitySetUp]
        public IEnumerator Стенд()
        {
            _stage = TestStage.Panel("misclick-stage", out _go, out _panel);
            yield return null;
        }

        [TearDown]
        public void Уборка()
        {
            LvnScreenDirector.Current.ShowChromeAll();
            if (_go != null) UnityEngine.Object.Destroy(_go);
            if (_panel != null) UnityEngine.Object.Destroy(_panel);
        }

        private Button Вариант(string надпись)
        {
            var меню = _go != null
                ? _go.GetComponent<UIDocument>()?.rootVisualElement?.Q<ChoiceList>() : null;
            if (меню == null) return null;
            foreach (var кнопка in меню.Query<Button>().ToList())
                foreach (var подпись in кнопка.Query<Label>().ToList())
                    if (подпись.text == надпись) return кнопка;
            return null;
        }

        // Окно между «варианты видны» и «вариант можно нажать», в миллисекундах.
        private IEnumerator ОкноЗащиты(string появление, System.Action<float, bool> готово)
        {
            if (_stage.Theme != null) _stage.Theme.BoxAppear = появление;
            _stage.Play(Глава);
            yield return null;

            float срок = Time.realtimeSinceStartup + 10f;
            while (Вариант("налево") == null && Time.realtimeSinceStartup < срок) yield return null;
            var кнопка = Вариант("налево");
            if (кнопка == null) { готово(-1f, false); yield break; }

            float виден = Time.realtimeSinceStartup;
            bool сразу = кнопка.enabledInHierarchy;
            while (!кнопка.enabledInHierarchy && Time.realtimeSinceStartup < срок) yield return null;
            готово((Time.realtimeSinceStartup - виден) * 1000f, сразу);
        }

        [UnityTest]
        public IEnumerator ВариантыНеЖивыеВТотЖеМигКогдаПоявились()
        {
            float окно = -1f; bool сразу = false;
            yield return ОкноЗащиты("fade", (мс, ж) => { окно = мс; сразу = ж; });
            Assert.GreaterOrEqual(окно, 0f, "стенд: развилка не открылась, мерить нечего");
            TestContext.WriteLine($"тема с появлением «fade»: окно {окно:0} мс, живые сразу = {сразу}");

            Assert.IsFalse(сразу,
                "варианты можно нажать в тот же кадр, в котором они появились — "
              + "тап, летевший в реплику, сделает выбор за игрока");
            // Порог намеренно мягкий: проверяется НАЛИЧИЕ окна, а не его размер
            // (он зависит от темы и от скорости машины).
            Assert.Greater(окно, 30f, $"окно защиты {окно:0} мс — это меньше одного человеческого тапа");
        }

        // Тема вправе выключить анимацию появления («мгновенно»). Защита от
        // случайного тапа при этом обязана остаться: она не украшение.
        [UnityTest]
        public IEnumerator БезАнимацииПоявленияЗащитаНеИсчезает()
        {
            float окно = -1f; bool сразу = false;
            yield return ОкноЗащиты("", (мс, ж) => { окно = мс; сразу = ж; });
            Assert.GreaterOrEqual(окно, 0f, "стенд: развилка не открылась, мерить нечего");
            TestContext.WriteLine($"тема без появления: окно {окно:0} мс, живые сразу = {сразу}");

            Assert.IsFalse(сразу,
                "без анимации появления варианты живые сразу — защита от случайного тапа "
              + "оказалась побочным эффектом украшения, а не правилом");
            // Пауза взвода принадлежит движку, а не теме: замер до починки
            // давал здесь ноль. Порог с запасом вниз — планировщик UITK
            // просыпается по кадрам, и на медленной машине он может опоздать,
            // но не поторопиться.
            Assert.Greater(окно, VnStage.ChoiceArmingMs * 0.5f,
                $"окно {окно:0} мс меньше половины взвода ({VnStage.ChoiceArmingMs} мс) — "
              + "пауза не сработала");
        }
    }
}
