using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace Lvn
{
    /// <summary>
    /// The persistence seam for save slots — a tiny key→string store the
    /// <see cref="LvnSaveStore"/> writes through. PlayerPrefs in a build, an
    /// in-memory map in tests. Keeps the store's slot/versioning logic pure and
    /// unit-testable off any real disk.
    /// </summary>
    public interface ILvnKeyStore
    {
        string Get(string key);
        void Set(string key, string value);
        void Delete(string key);
    }

    /// <summary>Default backend: Unity PlayerPrefs (persists across sessions).</summary>
    public sealed class PlayerPrefsKeyStore : ILvnKeyStore
    {
        public string Get(string key) => PlayerPrefs.GetString(key, null);
        public void Set(string key, string value) { PlayerPrefs.SetString(key, value); PlayerPrefs.Save(); }
        public void Delete(string key) { PlayerPrefs.DeleteKey(key); PlayerPrefs.Save(); }
    }

    /// <summary>In-memory backend for tests (and headless hosts).</summary>
    public sealed class MemoryKeyStore : ILvnKeyStore
    {
        private readonly Dictionary<string, string> _m = new Dictionary<string, string>();
        public string Get(string key) => _m.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) => _m[key] = value;
        public void Delete(string key) => _m.Remove(key);
    }

    /// <summary>A listable save slot's metadata (no player state) — enough to draw
    /// a load menu row.</summary>
    public sealed class SaveSlotInfo
    {
        public string Slot;
        public string ScriptUrl;
        public long SavedAtUnix;
        public int Index;
        public bool Finished;
    }

    /// <summary>
    /// Persists <see cref="LvnPlayer.LvnSnapshot"/>s by named slot with three
    /// robustness guarantees the raw PlayerPrefs write never had:
    ///   • <b>versioning</b> — every write stamps the schema version and every read
    ///     runs <see cref="LvnPlayer.LvnSnapshot.Migrate"/>, so an old (or
    ///     future-and-untrusted) save loads safely instead of silently corrupting;
    ///   • <b>a slot index</b> — <see cref="List"/>/<see cref="Slots"/>/<see cref="Delete"/>
    ///     so a UI can enumerate and manage saves (the old scheme had no registry);
    ///   • <b>corruption-safe reads</b> — a mangled slot reads back as absent, never
    ///     an exception.
    /// The per-slot key scheme (<c>lvn_save_&lt;slot&gt;</c>) matches the old inline
    /// writer, so pre-existing saves still load (they just aren't in the index until
    /// rewritten).
    /// </summary>
    public sealed class LvnSaveStore
    {
        private const string SlotPrefix = "lvn_save_";
        private const string IndexKey = "lvn_save_index";
        private readonly ILvnKeyStore _kv;

        public LvnSaveStore(ILvnKeyStore kv = null) { _kv = kv ?? new MemoryKeyStore(); }

        /// <summary>Empty slot name resolves to the default quick slot.</summary>
        public static string Norm(string slot) => string.IsNullOrEmpty(slot) ? "quick" : slot;

        public void Write(string slot, LvnPlayer.LvnSnapshot snap, string scriptUrl, long savedAtUnix)
        {
            if (snap == null) return;
            slot = Norm(slot);
            snap.Version = LvnPlayer.LvnSnapshot.CurrentVersion;
            if (!string.IsNullOrEmpty(scriptUrl)) snap.ScriptUrl = scriptUrl;
            snap.SavedAtUnix = savedAtUnix;
            _kv.Set(SlotPrefix + slot, JsonConvert.SerializeObject(snap));
            AddToIndex(slot);
        }

        /// <summary>Read + migrate a slot. Null if absent, corrupt, or from a newer
        /// build than this one can trust.</summary>
        public LvnPlayer.LvnSnapshot Read(string slot)
        {
            var json = _kv.Get(SlotPrefix + Norm(slot));
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var snap = JsonConvert.DeserializeObject<LvnPlayer.LvnSnapshot>(json);
                return LvnPlayer.LvnSnapshot.Migrate(snap);
            }
            catch { return null; } // corrupt slot → treat as absent, never throw
        }

        public bool Has(string slot) => !string.IsNullOrEmpty(_kv.Get(SlotPrefix + Norm(slot)));

        public void Delete(string slot)
        {
            slot = Norm(slot);
            _kv.Delete(SlotPrefix + slot);
            RemoveFromIndex(slot);
        }

        /// <summary>Slot names in the index (write order).</summary>
        public IReadOnlyList<string> Slots()
        {
            var raw = _kv.Get(IndexKey);
            if (string.IsNullOrEmpty(raw)) return new List<string>();
            try { return JsonConvert.DeserializeObject<List<string>>(raw) ?? new List<string>(); }
            catch { return new List<string>(); }
        }

        /// <summary>Listable metadata for every readable slot (corrupt ones skipped).</summary>
        public IReadOnlyList<SaveSlotInfo> List()
        {
            var infos = new List<SaveSlotInfo>();
            foreach (var slot in Slots())
            {
                var snap = Read(slot);
                if (snap == null) continue;
                infos.Add(new SaveSlotInfo
                {
                    Slot = slot,
                    ScriptUrl = snap.ScriptUrl,
                    SavedAtUnix = snap.SavedAtUnix,
                    Index = snap.Index,
                    Finished = snap.Finished,
                });
            }
            return infos;
        }

        private void AddToIndex(string slot)
        {
            var list = new List<string>(Slots());
            if (list.Contains(slot)) return;
            list.Add(slot);
            _kv.Set(IndexKey, JsonConvert.SerializeObject(list));
        }

        private void RemoveFromIndex(string slot)
        {
            var list = new List<string>(Slots());
            if (list.Remove(slot)) _kv.Set(IndexKey, JsonConvert.SerializeObject(list));
        }
    }
}
