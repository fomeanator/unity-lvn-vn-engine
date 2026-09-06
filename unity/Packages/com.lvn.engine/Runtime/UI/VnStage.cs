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
    /// The drop-in stage: a <see cref="MonoBehaviour"/> that composes the
    /// reference layers (background → actors → dialogue → choices) into a
    /// <see cref="UIDocument"/> and plays a <c>.lvn</c> through an
    /// <see cref="LvnPlayer"/>. Implements <see cref="ILvnStage"/> itself, so
    /// dropping it on a GameObject with a UIDocument and a script TextAsset is a
    /// playable game. Swap <see cref="Theme"/> to restyle, assign
    /// <see cref="Assets"/> to load art.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed partial class VnStage : MonoBehaviour, ILvnStage
    {
        // ── ВОПРОСЫ ПРИНАДЛЕЖНОСТИ ────────────────────────────────────────
        // Сцена насквозь асинхронна: пока грузится спрайт или доигрывает уход
        // окна, глава может смениться, сохранение — загрузиться, а игрок —
        // нажать дальше. Ответ «моё ли это ещё?» стоял формулами по файлам, и
        // формулы разошлись: где-то пара «плеер и поколение», где-то только
        // поколение. Разойтись им нельзя — на этой паре держится защита от
        // «чужой работы», из-за которой сцена показывала кадр прошлой главы.

        /// <summary>ЭТО ВСЁ ЕЩЁ МОЙ ПРОГОН: тот же плеер и то же поколение
        /// запуска. Пара стояла четырьмя копиями в двух файлах.</summary>
        private bool RunCurrent(LvnPlayer player, int gen)
            => _player == player && _startGen == gen;

        /// <summary>МОЯ ЛИ ЭТО ПОДМЕНА ОКНА. Реплика меняется движением: старая
        /// уходит, новая приходит — а между этими шагами успевает прийти
        /// следующая команда. Поколение отсекает опоздавших.</summary>
        private bool SwapCurrent(int gen) => gen == _dialogueSwapGeneration;

        /// <summary>Моя подмена И окно ещё на месте — для тех, кто окно
        /// трогает. Отдельно от <see cref="SwapCurrent"/> намеренно: в сценах
        /// без окна (NVL, выборы без реплики) его отсутствие — норма, и
        /// приписать эту проверку всем значило бы молча пропускать работу.</summary>
        private bool BoxMine(int gen) => SwapCurrent(gen) && _dialogue != null;

        [Tooltip("Look-and-feel for the built-in components.")]
        public VnTheme Theme = new VnTheme();

        [Tooltip("A .lvn file as a TextAsset; played on enable. Optional — call Play() instead.")]
        public TextAsset Script;

        /// <summary>Resolves <c>sprite_url</c>s to sprites. Null → solid-colour
        /// backgrounds and no character art. Assign in code before play.</summary>
        public ILvnAssets Assets;

        /// <summary>Optional sprite/entity catalog (from <c>manifest.sprites</c>).
        /// When set, <c>actor</c>/<c>obj</c>/<c>bg id="..."</c> resolve their
        /// layers (with conditional <c>when</c> display) from it instead of raw
        /// urls. Assign from the host's manifest before play.</summary>
        public SpriteCatalog Catalog;

        /// <summary>Optional localization catalog (<c>text_id</c> → string) for the
        /// active language. Assign before Play — or mid-chapter (a language switch):
        /// the running player picks it up and renders subsequent lines with it.</summary>
        public System.Collections.Generic.IReadOnlyDictionary<string, string> Strings
        {
            get => _strings;
            set { _strings = value; if (_player != null) _player.Strings = value; }
        }
        private System.Collections.Generic.IReadOnlyDictionary<string, string> _strings;

        /// <summary>
        /// ОФОРМЛЕНИЕ ФОРМЫ ВВОДА, каким его задал автор (<c>ui.name_input</c>).
        ///
        /// <para>Блок в манифесте существовал с самого начала и был заполнен
        /// целиком — вопрос, подпись поля, текст кнопки, пять цветов, — но НЕ
        /// ЧИТАЛСЯ НИКЕМ: форма одевалась только темой диалога. Автор написал
        /// правильно и получил молча не то, а узнать об этом было неоткуда.</para>
        ///
        /// <para>Порядок старшинства: команда новеллы (<c>input prompt=…</c>)
        /// сильнее манифеста — она ближе к месту, где задают вопрос; манифест
        /// сильнее темы; тема сильнее умолчаний движка.</para>
        /// </summary>
        public Lvn.Content.NameInputConfig NameInput;

        [Tooltip("Optional content folder. If set and Assets is unwired, the stage " +
                 "loads sprites from here via DirectoryAssets — so a scene plays with " +
                 "art straight from Play, no code. Editor/standalone file paths.")]
        public string ContentRoot;

        // СЦЕНА РИСУЕТСЯ ОДНИМ СПОСОБОМ — uGUI-канвасом (60 кадров, Spine,
        // шейдеры, меши). Вторая реализация на UI Toolkit жила здесь за
        // переключателем UseCanvasScene и была снесена: продукт всегда шёл
        // канвасом, а поддерживать приходилось обе.
        private ISceneRenderer _renderer;  // bg + actors + camera, renderer-agnostic
        private ParticleField _particles;
        private DialogueBox _dialogue;
        // Safe-area hosts: dialogue/choices/labels and the quick menu are inset to
        // Screen.safeArea; scene, weather and FX veils stay full-bleed (see Build).
        private SafeAreaElement _chromeSafe, _menuSafe;
        private ChoiceList _choices;
        private VisualElement _labelLayer; // reactive HUD/stat text overlay (the `text` op)
        /// <summary>
        /// ПОДПИСЬ НА КАДРЕ — сама надпись и её живой шаблон, одной записью.
        ///
        /// <para>Памятей было две: элемент и текст с подстановками
        /// (<c>{expr}</c>), который пересчитывают каждый тик. Пока их две,
        /// «убрать подпись» — два удаления, а сброс сцены — две очистки, и
        /// написаны они в РАЗНЫХ ФАЙЛАХ (сброс кадра и сброс всей сцены).
        /// Разъехавшись, пара даёт подпись-призрак: элемент снят, шаблон
        /// остался и продолжает считаться каждый тик, — или наоборот, надпись
        /// висит и больше не обновляется.</para>
        /// </summary>
        private sealed class HudLabel
        {
            public Label El;
            public string Tmpl;   // живой текст с подстановками; пусто — надпись статична
        }

        private readonly Dictionary<string, HudLabel> _labels = new Dictionary<string, HudLabel>();
        private VisualElement _hintHost;   // centred motion host: keeps the card's layout transform intact
        private VisualElement _hintCard;   // top-center popup for the `hint` op
        private Label _hintLabel;
        private IVisualElementScheduledItem _hintHide; // auto-dismiss timer (duration>0)
        private FxLayer _fx;
        private StageAudio _audio;
        private StageMenu _menu;
        private TapBurstLayer _tapBurst;
        private Dictionary<string, CastEntity> _cast;
        private readonly Dictionary<string, LvnAnim> _talkAnims = new Dictionary<string, LvnAnim>(); // actor id → lip-sync anim
        private LvnPlayer _player;
        private CancellationTokenSource _cts;
        // Ждём ли касания по реплике. СВОЙСТВО, а не поле: слой `ui` обязан
        // узнавать об этом сразу, а точек сброса восемь — рано или поздно одна
        // осталась бы без оповещения. «Идёт реплика» — это именно ожидание
        // чтения, а не видимость окна: окно остаётся на экране и после того,
        // как игра ушла дальше, и по нему стадию определять нельзя.
        private bool _awaitingTapFlag;
        private bool _awaitingTap
        {
            get => _awaitingTapFlag;
            set
            {
                if (_awaitingTapFlag == value) return;
                _awaitingTapFlag = value;
                NotifyUiStage();
            }
        }
        private bool _awaitingWait;
        // Ожидание живёт дорожкой Хронометриста (LvnStageClock.WaitLane): прыжок
        // по горячей точке посреди `wait` отменяет таймер — иначе отложенный
        // Advance срабатывал ПОСЛЕ прыжка и съедал реплику в другом месте.
        // Current on-screen beat — restored after a live theme rebuild so ApplyTheme
        // is safe to call mid-line (realtime theming keeps the line/choices visible).
        private bool _sayUp;
        // Every text/choice beat replaces the previous card through a short
        // dissolve. The generation invalidates delayed callbacks on reset/new beat.
        private int _dialogueSwapGeneration;
        /// <summary>
        /// ПОЧЕМУ СТОПКА ВЫБОРА ПОГАШЕНА — причин две, а ручка одна.
        ///
        /// <para>Гасят её обработка нажатия (идёт оплата или доигрывается
        /// такт) и незакончившаяся хореография (актёр ещё входит в кадр).
        /// Каждая держала <c>SetEnabled</c> сама, парами «выключил — включил»
        /// по девяти местам, и вторая снимала первую: пока шла оплата, конец
        /// входа актёра ЗАЖИГАЛ стопку обратно и заново запускал отсчёт
        /// выбора. Второе нажатие ловил отдельный флаг, а вот кнопки светились
        /// живыми и срок тикал.</para>
        /// </summary>
        private readonly LvnReasons _choiceLocks = new LvnReasons();

        /// <summary>Причины, по которым выбор погашен. Слова, а не числа: на
        /// вопрос «почему кнопки не нажимаются» отвечает журнал.</summary>
        private const string ChoiceLockCommit = "нажатие в обработке";
        private const string ChoiceLockChoreography = "хореография не доиграла";
        private const string ChoiceLockArming = "стопка только что появилась";

        /// <summary>
        /// СКОЛЬКО СТОПКА ВЫБОРА НЕ ПРИНИМАЕТ НАЖАТИЙ после появления.
        ///
        /// <para>Человек читает, тапая в своём ритме, и следующий тап уже
        /// летит, когда на месте текста вырастает стопка вариантов. Без паузы
        /// выбор делает палец, а не человек, — а в новелле решения необратимы.
        /// </para>
        ///
        /// <para>Замер 06.09: с анимацией появления окно было 215 мс, а с
        /// темой БЕЗ анимации — ноль, то есть защита оказалась побочным
        /// эффектом украшения. Хуже всего это било по тем, кто выключил
        /// движение в настройках доступности: у них украшения нет вовсе.
        /// Поэтому пауза теперь своя и не зависит ни от темы, ни от анимаций.
        /// </para>
        ///
        /// <para>180 мс — меньше времени реакции на новый экран (около 250 мс),
        /// то есть намеренного тапа она не съедает, но перекрывает хвост
        /// предыдущего.</para>
        /// </summary>
        internal const int ChoiceArmingMs = 180;

        /// <summary>Нажатие уже обрабатывается (оплата, доигрывание такта).
        /// Отдельный вопрос от «стопка погашена»: тап по реплике гасит именно
        /// он, а не всякое погашение.</summary>
        private bool _choiceCommitInFlight => _choiceLocks.Has(ChoiceLockCommit);

        /// <summary>Единственное место, где стопка выбора включается и
        /// гаснет. Пока их было девять, «включить» и «выключить» стояли
        /// парами — и пара, написанная одной причиной, снимала чужую.</summary>
        private void PaintChoiceEnabled() => _choices?.SetEnabled(!_choiceLocks.Any);
        // A rebuilt UIDocument has no old pixels to fade out even if _sayUp
        // remembers that the player is parked on a line. The first rerender onto
        // that fresh surface must install the current card directly.
        private bool _dialogueSurfaceFresh;
        private IReadOnlyList<LvnOption> _curChoices;

        /// <summary>Public access to the underlying player for save/load.</summary>
        public LvnPlayer Player => _player;

        /// <summary>Открыть квик-меню извне (единый навбар); pane "history" —
        /// сразу в историю.</summary>
        public void OpenQuickMenu(string pane = null) => _menu?.Open(pane);

        private readonly List<(string who, string text, string style)> _backlog
            = new List<(string, string, string)>();

        /// <summary>Read-only access to the dialogue history.</summary>
        public IReadOnlyList<(string who, string text, string style)> Backlog => _backlog;

        private bool _built;
        private VisualElement _uiRoot; // panel root — normalizes the pointer position
        // Clickable hotspots for the Canvas scene (which has no uGUI raycaster) —
        // hit-tested in OnPointerDown against each actor's real on-screen RectTransform
        // (so the clickable area matches the visible sprite exactly).
        private readonly List<(string id, System.Action onClick)> _hotspots = new List<(string, System.Action)>();

        // UIDocument's rootVisualElement can be null in OnEnable (it initializes
        // its panel on its own OnEnable, and script order isn't guaranteed), so we
        // also try in Start, by which point the panel is ready. Whichever sees a
        // non-null root first builds; the other is a no-op.
        // Renew the cancellation source on every enable — it is the token every
        // asset load uses. Build() is gated by `_built`, so without this a
        // disable/enable cycle would leave the source cancelled (from OnDisable)
        // and every bg/actor/audio load would throw immediately → a blank stage.
        private void OnEnable()
        {
            // Прежний источник уже ОТМЕНЁН в OnDisable — гасить нечего, только
            // отпустить. Retire звать не нужно: он бы отменил повторно, а тут
            // важна не отмена, а свежий источник для нового включения.
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            // Скрытие интерфейса просят и не через сцену (это общая роль), а
            // видимость слоёв ставит она. Без подписки чужая просьба доехала
            // бы до Режиссёра и осталась там.
            LvnScreenDirector.Current.Changed += ApplyChromeVisibility;
            // Кадры считает наблюдатель панели (с первого кадра, см. LvnPanel);
            // сцена лишь рассказывает счётчику то, что знает только она.
            Lvn.LvnFrameWatch.Busy = BusyNote;
            Build();
        }
        private void Start() => Build();

        /// <summary>Чем занята сцена — пояснение к запинке; спрашивают только у неё.</summary>
        private string BusyNote()
        {
            var busy = string.Join(",", BuildingSkeletons());
            return busy.Length > 0 ? $" (spine builds in flight: {busy})" : "";
        }
        // Start runs once per component lifetime — after a disable/enable cycle
        // it can't retry a Build whose panel wasn't ready yet, so keep a cheap
        // per-frame guard until the chrome exists.
        private void Update()
        {
            if (!_built) Build();
            // The platform BACK (Android back = Escape in Unity): closes the
            // TOPMOST surface. Кто именно наверху — не дело сцены: у неё была
            // своя лесенка условий, а теперь есть Режиссёр. Сама сцена никогда
            // не выходит из главы по «назад», а модаль оболочки (магазин из
            // гейта) забирает «назад» себе — её стек ведёт NovelShell.
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                // Кто наверху — знает Режиссёр; сцена лишь исполняет «назад»
                // для СВОИХ поверхностей. Модаль оболочки наверху — «назад»
                // её, и сцена молчит.
                switch (LvnScreenDirector.Current.BackTarget)
                {
                    case LvnScreenDirector.StoryPanel: PanelCancelRequested?.Invoke(); break;
                    case LvnScreenDirector.QuickMenu: _menu?.Close(); break;
                }
            }
            if (_renderer is CanvasSceneRenderer csr)
            {
                // Переход полотна в «пусто и бело» логируем СОБЫТИЕМ, а не
                // каждый кадр: в логе должно быть видно, что случилось прямо
                // перед ним. Лечит это Лекарь (см. HireHealer).
                bool blank = csr.BackdropBlankWhite;
                if (blank != _bgWasBlankWhite)
                {
                    _bgWasBlankWhite = blank;
                    LvnLog.Trace($"[lvn-bg] полотно {(blank ? "СТАЛО ПУСТЫМ И БЕЛЫМ" : "снова с картинкой")}: "
                              + $"{csr.BackdropState}, HasBackdrop={HasBackdrop}, epoch={_stageEpoch}, кадр {Time.frameCount}");
                }
                Healer.Tick(LvnClock.Now());
                if (LvnClock.Now() >= _nextDriftCheck)
                {
                    _nextDriftCheck = LvnClock.Now() + 0.5f;
                    CompareFrameToScreen();
                }
            }
        }

        private void Build()
        {
            if (_built) return;
            var root = GetComponent<UIDocument>().rootVisualElement;
            if (root == null) return; // panel not ready yet — Start will retry
            _uiRoot = root;
            _built = true;
            // ОТКЛИК НА НАЖАТИЕ — на корне СЦЕНЫ тоже. Механизм заведён общим
            // и висит на корне, а не на каждой кнопке, ровно затем, чтобы про
            // него не пришлось помнить в каждом новом месте. И всё же про него
            // забыли — здесь: оболочка отвечала на палец, а игровые
            // поверхности (окно диалога, выборы, форма ввода имени) молчали,
            // хотя кнопку формы даже пометили нажимаемой (VnStage.Input).
            // Пометка была, слушателя не было.
            LvnMotion.EnableTapFeedback(root);
            LvnPlayer.Log = m => LvnLog.Trace("[lvn] " + m); // full step trace to the console

            if (Assets == null && !string.IsNullOrEmpty(ContentRoot))
                Assets = new DirectoryAssets(ContentRoot);
            root.Clear();
            root.style.flexGrow = 1;

            // Scene = background + actors + camera. Канвас-сцена живёт на
            // СОСЕДНЕМ канвасе ПОД этой UI Toolkit-панелью, поэтому окно, выборы
            // и меню всегда рисуются поверх сцены.
            {
                // sortingOrder below the panel (10) so the UITK chrome composites on top.
                var scene = new World.WorldStage(transform, sortingOrder: LvnFloor.Scene);
                scene.Clock = _clock;   // сроки своих кроссфейдов рендерер сдаёт Хронометристу
                scene.SetBackgroundColor(Color.black);
                _renderer = new CanvasSceneRenderer(scene);
                LvnAsync.Fire(ApplyDefaultBackdropAsync(scene), "ApplyDefaultBackdrop"); // seamless tiled filler instead of flat black
            }
            HireHealer();   // недуги сцены — под наблюдение с первого кадра

            _particles = new ParticleField();
            _panelHost = null; // died with the previous panel root — recreate lazily
            MakeChrome();       // окно реплики и стопка выборов — собраны и привязаны
            _fx = new FxLayer();

            _labelLayer = new VisualElement { name = "vn-labels", pickingMode = PickingMode.Ignore };
            LvnChrome.Stretch(_labelLayer);

            // Шрифт темы — на корень: unityFontDefinition наследуется вниз, и
            // сцена, интерфейс и диалог получают одну гарнитуру разом. Ставить
            // его в каждом экране значит однажды забыть про один из них.
            LvnFonts.ApplyDefault(root);
            // Сцена — второе живое дерево: гардероб и диалог живут в нём, и
            // без объявления они переодевались только при перезаходе.
            LvnRedress.Register(root);

            root.Add(_particles);   // weather sits over the scene, under the UI
            // Chrome lives inside the device SAFE AREA (never under a notch /
            // home indicator); the scene, weather and the FX veil stay full-bleed
            // so art and fades cover the physical screen edge to edge.
            _chromeSafe = new SafeAreaElement();
            _chromeSafe.Add(_dialogue);
            _chromeSafe.Add(_choices);
            _chromeSafe.Add(_labelLayer); // HUD/stat labels above dialogue/choices
            root.Add(_chromeSafe);
            root.Add(_fx);          // top: fades/dim veil everything below
            _menu = new StageMenu(this, Theme);
            _menuSafe = new SafeAreaElement();
            _menuSafe.Add(_menu);   // quick menu above even the FX veil — always reachable
            root.Add(_menuSafe);

            // Тап-салют (ui.stage.tap_burst): сердечки из точки КАЖДОГО
            // касания — trickle-down видит тап раньше кнопок и не мешает им.
            // Через дом закрытого слова: опечатка поднимала слой салюта и
            // подписку на каждое касание, но не рисовала НИЧЕГО — тише, чем
            // выключенный салют, и дороже.
            string burst = LvnAuthorWord.Pick(Theme?.TapBurst, "ui.stage.tap_burst", "", "hearts");
            if (burst == "hearts")
            {
                _tapBurst = new TapBurstLayer();
                root.Add(_tapBurst); // поверх всего хрома
                root.RegisterCallback<PointerDownEvent>(evt =>
                {
                    if (_tapBurst != null)
                        _tapBurst.Burst(_tapBurst.WorldToLocal(evt.position));
                }, TrickleDown.TrickleDown);
            }
            // Reactive tick: re-evaluate every live label's {expr} template against the
            // current variables so on-screen stats track changes (incl. background ones).
            root.schedule.Execute(RefreshLabels).Every(200);

            // Auto-advance: hands-free reading — once a line finishes revealing and
            // its reading delay passes, advance as if tapped. Choices always wait.
            root.schedule.Execute(AutoAdvanceTick).Every(100);

            // Skip: fast-forward gear (~13 lines/s), stops on anything interactive.
            root.schedule.Execute(SkipTick).Every(75);

            // Player comfort settings (dialogue window opacity now, live on change).
            LvnPrefs.Changed -= OnPrefsChanged;
            LvnPrefs.Changed += OnPrefsChanged;
            // Wardrobe equips re-apply the actor live if it's on screen.
            LvnWardrobe.Changed -= OnWardrobeChanged;
            LvnWardrobe.Changed += OnWardrobeChanged;

            root.pickingMode = PickingMode.Position;
            root.RegisterCallback<PointerDownEvent>(OnPointerDown);
            root.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            root.RegisterCallback<PointerUpEvent>(OnPointerUp);
            root.RegisterCallback<PointerCancelEvent>(_ => OnPointerCancelled());
            root.RegisterCallback<PointerCaptureOutEvent>(_ => OnPointerCancelled());
            // Desktop convenience (the Ren'Py convention): wheel-up steps back one beat.
            // Follows the theme's rollback switch — a title that cut the feature
            // (ui.menu.show_rollback:false) cuts the gesture with it.
            root.RegisterCallback<WheelEvent>(evt =>
            {
                if (InputBlocked) return;
                if (Theme != null && !Theme.MenuShowRollback) return;
                if (evt.delta.y < 0f && RollbackStep()) evt.StopPropagation();
            });

            // Audio channels (music/ambient/sfx) live in their own component.
            _audio = gameObject.AddComponent<StageAudio>();

            _cts ??= new CancellationTokenSource(); // OnEnable usually made it; safety for a direct Build()

            // A disable/enable cycle rebuilt the chrome: an open quick menu died
            // with the old panel WITHOUT running Close() — its input block must
            // not orphan (the panel-host block re-derives from IsOpen anyway).
            _inputBlockedFlag = false;
            _clock.Release(PanelGuardBarrier);

            if (_player != null)
            {
                // The chrome was rebuilt under a LIVE player (a disable/enable
                // cycle — see OnDisable): re-render the scene and the current
                // beat on the new panel, the same recipe rollback uses.
                _player.OnSay -= RecordSay; // OnDisable unhooked it; twice would double-log
                _player.OnSay += RecordSay; // resubscribe even before the first say exists
                _dialogueSurfaceFresh = true;
                var snap = _player.PopCurrent();
                if (snap != null)
                {
                    _player.Restore(snap);
                    _suppressDupSay = true; // the re-run beat is already in the backlog
                    int at = _player.Index;
                    _player.ReplayVisuals(at);
                    _player.ContinueFrom(at);
                }
            }
            else if (Script != null) Play(Script.text);
        }





        // Звук отклика на действие игрока (клик/выбор) + луп клавиатуры,
        // который печать строки водит через RevealingChanged диалога.
        private AudioClip _sndClick, _sndChoice, _sndType;

        private void PlayUiSound(AudioClip clip) =>
            _audio?.PlayUi(clip, Theme != null ? Theme.UiSoundVolume : 1f);

        // Печать началась → луп клавиатуры; кончилась (естественно, тапом или
        // сменой строки) → тишина. Клип может доехать позже первой реплики —
        // не страшно: следующая печать его подхватит.
        private void OnDialogueRevealing(bool on)
        {
            if (on) _audio?.PlayTypingLoop(_sndType, Theme != null ? Theme.UiSoundVolume : 1f);
            else _audio?.StopTypingLoop();
        }

        // Resolve the theme's typeface (manifest ui.dialogue.font) when no
        // explicit Font is assigned. Two forms:
        //   "MyFont"              — a font baked into the build (Resources);
        //   "/content/fonts/x.ttf" — a CUSTOM font served with the content —
        // downloaded into the disk cache (offline-safe like every other asset),
        // loaded from the file, applied via a chrome rebuild, and warmed with
        // the current chapter's glyph corpus so the late arrival doesn't hitch.
        private void OnDisable()
        {
            // НАРОЧНО только гасим: в полёте кто-то ещё читает токен, а
            // освобождает следующий OnEnable (или OnDestroy, если его не будет).
            _cts?.Cancel();
            LvnScreenDirector.Current.Changed -= ApplyChromeVisibility;
            if (_player != null) _player.OnSay -= RecordSay;
            if (_choices != null) _choices.OnSelected -= OnChoiceSelected;
            LvnPrefs.Changed -= OnPrefsChanged;
            LvnWardrobe.Changed -= OnWardrobeChanged;
            // Снимаем своё пояснение — и только своё: другая сцена могла уже
            // поставить собственное.
            if (Lvn.LvnFrameWatch.Busy == (System.Func<string>)BusyNote) Lvn.LvnFrameWatch.Busy = null;

            // UIDocument tears its panel down on disable and brings up a FRESH
            // empty root on the next enable — everything Build() made is orphaned
            // on the dead one. Drop the flag and the objects that outlive the
            // panel, so the next enable rebuilds the chrome (and re-renders a
            // live player's current beat) instead of leaving a blank stage.
            if (_built)
            {
                _built = false;
                _renderer?.Teardown();
                ReleaseActive3DSet();
                _renderer = null;
                _uiRoot = null;
                _menu = null;
                _labelLayer = null;
                ForgetHint();
                _labels.Clear();
                if (_audio != null) { Destroy(_audio); _audio = null; }
            }
        }

        private void OnPrefsChanged()
        {
            _dialogue?.SetUserOpacity(LvnPrefs.DialogOpacity);
            // Размер текста и гарнитура — тоже настройки, и их ждут ПРЯМО
            // СЕЙЧАС: игрок открыл настройки, чтобы разглядеть эту самую
            // реплику, а не следующую.
            _dialogue?.RefreshTextStyle();
            // И ВАРИАНТЫ ТОЖЕ. Они читают тот же размер, но читали его один раз
            // — при сборке. Игрок, открывший настройки прямо на выборе, получал
            // варианты прежнего кегля: со стороны настройка «не работает»
            // ровно там, где её и меняли.
            _choices?.RefreshTextStyle();
        }

        private void OnDestroy()
        {
            // Снос — единственный выход, после которого включения не будет, и
            // единственное место, где источник отмены надо ОТПУСТИТЬ. OnDisable
            // его только гасит (в полёте кто-то ещё читает токен), а освобождал
            // прежний OnEnable — которого при сносе не случится. Без этого
            // источник уходил в мусор живым, вместе с регистрациями отмены,
            // и так на каждую снесённую сцену.
            Lvn.LvnCancel.Retire(_cts);
            _cts = null;
            ReleaseActive3DSet();
            Assets?.UnloadAll();
            // The spine integration's static cache holds SkeletonData/materials
            // built around textures UnloadAll just destroyed — flush it with them.
            LvnSpineBridge.ClearCache?.Invoke();
            if (_pendingThumb != null) Destroy(_pendingThumb);
        }

        /// <summary>Close the quick menu if it's open (host screens that take
        /// over from a menu tap call this so the scrim doesn't linger).</summary>
        public void CloseQuickMenu() => _menu?.Close();

        /// <summary>True while the shared story panel (wardrobe…) is up — the
        /// quick-menu chrome polls this and keeps itself off the screen.</summary>
        public bool PanelOpen => _panelHost != null && _panelHost.IsOpen;

        /// <summary>ПОСМОТРЕТЬ, ЧТО НАДЕЛ. Панель занимает низ экрана, а наряд
        /// нарисован в полный рост — примеряя, игрок видит героиню по пояс.
        /// Этот режим убирает с глаз панель и весь интерфейс, НЕ закрывая
        /// панель: примерка продолжается, история по-прежнему не принимает
        /// касаний, а любое касание возвращает всё назад.
        ///
        /// <para>Отдельный режим, а не переиспользование долгого нажатия:
        /// то живёт на реплике и отпускается вместе с пальцем, это — на
        /// открытой панели и держится, пока игрок смотрит.</para></summary>
        public bool PanelPeeking { get; private set; }

        public void SetPanelPeek(bool on)
        {
            if (PanelPeeking == on) return;
            PanelPeeking = on;
            if (_panelHost != null)
                _panelHost.style.visibility = on ? Visibility.Hidden : Visibility.Visible;
            if (on) HideChrome(LvnScreenDirector.PeekReason);
            else ShowChrome(LvnScreenDirector.PeekReason);
        }
    }
}
