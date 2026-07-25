using System;
using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>Identifies one tile in a page's level-of-detail pyramid.</summary>
public readonly record struct TileAddress(int Level, int Col, int Row);

/// <summary>A tile plus where it belongs on the page card, in slot-space DIPs.</summary>
public readonly record struct TilePlacement(TileAddress Address, double Left, double Top, double Size);

/// <summary>
/// Works out which tiles a viewport needs, and where they go.
///
/// The pyramid mirrors render_core exactly: at <c>level</c> the page is
/// <c>TileSize * 2^level</c> pixels wide and <c>2^level</c> tiles across, with
/// the page aspect deciding how many rows exist so tiles stay square in pixel
/// space. Both sides must agree on that geometry or tiles land in the wrong
/// place, which is why the level rule lives here as one function rather than
/// being open-coded at each call site.
/// </summary>
public static class TileGrid
{
    /// <summary>
    /// Pixel edge of one tile. MUST equal TILE_SIZE in render_core.
    ///
    /// 512 rather than 256, because the tile count is what the smoothness
    /// costs: a bigger tile halves the level needed for a given resolution,
    /// which doubles a tile's size in slot DIPs and quarters how many of them
    /// cover the viewport. At 800% that is about 20 tiles instead of 80, so
    /// there are a quarter as many separate renders arriving one at a time and
    /// a quarter as many seams. It also keeps tiles further away from the
    /// sub-DIP layout sizes where anything to do with rounding starts to hurt.
    ///
    /// The cost is coarser reuse when panning, since a tile that scrolls off
    /// takes four times as much with it, and 1MB per tile instead of 256KB.
    /// Both are cheap next to a page that stutters.
    /// </summary>
    public const int TileSize = 512;

    /// <summary>Highest level allowed, matching render_core's guard.</summary>
    public const int MaxLevel = 20;

    /// <summary>
    /// The level whose rendered page width first meets or exceeds what the
    /// screen needs. Rounds UP: a tile must never be softer than asked for.
    /// </summary>
    public static int LevelForWidth(double neededPageWidthPx)
    {
        if (neededPageWidthPx <= TileSize)
        {
            return 0;
        }

        int level = 0;
        long width = TileSize;
        while (width < neededPageWidthPx && level < MaxLevel)
        {
            width *= 2;
            level++;
        }

        return level;
    }

    /// <summary>Edge length of a tile at this level, in slot-space DIPs.</summary>
    public static double TileDip(double slotWidth, int level) => slotWidth / (1 << level);

    /// <summary>Number of tile columns at this level.</summary>
    public static int ColumnsAt(int level) => 1 << level;

    /// <summary>
    /// Number of tile rows, which follows the page's aspect because tiles are
    /// square in pixels, not in page fractions.
    /// </summary>
    public static int RowsAt(int level, double slotWidth, double slotHeight)
    {
        if (slotWidth <= 0)
        {
            return 0;
        }

        return (int)Math.Ceiling((1 << level) * (slotHeight / slotWidth));
    }

    /// <summary>
    /// The tiles covering a visible rectangle, expressed in page-local slot
    /// DIPs, widened by <paramref name="ring"/> tiles.
    ///
    /// The ring is prefetch: without it, panning exposes an unrendered edge
    /// before the new tiles arrive, which reads as tearing. One ring of margin
    /// is enough to stay ahead of an ordinary drag.
    /// </summary>
    public static List<TilePlacement> VisibleTiles(
        double slotWidth,
        double slotHeight,
        int level,
        double visibleLeft,
        double visibleTop,
        double visibleRight,
        double visibleBottom,
        int ring = 1)
    {
        var tiles = new List<TilePlacement>();
        if (slotWidth <= 0 || slotHeight <= 0 || level < 0)
        {
            return tiles;
        }

        double dip = TileDip(slotWidth, level);
        if (dip <= 0)
        {
            return tiles;
        }

        int cols = ColumnsAt(level);
        int rows = RowsAt(level, slotWidth, slotHeight);

        int firstCol = Math.Max(0, (int)Math.Floor(visibleLeft / dip) - ring);
        int lastCol = Math.Min(cols - 1, (int)Math.Floor((visibleRight - 1e-9) / dip) + ring);
        int firstRow = Math.Max(0, (int)Math.Floor(visibleTop / dip) - ring);
        int lastRow = Math.Min(rows - 1, (int)Math.Floor((visibleBottom - 1e-9) / dip) + ring);

        for (int row = firstRow; row <= lastRow; row++)
        {
            for (int col = firstCol; col <= lastCol; col++)
            {
                tiles.Add(new TilePlacement(
                    new TileAddress(level, col, row),
                    col * dip,
                    row * dip,
                    dip));
            }
        }

        return tiles;
    }
}
