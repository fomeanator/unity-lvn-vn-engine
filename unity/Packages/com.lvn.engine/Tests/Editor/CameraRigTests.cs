using Lvn;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class CameraRigTests
    {
        [Test]
        public void StagingOpsIncludesNewEffects()
        {
            Assert.IsTrue(StagingOps.Known.Contains("flash"));
            Assert.IsTrue(StagingOps.Known.Contains("tint"));
            Assert.IsTrue(StagingOps.Known.Contains("blur"));
            Assert.IsTrue(StagingOps.Known.Contains("text_pace"));
        }

        [Test]
        public void StagingOpsStillHasOriginalOps()
        {
            Assert.IsTrue(StagingOps.Known.Contains("say"));
            Assert.IsTrue(StagingOps.Known.Contains("choice"));
            Assert.IsTrue(StagingOps.Known.Contains("bg"));
            Assert.IsTrue(StagingOps.Known.Contains("actor"));
            Assert.IsTrue(StagingOps.Known.Contains("fade"));
            Assert.IsTrue(StagingOps.Known.Contains("dim"));
            Assert.IsTrue(StagingOps.Known.Contains("camera"));
            Assert.IsTrue(StagingOps.Known.Contains("wait"));
            Assert.IsTrue(StagingOps.Known.Contains("preload"));
        }

        [Test]
        public void StagingOpsCountIsCorrect()
        {
            Assert.AreEqual(24, StagingOps.Known.Count);
        }
    }
}
