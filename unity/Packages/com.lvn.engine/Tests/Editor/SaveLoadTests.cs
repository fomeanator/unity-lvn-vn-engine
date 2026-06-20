using System.Collections.Generic;
using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class SaveLoadTests
    {
        private sealed class SaveStage : ILvnStage
        {
            public readonly List<string> Lines = new List<string>();
            public string Last => Lines.Count > 0 ? Lines[Lines.Count - 1] : null;
            public IReadOnlyList<LvnOption> Options;

            public void ShowSay(string who, string text, string style)
                => Lines.Add(text);
            public void ShowChoice(IReadOnlyList<LvnOption> options) => Options = options;
            public void ApplyStage(JObject command) { }
            public void OnEnd() { }
        }

        private static LvnPlayer Play(string json, out SaveStage stage)
        {
            stage = new SaveStage();
            return new LvnPlayer(LvnDocument.Parse(json), stage);
        }

        // NOTE on Advance() granularity: one Advance() runs the script up to AND
        // INCLUDING the next pausing op (say/choice) — it shows that line, then
        // pauses. So the first Advance shows the first say; the cursor afterwards
        // points at the op following it. The expectations below follow that model.

        [Test]
        public void SaveCapturesState()
        {
            var json = @"{""script"":[
                {""op"":""set"",""key"":""health"",""value"":100},
                {""op"":""say"",""text"":""checkpoint""},
                {""op"":""say"",""text"":""next line""}
            ]}";
            var p = Play(json, out var stage);

            p.Advance(); // set health=100, say "checkpoint" (cursor -> index 2)
            Assert.AreEqual("checkpoint", stage.Last);

            var snap = p.Save();
            Assert.AreEqual(2, snap.Index);
            Assert.AreEqual(100d, (double)snap.Vars["health"], 0.0001);
            Assert.IsNotNull(snap.CallStack);
        }

        [Test]
        public void RestoreResumesFromSnapshot()
        {
            var json = @"{""script"":[
                {""op"":""set"",""key"":""score"",""value"":0},
                {""op"":""inc"",""key"":""score"",""by"":10},
                {""op"":""say"",""text"":""before save""},
                {""op"":""say"",""text"":""after save""}
            ]}";
            var p = Play(json, out var stage);

            p.Advance(); // set, inc, say "before save" (cursor -> index 3)
            Assert.AreEqual("before save", stage.Last);
            var snap = p.Save();
            Assert.AreEqual(3, snap.Index);

            p.Advance(); // say "after save"
            Assert.AreEqual("after save", stage.Last);

            p.Restore(snap);
            Assert.IsFalse(p.Finished);
            Assert.AreEqual(3, p.Index);

            p.Advance(); // resumes at index 3 -> say "after save" again
            Assert.AreEqual("after save", stage.Last);
        }

        [Test]
        public void RestoreWithVarsAndCallStack()
        {
            var json = @"{""script"":[
                {""op"":""set"",""key"":""flag"",""value"":true},
                {""op"":""call"",""label"":""sub""},
                {""op"":""say"",""text"":""main""},
                {""op"":""goto"",""label"":""__end""},
                {""op"":""label"",""id"":""sub""},
                {""op"":""say"",""text"":""subroutine""},
                {""op"":""return""}
            ]}";
            var p = Play(json, out var stage);

            p.Advance(); // set flag, call sub, say "subroutine" (inside the call)
            Assert.AreEqual("subroutine", stage.Last);

            var snap = p.Save();
            Assert.AreEqual(true, (bool)snap.Vars["flag"]);
            Assert.Greater(snap.CallStack.Length, 0, "call stack should hold the return address");

            p.Advance(); // return -> say "main"
            Assert.AreEqual("main", stage.Last);

            // Restore lands back inside the subroutine with the call stack intact,
            // so the next Advance follows `return` back out to "main".
            p.Restore(snap);
            p.Advance();
            Assert.AreEqual("main", stage.Last);
        }

        [Test]
        public void MultipleSnapshotsAreIndependent()
        {
            var json = @"{""script"":[
                {""op"":""set"",""key"":""x"",""value"":1},
                {""op"":""say"",""text"":""first""},
                {""op"":""set"",""key"":""x"",""value"":2},
                {""op"":""say"",""text"":""second""}
            ]}";
            var p = Play(json, out var stage);

            p.Advance(); // set x=1, say "first"
            Assert.AreEqual("first", stage.Last);
            var snap1 = p.Save();

            p.Advance(); // set x=2, say "second"
            Assert.AreEqual("second", stage.Last);
            var snap2 = p.Save();

            p.Restore(snap1);
            Assert.AreEqual(1d, (double)p.Vars["x"], 0.0001);

            p.Restore(snap2);
            Assert.AreEqual(2d, (double)p.Vars["x"], 0.0001);
        }
    }
}
