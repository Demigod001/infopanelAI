using System;
using System.Collections.Generic;
using System.Linq;

namespace InfoPanel.Utils;

/// <summary>Axis-aligned bounds of a panel item, in panel pixels.</summary>
public readonly record struct ItemRect(float Left, float Top, float Right, float Bottom)
{
    public float Width => Right - Left;
    public float Height => Bottom - Top;
    public float CenterX => (Left + Right) / 2f;
    public float CenterY => (Top + Bottom) / 2f;

    public bool IntersectsWith(ItemRect other) =>
        Left <= other.Right && other.Left <= Right && Top <= other.Bottom && other.Top <= Bottom;

    public static ItemRect FromPoints(float x1, float y1, float x2, float y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
}

public enum AlignMode
{
    Left,
    CenterHorizontally,
    Right,
    Top,
    CenterVertically,
    Bottom
}

/// <summary>
/// Layout calculations for the panel editor. Each method returns how far to move each item
/// (dx, dy), in the same order as the input, so callers apply them to X/Y. Pure maths with no
/// app dependencies, so it can be unit tested.
/// </summary>
public static class LayoutMath
{
    /// <summary>
    /// Align the items to the outer edge (or centre) of the whole selection, as in most design tools:
    /// "Left" moves every item so its left edge matches the leftmost one.
    /// </summary>
    public static (int Dx, int Dy)[] Align(IReadOnlyList<ItemRect> rects, AlignMode mode)
    {
        if (rects.Count < 2) return rects.Select(_ => (0, 0)).ToArray();

        var left = rects.Min(r => r.Left);
        var right = rects.Max(r => r.Right);
        var top = rects.Min(r => r.Top);
        var bottom = rects.Max(r => r.Bottom);
        var centerX = (left + right) / 2f;
        var centerY = (top + bottom) / 2f;

        return rects.Select(r => mode switch
        {
            AlignMode.Left => (Round(left - r.Left), 0),
            AlignMode.Right => (Round(right - r.Right), 0),
            AlignMode.CenterHorizontally => (Round(centerX - r.CenterX), 0),
            AlignMode.Top => (0, Round(top - r.Top)),
            AlignMode.Bottom => (0, Round(bottom - r.Bottom)),
            AlignMode.CenterVertically => (0, Round(centerY - r.CenterY)),
            _ => (0, 0)
        }).ToArray();
    }

    /// <summary>
    /// Space items evenly: the first and last (by position) stay put and the gaps between
    /// neighbouring items are made equal. Needs at least three items.
    /// </summary>
    public static (int Dx, int Dy)[] Distribute(IReadOnlyList<ItemRect> rects, bool horizontal)
    {
        var moves = new (int Dx, int Dy)[rects.Count];
        if (rects.Count < 3) return moves;

        var order = Enumerable.Range(0, rects.Count)
            .OrderBy(i => horizontal ? rects[i].Left : rects[i].Top)
            .ToList();

        float Start(ItemRect r) => horizontal ? r.Left : r.Top;
        float Size(ItemRect r) => horizontal ? r.Width : r.Height;

        var first = rects[order[0]];
        var last = rects[order[^1]];
        var span = (Start(last) + Size(last)) - Start(first);
        var totalSize = order.Sum(i => Size(rects[i]));
        var gap = (span - totalSize) / (rects.Count - 1);

        var position = Start(first);
        foreach (var i in order)
        {
            var delta = Round(position - Start(rects[i]));
            moves[i] = horizontal ? (delta, 0) : (0, delta);
            position += Size(rects[i]) + gap;
        }

        // Keep the outermost items exactly where they were, despite rounding.
        moves[order[0]] = (0, 0);
        moves[order[^1]] = (0, 0);
        return moves;
    }

    /// <summary>
    /// Snap a drag of the whole selection to the grid using one shared offset, so the spacing between
    /// items is preserved. <paramref name="anchorX"/>/<paramref name="anchorY"/> is where the anchor
    /// item would land without snapping; returns the offset to apply to every item's start position.
    /// </summary>
    public static (int Dx, int Dy) SnappedDragOffset(int anchorStartX, int anchorStartY, double anchorX, double anchorY, int grid)
    {
        grid = Math.Max(1, grid);
        var snappedX = (int)(Math.Round(anchorX / grid) * grid);
        var snappedY = (int)(Math.Round(anchorY / grid) * grid);
        return (snappedX - anchorStartX, snappedY - anchorStartY);
    }

    private static int Round(float value) => (int)MathF.Round(value, MidpointRounding.AwayFromZero);
}
