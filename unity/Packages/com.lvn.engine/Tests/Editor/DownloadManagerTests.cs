using System.Collections.Generic;
using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class DownloadManagerTests
    {
        private static LvnChapter Ch(string id, int number) =>
            new LvnChapter { id = id, number = number };

        private static LvnManifest OneTitle(params LvnChapter[] chapters) =>
            new LvnManifest
            {
                titles = new List<LvnTitle>
                {
                    new LvnTitle
                    {
                        id = "t1",
                        seasons = new List<LvnSeason>
                        {
                            new LvnSeason { chapters = new List<LvnChapter>(chapters) },
                        },
                    },
                },
            };

        [Test]
        public void FindNextChapter_ReturnsSmallestNumberGreater()
        {
            var c1 = Ch("a", 1);
            var m = OneTitle(c1, Ch("b", 2), Ch("c", 3));

            var next = DownloadManager.FindNextChapter(m, c1);

            Assert.IsNotNull(next);
            Assert.AreEqual("b", next.id);
        }

        [Test]
        public void FindNextChapter_OrdersByNumberNotArrayPosition()
        {
            // Listed out of order; the chain must follow `number`.
            var c1 = Ch("a", 1);
            var m = OneTitle(Ch("c", 3), c1, Ch("b", 2));

            var next = DownloadManager.FindNextChapter(m, c1);

            Assert.AreEqual("b", next.id);
        }

        [Test]
        public void FindNextChapter_SkipsPilotNumberZero()
        {
            // A pilot (number 0) sitting out of sequence isn't the "next" of ch1.
            var c1 = Ch("a", 1);
            var m = OneTitle(Ch("pilot", 0), c1, Ch("b", 2));

            var next = DownloadManager.FindNextChapter(m, c1);

            Assert.AreEqual("b", next.id);
        }

        [Test]
        public void FindNextChapter_LastChapterReturnsNull()
        {
            var last = Ch("c", 3);
            var m = OneTitle(Ch("a", 1), Ch("b", 2), last);

            Assert.IsNull(DownloadManager.FindNextChapter(m, last));
        }

        [Test]
        public void FindNextChapter_NullSafe()
        {
            Assert.IsNull(DownloadManager.FindNextChapter(null, Ch("a", 1)));
            Assert.IsNull(DownloadManager.FindNextChapter(OneTitle(Ch("a", 1)), null));
        }

        [Test]
        public void FindNextChapter_ChapterNotInManifestReturnsNull()
        {
            var m = OneTitle(Ch("a", 1), Ch("b", 2));
            var stranger = Ch("zzz", 1);

            Assert.IsNull(DownloadManager.FindNextChapter(m, stranger));
        }
    }
}
