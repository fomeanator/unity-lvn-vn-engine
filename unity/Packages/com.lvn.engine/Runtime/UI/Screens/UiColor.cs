using UnityEngine;

namespace Lvn.UI.Screens
{
    /// <summary>Parses the manifest's <c>"#rrggbb"</c> / <c>"#rrggbbaa"</c> hex
    /// color strings into <see cref="Color"/>, with a fallback for null/garbage.
    /// Keeps the screen components free of inline color parsing.</summary>
    public static class UiColor
    {
        public static Color Parse(string hex, Color fallback)
        {
            if (string.IsNullOrEmpty(hex)) return fallback;
            var s = hex[0] == '#' ? hex.Substring(1) : hex;
            // Unity's util accepts 6/8-digit (and #-prefixed); normalise to that.
            if (ColorUtility.TryParseHtmlString("#" + s, out var c)) return c;
            return fallback;
        }
    }
}
