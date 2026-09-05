using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using Lvn;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.TestTools;

namespace Lvn.Tests
{
    /// <summary>
    /// МЕДЛЕННАЯ СЕТЬ — НЕ ЧЁРНЫЙ ЭКРАН, А МЁРТВАЯ — НЕ ВЕЧНОЕ ОЖИДАНИЕ.
    ///
    /// <para>Настоящая мобильная сеть редко «пропадает». Она тормозит: сокет
    /// открыт, ответ идёт, но идёт по капле или замирает на середине. Это два
    /// РАЗНЫХ случая, и путать их дорого в обе стороны. Оборвать медленную, но
    /// живую передачу — значит не дать игроку скачать фон на слабой соте и
    /// устроить бурю «сеть пропала — сеть вернулась» на всю сессию (живой
    /// случай на эмуляторе: «Request timeout» каждые несколько секунд). Ждать
    /// замершую — значит показать чёрный экран и ждать вечно.</para>
    ///
    /// <para>Правило движка: срок считается НЕ с начала запроса, а с последнего
    /// пришедшего байта. Здесь оно и проверяется — на настоящих сокетах, а не
    /// на подменённом таймере.</para>
    /// </summary>
    public class SlowNetworkTests
    {
        private static string RepoRoot => Path.GetFullPath(
            Path.Combine(Application.dataPath, "..", "..", ".."));

