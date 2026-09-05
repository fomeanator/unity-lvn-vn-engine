using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using Lvn;
using Lvn.Services;
using Lvn.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Lvn.Tests
{
    /// <summary>
    /// СЕРВЕР ПЕРЕСТАЛ УЗНАВАТЬ ИГРОКА — ЕГО ИГРА ОСТАЁТСЯ ЕГО.
    ///
    /// <para>Условие не выдумано: базу восстановили из вчерашнего снимка, диск
    /// переставили, службу подняли заново с чистого места. Устройство при этом
    /// то же самое, игра на нём та же, сохранения лежат на диске телефона — но
    /// сервер видит устройство ВПЕРВЫЕ и выдаёт ему НОВЫЙ номер игрока.</para>
    ///
    /// <para>Номер игрока — это владелец локальных ключей: сохранения,
    /// прогресс, галерея, «прочитано». Сменился номер — и человек открывает
    /// игру, в которой он никогда не играл, хотя все его данные целы в
    /// сантиметре от него. Деньги — другое дело: кошелёк живёт на сервере, и
    /// его сброс здесь честен. Прохождение живёт на устройстве.</para>
    /// </summary>
    public class ServerForgotPlayerTests
    {
        private const string Новелла = "стенд-сервер-забыл";

        private static string RepoRoot => Path.GetFullPath(
            Path.Combine(Application.dataPath, "..", "..", ".."));

        private static string FindServerBin()
        {
            var env = Environment.GetEnvironmentVariable("LVN_SERVER_BIN");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
            var built = Path.Combine(RepoRoot, "qa", "bin", "lvnserver-test");
            return File.Exists(built) ? built : null;
        }

        private static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start();
            var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private string _keptBase;

        [SetUp]
        public void SetUp()
        {
            _keptBase = LvnBackend.BaseUrl;
            LvnSaveStore.DeleteAll(Новелла);
        }

        [TearDown]
        public void TearDown()
        {
            LvnSaveStore.DeleteAll(Новелла);
            LvnBackend.BaseUrl = _keptBase;
            LvnKeep.NoteOwner("");
        }

        private static IEnumerator Await(Task t)
        {
            while (!t.IsCompleted) yield return null;
            if (t.IsFaulted) throw t.Exception;
        }

        private static System.Diagnostics.Process Start(string bin, int port, string content)
        {
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = bin,
                Arguments = $"-addr 127.0.0.1:{port} -content \"{content}\" -auth-dev",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return p;
        }

        [UnityTest]
        public IEnumerator ПотерявСвоюБазуСерверНеОтнимаетПрохождение()
        {
            var bin = FindServerBin();
            if (bin == null)
                Assert.Ignore("qa/bin/lvnserver-test не собран (его кладёт qa/run-all.sh) — проверка пропущена");

            var stand = Path.Combine(Path.GetTempPath(), "lvn-forgot-" + Guid.NewGuid().ToString("N"));
            var content = Path.Combine(stand, "content");
            Directory.CreateDirectory(content);
            File.WriteAllText(Path.Combine(content, "manifest.json"), "{\"titles\":[]}");

            var port = FreePort();
            var proc = Start(bin, port, content);
            try
            {
                var healthy = false;
                var deadline = Time.realtimeSinceStartup + 10f;
                while (!healthy && Time.realtimeSinceStartup < deadline)
                {
                    using (var probe = UnityEngine.Networking.UnityWebRequest.Get($"http://127.0.0.1:{port}/healthz"))
                    {
                        probe.timeout = 2;
                        yield return probe.SendWebRequest();
                        healthy = probe.result == UnityEngine.Networking.UnityWebRequest.Result.Success;
                    }
                }
                Assert.IsTrue(healthy, "локальный сервер не ответил на /healthz за 10 с");
                LvnBackend.BaseUrl = $"http://127.0.0.1:{port}";

                // ── игрок играет ────────────────────────────────────────────
                var reg = LvnBackend.EnsureRegisteredAsync();
                yield return Await(reg);
                string был = LvnBackend.UserId;
                Assert.IsNotEmpty(был, "стенд: игрок не завёлся");

                LvnSaveStore.Put(Новелла, "1", new LvnSaveSlot
                {
                    ChapterId = "глава-3", Preview = "три часа игры", SavedAtUnixMs = 1,
                });
                LvnReadStore.MarkRead(Новелла, "Герой", "три часа игры");
                LvnReadStore.FlushNow();

                // ── сервер поднимают заново, БЕЗ базы учёток ────────────────
                // Ровно то, что происходит при развороте из старого снимка или
                // переезде на новый диск: контент на месте, аккаунтов нет.
                try { if (!proc.HasExited) proc.Kill(); } catch { }
                yield return new WaitForSecondsRealtime(0.6f);
                var services = Path.Combine(content, "services");
                if (Directory.Exists(services))
                    foreach (var f in Directory.GetFiles(services, "lvn.db*"))
                        File.Delete(f);

                proc = Start(bin, port, content);
                healthy = false;
                deadline = Time.realtimeSinceStartup + 10f;
                while (!healthy && Time.realtimeSinceStartup < deadline)
                {
                    using (var probe = UnityEngine.Networking.UnityWebRequest.Get($"http://127.0.0.1:{port}/healthz"))
                    {
                        probe.timeout = 2;
                        yield return probe.SendWebRequest();
                        healthy = probe.result == UnityEngine.Networking.UnityWebRequest.Result.Success;
                    }
                }
                Assert.IsTrue(healthy, "сервер не поднялся после потери базы");

                // ── игрок открывает игру снова ─────────────────────────────
                var again = LvnBackend.EnsureRegisteredAsync();
                yield return Await(again);
                string стал = LvnBackend.UserId;
                TestContext.WriteLine($"номер игрока: {был} → {стал}");
                Assert.AreNotEqual(был, стал, "стенд: сервер узнал игрока — условие не воспроизвелось");

                var слот = LvnSaveStore.Get(Новелла, "1");
                bool видитПрочитанное = LvnReadStore.IsRead(Новелла, "Герой", "три часа игры");
                TestContext.WriteLine($"своё сохранение видно: {слот != null}, «прочитано» видно: {видитПрочитанное}");

                Assert.IsNotNull(слот,
                    "сервер потерял свою базу — и игрок потерял ТРИ ЧАСА ИГРЫ, лежащие тут же на телефоне");
                Assert.AreEqual("три часа игры", слот.Preview, "сохранение подменилось чужим");
                Assert.IsTrue(видитПрочитанное,
                    "отметки прочитанного стали чужими — пропуск снова спросит про знакомый текст");
            }
            finally
            {
                try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
                try { Directory.Delete(stand, true); } catch { }
            }
        }
    }
}
