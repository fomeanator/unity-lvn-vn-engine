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
    public class CarouselAndHudTests
    {
        private sealed class NoAssets : ILvnAssets
        {
            public Task<Sprite> LoadSpriteAsync(string url, CancellationToken ct) => Task.FromResult<Sprite>(null);
            public Task<AudioClip> LoadAudioAsync(string url, CancellationToken ct) => Task.FromResult<AudioClip>(null);
            public Task PreloadAsync(IReadOnlyList<string> urls, string kind, CancellationToken ct) => Task.CompletedTask;
            public void Unload(string url) { }
            public void UnloadAll() { }
        }

        // ── CarouselSnap ──
        [Test]
        public void CarouselSnap_OffsetIndexRoundTrip()
        {
            var s = new CarouselSnap(stride: 100f, count: 4);
            Assert.AreEqual(0f, s.OffsetFor(0), 0.001f);
            Assert.AreEqual(200f, s.OffsetFor(2), 0.001f);
            Assert.AreEqual(2, s.IndexAt(180f));   // rounds to nearest
            Assert.AreEqual(2, s.IndexAt(220f));
        }

        [Test]
        public void CarouselSnap_ClampsAndValidates()
        {
            var s = new CarouselSnap(100f, 3);
            Assert.AreEqual(0, s.Clamp(-5));
            Assert.AreEqual(2, s.Clamp(99));
            // out-of-range index clamps to the last card before mapping to an offset
            Assert.AreEqual(200f, s.OffsetFor(99), 0.001f);
            Assert.AreEqual(0f, s.OffsetFor(-3), 0.001f);
            Assert.AreEqual(200f, s.OffsetFor(2), 0.001f);
            Assert.IsTrue(s.IsValid(1));
            Assert.IsFalse(s.IsValid(3));
        }

        [Test]
        public void CarouselSnap_EmptyIsSafe()
        {
            var s = new CarouselSnap(100f, 0);
            Assert.AreEqual(0, s.Clamp(2));
            Assert.AreEqual(0, s.IndexAt(500f));
            Assert.IsFalse(s.IsValid(0));
        }

        // ── Percent ──
        [Test]
        public void Percent_RoundsAndClamps()
        {
            Assert.AreEqual(0, Percent.Value(0, 0));
            Assert.AreEqual(50, Percent.Value(1, 2));
            Assert.AreEqual(100, Percent.Value(3, 3));
            Assert.AreEqual(100, Percent.Value(9, 3));   // clamps over 100
            Assert.AreEqual("50%", Percent.Text(1, 2));
        }

        // ── IdleCreep ──
        [Test]
        public void IdleCreep_StartsAtZeroRisesBoundedByCeiling()
        {
            Assert.AreEqual(0f, LoadingProgressModel.IdleCreepTarget(0f), 0.0001f);
            float a = LoadingProgressModel.IdleCreepTarget(1f);
            float b = LoadingProgressModel.IdleCreepTarget(5f);
            Assert.Greater(b, a);
            Assert.LessOrEqual(b, LoadingProgressModel.IdleCreepCeiling + 0.0001f);
        }

        // ── component smoke ──
        [Test]
        public void BootScreen_Builds()
        {
            var s = new BootScreen(null, new NoAssets());
            Assert.Greater(s.childCount, 0);
        }

        [Test]
        public void TitleCarousel_BuildsFromTitlesAndPlayFires()
        {
            var titles = new List<LvnTitle>
            {
                new LvnTitle { id = "a", name = "Title A", subtitle = "one" },
                new LvnTitle { id = "b", name = "Title B" },
            };
            var c = new TitleCarousel(titles, new CarouselConfig { play_text = "Go" }, new NoAssets());
            c.OnPlay += _ => { };       // subscribable
            c.OnIndexChanged += _ => { };
            Assert.Greater(c.childCount, 0);
            Assert.AreEqual("a", c.Current.id);
            Assert.AreEqual(0, c.Index);
        }

        [Test]
        public void GameHud_ProgressAndPills()
        {
            var hud = new GameHud(new HudConfig(), new NoAssets());
            hud.SetProgress(1, 2);
            hud.SetBalance("soft", 1234);
            hud.SetBalances(new Dictionary<string, long> { { "hard", 5 } });
            Assert.Greater(hud.childCount, 0);
        }

        [Test]
        public void NovelShell_FirstChapterPicksLowestNumber()
        {
            var title = new LvnTitle
            {
                id = "t",
                seasons = new List<LvnSeason>
                {
                    new LvnSeason { chapters = new List<LvnChapter>
                    {
                        new LvnChapter { id = "c2", number = 2 },
                        new LvnChapter { id = "c1", number = 1 },
                    }},
                },
            };
            Assert.AreEqual("c1", NovelShell.FirstChapter(title).id);
            Assert.IsNull(NovelShell.FirstChapter(new LvnTitle { id = "empty" }));
            Assert.IsNull(NovelShell.FirstChapter(null));
        }

        [Test]
        public void Manifest_TitleNameAndUiSlidersDeserialize()
        {
            var json = @"{
                ""titles"":[{""id"":""t1"",""name"":""Demo"",""subtitle"":""tag""}],
                ""ui"":{
                    ""boot"":{""min_seconds"":2.0,""logo_url"":""/l.png""},
                    ""carousel"":{""card_width"":0.7,""play_text"":""Start""},
                    ""hud"":{""show_progress"":false,""height"":0.05}
                }
            }";
            var m = Newtonsoft.Json.JsonConvert.DeserializeObject<LvnManifest>(json);
            Assert.AreEqual("Demo", m.titles[0].name);
            Assert.AreEqual("tag", m.titles[0].subtitle);
            Assert.AreEqual(2.0f, m.ui.boot.min_seconds);
            Assert.AreEqual(0.7f, m.ui.carousel.card_width);
            Assert.AreEqual("Start", m.ui.carousel.play_text);
            Assert.AreEqual(false, m.ui.hud.show_progress);
        }
    }
}
