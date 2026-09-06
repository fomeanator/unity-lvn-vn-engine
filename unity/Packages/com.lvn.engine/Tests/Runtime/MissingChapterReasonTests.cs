using System;
using System.Collections;
using System.IO;
using System.Threading;
using Lvn;
using Lvn.Content;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Lvn.Tests
{
    /// <summary>
    /// ПОЧЕМУ ГЛАВА НЕ ОТКРЫЛАСЬ — ВОПРОС ИГРОКА, А НЕ НАШ.
    ///
    /// <para>Условие: автор завёл главу в каталоге, а файл ещё не выложил (или
    /// удалил его, переименовав). Манифест на неё уже ссылается, карточка на
    /// витрине, игрок жмёт «играть» — при исправной сети.</para>
    ///
    /// <para>Изнутри «нет сети» и «главы нет на сервере» выглядят одинаково:
    /// скачать не удалось. Снаружи это разные миры. В первом человек идёт
    /// проверять вайфай, во втором проверять нечего — виноват автор, и
    /// единственное верное действие игрока это подождать. Сообщение обязано
    /// называть причину, иначе оно отправляет чинить исправное.</para>
    ///
    /// <para>Проверяется различение ПО ПРОВОДУ: живой сервер отдаёт 404 на
    /// отсутствующий скрипт, и загрузчик обязан отличить это от обрыва.</para>
    /// </summary>
    public class MissingChapterReasonTests
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

        [TearDown]
        public void Уборка() => LvnNetworkStatus.MarkOnline("проверка причины закончена");

        [UnityTest]
        public IEnumerator ОтсутствиеГлавыОтличаетсяОтОбрываСети()
        {
            var bin = FindServerBin();
            if (bin == null)
                Assert.Ignore("qa/bin/lvnserver-test не собран (его кладёт qa/run-all.sh) — проверка пропущена");

            var stand = Path.Combine(Path.GetTempPath(), "lvn-missing-" + Guid.NewGuid().ToString("N"));
            var content = Path.Combine(stand, "content");
            Directory.CreateDirectory(Path.Combine(content, "scripts"));
            File.WriteAllText(Path.Combine(content, "manifest.json"), "{\"titles\":[]}");
            // Одна глава есть, второй нет — автор её ещё не выложил.
            File.WriteAllText(Path.Combine(content, "scripts", "есть.lvn"),
                "{\"scene\":\"г\",\"script\":[{\"op\":\"say\",\"text\":\"я на месте\"}]}");

            var port = FreePort();
            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = bin,
                Arguments = $"-addr 127.0.0.1:{port} -content \"{content}\"",
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
                LvnNetworkStatus.MarkOnline("стенд: сеть жива");

                var loader = new ContentLoader($"http://127.0.0.1:{port}",
                    Path.Combine(stand, "cache"));

                // ── глава на месте: скачивается ─────────────────────────────
                // Флаг сети ставим ПЕРЕД КАЖДЫМ запросом: он общий на процесс,
                // и соседи по прогону (их петли восстановления) успевают
                // объявить офлайн между шагами. Тогда загрузчик отказывает ДО
                // провода с кодом «network», и проверка мерит не 404, а чужой
                // флаг — ровно это и случилось в общем прогоне 06.09, хотя в
                // одиночку тест был зелёным.
                LvnNetworkStatus.MarkOnline("стенд: сеть жива");
                var есть = loader.DownloadScriptText("/content/scripts/есть.lvn");
                while (!есть.IsCompleted) yield return null;
                Assert.IsFalse(есть.IsFaulted, "существующая глава не скачалась — стенд сломан");

                // ── главы нет: 404, а НЕ обрыв ──────────────────────────────
                string код = null, вид = null;
                LvnNetworkStatus.MarkOnline("стенд: сеть жива");
                var нет = loader.DownloadScriptText("/content/scripts/нет-такой.lvn");
                while (!нет.IsCompleted) yield return null;
                if (нет.IsFaulted)
                    foreach (var e in нет.Exception.Flatten().InnerExceptions)
                        if (e is LvnFetchException f) { код = f.Code; вид = f.GetType().Name; }

                TestContext.WriteLine($"главы нет на сервере: исключение {вид}, код «{код}», "
                                    + $"сеть после запроса считается {(LvnNetworkStatus.IsOnline ? "живой" : "мёртвой")}");

                Assert.IsNotNull(код, "отсутствие главы не пришло опознаваемой ошибкой");
                StringAssert.StartsWith("http_4", код,
                    "отсутствие главы неотличимо от обрыва связи — игроку скажут «проверьте сеть», "
                  + "и он пойдёт чинить исправное");
                // ФЛАГ СЕТИ ЗДЕСЬ НЕ СУДЬЯ. В общем прогоне его треплет посторонний
                // фон: отправщик клиентских логов стучится на адрес, которого в
                // стенде нет, и честно объявляет офлайн между нашими шагами
                // («[lvn-net] offline: services POST /v1/log/client» в выводе).
                // Проверять надо не флаг, а ПОСЛЕДСТВИЕ: после 404 загрузка не
                // выключилась — соседняя глава по-прежнему приходит.
                LvnNetworkStatus.MarkOnline("стенд: сеть жива");
                var снова = loader.DownloadScriptText("/content/scripts/есть.lvn");
                while (!снова.IsCompleted) yield return null;
                TestContext.WriteLine($"после 404 соседняя глава: {(снова.IsFaulted ? "НЕ пришла" : "пришла")}");
                Assert.IsFalse(снова.IsFaulted,
                    "после одной ненайденной главы перестала грузиться существующая — "
                  + "404 выключил загрузку целиком");
            }
            finally
            {
                try { if (!proc.HasExited) proc.Kill(); } catch { }
                try { Directory.Delete(stand, true); } catch { }
            }
        }
    }
}
