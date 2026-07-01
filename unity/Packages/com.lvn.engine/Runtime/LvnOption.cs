namespace Lvn
{
    /// <summary>
    /// A presentable choice option: its caption, the script index to pass back
    /// to <see cref="LvnPlayer.Choose"/>, and the optional narrative cost line
    /// shown beneath it.
    ///
    /// Gating comes in two flavours (see <see cref="LvnPlayer"/>):
    ///   • <b>hidden</b> — an <c>expr</c> filter that evaluated false is dropped
    ///     entirely and never reaches the host (pure logic branching);
    ///   • <b>locked</b> — a failed <c>requires_stat</c> skill check or an
    ///     unaffordable <c>cost</c> is still handed over, with
    ///     <see cref="Enabled"/> false and an optional <see cref="Note"/>, so the
    ///     host can show it greyed-out (the player sees what they can't do yet).
    /// </summary>
    public readonly struct LvnOption
    {
        public readonly int Index;
        public readonly string Text;
        public readonly string Cost;

        /// <summary>False when a skill check / cost gate is unmet: show it, but
        /// greyed and non-interactive.</summary>
        public readonly bool Enabled;

        /// <summary>Optional reason a locked option is unavailable (e.g. a skill
        /// requirement), rendered beneath the caption when set.</summary>
        public readonly string Note;

        public LvnOption(int index, string text, string cost, bool enabled = true, string note = null)
        {
            Index = index;
            Text = text;
            Cost = cost;
            Enabled = enabled;
            Note = note;
        }
    }
}
