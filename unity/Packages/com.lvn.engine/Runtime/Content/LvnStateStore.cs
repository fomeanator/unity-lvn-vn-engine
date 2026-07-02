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

        public HttpStateStore(string baseUrl, string userId)
        {
            _base = (baseUrl ?? "").TrimEnd('/');
            _user = string.IsNullOrEmpty(userId) ? "anon" : userId;
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

        private async Task<JObject> TryGet(string titleId, CancellationToken ct)
        {
            try
            {
                using var req = UnityWebRequest.Get(Url(titleId));
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
                if (req.responseCode < 200 || req.responseCode >= 300) return null; // 404 = no save yet
                return JObject.Parse(req.downloadHandler.text);
            }
            catch { return null; }
        }

        private async Task Put(string titleId, JObject doc, CancellationToken ct)
        {
            using var req = new UnityWebRequest(Url(titleId), "PUT");
            var body = System.Text.Encoding.UTF8.GetBytes(doc.ToString(Newtonsoft.Json.Formatting.None));
            req.uploadHandler = new UploadHandlerRaw(body);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = TimeoutSeconds;
            var op = req.SendWebRequest();
            while (!op.isDone)
            {
                if (ct.IsCancellationRequested) { req.Abort(); return; }
                await Task.Yield();
            }
            if (req.result is UnityWebRequest.Result.ConnectionError
                           or UnityWebRequest.Result.DataProcessingError)
                LvnNetworkStatus.MarkOffline("state PUT network error");
            else
                LvnNetworkStatus.MarkOnline("state PUT ok"); // the sync reached the server → we're online
        }
    }
}
