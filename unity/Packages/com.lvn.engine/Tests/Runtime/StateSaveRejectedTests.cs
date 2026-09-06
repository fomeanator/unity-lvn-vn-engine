using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Lvn;
using Lvn.Content;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Lvn.Tests
{
    /// <summary>
    /// СЕРВЕР НЕ СМОГ СОХРАНИТЬ — ИГРОК НЕ ПОТЕРЯЛ НИЧЕГО.
    ///
    /// <para>Условие: у сервера кончился диск, упала база, он под нагрузкой —
    /// и на сохранение прогресса отвечает пятисоткой. Игрок в этот момент
    /// играет и ничего об этом не знает. Обещаний два, и оба про него: то, что
    /// он прошёл, обязано остаться у него на устройстве, а когда сервер
    /// починится — доехать туда без единого действия с его стороны.</para>
    ///
    /// <para>Худший исход тут не «не сохранилось», а «сохранилось наполовину»:
    /// клиент решил, что отправил, забыл о правках — и назавтра сервер
    /// возвращает игроку состояние недельной давности, стирая всё, что тот
    /// прошёл. Поэтому проверяется не только диск, но и ДОГОВОР: до
    /// подтверждения сервера клиент не имеет права считать правки уехавшими.
    /// </para>
    /// </summary>
    public class StateSaveRejectedTests
    {
        private const string Новелла = "стенд-отказ-сохранения";

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

        private string _журнал;

        private static Process СервёрОшибок(int port, string журнал, out string why)
        {
            why = null;
            var script = Path.Combine(RepoRoot, "qa", "slow-server.py");
            if (!File.Exists(script)) { why = "нет qa/slow-server.py"; return null; }
            try
            {
                var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "python3",
                    Arguments = $"\"{script}\" error {port} 0 0 \"{журнал}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                if (p.StandardOutput.ReadLine() == null) { why = "сервер ошибок не отозвался"; return null; }
                return p;
            }
            catch (Exception e) { why = "python3 не запустился: " + e.Message; return null; }
        }

        [SetUp]
        public void Стенд()
        {
            LvnKeep.Drop(LocalStateStore.Key(Новелла));
            LvnKeep.Drop(LocalStateStore.BaseKey(Новелла));
            // Флаг сети — состояние процесса: оставленный соседом «офлайн»
            // отменил бы отправку целиком, и проверка мерила бы тишину.
            LvnNetworkStatus.MarkOnline("проверка отказа сохранения начинается");
        }

        [TearDown]
        public void Уборка()
        {
            LvnKeep.Drop(LocalStateStore.Key(Новелла));
            LvnKeep.Drop(LocalStateStore.BaseKey(Новелла));
            LvnNetworkStatus.MarkOnline("проверка отказа сохранения закончена");
            try { if (!string.IsNullOrEmpty(_журнал) && File.Exists(_журнал)) File.Delete(_журнал); } catch { }
        }

        private static async System.Threading.Tasks.Task<long> ПробаЧерезОжидание(string url)
        {
            using var req = UnityEngine.Networking.UnityWebRequest.Put(url, "{}");
            req.timeout = 10;
            var op = req.SendWebRequest();
            await LvnNetWait.CompletedAsync(req, op, CancellationToken.None);
            return req.responseCode;
        }

        [UnityTest]
        public IEnumerator ОтказСервераНеТеряетПрогрессИгрока()
        {
            var port = FreePort();
            _журнал = Path.Combine(Path.GetTempPath(), "lvn-500-" + Guid.NewGuid().ToString("N") + ".log");
            var proc = СервёрОшибок(port, _журнал, out var why);
            if (proc == null) Assert.Ignore(why);
            try
            {
                TestContext.WriteLine($"сеть считается: {(LvnNetworkStatus.IsOnline ? "живой" : "мёртвой")}, "
                                    + $"ForceOffline={LvnNetworkStatus.ForceOffline}");
                // Пробный запрос СВОИМИ руками: если он доедет, а хранилище — нет,
                // виновато хранилище, а не сеть и не стенд.
                using (var проба = UnityEngine.Networking.UnityWebRequest.Put($"http://127.0.0.1:{port}/v1/state?user=проба", "{}"))
                {
                    проба.timeout = 5;
                    yield return проба.SendWebRequest();
                    TestContext.WriteLine($"ручной запрос: {проба.result}, код {проба.responseCode}");
                }

                // Ручной запрос УЖЕ попал в журнал — считаем прирост, а не итог:
                // иначе собственная проба стенда сойдёт за поход хранилища
                // (ровно так проверка и обманулась на прошлом прогоне).
                int доСохранения = File.Exists(_журнал) ? File.ReadAllLines(_журнал).Length : 0;

                // КЛЮЧ И ИМЯ ИГРОКА — ЛАТИНИЦЕЙ. Ключ уезжает HTTP-ЗАГОЛОВКОМ
                // (X-State-Key), а заголовки допускают только ASCII: кириллица
                // в нём роняет запрос ещё до отправки, и хранилище молча не
                // ходит на сервер вовсе. Именно это и показал журнал сервера:
                // ноль запросов при живой сети и работающей дороге. Дефект был
                // в стенде, а не в движке — в игре ключ выдаёт устройство, и он
                // шестнадцатеричный.
                var store = new HttpStateStore($"http://127.0.0.1:{port}", "player-1", "device-key-1");
                var прогресс = new JObject { ["глава"] = 3, ["дружба"] = 7, ["имя"] = "Спутник" };

                TestContext.WriteLine($"перед сохранением сеть: {(LvnNetworkStatus.IsOnline ? "живая" : "мёртвая")}");
                // ТОТ ЖЕ ПУТЬ, ЧТО У ХРАНИЛИЩА, но своими руками: запрос ждём
                // через LvnNetWait (await), а не корутиной. Если так он не
                // уходит, причина в связке ожидания с игровым циклом, а не в
                // хранилище — и это надо знать до всякого вердикта.
                var задача = ПробаЧерезОжидание($"http://127.0.0.1:{port}/v1/state?user=проба2");
                float срокПробы = Time.realtimeSinceStartup + 15f;
                while (!задача.IsCompleted && Time.realtimeSinceStartup < срокПробы) yield return null;
                int послеПробы = File.Exists(_журнал) ? File.ReadAllLines(_журнал).Length : 0;
                TestContext.WriteLine($"проба через await: завершилась={задача.IsCompleted}, "
                                    + $"код {(задача.IsCompleted ? задача.Result : -1)}, "
                                    + $"журнал {доСохранения} → {послеПробы}");
                доСохранения = послеПробы;
                // Сначала ЧТЕНИЕ: оно ходит тем же путём. Если не ходит и оно —
                // дело не в записи, а в дороге целиком.
                // ФЛАГ СЕТИ СТАВИМ ПЕРЕД КАЖДЫМ ШАГОМ, а не только в SetUp: он
                // общий на процесс, и сосед по прогону (петля восстановления
                // загрузчика) успевает объявить офлайн между приготовлением и
                // телом проверки. В общем прогоне это и случилось: сеть
                // «мёртвая», хранилище честно не пошло, замер снова мерил
                // тишину.
                LvnNetworkStatus.MarkOnline("проверка отказа: сеть жива");
                var чтение = store.LoadVarsAsync(Новелла, CancellationToken.None);
                float срокЧтения = Time.realtimeSinceStartup + 20f;
                while (!чтение.IsCompleted && Time.realtimeSinceStartup < срокЧтения) yield return null;
                int послеЧтения = File.Exists(_журнал) ? File.ReadAllLines(_журнал).Length : 0;
                TestContext.WriteLine($"чтение состояния: завершилось={чтение.IsCompleted}, "
                                    + $"запросов в журнале {доСохранения} → {послеЧтения}");
                доСохранения = послеЧтения;

                LvnNetworkStatus.MarkOnline("проверка отказа: сеть жива");
                var сохранение = store.SaveVarsAsync(Новелла, прогресс, CancellationToken.None);
                float срок = Time.realtimeSinceStartup + 20f;
                while (!сохранение.IsCompleted && Time.realtimeSinceStartup < срок) yield return null;

                // СНАЧАЛА УБЕДИТЬСЯ, ЧТО ЗАПРОС БЫЛ. Клиент шлёт состояние только
                // когда сеть считается живой; проверка, не глянувшая на журнал
                // сервера, приняла бы «мы никуда не ходили» за «сервер отказал»
                // — и вердикт был бы про несостоявшийся запрос.
                int послеСохранения = File.Exists(_журнал) ? File.ReadAllLines(_журнал).Length : 0;
                int ушло = послеСохранения - доСохранения;
                TestContext.WriteLine($"сохранение при 500: завершилось={сохранение.IsCompleted}, "
                                    + $"с исключением={сохранение.IsFaulted}, "
                                    + $"запросов от хранилища={ушло} (в журнале было {доСохранения}, стало {послеСохранения})");
                Assert.Greater(ушло, 0,
                    "хранилище не постучалось на сервер вовсе — замер про отказ ничего не значит");
                Assert.IsTrue(сохранение.IsCompleted, "сохранение зависло — игра будет ждать сервер вечно");
                Assert.IsFalse(сохранение.IsFaulted,
                    "отказ сервера прилетел игре исключением — уронит того, кто просто сохранял прогресс");

                // 1. ПРОГРЕСС НА УСТРОЙСТВЕ. Сервер отказал — значит единственная
                //    копия у игрока эта, и она обязана быть полной.
                var местная = LocalStateStore.Vars(JObject.Parse(LvnKeep.Get(LocalStateStore.Key(Новелла), "{}")));
                TestContext.WriteLine($"на устройстве после отказа: {местная}");
                Assert.AreEqual(3, (int?)местная["глава"], "прогресс не лёг на устройство");
                Assert.AreEqual(7, (int?)местная["дружба"], "часть правок потерялась");

                // 2. ДОГОВОР С СЕРВЕРОМ НЕ СЧИТАЕТСЯ ЗАКЛЮЧЁННЫМ. «База согласия» —
                //    то, что сервер подтвердил; отказ её двигать не вправе, иначе
                //    следующая синхронизация решит, что отправлять нечего.
                var база = LocalStateStore.ReadBase(Новелла);
                TestContext.WriteLine($"база согласия после отказа: {(база == null ? "нет" : база.ToString(Newtonsoft.Json.Formatting.None))}");
                Assert.IsTrue(база == null || база["глава"] == null,
                    "клиент записал отвергнутые правки как согласованные с сервером — "
                  + "при следующей синхронизации он их не отправит, и сервер вернёт старое состояние");
            }
            finally
            {
                try { if (!proc.HasExited) proc.Kill(); } catch { }
            }
        }
    }
}
