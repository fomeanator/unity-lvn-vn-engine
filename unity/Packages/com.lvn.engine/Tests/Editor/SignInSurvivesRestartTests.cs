using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using Lvn;
using Lvn.Services;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Lvn.Tests
{
    /// <summary>
    /// ВХОД ПЕРЕЖИВАЕТ ЗАКРЫТИЕ ИГРЫ.
    ///
    /// <para>Ради чего игрок вообще жмёт «войти через Google»: перенести свой
    /// прогресс и свои покупки на новый телефон. Он входит, играет, закрывает
    /// игру — и следующим запуском обязан остаться собой. Если при каждом
    /// старте игра молча представляется сервером устройством, то вход живёт
    /// ровно до закрытия, а на экране появляется чужая (пустая) учётка с
    /// чужим кошельком, и объяснить это игроку нечем.</para>
    ///
    /// <para>Отдельно — «выйти»: это ОСОЗНАННОЕ действие, после которого игра
    /// возвращается к учётке устройства. Оно обязано быть, и оно обязано быть
    /// единственным способом смены — иначе смены не отличить от сбоя.</para>
    /// </summary>
    public class SignInSurvivesRestartTests
    {
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
        public void SetUp() { _keptBase = LvnBackend.BaseUrl; }

        [TearDown]
        public void TearDown()
        {
            LvnBackend.BaseUrl = _keptBase;
            LvnKeep.NoteOwner("");
        }

        private static IEnumerator Await(Task t)
        {
            while (!t.IsCompleted) yield return null;
            if (t.IsFaulted) throw t.Exception;
        }

        [UnityTest]
        public IEnumerator ВойдяСвоимАккаунтомИгрокОстаётсяСобойПослеПерезапуска()
        {
            var bin = FindServerBin();
            if (bin == null)
                Assert.Ignore("qa/bin/lvnserver-test не собран (его кладёт qa/run-all.sh) — проверка пропущена");

            var stand = Path.Combine(Path.GetTempPath(), "lvn-signin-" + Guid.NewGuid().ToString("N"));
            var content = Path.Combine(stand, "content");
            Directory.CreateDirectory(content);
            File.WriteAllText(Path.Combine(content, "manifest.json"), "{\"titles\":[]}");

            var port = FreePort();
            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = bin,
                Arguments = $"-addr 127.0.0.1:{port} -content \"{content}\" -auth-dev",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

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

                // Первый запуск: игра представляется устройством.
                var first = LvnBackend.EnsureRegisteredAsync();
                yield return Await(first);
                string устройство = LvnBackend.UserId;
                Assert.IsNotEmpty(устройство, "стенд: устройство не завелось");

                // Игрок входит СВОИМ аккаунтом — ради своих покупок и прогресса.
                var login = LvnBackend.LoginWithProviderAsync("dev", "мой-аккаунт-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                yield return Await(login);
                string мой = LvnBackend.UserId;
                Assert.AreNotEqual(устройство, мой, "стенд: вход не сменил учётку, проверять нечего");

                // ЗАКРЫЛ И ОТКРЫЛ ИГРУ. Каждый старт зовёт то же самое.
                var restart = LvnBackend.EnsureRegisteredAsync();
                yield return Await(restart);
                TestContext.WriteLine($"устройство: {устройство}; вошёл как: {мой}; после перезапуска: {LvnBackend.UserId}");

                Assert.AreEqual(мой, LvnBackend.UserId,
                    "вход не пережил перезапуск: игра снова представилась устройством, "
                  + "и игрок остался без своих покупок и облачного прогресса");

                // «Выйти» — осознанное действие, и оно возвращает к устройству.
                var out1 = LvnBackend.SignOutAsync();
                yield return Await(out1);
                Assert.AreEqual(устройство, LvnBackend.UserId,
                    "после выхода игра не вернулась к учётке устройства");
            }
            finally
            {
                try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
                try { Directory.Delete(stand, true); } catch { }
            }
        }
    }
}
