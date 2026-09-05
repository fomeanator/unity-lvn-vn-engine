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
    /// ЧУЖОЕ ПРОХОЖДЕНИЕ НЕ ДОСТАЁТСЯ ВТОРОМУ ИГРОКУ.
    ///
    /// <para>Телефон один, аккаунтов может быть два: отдали поиграть, вошли
    /// своим Google, тестировщик переключается между учётками. Сохранения,
    /// прогресс, галерея и «прочитано» лежат на устройстве под именем новеллы —
    /// и до 05.09 только под ним. Замер на живом сервере: первый игрок
    /// сохранился, второй вошёл своим аккаунтом — и увидел чужой сейв и чужое
    /// «прочитано». Кошелёк при этом сбросился правильно: у денег такое правило
    /// было, у прохождения — нет.</para>
    ///
    /// <para>Ключи без приставки принадлежат тому, кто вошёл первым: у всех,
    /// кто уже играет, ничего не переезжает. Второй аккаунт получает своё
    /// пространство, вернувшийся первый — снова своё.</para>
    /// </summary>
    public class AccountSwitchDataTests
    {
        private const string Новелла = "стенд-смена-аккаунта";

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
            // Владелец локальных данных — состояние ПРОЦЕССА: оставь его здесь,
            // и соседний тест, который строит ключ настроек руками, полезет не
            // в тот ящик. Один раз уже полез.
            LvnKeep.NoteOwner("");
        }

        private static IEnumerator Await(Task t)
        {
            while (!t.IsCompleted) yield return null;
            if (t.IsFaulted) throw t.Exception;
        }

        [UnityTest]
        public IEnumerator ВторойАккаунтНеВидитЧужогоПрохождения()
        {
            var bin = FindServerBin();
            if (bin == null)
                Assert.Ignore("qa/bin/lvnserver-test не собран (его кладёт qa/run-all.sh) — проверка пропущена");

            var stand = Path.Combine(Path.GetTempPath(), "lvn-switch-" + Guid.NewGuid().ToString("N"));
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
                var deadline = Time.realtimeSinceStartup + 10f;
                var healthy = false;
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

                // ── первый игрок: входит и сохраняется ──────────────────────
                var reg = LvnBackend.EnsureRegisteredAsync();
                yield return Await(reg);
                string первый = LvnBackend.UserId;

                LvnSaveStore.Put(Новелла, "1", new LvnSaveSlot
                {
                    ChapterId = "глава-1", Preview = "реплика первого игрока", SavedAtUnixMs = 1,
                });
                LvnReadStore.MarkRead(Новелла, "Герой", "реплика первого игрока");
                LvnReadStore.FlushNow();

                // ── второй игрок: входит СВОИМ аккаунтом на том же устройстве ─
                var login = LvnBackend.LoginWithProviderAsync("dev", "второй-игрок-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                yield return Await(login);
                string второй = LvnBackend.UserId;
                Assert.AreNotEqual(первый, второй, "стенд: аккаунт не сменился, проверять нечего");

                var слоты = LvnSaveStore.Slots(Новелла);
                bool видитЧужойСейв = слоты != null && слоты.ContainsKey("1");
                bool видитЧужоеПрочитанное = LvnReadStore.IsRead(Новелла, "Герой", "реплика первого игрока");

                TestContext.WriteLine($"аккаунт сменился: {первый} → {второй}");
                TestContext.WriteLine($"второй игрок видит чужой сейв: {видитЧужойСейв}, "
                                    + $"чужое «прочитано»: {видитЧужоеПрочитанное}");

                Assert.IsFalse(видитЧужойСейв,
                    "второй игрок видит чужое сохранение — он продолжит чужую главу и перезапишет её слоты");
                Assert.IsFalse(видитЧужоеПрочитанное,
                    "второй игрок видит чужое «прочитано» — пропуск сочтёт знакомым текст, которого он не читал");

                // Свой сейв второй игрок пишет и видит.
                LvnSaveStore.Put(Новелла, "1", new LvnSaveSlot
                {
                    ChapterId = "глава-2", Preview = "сейв второго игрока", SavedAtUnixMs = 2,
                });
                Assert.AreEqual("сейв второго игрока", LvnSaveStore.Get(Новелла, "1")?.Preview,
                    "второй игрок не может сохраниться под своим аккаунтом");

                // Первый вернулся — и застаёт СВОЁ, не тронутое соседом.
                // Возврат идёт ЧЕРЕЗ ВЫХОД: до 06.09 его исполняла регистрация
                // при старте, то есть вход второго игрока не пережил бы и
                // перезапуска игры. Теперь смена учётки — только осознанная.
                var назад = LvnBackend.SignOutAsync();
                yield return Await(назад);
                Assert.AreEqual(первый, LvnBackend.UserId, "стенд: вернуться в первый аккаунт не вышло");
                Assert.AreEqual("реплика первого игрока", LvnSaveStore.Get(Новелла, "1")?.Preview,
                    "вернувшийся игрок не нашёл своего сохранения — его затёр сосед");
                Assert.IsTrue(LvnReadStore.IsRead(Новелла, "Герой", "реплика первого игрока"),
                    "вернувшийся игрок потерял отметки прочитанного");
            }
            finally
            {
                try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
                try { Directory.Delete(stand, true); } catch { }
            }
        }
    }
}
