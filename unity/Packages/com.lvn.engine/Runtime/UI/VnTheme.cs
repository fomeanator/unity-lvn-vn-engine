using System;
using UnityEngine;

namespace Lvn.UI
{
    /// <summary>
    /// The look-and-feel props for the reference component set: colours, font,
    /// sizes and reveal timing. One theme is shared by the dialogue box, choice
    /// list and stage, so a game restyles everything by editing these fields in
    /// the Inspector — no USS file required. This is the "constructor" knob set;
    /// for a bespoke skin, ignore the components and style your own from the
    /// same <see cref="LvnPlayer"/>.
    /// </summary>
    [Serializable]
    public class VnTheme
    {
        [Header("Dialogue")]
        public Color PanelColor = new Color(0.05f, 0.05f, 0.08f, 0.80f);
        public Color TextColor = new Color(0.96f, 0.96f, 0.96f, 1f);
        public Color SpeakerColor = new Color(1f, 0.82f, 0.40f, 1f);
        public Font Font;
        public int BodyFontSize = 34;
        public int SpeakerFontSize = 24;
        public float PanelCornerRadius = 12f;

        [Header("Reveal")]
        [Tooltip("Typewriter speed in characters per second.")]
        public float CharsPerSecond = 45f;
        [Tooltip("Soft per-glyph fade-in width, in trailing characters.")]
        public float FadeWidth = 5f;

        [Header("Choices")]
        public Color ChoiceColor = new Color(0.12f, 0.12f, 0.16f, 0.92f);
        public Color ChoiceHoverColor = new Color(0.20f, 0.20f, 0.26f, 0.96f);
        public Color ChoiceTextColor = new Color(0.96f, 0.96f, 0.96f, 1f);
        public Color ChoiceCostColor = new Color(0.90f, 0.64f, 0.23f, 1f);
        public int ChoiceFontSize = 28;
    }
}
