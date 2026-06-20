using System.Collections.Generic;
using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class AssetSchedulerTests
    {
        private static LvnAssetMeta Meta(long size, bool critical, long eta = 0) =>
            new LvnAssetMeta { size = size, critical = critical, eta_ms = eta };

        [Test]
        public void OrderForDownload_PartitionsByCritical()
        {
            var set = new Dictionary<string, LvnAssetMeta>
            {
                ["/a.png"] = Meta(10, critical: true),
                ["/b.png"] = Meta(10, critical: false),
                ["/c.png"] = Meta(10, critical: true),
            };

            var (required, deferred) = AssetScheduler.OrderForDownload(set);

            Assert.AreEqual(2, required.Count);
            Assert.AreEqual(1, deferred.Count);
            Assert.AreEqual("/b.png", deferred[0].Key);
        }

        [Test]
        public void OrderForDownload_ExcludesScripts()
        {
            var set = new Dictionary<string, LvnAssetMeta>
            {
                ["/ch1.lvn"] = Meta(5, critical: true),
                ["/a.png"] = Meta(5, critical: true),
            };

            var (required, deferred) = AssetScheduler.OrderForDownload(set);

            Assert.AreEqual(1, required.Count);
            Assert.AreEqual("/a.png", required[0].Key);
            Assert.AreEqual(0, deferred.Count);
        }

        [Test]
        public void Required_SortedSmallestFirst()
        {
            var set = new Dictionary<string, LvnAssetMeta>
            {
                ["/big.png"] = Meta(2_000_000, critical: true),
                ["/mini.png"] = Meta(1_000, critical: true),
                ["/mid.png"] = Meta(500_000, critical: true),
            };

            var (required, _) = AssetScheduler.OrderForDownload(set);

            Assert.AreEqual("/mini.png", required[0].Key);
            Assert.AreEqual("/mid.png", required[1].Key);
            Assert.AreEqual("/big.png", required[2].Key);
        }

        [Test]
        public void Deferred_SortedByEtaThenSize()
        {
            var set = new Dictionary<string, LvnAssetMeta>
            {
                ["/late.png"] = Meta(10, critical: false, eta: 9000),
                ["/early.png"] = Meta(10, critical: false, eta: 100),
                ["/mid.png"] = Meta(10, critical: false, eta: 3000),
            };

            var (_, deferred) = AssetScheduler.OrderForDownload(set);

            Assert.AreEqual("/early.png", deferred[0].Key);
            Assert.AreEqual("/mid.png", deferred[1].Key);
            Assert.AreEqual("/late.png", deferred[2].Key);
        }

        [Test]
        public void OrderForDownload_DeterministicTiebreakByPath()
        {
            // Equal size + eta → stable ordinal path order, so the same set always
            // downloads in the same sequence.
            var set = new Dictionary<string, LvnAssetMeta>
            {
                ["/z.png"] = Meta(10, critical: true),
                ["/a.png"] = Meta(10, critical: true),
                ["/m.png"] = Meta(10, critical: true),
            };

            var (required, _) = AssetScheduler.OrderForDownload(set);

            Assert.AreEqual("/a.png", required[0].Key);
            Assert.AreEqual("/m.png", required[1].Key);
            Assert.AreEqual("/z.png", required[2].Key);
        }

        [Test]
        public void OrderForDownload_NullAndEmptyKeysSkipped()
        {
            var set = new Dictionary<string, LvnAssetMeta>
            {
                [""] = Meta(10, critical: true),
                ["/ok.png"] = Meta(10, critical: false),
            };

            var (required, deferred) = AssetScheduler.OrderForDownload(set);

            Assert.AreEqual(0, required.Count);
            Assert.AreEqual(1, deferred.Count);
        }

        [Test]
        public void OrderForDownload_NullSetIsEmpty()
        {
            var (required, deferred) = AssetScheduler.OrderForDownload(null);
            Assert.AreEqual(0, required.Count);
            Assert.AreEqual(0, deferred.Count);
        }
    }
}
