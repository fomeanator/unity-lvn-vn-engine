using System.Collections.Generic;
using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class SaveStoreTests
    {
        private static LvnPlayer.LvnSnapshot Snap(int index, int gold) => new LvnPlayer.LvnSnapshot
        {
            Index = index,
            Vars = new Dictionary<string, JToken> { { "gold", new JValue(gold) } },
            CallStack = new int[0],
            CommandCount = 20,
        };

        // ── migration ────────────────────────────────────────────────────────

        [Test]
        public void MigrateStampsVersionOnPreVersioningSave()
        {
            var s = Snap(3, 7); // Version defaults to 0 (a pre-versioning save)
            var m = LvnPlayer.LvnSnapshot.Migrate(s);
            Assert.IsNotNull(m);
            Assert.AreEqual(LvnPlayer.LvnSnapshot.CurrentVersion, m.Version);
            Assert.AreEqual(3, m.Index, "state preserved across migration");
        }

        [Test]
        public void MigrateRefusesFutureVersion()
        {
            var s = Snap(1, 1);
            s.Version = LvnPlayer.LvnSnapshot.CurrentVersion + 5; // save from a newer build
            Assert.IsNull(LvnPlayer.LvnSnapshot.Migrate(s), "untrusted future save → refused");
        }

        [Test]
        public void MigrateNullIsNull() => Assert.IsNull(LvnPlayer.LvnSnapshot.Migrate(null));

        // ── store round-trip + slots ─────────────────────────────────────────

        [Test]
        public void WriteReadRoundTripsAndStampsVersion()
        {
            var store = new LvnSaveStore(new MemoryKeyStore());
            store.Write("quick", Snap(5, 42), "/ch1.lvn", 1000);

            var back = store.Read("quick");
            Assert.IsNotNull(back);
            Assert.AreEqual(5, back.Index);
            Assert.AreEqual(42d, (double)back.Vars["gold"], 0.0001);
            Assert.AreEqual("/ch1.lvn", back.ScriptUrl);
            Assert.AreEqual(1000, back.SavedAtUnix);
            Assert.AreEqual(LvnPlayer.LvnSnapshot.CurrentVersion, back.Version);
        }

        [Test]
        public void ListAndDeleteManageSlots()
        {
            var store = new LvnSaveStore(new MemoryKeyStore());
            store.Write("a", Snap(1, 1), "/ch1.lvn", 10);
            store.Write("b", Snap(2, 2), "/ch2.lvn", 20);

            var list = store.List();
            Assert.AreEqual(2, list.Count);

            store.Delete("a");
            Assert.IsFalse(store.Has("a"));
            Assert.IsTrue(store.Has("b"));
            Assert.AreEqual(1, store.List().Count);
        }

        [Test]
        public void EmptySlotNameIsTheQuickSlot()
        {
            var store = new LvnSaveStore(new MemoryKeyStore());
            store.Write(null, Snap(9, 0), null, 0);
            Assert.IsTrue(store.Has("quick"));
            Assert.AreEqual(9, store.Read(null).Index);
        }

        [Test]
        public void CorruptSlotReadsAsAbsentNeverThrows()
        {
            var kv = new MemoryKeyStore();
            kv.Set("lvn_save_broken", "{ this is not json");
            var store = new LvnSaveStore(kv);
            Assert.IsNull(store.Read("broken"));
        }

        [Test]
        public void MissingSlotReadsNull()
        {
            var store = new LvnSaveStore(new MemoryKeyStore());
            Assert.IsNull(store.Read("nope"));
            Assert.IsFalse(store.Has("nope"));
        }
    }
}
