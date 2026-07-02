using Lvn.Content;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Lvn.UI.Screens
{
    /// <summary>
    /// Per-title reading progress, shared by the play loop (auto-continue) and
    /// the carousel (the Continue label + the chapter picker):
    /// <list type="bullet">
    ///   <item><b>Current</b> — the chapter the player is in / left off in.
    ///   Moves freely (forward on chapter transitions, anywhere on a save load).</item>
    ///   <item><b>Reached</b> — the furthest chapter number ever STARTED. Only
    ///   ever goes up, so replaying an early chapter never re-locks later ones
    ///   in the picker.</item>
    /// </list>
    /// PlayerPrefs-backed, like the save slots.
    /// </summary>
    public static class LvnProgress
    {
        private static string CurKey(string titleId) => "lvn_chapter_" + (titleId ?? "");
        private static string ReachedKey(string titleId) => "lvn_reached_" + (titleId ?? "");

        /// <summary>Record that the player is (now) in this chapter. Bumps
        /// Reached when this is the furthest chapter so far.</summary>
        public static void SetCurrent(LvnTitle title, LvnChapter chapter)
        {
            if (title == null || chapter == null) return;
            PlayerPrefs.SetString(CurKey(title.id), chapter.id);
            if (chapter.number > Reached(title))
                PlayerPrefs.SetInt(ReachedKey(title.id), chapter.number);
            PlayerPrefs.Save();
        }

        /// <summary>The chapter to continue from, or null to start fresh.</summary>
        public static LvnChapter Current(LvnTitle title)
        {
            if (title?.seasons == null) return null;
            var id = PlayerPrefs.GetString(CurKey(title.id), "");
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var s in title.seasons)
                if (s?.chapters != null)
                    foreach (var c in s.chapters)
                        if (c != null && c.id == id)
                            return c;
            return null; // the chapter vanished from the manifest — start over
        }

        /// <summary>The furthest chapter number ever started (0 = nothing yet).</summary>
        public static int Reached(LvnTitle title) =>
            title == null ? 0 : PlayerPrefs.GetInt(ReachedKey(title.id), 0);

        /// <summary>Forget the continue point (the novel was finished — replays
        /// start clean). Reached is kept so the picker stays unlocked.</summary>
        public static void ClearCurrent(LvnTitle title)
        {
            if (title == null) return;
            PlayerPrefs.DeleteKey(CurKey(title.id));
        }

        // ── chapter-entry checkpoints ────────────────────────────────────────
        // The genre-standard restart semantics: "start from chapter N" resets the
        // variables to what they were when chapter N was FIRST entered on this
        // playthrough — not to whatever the player has accumulated since (stats
        // from the future would leak into the past and mis-gate choices). The
        // play loop snapshots the seed vars on every fresh chapter entry; the
        // picker requests a restart, and the loop seeds from the checkpoint.

        private static string EntryKey(string titleId) => "lvn_entry_" + (titleId ?? "");
        private static string RestartKey(string titleId) => "lvn_restart_" + (titleId ?? "");

        /// <summary>Snapshot the variables as they were entering a chapter.</summary>
        public static void SaveCheckpoint(string titleId, string chapterId, JObject vars)
        {
            if (string.IsNullOrEmpty(chapterId)) return;
            try
            {
                var all = ReadCheckpoints(titleId);
                all[chapterId] = vars ?? new JObject();
                PlayerPrefs.SetString(EntryKey(titleId), all.ToString(Newtonsoft.Json.Formatting.None));
                PlayerPrefs.Save();
            }
            catch { /* checkpoints are a comfort feature — never fatal */ }
        }

        /// <summary>The variables as of the chapter's first entry, or null when
        /// it was never entered (→ seed empty on a picked restart).</summary>
        public static JObject Checkpoint(string titleId, string chapterId)
        {
            if (string.IsNullOrEmpty(chapterId)) return null;
            return ReadCheckpoints(titleId)[chapterId] as JObject;
        }

        private static JObject ReadCheckpoints(string titleId)
        {
            try
            {
                var s = PlayerPrefs.GetString(EntryKey(titleId), "");
                return string.IsNullOrEmpty(s) ? new JObject() : JObject.Parse(s);
            }
            catch { return new JObject(); }
        }

        /// <summary>The picker calls this: "the next entry into this chapter is an
        /// explicit RESTART — seed from its checkpoint, not the live state".</summary>
        public static void RequestRestart(string titleId, string chapterId)
        {
            PlayerPrefs.SetString(RestartKey(titleId), chapterId ?? "");
            PlayerPrefs.Save();
        }

        /// <summary>Consume a pending restart request for this chapter (one-shot).</summary>
        public static bool TakeRestart(string titleId, string chapterId)
        {
            var pending = PlayerPrefs.GetString(RestartKey(titleId), "");
            if (string.IsNullOrEmpty(pending) || pending != chapterId) return false;
            PlayerPrefs.DeleteKey(RestartKey(titleId));
            return true;
        }
    }
}
