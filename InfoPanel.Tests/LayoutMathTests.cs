using InfoPanel.Utils;

namespace InfoPanel.Tests;

public class LayoutMathTests
{
    private static ItemRect R(float x, float y, float w, float h) => new(x, y, x + w, y + h);

    [Fact]
    public void Align_left_moves_every_item_to_the_leftmost_edge()
    {
        var rects = new[] { R(10, 0, 50, 20), R(30, 40, 20, 20), R(25, 80, 100, 20) };
        var moves = LayoutMath.Align(rects, AlignMode.Left);
        Assert.Equal(new[] { (0, 0), (-20, 0), (-15, 0) }, moves);
    }

    [Fact]
    public void Align_right_matches_the_rightmost_edge()
    {
        var rects = new[] { R(10, 0, 50, 20), R(30, 40, 20, 20) }; // rights: 60, 50
        Assert.Equal(new[] { (0, 0), (10, 0) }, LayoutMath.Align(rects, AlignMode.Right));
    }

    [Fact]
    public void Align_centre_uses_the_centre_of_the_whole_selection()
    {
        var rects = new[] { R(0, 0, 100, 10), R(0, 20, 20, 10) }; // selection 0..100, centre 50
        Assert.Equal(new[] { (0, 0), (40, 0) }, LayoutMath.Align(rects, AlignMode.CenterHorizontally));
    }

    [Fact]
    public void Align_top_and_bottom_move_only_vertically()
    {
        var rects = new[] { R(0, 5, 10, 10), R(50, 30, 10, 40) };
        Assert.Equal(new[] { (0, 0), (0, -25) }, LayoutMath.Align(rects, AlignMode.Top));
        Assert.Equal(new[] { (0, 55), (0, 0) }, LayoutMath.Align(rects, AlignMode.Bottom));
    }

    [Fact]
    public void Align_does_nothing_for_a_single_item()
    {
        Assert.Equal(new[] { (0, 0) }, LayoutMath.Align(new[] { R(5, 5, 10, 10) }, AlignMode.Left));
    }

    [Fact]
    public void Distribute_makes_gaps_equal_and_keeps_the_ends_fixed()
    {
        // Widths 10, 20, 10. Span 0..100 => gap (100 - 40) / 2 = 30. Middle item should start at 40.
        var rects = new[] { R(0, 0, 10, 10), R(60, 0, 20, 10), R(90, 0, 10, 10) };
        var moves = LayoutMath.Distribute(rects, horizontal: true);
        Assert.Equal(new[] { (0, 0), (-20, 0), (0, 0) }, moves);
    }

    [Fact]
    public void Distribute_works_regardless_of_selection_order()
    {
        var rects = new[] { R(0, 90, 10, 10), R(0, 0, 10, 10), R(0, 20, 10, 10) }; // last, first, middle
        var moves = LayoutMath.Distribute(rects, horizontal: false);
        // Span 0..100, heights 30, gap 35: middle should start at 45 (currently 20).
        Assert.Equal(new[] { (0, 0), (0, 0), (0, 25) }, moves);
    }

    [Fact]
    public void Distribute_needs_three_items()
    {
        Assert.Equal(new[] { (0, 0), (0, 0) }, LayoutMath.Distribute(new[] { R(0, 0, 5, 5), R(50, 0, 5, 5) }, true));
    }

    [Fact]
    public void Snapped_drag_offset_keeps_relative_positions()
    {
        // Anchor starts at (13, 7) and is dragged to (41.6, 22.2) on a 10 px grid: lands on (40, 20).
        var (dx, dy) = LayoutMath.SnappedDragOffset(13, 7, 41.6, 22.2, 10);
        Assert.Equal((27, 13), (dx, dy));

        // A second item at (101, 55) moves by the same offset, keeping its 88 px gap from the anchor.
        Assert.Equal((128, 68), (101 + dx, 55 + dy));
    }

    [Fact]
    public void Intersects_includes_touching_edges()
    {
        Assert.True(R(0, 0, 10, 10).IntersectsWith(R(10, 10, 5, 5)));
        Assert.False(R(0, 0, 10, 10).IntersectsWith(R(11, 0, 5, 5)));
    }
}
