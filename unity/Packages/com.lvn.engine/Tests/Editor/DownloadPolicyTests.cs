using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class DownloadPolicyTests
    {
        [Test]
        public void Classify_ScriptAndAudioWinOverPath()
        {
            // script/audio extension beats any folder bucket.
            Assert.AreEqual(AssetClass.Script, DownloadPolicy.Classify("/content/ui/ch1.lvn"));
            Assert.AreEqual(AssetClass.Audio, DownloadPolicy.Classify("/content/bg/theme.ogg"));
        }

        [Test]
        public void Classify_LoadingBeatsUi()
        {
            // /loading/ must be checked before /ui/ — a loading bg is a ChapterBg.
            Assert.AreEqual(AssetClass.ChapterBg, DownloadPolicy.Classify("/content/ui/loading/ch1.png"));
            Assert.AreEqual(AssetClass.Ui, DownloadPolicy.Classify("/content/ui/frame_top.png"));
        }

        [Test]
        public void Classify_FoldersAndFallback()
        {
            Assert.AreEqual(AssetClass.Cover, DownloadPolicy.Classify("/content/covers/novel1.png"));
            Assert.AreEqual(AssetClass.Actor, DownloadPolicy.Classify("/content/actors/mara.png"));
            Assert.AreEqual(AssetClass.SceneBg, DownloadPolicy.Classify("/content/bg/porch.jpg"));
            Assert.AreEqual(AssetClass.Other, DownloadPolicy.Classify("/content/misc/x.png"));
            Assert.AreEqual(AssetClass.Other, DownloadPolicy.Classify(null));
        }

        [Test]
        public void Classify_IgnoresQueryString()
        {
            Assert.AreEqual(AssetClass.SceneBg, DownloadPolicy.Classify("/content/bg/porch.jpg?v=abc123"));
            Assert.AreEqual(AssetClass.Script, DownloadPolicy.Classify("/content/scripts/ch1.lvn?v=deadbeef"));
        }

        [Test]
        public void Kind_MapsByExtension()
        {
            Assert.AreEqual("sprite", DownloadPolicy.Kind("/a/b.png"));
            Assert.AreEqual("sprite", DownloadPolicy.Kind("/a/b.webp"));
            Assert.AreEqual("audio", DownloadPolicy.Kind("/a/b.ogg"));
            Assert.AreEqual("bin", DownloadPolicy.Kind("/a/b.lvn"));
        }

        [Test]
        public void WarmToMemory_OnlyImmediateArt()
        {
            // Warm what the player sees right away; disk-only for chapter-scoped art.
            Assert.IsTrue(DownloadPolicy.WarmToMemory(AssetClass.Ui));
            Assert.IsTrue(DownloadPolicy.WarmToMemory(AssetClass.ChapterBg));
            Assert.IsTrue(DownloadPolicy.WarmToMemory(AssetClass.Cover));
            Assert.IsFalse(DownloadPolicy.WarmToMemory(AssetClass.Actor));
            Assert.IsFalse(DownloadPolicy.WarmToMemory(AssetClass.SceneBg));
            Assert.IsFalse(DownloadPolicy.WarmToMemory(AssetClass.Audio));
        }

        [Test]
        public void NeededAtBoot_ExcludesChapterScopedArt()
        {
            Assert.IsTrue(DownloadPolicy.NeededAtBoot("/content/ui/frame.png"));
            Assert.IsTrue(DownloadPolicy.NeededAtBoot("/content/covers/n.png"));
            Assert.IsFalse(DownloadPolicy.NeededAtBoot("/content/actors/mara.png"));
            Assert.IsFalse(DownloadPolicy.NeededAtBoot("/content/bg/porch.jpg"));
        }
    }
}
