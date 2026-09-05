using System.Collections.Generic;
using System.Text;
using Lvn.Content;
using Lvn.UI;
using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    /// <summary>
    /// ВСТАВЛЕННАЯ ГЛАВА НЕ ПЕРЕПИСЫВАЕТ ЧУЖОЕ ПРОХОЖДЕНИЕ.
    ///
    /// <para>Условие с натуры: сезон уже вышел, игроки его читают, и автор
    /// дописывает эпизод В СЕРЕДИНУ — между вторым и третьим. Так работает
    /// живой сериал, и так работает импорт: номера всех последующих глав
    /// съезжают на единицу.</para>
    ///
    /// <para>У игрока в этот момент записано «докуда дошёл» — числом. Значит
    /// вопрос: что он увидит в списке глав наутро. Ошибка тут не роняет игру,
    /// она хуже — она врёт молча: помеченная «пройденной» глава, которой игрок
    /// не читал, просто не будет открыта никогда.</para>
    /// </summary>
    public class ChapterInsertProgressTests
    {
        private const string TitleId = "стенд-вставка";

        private static LvnTitle Title(params (string id, int number)[] главы)
        {
            var chapters = new List<LvnChapter>();
            foreach (var (id, number) in главы)
                chapters.Add(new LvnChapter { id = id, number = number, name = "Глава " + number });
            return new LvnTitle
            {
                id = TitleId,
                name = "Проба",
                seasons = new List<LvnSeason> { new LvnSeason { chapters = chapters } },
            };
        }

        [SetUp]
        [TearDown]
        public void Чисто()
        {
            foreach (var k in new[] { "lvn_chapter_", "lvn_chapter_num_", "lvn_reached_", "lvn_entry_" })
                PlayerPrefs.DeleteKey(k + TitleId);
            PlayerPrefs.Save();
        }

        private static string Метки(LvnTitle title)
        {
            var главы = title.ChaptersOf();
            var метки = LvnChapterMarks.ForAll(title, главы);
            var sb = new StringBuilder();
            for (int i = 0; i < главы.Count; i++)
                sb.Append(главы[i].id).Append('(').Append(главы[i].number).Append(")=")
                  .Append(метки[i]).Append(i + 1 < главы.Count ? ", " : "");
            return sb.ToString();
        }

        [Test]
        public void ВставкаГлавыВСерединуНеВрётПроПройденное()
        {
            // Игрок прочитал четыре главы и стоит в пятой.
            var было = Title(("ch1", 1), ("ch2", 2), ("ch3", 3), ("ch4", 4), ("ch5", 5));
            var пятая = было.ChaptersOf()[4];
            LvnProgress.StartChapter(было, пятая);
            Assert.AreEqual(5, LvnProgress.Reached(было), "стенд: игрок не дошёл до пятой");
            TestContext.WriteLine("до вставки:    " + Метки(было));

            // Автор дописал эпизод между второй и третьей — номера съехали.
            var стало = Title(("ch1", 1), ("ch2", 2), ("новая", 3), ("ch3", 4), ("ch4", 5), ("ch5", 6));
            TestContext.WriteLine("после вставки: " + Метки(стало));
            TestContext.WriteLine($"докуда дошёл: {LvnProgress.Reached(стало)}, "
                                + $"где стоит: {LvnProgress.Current(стало)?.id ?? "нигде"}");

            var главы = стало.ChaptersOf();
            var метки = LvnChapterMarks.ForAll(стало, главы);
            var поId = new Dictionary<string, LvnChapterMark>();
            for (int i = 0; i < главы.Count; i++) поId[главы[i].id] = метки[i];

            // 1. Игрок остался там, где стоял: это держит id, а не номер.
            Assert.AreEqual("ch5", LvnProgress.Current(стало)?.id,
                "после вставки игрок потерял место, на котором остановился");
            Assert.AreEqual(LvnChapterMark.Current, поId["ch5"],
                "глава, в которой игрок стоит, перестала быть текущей");

            // 2. Прочитанные главы остаются прочитанными — все четыре.
            foreach (var id in new[] { "ch1", "ch2", "ch3", "ch4" })
                Assert.AreEqual(LvnChapterMark.Done, поId[id],
                    $"глава {id} прочитана игроком, а список показывает её как «{поId[id]}»");

            // 3. Дописанный эпизод открыт: игрок прошёл дальше него.
            Assert.AreNotEqual(LvnChapterMark.Locked, поId["новая"],
                "дописанный эпизод закрыт от игрока, который прошёл дальше него");

            // 4. ГРАНИЦА, КОТОРУЮ ЭТОТ ДОМ ЗАКРЫТЬ НЕ МОЖЕТ, и она записана
            //    здесь честно, а не спрятана. «Пройдено» вычисляется из ОДНОГО
            //    числа-потолка: всё, что ниже него по номеру, считается
            //    прочитанным. Дописанный в середину эпизод попадает под это
            //    правило и получает галочку, которой не заслужил, — а к
            //    пройденному игрок не возвращается.
            //
            //    Починить это числом нельзя в принципе: нужно помнить
            //    пройденные главы ПОИМЁННО (реестр id + посев для тех, кто уже
            //    играет, + поле в облачном свёртке). Работа не на одну ночь, и
            //    до неё проверка фиксирует НЫНЕШНЕЕ поведение, чтобы починка
            //    была видна как изменение, а не как случайность.
            Assert.AreEqual(LvnChapterMark.Done, поId["новая"],
                "поведение изменилось: если дописанный эпизод перестал считаться "
              + "пройденным — обещание закрыто целиком, перепишите этот блок и канон");
        }
    }
}
