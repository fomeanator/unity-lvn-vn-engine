using System;
using System.Collections;
using System.IO;
using System.Linq;
using Lvn;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Lvn.Tests
{
    /// <summary>
    /// ПЕРВЫЙ ЗАПУСК БЕЗ СЕТИ — НЕ ТУПИК И НЕ ПУСТОЙ ЭКРАН.
    ///
    /// <para>Условие с натуры: человек поставил игру и открыл её в самолёте
    /// (или дома, когда лёг сервер). Кэша нет — качать было нечего и некогда,
    /// это ПЕРВЫЙ запуск. Игре нечего показать, и это честно; нечестно было бы
    /// показать пустую витрину «игр нет» (человек решит, что здесь пусто) или
    /// повесить чёрный экран без единого слова.</para>
    ///
    /// <para>Обещание из двух половин. Первая: игрок видит объяснение и видит,
    /// что игра продолжает пытаться. Вторая, более важная: когда сеть появится,
    /// игра поднимется САМА — без перезапуска, без «убей и открой заново».
    /// Именно вторую половину проверить труднее всего, и именно её отсутствие
    /// стоило бы установки.</para>
    /// </summary>
    public class OfflineFirstRunTests
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

        // КЭШ МАНИФЕСТА — ОДИН НА ПРИЛОЖЕНИЕ, а не на адрес сервера (ключ живёт
        // в NovelApp.cs). Для игры это верно: она ходит на один сервер. Для
        // проверки «первого запуска» — беда: кэш, оставленный соседним тестом,
        // превращает замер в фикцию, и первая редакция стенда именно на этом и
        // споткнулась (оболочка построилась без сервера). Убираем на время
        // замера и возвращаем, как было.
        private const string КлючКэшаМанифеста = "lvn_manifest_cache";
        private string _кэш;

        [SetUp]
        public void ЗабытьЧужойКэш()
        {
            _кэш = LvnKeep.Get(КлючКэшаМанифеста, null);
            LvnKeep.Drop(КлючКэшаМанифеста);
        }

        [TearDown]
        public void ВернутьКэш()
        {
            if (_кэш != null) LvnKeep.Put(КлючКэшаМанифеста, _кэш);
            // ФЛАГ СЕТИ — СОСТОЯНИЕ ПРОЦЕССА, а не этого теста. Оставленный
            // «мёртвым», он валит соседей ещё до провода: их запросы падают на
            // «offline (global status)», не дойдя до сокета. Замерено в том же
            // прогоне — сосед про самолечение кэша упал именно так.
            LvnNetworkStatus.MarkOnline("проверка первого запуска закончена");
        }

        // Весь видимый текст приложения — из всех живых панелей UITK.
        private static string ТекстНаЭкране()
        {
            var buf = new System.Text.StringBuilder();
            foreach (var doc in UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
            {
                var root = doc != null ? doc.rootVisualElement : null;
                if (root == null) continue;
                foreach (var label in root.Query<Label>().ToList())
                    if (!string.IsNullOrEmpty(label.text)) buf.Append(label.text).Append('\n');
            }
            return buf.ToString();
        }

        [UnityTest]
        public IEnumerator ПервыйЗапускБезСетиОбъясняетИПоднимаетсяСамКогдаСетьПоявилась()
        {
            var bin = FindServerBin();
            if (bin == null)
                Assert.Ignore("qa/bin/lvnserver-test не собран (его кладёт qa/run-all.sh) — проверка пропущена");

            var stand = Path.Combine(Path.GetTempPath(), "lvn-firstrun-" + Guid.NewGuid().ToString("N"));
            var content = Path.Combine(stand, "content");
            Directory.CreateDirectory(content);
            File.WriteAllText(Path.Combine(content, "manifest.json"),
                "{\"titles\":[{\"id\":\"проба\",\"name\":\"Проба\",\"seasons\":[{\"chapters\":[]}]}]}");

            // Порт СВОЙ и сервер на нём НЕ ПОДНЯТ: кэш ключуется адресом, значит
            // этот запуск для игры действительно первый — чужого кэша нет.
            var port = FreePort();
            string shellBuilt = null;
            void OnLog(string cond, string stack, LogType type)
            {
                if (cond != null && cond.Contains("shell built")) shellBuilt = cond;
            }
            Application.logMessageReceived += OnLog;

            var go = new GameObject("NovelApp-first-run-offline");
            System.Diagnostics.Process proc = null;
            try
            {
                var app = go.AddComponent<Lvn.UI.Screens.NovelApp>();
                app.ServerUrl = $"http://127.0.0.1:{port}";
                app.SyncInterval = 0f;

                // ── В САМОЛЁТЕ ──────────────────────────────────────────────
                var срок = Time.realtimeSinceStartup + 14f;
                string объяснение = null;
                while (Time.realtimeSinceStartup < срок)
                {
                    var текст = ТекстНаЭкране();
                    if (объяснение == null && текст.IndexOf("reconnect", StringComparison.OrdinalIgnoreCase) >= 0)
                        объяснение = текст.Split('\n').First(l =>
                            l.IndexOf("reconnect", StringComparison.OrdinalIgnoreCase) >= 0);
                    yield return null;
                }

                TestContext.WriteLine($"без сети: объяснение на экране = {объяснение ?? "НЕТ"}; "
                                    + $"оболочка построена = {shellBuilt != null}; "
                                    + $"сеть считается {(LvnNetworkStatus.IsOnline ? "живой" : "мёртвой")}");

                Assert.IsNull(shellBuilt,
                    "стенд: оболочка построилась без сервера — значит кэш манифеста не вычищен "
                  + "(ключ мог переехать из NovelApp.cs), и замер недействителен");
                Assert.IsNotNull(объяснение,
                    "первый запуск без сети не объяснил игроку НИЧЕГО: ни слова о соединении на экране");

                // ── САМОЛЁТ СЕЛ ─────────────────────────────────────────────
                proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = bin,
                    Arguments = $"-addr 127.0.0.1:{port} -content \"{content}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                float появился = Time.realtimeSinceStartup;

                var ждём = Time.realtimeSinceStartup + 40f;
                while (shellBuilt == null && Time.realtimeSinceStartup < ждём) yield return null;
                float поднялась = Time.realtimeSinceStartup - появился;

                TestContext.WriteLine($"сеть появилась → оболочка за {поднялась:0.0} с "
                                    + $"(без единого перезапуска)");
                Assert.IsNotNull(shellBuilt,
                    "сеть появилась, а игра осталась на вуали — игрок обязан был бы убить приложение и открыть заново");
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
                UnityEngine.Object.Destroy(go);
                try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
                try { Directory.Delete(stand, true); } catch { }
            }
        }
    }
}
