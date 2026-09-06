using System.Collections.Generic;

namespace Lvn.Content
{
    /// <summary>
    /// The content manifest the client fetches from a backend: the catalog of
    /// titles and the top-level (boot/menu) asset set. Plain serializable POCOs —
    /// deserialize your server's JSON into these (field names match the bundled
    /// Go server template) and hand the result to <see cref="DownloadManager"/>.
    /// Everything is optional and null-safe: a host that ships a single bundled
    /// chapter can leave <see cref="titles"/> null and drive a chapter directly.
    /// </summary>
    public sealed class LvnManifest
    {
        /// <summary>Top-level assets the client warms at boot/menu (shared UI
        /// chrome, title covers, chapter loading backgrounds), keyed by content
        /// path → <see cref="LvnAssetMeta"/>. When present this is the
        /// authoritative boot set; when empty the manager falls back to
        /// <see cref="DownloadManager.FallbackBootUi"/> + a manifest walk.</summary>
        public Dictionary<string, LvnAssetMeta> assets;

        /// <summary>The title catalog (anthology). Optional.</summary>
        public List<LvnTitle> titles;

        /// <summary>Hub collections grouping titles into browsable tiles
        /// (expeditions/dates/reality). Drives the <c>ui.browse.layout = "hub"</c>
        /// screen flow; ignored by the default carousel. Optional.</summary>
        public List<LvnCollection> collections;

        /// <summary>The game's default MAIN HEROINE — a sprite catalog entity id.
        /// The concept a single-heroine game leans on: her
        /// wardrobe fronts the skin shop and her portrait can front the profile.
        /// A title may override it with <see cref="LvnTitle.hero"/>. The skin shop
        /// itself still holds outfits for EVERY actor across all novels — this is
        /// just which one it opens on.</summary>
        public string hero;

        /// <summary>Manifest-driven theme for the built-in novel screens (loading,
        /// title card, name input). Optional — components use defaults when null.</summary>
        public LvnUiConfig ui;

        /// <summary>Language codes this content ships string catalogs for
        /// (<c>["en", "ru"]</c> → <c>&lt;script&gt;.en.json</c> sidecars exist).
        /// Non-empty enables the language picker in Settings; the script's
        /// inline text (the original) is always an implicit option.</summary>
        public List<string> languages;
        /// <summary>Код языка, на котором НАПИСАН контент ("ru"). Пилюля
        /// оригинала в настройках носит его имя, а не слово «Оригинал».</summary>
        public string language;

        /// <summary>The sprite/entity catalog, keyed by id. Scripts reference these
        /// ids (e.g. <c>actor id="mara" pose="sitting"</c>) instead of raw urls; the
        /// client resolves an id to its ordered layer urls and composites them. A
        /// simple sprite is just a one-layer entity; a character is a multi-layer
        /// entity parameterised by axes. Optional.</summary>
        public Dictionary<string, LvnSpriteEntity> sprites;

        /// <summary>The server-authored 3D set catalog. Scripts keep the stable,
        /// save-safe id (<c>bg3d id="forest"</c>); the manifest chooses the
        /// platform-specific AssetBundle URL and asset address. A set can also
        /// name a Resources fallback for first launch/offline builds.</summary>
        public Dictionary<string, Lvn3DSet> sets3d;

        /// <summary>Product economy rules layered over the wallet (chapter-entry
        /// gating). Optional — null, or an empty chapter_currency, means chapters
        /// are free and nothing gates entry (the default for every existing
        /// novel).</summary>
        public LvnEconomyConfig economy;
    }

    /// <summary>One remotely replaceable 3D backdrop.</summary>
    public sealed class Lvn3DSet
    {
        /// <summary>Platform key → bundle. Supported runtime keys are android,
        /// ios, windows, macos, linux and webgl; "default" is the optional
        /// catch-all.</summary>
        public Dictionary<string, Lvn3DBundle> platforms;

        /// <summary>Resources path used when the requested bundle is absent,
        /// unreachable or not cached yet. Example: "Sets/forest".</summary>
        public string fallback_resource;
    }

