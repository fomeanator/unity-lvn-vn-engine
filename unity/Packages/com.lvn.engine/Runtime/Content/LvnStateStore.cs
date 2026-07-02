using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace Lvn.Content
{
    /// <summary>
    /// The player-state seam: how the app persists a title's script variables
    /// (relationships, route, memory flags — the "stats"). Like <c>ILvnAssets</c>,
    /// the engine ships a local-only default and lets a host plug in server sync.
    ///
    /// Contract: a title's stats are a flat JSON object (name → value). Load returns
    /// the stats to seed the next chapter with; Save records the ending stats.
    /// Implementations MUST be offline-safe (never throw on a dead network).
    /// </summary>
    public interface ILvnStateStore
    {
        Task<JObject> LoadVarsAsync(string titleId, CancellationToken ct);
        Task SaveVarsAsync(string titleId, JObject vars, CancellationToken ct);
    }

    /// <summary>
    /// Local-first, offline store: stats live in PlayerPrefs under
    /// <c>lvn_state_&lt;title&gt;</c> as <c>{"vars":{…},"updatedAt":"ISO"}</c>. No
    /// network — the app plays and keeps stats with no server at all. Also the disk
    /// layer <see cref="HttpStateStore"/> writes through, so it's the single source
    /// of the reconcile timestamp.
    /// </summary>
    public sealed class LocalStateStore : ILvnStateStore
    {
        internal static string Key(string titleId) => "lvn_state_" + (titleId ?? "");

        public Task<JObject> LoadVarsAsync(string titleId, CancellationToken ct)
        {
            var doc = ReadDoc(titleId);
            return Task.FromResult(Vars(doc));
        }

        public Task SaveVarsAsync(string titleId, JObject vars, CancellationToken ct)
        {
            WriteDoc(titleId, MakeDoc(vars));
            return Task.CompletedTask;
        }

        internal static JObject Vars(JObject doc) => doc?["vars"] as JObject ?? new JObject();

        internal static JObject MakeDoc(JObject vars) => new JObject
        {
            ["vars"] = vars ?? new JObject(),
            ["updatedAt"] = DateTime.UtcNow.ToString("o"),
        };

        internal static JObject ReadDoc(string titleId)
        {
            try
            {
                var s = PlayerPrefs.GetString(Key(titleId), "");
                return string.IsNullOrEmpty(s) ? null : JObject.Parse(s);
            }
            catch { return null; }
        }

        internal static void WriteDoc(string titleId, JObject doc)
        {
            try
            {
                PlayerPrefs.SetString(Key(titleId), doc.ToString(Newtonsoft.Json.Formatting.None));
                PlayerPrefs.Save();
            }
            catch (Exception e) { Debug.LogWarning("[lvn-state] local write failed: " + e.Message); }
        }

        // ── sync base ────────────────────────────────────────────────────────
        // The vars as of the LAST successful server sync. The field-level merge
        // needs it: "which keys did THIS device change since we agreed with the
        // server" — those keys win over the server's copy in a conflict; keys we
        // didn't touch take the other device's values.

        internal static string BaseKey(string titleId) => "lvn_state_base_" + (titleId ?? "");

        internal static JObject ReadBase(string titleId)
        {
            try
            {
                var s = PlayerPrefs.GetString(BaseKey(titleId), "");
                return string.IsNullOrEmpty(s) ? null : JObject.Parse(s);
            }
            catch { return null; }
        }

        internal static void WriteBase(string titleId, JObject vars)
        {
            try
            {
                PlayerPrefs.SetString(BaseKey(titleId), (vars ?? new JObject()).ToString(Newtonsoft.Json.Formatting.None));
                PlayerPrefs.Save();
            }
            catch { /* base is an optimisation — merge degrades to overlay-all */ }
        }
    }

    /// <summary>
    /// Local-first store that syncs to the LVN server's <c>/v1/state</c> — the
    /// offline-first model proven in the Liminal client:
    /// <list type="bullet">
    ///   <item>Save writes the local cache FIRST (instant, survives a dead network),
    ///   then PUTs to the server when online. A failed PUT just stays local; the next
    ///   online save/load reconciles it.</item>
    ///   <item>Load, when online, GETs the server copy and reconciles with local by
    ///   <c>updatedAt</c> (newer wins) so an app killed after a local write but before
    ///   the PUT doesn't roll the player back; offline, it returns the local copy.</item>
    /// </list>
    /// Each (user, title) is its own server blob (<c>?user=&lt;uid&gt;__&lt;title&gt;</c>),
    /// so a PUT never has to merge other titles. Never throws on network errors.
    /// </summary>
    public sealed class HttpStateStore : ILvnStateStore
    {
        private readonly string _base;
        private readonly string _user;
        private const int TimeoutSeconds = 8;

        // Last server version seen per title (the OCC token): echoed on PUT so the
        // server can detect that another device wrote in between. A conflict comes
        // back as a 409 with the winning doc — we merge (newer updatedAt wins) and
        // retry once, instead of silently clobbering the other device's progress.
        private readonly System.Collections.Generic.Dictionary<string, long> _versions
            = new System.Collections.Generic.Dictionary<string, long>();

        private string VKey(string titleId) => titleId ?? "";

        // The per-blob secret (X-State-Key header). The user id travels in the
        // URL, which proxies and access logs record — the key is what actually
        // gates the blob (TOFU-claimed server-side on the first keyed PUT). An
        // account-style host passes a shared key; otherwise a per-device secret
        // is generated once.
        private readonly string _key;

        internal static string DeviceKey()
        {
            var k = PlayerPrefs.GetString("lvn_state_key", "");
            if (string.IsNullOrEmpty(k))
            {
                k = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
                PlayerPrefs.SetString("lvn_state_key", k);
                PlayerPrefs.Save();
            }
            return k;
        }

        /// <param name="stateKey">Shared secret for the user's blobs. REQUIRED to
        /// be the same across devices when <paramref name="userId"/> is an account
        /// id used on several devices; defaults to a per-device secret.</param>
        public HttpStateStore(string baseUrl, string userId, string stateKey = null)
        {
            _base = (baseUrl ?? "").TrimEnd('/');
            _user = string.IsNullOrEmpty(userId) ? "anon" : userId;
            _key = string.IsNullOrEmpty(stateKey) ? DeviceKey() : stateKey;
        }

        private string Url(string titleId) =>
            _base + "/v1/state?user=" + UnityWebRequest.EscapeURL(_user + "__" + (titleId ?? ""));

        public async Task<JObject> LoadVarsAsync(string titleId, CancellationToken ct)
        {
            var local = LocalStateStore.ReadDoc(titleId);
            if (!LvnNetworkStatus.IsOffline)
            {
                var server = await TryGet(titleId, ct);
                if (server != null)
                {
                    var pick = Newer(server, local);
                    LocalStateStore.WriteDoc(titleId, pick); // keep local aligned with the winner
                    if (ReferenceEquals(pick, server))
                        LocalStateStore.WriteBase(titleId, LocalStateStore.Vars(server)); // in sync now
                    return LocalStateStore.Vars(pick);
                }
            }
            return LocalStateStore.Vars(local);
        }

        public async Task SaveVarsAsync(string titleId, JObject vars, CancellationToken ct)
        {
            var doc = LocalStateStore.MakeDoc(vars);
            LocalStateStore.WriteDoc(titleId, doc); // local first — instant, offline-safe
            if (!LvnNetworkStatus.IsOffline)
                try { await Put(titleId, doc, ct); } catch { /* stays local; reconciles later */ }
        }

        /// <summary>Field-level conflict merge: start from the OTHER device's doc
        /// (the server's winner) and overlay only the keys THIS device changed
        /// since it last agreed with the server — so two devices touching
        /// different stats both keep their progress, instead of whole-blob
        /// newer-wins throwing one side away. With no baseline (fresh install),
        /// every local key overlays — the old behaviour.</summary>
        internal static JObject MergeVars(JObject serverVars, JObject localVars, JObject baseVars)
        {
            var merged = serverVars != null ? (JObject)serverVars.DeepClone() : new JObject();
            if (localVars == null) return merged;
            foreach (var p in localVars.Properties())
            {
                var baseVal = baseVars?[p.Name];
                if (baseVal != null && JToken.DeepEquals(baseVal, p.Value)) continue; // untouched here — theirs wins
                merged[p.Name] = p.Value.DeepClone();
            }
            return merged;
        }

        // Pick the doc with the later updatedAt; a null side loses to a real one.
        private static JObject Newer(JObject a, JObject b)
        {
            if (a == null) return b ?? new JObject();
            if (b == null) return a;
            return IsNewer((string)a["updatedAt"], (string)b["updatedAt"]) ? a : b;
        }

        private static bool IsNewer(string x, string y)
        {
            if (string.IsNullOrEmpty(x)) return false;
            if (string.IsNullOrEmpty(y)) return true;
            var ox = DateTime.TryParse(x, null, DateTimeStyles.RoundtripKind, out var dx);
            var oy = DateTime.TryParse(y, null, DateTimeStyles.RoundtripKind, out var dy);
            if (!ox) return false;
            if (!oy) return true;
            return dx > dy;
        }

        private bool _keyRejectedLogged;

        private void WarnKeyRejected(string what)
        {
            if (_keyRejectedLogged) return;
            _keyRejectedLogged = true;
            Debug.LogWarning("[lvn-state] " + what + ": the server blob is claimed by a different state key. " +
                             "Multi-device accounts must share one key (HttpStateStore stateKey / NovelApp.StateKey). " +
                             "Playing on local state.");
        }

        private async Task<JObject> TryGet(string titleId, CancellationToken ct)
        {
            try
            {
                using var req = UnityWebRequest.Get(Url(titleId));
                req.SetRequestHeader("X-State-Key", _key);
                req.timeout = TimeoutSeconds;
                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    if (ct.IsCancellationRequested) { req.Abort(); return null; }
                    await Task.Yield();
                }
                if (req.result is UnityWebRequest.Result.ConnectionError
                               or UnityWebRequest.Result.DataProcessingError)
                {
                    LvnNetworkStatus.MarkOffline("state GET network error");
                    return null;
                }
                // A real HTTP response (even a 404) proves the wire is back —
                // recover the global flag so other subsystems resume too.
                LvnNetworkStatus.MarkOnline("state GET ok");
                if (req.responseCode == 401) { WarnKeyRejected("load"); return null; }
                if (req.responseCode < 200 || req.responseCode >= 300) return null; // 404 = no save yet
                var doc = JObject.Parse(req.downloadHandler.text);
                if (doc["_version"] != null) // remember the OCC token for the next PUT
                    _versions[VKey(titleId)] = (long)doc["_version"];
                return doc;
            }
            catch { return null; }
        }

        private async Task Put(string titleId, JObject doc, CancellationToken ct)
        {
            // Up to one merge-retry: attempt 1 with our last-seen version; on a 409
            // (another device wrote) merge newer-wins and retry with the fresh token.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var send = (JObject)doc.DeepClone();
                if (_versions.TryGetValue(VKey(titleId), out var known))
                    send["_version"] = known;

                using var req = new UnityWebRequest(Url(titleId), "PUT");
                var body = System.Text.Encoding.UTF8.GetBytes(send.ToString(Newtonsoft.Json.Formatting.None));
                req.uploadHandler = new UploadHandlerRaw(body);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("X-State-Key", _key);
                req.timeout = TimeoutSeconds;
                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    if (ct.IsCancellationRequested) { req.Abort(); return; }
                    await Task.Yield();
                }
                if (req.result is UnityWebRequest.Result.ConnectionError
                               or UnityWebRequest.Result.DataProcessingError)
                {
                    LvnNetworkStatus.MarkOffline("state PUT network error");
                    return;
                }
                LvnNetworkStatus.MarkOnline("state PUT ok"); // the sync reached the server → we're online

                if (req.responseCode == 401) { WarnKeyRejected("save"); return; } // stays local

                if (req.responseCode == 409)
                {
                    try
                    {
                        var resp = JObject.Parse(req.downloadHandler.text);
                        _versions[VKey(titleId)] = (long?)resp["version"] ?? 0;
                        // Field-level merge: the other device's doc wins by default;
                        // only the keys WE changed since the last agreed sync overlay it.
                        var serverVars = LocalStateStore.Vars(resp["doc"] as JObject);
                        var merged = LocalStateStore.MakeDoc(MergeVars(
                            serverVars, LocalStateStore.Vars(doc), LocalStateStore.ReadBase(titleId)));
                        LocalStateStore.WriteDoc(titleId, merged); // local mirrors the merge outcome
                        doc = merged;
                        continue; // one retry with the fresh version
                    }
                    catch { return; } // unparseable conflict — leave it local, reconcile next load
                }
                if (req.responseCode >= 200 && req.responseCode < 300)
                {
                    try
                    {
                        var resp = JObject.Parse(req.downloadHandler.text);
                        if (resp["version"] != null) _versions[VKey(titleId)] = (long)resp["version"];
                    }
                    catch { /* legacy server without versions — LWW as before */ }
                    // The server accepted this doc — it IS the agreed state now.
                    LocalStateStore.WriteBase(titleId, LocalStateStore.Vars(doc));
                }
                return;
            }
        }
    }
}
