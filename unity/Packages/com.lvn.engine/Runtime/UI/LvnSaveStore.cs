using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>One persisted save slot: the player snapshot plus the display
    /// metadata a save/load UI shows (when, where, the last line read).</summary>
    public sealed class LvnSaveSlot
    {
        /// <summary>Slot schema version, for forward migration.</summary>
        public int Version = 1;
        public LvnPlayer.LvnSnapshot Snap;
        public long SavedAtUnixMs;
        public string ChapterId;
        public string Preview; // the last dialogue line at save time
    }

    /// <summary>
    /// Disk-backed save slots, namespaced per title so two novels on one device
    /// never see each other's saves. PlayerPrefs-backed (like the stat store) —
    /// survives restarts on every platform without file-permission concerns.
    /// Slots are small (a cursor anchor + variables), so a title's whole slot
    /// map serializes as one JSON blob.
    /// </summary>
    public static class LvnSaveStore
    {
        /// <summary>The slot name the engine autosaves into.</summary>
        public const string AutoSlot = "auto";

        private static string Key(string titleId) =>
            "lvn_slots_" + (string.IsNullOrEmpty(titleId) ? "default" : titleId);

        /// <summary>All of a title's slots (name → slot). Never null.</summary>
        public static Dictionary<string, LvnSaveSlot> Slots(string titleId)
        {
            var json = PlayerPrefs.GetString(Key(titleId), "");
            if (string.IsNullOrEmpty(json)) return new Dictionary<string, LvnSaveSlot>();
            try
            {
                return JsonConvert.DeserializeObject<Dictionary<string, LvnSaveSlot>>(json)
                       ?? new Dictionary<string, LvnSaveSlot>();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[lvn] save slots unreadable (" + e.Message + ") — starting empty");
                return new Dictionary<string, LvnSaveSlot>();
            }
        }

        /// <summary>A single slot, or null when empty/unreadable.</summary>
        public static LvnSaveSlot Get(string titleId, string slot)
        {
            return Slots(titleId).TryGetValue(slot ?? "", out var s) ? s : null;
        }

        /// <summary>Write a slot (stamps <see cref="LvnSaveSlot.SavedAtUnixMs"/>).</summary>
        public static void Put(string titleId, string slot, LvnSaveSlot data)
        {
            if (string.IsNullOrEmpty(slot) || data == null) return;
            data.SavedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var all = Slots(titleId);
            all[slot] = data;
            Write(titleId, all);
        }

        public static void Delete(string titleId, string slot)
        {
            var all = Slots(titleId);
            if (!all.Remove(slot ?? "")) return;
            Write(titleId, all);
        }

        private static void Write(string titleId, Dictionary<string, LvnSaveSlot> all)
        {
            try
            {
                PlayerPrefs.SetString(Key(titleId), JsonConvert.SerializeObject(all));
                PlayerPrefs.Save();
            }
            catch (Exception e) { Debug.LogWarning("[lvn] save write failed: " + e.Message); }
        }
    }
}