    /// <summary>A platform-specific Unity AssetBundle containing one set prefab.</summary>
    public sealed class Lvn3DBundle
    {
        /// <summary>Absolute URL or content-relative path, normally
        /// /content/sets/forest.android.bundle.</summary>
        public string url;

        /// <summary>Address of the prefab inside the bundle. Defaults to the set id.</summary>
        public string asset;

        /// <summary>Бандл несёт СЦЕНУ, а не префаб. Покупная сцена держит
        /// половину себя вне объектов — террейн (его данные отдельный ассет),
        /// деревья и траву террейна, запечённый свет; префаб всё это теряет,
        /// и на устройстве остаётся геометрия, висящая в пустоте. Сцена увозит
        /// всё как есть, поэтому наборы из магазинных сцен пакуются так.</summary>
        public bool scene;

        /// <summary>BuildPipeline AssetBundle hash. It participates in the
        /// in-memory identity, so a live manifest update can replace a bundle
        /// while keeping its public URL stable.</summary>
        public string hash;

        /// <summary>Informational byte size for authoring/progress UI.</summary>
        public long bytes;
    }

    /// <summary>
    /// Economy rules that gate content behind currency. Today: the chapter-entry
    /// gate (spend N of a currency — typically the regenerating "energy" — to
    /// start a chapter). All strings are optional with neutral English fallbacks;
    /// content localizes them here.
    /// </summary>
    public sealed class LvnEconomyConfig
    {
        /// <summary>Currency spent to ENTER a chapter (e.g. "energy"). Empty/null
        /// disables the gate entirely.</summary>
        public string chapter_currency;
        /// <summary>Amount spent per chapter entry; default 1 when a currency is set.</summary>
        public int? chapter_cost;
        /// <summary>Chapter ids that never charge (onboarding/tutorial). The first
        /// chapter can be listed here to keep it free.</summary>
        public List<string> free_chapters;

        // Gate popup copy (optional; English fallbacks in NovelApp).
        public string gate_title;    // e.g. "Not enough energy"
        public string gate_message;  // e.g. "You need 1 energy to open this chapter."
        public string gate_buy;      // confirm button → store; default "Store"
        public string gate_cancel;   // cancel button; default "Not now"
        public string gate_denied;   // shown when still short after the store; default gate_title

        /// <summary>Test-build faucet: a quick-menu item that grants currency on
        /// tap (partner/demo builds — "получить 100"). Absent → no button.</summary>
        public LvnDebugGrantConfig debug_grant;
    }

    /// <summary>The debug currency faucet (<c>economy.debug_grant</c>).</summary>
    public sealed class LvnDebugGrantConfig
    {
        public string currency; // e.g. "crystals"
        public int? amount;     // default 100
        public string label;    // menu item text; default "Получить {amount}"
    }

    /// <summary>
    /// A catalog entry: an ordered list of full-frame layer URL templates plus
    /// default axis values. Mirrors the engine's cast model — to draw the entity
    /// in a state, fill each template's <c>{axis}</c> tokens from the command's
    /// axis values (overlaid on <see cref="defaults"/>) and stack the layers. A
    /// layer whose token stays unresolved is skipped, so optional parts only
    /// appear when an axis supplies them.
    /// </summary>
    public sealed class LvnSpriteEntity
    {
        /// <summary>Optional display name (e.g. a speaker label).</summary>
        public string name;
        /// <summary>Optional speaker/name colour (hex) — light entity data.</summary>
        public string color;
        /// <summary>Ordered layers, bottom-to-top. Each layer is a URL template
        /// (with optional <c>{axis}</c> tokens) plus an optional <c>when</c>
        /// condition for conditional display. A simple sprite is one plain layer.</summary>
        public List<LvnLayer> layers;
        /// <summary>Default axis values (axis → value), overridden per-command.</summary>
        public Dictionary<string, string> defaults;
        /// <summary>Per-entity overrides for NAMED stage slots (label → x
        /// fraction): <c>{"left": 0.40}</c> re-tunes where THIS character stands
        /// when the script says <c>position=left</c> — without touching the
        /// global slot table, other characters, or the script. An explicit
        /// <c>x</c> in the command still wins.</summary>
        public Dictionary<string, float> slots;
        /// <summary>Allowed values per axis (axis → values) — drives the authoring
        /// dropdowns and validation; optional (free-form when absent).</summary>
        public Dictionary<string, List<string>> axes;
        /// <summary>Renderer kind: <c>static</c> (default) | <c>rigged</c> (named
        /// transform animations) | <c>spine</c> | <c>live2d</c> (future).</summary>
        public string kind;

