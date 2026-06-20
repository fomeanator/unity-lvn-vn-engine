using System;
using Lvn;
using Lvn.UI.MetaShell;
using NUnit.Framework;

namespace Lvn.Tests
{
    public class MetaShellTests
    {
        [Test]
        public void LifeCardSystemConsumesLife()
        {
            var life = new LifeCardSystem(5, 300f);
            Assert.AreEqual(5, life.CurrentLives);
            Assert.IsTrue(life.TryConsume());
            Assert.AreEqual(4, life.CurrentLives);
        }

        [Test]
        public void LifeCardSystemBlocksWhenEmpty()
        {
            var life = new LifeCardSystem(2, 300f);
            life.TryConsume();
            life.TryConsume();
            Assert.AreEqual(0, life.CurrentLives);
            Assert.IsFalse(life.TryConsume());
        }

        [Test]
        public void LifeCardSystemRegenerates()
        {
            var past = DateTime.UtcNow.AddSeconds(-600);
            var life = new LifeCardSystem(3, 300f, 1, past);
            Assert.AreEqual(1, life.CurrentLives);
            life.TryConsume();
            Assert.AreEqual(2, life.CurrentLives);
        }

        [Test]
        public void LifeCardSystemAddLifeRespectsMax()
        {
            var life = new LifeCardSystem(3, 300f);
            life.AddLife();
            Assert.AreEqual(3, life.CurrentLives);
        }

        [Test]
        public void LifeCardSystemSetFull()
        {
            var life = new LifeCardSystem(5, 300f);
            life.TryConsume();
            life.TryConsume();
            life.SetFull();
            Assert.AreEqual(5, life.CurrentLives);
            Assert.IsTrue(life.IsFull);
        }

        [Test]
        public void LifeCardSystemTimeUntilNextLife()
        {
            var life = new LifeCardSystem(5, 60f);
            life.TryConsume();
            float time = life.TimeUntilNextLife();
            Assert.Greater(time, 0f);
            Assert.LessOrEqual(time, 60f);
        }
    }
}
