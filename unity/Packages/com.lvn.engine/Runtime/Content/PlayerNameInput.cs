using System.Text.RegularExpressions;

namespace Lvn.Content
{
    /// <summary>
    /// Pure rules for the name-input screen — sanitising the raw text-field value
    /// into a player name and deciding whether it may be committed. Keeping the
    /// rules (trim, collapse internal whitespace, hard cap, empty rejection) here
    /// makes them unit-testable instead of an inline regex nobody re-checks. No
    /// UnityEngine.
    /// </summary>
    public static class PlayerNameInput
    {
        /// <summary>Default max stored name length. The screen mirrors this on the
        /// field's maxLength so the sanitised value can never exceed it.</summary>
        public const int MaxLength = 24;

        private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

        /// <summary>Trim, collapse internal whitespace to single spaces, and cap
        /// length to <paramref name="maxLength"/>. Null/blank → "".</summary>
        public static string Sanitize(string raw, int maxLength = MaxLength)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var s = Whitespace.Replace(raw.Trim(), " ");
            if (maxLength > 0 && s.Length > maxLength) s = s.Substring(0, maxLength);
            return s;
        }

        /// <summary>True if the raw value yields a non-empty name (i.e. the
        /// confirm action should commit).</summary>
        public static bool CanCommit(string raw, int maxLength = MaxLength) =>
            Sanitize(raw, maxLength).Length > 0;
    }
}
