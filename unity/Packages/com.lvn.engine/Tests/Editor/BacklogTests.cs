using System.Collections.Generic;
using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class BacklogTests
    {
        private sealed class BacklogStage : ILvnStage
        {
            public readonly List<(string who, string text, string style)> Backlog
                = new List<(string, string, string)>();

            public void ShowSay(string who, string text, string style)
                => Backlog.Add((who, text, style));
            public void ShowChoice(IReadOnlyList<LvnOption> options) { }
            public void ApplyStage(JObject command) { }
            public void OnEnd() { }
        }

        private static LvnPlayer Play(string json, out BacklogStage stage)
        {
            stage = new BacklogStage();
            return new LvnPlayer(LvnDocument.Parse(json), stage);
        }

        [Test]
        public void RecordsSayLines()
        {
            var json = @"{""script"":[
                {""op"":""say"",""who"":""Alice"",""text"":""Hello!"",""style"":""normal""},
                {""op"":""say"",""text"":""A quiet narration.""},
                {""op"":""say"",""who"":""Bob"",""text"":""Hi there."",""style"":""whisper""}
            ]}";
            var p = Play(json, out var stage);

            p.Advance();
            p.Advance();
            p.Advance();

            Assert.AreEqual(3, stage.Backlog.Count);
            Assert.AreEqual(("Alice", "Hello!", "normal"), stage.Backlog[0]);
            Assert.AreEqual(((string)null, "A quiet narration.", (string)null), stage.Backlog[1]);
            Assert.AreEqual(("Bob", "Hi there.", "whisper"), stage.Backlog[2]);
        }

        [Test]
        public void OnlyRecordsSayNotOtherOps()
        {
            var json = @"{""script"":[
                {""op"":""set"",""key"":""x"",""value"":1},
                {""op"":""say"",""text"":""only this""},
                {""op"":""inc"",""key"":""x""},
                {""op"":""say"",""text"":""and this""}
            ]}";
            var p = Play(json, out var stage);

            p.Advance();
            p.Advance();

            Assert.AreEqual(2, stage.Backlog.Count);
            Assert.AreEqual("only this", stage.Backlog[0].text);
            Assert.AreEqual("and this", stage.Backlog[1].text);
        }

        [Test]
        public void BacklogSurvivesChoice()
        {
            var json = @"{""script"":[
                {""op"":""say"",""text"":""before choice""},
                {""op"":""choice"",""options"":[
                    {""text"":""A"",""goto"":""a""}]},
                {""op"":""label"",""id"":""a""},
                {""op"":""say"",""text"":""after choice""}
            ]}";
            var p = Play(json, out var stage);

            p.Advance();
            Assert.AreEqual(1, stage.Backlog.Count);

            p.Advance();
            p.Choose(0);
            p.Advance();
            Assert.AreEqual(2, stage.Backlog.Count);
            Assert.AreEqual("after choice", stage.Backlog[1].text);
        }
    }
}
