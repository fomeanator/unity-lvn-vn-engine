using Lvn.Content;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    // Field-level stat merge: on a sync conflict, the other device's doc wins by
    // default and only the keys THIS device changed since the last agreed sync
    // overlay it — so two devices touching different stats both keep progress.
    public class StateMergeTests
    {
        private static JObject J(string json) => JObject.Parse(json);

        [Test]
        public void DevicesTouchingDifferentKeysBothKeepProgress()
        {
            var baseline = J(@"{""gold"":10,""bond.mara"":1}");
            var local    = J(@"{""gold"":10,""bond.mara"":5}");  // this device raised the bond
            var server   = J(@"{""gold"":99,""bond.mara"":1}");  // the other device earned gold

            var merged = HttpStateStore.MergeVars(server, local, baseline);

            Assert.AreEqual(99, (int)merged["gold"], "the other device's gold survives");
            Assert.AreEqual(5, (int)merged["bond.mara"], "this device's bond survives");
        }

        [Test]
        public void SameKeyConflictLocalChangeWins()
        {
            var baseline = J(@"{""route"":""a""}");
            var local    = J(@"{""route"":""b""}");
            var server   = J(@"{""route"":""c""}");
            var merged = HttpStateStore.MergeVars(server, local, baseline);
            Assert.AreEqual("b", (string)merged["route"],
                "a key changed on BOTH sides keeps this device's value (it retried the PUT)");
        }

        [Test]
        public void NewKeysFromBothSidesAreKept()
        {
            var baseline = J(@"{}");
            var local    = J(@"{""seen_intro"":true}");
            var server   = J(@"{""seen_credits"":true}");
            var merged = HttpStateStore.MergeVars(server, local, baseline);
            Assert.IsTrue((bool)merged["seen_intro"]);
            Assert.IsTrue((bool)merged["seen_credits"]);
        }

        [Test]
        public void NoBaselineFallsBackToOverlayAll()
        {
            var local  = J(@"{""gold"":5}");
            var server = J(@"{""gold"":99,""extra"":1}");
            var merged = HttpStateStore.MergeVars(server, local, null);
            Assert.AreEqual(5, (int)merged["gold"], "without a baseline every local key overlays (legacy behaviour)");
            Assert.AreEqual(1, (int)merged["extra"], "server-only keys survive");
        }

        [Test]
        public void NullSidesDegradeGracefully()
        {
            Assert.AreEqual(0, HttpStateStore.MergeVars(null, null, null).Count);
            var onlyLocal = HttpStateStore.MergeVars(null, J(@"{""a"":1}"), null);
            Assert.AreEqual(1, (int)onlyLocal["a"]);
        }
    }
}