        private static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start();
            var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        // Медленный сервер живёт отдельным файлом (qa/slow-server.py): его
        // зовут и стенды, и этот тест, и «медленная сеть» описана в одном месте.
        private static System.Diagnostics.Process StartSlow(string mode, int port, out string why)
        {
            why = null;
            var script = Path.Combine(RepoRoot, "qa", "slow-server.py");
            if (!File.Exists(script)) { why = "нет qa/slow-server.py"; return null; }
            try
            {
                var p = System.Diagnostics.Process.Start(new ProcessStartInfo
                {
                    FileName = "python3",
                    Arguments = $"\"{script}\" {mode} {port}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                // Ждём СЛОВО «готов», а не спим наугад: иначе первый запрос
                // упирался бы в ещё не открытый порт и тест мерил бы не то.
                var ready = p.StandardOutput.ReadLine();
                if (ready == null) { why = "медленный сервер не отозвался"; return null; }
                return p;
            }
            catch (Exception e) { why = "python3 не запустился: " + e.Message; return null; }
        }

        private bool _keptForce;

        [SetUp]
        public void SetUp() { _keptForce = LvnNetworkStatus.ForceOffline; }

        [TearDown]
        public void TearDown() { LvnNetworkStatus.ForceOffline = _keptForce; LvnNetworkStatus.MarkOnline("тест закончен"); }

        // Медленно, но ЖИВО: байты идут кусочками с паузами, каждая из которых
        // короче срока. Такую передачу обрывать нельзя.
        [UnityTest]
        public IEnumerator МедленнаяНоЖиваяПередачаНеОбрывается()
        {
            var port = FreePort();
            var proc = StartSlow("drip", port, out var why);
            if (proc == null) Assert.Ignore(why);
            try
            {
                using var req = UnityWebRequest.Get($"http://127.0.0.1:{port}/большой-фон.jpg");
                req.timeout = 0; // срок держит страж замирания, а не общий таймер
                var op = req.SendWebRequest();
                var часы = Stopwatch.StartNew();
                // Срок в две секунды при паузах по 0,4 — если бы он считался с
                // НАЧАЛА запроса, передача оборвалась бы на третьей секунде.
                var wait = LvnNetWait.AwaitAsync(req, op, default, stallSeconds: 2);
                while (!wait.IsCompleted) yield return null;
                часы.Stop();

                long байт = req.downloadHandler?.data?.Length ?? 0;
                TestContext.WriteLine($"капельница: {байт} байт за {часы.Elapsed.TotalSeconds:0.0} с, "
                                    + $"результат {req.result}");
                Assert.AreEqual(4096, байт,
                    "медленная, но живая передача оборвана — на слабой сети игрок не скачает ни одного фона");
                Assert.Greater(часы.Elapsed.TotalSeconds, 2.0,
                    "стенд: сервер отдал всё мгновенно, срок проверить было нечем");
            }
            finally { try { if (!proc.HasExited) proc.Kill(); } catch { } }
        }

        // Замершая передача: ответ начался и встал. Ждать её нельзя.
        [UnityTest]
        public IEnumerator ЗамершаяПередачаОбрываетсяВСрок()
        {
            var port = FreePort();
            var proc = StartSlow("stall", port, out var why);
            if (proc == null) Assert.Ignore(why);
            try
            {
                using var req = UnityWebRequest.Get($"http://127.0.0.1:{port}/большой-фон.jpg");
                req.timeout = 0;
                var op = req.SendWebRequest();
                var часы = Stopwatch.StartNew();
                var wait = LvnNetWait.AwaitAsync(req, op, default, stallSeconds: 2);
                while (!wait.IsCompleted) yield return null;
                часы.Stop();

                TestContext.WriteLine($"замерший: оборвано за {часы.Elapsed.TotalSeconds:0.0} с, "
                                    + $"результат {req.result}");
                Assert.Less(часы.Elapsed.TotalSeconds, 6.0,
                    "замершую передачу ждали дольше срока — игрок смотрит в чёрный экран");
                Assert.Greater(часы.Elapsed.TotalSeconds, 1.5,
                    "оборвано раньше срока — так обрывалась бы и живая медленная передача");
                Assert.AreNotEqual(UnityWebRequest.Result.Success, req.result,
                    "замершая передача объявлена успешной");
            }
            finally { try { if (!proc.HasExited) proc.Kill(); } catch { } }
        }

        // Сервер принял соединение и молчит вовсе. Короткий запрос (вход,
        // кошелёк, манифест) обязан сдаться по своему сроку и объявить офлайн —
        // тогда игра сползает на кэш, а не висит.
        [UnityTest]
        public IEnumerator МолчащийСерверСдаётсяПоСрокуИОбъявляетОфлайн()
        {
            var port = FreePort();
            var proc = StartSlow("silent", port, out var why);
            if (proc == null) Assert.Ignore(why);
            try
            {
                LvnNetworkStatus.MarkOnline("перед проверкой");
                using var req = UnityWebRequest.Get($"http://127.0.0.1:{port}/v1/content/manifest");
                req.timeout = LvnNetPatience.RequestSeconds;
                var op = req.SendWebRequest();
                var часы = Stopwatch.StartNew();
                var wait = LvnNetWait.CompletedAsync(req, op, default);
                while (!wait.IsCompleted) yield return null;
                часы.Stop();

                bool дошло = req.result == UnityWebRequest.Result.Success || req.responseCode != 0;
                if (!дошло) LvnNetworkStatus.MarkOffline("молчащий сервер");
                TestContext.WriteLine($"молчун: сдались за {часы.Elapsed.TotalSeconds:0.0} с "
                                    + $"(срок {LvnNetPatience.RequestSeconds} с), результат {req.result}, "
                                    + $"сеть считается {(LvnNetworkStatus.IsOnline ? "живой" : "мёртвой")}");
                Assert.IsFalse(дошло, "молчащий сервер сошёл за живой");
                Assert.Less(часы.Elapsed.TotalSeconds, LvnNetPatience.RequestSeconds + 4.0,
                    "ждали дольше собственного срока — это и есть чёрный экран на буте");
                Assert.IsTrue(LvnNetworkStatus.IsOffline,
                    "сеть не объявлена мёртвой — игра будет ходить на молчащий сервер снова и снова");
            }
            finally { try { if (!proc.HasExited) proc.Kill(); } catch { } }
        }
    }
}
