using System;

namespace Lvn
{
    /// <summary>
    /// Thrown when a .lvn script is malformed at runtime — a bad expression,
    /// a division by zero, a value used as the wrong type. These are content
    /// bugs the validator and authoring tools aim to catch before shipping.
    /// </summary>
    public sealed class LvnException : Exception
    {
        public LvnException(string message) : base(message) { }
    }
}
