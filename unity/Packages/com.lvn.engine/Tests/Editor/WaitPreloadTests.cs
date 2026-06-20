using System.Collections.Generic;
using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class WaitPreloadTests
    {
        private sealed class WaitStage : ILvnStage
        {
            public readonly List<string> Lines = new List<string>();
            public string Last => Lines.Count > 0 ? Lines[Lines.Count - 1] : null;
            public readonly List<JObject> ApplyStageOps = new List<JObject>();

            public void ShowSay(string who, string text, string style)
                => Lines.Add(text);
            public void ShowChoice(IReadOnlyList<LvnOption> options) { }
            public void ApplyStage(JObject command)
            {
                ApplyStageOps.Add(command);
            }
            public void OnEnd() { }
        }

        private static LvnPlayer Play(string json, out WaitStage stage)
        {
            stage = new WaitStage();
            return new LvnPlayer(LvnDocument.Parse(json), stage);
        }

        [Test]
        public void WaitPausesExecution()
        {
            var json = @"{""script"":[
                {""op"":""say"",""text"":""before wait""},
                {""op"":""wait"",""ms"":500},
                {""op"":""say"",""text"":""after wait""}
            ]}";
            var p = Play(json, out var stage);

            p.Advance();
            Assert.AreEqual("before wait", stage.Last);

            p.Advance();
            Assert.AreEqual(1, stage.ApplyStageOps.Count, "wait is forwarded to stage");
            Assert.AreEqual("wait", (string)stage.ApplyStageOps[0]["op"]);
            Assert.AreEqual("before wait", stage.Last, "execution should pause at wait");

            p.Advance();
            Assert.AreEqual("after wait", stage.Last);
        }

        [Test]
        public void WaitPassesMsField()
        {
            var json = @"{""script"":[
                {""op"":""wait"",""ms"":2000}
            ]}";
            var p = Play(json, out var stage);

            p.Advance();
            Assert.AreEqual(1, stage.ApplyStageOps.Count);
            Assert.AreEqual(2000d, (double)stage.ApplyStageOps[0]["ms"], 0.0001);
        }

        [Test]
        public void PreloadDoesNotPauseExecution()
        {
            var json = @"{""script"":[
                {""op"":""say"",""text"":""before preload""},
                {""op"":""preload"",""assets"":[
                    {""url"":""bg/forest.png"",""kind"":""sprite""},
                    {""url"":""sfx/door.wav"",""kind"":""audio""}
                ]},
                {""op"":""say"",""text"":""after preload""}
            ]}";
            var p = Play(json, out var stage);

            p.Advance();
            Assert.AreEqual("before preload", stage.Last);

            p.Advance();
            Assert.AreEqual(1, stage.ApplyStageOps.Count);
            Assert.AreEqual("preload", (string)stage.ApplyStageOps[0]["op"]);
            var assets = (JArray)stage.ApplyStageOps[0]["assets"];
            Assert.AreEqual(2, assets.Count);

            p.Advance();
            Assert.AreEqual("after preload", stage.Last,
                "preload should not pause — execution continues to next say");
        }

        [Test]
        public void PreloadWithEmptyAssetsIsNoOp()
        {
            var json = @"{""script"":[
                {""op"":""preload"",""assets"":[]},
                {""op"":""say"",""text"":""ok""}
            ]}";
            var p = Play(json, out var stage);

            p.Advance();
            p.Advance();
            Assert.AreEqual("ok", stage.Last);
        }
    }
}
