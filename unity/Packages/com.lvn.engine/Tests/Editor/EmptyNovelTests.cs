using System.Collections.Generic;
using Lvn;
using Lvn.Content;
using Lvn.UI;
using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    /// <summary>
    /// НОВЕЛЛА БЕЗ ГЛАВ И ГЛАВА ИЗ ОДНОЙ РЕПЛИКИ — ЭТО НОРМАЛЬНЫЕ СОСТОЯНИЯ.
    ///
    /// <para>Так выглядит первый час нового автора: он завёл новеллу в панели,
    /// она уже в каталоге, а глав ещё нет. И так же выглядит первая проба
    /// пера — глава из одной реплики. Оба состояния попадают к игроку, потому
    /// что каталог живой: между «создал» и «написал» проходит время, и всё
    /// это время новелла видна.</para>
    ///
    /// <para>Проверяется не красота, а отсутствие вранья: пустая новелла не
    /// притворяется пройденной и не обещает продолжения, а глава из одной
    /// реплики честно кончается и считается пройденной.</para>
    /// </summary>
    public class EmptyNovelTests
    {
        private const string Пустая = "стенд-пустая";
        private const string Крошечная = "стенд-крошечная";

        private static LvnTitle Новелла(string id, params int[] номера)
        {
            var главы = new List<LvnChapter>();
            foreach (var n in номера)
                главы.Add(new LvnChapter { id = "ch" + n, number = n, name = "Глава " + n });
            return new LvnTitle
            {
                id = id, name = "Проба",
                seasons = new List<LvnSeason> { new LvnSeason { chapters = главы } },
            };
        }

        [SetUp]
        [TearDown]
        public void Чисто()
        {
            foreach (var id in new[] { Пустая, Крошечная })
                foreach (var k in new[] { "lvn_chapter_", "lvn_chapter_num_", "lvn_reached_", "lvn_reached_id_", "lvn_entry_" })
                    PlayerPrefs.DeleteKey(k + id);
            PlayerPrefs.Save();
        }

        [Test]
        public void НовеллаБезГлавНеПритворяетсяНиПройденнойНиНачатой()
        {
            var пустая = Новелла(Пустая);   // ни одной главы: автор только завёл её

            Assert.AreEqual(0, пустая.ChaptersOf().Count, "стенд: главы всё-таки есть");
            Assert.IsFalse(LvnProgress.Finished(пустая),
                "новелла без глав объявлена пройденной — игрок увидит галочку там, где читать нечего");
            Assert.IsFalse(LvnProgress.Touched(пустая), "непочатая новелла считается начатой");
            Assert.AreEqual(0, LvnProgress.Done(пустая), "в пустой новелле насчитано пройденное");
            Assert.AreEqual(0f, LvnProgress.Fraction(пустая), "полоса прогресса пустой новеллы не пуста");
            Assert.IsNull(LvnProgress.Current(пустая), "у пустой новеллы нашлась точка продолжения");

            // Метки списка глав на пустом списке — пустой список меток, не исключение.
            var метки = LvnChapterMarks.ForAll(пустая, пустая.ChaptersOf());
            Assert.AreEqual(0, метки.Count, "на пустом списке глав выдуманы метки");
        }

        [Test]
        public void ГлаваИзОднойРепликиЧестноКончаетсяИСчитаетсяПройденной()
        {
            var крошечная = Новелла(Крошечная, 1);
            var единственная = крошечная.ChaptersOf()[0];

            LvnProgress.StartChapter(крошечная, единственная);
            Assert.AreEqual(1, LvnProgress.Reached(крошечная));
            Assert.AreEqual("ch1", LvnProgress.Current(крошечная)?.id);
            Assert.IsFalse(LvnProgress.Finished(крошечная),
                "новелла считается пройденной, пока игрок в её единственной главе");

            // Дочитал: следующей главы нет — точка продолжения снимается.
            LvnProgress.FinishChapter(крошечная, null);
            Assert.IsTrue(LvnProgress.Finished(крошечная),
                "единственная глава дочитана, а новелла не считается пройденной");
            Assert.AreEqual(1, LvnProgress.Done(крошечная), "пройденных глав не 1");
            Assert.AreEqual(1f, LvnProgress.Fraction(крошечная), "полоса не полная у пройденной новеллы");

            var метки = LvnChapterMarks.ForAll(крошечная, крошечная.ChaptersOf());
            Assert.AreEqual(LvnChapterMark.Done, метки[0],
                "единственная дочитанная глава помечена не как пройденная");
        }

        // Пилот с номером 0 — законное начало (вводная новелла начинается с него).
        [Test]
        public void ГлаваСНомеромНольНеСчитаетсяПройденнойНаЧистомУстройстве()
        {
            var пилот = Новелла(Крошечная, 0);
            Assert.IsFalse(LvnProgress.Finished(пилот),
                "на чистом устройстве новелла из пилота объявлена пройденной — воронка не включится ни разу");
            var метки = LvnChapterMarks.ForAll(пилот, пилот.ChaptersOf());
            Assert.AreNotEqual(LvnChapterMark.Done, метки[0], "непрочитанный пилот помечен пройденным");
            Assert.AreNotEqual(LvnChapterMark.Locked, метки[0], "первая глава заперта — войти неоткуда");
        }
    }
}
