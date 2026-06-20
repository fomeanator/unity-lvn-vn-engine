namespace Lvn.Content
{
    /// <summary>
    /// Per-asset metadata the server computes for a chapter's release set and
    /// the scheduler uses to order downloads. Engine-agnostic: a backend emits
    /// it (e.g. from a content catalog), the client deserializes it on the
    /// chapter object. Every field is optional — a null/zero value just makes
    /// the scheduler fall back to extension/size heuristics.
    /// </summary>
    public sealed class LvnAssetMeta
    {
        /// <summary>Content hash (sha256). "" if not on the server's disk yet.</summary>
        public string sha;

        /// <summary>Size in bytes. 0 if unknown / not uploaded.</summary>
        public long size;

        /// <summary>Server classification: <c>sprite | audio | video | bin</c>.</summary>
        public string kind;

        /// <summary>Concurrency tier: <c>mini | normal | large</c>.</summary>
        public string tier;

        /// <summary>Lifecycle scope: <c>boot | main-menu | novel-data | chapter</c>.</summary>
        public string scope;

        /// <summary>Needed at/near chapter start → goes in the <i>required</i> set
        /// and gates the Play button.</summary>
        public bool critical;

        /// <summary>Estimated ms from chapter start until the asset is first used —
        /// the deferred ordering key (earliest-used first).</summary>
        public long eta_ms;

        /// <summary>Human-readable loading-screen label ("" = none).</summary>
        public string alias;
    }
}