        /// <summary>The width/height ratio of the box the layers were AUTHORED in.
        /// When set, the renderer locks the actor's on-screen box to this aspect
        /// (shrinking within the placed width/height) — layered/boned art keeps
        /// pixel-exact registration on every screen instead of each layer
        /// letterboxing differently. Unset (0) = legacy percent box.</summary>
        public float aspect;
        /// <summary>РОСТ ПЕРСОНАЖА В МЕТРАХ — его собственная мера, одна на все
        /// экраны и всех, кто его ставит. Высота в кадре считается из неё и из
        /// высоты сцены (<c>ui.stage.meters</c>, по умолчанию 2): героиня в
        /// 1.7 занимает 0.85 кадра — и в главе, и в меню, и в гардеробе.
        /// Пока роста не было, каждый ставящий называл свою долю экрана, и рост
        /// скакал на переходах. Пусто (0) = прежние <c>w=/h=</c>.
        /// Меряется по ФИГУРЕ (см. <see cref="content"/>), а не по холсту.</summary>
        public float meters;
        /// <summary>ГАБАРИТ ФИГУРЫ ВНУТРИ ХОЛСТА — доли холста, объединение
        /// непрозрачных областей всех слоёв всех вариантов (<c>lvnconv figure</c>).
        /// Художник оставляет вокруг персонажа воздух, и его доля у разных героев
        /// разная: пока постановка мерила холст, одинаковые <c>w=/h=</c> давали
        /// разный рост. С этим полем числа в скрипте означают размер видимой
        /// фигуры. Объединение (а не бокс текущего наряда) держит рост
        /// неподвижным при смене одежды и причёски. Пусто = мерим весь холст.</summary>
        public LvnBox content;
        /// <summary>Named animations (name → tracks). A <c>rigged</c> entity plays
        /// these via <c>actor play="name"</c>; <c>auto:true</c> animations loop on
        /// show. See <see cref="LvnAnim"/>.</summary>
        public Dictionary<string, LvnAnim> anim;

        /// <summary>For <c>kind: "spine"</c>: the exported skeleton's files. The
        /// runtime builds the skeleton from these at load (no Unity assets) —
        /// requires the optional spine-unity integration to be installed.</summary>
        public LvnSpineRef spine;

        /// <summary>The entity's wardrobe, keyed by axis (<c>"armor"</c>,
        /// <c>"outfit"</c>…): each slot lists the axis values as purchasable /
        /// equippable items. Presence of this block puts the character in the
        /// wardrobe screen; the layers themselves already handle "nothing
        /// equipped" (an unset axis skips its layer). Optional.</summary>
        public Dictionary<string, LvnWardrobeSlot> wardrobe;
    }

    /// <summary>Прямоугольник в долях (0..1) — левый-верхний угол и размер.
    /// Пока служит габаритом фигуры внутри холста (<see cref="LvnSpriteEntity.content"/>).</summary>
    public sealed class LvnBox
    {
        public float x, y, w, h;
    }

