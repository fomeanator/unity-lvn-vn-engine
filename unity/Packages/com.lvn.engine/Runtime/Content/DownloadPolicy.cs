using System;

namespace Lvn.Content
{
    /// <summary>
    /// The visual/asset class of a content URL, inferred from its path. The
    /// single place that decides "what KIND of thing is this URL" so every
    /// download phase agrees (boot prefetch, menu refresh, chapter entry, in-game
    /// look-ahead) instead of each re-deriving it from path substrings inline.
    /// </summary>
    public enum AssetClass
    {
        Ui,         // shared interface art (dialogue frame, badges, icons)
        ChapterBg,  // a chapter's loading-screen background
        Cover,      // a title cover (menu carousel)
        Script,     // a .lvn chapter script
        Actor,      // character art
        Audio,      // music / sfx
        SceneBg,    // in-chapter scene background
        Other,
    }

    /// <summary>
    /// Pure, deterministic download policy — classifies content URLs and answers
    /// the cross-cutting questions the download phases ask:
    /// <list type="bullet">
    ///   <item>what class is this URL?</item>
    ///   <item>should it be decoded into the in-memory sprite cache (warm), or is
    ///   living on disk enough?</item>
    ///   <item>is it wanted during boot prefetch?</item>
    /// </list>
    /// No UnityEngine, no I/O — every rule here is unit-testable, so "what loads
    /// when" is a calculable contract rather than scattered path checks. The
    /// caller supplies the actual URLs and performs the side effects; this only
    /// judges. Path conventions (<c>/ui/</c>, <c>/loading/</c>, <c>/cover</c>,
    /// <c>/actors/</c>, <c>/bg/</c>) are sensible defaults — a host with a
    /// different layout can classify by server-supplied <see cref="LvnAssetMeta"/>
    /// instead.
    /// </summary>
    public static class DownloadPolicy
    {
        /// <summary>Strip a query string for extension/segment matching.</summary>
        private static string Bare(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            int q = url.IndexOf('?');
            return q >= 0 ? url.Substring(0, q) : url;
        }

        public static bool IsImage(string url)
        {
            var u = Bare(url);
            return u.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || u.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || u.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                || u.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsAudio(string url)
        {
            var u = Bare(url);
            return u.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                || u.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
                || u.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsScript(string url) =>
            Bare(url).EndsWith(".lvn", StringComparison.OrdinalIgnoreCase);

        /// <summary>The prefetch kind string ("sprite" | "audio" | "bin").</summary>
        public static string Kind(string url) =>
            IsImage(url) ? "sprite" : IsAudio(url) ? "audio" : "bin";

        /// <summary>Classify by path segment. Order matters: script and audio win
        /// over the image buckets; among images, the path folder decides.</summary>
        public static AssetClass Classify(string url)
        {
            if (string.IsNullOrEmpty(url)) return AssetClass.Other;
            var u = Bare(url).ToLowerInvariant();
            if (IsScript(u)) return AssetClass.Script;
            if (IsAudio(u))  return AssetClass.Audio;
            // Loading backgrounds live under /loading/ — check BEFORE /ui/.
            if (u.Contains("/loading/")) return AssetClass.ChapterBg;
            if (u.Contains("/ui/"))      return AssetClass.Ui;
            if (u.Contains("/cover"))    return AssetClass.Cover;
            if (u.Contains("/actors/") || u.Contains("/actor/")) return AssetClass.Actor;
            if (u.Contains("/bg/"))      return AssetClass.SceneBg;
            return AssetClass.Other;
        }

        /// <summary>Should this URL be decoded into the in-memory sprite cache up
        /// front (so a view can paint it on the first frame), or is on-disk
        /// enough? Warm the art the player sees immediately: shared UI, chapter
        /// loading backgrounds, and covers (the carousel is the first screen).
        /// Scene backgrounds, actors and audio stay disk-only — chapter-scoped,
        /// loaded when their command needs them.</summary>
        public static bool WarmToMemory(AssetClass cls) =>
            cls == AssetClass.Ui || cls == AssetClass.ChapterBg || cls == AssetClass.Cover;

        public static bool WarmToMemory(string url) => WarmToMemory(Classify(url));

        /// <summary>Is this URL part of the boot prefetch set — the art the player
        /// sees immediately at/after launch (UI chrome, menu covers, chapter
        /// loading backgrounds)? Scene backgrounds, actors and audio are
        /// chapter-scoped and fetched by the chapter scheduler, not at boot.</summary>
        public static bool NeededAtBoot(AssetClass cls) =>
            cls == AssetClass.Ui || cls == AssetClass.Cover || cls == AssetClass.ChapterBg;

        public static bool NeededAtBoot(string url) => NeededAtBoot(Classify(url));
    }
}
