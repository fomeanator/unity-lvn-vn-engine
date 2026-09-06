using System.Runtime.CompilerServices;

// The EditMode test assembly drives the pure planner (AssetScheduler.OrderForDownload)
// and the cache-key hash (ContentLoader.HashKey) directly, without a live download.
[assembly: InternalsVisibleTo("Lvn.Engine.Tests")]
// Проверка «сервер отказал в сохранении» живёт в PlayMode — только там
// UnityWebRequest действительно ходит по сети (в EditMode запрос до сервера не
// доехал вовсе, и первая редакция проверки мерила тишину). Ей нужны ключи
// локального состояния и «базы согласия»: снаружи их не видно, а именно они и
// отвечают на вопрос «что осталось у игрока».
[assembly: InternalsVisibleTo("Lvn.Engine.Tests.Runtime")]
