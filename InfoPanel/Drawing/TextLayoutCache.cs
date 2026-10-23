using SkiaSharp;
using System;
using System.Collections.Generic;
using Topten.RichTextKit;

namespace InfoPanel.Drawing
{
    /// <summary>
    /// Caches laid-out RichTextKit TextBlocks between frames.
    ///
    /// Building a TextBlock runs full text shaping and line layout. Every display surface redraws
    /// every item each frame (15-30 fps), while most text (labels, units, and sensor values that
    /// update about once a second) is identical from one frame to the next, so nearly all of that
    /// work was repeated for nothing.
    ///
    /// Two-generation cache: lookups check the current generation, then the previous one (promoting
    /// hits). When the current generation fills up, it becomes the previous one and the old previous
    /// generation is dropped. Text still in use survives; text that stopped appearing (old sensor
    /// values) ages out after two rotations, so memory stays bounded without per-entry bookkeeping.
    /// </summary>
    internal static class TextLayoutCache
    {
        private const int GenerationCapacity = 2048;

        private static readonly object _lock = new();
        private static Dictionary<Key, TextBlock> _current = new(GenerationCapacity);
        private static Dictionary<Key, TextBlock> _previous = new(GenerationCapacity);

        internal readonly record struct Key(
            string Text,
            SKTypeface Typeface,
            float FontSize,
            SKColor Color,
            bool Underline,
            bool Strikeout,
            bool Ellipsis,
            bool Wrap,
            int Width,
            TextAlignment Alignment);

        /// <summary>
        /// Returns a laid-out TextBlock for the key, building it with <paramref name="factory"/> on a miss.
        /// The returned block may be shared between render threads; callers must paint it inside
        /// <c>lock (block)</c>.
        /// </summary>
        public static TextBlock GetOrCreate(in Key key, Func<Key, TextBlock> factory)
        {
            lock (_lock)
            {
                if (_current.TryGetValue(key, out var block))
                {
                    return block;
                }

                if (_previous.Remove(key, out block))
                {
                    AddToCurrent(key, block);
                    return block;
                }
            }

            // Build outside the lock: layout is the expensive part and must not block other threads.
            var created = factory(key);

            // Force layout now so later reads (MeasuredWidth, Paint) don't lay out lazily on a shared instance.
            _ = created.MeasuredHeight;

            lock (_lock)
            {
                // Another thread may have built the same block meanwhile; keep the first one.
                if (_current.TryGetValue(key, out var existing))
                {
                    return existing;
                }

                AddToCurrent(key, created);
                return created;
            }
        }

        private static void AddToCurrent(in Key key, TextBlock block)
        {
            if (_current.Count >= GenerationCapacity)
            {
                _previous = _current;
                _current = new Dictionary<Key, TextBlock>(GenerationCapacity);
            }

            _current[key] = block;
        }
    }
}
