using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class ContentLoaderCacheTests
    {
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
