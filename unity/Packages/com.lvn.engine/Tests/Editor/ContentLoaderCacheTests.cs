using System.IO;
using System.Linq;
using System.Text;
using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class ContentLoaderCacheTests
    {
        [Test]
        public void AtomicWrite_WritesContentAndLeavesNoTemp()
        {
            var dir = Path.Combine(Path.GetTempPath(), "lvn-atomic-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var path = Path.Combine(dir, "cache.bin");
                ContentLoader.AtomicWriteAllBytes(path, Encoding.UTF8.GetBytes("hello"));
                Assert.AreEqual("hello", File.ReadAllText(path));

                // Overwrite must replace the content, not append or fail.
                ContentLoader.AtomicWriteAllBytes(path, Encoding.UTF8.GetBytes("world!!"));
                Assert.AreEqual("world!!", File.ReadAllText(path));

                // No staging temp files may be left behind.
                var leftovers = Directory.GetFiles(dir).Where(f => f.Contains(".tmp-")).ToArray();
                CollectionAssert.IsEmpty(leftovers, "atomic write left a temp file behind");
            }
            finally { Directory.Delete(dir, true); }
        }

        [Test]
        public void PickEvictions_OldestFirstUntilUnderBudget()
        {
            const long MB = 1 << 20;
            var entries = new System.Collections.Generic.List<(string, long, long, float)>
            {
                ("old-a", 10 * MB, 1, 0f),
                ("old-b", 10 * MB, 2, 0f),
                ("newer", 10 * MB, 3, 0f),
            };
            // Budget 15MB, total 30MB, all past grace → evict the two oldest.
            var evict = ContentLoader.PickEvictions(entries, 15 * MB, 1000f, 60f);
            CollectionAssert.AreEqual(new[] { "old-a", "old-b" }, evict);
        }

        [Test]
        public void PickEvictions_GraceProtectsRecentlyUsed()
        {
            const long MB = 1 << 20;
            var entries = new System.Collections.Generic.List<(string, long, long, float)>
            {
                ("visible-bg", 20 * MB, 1, 995f), // requested 5s ago — on screen
                ("stale",      20 * MB, 2, 0f),
            };
            var evict = ContentLoader.PickEvictions(entries, 25 * MB, 1000f, 60f);
            CollectionAssert.AreEqual(new[] { "stale" }, evict,
                "recently-requested art is never evicted, even if it's the oldest by sequence");
        }

        [Test]
        public void PickEvictions_UnderBudgetEvictsNothing()
        {
            const long MB = 1 << 20;
            var entries = new System.Collections.Generic.List<(string, long, long, float)>
            {
                ("a", 5 * MB, 1, 0f), ("b", 5 * MB, 2, 0f),
            };
            CollectionAssert.IsEmpty(ContentLoader.PickEvictions(entries, 100 * MB, 1000f, 60f));
        }

        [Test]
        public void Sha256Matches_AcceptsCorrectRejectsWrong()
        {
            var data = System.Text.Encoding.UTF8.GetBytes("hello");
            const string good = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";
            Assert.IsTrue(ContentLoader.Sha256Matches(data, good));
            Assert.IsTrue(ContentLoader.Sha256Matches(data, good.ToUpperInvariant()), "hex case-insensitive");
            Assert.IsFalse(ContentLoader.Sha256Matches(data, good.Replace('2', '3')));
            Assert.IsFalse(ContentLoader.Sha256Matches(data, "deadbeef"), "wrong length rejected");
            Assert.IsFalse(ContentLoader.Sha256Matches(null, good));
            Assert.IsFalse(ContentLoader.Sha256Matches(data, null));
        }

        [Test]
        public void HashKey_IsDeterministic()
        {
            var a = ContentLoader.HashKey("/content/bg/porch.jpg", null);
            var b = ContentLoader.HashKey("/content/bg/porch.jpg", null);
            Assert.AreEqual(a, b);
        }

        [Test]
        public void HashKey_IsSha1Hex()
        {
            var key = ContentLoader.HashKey("/content/bg/porch.jpg", null);
            Assert.AreEqual(40, key.Length);            // sha1 = 20 bytes = 40 hex chars
            StringAssert.IsMatch("^[0-9a-f]+$", key);
        }

        [Test]
        public void HashKey_VersionChangesKey()
        {
            // The whole point of cache-busting: a new content version → a new key
            // → a fresh cache file, leaving the old one as an offline fallback.
            var unversioned = ContentLoader.HashKey("/content/bg/porch.jpg", null);
            var v1 = ContentLoader.HashKey("/content/bg/porch.jpg", "aaaa1111");
            var v2 = ContentLoader.HashKey("/content/bg/porch.jpg", "bbbb2222");

            Assert.AreNotEqual(unversioned, v1);
            Assert.AreNotEqual(v1, v2);
        }

        [Test]
        public void HashKey_DifferentUrlsDiffer()
        {
            var a = ContentLoader.HashKey("/content/bg/a.jpg", "v1");
            var b = ContentLoader.HashKey("/content/bg/b.jpg", "v1");
            Assert.AreNotEqual(a, b);
        }
    }
}
