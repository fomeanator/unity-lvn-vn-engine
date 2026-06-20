using System;

namespace Lvn.Content
{
    /// <summary>
    /// Process-wide connectivity flag, shared by every content fetch. The boot
    /// probe sets it; a live wire failure during a download self-corrects it to
    /// offline so the next fetch fast-fails into the disk cache instead of
    /// waiting out a socket timeout. Recovery (flipping back online) is owned by
    /// the host's health-check loop. A single volatile bool — written from
    /// background download threads, read from the main thread.
    /// </summary>
    public static class LvnNetworkStatus
    {
        private static volatile bool _online = true;

        // Test/debug kill-switch: when set the app behaves as if the network is
        // permanently dead — IsOnline is forced false and MarkOnline is ignored,
        // so offline paths are fully deterministic without touching a socket.
        private static volatile bool _forceOffline;

        public static bool ForceOffline
        {
            get => _forceOffline;
            set { _forceOffline = value; if (value) Set(false); }
        }

        public static bool IsOnline => !_forceOffline && _online;
        public static bool IsOffline => !IsOnline;

        /// <summary>Raised on every real transition (not on idempotent re-marks).
        /// Argument is the new IsOnline value. May fire from a background thread —
        /// marshal to the main thread before touching Unity objects.</summary>
        public static event Action<bool> Changed;

        public static void MarkOffline(string reason = null) => Set(false);
        public static void MarkOnline(string reason = null) => Set(true);

        private static void Set(bool online)
        {
            if (online && _forceOffline) return; // forced offline wins
            if (_online == online) return;        // idempotent — no spurious events
            _online = online;
            try { Changed?.Invoke(online); } catch { /* a bad subscriber must not break status */ }
        }
    }

    /// <summary>
    /// A content-fetch failure carrying the HTTP status and a short machine code
    /// (<c>"network"</c> for connectivity misses, <c>"http_NNN"</c> for bad
    /// responses). Retry loops branch on these: a <c>4xx</c> is permanent (give
    /// up), a <c>"network"</c> while offline is pointless to retry.
    /// </summary>
    public sealed class LvnFetchException : Exception
    {
        public int Status { get; }
        public string Code { get; }

        public LvnFetchException(int status, string code, string message)
            : base($"{code} ({status}): {message}")
        {
            Status = status;
            Code = code;
        }
    }

    /// <summary>
    /// Exponential backoff for retrying a failed fetch. Attempt 1 has no delay;
    /// every subsequent attempt doubles (capped) so a flaky link recovers
    /// quickly without hammering a dead one.
    /// </summary>
    public static class LvnBackoff
    {
        public const float DefaultCapSeconds = 30f;

        public static float DelaySeconds(int attempt, float capSeconds = DefaultCapSeconds)
        {
            if (attempt <= 1) return 0f;
            var exp = Math.Min(attempt - 2, 30);           // guard against overflow
            var delay = (float)Math.Pow(2d, exp);
            return Math.Min(capSeconds, delay);
        }
    }
}
