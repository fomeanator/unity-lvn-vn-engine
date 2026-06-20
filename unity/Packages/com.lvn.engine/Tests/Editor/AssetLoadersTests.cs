using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn;
using Lvn.UI;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class AssetLoadersTests
    {
        // ── MemoryCache ─────────────────────────────────────────────────────

        [Test]
        public void MemoryCacheReturnsNullForUnknownUrl()
        {
            var cache = new MemoryCache();
            Assert.IsNull(cache.LoadSpriteAsync("nope.png", CancellationToken.None).Result);
            Assert.IsNull(cache.LoadAudioAsync("nope.wav", CancellationToken.None).Result);
        }

        [Test]
        public void MemoryCacheSeedAndRetrieve()
        {
            var cache = new MemoryCache();
            cache.Seed("test.png", UnityEngine.Sprite.Create(UnityEngine.Texture2D.whiteTexture, new UnityEngine.Rect(0, 0, 1, 1), UnityEngine.Vector2.zero));
            Assert.IsTrue(cache.HasSprite("test.png"));
            Assert.IsFalse(cache.HasSprite("other.png"));
        }

        [Test]
        public void MemoryCacheUnloadRemovesEntry()
        {
            var cache = new MemoryCache();
            cache.Seed("a.png", UnityEngine.Sprite.Create(UnityEngine.Texture2D.whiteTexture, new UnityEngine.Rect(0, 0, 1, 1), UnityEngine.Vector2.zero));
            Assert.IsTrue(cache.HasSprite("a.png"));
            cache.Unload("a.png");
            Assert.IsFalse(cache.HasSprite("a.png"));
        }

        [Test]
        public void MemoryCacheUnloadAllClearsEverything()
        {
            var cache = new MemoryCache();
            cache.Seed("a.png", UnityEngine.Sprite.Create(UnityEngine.Texture2D.whiteTexture, new UnityEngine.Rect(0, 0, 1, 1), UnityEngine.Vector2.zero));
            cache.Seed("b.wav", UnityEngine.AudioClip.Create("stub", 1, 1, 44100, false));
            cache.UnloadAll();
            Assert.IsFalse(cache.HasSprite("a.png"));
            Assert.IsFalse(cache.HasAudio("b.wav"));
        }

        [Test]
        public void MemoryCachePreloadIsNoOp()
        {
            var cache = new MemoryCache();
            cache.PreloadAsync(new[] { "a.png" }, "sprite", CancellationToken.None).Wait();
        }

        // ── ChainAssets ─────────────────────────────────────────────────────

        [Test]
        public void ChainAssetsFallsThroughToNextLoader()
        {
            var chain = new ChainAssets()
                .Add(new MemoryCache())
                .Add(new StubAssets("hit.png"));

            var result = chain.LoadSpriteAsync("hit.png", CancellationToken.None).Result;
            Assert.IsNotNull(result);
        }

        [Test]
        public void ChainAssetsReturnsNullWhenAllFail()
        {
            var chain = new ChainAssets()
                .Add(new MemoryCache())
                .Add(new MemoryCache());

            var result = chain.LoadSpriteAsync("nope.png", CancellationToken.None).Result;
            Assert.IsNull(result);
        }

        [Test]
        public void ChainAssetsUnloadCallsAllLoaders()
        {
            var stub1 = new StubAssets();
            var stub2 = new StubAssets();
            var chain = new ChainAssets().Add(stub1).Add(stub2);
            chain.Unload("test.png");
            Assert.AreEqual(1, stub1.UnloadCount);
            Assert.AreEqual(1, stub2.UnloadCount);
        }

        [Test]
        public void ChainAssetsUnloadAllCallsAllLoaders()
        {
            var stub1 = new StubAssets();
            var stub2 = new StubAssets();
            var chain = new ChainAssets().Add(stub1).Add(stub2);
            chain.UnloadAll();
            Assert.AreEqual(1, stub1.UnloadAllCount);
            Assert.AreEqual(1, stub2.UnloadAllCount);
        }

        // ── StubAssets helper ───────────────────────────────────────────────

        private sealed class StubAssets : ILvnAssets
        {
            private readonly string _hitUrl;
            public int UnloadCount;
            public int UnloadAllCount;

            public StubAssets(string hitUrl = null) => _hitUrl = hitUrl;

            public Task<UnityEngine.Sprite> LoadSpriteAsync(string url, CancellationToken ct)
            {
                if (_hitUrl != null && url == _hitUrl)
                    return Task.FromResult<UnityEngine.Sprite>(UnityEngine.Sprite.Create(UnityEngine.Texture2D.whiteTexture, new UnityEngine.Rect(0, 0, 1, 1), UnityEngine.Vector2.zero));
                return Task.FromResult<UnityEngine.Sprite>(null);
            }

            public Task<UnityEngine.AudioClip> LoadAudioAsync(string url, CancellationToken ct)
            {
                if (_hitUrl != null && url == _hitUrl)
                    return Task.FromResult(UnityEngine.AudioClip.Create("stub", 1, 1, 44100, false));
                return Task.FromResult<UnityEngine.AudioClip>(null);
            }

            public Task PreloadAsync(IReadOnlyList<string> urls, string kind, CancellationToken ct)
            {
                var tasks = new List<Task>();
                foreach (var url in urls)
                    tasks.Add(kind == "audio"
                        ? LoadAudioAsync(url, ct).ContinueWith(_ => { })
                        : LoadSpriteAsync(url, ct).ContinueWith(_ => { }));
                return Task.WhenAll(tasks);
            }

            public void Unload(string url) => UnloadCount++;
            public void UnloadAll() => UnloadAllCount++;
        }
    }
}
