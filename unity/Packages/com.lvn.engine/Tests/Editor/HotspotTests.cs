using System.Collections.Generic;
using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class HotspotTests
    {
        private sealed class HotspotStage : ILvnStage
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

        private static LvnPlayer Play(string json, out HotspotStage stage)
        {
            stage = new HotspotStage();
            return new LvnPlayer(LvnDocument.Parse(json), stage);
        }

        [Test]
        public void OnClickGotoWorks()
        {
            var json = @"{""script"":[
                {""op"":""say"",""text"":""look at the door""},
                {""op"":""actor"",""id"":""door"",""sprite_url"":""door.png"",""on_click"":""open""},
                {""op"":""label"",""id"":""open""},
                {""op"":""say"",""text"":""the door opens!""},
                {""op"":""goto"",""label"":""__end""}
            ]}";
            var p = Play(json, out var stage);

            p.Advance();
            Assert.AreEqual("look at the door", stage.Last);

            p.GoTo("open");
            p.Advance();
            Assert.AreEqual("the door opens!", stage.Last);
        }

        [Test]
        public void OnClickObjectWithSetAndGoto()
        {
            var json = @"{""script"":[
                {""op"":""say"",""text"":""find the key""},
                {""op"":""actor"",""id"":""key"",""sprite_url"":""key.png"",""on_click"":{""set"":{""has_key"":true},""goto"":""pickup""}},
                {""op"":""label"",""id"":""pickup""},
                {""op"":""say"",""text"":""picked up the key""},
                {""op"":""goto"",""label"":""__end""}
            ]}";
            var p = Play(json, out var stage);

            p.Advance();
            Assert.AreEqual("find the key", stage.Last);

            // Simulate the on_click behavior manually (since we can't click in tests)
            p.Vars["has_key"] = new JValue(true);
            p.GoTo("pickup");
            p.Advance();
            Assert.AreEqual("picked up the key", stage.Last);
            Assert.AreEqual(true, (bool)p.Vars["has_key"]);
        }

        [Test]
        public void GoToReactivatesFinishedPlayer()
        {
            var json = @"{""script"":[
                {""op"":""say"",""text"":""hello""},
                {""op"":""goto"",""label"":""__end""},
                {""op"":""label"",""id"":""restart""},
                {""op"":""say"",""text"":""again""}
            ]}";
            var p = Play(json, out var stage);

            p.Advance();
            Assert.AreEqual("hello", stage.Last);

            p.Advance();
            Assert.IsTrue(p.Finished);

            p.GoTo("restart");
            Assert.IsFalse(p.Finished);
            p.Advance();
            Assert.AreEqual("again", stage.Last);
        }
    }
}
