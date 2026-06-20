using System;
using UnityEngine;

namespace Lvn.UI.MetaShell
{
    /// <summary>
    /// A life/card system for gating gameplay. Tracks remaining lives,
    /// regeneration time, and paywall status.
    /// </summary>
    public class LifeCardSystem
    {
        public int MaxLives { get; private set; }
        public int CurrentLives { get; private set; }
        public float RegenSeconds { get; private set; }
        public DateTime LastRegenTime { get; private set; }

        public bool IsFull => CurrentLives >= MaxLives;
        public bool IsEmpty => CurrentLives <= 0;

        public LifeCardSystem(int maxLives = 5, float regenSeconds = 300f)
        {
            MaxLives = maxLives;
            RegenSeconds = regenSeconds;
            CurrentLives = maxLives;
            LastRegenTime = DateTime.UtcNow;
        }

        public LifeCardSystem(int maxLives, float regenSeconds, int currentLives, DateTime lastRegen)
        {
            MaxLives = maxLives;
            RegenSeconds = regenSeconds;
            CurrentLives = currentLives;
            LastRegenTime = lastRegen;
        }

        public bool TryConsume()
        {
            Regenerate();
            if (CurrentLives <= 0) return false;
            CurrentLives--;
            return true;
        }

        public void AddLife()
        {
            if (CurrentLives < MaxLives)
                CurrentLives++;
        }

        public void SetFull()
        {
            CurrentLives = MaxLives;
        }

        public float TimeUntilNextLife()
        {
            if (IsFull) return 0f;
            var elapsed = (DateTime.UtcNow - LastRegenTime).TotalSeconds;
            var nextIn = RegenSeconds - elapsed;
            return nextIn > 0 ? (float)nextIn : 0f;
        }

        private void Regenerate()
        {
            if (IsFull) return;
            var elapsed = (DateTime.UtcNow - LastRegenTime).TotalSeconds;
            int regained = (int)(elapsed / RegenSeconds);
            if (regained > 0)
            {
                CurrentLives = Mathf.Min(CurrentLives + regained, MaxLives);
                LastRegenTime = LastRegenTime.AddSeconds(regained * RegenSeconds);
            }
        }
    }
}
