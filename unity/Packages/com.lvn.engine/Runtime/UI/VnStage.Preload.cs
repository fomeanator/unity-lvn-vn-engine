using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lvn.Content;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// Look-ahead asset warmup: prefetch the next beats' art/audio while the
    /// player reads, and the chapter-entry decode of upcoming plain art.
    /// </summary>
    public sealed partial class VnStage
    {
        // ── look-ahead prefetch ──────────────────────────────────────────────
        // While the player reads a line, warm the assets the next few beats will
        // show — the decode happens during the pause, so a cold bg/portrait never
        // pops in a frame late mid-scene. Bounded per beat (the sprite cache and
        // in-flight dedup make repeats free); the set resets with the stage.
        private readonly HashSet<string> _prefetched = new HashSet<string>();

        private void PrefetchAhead()
        {
            if (_player == null || Assets == null) return;
            const int lookAhead = 25, maxSprites = 6, maxAudio = 2;
            List<string> sprites = null, audio = null;
            bool spineKicked = false, set3dKicked = false;
            foreach (var c in _player.PeekForward(lookAhead))
            {
                var op = (string)c["op"];
                if (op == "bg3d")
                {
                    var id = (string)c["id"];
                    if (!set3dKicked && !string.IsNullOrEmpty(id) &&
                        !Turns3DOff(c) && id != Lvn.UI.World.Lvn3DDemoSet.Id &&
                        _prefetched.Add("bg3d:" + id))
                    {
                        set3dKicked = true;
                        LvnAsync.Fire(Warm3DSetBestEffortAsync(id), "Warm3DSetBestEffort");
                    }
                }
                else if (Lvn.LvnOpKind.CarriesArt(op))
                {
                    var url = (string)c["sprite_url"];
                    // A Spine actor carries no sprite_url — its (heavy) assets
                    // live in the catalog. Warm the WHOLE scene (json + atlas +
                    // every page + bg, 2K variants preferred): the un-prefetched
                    // SECOND page of a multi-page atlas used to decode
                    // synchronously in the reveal frame. Fire-and-forget,
                    // deduped per actor id — and at most ONE new spine per pass:
                    // each page decode is a main-thread hit, and kicking several
                    // scenes at once stacked those hits into a visible stutter
                    // right at chapter entry. Later beats warm the rest.
                    if (string.IsNullOrEmpty(url) && Lvn.LvnOpKind.Of(op) == Lvn.LvnOpSubject.Actor)
                    {
                        // Скелет, названный одним адресом в самой команде,
                        // греется наравне с каталожным: автору не должно быть
                        // выгоднее писать длиннее.
                        var прямой = (string)c["spine"];
                        var sp = !string.IsNullOrEmpty(прямой)
                            ? InlineSpine((string)c["id"], прямой, (JObject)c)
                            : Catalog?.Get((string)c["id"]);
                        if (sp != null && sp.kind == "spine" && sp.spine != null)
                        {
                            var spineId = (string)c["id"];
                            // A skeleton that's already built (e.g. the scene
                            // currently showing right after a resume, when
                            // _prefetched starts empty) must not eat the
                            // one-per-pass slot — otherwise the pause before
                            // the NEXT scene warms nothing and its build lands
                            // cold in the reveal frame.
                            if (Skel(spineId)?.Go != null)
                            {
                                _prefetched.Add("spine:" + spineId);
                                continue;
                            }
                            if (!spineKicked && _prefetched.Add("spine:" + spineId))
                            {
                                spineKicked = true;
                                LvnAsync.Fire(PrefetchSpineAsync(spineId, sp), "PrefetchSpine");
                            }
                            continue;
                        }
                    }
                    // A LAYERED catalog character (id-based, no sprite_url) was a
                    // prefetch blind spot — its five layers all fetched cold in
                    // the reveal frame. Resolve the layers it will show and warm
                    // their bytes like any direct url.
                    if (string.IsNullOrEmpty(url) && Catalog != null)
                    {
                        var cid = (string)c["id"];
                        if (!string.IsNullOrEmpty(cid) && Catalog.Has(cid))
                        {
                            try
                            {
                                if (Lvn.LvnOpKind.Of(op) == Lvn.LvnOpSubject.Background)
                                {
                                    foreach (var u in Catalog.Resolve(cid, AxesFrom(c), CatalogCond()))
                                        if (!string.IsNullOrEmpty(u) && _prefetched.Add(u))
                                            (sprites ??= new List<string>()).Add(u);
                                }
                                else
                                {
                                    foreach (var rl in Catalog.ResolveLayers(cid, AxesOf(c), CatalogCond()))
                                        if (!string.IsNullOrEmpty(rl.Url) && _prefetched.Add(rl.Url))
                                            (sprites ??= new List<string>()).Add(rl.Url);
                                }
                            }
                            catch { /* a bad catalog entry must not kill the prefetch */ }
                        }
                        continue;
                    }
                    if (string.IsNullOrEmpty(url) || !_prefetched.Add(url)) continue;
                    (sprites ??= new List<string>()).Add(url);
                }
                else if (op == "audio")
                {
                    var url = (string)c["url"];
                    if (string.IsNullOrEmpty(url) || !_prefetched.Add(url)) continue;
                    (audio ??= new List<string>()).Add(url);
                }
                if ((sprites?.Count ?? 0) >= maxSprites && (audio?.Count ?? 0) >= maxAudio) break;
            }
            if (sprites != null && sprites.Count > maxSprites) sprites.RemoveRange(maxSprites, sprites.Count - maxSprites);
            if (audio != null && audio.Count > maxAudio) audio.RemoveRange(maxAudio, audio.Count - maxAudio);
            if (sprites != null) LvnAsync.Fire(Assets.PreloadAsync(sprites, "sprite", _cts.Token), "Preload");
            if (audio != null) LvnAsync.Fire(Assets.PreloadAsync(audio, "audio", _cts.Token), "Preload");
        }

        private async Task Warm3DSetBestEffortAsync(string id)
        {
            try { await Assets.Preload3DSetAsync(id, _cts.Token); }
            catch (OperationCanceledException) { }   // предзагрузка — не обязательство: не вышло, загрузим по месту
            catch (Exception e) { Debug.LogWarning($"[lvn-preload] 3D set '{id}': {e.Message}"); }
        }

        /// <summary>Gate chapter entry only on the first upcoming 3D set. Unlike
        /// ordinary sprite warming this removes both network and bundle-open work
        /// from the first <c>bg3d</c> frame.</summary>
        internal async Task WarmUpcoming3DAsync(int lookAhead)
        {
            if (_player == null || Assets == null) return;
            // Первый набор 3D ДЕРЖИТ вводную реплику — значит это первый кадр,
            // а не фон: игрок ждёт именно его.
            using var rung = Lvn.Content.LvnRungScope.At(Lvn.Content.LvnRung.FirstFrame);
            foreach (var c in _player.PeekForward(lookAhead))
            {
                if ((string)c["op"] != "bg3d") continue;
                var id = (string)c["id"];
                // Выключение спрашиваем у дома чтения: раньше здесь знали
                // только имя-признак, и команда с полем off тянула целый набор.
                if (string.IsNullOrEmpty(id) || Turns3DOff(c) ||
                    id == Lvn.UI.World.Lvn3DDemoSet.Id) continue;
                _prefetched.Add("bg3d:" + id);
                await Assets.Preload3DSetAsync(id, _cts.Token);
                return;
            }
        }

        // Chapter-entry warmup for PLAIN art — the sibling of WarmUpcomingSpineAsync.
        // The loading screen stages the BYTES onto disk; this DECODES the first
        // beats' background and character layers into the sprite cache behind the
        // entry fade, so the opening beats never pay a decode in the reveal frame.
        // Spine entities are skipped (their own warmup builds the full skeleton).
        internal async Task WarmUpcomingArtAsync(int lookAhead, int maxSprites = 12)
        {
            if (_player == null || Assets == null) return;
            // СТУПЕНЬ ОБЪЯВЛЯЕТ САМ ПРОГРЕВ, а не тот, кто его позвал:
            // «увидят ли это сейчас» — свойство работы, и знает его она.
            using var rung = Lvn.Content.LvnRungScope.At(Lvn.Content.LvnRung.CurrentChapter);
            var urls = new List<string>();
            var seen = new HashSet<string>();
            void Take(string u)
            {
                if (!string.IsNullOrEmpty(u) && seen.Add(u) && urls.Count < maxSprites) urls.Add(u);
            }
            foreach (var c in _player.PeekForward(lookAhead))
            {
                var op = (string)c["op"];
                if (!Lvn.LvnOpKind.CarriesArt(op)) continue;
                Take((string)c["sprite_url"]);
                var id = (string)c["id"];
                if (!string.IsNullOrEmpty(id) && Catalog != null && Catalog.Has(id))
                {
                    var e = Catalog.Get(id);
                    if (e == null || e.kind != "spine")
                    {
                        try
                        {
                            if (Lvn.LvnOpKind.Of(op) == Lvn.LvnOpSubject.Background)
                                foreach (var u in Catalog.Resolve(id, AxesFrom(c), CatalogCond())) Take(u);
                            else
                                foreach (var rl in Catalog.ResolveLayers(id, AxesOf(c), CatalogCond())) Take(rl.Url);
                        }
                        catch { /* a bad catalog entry must not kill the warmup */ }
                    }
                }
                if (urls.Count >= maxSprites) break;
            }
            if (urls.Count == 0) return;
            var loads = new List<Task>(urls.Count);
            foreach (var u in urls) loads.Add(WarmOneAsync(u));
            await Task.WhenAll(loads);

            async Task WarmOneAsync(string u)
            {
                try { await Assets.LoadSpriteAsync(u, _cts.Token); }
                catch { /* warmup is best-effort */ }
            }
        }
    }
}
