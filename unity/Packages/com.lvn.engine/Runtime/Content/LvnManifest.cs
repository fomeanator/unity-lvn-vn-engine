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

        /// <summary>Manifest-driven theme for the built-in novel screens (loading,
        /// title card, name input). Optional — components use defaults when null.</summary>
        public LvnUiConfig ui;

        /// <summary>The sprite/entity catalog, keyed by id. Scripts reference these
        /// ids (e.g. <c>actor id="mara" pose="sitting"</c>) instead of raw urls; the
        /// client resolves an id to its ordered layer urls and composites them. A
        /// simple sprite is just a one-layer entity; a character is a multi-layer
        /// entity parameterised by axes. Optional.</summary>
        public Dictionary<string, LvnSpriteEntity> sprites;
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
        /// <summary>Allowed values per axis (axis → values) — drives the authoring
        /// dropdowns and validation; optional (free-form when absent).</summary>
        public Dictionary<string, List<string>> axes;
        /// <summary>Renderer kind: <c>static</c> (default) | <c>rigged</c> (named
        /// transform animations) | <c>spine</c> | <c>live2d</c> (future).</summary>
        public string kind;
        /// <summary>Named animations (name → tracks). A <c>rigged</c> entity plays
        /// these via <c>actor play="name"</c>; <c>auto:true</c> animations loop on
        /// show. See <see cref="LvnAnim"/>.</summary>
        public Dictionary<string, LvnAnim> anim;
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
        /// <summary><c>[[time, value], …]</c>. Value is a number for transforms,
        /// or an axis value string for <c>frame</c> tracks.</summary>
        public List<object[]> keys;
    }

    /// <summary>One title (a series of chapters grouped into seasons).</summary>
    public sealed class LvnTitle
    {
        public string id;
        /// <summary>Display name shown on the carousel card (falls back to id).</summary>
        public string name;
        /// <summary>Short tagline under the name on the carousel card.</summary>
        public string subtitle;
        /// <summary>Cover art for the menu carousel.</summary>
        public string cover_url;
        public List<LvnSeason> seasons;
        /// <summary>Optional per-title UI theme override — layered over the global
        /// manifest.ui when this title's chapters play, so each game can have its
        /// own dialogue/choice look (e.g. a fantasy frame for an RPG).</summary>
        public LvnUiConfig ui;
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
