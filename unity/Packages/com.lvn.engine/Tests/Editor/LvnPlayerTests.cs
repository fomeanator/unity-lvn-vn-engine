using System.Collections.Generic;
using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class LvnPlayerTests
    {
        // A headless stage that records what the player drives.
        private sealed class RecordStage : ILvnStage
        {
            public readonly List<string> Lines = new List<string>();
            public IReadOnlyList<LvnOption> Options;
            public bool Ended;

            public string Last => Lines.Count > 0 ? Lines[Lines.Count - 1] : null;

            public readonly List<string> Staged = new List<string>(); // ops sent to ApplyStage

            public void ShowSay(string who, string text, string style)
                => Lines.Add(string.IsNullOrEmpty(who) ? text : who + ": " + text);
            public void ShowChoice(IReadOnlyList<LvnOption> options) => Options = options;
            public void ApplyStage(JObject command) => Staged.Add((string)command["op"]);
            public void OnEnd() => Ended = true;
        }

        private static LvnPlayer Play(string json, out RecordStage stage)
        {
            stage = new RecordStage();
            return new LvnPlayer(LvnDocument.Parse(json), stage);
        }

        [Test]
        public void LinearSayThenBranchToLabel()
        {
            var json = @"{""script"":[
                {""op"":""say"",""text"":""a""},
                {""op"":""choice"",""options"":[
                    {""text"":""x"",""goto"":""X""},
                    {""text"":""y"",""goto"":""Y""}]},
                {""op"":""label"",""id"":""X""},{""op"":""say"",""text"":""chose x""},{""op"":""goto"",""label"":""__end""},
                {""op"":""label"",""id"":""Y""},{""op"":""say"",""text"":""chose y""}
            ]}";
            var p = Play(json, out var stage);

            p.Advance();
            Assert.AreEqual("a", stage.Last);

            p.Advance();
            Assert.IsNotNull(stage.Options);
            Assert.AreEqual(2, stage.Options.Count);

            p.Choose(stage.Options[0].Index);
            p.Advance();
            Assert.AreEqual("chose x", stage.Last);

            p.Advance();
            Assert.IsTrue(p.Finished);
            Assert.IsTrue(stage.Ended);
        }

        [Test]
        public void SetAndIncDriveConditionals()
        {
            var json = @"{""script"":[
                {""op"":""set"",""key"":""courage"",""value"":0},
                {""op"":""inc"",""key"":""courage"",""by"":2},
                {""op"":""if"",""expr"":""courage >= 2"",""then"":""brave"",""else"":""timid""},
                {""op"":""label"",""id"":""timid""},{""op"":""say"",""text"":""timid""},{""op"":""goto"",""label"":""__end""},
                {""op"":""label"",""id"":""brave""},{""op"":""say"",""text"":""brave""}
            ]}";
            var p = Play(json, out var stage);
            p.Advance();
            Assert.AreEqual("brave", stage.Last);
            Assert.AreEqual(2d, (double)p.Vars["courage"], 0.0001);
        }

        // End-to-end of the once-only fix through the player: a `*` choice gated
        // by __o==0, set on pick, looping back — visible first, gone after.
        [Test]
        public void OnceOnlyOptionFiltersAfterChosen()
        {
            var json = @"{""script"":[
                {""op"":""label"",""id"":""hub""},
                {""op"":""choice"",""options"":[
                    {""text"":""once"",""expr"":""__o == 0"",""body"":[
                        {""op"":""set"",""key"":""__o"",""value"":1},
                        {""op"":""goto"",""label"":""hub""}]}
                ]}
            ]}";
            var p = Play(json, out var stage);

            p.Advance();
            Assert.AreEqual(1, stage.Options.Count, "first visit: option visible");

            p.Choose(stage.Options[0].Index);
            p.Advance();
            Assert.AreEqual(0, stage.Options.Count, "after choosing: gated out");
        }

        [Test]
        public void GoToDrivesHotspotJumps()
        {
            var json = @"{""script"":[
                {""op"":""say"",""text"":""a room with a door""},
                {""op"":""label"",""id"":""door""},{""op"":""say"",""text"":""the door opens""},{""op"":""goto"",""label"":""__end""}
            ]}";
            var p = Play(json, out var stage);
            p.Advance();
            Assert.AreEqual("a room with a door", stage.Last);

            p.GoTo("door"); // a hotspot was clicked
            p.Advance();
            Assert.AreEqual("the door opens", stage.Last);
        }

        // ── live-edit hot-swap (keep position on a non-structural edit) ──────

        [Test]
        public void HotSwapKeepsPositionOnTextEdit()
        {
            var v1 = @"{""script"":[
                {""op"":""say"",""text"":""line one""},
                {""op"":""say"",""text"":""line two""},
                {""op"":""say"",""text"":""line three""}
            ]}";
            var p = Play(v1, out var stage);
            p.Advance();            // line one
            p.Advance();            // line two — cursor now past index 1
            Assert.AreEqual("line two", stage.Last);

            // reword the current line and a future line — same structure
            var v2 = @"{""script"":[
                {""op"":""say"",""text"":""line one""},
                {""op"":""say"",""text"":""line two EDITED""},
                {""op"":""say"",""text"":""line three EDITED""}
            ]}";
            Assert.IsTrue(p.TryReplaceScript(LvnDocument.Parse(v2)));
            p.RerenderCurrent();
            Assert.AreEqual("line two EDITED", stage.Last, "current beat re-rendered with the edit");

            p.Advance();
            Assert.AreEqual("line three EDITED", stage.Last, "continues into the edited future line");
            Assert.IsFalse(p.Finished);
        }

        [Test]
        public void HotSwapReappliesEditedAnimBeforeCursor()
        {
            var v1 = @"{""script"":[
                {""op"":""anim"",""id"":""h"",""anim"":{""loop"":true,""duration"":1,""tracks"":[{""prop"":""rotation"",""keys"":[[0,4]]}]}},
                {""op"":""say"",""text"":""a""},
                {""op"":""say"",""text"":""b""}
            ]}";
            var p = Play(v1, out var stage);
            p.Advance(); // runs the anim (idx 0) then shows say "a"; cursor now past both
            stage.Staged.Clear();

            // edit ONLY the anim's amplitude — structure unchanged
            var v2 = @"{""script"":[
                {""op"":""anim"",""id"":""h"",""anim"":{""loop"":true,""duration"":1,""tracks"":[{""prop"":""rotation"",""keys"":[[0,20]]}]}},
                {""op"":""say"",""text"":""a""},
                {""op"":""say"",""text"":""b""}
            ]}";
            Assert.IsTrue(p.TryReplaceScript(LvnDocument.Parse(v2)));
            // the already-run, edited anim was re-issued to the stage (live update)
            Assert.AreEqual(1, stage.Staged.Count, "edited anim should be re-applied");
            Assert.AreEqual("anim", stage.Staged[0]);
        }

        [Test]
        public void HotSwapDoesNotReapplyUnchangedAnim()
        {
            var v = @"{""script"":[
                {""op"":""anim"",""id"":""h"",""anim"":{""loop"":true,""duration"":1,""tracks"":[{""prop"":""rotation"",""keys"":[[0,4]]}]}},
                {""op"":""say"",""text"":""a""}
            ]}";
            var p = Play(v, out var stage);
            p.Advance();
            stage.Staged.Clear();
            // same content (only a say text differs elsewhere would still skip the anim)
            Assert.IsTrue(p.TryReplaceScript(LvnDocument.Parse(v)));
            Assert.AreEqual(0, stage.Staged.Count, "unchanged anim must not be re-applied");
        }

        [Test]
        public void HotSwapKeepsPositionAcrossInsert()
        {
            // An edit that changes the command count must NOT restart the chapter:
            // the cursor re-anchors to its nearest preceding label + offset, so the
            // reader stays on the same beat even though every index shifted.
            var v1 = @"{""script"":[
                {""op"":""say"",""text"":""a""},
                {""op"":""label"",""id"":""mid""},
                {""op"":""say"",""text"":""b""},
                {""op"":""say"",""text"":""c""}
            ]}";
            var p = Play(v1, out var stage);
            p.Advance();            // "a"
            p.Advance();            // label → "b" (current beat)
            Assert.AreEqual("b", stage.Last);

            // insert a bg at the very top — count changes, indices shift by one
            var inserted = @"{""script"":[
                {""op"":""bg"",""id"":""x""},
                {""op"":""say"",""text"":""a""},
                {""op"":""label"",""id"":""mid""},
                {""op"":""say"",""text"":""b""},
                {""op"":""say"",""text"":""c""}
            ]}";
            Assert.IsTrue(p.TryReplaceScript(LvnDocument.Parse(inserted)));
            p.RerenderCurrent();
            Assert.AreEqual("b", stage.Last, "cursor re-anchored via the label — same beat, no restart");

            p.Advance();
            Assert.AreEqual("c", stage.Last, "continues into the next line, not back to the start");
            Assert.IsFalse(p.Finished);
        }

        [Test]
        public void HotSwapSurvivesRenamedLabelWithoutRestart()
        {
            // Even a renamed anchor label doesn't restart: it falls back to the raw
            // index (clamped). The contract is "never throw the player to the top".
            var v1 = @"{""script"":[
                {""op"":""say"",""text"":""a""},
                {""op"":""label"",""id"":""door""},
                {""op"":""say"",""text"":""b""}
            ]}";
            var p = Play(v1, out _);
            p.Advance();

            var renamed = @"{""script"":[
                {""op"":""say"",""text"":""a""},
                {""op"":""label"",""id"":""window""},
                {""op"":""say"",""text"":""b""}
            ]}";
            Assert.IsTrue(p.TryReplaceScript(LvnDocument.Parse(renamed)));
        }

        [Test]
        public void CallReturnTunnel()
        {
            var json = @"{""script"":[
                {""op"":""call"",""label"":""sub""},
                {""op"":""say"",""text"":""after""},
                {""op"":""goto"",""label"":""__end""},
                {""op"":""label"",""id"":""sub""},{""op"":""say"",""text"":""inside""},{""op"":""return""}
            ]}";
            var p = Play(json, out var stage);
            p.Advance();
            Assert.AreEqual("inside", stage.Last);
            p.Advance();
            Assert.AreEqual("after", stage.Last);
        }
    }
}
