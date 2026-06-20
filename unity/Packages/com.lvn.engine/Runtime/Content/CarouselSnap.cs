using System;

namespace Lvn.Content
{
    /// <summary>
    /// Pure paged-carousel math — maps between a horizontal scroll offset and a
    /// card index for a fixed card stride, with one defined answer for the edge
    /// cases (empty list, out-of-range). Used by the title slider so snap, clamp
    /// and the offset↔index mapping can't drift apart. No UnityEngine.
    /// </summary>
    public readonly struct CarouselSnap
    {
        /// <summary>Pixel distance between adjacent card anchors (card width + gap).</summary>
        public readonly float Stride;
        /// <summary>Number of cards (pages). Indices are valid in [0, Count-1].</summary>
        public readonly int Count;

        public CarouselSnap(float stride, int count)
        {
            Stride = stride > 0f ? stride : 1f;
            Count = count < 0 ? 0 : count;
        }

        /// <summary>Scroll offset (px) that puts <paramref name="index"/> at rest,
        /// after clamping the index into range.</summary>
        public float OffsetFor(int index) => Clamp(index) * Stride;

        /// <summary>Nearest card index for a scroll offset, clamped into range.</summary>
        public int IndexAt(float offset)
        {
            int raw = (int)Math.Round(offset / Stride, MidpointRounding.AwayFromZero);
            return Clamp(raw);
        }

        /// <summary>Clamp an arbitrary index into [0, Count-1] (or 0 when empty).</summary>
        public int Clamp(int index)
        {
            if (Count <= 0) return 0;
            if (index < 0) return 0;
            int max = Count - 1;
            return index > max ? max : index;
        }

        /// <summary>True if <paramref name="index"/> is a valid card position.</summary>
        public bool IsValid(int index) => Count > 0 && index >= 0 && index < Count;
    }

    /// <summary>
    /// Single rounding rule for every "NN%" the UI shows (loading bar, HUD
    /// progress) so they all round and clamp identically. No UnityEngine.
    /// </summary>
    public static class Percent
    {
        /// <summary>Integer percent (0..100) of <paramref name="current"/> over
        /// <paramref name="total"/>, round-half-up, clamped. 0 when total &lt;= 0.</summary>
        public static int Value(int current, int total)
        {
            if (total <= 0) return 0;
            float f = (float)current / total;
            if (f < 0f) f = 0f;
            if (f > 1f) f = 1f;
            return (int)Math.Round(f * 100f, MidpointRounding.AwayFromZero);
        }

        /// <summary>The percent as a display string, e.g. <c>"42%"</c>.</summary>
        public static string Text(int current, int total) => Value(current, total) + "%";
    }
}
