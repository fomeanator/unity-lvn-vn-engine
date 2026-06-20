using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Lvn.UI;
using Lvn.UI.Screens;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    public class UiKitTests
    {
        private sealed class NoAssets : ILvnAssets
        {
            public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct) => Task.FromResult<Sprite>(null);
            public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct) => Task.FromResult<AudioClip>(null);
            public Task PreloadAsync(IReadOnlyList<string> urls, string kind, CancellationToken ct) => Task.CompletedTask;
            public void Unload(string url) { }
            public void UnloadAll() { }
        }

        [Test]
        public void UiColor_ParsesHexWithFallback()
        {
            Assert.AreEqual(Color.red, UiColor.Parse("#ff0000", Color.black));
            Assert.AreEqual(Color.black, UiColor.Parse(null, Color.black));
            Assert.AreEqual(Color.black, UiColor.Parse("not-a-color", Color.black));

            var withAlpha = UiColor.Parse("#00ff0080", Color.white);
            Assert.AreEqual(0f, withAlpha.r, 0.01f);
            Assert.AreEqual(1f, withAlpha.g, 0.01f);
            Assert.AreEqual(0.5f, withAlpha.a, 0.02f);
        }

        [Test]
        public void LoadingScreen_BuildsWithDefaultConfig()
        {
            var s = new LoadingScreen(null, new NoAssets());
            Assert.Greater(s.childCount, 0, "loader should build its element tree");
        }

        [Test]
        public void TitleCard_BuildsAndSetsText()
        {
            var card = new TitleCard(new TitleCardConfig { chapter_size = 50f }, new NoAssets());
            card.Set("Chapter 1", "The Last Guest");
            Assert.Greater(card.childCount, 0);
        }

        [Test]
        public void NameInputScreen_BuildsWithCustomConfig()
        {
            var s = new NameInputScreen(new NameInputConfig
            {
                prompt = "Your name?",
                confirm_text = "OK",
                max_length = 12,
            }, new NoAssets());
            Assert.Greater(s.childCount, 0);
        }

        [Test]
        public void ManifestUiConfig_DeserializesFromJson()
        {
            var json = @"{
                ""ui"": {
                    ""loading"": { ""scrim_opacity"": 0.4, ""tips"": [""one"",""two""] },
                    ""title"": { ""hold_seconds"": 3.0 },
                    ""name_input"": { ""prompt"": ""Name?"", ""max_length"": 16 }
                }
            }";
            var m = Newtonsoft.Json.JsonConvert.DeserializeObject<LvnManifest>(json);
            Assert.IsNotNull(m.ui);
            Assert.AreEqual(0.4f, m.ui.loading.scrim_opacity);
            Assert.AreEqual(2, m.ui.loading.tips.Length);
            Assert.AreEqual(3.0f, m.ui.title.hold_seconds);
            Assert.AreEqual(16, m.ui.name_input.max_length);
        }
    }
}
