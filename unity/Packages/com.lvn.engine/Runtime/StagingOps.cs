using System.Collections.Generic;

namespace Lvn
{
    /// <summary>
    /// The command ops the runtime understands. Mirrors the Go validator's
    /// registry — an op outside this set is a content error. The host may
    /// register additional project ops it interprets in <see cref="ILvnStage.ApplyStage"/>.
    /// </summary>
    public static class StagingOps
    {
        public static readonly HashSet<string> Known = new HashSet<string>
        {
            "say", "choice", "bg", "actor", "obj",
            "fade", "dim", "flash", "tint", "blur",
            "camera", "particles",
            "audio", "wait", "preload", "text_pace",
            "label", "goto", "if",
            "set", "inc", "hint",
            "call", "return",
        };
    }
}
