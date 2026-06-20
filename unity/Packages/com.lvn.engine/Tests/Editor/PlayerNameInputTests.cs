using Lvn.Content;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class PlayerNameInputTests
    {
        [Test]
        public void Sanitize_TrimsAndCollapsesWhitespace()
        {
            Assert.AreEqual("hello", PlayerNameInput.Sanitize("  hello  "));
            Assert.AreEqual("a b", PlayerNameInput.Sanitize("a   b"));
            Assert.AreEqual("a b c", PlayerNameInput.Sanitize("a\tb\nc"));
        }

        [Test]
        public void Sanitize_EmptyAndNull()
        {
            Assert.AreEqual("", PlayerNameInput.Sanitize(null));
            Assert.AreEqual("", PlayerNameInput.Sanitize(""));
            Assert.AreEqual("", PlayerNameInput.Sanitize("   "));
        }

        [Test]
        public void Sanitize_CapsAtMaxLength()
        {
            var raw = new string('x', 50);
            Assert.AreEqual(PlayerNameInput.MaxLength, PlayerNameInput.Sanitize(raw).Length);
            Assert.AreEqual("ab", PlayerNameInput.Sanitize("abcdef", 2));
        }

        [Test]
        public void CanCommit_NonEmptyOnly()
        {
            Assert.IsTrue(PlayerNameInput.CanCommit("Raven"));
            Assert.IsFalse(PlayerNameInput.CanCommit("   "));
            Assert.IsFalse(PlayerNameInput.CanCommit(null));
        }
    }
}
