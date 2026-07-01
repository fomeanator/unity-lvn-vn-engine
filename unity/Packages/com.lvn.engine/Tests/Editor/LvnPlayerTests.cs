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

        // Skill checks and structured costs LOCK an option (shown greyed) rather
        // than hide it; picking a locked one is ignored, an affordable cost is
        // deducted from its variable.
        [Test]
        public void StatAndCostGatesLockAndDeduct()
        {
            var json = @"{""script"":[
                {""op"":""set"",""key"":""gold"",""value"":10},
                {""op"":""choice"",""options"":[
                    {""text"":""force"",""requires_stat"":""str"",""requires_min"":5,""goto"":""done""},
                    {""text"":""buy"",""cost"":{""var"":""gold"",""amount"":4},""goto"":""done""},
                    {""text"":""poor"",""cost"":{""var"":""gold"",""amount"":999},""goto"":""done""}
                ]},
                {""op"":""label"",""id"":""done""},{""op"":""say"",""text"":""ok""}
            ]}";
            var p = Play(json, out var stage);
            p.Advance();
            Assert.AreEqual(3, stage.Options.Count, "all shown — none hidden");
            Assert.IsFalse(stage.Options[0].Enabled, "stat check unmet → locked");
            Assert.IsTrue(stage.Options[1].Enabled, "affordable → enabled");
            Assert.IsFalse(stage.Options[2].Enabled, "unaffordable → locked");

            // A locked pick is a no-op: nothing spent, still at the choice.
            p.Choose(stage.Options[0].Index);
            Assert.IsTrue(p.AtChoice, "locked pick ignored — still at choice");
            Assert.AreEqual(10d, (double)p.Vars["gold"], 0.0001);

            // The affordable paid option deducts its cost, then proceeds.
            p.Choose(stage.Options[1].Index);
            Assert.AreEqual(6d, (double)p.Vars["gold"], 0.0001);
            p.Advance();
            Assert.AreEqual("ok", stage.Last);
        }

        // hide_if_locked turns a failed *locked* gate back into a *hidden* one.
        [Test]
        public void HideIfLockedHidesFailedGate()
        {
            var json = @"{""script"":[
                {""op"":""choice"",""options"":[
                    {""text"":""secret"",""requires_stat"":""str"",""requires_min"":5,""hide_if_locked"":true,""goto"":""done""},
                    {""text"":""plain"",""goto"":""done""}
                ]},
                {""op"":""label"",""id"":""done""},{""op"":""say"",""text"":""ok""}
            ]}";
            var p = Play(json, out var stage);
            p.Advance();
            Assert.AreEqual(1, stage.Options.Count, "locked + hide_if_locked → hidden");
            Assert.AreEqual("plain", stage.Options[0].Text);
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
        public void HotSwapRejectsStructuralChange()
        {
            var v1 = @"{""script"":[
                {""op"":""say"",""text"":""a""},
                {""op"":""say"",""text"":""b""}
            ]}";
            var p = Play(v1, out _);
            p.Advance();

            // an inserted command → count differs → reject (host restarts)
            var inserted = @"{""script"":[
                {""op"":""say"",""text"":""a""},
                {""op"":""bg"",""id"":""x""},
                {""op"":""say"",""text"":""b""}
            ]}";
            Assert.IsFalse(p.TryReplaceScript(LvnDocument.Parse(inserted)));

            // same count but a changed op at index 1 → reject
            var swappedOp = @"{""script"":[
                {""op"":""say"",""text"":""a""},
                {""op"":""choice"",""options"":[{""text"":""x"",""goto"":""__end""}]}
            ]}";
            Assert.IsFalse(p.TryReplaceScript(LvnDocument.Parse(swappedOp)));
        }

        [Test]
        public void HotSwapRejectsRenamedLabel()
        {
            var v1 = @"{""script"":[
                {""op"":""say"",""text"":""a""},
                {""op"":""label"",""id"":""door""}
            ]}";
            var p = Play(v1, out _);
            p.Advance();

            var renamed = @"{""script"":[
                {""op"":""say"",""text"":""a""},
                {""op"":""label"",""id"":""window""}
            ]}";
            Assert.IsFalse(p.TryReplaceScript(LvnDocument.Parse(renamed)),
                "a renamed label is structural — jumps would break");
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