    /// <summary>One wardrobe slot — a themed group of items behind one axis
    /// (the "Armor" tab). Items map to the axis' values; buying uses the
    /// wallet's sku inventory, so ownership is server-authoritative.</summary>
    public sealed class LvnWardrobeSlot
    {
        public string name;               // tab label; default: the axis id
        public string icon;               // content url — the in-story sheet's tab icon
        /// <summary>Can the slot be emptied (item taken off)? Default true —
        /// matches the layer model where an unset axis draws nothing.</summary>
        public bool? removable;
        /// <summary>Story variable this axis drives (e.g. "Wardrobe.mainCh_Clothes").
        /// When set, equipping the axis in the in-story sheet writes the picked value
        /// back into the novel's state so its logic sees the choice. Empty = wardrobe-
        /// only (no write-back). JSON key is "var" (a C# keyword, hence the alias).</summary>
        [Newtonsoft.Json.JsonProperty("var")]
        public string storyVar;
        /// <summary>Axis this slot REFINES (e.g. hair colour's
        /// <c>subOf: "hairstyle"</c>): the slot loses its own tab and renders
        /// inside the parent's tab as a row of round swatches. Optional.</summary>
        public string subOf;
        public List<LvnWardrobeItem> items;
    }

    /// <summary>One wardrobe item: an axis value with shop presentation. No
    /// price (or 0) = free, owned from the start.</summary>
    public sealed class LvnWardrobeItem
    {
        public string value;    // the axis value this item sets (required)
        public string name;     // display name; default: the value
        public string icon;     // content url (a layer png works fine)
        public string currency; // price currency; with price>0 the item is bought
        public long price;
        public string rarity;   // optional tint key ("rare"/"epic"/…) → WardrobeConfig.rarity_colors
        /// <summary>Swatch fill ("#a93a2b") for sub-slot pickers (hair colour
        /// dots). Without it the swatch falls back to the item's icon.</summary>
        public string color;
    }

    /// <summary>A Spine export reference: content urls of the three files the
    /// Spine editor produces, plus the import scale and the idle to auto-play.</summary>
    public sealed class LvnSpineRef
    {
        /// <summary>
        /// ОДИН АДРЕС ВМЕСТО ЧЕТЫРЁХ ПОЛЕЙ.
        ///
        /// <para>Spine — отраслевой стандарт, и экспортирует он всегда один и
        /// тот же комплект: <c>имя.json</c> (или <c>.skel</c>), <c>имя.atlas</c>
        /// рядом и страницы, которые атлас называет сам. Значит автору
        /// достаточно назвать ОДИН файл — остальное выводится, а не
        /// переписывается руками в каталог спрайтов.</para>
        ///
        /// <para>Принимаем и папку: <c>/content/spine/hero/</c> означает
        /// <c>hero.json</c> внутри неё — так пишут, когда комплект назван по
        /// папке, а это самый частый случай выгрузки.</para>
        /// </summary>
        public static LvnSpineRef FromUrl(string url, string bg = null, string play = null)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            url = url.Trim();
            if (url.EndsWith("/"))
            {
                var name = url.TrimEnd('/');
                int slash = name.LastIndexOf('/');
                if (slash >= 0) name = name.Substring(slash + 1);
                if (name.Length == 0) return null;
                url += name + ".json";
            }
            string basePath = url;
            foreach (var ext in new[] { ".json", ".skel.bytes", ".skel" })
                if (basePath.EndsWith(ext, System.StringComparison.OrdinalIgnoreCase))
                {
                    basePath = basePath.Substring(0, basePath.Length - ext.Length);
                    break;
                }
            return new LvnSpineRef
            {
                json = url,
                // Spine пишет атлас либо как .atlas, либо (для веба и Unity) как
                // .atlas.txt. Берём второй: он же лежит у нас на сервере, а
                // сцена при промахе пробует первый.
                atlas = basePath + ".atlas.txt",
                bg = bg,
                auto = play,
            };
        }

        /// <summary>Запасное имя атласа — то же, но без <c>.txt</c>. Сцена
        /// пробует его, если первого на сервере не оказалось: два написания
        /// одного файла не должны быть заботой автора.</summary>
        public string AtlasFallback
            => string.IsNullOrEmpty(atlas) || !atlas.EndsWith(".atlas.txt")
                ? null : atlas.Substring(0, atlas.Length - 4);

