using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    public sealed partial class VnStage
    {
        // ── spine actors (the optional spine-unity runtime) ─────────────────

        /// <summary>
        /// СКЕЛЕТ — ОДНА ЗАПИСЬ, А НЕ ЧЕТЫРЕ ПАМЯТИ.
        ///
        /// <para>Про каждый скелет знали врозь: построенный объект, отметка
        /// «строится», отложенный проигрыш и место в порядке давности. Уборка
        /// по вытеснению знала про три из четырёх — отложенный проигрыш она не
        /// трогала, и он оставался лежать после неудавшейся сборки, чтобы
        /// однажды примениться к скелету, построенному через полглавы.</para>
        ///
        /// <para>Тот же урок уже записан про мизансцену
        /// (<c>WorldStage.Slot</c>): пять памятей по одному ключу и уборка,
        /// делящая их на группы вручную. Список ломается не сегодня — он
        /// ломается на следующей памяти, которую в него забудут внести.</para>
        ///
        /// <para>Давность (MRU) осталась списком снаружи: это порядок МЕЖДУ
        /// скелетами, а не свойство одного из них.</para>
        /// </summary>
        private sealed class Skeleton
        {
            public GameObject Go;                       // построен; null — ещё нет
            public bool Building;                       // сборка в полёте
            public (string name, bool loop)? Pending;   // проигрыш, пришедший во время сборки
        }

        private readonly Dictionary<string, Skeleton> _skeletons = new Dictionary<string, Skeleton>();

        /// <summary>Запись, если она есть. Отсутствие — не ошибка: про
        /// большинство актёров скелетный дом ничего не знает.</summary>
        private Skeleton Skel(string id)
            => id != null && _skeletons.TryGetValue(id, out var sk) ? sk : null;

        /// <summary>Занять имя. Повторы сыплются пачкой, пока первая сборка
        /// ждёт файлы: не займи мы имя, каждый вызов строил бы свой скелет —
        /// армия клонов вместо одного актёра.</summary>
        private Skeleton Reserve(string id)
        {
            if (!_skeletons.TryGetValue(id, out var sk)) _skeletons[id] = sk = new Skeleton();
            return sk;
        }

        /// <summary>Забыть скелет ЦЕЛИКОМ. Одна дверь вместо трёх строк, из
        /// которых одну всегда забывают.</summary>
        private void ForgetSkeleton(string id)
        {
            _skeletons.Remove(id);
            _spineMru.Remove(id);
            UnpinSpinePages(id);
        }

        /// <summary>Кто сейчас строится — для диагностики рывка кадра.</summary>
        private IEnumerable<string> BuildingSkeletons()
        {
            foreach (var kv in _skeletons) if (kv.Value.Building) yield return kv.Key;
        }

        // Every in-flight cold build, so chapter entry can AWAIT the stage
        // settling (see StartWithSpineWarmup / RestoreSnapshot) instead of
        // letting the reveal race the typewriter.
        private readonly List<Task> _spineBuilds = new List<Task>();
        // Live skeletons cost real memory (meshes + big atlas textures). Keep only
        // the most-recently-used few resident; when a new one is built, destroy the
        // oldest HIDDEN skeleton past the cap ("passed" scenes leave memory). The
        // parsed SkeletonData/atlas stays cached in the bridge, so a later re-show
        // rebuilds fast. MRU order: last entry = most recent.
        private const int SpineLiveCap = 4;
        private readonly List<string> _spineMru = new List<string>();

        // Страницы атласа живого скелета держит та же доска, что и арт сцены.
        // Отпускаем СРАЗУ: у скелета нет прокси-кроссфейда, под которым старые
        // страницы ещё видны, — его показ и уход мгновенны.
        //
        // Здесь эта доска чинит и старую ошибку порядка: пересборка снимала
        // прежний набор ДО того, как прикрепить новый, а наборы пересекаются —
        // страницы атласа у перестроенного скелета обычно те же самые. Общая
        // страница на мгновение доходила до нуля держателей, и окно вправе
        // было забрать текстуру именно в этот момент (скелет становится
        // чёрно-розовым без шанса восстановиться иначе как полной пересборкой).
        private readonly LvnPinBoard<string> _spinePages = new LvnPinBoard<string>();

        private void PinSpinePages(string id, List<Sprite> pages)
            => _spinePages.Hold(id, (Assets as CachingAssets)?.Loader, pages);

        private void UnpinSpinePages(string id) => _spinePages.Release(id);

        private void UnpinAllSpinePages() => _spinePages.ReleaseAll();

        private void TouchSpine(string id)
        {
            _spineMru.Remove(id);
            _spineMru.Add(id);
            for (int i = 0; i < _spineMru.Count - SpineLiveCap; )
            {
                var victim = _spineMru[i];
                var go = Skel(victim)?.Go;
                // Evict only a hidden skeleton; keep scanning if the oldest is on
                // screen (don't destroy what the player is looking at).
                if (go != null && go.activeSelf) { i++; continue; }
                if (go != null) UnityEngine.Object.Destroy(go);
                ForgetSkeleton(victim);   // объект, отметки, страницы и место — разом
            }
        }

        // Show/hide via the bridge's short fade when available; a hard toggle is
        // the fallback when the (older) optional assembly has no SetVisible.
        private static void SetSpineVisible(GameObject go, bool visible)
        {
            if (go == null) return;
            if (LvnSpineBridge.SetVisible != null) LvnSpineBridge.SetVisible(go, visible);
            else go.SetActive(visible);
        }

        // ── downscaled variants ("@2k") ─────────────────────────────────────
        // Spine page exports run up to 7708×8252 — decoding that PNG stalls the
        // main thread for hundreds of ms and the RGBA fills ~254 MB of VRAM.
        // The server (server/downscale.go) serves "<name>@2k.png": the same
        // image resized once to fit 2048, cached to disk. Spine UVs come from
        // the atlas file's "size:" line (normalized), so a downscaled page
        // renders correctly with the ORIGINAL .atlas untouched. One failed
        // variant request (old server, local directory install) latches the
        // whole session onto originals — no per-page 404 spam.
        private static bool _spineVariantUnavailable;

        // "…/page.png" → "…/page@2k.png"; null when the url has no downscalable
        // image extension. Must mirror server/downscale.go's naming exactly.
        internal static string SpineVariantUrl(string url)
            // Имя варианта знает СНАБЖЕНЕЦ: здесь оно было второй копией той же
            // строки, и переименование бокса на сервере разошлось бы молча.
            => Lvn.Content.DownloadPolicy.WithVariant(
                   url, Lvn.Content.DownloadPolicy.DisplayVariant);

        // Loads a Spine image (atlas page or container bg) preferring the
        // server's 2K variant, falling back to the full-size original.
        private async Task<Sprite> LoadSpineImageAsync(string url, System.Threading.CancellationToken ct)
        {
            var variant = !_spineVariantUnavailable ? SpineVariantUrl(url) : null;
            if (variant != null)
            {
                Sprite spr = null;
                try { spr = await Assets.LoadSpriteAsync(variant, ct); }
                catch (System.OperationCanceledException) { throw; }
                catch { }   // скелет не собрался — актёр останется плоским спрайтом
                if (spr != null) return spr;
                _spineVariantUnavailable = true;
            }
            return await Assets.LoadSpriteAsync(url, ct);
        }

        // Chapter-entry warmup: find the first spine actor among the upcoming
        // ops and warm-BUILD it hidden (full download + decode + parse + mesh)
        // before the intro advances. "Everyone stages their opening" — without
        // this the typewriter starts, then freezes mid-sentence while the entry
        // scene's skeleton builds. The entry is behind the chapter fade anyway,
        // so the extra beat is invisible; a chapter with no imminent spine
        // starts exactly as before. Only the FIRST one — the ordinary
        // read-pause prefetch warms the rest one scene at a time.
        internal async Task WarmUpcomingSpineAsync(int lookAhead)
        {
            if (_player == null || Catalog == null || Assets == null) return;
            // Скелеты ДЕРЖАТ первую реплику (иначе она видимо подвисает) —
            // первый кадр, не фон.
            using var rung = Lvn.Content.LvnRungScope.At(Lvn.Content.LvnRung.FirstFrame);
            foreach (var c in _player.PeekForward(lookAhead))
            {
                if ((string)c["op"] != "actor") continue;
                var id = (string)c["id"];
                var sp = Catalog.Get(id);
                if (sp == null || sp.kind != "spine" || sp.spine == null) continue;
                var warm = new JObject { ["op"] = "actor", ["id"] = id, ["show"] = false };
                await ApplySpineAsync(id, sp, warm);
                // Let the fader's GPU warm pulse finish before the intro starts:
                // the build lands the meshes, but the pipeline compile + texture
                // upload happen over the next few nearly-invisible drawn frames.
                for (int i = 0; i < 6; i++) await Task.Yield();
                return;
            }
        }

        // Completes when every spine build currently in flight has landed —
        // resume's ReplayVisuals fires its builds without awaiting them, and
        // ContinueFrom must not render the saved beat over a half-built stage.
        internal Task SpineBuildsSettled()
        {
            _spineBuilds.RemoveAll(t => t.IsCompleted);
            return _spineBuilds.Count == 0 ? Task.CompletedTask : Task.WhenAll(_spineBuilds.ToArray());
        }

        internal int PendingSpineBuilds
        {
            get { _spineBuilds.RemoveAll(t => t.IsCompleted); return _spineBuilds.Count; }
        }

        // Warms a whole Spine scene during the read pause — the FULL path, not
        // just the downloads: ApplySpineAsync with show=false fetches json/
        // atlas/every page/bg, decodes them, parses the skeleton, builds the
        // container hidden, and the fader's warm pulse then compiles the
        // pipeline and uploads the textures. A later transition to this scene
        // is a pure SetVisible — the tap never pays import costs. One scene
        // per read pause (see the caller's spineKicked cap).
        internal async Task PrefetchSpineAsync(string id, Lvn.Content.LvnSpriteEntity sp)
        {
            try
            {
                var warm = new JObject { ["op"] = "actor", ["id"] = id, ["show"] = false };
                await ApplySpineAsync(id, sp, warm);
            }
            catch (System.OperationCanceledException) { }   // нет такой анимации в скелете — молчим, сцена важнее
            catch { /* prefetch is best-effort; the real show reloads what it needs */ }
        }

        // The atlas' page image URLs, in file order, resolved next to the atlas.
        // libgdx atlases name each page on its own line ending in .png; we map
        // those to sibling URLs of the atlas. Falls back to the catalog texture.
        private static List<string> SpinePageUrls(string atlasUrl, string atlasText, string fallback)
        {
            var urls = new List<string>();
            string dir = "";
            int slash = atlasUrl != null ? atlasUrl.LastIndexOf('/') : -1;
            if (slash >= 0) dir = atlasUrl.Substring(0, slash + 1);
            if (!string.IsNullOrEmpty(atlasText))
            {
                foreach (var raw in atlasText.Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.EndsWith(".png") || line.EndsWith(".PNG")) urls.Add(dir + line);
                }
            }
            if (urls.Count == 0 && !string.IsNullOrEmpty(fallback)) urls.Add(fallback);
            return urls;
        }

        // Сущности, собранные из адреса в команде. Помним их: сцена спрашивает
        // облик не только в миг показа (перестановка, прогрев, возврат из
        // главы), а второй раз адрес может и не прийти.
        private readonly Dictionary<string, Lvn.Content.LvnSpriteEntity> _inlineSpine
            = new Dictionary<string, Lvn.Content.LvnSpriteEntity>();

        /// <summary>Скелет, названный одним адресом прямо в команде.</summary>
        internal Lvn.Content.LvnSpriteEntity InlineSpine(string id, string url, JObject cmd)
        {
            var bg = (string)cmd["spine_bg"];
            var play = (string)cmd["play"];
            if (_inlineSpine.TryGetValue(id, out var было) && было?.spine != null &&
                было.spine.json == Lvn.Content.LvnSpineRef.FromUrl(url)?.json)
            {
                // Тот же скелет — не пересобираем, но проигрыш мог смениться.
                if (!string.IsNullOrEmpty(play)) было.spine.auto = play;
                if (!string.IsNullOrEmpty(bg)) было.spine.bg = bg;
                return было;
            }
            var sp = Lvn.Content.LvnSpineRef.FromUrl(url, bg, play);
            if (sp == null) return null;
            var e = new Lvn.Content.LvnSpriteEntity { kind = "spine", spine = sp };
            _inlineSpine[id] = e;
            return e;
        }

        private async Task ApplySpineAsync(string id, Lvn.Content.LvnSpriteEntity e, JObject cmd)
        {
            int epoch = _stageEpoch; // the scene this build belongs to (see ResetStage)
            var placement = _memory.TryWhere(id, out var prevSp)
                ? PlacementFrom(cmd, prevSp, SlotsOf(id)) : PlacementFrom(cmd, SlotsOf(id));
            FillTransitionDefaults(cmd, ref placement);
            _memory.SetWhere(id, placement); // sticky base (spine actors too)

            if (!LvnSpineBridge.Available)
            {
                Debug.LogWarning("[lvn] '" + id + "' is kind:spine, but the Spine integration " +
                                 "isn't installed (add the com.lvn.engine.spine package plus the official " +
                                 "com.esotericsoftware.spine.spine-unity runtime to the project)");
                return;
            }
            if (!(_renderer is CanvasSceneRenderer canvas))
            {
                Debug.LogWarning("[lvn] spine entities render on the Canvas scene path only");
                return;
            }

            var existing = Skel(id)?.Go;
            bool alreadyBuilt = existing != null;

            // Fast hide: an already-built skeleton just toggles off. But a
            // NEVER-built one with show=false is WARMED below — created hidden so
            // the later reveal is a single SetActive, never a synchronous build
            // in the frame the player sees. (Author: `actor id=x show=false` a
            // couple of lines before you need it.)
            if (!placement.Show && alreadyBuilt)
            {
                SetSpineVisible(existing, false);
                return;
            }

            // The full sprite-actor placement vocabulary applies: x/y/width/height/
            // anchor/flip/rotation via the slot, opacity via the rig's CanvasGroup.
            canvas.PlaceSpineActor(id, placement);
            canvas.SetActorOpacity(id, placement.Opacity);
            var slot = canvas.RigFor(id);
            if (slot == null) return;

            // Build once — for a real show OR a warm (show=false, never built).
            // The reveal frame never pays the skeleton-parse + mesh cost.
            if (!alreadyBuilt && Skel(id)?.Building != true)
            {
                var sk = Reserve(id);
                sk.Building = true;
                var buildDone = new TaskCompletionSource<bool>();
                _spineBuilds.RemoveAll(t => t.IsCompleted);
                _spineBuilds.Add(buildDone.Task);
                // [lvn-perf] one line per cold build with a per-stage breakdown —
                // the map for hunting first-show hitches. Cheap (builds are rare).
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var perf = new System.Text.StringBuilder("[lvn-perf] spine build '" + id + "':");
                long mark = 0;
                System.Action<string> lap = stage =>
                {
                    perf.Append(' ').Append(stage).Append('=').Append(sw.ElapsedMilliseconds - mark).Append("ms");
                    mark = sw.ElapsedMilliseconds;
                };
                try
                {
                    var sp = e.spine;
                    string json = null, atlasText = null;
                    var textures = new List<Texture2D>();
                    var pageSprites = new List<Sprite>(); // pinned below so the LRU can't evict a live skeleton's pages
                    Texture2D bgTex = null;
                    try
                    {
                        json = await Assets.LoadTextAsync(sp.json, _cts.Token);
                        lap("json");
                        try { atlasText = await Assets.LoadTextAsync(sp.atlas, _cts.Token); }
                        catch
                        {
                            // Два написания одного файла: Spine кладёт рядом
                            // либо .atlas, либо .atlas.txt. Какое из них выбрал
                            // экспорт — не забота автора.
                            var запасной = sp.AtlasFallback;
                            if (string.IsNullOrEmpty(запасной)) throw;
                            atlasText = await Assets.LoadTextAsync(запасной, _cts.Token);
                            sp.atlas = запасной;
                        }
                        lap("atlas");
                        // Load EVERY atlas page (multi-page atlases have >1), in
                        // the order they appear, resolved next to the atlas file.
                        foreach (var url in SpinePageUrls(sp.atlas, atlasText, sp.texture))
                        {
                            var spr = await LoadSpineImageAsync(url, _cts.Token);
                            if (spr != null && spr.texture != null) { textures.Add(spr.texture); pageSprites.Add(spr); }
                            lap("page(" + (spr != null && spr.texture != null ? spr.texture.width + "x" + spr.texture.height : "miss") + ")");
                        }
                        // Optional bg that belongs to the skeleton (rides with it).
                        if (!string.IsNullOrEmpty(sp.bg))
                        {
                            var bgSpr = await LoadSpineImageAsync(sp.bg, _cts.Token);
                            if (bgSpr != null) { bgTex = bgSpr.texture; pageSprites.Add(bgSpr); }
                            lap("bg");
                        }
                    }
                    catch { }   // страница атласа не пришла — рисуем тем, что есть
                    if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(atlasText) || textures.Count == 0)
                    {
                        Debug.LogWarning("[lvn] spine '" + id + "': failed to load skeleton files");
                        return;
                    }
                    // Parse the (multi-MB) skeleton JSON on the thread pool and
                    // prime the bridge's resource cache: parsing was the LAST
                    // main-thread cost of a build (100-170 ms — a visible
                    // stutter in whatever idle animation plays during the read
                    // pause). Best-effort: on any failure Create() below parses
                    // synchronously exactly as before.
                    var pageArr = textures.ToArray();
                    if (LvnSpineBridge.Prepare != null)
                    {
                        try { await LvnSpineBridge.Prepare(json, atlasText, pageArr); }
                        catch { }   // открепление страницы: объект мог уже умереть
                        lap("parse(worker)");
                    }
                    // Re-fetch the slot: the awaits above yield, and a scene
                    // rebuild (resume/replay) can tear the old slot down meanwhile.
                    // Parenting to a destroyed slot orphans the container (no
                    // canvas → the fit can't run → a black, invisible skeleton).
                    // A fresh frame for the mesh build: the last page decode
                    // already cost this frame — stacking the mesh build into
                    // the SAME frame is what reads as a transition
                    // micro-freeze. The slot re-fetch stays AFTER the yield —
                    // it's an await like any other, and the scene can be torn
                    // down during it.
                    await Task.Yield();
                    // A chapter change happened during the load: a new scene may
                    // have re-created a slot with this same id, so a null-check
                    // isn't enough — this stale build would parent a SECOND
                    // skeleton into the new scene and orphan/leak one. Bail on a
                    // superseded epoch. (Спрайты сторожит дорожка актёра; spine
                    // had no equivalent.)
                    if (!StageCurrent(epoch)) return;
                    slot = canvas.RigFor(id);
                    if (slot == null) return;
                    var go = LvnSpineBridge.Create(slot, json, atlasText, pageArr, sp.scale, bgTex);
                    lap("mesh");
                    LvnLog.Trace(perf.Append(" total=").Append(sw.ElapsedMilliseconds).Append("ms").ToString());
                    if (go == null) return;
                    sk.Go = go;
                    // Pin the page textures for as long as this skeleton is alive:
                    // its atlas/material reference them directly, and the sprite
                    // LRU would otherwise destroy them after the grace window in a
                    // long session, blacking out the skeleton with no recovery.
                    PinSpinePages(id, pageSprites);
                    TouchSpine(id); // MRU + evict passed skeletons past the cap
                    existing = go;
                    if (sk.Pending is { } pend)
                    {
                        sk.Pending = null;
                        LvnSpineBridge.Play(go, pend.name, pend.loop);
                    }
                    else if (!string.IsNullOrEmpty(sp.auto)) LvnSpineBridge.Play(go, sp.auto, true);
                }
                finally { sk.Building = false; buildDone.TrySetResult(true); }
            }

            // Visibility follows show; a warm build stays inactive until the
            // later show=true flips it on with zero build cost. Read the FRESH
            // placement, not this call's local one: a `show=true` that arrived
            // while our warm build was awaiting skipped the build (dedup via
            // Skeleton.Building) and updated _placements — applying the stale local
            // Show=false here would hide an actor the script just revealed.
            if (existing != null)
            {
                bool show = _memory.TryWhere(id, out var cur) ? cur.Show : placement.Show;
                // Real-time size: re-fit to the screen each command, so `scale`/
                // `fit` resize the Spine on the fly. Refit BEFORE the fade so the
                // reveal is already correctly sized.
                LvnSpineBridge.Refit?.Invoke(existing, e.spine.scale, e.spine.fit);
                SetSpineVisible(existing, show);
                if (show) TouchSpine(id); // showing it makes it most-recent
            }

            var play = (string)cmd["play"];
            if (!string.IsNullOrEmpty(play))
            {
                var g = Skel(id)?.Go;
                if (g != null)
                    LvnSpineBridge.Play(g, play, BoolOr(cmd["loop"], false));
                else
                    Reserve(id).Pending = (play, BoolOr(cmd["loop"], false)); // ляжет после сборки
            }

            // Костяная фигура тащится наравне с любым предметом: перетаскивание
            // двигает слот, скелет едет на нём и продолжает играть.
            ArmDrag(id, cmd, placement);
        }
    }
}
