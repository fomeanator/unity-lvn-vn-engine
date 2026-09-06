using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using Lvn;
using Lvn.Services;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Lvn.Tests
{
    /// <summary>
    /// ПОТРАЧЕННОЕ БЕЗ СЕТИ ДОЕЗЖАЕТ РОВНО ОДИН РАЗ.
    ///
    /// <para>Условие с натуры: игрок в метро. Он покупает наряд за мягкую
    /// валюту, игра показывает купленное — иначе офлайн бессмысленен. Через
    /// двадцать минут связь возвращается, и с этой секунды правда одна:
    /// на сервере обязано быть списано ровно столько же и ровно один раз.
    /// Ошибка в любую сторону — либо игрок платит дважды за одну вещь, либо
    /// получает её даром, и обе видны не в баге, а в отчёте по выручке.</para>
    ///
    /// <para>Локальные пути кошелька закрыты <c>WalletOfflineTests</c>; здесь
    /// проверяется СВЯЗКА с настоящим сервером: очередь, повтор и то, что
    /// после синхронизации игра и сервер называют одно и то же число.</para>
    /// </summary>
    public class WalletOfflineSyncTests
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
        public void SetUp()
        {
            _keptBase = LvnBackend.BaseUrl;
            LvnWallet.ResetLocal();
        }

        [TearDown]
        public void TearDown()
        {
            LvnWallet.ResetLocal();
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
                Arguments = $"-addr 127.0.0.1:{port} -content \"{content}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return p;
        }

        // ПОРТ ОСВОБОЖДАЕТСЯ НЕ МГНОВЕННО. Тест гасит сервер и поднимает его
        // заново на том же порту; убитый процесс успевает отдать порт не
        // всегда, и `bind` у нового падает молча — тест видел «сервер не
        // поднялся» и краснел без единого дефекта в движке (прогон 06.09).
        // Поэтому подъём настойчивый: несколько попыток с паузой.
        private static IEnumerator StartUntilHealthy(string bin, int port, string content,
                                                     System.Action<System.Diagnostics.Process, bool> got)
        {
            System.Diagnostics.Process p = null;
            bool healthy = false;
            for (int попытка = 1; попытка <= 3 && !healthy; попытка++)
            {
                p = Start(bin, port, content);
                yield return WaitHealthy(port, v => healthy = v);
                if (healthy) break;
                TestContext.WriteLine($"подъём сервера: попытка {попытка} не удалась, порт ещё занят — повторяю");
                try { if (!p.HasExited) p.Kill(); } catch { }
                yield return new WaitForSecondsRealtime(1.5f);
            }
            got(p, healthy);
        }

        private static IEnumerator WaitHealthy(int port, System.Action<bool> got)
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
            got(healthy);
        }

        // ГЛАЗАМИ СЕРВЕРА, а не игры: спрашиваем его напрямую, иначе сверять
        // было бы нечего — обе стороны показывали бы одно зеркало.
        private static IEnumerator ServerWallet(System.Action<JObject> got)
        {
            var task = LvnBackend.GetAsync("/v1/wallet");
            while (!task.IsCompleted) yield return null;
            var (code, body) = task.Result;
            if (!LvnBackend.Ok(code) || string.IsNullOrEmpty(body)) { got(null); yield break; }
            got(JObject.Parse(body));
        }

        private static long Gold(JObject wallet)
        {
            var bal = wallet? ["balances"] as JObject;
            return bal != null && bal["gold"] != null ? (long)bal["gold"] : -1;
        }

        // Сколько записей журнала СЕРВЕРА несут эту причину. Баланс сходится и
        // при задвоении, если одна из двух записей — возврат; журнал не
        // сходится никогда, поэтому считаем по нему.
        private static int Entries(JObject wallet, string reason)
        {
            int n = 0;
            if (wallet? ["history"] is JArray h)
                foreach (var e in h)
                    if ((string)e["reason"] == reason) n++;
            return n;
        }

        [UnityTest]
        public IEnumerator ПотраченноеБезСетиДоезжаетРовноОдинРаз()
        {
            var bin = FindServerBin();
            if (bin == null)
                Assert.Ignore("qa/bin/lvnserver-test не собран (его кладёт qa/run-all.sh) — проверка пропущена");

            var stand = Path.Combine(Path.GetTempPath(), "lvn-wallet-" + Guid.NewGuid().ToString("N"));
            var content = Path.Combine(stand, "content");
            Directory.CreateDirectory(content);
            File.WriteAllText(Path.Combine(content, "manifest.json"), "{\"titles\":[]}");

            var port = FreePort();
            var proc = Start(bin, port, content);
            try
            {
                bool healthy = false;
                yield return WaitHealthy(port, v => healthy = v);
                Assert.IsTrue(healthy, "локальный сервер не ответил на /healthz за 10 с");
                LvnBackend.BaseUrl = $"http://127.0.0.1:{port}";

                var reg = LvnBackend.EnsureRegisteredAsync();
                yield return Await(reg);
                Assert.IsNotEmpty(LvnBackend.UserId, "стенд: игрок не завёлся");

                // ── онлайн: сотня на счету ──────────────────────────────────
                var earn = LvnWallet.EarnAsync("gold", 100, "стенд");
                yield return Await(earn);
                JObject сервер = null;
                yield return ServerWallet(v => сервер = v);
                Assert.AreEqual(100, Gold(сервер), "стенд: начисление не доехало, проверять нечего");

                // ── связь пропала ───────────────────────────────────────────
                try { if (!proc.HasExited) proc.Kill(); } catch { }
                yield return new WaitForSecondsRealtime(0.5f);

                var spend = LvnWallet.SpendAsync("gold", 30, "магазин", "наряд:проба");
                yield return Await(spend);
                Assert.IsTrue(spend.Result, "покупка без сети обязана состояться — ради этого офлайн и делают");
                Assert.AreEqual(70, LvnWallet.Balances["gold"], "игра показывает не то, что купила");

                var earn2 = LvnWallet.EarnAsync("gold", 10, "глава");
                yield return Await(earn2);
                Assert.AreEqual(80, LvnWallet.Balances["gold"]);
                int вОчереди = LvnWallet.PendingCount;
                Assert.AreEqual(2, вОчереди, "офлайновые операции обязаны ждать в очереди");

                // ── связь вернулась ─────────────────────────────────────────
                healthy = false;
                yield return StartUntilHealthy(bin, port, content, (p, ok) => { proc = p; healthy = ok; });
                Assert.IsTrue(healthy, "сервер не поднялся обратно");

                var refresh = LvnWallet.RefreshAsync();
                yield return Await(refresh);
                yield return ServerWallet(v => сервер = v);
                TestContext.WriteLine($"после возврата связи: игра {LvnWallet.Balances["gold"]}, сервер {Gold(сервер)}, "
                                    + $"в очереди {LvnWallet.PendingCount} (было {вОчереди}), "
                                    + $"записей «магазин» в журнале сервера {Entries(сервер, "магазин")}");

                Assert.AreEqual(0, LvnWallet.PendingCount, "очередь не разошлась — операции застряли");
                Assert.AreEqual(80, Gold(сервер), "сервер посчитал не то же самое, что игра показывала офлайн");
                Assert.AreEqual(80, LvnWallet.Balances["gold"], "игра разошлась с сервером после синхронизации");
                Assert.AreEqual(1, Entries(сервер, "магазин"),
                    "покупка легла в журнал не один раз — по журналу считают выручку и делают возвраты");

                // ── повтор синхронизации ────────────────────────────────────
                // Второй Refresh подряд — обычное дело: экран открылся, игрок
                // потянул список. Ни одной новой записи он породить не вправе.
                var again = LvnWallet.RefreshAsync();
                yield return Await(again);
                yield return ServerWallet(v => сервер = v);
                Assert.AreEqual(80, Gold(сервер), "повторная синхронизация списала (или начислила) второй раз");
                Assert.AreEqual(1, Entries(сервер, "магазин"), "повторная синхронизация дописала журнал");

                // ── перезапуск игры очередь не теряет ───────────────────────
                // Очередь долговечна: питание кончилось между покупкой и сетью.
                try { if (!proc.HasExited) proc.Kill(); } catch { }
                yield return new WaitForSecondsRealtime(0.5f);
                var spend2 = LvnWallet.SpendAsync("gold", 20, "магазин", "наряд:вторая");
                yield return Await(spend2);
                Assert.AreEqual(1, LvnWallet.PendingCount);

                LvnWallet.ReloadLocal();   // «игру закрыли и открыли»
                Assert.AreEqual(1, LvnWallet.PendingCount, "очередь не пережила перезапуск игры");
                Assert.AreEqual(60, LvnWallet.Balances["gold"], "зеркало баланса не пережило перезапуск");

                healthy = false;
                yield return StartUntilHealthy(bin, port, content, (p, ok) => { proc = p; healthy = ok; });
                Assert.IsTrue(healthy, "сервер не поднялся обратно");
                var last = LvnWallet.RefreshAsync();
                yield return Await(last);
                yield return ServerWallet(v => сервер = v);
                TestContext.WriteLine($"после перезапуска игры: игра {LvnWallet.Balances["gold"]}, сервер {Gold(сервер)}, "
                                    + $"записей «магазин» {Entries(сервер, "магазин")}");
                Assert.AreEqual(60, Gold(сервер), "покупка, пережившая перезапуск, до сервера не доехала");
                Assert.AreEqual(2, Entries(сервер, "магазин"), "второй наряд лёг в журнал не один раз");
            }
            finally
            {
                try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
                try { Directory.Delete(stand, true); } catch { }
            }
        }
    }
}