        public string json;    // skeleton (.json export)
        public string atlas;   // .atlas text
        public string texture; // the atlas page image
        public float scale = 1f;
        /// <summary>Animation to loop on show (e.g. "idle"/"walk").</summary>
        public string auto;
        /// <summary>How the skeleton sizes itself to the screen — a
        /// self-contained container that depends on nothing but the canvas and
        /// its own posed bounds. Mirrors spine-unity's LayoutScaleMode:
        /// <c>"width"</c> (DEFAULT) width-to-width, height follows the aspect;
        /// <c>"height"</c> height-to-height; <c>"cover"</c> fill both (crop);
        /// <c>"contain"</c> fit inside (letterbox). Times <see cref="scale"/>.</summary>
        public string fit = "width";
        /// <summary>Optional static background image that BELONGS to the
        /// skeleton: rendered behind it inside the same container, so it scales,
        /// moves and drags together with the Spine and stays perfectly aligned
        /// (the base plate the animated overlay was authored on top of).</summary>
        public string bg;
    }

    /// <summary>A named animation: a set of tracks tweened over <c>duration</c>
    /// seconds, optionally looping. Engine-agnostic data — the runtime tweens an
    /// actor's transform; the authoring panel and language server read the names
    /// for autocomplete/validation.</summary>
    public sealed class LvnAnim
    {
        /// <summary>Loop forever (idle/breathe) vs play once (a gesture).</summary>
        public bool loop;
        /// <summary>When looping, ping-pong (forward then back) instead of
        /// restarting — with easing this is the cheap path to idle motion.</summary>
        public bool yoyo;
        /// <summary>Total length in seconds.</summary>
        public float duration = 1f;
        /// <summary>Auto-run: <c>"true"</c> loops on show (idle/blink);
        /// reserved <c>"speaking"</c> runs while the actor talks (v2). Null = manual.</summary>
        public string auto;
        /// <summary>The animated channels.</summary>
        public List<LvnAnimTrack> tracks;

        /// <summary>ЕСТЬ ЧТО ИГРАТЬ: анимация названа и в ней есть дорожки.
        /// Пустая — не ошибка, а обычное дело (автор объявил и не заполнил,
        /// имя не нашлось в каталоге), и играть её нечем. Проверка стояла
        /// ЧЕТЫРЕЖДЫ дословно, тремя частями каждая.</summary>
        public static bool Playable(LvnAnim a) => a?.tracks != null && a.tracks.Count > 0;
    }

    /// <summary>One animated property over time. <c>keys</c> is a list of
    /// <c>[time, value]</c> pairs (time in seconds, 0..duration).</summary>
    public sealed class LvnAnimTrack
    {
        /// <summary>Target layer id (<c>eyes</c>, <c>mouth</c>, …) for per-layer
        /// blink/lip-sync; null = the whole actor's transform.</summary>
        public string layer;
        /// <summary>Property: <c>x</c>/<c>y</c> (translate by a fraction of own size) |
        /// <c>screen_x</c>/<c>screen_y</c> (move the whole actor across the screen,
        /// fraction of the screen) | <c>scale</c> (uniform) | <c>scalex</c>/<c>scaley</c>
        /// (squash/stretch) | <c>rotation</c> (degrees) | <c>alpha</c> | <c>frame</c>
        /// (swap the layer's sprite by an axis value — blink/lip-sync/curl).</summary>
        public string prop;
        /// <summary>For <c>prop:"frame"</c> — which axis the frame values name
        /// (e.g. <c>eyes</c>, <c>mouth</c>). The layer's url template is resolved
        /// with this axis = the keyed value.</summary>
        public string axis;
        /// <summary>Easing curve: <c>linear</c> | <c>inOutSine</c> | <c>outCubic</c> |
        /// <c>outBack</c>. Default linear.</summary>
        public string ease;
        /// <summary>Interpolation between keys: <c>linear</c> (default) | <c>spline</c>
        /// (smooth Catmull-Rom through the keys) | <c>step</c>. Forward-compatible —
        /// the linear sampler treats unknown values as linear.</summary>
        public string interp;
        /// <summary>On the <c>screen_x</c> track of a path pair (<c>move …
        /// orient=true</c>): rotate the actor to face along the path tangent.</summary>
        public bool orient;
        /// <summary><c>[[time, value], …]</c>. Value is a number for transforms,
        /// or an axis value string for <c>frame</c> tracks.</summary>
        public List<object[]> keys;
    }

    /// <summary>
    /// Помощники над моделью манифеста. Здесь, а не в экранах: «главы новеллы
    /// по порядку» — свойство ДАННЫХ, и каждый экран, считавший его сам,
    /// заводил свою копию (карусель и экран детали расходились бы молча,
    /// стоило одному из них поменять сортировку).
    /// </summary>
    public static class LvnTitleExtensions
    {
        /// <summary>
        /// АРТ КАРТОЧКИ: изображение карточки, если автор его задал, иначе
        /// обложка.
        ///
        /// <para>Правило было записано ПЯТЬ раз — в двух видах карточек хаба, в
        /// его же деталях и в экране новеллы. Хуже, что предзагрузка и подсчёт
        /// «скачано ли» правила не знали вовсе и брали только обложку: новелла
        /// с заданным <c>card.image</c> объявлялась скачанной, а карточка потом
        /// ждала сеть — грелся не тот файл, который показывают.</para>
        /// </summary>
        public static string CardArt(this LvnTitle t) => t?.card?.image ?? t?.cover_url;

        /// <summary>Все главы новеллы, по возрастанию номера.</summary>
        public static List<LvnChapter> ChaptersOf(this LvnTitle t)
        {
            var list = new List<LvnChapter>();
            if (t?.seasons == null) return list;
            foreach (var s in t.seasons)
                if (s?.chapters != null)
                    foreach (var c in s.chapters)
                        if (c != null)
                            list.Add(c);
            // УСТОЙЧИВО: List.Sort устойчивость НЕ обещает и на списках
            // длиннее полутора десятков её не даёт. У глав с одинаковым
            // номером (битый импорт) порядок гулял бы от запуска к запуску — и
            // «продолжить» приводило бы то в одну главу, то в другую.
            return new List<LvnChapter>(
                System.Linq.Enumerable.OrderBy(list, c => c.number));
        }

        /// <summary>ПЕРВАЯ ГЛАВА — с наименьшим НОМЕРОМ, а не первая
        /// перечисленная. Совпадают они лишь пока автор пишет главы по
        /// порядку; запиши вторую выше первой — и «начать сначала» поведёт не
        /// туда. Номер — замысел, порядок в файле — случайность формата.</summary>
        public static LvnChapter FirstChapter(this LvnTitle t)
        {
            var all = t.ChaptersOf();
            return all.Count > 0 ? all[0] : null;
        }

        /// <summary>ПОСЛЕДНЯЯ ГЛАВА — с наибольшим номером. По ней узнают, что
        /// новелла пройдена до конца.</summary>
        public static LvnChapter LastChapter(this LvnTitle t)
        {
            var all = t.ChaptersOf();
            return all.Count > 0 ? all[all.Count - 1] : null;
        }

        /// <summary>СЛЕДУЮЩАЯ ЗА ЭТОЙ — с наименьшим номером, строго большим
        /// текущего. Null значит «эта была последней»: звонящий возвращает
        /// игрока в меню, а не ищет главу дальше.</summary>
        public static LvnChapter ChapterAfter(this LvnTitle t, LvnChapter current)
        {
            if (current == null) return null;
            foreach (var c in t.ChaptersOf())
                if (c.number > current.number) return c;
            return null;
        }

        /// <summary>Глава по id — ровно она, без догадок.</summary>
        public static LvnChapter ChapterById(this LvnTitle t, string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var c in t.ChaptersOf())
                if (c.id == id) return c;
            return null;
        }

        /// <summary>«Номер не записан». Нулём этот признак быть не может: ноль
        /// — ЗАКОННЫЙ номер главы, с него начинается вводная новелла и вся
        /// воронка приложения. Пока признаком был ноль, у игрока, стоящего в
        /// нулевой главе, переимпорт отбирал выручалочку: id больше нет,
        /// номер «не записан», точка продолжения теряется. Потолок при этом
        /// уцелевал, так что карточка честно звала «Продолжить», а вход уводил
        /// в перезапуск — воронка игралась сначала.</summary>
        public const int NoNumber = int.MinValue;

        public static LvnChapter ChapterByIdOrNumber(this LvnTitle t, string id, int number)
            => t.ChapterById(id) ?? (number != NoNumber ? t.ChapterByNumber(number) : null);

        /// <summary>Глава по номеру — резервный ключ прогресса.</summary>
        public static LvnChapter ChapterByNumber(this LvnTitle t, int number)
        {
            foreach (var c in t.ChaptersOf())
                if (c.number == number) return c;
            return null;
        }
    }

    /// <summary>Одна новелла: главы, сгруппированные в сезоны.</summary>
    public sealed class LvnTitle
    {
        public string id;
        /// <summary>Display name shown on the carousel card (falls back to id).</summary>
        public string name;
        /// <summary>Short tagline under the name on the carousel card.</summary>
        public string subtitle;
        /// <summary>Cover art for the menu carousel.</summary>
        public string cover_url;
        /// <summary>Optional URL of the title's variable declarations —
        /// <c>{"game":{key:value…},"chapter":{key:value…}}</c>. ONE declaration
        /// for the whole game instead of a per-chapter boilerplate: "game" keys
        /// are defaults that persist across chapters (applied only when unset),
        /// "chapter" keys are chapter-local — reset to their default on every
        /// fresh chapter entry (a mid-chapter resume keeps the snapshot's own).</summary>
        public string vars_url;
        public List<LvnSeason> seasons;
        /// <summary>Optional per-title UI theme override — layered over the global
        /// manifest.ui when this title's chapters play, so each game can have its
        /// own dialogue/choice look (e.g. a fantasy frame for an RPG).</summary>
        public LvnUiConfig ui;
        /// <summary>Optional CG gallery: the curated list of unlockable art. An
        /// item unlocks forever the first time a <c>bg</c> with its url is shown;
        /// the quick menu grows a Gallery entry when this list is non-empty.</summary>
        public List<LvnGalleryItem> gallery;

        // ── the hub/collection browse model (ui.browse.layout = "hub") ──
        /// <summary>Who owns this title — an author id the product services
        /// resolve payouts and per-author statistics through. The client never
        /// sends it: the SERVER reads it from the manifest and stamps it onto
        /// every wallet entry and analytics event as they are written, because
        /// "which title did this money move in, and whose was it" cannot be
        /// reconstructed afterwards. Empty means unattributed, never guessed.</summary>
        public string author;

        /// <summary>Вид содержимого — авторская пометка, зеркалящая подборку
        /// (<c>expedition</c>/<c>date</c>/<c>reality</c>). Для движка чисто
        /// справочная: доступ и завершённость ведут <see cref="unlock"/> и флаги
        /// <c>global.*</c> из скрипта.</summary>
        public string type;
        /// <summary>Detail-card presentation on the hub's title screen: big image +
        /// description + a Play button. Falls back to name/subtitle/cover_url.</summary>
        public LvnCardArt card;
        /// <summary>Access gate — an expression over the player's <c>global.*</c>
        /// stats (e.g. <c>"exp_1_done"</c> or <c>"rep >= 5 &amp;&amp; date_a_done"</c>).
        /// Empty/absent = always available. When false the card shows locked and a
        /// tap explains why (see <see cref="locked_hint"/>).</summary>
        public string unlock;
        /// <summary>Shown in a popup when a locked card is tapped ("Пройди
        /// Экспедицию 1"). Optional.</summary>
        public string locked_hint;
        /// <summary>Cost to START this title from the hub (typically 1 energy for an
        /// expedition). Null/zero = free. Charged on Play via the wallet; too little
        /// → a "buy?" popup routes to the store.</summary>
        public LvnCost cost;
        /// <summary>The main heroine / player character of this title — a sprite
        /// catalog entity id. Her wardrobe is the default one the skin shop opens,
        /// and her portrait can front the profile. Falls back to
        /// <see cref="LvnManifest.hero"/>.</summary>
        public string hero;
        /// <summary>Player-facing stats for the detail page and in-game stats
        /// panel — trait pairs plus one relationship meter per character,
        /// declared by the importer's template (<c>stats</c> in the import
        /// template JSON) and read live from the player's own vars. Empty/null
        /// → the stats section stays hidden (no fake placeholder data).</summary>
        public List<LvnStatDef> stats;
    }

    /// <summary>One entry of <see cref="LvnTitle.stats"/>: either a bipolar trait
    /// pair (two counters shown as one bar, filled by relative weight — no fixed
    /// max) or a single 0..max meter (used for per-character relationships).</summary>
    public sealed class LvnStatDef
    {
        /// <summary>"pair" or "single".</summary>
        public string kind;
        /// <summary>Single: the player-var key (dotted path, e.g. "Relationships.Roman").</summary>
        public string key;
        /// <summary>Single: the display label.</summary>
        public string label;
        /// <summary>Single: the display ceiling — cosmetic only.</summary>
        public int max;
        /// <summary>Single: this is a per-character relationship meter (vs. a
        /// generic single stat) — informational, doesn't change rendering.</summary>
        public bool relationship;
        /// <summary>Pair: the "positive" counter's key and label.</summary>
        public string pos_key;
        public string pos_label;
        /// <summary>Pair: the "negative"/opposite counter's key and label.</summary>
        public string neg_key;
        public string neg_label;
    }

    /// <summary>A named group of titles shown as one hub tile (an "expeditions",
    /// "dates" or "reality" collection). Titles are listed explicitly and in
    /// order; a title may appear in more than one collection.</summary>
    public sealed class LvnCollection
    {
        public string id;
        public string name;      // "Экспедиции"
        public string subtitle;  // optional line under the name
        public string type;      // author tag applied to the group
        public LvnCardArt card;  // the hub tile's art/description
        public List<string> titles; // ordered title ids in this collection
    }

    /// <summary>Card art + copy for a hub tile or a title's detail screen.</summary>
    public sealed class LvnCardArt
    {
        public string image;       // content url (big card image)
        public string description; // body text on the detail screen
    }

    /// <summary>A currency price (hub entry cost). Amount 0 = free.</summary>
    public sealed class LvnCost
    {
        public string currency; // e.g. "energy"
        public int amount;
    }

    /// <summary>One unlockable gallery CG.</summary>
    public sealed class LvnGalleryItem
    {
        /// <summary>Stable id the unlock is stored under — keep it constant across
        /// releases or players lose their unlocks.</summary>
        public string id;
        /// <summary>The CG's content url — must match the <c>bg</c> url that shows it.</summary>
        public string url;
        /// <summary>Optional caption shown in the gallery.</summary>
        public string name;
    }

    /// <summary>A season — an ordered group of chapters within a title.</summary>
    public sealed class LvnSeason
    {
        public List<LvnChapter> chapters;
    }

    /// <summary>One playable chapter and its release set.</summary>
    public sealed class LvnChapter
    {
        public string id;
        /// <summary>Sequence number within the title. The auto-continue / look-ahead
        /// logic orders by this (not array position), so out-of-order or pilot
        /// (number 0) entries don't break the chain.</summary>
        public int number;
        /// <summary>Episode display name ("Эпизод 3. …") — shown by the chapter
        /// picker and the Continue label. Optional; importers emit it.</summary>
        public string name;
        /// <summary>URL of the chapter's <c>.lvn</c> script.</summary>
        public string script_url;
        /// <summary>Loading-screen background, painted the instant the chapter opens.</summary>
        public string bg_url;
        /// <summary>The chapter's prioritized release set: content path →
        /// <see cref="LvnAssetMeta"/> (critical gates Play; the rest streams in
        /// during play). Fed to the <see cref="AssetScheduler"/>.</summary>
        public Dictionary<string, LvnAssetMeta> assets;
    }
}
