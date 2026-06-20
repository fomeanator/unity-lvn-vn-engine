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
