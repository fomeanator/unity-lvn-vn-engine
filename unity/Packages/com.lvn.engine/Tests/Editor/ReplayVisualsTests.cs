using System.Collections.Generic;
using System.Linq;
using Lvn;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Lvn.Tests
{
    // ReplayVisuals rebuilds the scene a save/rollback landed in. Structural ops
    // (bg/actor/obj/anim/text) re-run in order; FX/audio collapse to the LAST
    // value per state key so a load doesn't flash through every fade of the
    // chapter or restart the soundtrack N times.
    public class ReplayVisualsTests
    {
        private sealed class RecStage : ILvnStage
        {
            public readonly List<JObject> Applied = new List<JObject>();
            public void ShowSay(string who, string text, string style) { }
            public void ShowChoice(IReadOnlyList<LvnOption> options) { }
            public void ApplyStage(JObject command) => Applied.Add(command);
            public void OnEnd() { }
        }

        private static (LvnPlayer p, RecStage s) Make(string json)
        {
            var s = new RecStage();
            return (new LvnPlayer(LvnDocument.Parse(json), s), s);
        }

        private List<JObject> Ops(RecStage s, string op)
            => s.Applied.Where(c => (string)c["op"] == op).ToList();

        [Test]
        public void FxCollapsesToLastValuePerKind()
        {
            var (p, s) = Make(@"{""script"":[
                {""op"":""fade"",""to"":""black""},
                {""op"":""say"",""text"":""a""},
                {""op"":""fade"",""to"":""clear""},
                {""op"":""dim"",""alpha"":0.2},
                {""op"":""dim"",""alpha"":0.7},
                {""op"":""tint"",""color"":""warm""},
                {""op"":""say"",""text"":""b""}
            ]}");
            p.ReplayVisuals(7);

            var fades = Ops(s, "fade");
            Assert.AreEqual(1, fades.Count, "only the LAST fade replays");
            Assert.AreEqual("clear", (string)fades[0]["to"]);

            var dims = Ops(s, "dim");
            Assert.AreEqual(1, dims.Count);
            Assert.AreEqual(0.7f, (float)dims[0]["alpha"], 0.001f);

            Assert.AreEqual(1, Ops(s, "tint").Count);
        }

        [Test]
        public void ParticlesKeyedPerType()
        {
            var (p, s) = Make(@"{""script"":[
                {""op"":""particles"",""type"":""rain"",""on"":true},
                {""op"":""particles"",""type"":""snow"",""on"":true},
                {""op"":""particles"",""type"":""rain"",""on"":false},
                {""op"":""say"",""text"":""x""}
            ]}");
            p.ReplayVisuals(4);

            var parts = Ops(s, "particles");
            Assert.AreEqual(2, parts.Count, "one final state per particle type");
            var rain = parts.First(c => (string)c["type"] == "rain");
            Assert.IsFalse((bool)rain["on"], "rain ended OFF");
            var snow = parts.First(c => (string)c["type"] == "snow");
            Assert.IsTrue((bool)snow["on"], "snow stayed ON");
        }

        [Test]
        public void CameraZoomPanPersistShakeAndResetDoNot()
        {
            var (p, s) = Make(@"{""script"":[
                {""op"":""camera"",""action"":""shake"",""amplitude"":10},
                {""op"":""camera"",""action"":""zoom"",""factor"":1.5},
                {""op"":""camera"",""action"":""pan"",""x"":0.2,""y"":0},
                {""op"":""say"",""text"":""x""}
            ]}");
            p.ReplayVisuals(4);

            var cams = Ops(s, "camera");
            Assert.AreEqual(2, cams.Count, "zoom + pan replay; shake is transient");
            Assert.IsFalse(cams.Any(c => (string)c["action"] == "shake"));
        }

        [Test]
        public void CameraResetClearsAccumulatedZoomAndPan()
        {
            var (p, s) = Make(@"{""script"":[
                {""op"":""camera"",""action"":""zoom"",""factor"":2},
                {""op"":""camera"",""action"":""pan"",""x"":0.5,""y"":0.5},
                {""op"":""camera"",""action"":""reset""},
                {""op"":""say"",""text"":""x""}
            ]}");
            p.ReplayVisuals(4);
            Assert.AreEqual(0, Ops(s, "camera").Count, "reset returns camera to default — nothing to replay");
        }

        [Test]
        public void AudioResumesLastTrackPerChannelSfxSkipped()
        {
            var (p, s) = Make(@"{""script"":[
                {""op"":""audio"",""channel"":""music"",""url"":""/m1.ogg""},
                {""op"":""audio"",""channel"":""sfx"",""url"":""/boom.ogg""},
                {""op"":""audio"",""channel"":""music"",""url"":""/m2.ogg""},
                {""op"":""audio"",""channel"":""ambient"",""url"":""/wind.ogg""},
                {""op"":""say"",""text"":""x""}
            ]}");
            p.ReplayVisuals(5);

            var audio = Ops(s, "audio");
            Assert.AreEqual(2, audio.Count, "one per looping channel; sfx one-shots don't replay");
            Assert.AreEqual("/m2.ogg", (string)audio.First(c => (string)c["channel"] == "music")["url"]);
            Assert.AreEqual("/wind.ogg", (string)audio.First(c => (string)c["channel"] == "ambient")["url"]);
        }

        [Test]
        public void AudioStopIsTheFinalStateToo()
        {
            var (p, s) = Make(@"{""script"":[
                {""op"":""audio"",""channel"":""music"",""url"":""/m1.ogg""},
                {""op"":""audio"",""channel"":""music"",""action"":""stop""},
                {""op"":""say"",""text"":""x""}
            ]}");
            p.ReplayVisuals(3);

            var audio = Ops(s, "audio");
            Assert.AreEqual(1, audio.Count);
            Assert.AreEqual("stop", (string)audio[0]["action"], "a stopped channel replays as stopped");
        }

        [Test]
        public void StructuralOpsStillReplayInOrderAndFxComesAfter()
        {
            var (p, s) = Make(@"{""script"":[
                {""op"":""fade"",""to"":""black""},
                {""op"":""bg"",""sprite_url"":""/bg/a.jpg""},
                {""op"":""actor"",""id"":""hero"",""show"":true},
                {""op"":""bg"",""sprite_url"":""/bg/b.jpg""},
                {""op"":""say"",""text"":""x""}
            ]}");
            p.ReplayVisuals(5);

            var ops = s.Applied.Select(c => (string)c["op"]).ToList();
            Assert.AreEqual(new[] { "bg", "actor", "bg", "fade" }, ops,
                "structural ops in order, collapsed FX after");
        }

        [Test]
        public void SayChoiceSetWaitNeverReplay()
        {
            var (p, s) = Make(@"{""script"":[
                {""op"":""set"",""key"":""x"",""value"":1},
                {""op"":""say"",""text"":""a""},
                {""op"":""wait"",""ms"":500},
                {""op"":""choice"",""options"":[{""text"":""go"",""goto"":""L""}]},
                {""op"":""label"",""id"":""L""}
            ]}");
            p.ReplayVisuals(5);
            Assert.AreEqual(0, s.Applied.Count, "no data/pause/dialogue ops in a visual replay");
        }
    }
}
