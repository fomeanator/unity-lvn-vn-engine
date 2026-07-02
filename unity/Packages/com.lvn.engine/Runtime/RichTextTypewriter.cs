using System.Collections.Generic;
using System.Text;

namespace Lvn
{
    /// <summary>
    /// Reveals a rich-text string one visible glyph at a time without ever
    /// leaking a half-typed tag. UI Toolkit's Label has no maxVisibleCharacters,
    /// so we precompute, for each visible-character count k, a well-formed
    /// substring: markup up to the k-th glyph is emitted verbatim and any tags
    /// still open are closed so the Label always parses.
    ///
    /// Supported markup: &lt;b&gt;, &lt;i&gt;, &lt;color=...&gt; and closers.
    /// Unknown/self-closing tags pass through untouched. Pure C# — no UnityEngine.
    /// </summary>
    public sealed class RichTextTypewriter
    {
        private struct Step
        {
            public string Chunk;   // markup runs + the single visible char
            public string Close;   // closers for tags open after this glyph
        }

        private readonly List<Step> _steps = new List<Step>();
        private string _trailing = ""; // markup after the last glyph (closers)

        public int VisibleCount => _steps.Count;

        public void SetText(string full)
        {
            _steps.Clear();
            _trailing = "";
            if (string.IsNullOrEmpty(full)) return;

            var open = new List<string>();
            var pending = new StringBuilder();
            int i = 0;
            while (i < full.Length)
            {
                char c = full[i];
                if (c == '<')
                {
                    int close = full.IndexOf('>', i);
                    if (close < 0) { pending.Append(full.Substring(i)); break; }
                    var tag = full.Substring(i, close - i + 1);
                    TrackTag(tag, open);
                    pending.Append(tag);
                    i = close + 1;
                    continue;
                }

                pending.Append(c);
                _steps.Add(new Step { Chunk = pending.ToString(), Close = ClosersFor(open) });
                pending.Clear();
                i++;
            }
            _trailing = pending.ToString();
        }

        /// <summary>Well-formed markup for the first <paramref name="k"/> glyphs.</summary>
        public string Slice(int k)
        {
            if (k >= _steps.Count) return Full();
            if (k <= 0) return "";
            var sb = new StringBuilder();
            for (int n = 0; n < k; n++) sb.Append(_steps[n].Chunk);
            sb.Append(_steps[k - 1].Close);
            return sb.ToString();
        }

        public string Full()
        {
            var sb = new StringBuilder();
            for (int n = 0; n < _steps.Count; n++) sb.Append(_steps[n].Chunk);
            sb.Append(_trailing);
            return sb.ToString();
        }

        /// <summary>
        /// Like <see cref="Slice"/>, but with a soft trailing fade: glyphs within
        /// <paramref name="fadeWidth"/> of the reveal head get a per-glyph
        /// &lt;alpha&gt; ramp (newest = faintest), so the line blooms in instead
        /// of popping. <paramref name="progress"/> is measured in glyphs.
        /// </summary>
        public string SliceFaded(float progress, float fadeWidth)
        {
            if (_steps.Count == 0 || progress <= 0f) return "";
            if (fadeWidth < 0.01f) return Slice((int)progress);

            int shown = (int)System.Math.Ceiling(progress);
            if (shown > _steps.Count) shown = _steps.Count;

            var sb = new StringBuilder();
            int lastIdx = -1;
            for (int i = 0; i < shown; i++)
            {
                float a = (progress - i) / fadeWidth;
                if (a <= 0f) break;
                if (a > 1f) a = 1f;
                lastIdx = i;

                var chunk = _steps[i].Chunk;
                if (a >= 0.999f)
                {
                    sb.Append(chunk);
                }
                else
                {
                    int b = (int)(a * 255f + 0.5f);
                    sb.Append(chunk, 0, chunk.Length - 1);
                    sb.Append("<alpha=#").Append(b.ToString("X2")).Append('>');
                    sb.Append(chunk[chunk.Length - 1]);
                }
            }
            if (lastIdx >= 0) sb.Append(_steps[lastIdx].Close);
            return sb.ToString();
        }

        /// <summary>
        /// Like <see cref="SliceFaded"/>, but emits the WHOLE line every time: the
        /// revealed head with its fade ramp, then the rest of the text hidden under
        /// <c>&lt;alpha=#00&gt;</c>. The label therefore lays out the final text
        /// from glyph 0 — word-wrap and box height never shift mid-reveal (the
        /// classic typewriter reflow). Only opacity animates.
        /// </summary>
        public string SliceFadedFixed(float progress, float fadeWidth)
        {
            if (_steps.Count == 0) return Full();
            if (progress < 0f) progress = 0f;
            if (fadeWidth < 0.01f) fadeWidth = 0.01f;

            int shown = (int)System.Math.Ceiling(progress);
            if (shown > _steps.Count) shown = _steps.Count;

            var sb = new StringBuilder();
            int i = 0;
            for (; i < shown; i++)
            {
                float a = (progress - i) / fadeWidth;
                if (a <= 0f) break;
                if (a > 1f) a = 1f;

                var chunk = _steps[i].Chunk;
                if (a >= 0.999f)
                {
                    sb.Append(chunk);
                }
                else
                {
                    int b = (int)(a * 255f + 0.5f);
                    sb.Append(chunk, 0, chunk.Length - 1);
                    sb.Append("<alpha=#").Append(b.ToString("X2")).Append('>');
                    sb.Append(chunk[chunk.Length - 1]);
                }
            }
            // The unrevealed remainder: present for layout, invisible to the eye.
            // A <color> tag inside it would reset the alpha, so re-hide after any
            // chunk that carries markup.
            sb.Append("<alpha=#00>");
            for (; i < _steps.Count; i++)
            {
                var chunk = _steps[i].Chunk;
                sb.Append(chunk);
                if (chunk.Length > 1 && chunk.IndexOf('<') >= 0) sb.Append("<alpha=#00>");
            }
            sb.Append(_trailing);
            return sb.ToString();
        }

        private static void TrackTag(string tag, List<string> open)
        {
            if (tag.Length < 3) return;
            bool closing = tag[1] == '/';
            int start = closing ? 2 : 1;
            int end = start;
            while (end < tag.Length && char.IsLetter(tag[end])) end++;
            var name = tag.Substring(start, end - start).ToLowerInvariant();
            if (name != "b" && name != "i" && name != "color") return;

            if (closing)
            {
                for (int n = open.Count - 1; n >= 0; n--)
                    if (open[n] == name) { open.RemoveAt(n); break; }
            }
            else
            {
                open.Add(name);
            }
        }

        private static string ClosersFor(List<string> open)
        {
            if (open.Count == 0) return "";
            var sb = new StringBuilder();
            for (int n = open.Count - 1; n >= 0; n--) sb.Append("</").Append(open[n]).Append('>');
            return sb.ToString();
        }
    }
}
