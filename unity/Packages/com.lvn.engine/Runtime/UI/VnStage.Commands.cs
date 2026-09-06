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
    /// Stage command dispatch (ApplyStage) and the simple op appliers: FX
    /// veils, camera, reactive text labels, hints, waits, preloads and
    /// script-driven anims — plus the tolerant JSON token readers they share.
    /// </summary>
    public sealed partial class VnStage
    {
        // ── ui: дерево интерфейса ───────────────────────────────────────────
        //
        // Слой создаётся при первом же `ui` и живёт до конца главы. У него ДВА
        // этажа, и это не украшение: `layer=hud` (по умолчанию) уходит под
        // окно реплики, `layer=over` ложится поверх всего. Один этаж не
        // годится — на первой же живой проверке ряд кнопок боевого интерфейса
        // закрыл собой текст реплики.
        private LvnUiLayer _uiLayer;
        private VisualElement _uiHudHost;
        private VisualElement _uiOverHost;

        private void ApplyUi(JObject cmd)
        {
            if (_uiLayer == null)
            {
                var over = _labelLayer ?? _uiRoot;   // метки `text` стоят выше диалога
                _uiOverHost = over;
                var hud = UiHudHost() ?? over;
                if (hud == null) return;
                _uiLayer = new LvnUiLayer(
                    hud, over,
                    () => UiVars,
                    GoOnClick,
                    LoadUiImageAsync,
                    SetVarsOnClick);
            }
            _uiLayer.Apply(cmd);
            NotifyUiStage();   // новое дерево обязано сразу знать, что на экране
        }

        /// <summary>
        /// КАТСЦЕНА — кадр без интерфейса.
        ///
        /// <para>Убирает разом реплику, выборы, метки, меню и деревья `ui`, и
        /// по желанию наезжает камерой. Это состояние, а не эффект: `cutscene
        /// off=1` возвращает всё на место.</para>
        ///
        /// <para>Раньше до этого можно было добраться ТОЛЬКО долгим нажатием
        /// (режим разглядывания арта) — из языка не вызвать, а игроку оно
        /// мешало. Теперь тем же выключателем пользуются оба: автор оператором,
        /// игрок жестом.</para>
        /// </summary>
        private void ApplyCutscene(JObject cmd)
        {
            bool on = !(BoolOr(cmd["off"], false) || !BoolOr(cmd["on"], true));
            if (on) HideChrome(LvnScreenDirector.CutsceneReason);
            else ShowChrome(LvnScreenDirector.CutsceneReason);

            // Наезд — необязательная часть: `cutscene on=1 zoom=1.12 dur=3`.
            var zoom = NumOrNull(cmd["zoom"]);
            if (zoom != null)
            {
                var move = new JObject
                {
                    ["op"] = "camera",
                    ["action"] = "zoom",
                    ["factor"] = on ? zoom.Value : 1f,
                    ["duration"] = NumOr(cmd["dur"], 2.5f),
                };
                ApplyCamera(move);
            }
            else if (!on)
            {
                ApplyCamera(new JObject { ["op"] = "camera", ["action"] = "reset", ["duration"] = 0.4f });
            }
        }

        /// <summary>Этаж под окном диалога. Отдельный контейнер, а не позиция
        /// среди детей: пересборка оболочки при смене темы вставляет диалог и
        /// выборы перед слоем меток, и любой «просто индекс» после этого
        /// съезжает.</summary>
        private VisualElement UiHudHost()
        {
            if (_uiHudHost != null && _uiHudHost.panel != null) return _uiHudHost;
            var chrome = (VisualElement)_chromeSafe;
            if (chrome == null) return null;
            _uiHudHost = new VisualElement { name = "vn-ui-hud", pickingMode = PickingMode.Ignore };
            LvnChrome.Stretch(_uiHudHost);
            chrome.Insert(0, _uiHudHost);   // ниже диалога, выборов и меток
            return _uiHudHost;
        }

        /// <summary>Переменные истории — для живых значений в `ui`. Ставит
        /// ИГРОК при создании (см. BindStory), а не хост: иначе каждый, кто
        /// встраивает движок, обязан был бы про это помнить.</summary>
        public System.Func<System.Collections.Generic.IReadOnlyDictionary<string, JToken>> UiVarsProvider;

        /// <summary>
        /// НАЖАТИЕ ВЕДЁТ ПО МЕТКЕ — и у фигуры на сцене, и у кнопки дерева
        /// `ui`. Один метод: рецепт был записан дважды, и второй раз — уже
        /// после того, как первый объяснил, почему прыжка мало.
        ///
        /// <para>Прыжка мало. Игрок может стоять в `wait`, ждать касания или
        /// показывать выбор, и запись новой позиции без пробуждения оставляет
        /// его стоять — экран отвечает один раз, а дальше замирает. Ровно это
        /// и случилось на первой же проверке.</para>
        ///
        /// <para>Отсюда же берётся способ ЖДАТЬ НАЖАТИЯ БЕЗ ОКНА ДИАЛОГА:
        /// экран паркуется на длинном `wait`, а кнопка выигрывает гонку с
        /// таймером — как это давно работает у кликабельных объектов.</para>
        /// </summary>
        private void GoOnClick(string label)
        {
            if (_player == null) return;
            if (!string.IsNullOrEmpty(label)) _player.GoTo(label);
            StopWaitingForPlayer();
            _player.Advance();
        }

        /// <inheritdoc/>
        public void BindStory(System.Func<System.Collections.Generic.IReadOnlyDictionary<string, JToken>> vars,
                              System.Action<string> goTo)
        {
            UiVarsProvider = vars;
            UiGoTo = goTo;
        }
        private System.Collections.Generic.IReadOnlyDictionary<string, JToken> UiVars
            => UiVarsProvider != null ? UiVarsProvider() : null;
        /// <summary>Прыжок по нажатию на элемент `ui` — тот же путь, что у
        /// on_click у obj.</summary>
        public System.Action<string> UiGoTo;
        /// <summary>Откуда брать картинки для `image`.</summary>
        public ILvnAssets UiAssets;

        // Загрузка своим путём, а не помощником оболочки: слой живёт в ДВИЖКЕ,
        // а оболочка — пакет над ним. Тянуть её сюда значило бы перевернуть
        // зависимость и лишить движок права работать без неё.
        private async System.Threading.Tasks.Task LoadUiImageAsync(VisualElement el, string url)
        {
            if (el == null || string.IsNullOrEmpty(url) || UiAssets == null) return;
            try
            {
                var sprite = await UiAssets.LoadSpriteAsync(url, default);
                if (sprite != null) el.style.backgroundImage = new StyleBackground(sprite);
            }
            catch { /* картинки нет — элемент остаётся пустым, экран не падает */ }
        }

        // A script-driven `anim` command: deserialize its LvnAnim payload and play
        // it on the named channel (default "script") of an already-shown entity, so
        // .lvns can tween any prop/layer or move a sprite along a path live.
        private void ApplyAnim(JObject cmd)
        {
            var id = (string)cmd["id"];
            if (string.IsNullOrEmpty(id)) return;
            // Stop form: `anim id=x stop=all` / `stop=<channel/prop>`.
            var stop = (string)cmd["stop"];
            if (!string.IsNullOrEmpty(stop)) { SceneStopAnim(id, stop); return; }
            var payload = cmd["anim"];
            if (payload == null) return;
            LvnAnim anim;
            try { anim = payload.ToObject<LvnAnim>(); }
            catch { return; }
            if (!LvnAnim.Playable(anim)) return;
            // Channel: explicit if given, else derived from the first track's target
            // (e.g. "script:rotation", "script:face:y") — so distinct properties run
            // and compose at once, while re-animating the same property replaces it.
            var channel = (string)cmd["channel"];
            if (string.IsNullOrEmpty(channel))
            {
                var t0 = anim.tracks[0];
                channel = "script:" + (string.IsNullOrEmpty(t0.layer) ? "" : t0.layer + ":") + t0.prop;
            }
            // mode=queue → chain after the current anim on this channel (non-blocking)
            if ((string)cmd["mode"] == "queue") ScenePlayAnimQueued(id, channel, anim);
            else ScenePlayAnim(id, channel, anim);
        }

        /// <summary>ПОМРЕЖ — ведёт поток команд: кто отдаёт, о чём, чья
        /// побеждает. Публичен, потому что отправители снаружи (витрина,
        /// гардероб, катсцены) объявляют ему свои держания.</summary>
        public LvnStageManager Commands => _commands ??= NewStageManager();
        private LvnStageManager _commands;

        /// <summary>Помреж решает, ЧЬЯ команда играет, а исполняет её сцена:
        /// отложенное на занятый предмет он отдаёт обратно сюда, когда предмет
        /// освободится. Без этого обратного хода команда автора, пришедшая во
        /// время катсцены, просто пропадала бы — сценарий её второй раз не
        /// отдаст.</summary>
        private LvnStageManager NewStageManager()
            => new LvnStageManager { Apply = (cmd, sender) => ApplyDispatch(cmd, sender) };

        /// <summary>Дверь на сцену БЕЗ ПОДПИСИ — значит от истории: так писал
        /// сценарий с первого дня, и менять его смысла нет. Все остальные
        /// подписываются явно.</summary>
        public void ApplyStage(JObject command) => ApplyStage(command, LvnSender.Story);

        /// <summary>
        /// ЕДИНСТВЕННАЯ ДВЕРЬ НА СЦЕНУ — теперь с подписью отправителя.
        ///
        /// <para>Раньше она была коммутатором: смотрела <c>op</c> и раздавала
        /// по обработчикам, не зная, кто прислал команду. Присылают шестеро, и
        /// в споре за один предмет побеждал тот, чей <c>await</c> вернулся
        /// позже. Теперь спор разрешает <see cref="Commands"/> — по старшинству
        /// и занятости, а не по случайности, и каждое решение попадает в
        /// журнал.</para>
        /// </summary>
        public void ApplyStage(JObject command, LvnSender sender)
        {
            if (command == null) return;
            if (!Commands.Admit(command, sender, out _)) return;
            // Кадр истории ведётся ЗДЕСЬ, у единственной двери: любая другая
            // точка записи однажды осталась бы без обновления, и модель начала
            // бы расходиться с экраном — а расходящаяся модель хуже, чем её
            // отсутствие.
            RememberInStoryFrame(command, sender);
            ApplyDispatch(command, sender);
        }

        /// <summary>Исполнение команды, УЖЕ разрешённой Помрежем: сюда приходят
        /// и свежие команды, и отложенные, доигранные после освобождения
        /// предмета. Спор решается один раз — второй проверки быть не должно,
        /// иначе отложенное упёрлось бы в то же держание.</summary>
        private void ApplyDispatch(JObject command, LvnSender sender)
        {
            switch ((string)command["op"])
            {
                case "bg": LvnAsync.Fire(ApplyBgAsync(command), "ApplyBg"); break;
                case "bg3d": LvnAsync.Fire(ApplyBg3DAsync(command), "ApplyBg3D"); break;
                case "actor": LvnAsync.Fire(ApplyActorAsync(command, sender: sender), "ApplyActor"); break;
                case "obj": LvnAsync.Fire(ApplyActorAsync(command, sender: sender), "ApplyActor"); break; // any placeable sprite
                case "clear": ApplyClear(sender); break; // everyone off stage, scenery untouched
                case "ui": ApplyUi(command); break;  // дерево интерфейса из сценария
                case "cutscene": ApplyCutscene(command); break;  // кадр без интерфейса
                case "anim": ApplyAnim(command); break; // script-driven tween / path
                case "fade": ApplyFade(command); break;
                case "dim": ApplyDim(command); break;
                case "flash": ApplyFlash(command); break;
                case "tint": ApplyTint(command); break;
                case "blur": ApplyBlur(command); break;
                case "sfx":
                    // Спрайтовый эффект по id актёра; вне канвас-пути — no-op.
                    // Запоминать грим отдельно не нужно: кадр истории вбирает
                    // его вместе с позой (LvnFrame.Absorb), поэтому возврат
                    // после катсцены приводит актёра тем же — с гримом. Здесь
                    // стоял пустой метод-заглушка, чьё описание обещало ровно
                    // эту работу: код выглядел рабочим и уводил от настоящего
                    // места.
                    _renderer?.TrySpriteFx((string)command["id"], command);
                    break;
                case "portal":
                    // Створ — СЛОЙ сцены: рисуется всегда, живёт за актёрами и
                    // переживает уборку эффектов (в отличие от `fx portal`).
                    ApplyPortalCore((string)command["sprite_url"]);
                    _renderer?.TryPortal(command);
                    break;
                case "fx":
                    // Мультиэффект кадра; без камеры (overlay-канвас, UITK-путь)
                    // честный no-op — сцена просто остаётся чистой.
                    _renderer?.TryFx(command);
                    break;
                case "camera": ApplyCamera(command); break;
                case "particles":
                    _particles.Set((string)command["type"], BoolOr(command["on"], true));
                    break;
                case "audio": LvnAsync.Fire(_audio.ApplyAsync(command, Assets, _cts.Token), "Apply"); break;
                case "text": ApplyText(command); break; // reactive HUD/stat label
                case "save": SaveSlot(command); break;
                case "load": LoadSlot(command); break;
                case "text_pace": ApplyTextPace(command); break;
                case "wait":
                    _awaitingWait = true;
                    StartCoroutine(WaitCoroutine(command));
                    break;
                case "input": ApplyInput(command); break; // text entry → story var
                case "preload":
                    LvnAsync.Fire(PreloadAssetsAsync(command), "PreloadAssets");
                    break;
                case "hint": ApplyHint(command); break;
                // unknown-but-registered ops are simply not drawn.
            }
        }

        /// <summary>
        /// ЗАБЫТЬ ПОДСКАЗКУ — таймер и все ссылки на её части разом.
        ///
        /// <para>Подсказка — не одно поле, а четыре: хозяин движения, карточка,
        /// надпись и отсчёт автоухода. Разбирали их по месту, и слово в слово
        /// одинаково — при сбросе сцены и при сносе документа; третье место
        /// гасило только отсчёт. Пятая часть подсказки (значок, кнопка) попала
        /// бы в одно место из двух, и осталась бы ссылка на элемент,
        /// оторванный от дерева: он выглядит живым и молча не рисуется.</para>
        ///
        /// <para>Отсчёт гасится ПЕРВЫМ: он единственный переживает элементы и
        /// способен разбудить уже забытое.</para>
        /// </summary>
        private void ForgetHint()
        {
            _hintHide?.Pause();
            _hintHide = null;
            _hintHost = null;
            _hintCard = null;
            _hintLabel = null;
        }

        // `hint text="…" show=true [duration=0]` — a small card that pops up
        // top-center over the scene: a tutorial nudge, a stat unlock, a note tied
        // to a specific beat. `show=false` (or empty text) dismisses it; a positive
        // `duration` auto-dismisses after that many seconds. Text interpolates
        // {vars} like dialogue. Lives on the HUD layer, ignores the pointer.
        private void ApplyHint(JObject cmd)
        {
            if (_labelLayer == null) return;
            var text = (string)cmd["text"] ?? "";
            bool show = BoolOr(cmd["show"], true) && text.Length > 0;

            _hintHide?.Pause();
            _hintHide = null;

            if (!show)
            {
                HideHint();
                return;
            }

            if (_hintCard == null)
            {
                // Animate a full-width centred host, not the pill itself. The pill
                // needs its own layout transform for centring; animating that same
                // transform made it jump sideways when a vertical slide began.
                _hintHost = new VisualElement { name = "vn-hint-host", pickingMode = PickingMode.Ignore };
                _hintHost.style.position = Position.Absolute;
                _hintHost.style.left = 0; _hintHost.style.right = 0;
                _hintHost.style.top = Length.Percent(12);
                _hintHost.style.alignItems = Align.Center;
                _hintHost.style.display = DisplayStyle.None;

                _hintCard = new VisualElement { name = "vn-hint", pickingMode = PickingMode.Ignore };
                _hintCard.style.maxWidth = Length.Percent(82);
                _hintCard.style.flexDirection = FlexDirection.Row;
                _hintCard.style.alignItems = Align.Center;
                LvnAir.Pad(_hintCard, LvnTokens.Space3, LvnTokens.Space2);
                _hintCard.style.overflow = Overflow.Hidden;

                // The icon is deliberately part of the toast instead of a
                // nameplate. A system message must read as UI chrome at a glance,
                // never as a character called "Подсказка".
                var icon = new Label("i") { name = "vn-hint-icon", pickingMode = PickingMode.Ignore };
                icon.style.width = 36; icon.style.height = 36;
                icon.style.flexShrink = 0;
                icon.style.marginRight = LvnTokens.Space2;
                icon.style.unityTextAlign = TextAnchor.MiddleCenter;

                _hintLabel = new Label { name = "vn-hint-text", pickingMode = PickingMode.Ignore };
                _hintLabel.style.whiteSpace = WhiteSpace.Normal;
                _hintLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
                _hintLabel.style.flexShrink = 1;
                _hintCard.Add(icon);
                _hintCard.Add(_hintLabel);
                _hintHost.Add(_hintCard);
                _labelLayer.Add(_hintHost);
            }

            var bg = Theme != null ? Theme.PanelColor : new Color(0.03f, 0.055f, 0.075f, 0.94f);
            bg.a = Mathf.Max(bg.a, 0.94f);
            var accent = Theme != null ? Theme.SpeakerColor : new Color(0.1f, 0.9f, 0.82f, 1f);

            // A toast stays cheap and visually separate from dialogue: one
            // opaque/translucent surface, no fullscreen glass RenderTexture.
            UiGlass.Apply(_hintCard, 0f, bg);
            _hintCard.style.backgroundColor = bg;
            // Слева толще: подсказка — не рамка вокруг текста, а полоска,
            // от которой текст начинается.
            var border = UiColor.WithAlpha(accent, 0.7f);
            LvnChrome.Border(_hintCard, border, 1f);
            LvnChrome.EdgeOn(_hintCard, LvnSide.Left, border, 2f);
            float r = Mathf.Max(12f, (Theme != null ? Theme.PanelCornerRadius : 12f) * 0.65f);
            LvnChrome.Round(_hintCard, r);

            _hintLabel.style.color = Theme != null ? Theme.TextColor : Color.white;
            _hintLabel.style.fontSize = Theme != null
                ? Mathf.Max(22, Mathf.RoundToInt(Theme.BodyFontSize * 0.72f)) : 26;
            if (Theme != null) LvnFonts.Apply(_hintLabel, Theme.Font);
            var hintIcon = _hintCard.Q<Label>("vn-hint-icon");
            if (hintIcon != null)
            {
                hintIcon.style.backgroundColor = accent;
                hintIcon.style.color = bg;
                hintIcon.style.fontSize = LvnTokens.TextSm;
                LvnChrome.Round(hintIcon, LvnTokens.Radius);
                if (Theme != null) LvnFonts.Apply(hintIcon, Theme.Font);
            }
            _hintLabel.text = TextInterpolation.Apply(text, _player?.Vars);

            // Всплывает и утопает, а не мигает: подсказка появляется поверх
            // сцены без всякого предупреждения, и резкий скачок в углу глаза
            // читается как сбой, а не как сообщение.
            bool wasHidden = _hintHost.style.display == DisplayStyle.None;
            _hintHost.style.display = DisplayStyle.Flex;
            if (wasHidden)
                LvnAppear.Play(_hintHost, LvnAppearKind.SlideDown, true,
                    VnTheme.MotionMs(seconds: 0.22f));

            // A plain hint is a four-second toast. duration=0 remains the explicit
            // authoring escape hatch for a persistent tutorial card.
            float dur = NumOr(cmd["duration"], 4f);
            if (dur > 0f)
                _hintHide = _labelLayer.schedule
                    .Execute(HideHint)
                    .StartingIn((long)(dur * 1000f));
        }

        /// <summary>Убрать табличку — всегда одним способом. Два места гасили её
        /// по-разному (одно с анимацией, другое мгновенно), и подсказка по
        /// таймеру исчезала иначе, чем снятая сценарием.</summary>
        private void HideHint()
        {
            if (_hintHost == null || _hintHost.style.display == DisplayStyle.None) return;
            LvnAppear.Play(_hintHost, LvnAppearKind.SlideDown, false,
                VnTheme.MotionMs(seconds: 0.18f),
                () => { if (_hintHost != null) _hintHost.style.display = DisplayStyle.None; });
        }

        // ── wait / preload ──────────────────────────────────────────────────

        private IEnumerator WaitCoroutine(JObject cmd)
        {
            int gen = _clock.Claim(LvnStageClock.WaitLane); // таймер мой, пока его не отменят
            float ms = NumOr(cmd["ms"], 1000f);
            yield return new WaitForSecondsRealtime(ms / 1000f);
            if (!_clock.IsNewest(LvnStageClock.WaitLane, gen)) yield break; // отменён тапом или новым ожиданием
            _awaitingWait = false;
            if (_player != null && !_player.Finished)
                _player.Advance();
        }

        // A hotspot click that jumps the story must kill a pending `wait`, or its
        // deferred Advance() lands mid-flight somewhere else and skips a beat.
        private void CancelPendingWait()
        {
            _clock.Cancel(LvnStageClock.WaitLane);
            _awaitingWait = false;
        }

        private async Task PreloadAssetsAsync(JObject cmd)
        {
            if (Assets == null) return;

            var spriteUrls = new List<string>();
            var audioUrls = new List<string>();

            // ЦЕЛЫЙ СКЕЛЕТ ОДНОЙ СТРОКОЙ. Раньше автор грел спайн, называя
            // страницу атласа спрайтом: грелась картинка, а разметка (json и
            // атлас) всё равно ехала в миг показа. Комплект известен по одному
            // адресу — значит и греть его нужно комплектом.
            var spineRefs = new List<Lvn.Content.LvnSpineRef>();

            void Add(string url, string kind)
            {
                if (string.IsNullOrEmpty(url)) return;
                if (kind == "audio") { audioUrls.Add(url); return; }
                if (kind == "spine")
                {
                    var sp = Lvn.Content.LvnSpineRef.FromUrl(url);
                    if (sp == null) return;
                    spineRefs.Add(sp);
                    return;
                }
                spriteUrls.Add(url); // a Spine texture warms as a sprite too
            }

            // Batch form (`assets=[…]`) OR the terse single-asset form
            // (`preload url=… kind=…`) — the latter is how a chapter warms one
            // heavy Spine texture before its actor appears, killing the pop-in.
            if (cmd["assets"] is JArray assetArray)
                foreach (var a in assetArray)
                    Add((string)((JObject)a)["url"], (string)((JObject)a)["kind"]);
            else
                Add((string)cmd["url"], (string)cmd["kind"]);

            // Скелет греется своим трактом: сцена сама разберёт атлас и вытянет
            // все его страницы, а не только ту, что назвал автор.
            foreach (var sp in spineRefs)
            {
                var id = "preload:" + sp.json;
                LvnAsync.Fire(PrefetchSpineAsync(id, new Lvn.Content.LvnSpriteEntity
                {
                    kind = "spine",
                    spine = sp,
                }), "PreloadSpine");
            }

            if (spriteUrls.Count == 0 && audioUrls.Count == 0) return;

            var tasks = new List<Task>();
            if (spriteUrls.Count > 0)
                tasks.Add(Assets.PreloadAsync(spriteUrls, "sprite", _cts.Token));
            if (audioUrls.Count > 0)
                tasks.Add(Assets.PreloadAsync(audioUrls, "audio", _cts.Token));
            await Task.WhenAll(tasks);
        }

    }
}
