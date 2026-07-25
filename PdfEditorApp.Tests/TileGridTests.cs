using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

public class TileGridTests
{
    [Fact]
    public void level_matches_the_render_core_pyramid()
    {
        // Must agree exactly with tile_level_for_width in render_core, or the
        // app asks for tiles from a different grid than the one it draws.
        Assert.Equal(0, TileGrid.LevelForWidth(256));
        Assert.Equal(1, TileGrid.LevelForWidth(512));
        Assert.Equal(2, TileGrid.LevelForWidth(1024));
        Assert.Equal(5, TileGrid.LevelForWidth(8192));
    }

    [Fact]
    public void level_rounds_up_so_a_tile_is_never_softer_than_asked()
    {
        Assert.Equal(3, TileGrid.LevelForWidth(1025));
        Assert.Equal(1, TileGrid.LevelForWidth(257));
    }

    [Fact]
    public void a_tile_shrinks_in_dips_as_the_level_rises()
    {
        // The page keeps its slot size; higher levels just divide it more
        // finely, which is what keeps a tile a fixed 256px of detail.
        Assert.Equal(800, TileGrid.TileDip(800, 0), 6);
        Assert.Equal(400, TileGrid.TileDip(800, 1), 6);
        Assert.Equal(25, TileGrid.TileDip(800, 5), 6);
    }

    [Fact]
    public void row_count_follows_the_page_aspect()
    {
        // Tiles are square in PIXELS, so a tall page has more rows than
        // columns. A4 is about 1.41.
        Assert.Equal(4, TileGrid.ColumnsAt(2));
        Assert.Equal(6, TileGrid.RowsAt(2, 800, 1131));
    }

    [Fact]
    public void only_the_visible_tiles_are_returned()
    {
        // Level 4 over an 800 DIP page: 16 columns of 50 DIP each. The band
        // 100..200 therefore covers columns 2 and 3 only, so 2x2 tiles.
        var tiles = TileGrid.VisibleTiles(800, 1131, 4, 100, 100, 200, 200, ring: 0);

        Assert.All(tiles, t => Assert.InRange(t.Address.Col, 2, 3));
        Assert.All(tiles, t => Assert.InRange(t.Address.Row, 2, 3));
        Assert.Equal(4, tiles.Count);
    }

    [Fact]
    public void the_prefetch_ring_widens_the_set()
    {
        var bare = TileGrid.VisibleTiles(800, 1131, 4, 200, 200, 250, 250, ring: 0);
        var ringed = TileGrid.VisibleTiles(800, 1131, 4, 200, 200, 250, 250, ring: 1);

        Assert.True(ringed.Count > bare.Count, "the ring must add margin tiles");
        Assert.Contains(bare, b => ringed.Any(r => r.Address == b.Address));
    }

    [Fact]
    public void tiles_never_run_off_the_page()
    {
        // Asking for a region past the edge, with a ring, must still stay
        // inside the grid: render_core rejects out-of-range tiles.
        var tiles = TileGrid.VisibleTiles(800, 1131, 3, -500, -500, 5000, 5000, ring: 2);

        int cols = TileGrid.ColumnsAt(3);
        int rows = TileGrid.RowsAt(3, 800, 1131);
        Assert.All(tiles, t => Assert.InRange(t.Address.Col, 0, cols - 1));
        Assert.All(tiles, t => Assert.InRange(t.Address.Row, 0, rows - 1));
    }

    [Fact]
    public void placement_tiles_the_page_without_gaps_or_overlap()
    {
        var tiles = TileGrid.VisibleTiles(800, 1131, 2, 0, 0, 800, 1131, ring: 0);
        double dip = TileGrid.TileDip(800, 2);

        foreach (var t in tiles)
        {
            Assert.Equal(t.Address.Col * dip, t.Left, 6);
            Assert.Equal(t.Address.Row * dip, t.Top, 6);
            Assert.Equal(dip, t.Size, 6);
        }

        // Every tile is distinct, so nothing is drawn twice.
        Assert.Equal(tiles.Count, tiles.Select(t => t.Address).Distinct().Count());
    }

    [Fact]
    public void a_degenerate_slot_returns_nothing_rather_than_throwing()
    {
        Assert.Empty(TileGrid.VisibleTiles(0, 0, 2, 0, 0, 100, 100));
        Assert.Empty(TileGrid.VisibleTiles(800, 1131, -1, 0, 0, 100, 100));
    }

    [Fact]
    public void a_viewport_at_deep_zoom_needs_a_bounded_number_of_tiles()
    {
        // The claim the whole design rests on: tile count tracks the VIEWPORT,
        // not the zoom. A 1080p viewport should need a similar handful of
        // tiles whether it is showing 2x or 64x.
        foreach (int level in new[] { 3, 6, 9 })
        {
            double dip = TileGrid.TileDip(800, level);
            // A viewport showing 1920x1080 device px at this level.
            var tiles = TileGrid.VisibleTiles(
                800, 1131, level, 0, 0, dip * 8, dip * 5, ring: 1);

            Assert.InRange(tiles.Count, 1, 130);
        }
    }
}
