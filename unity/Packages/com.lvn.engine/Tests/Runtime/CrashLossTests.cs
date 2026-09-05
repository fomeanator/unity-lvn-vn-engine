using System;
using System.Collections;
using System.Text;
using Lvn;
using Lvn.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// <summary>
    /// ВНЕЗАПНОЕ ЗАКРЫТИЕ НЕ ОТМАТЫВАЕТ ГЛАВУ НАЗАД.
    ///
    /// <para>Условие с натуры, и оно частое: телефон отобрал память у фоновой
    /// игры, игрок смахнул её из списка задач, приложение упало. Ни одного из
    /// этих случаев игра не предвидит — её просто убивают, без «сохранись
    /// напоследок». Вопрос не «есть ли автосейв» (есть), а СКОЛЬКО ИМЕННО
    /// теряет игрок: пару реплик или весь вечер чтения.</para>
    ///
    /// <para>Движок обещает мерить потерю репликами: скользящий автосейв идёт
    /// каждые несколько реплик, а выбор, ввод и завершённое перетаскивание
    /// сохраняются немедленно — именно их потерять обиднее всего, потому что
    /// это решения игрока, а не прочитанный текст.</para>
    /// </summary>
    public class CrashLossTests
    {
        private GameObject _go;
        private PanelSettings _panel;
        private VnStage _stage;

        private const string Новелла = "стенд-крэш";

        private static string Глава(int реплик)
        {
            var sb = new StringBuilder();
            sb.Append(@"{""scene"":""крэш"",""script"":[");
            for (int i = 0; i < реплик; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(@"{""op"":""say"",""text"":""Реплика ").Append(i).Append(@".""}");
            }
            sb.Append(@",{""op"":""choice"",""options"":[{""text"":""налево"",""goto"":""Л""},{""text"":""направо"",""goto"":""П""}]}");
            sb.Append(@",{""op"":""label"",""id"":""Л""},{""op"":""say"",""text"":""левая ветка""},{""op"":""goto"",""label"":""КОНЕЦ""}");
            sb.Append(@",{""op"":""label"",""id"":""П""},{""op"":""say"",""text"":""правая ветка""}");
            sb.Append(@",{""op"":""label"",""id"":""КОНЕЦ""}");
            sb.Append("]}");
            return sb.ToString();
        }

        [UnitySetUp]
        public IEnumerator Стенд()
        {
            LvnSaveStore.DeleteAll(Новелла);
            _stage = TestStage.Panel("crash-loss-stage", out _go, out _panel);
            _stage.SetSaveContext(Новелла, "гл1", "scripts/гл1.lvn");
            yield return null;
        }

        [TearDown]
        public void Уборка()
        {
            LvnSaveStore.DeleteAll(Новелла);
            LvnScreenDirector.Current.ShowChromeAll();
            if (_go != null) UnityEngine.Object.Destroy(_go);
            if (_panel != null) UnityEngine.Object.Destroy(_panel);
        }

        private static IEnumerator Ждём(Func<bool> готово, float секунд)
        {
            float срок = Time.realtimeSinceStartup + секунд;
            while (Time.realtimeSinceStartup < срок && !готово()) yield return null;
        }

        // «Убили приложение» — это НЕ выход из игры: ни Flush, ни quitting не
        // случаются. Читаем ровно то, что уже лежит в книжке устройства.
        private static int СохранённаяПозиция()
        {
            var слот = LvnSaveStore.Get(Новелла, LvnSaveStore.AutoSlot);
            return слот?.Snap?.Index ?? -1;
        }

        private Button Вариант(string надпись)
        {
            var меню = _go != null ? _go.GetComponent<UIDocument>()?.rootVisualElement?.Q<ChoiceList>() : null;
            if (меню == null) return null;
            foreach (var кнопка in меню.Query<Button>().ToList())
                foreach (var подпись in кнопка.Query<Label>().ToList())
                    if (подпись.text == надпись) return кнопка;
            return null;
        }

        [UnityTest]
        public IEnumerator ПотеряОтКрэшаМеряетсяРепликами()
        {
            _stage.Play(Глава(40));
            yield return null;

            // Читаем главу так, как её читают: тап за тапом. Каждый тап —
            // одна реплика, и после каждого спрашиваем книжку устройства, что
            // в ней сейчас лежит: ровно это переживёт внезапное закрытие.
            int худшееОтставание = 0, замеров = 0;
            float срок = Time.realtimeSinceStartup + 20f;
            while (Time.realtimeSinceStartup < срок && Вариант("налево") == null)
            {
                if (_stage.Player == null || _stage.Player.Finished) break;
                if (!_stage.Player.AtChoice) _stage.Player.Advance();
                yield return null;
                int сейчас = _stage.Player?.Index ?? 0;
                int сохранено = СохранённаяПозиция();
                if (сохранено >= 0 && сейчас > 0)
                {
                    худшееОтставание = Mathf.Max(худшееОтставание, сейчас - сохранено);
                    замеров++;
                }
            }
            yield return Ждём(() => Вариант("налево") != null, 5f);
            Assert.IsNotNull(Вариант("налево"), "стенд: до развилки не дошли, мерить нечего");
            Assert.Greater(замеров, 10, "стенд: замеров слишком мало, чтобы говорить об отставании");

            // Сорок реплик прочитано. Если бы автосейва не было вовсе,
            // отставание равнялось бы всей главе.
            TestContext.WriteLine($"худшее отставание автосейва: {худшееОтставание} команд "
                                + $"при шаге автосейва {VnStage.AutosaveEveryLines} реплик ({замеров} замеров)");
            Assert.LessOrEqual(худшееОтставание, VnStage.AutosaveEveryLines * 3,
                "автосейв отстаёт больше, чем на свой шаг — внезапное закрытие отматывает главу назад");

            // ── Решение игрока не теряется вовсе ────────────────────────────
            int доВыбора = СохранённаяПозиция();
            TestStage.Press(Вариант("налево"))?.Invoke();
            yield return Ждём(() => СохранённаяПозиция() != доВыбора, 6f);
            int послеВыбора = СохранённаяПозиция();
            int позиция = _stage.Player?.Index ?? -1;
            TestContext.WriteLine($"выбор: сохранено {доВыбора} → {послеВыбора}, плеер на {позиция}");
            Assert.AreNotEqual(доВыбора, послеВыбора,
                "выбор не сохранился немедленно — крэш отменит решение игрока, и он выберет заново вслепую");
        }
    }
}
