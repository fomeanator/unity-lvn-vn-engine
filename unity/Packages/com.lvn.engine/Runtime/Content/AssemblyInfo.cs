using System.Runtime.CompilerServices;

// The EditMode test assembly drives the pure planner (AssetScheduler.OrderForDownload)
// and the cache-key hash (ContentLoader.HashKey) directly, without a live download.
[assembly: InternalsVisibleTo("Lvn.Engine.Tests")]
