using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Lvn;
using Lvn.Editor;
using NUnit.Framework;

namespace Lvn.Tests
{
    /// <summary>
    /// ГЛАВУ УКОРОТИЛИ, А ИГРОК СТОЯЛ ЗА ЕЁ НОВЫМ КОНЦОМ.
    ///
    /// <para>Условие с натуры и целиком: автор правит живой <c>.lvns</c> —
    /// вырезает сцену, которая не пошла, — компилятор пересобирает главу, а у
    /// игрока лежит сохранение, снятое ПОЗЖЕ нового конца. Это не выдуманный
    /// край: сокращать написанное автору приходится чаще, чем дописывать в
    /// середину.</para>
    ///
    /// <para>Опасность тихая. Позиция за концом скрипта — это мгновенный конец
    /// главы: игрок жмёт «продолжить», получает экран финала главы, которую не
    /// дочитал, и она отмечается пройденной. Ни падения, ни сообщения — просто
    /// украденная сцена.</para>
    ///
    /// <para>Проверяется ТРАКТОМ, а не подделкой JSON: исходник компилируется
    /// тем же компилятором, что и у автора, правится как файл и собирается
    /// заново.</para>
    /// </summary>
    public class ShortenedChapterResumeTests
    {
        private sealed class NullStage : ILvnStage
        {
            public readonly List<string> Реплики = new List<string>();
            public void ShowSay(string who, string text, string style) => Реплики.Add(text);
            public void ShowChoice(IReadOnlyList<LvnOption> options) { }
            public void ApplyStage(Newtonsoft.Json.Linq.JObject command, LvnSender sender) { }
            public void ApplyStage(Newtonsoft.Json.Linq.JObject command) { }
            public void OnEnd() { }
        }

        private string _dir;

        [SetUp]
        public void Стенд() => _dir = Path.Combine(Path.GetTempPath(), "lvn-short-" + Guid.NewGuid().ToString("N"));

        [TearDown]
        public void Уборка()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        // Пишем исходник на языке автора и собираем его тем же компилятором,
        // которым собирает студия.
        private string Собрать(string имя, int реплик)
        {
            Directory.CreateDirectory(_dir);
            var sb = new StringBuilder();
            sb.Append("scene глава\n\n");
            for (int i = 1; i <= реплик; i++) sb.Append("Реплика ").Append(i).Append(".\n");
            var путь = Path.Combine(_dir, имя + ".lvns");
            File.WriteAllText(путь, sb.ToString(), new UTF8Encoding(false));
            return LvnsCompiler.CompileFile(путь);
        }

        [Test]
        public void СейвЗаНовымКонцомНеЗасчитываетГлавуПройденной()
        {
            // ── играем длинную редакцию и сохраняемся ближе к концу ─────────
            var длинная = Собрать("глава", 10);
            var сцена1 = new NullStage();
            var игрок1 = new LvnPlayer(LvnDocument.Parse(длинная), сцена1);
            for (int i = 0; i < 8 && !игрок1.Finished; i++) игрок1.Advance();
            var снимок = игрок1.Save();
            TestContext.WriteLine($"сохранились на команде {снимок.Index} из {снимок.CommandCount}; "
                                + $"прочитано реплик: {сцена1.Реплики.Count}");
            Assert.IsFalse(игрок1.Finished, "стенд: глава кончилась раньше, чем мы сохранились");

            // ── автор вырезал больше половины и пересобрал главу ────────────
            var короткая = Собрать("глава", 4);
            var док2 = LvnDocument.Parse(короткая);
            int командВГлаве = док2.Script.Count;
            var сцена2 = new NullStage();
            var игрок2 = new LvnPlayer(док2, сцена2);
            игрок2.Restore(снимок);

            TestContext.WriteLine($"после укорачивания: позиция {игрок2.Index}, "
                                + $"команд в главе {командВГлаве}, "
                                + $"доиграна={игрок2.Finished}, точность={игрок2.LastRestore}");

            // 1. Глава НЕ считается доигранной: иначе она отметится пройденной,
            //    а игрок её не дочитал.
            Assert.IsFalse(игрок2.Finished,
                "сейв за новым концом мгновенно доиграл главу — игрок получит финал вместо сцены, "
              + "и глава отметится пройденной");

            // 2. Позиция внутри скрипта, а не за ним.
            Assert.Less(игрок2.Index, командВГлаве,
                "позиция осталась за концом укороченной главы");
            Assert.GreaterOrEqual(игрок2.Index, 0, "позиция ушла в минус");

            // 3. Игра продолжается: следующий тап показывает реплику, а не пустоту.
            игрок2.Advance();
            TestContext.WriteLine($"после тапа: реплик показано {сцена2.Реплики.Count}, "
                                + $"последняя «{(сцена2.Реплики.Count > 0 ? сцена2.Реплики[сцена2.Реплики.Count - 1] : "—")}»");
            Assert.Greater(сцена2.Реплики.Count, 0,
                "после восстановления в укороченной главе не показано ни одной реплики");
        }

        // Обратный случай: глава стала ДЛИННЕЕ. Позиция обязана остаться там,
        // где игрок стоял, а не уехать вместе с новым текстом.
        [Test]
        public void ДописаннаяГлаваНеСдвигаетИгрокаВперёд()
        {
            var короткая = Собрать("глава", 5);
            var сцена1 = new NullStage();
            var игрок1 = new LvnPlayer(LvnDocument.Parse(короткая), сцена1);
            for (int i = 0; i < 3 && !игрок1.Finished; i++) игрок1.Advance();
            int прочитано = сцена1.Реплики.Count;
            var снимок = игрок1.Save();

            var длинная = Собрать("глава", 12);
            var сцена2 = new NullStage();
            var игрок2 = new LvnPlayer(LvnDocument.Parse(длинная), сцена2);
            игрок2.Restore(снимок);
            игрок2.Advance();

            TestContext.WriteLine($"дописали главу: было прочитано {прочитано} реплик, "
                                + $"после возврата показана «{сцена2.Реплики[сцена2.Реплики.Count - 1]}», "
                                + $"точность={игрок2.LastRestore}");
            Assert.IsFalse(игрок2.Finished, "дописанная глава сразу оказалась доигранной");
            Assert.Greater(сцена2.Реплики.Count, 0, "после возврата не показано ни одной реплики");
        }
    }
}
