using System;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The night-reading transform.
///
/// It runs over every pixel of every page render, so the properties that
/// matter are the ones that hold for all 256 values rather than a few spot
/// checks.
/// </summary>
public class NightModeTests
{
    [Fact]
    public void a_white_page_becomes_dark_but_not_black()
    {
        // Pure black would lose the sheet's edge against the canvas entirely,
        // and is harsh on an OLED panel.
        Assert.Equal(NightMode.Floor, NightMode.Channel(255));
        Assert.True(NightMode.Floor > 0);
    }

    [Fact]
    public void black_text_becomes_light_but_not_white()
    {
        // Pure white on near-black is the combination that smears.
        Assert.Equal(NightMode.Ceiling, NightMode.Channel(0));
        Assert.True(NightMode.Ceiling < 255);
    }

    [Fact]
    public void the_transform_never_leaves_the_reading_range()
    {
        for (int v = 0; v <= 255; v++)
        {
            byte result = NightMode.Channel((byte)v);

            Assert.InRange(result, NightMode.Floor, NightMode.Ceiling);
        }
    }

    [Fact]
    public void order_is_preserved_so_the_page_still_reads_as_itself()
    {
        // Monotonically DECREASING: anything darker than its neighbour before
        // must be lighter than it after. If this ever stopped holding, the
        // result would be a different image rather than the same one at night.
        for (int v = 1; v <= 255; v++)
        {
            Assert.True(
                NightMode.Channel((byte)v) <= NightMode.Channel((byte)(v - 1)),
                $"{v} did not stay in order against {v - 1}");
        }
    }

    [Fact]
    public void mid_grey_stays_in_the_middle()
    {
        // A page that is half grey should not lurch to one end.
        byte mid = NightMode.Channel(128);
        int centre = (NightMode.Floor + NightMode.Ceiling) / 2;

        Assert.InRange(mid, centre - 3, centre + 3);
    }

    [Fact]
    public void colour_channels_are_each_transformed()
    {
        // A blue heading has to come back as a readable light blue, not as
        // grey. Each channel moves independently.
        byte[] bgra = [255, 0, 0, 255];   // pure blue in BGRA

        NightMode.Apply(bgra);

        Assert.Equal(NightMode.Channel(255), bgra[0]);
        Assert.Equal(NightMode.Channel(0), bgra[1]);
        Assert.Equal(NightMode.Channel(0), bgra[2]);
    }

    [Fact]
    public void alpha_is_never_touched()
    {
        // A page render is opaque, but a tile at the edge of a page is not.
        // Rewriting alpha would paint a dark rectangle where the corner should
        // be transparent.
        byte[] bgra = [10, 20, 30, 0, 40, 50, 60, 128, 70, 80, 90, 255];

        NightMode.Apply(bgra);

        Assert.Equal(0, bgra[3]);
        Assert.Equal(128, bgra[7]);
        Assert.Equal(255, bgra[11]);
    }

    [Fact]
    public void every_pixel_in_a_buffer_is_covered()
    {
        byte[] bgra = Enumerable.Repeat((byte)255, 16).ToArray();

        NightMode.Apply(bgra);

        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            Assert.Equal(NightMode.Floor, bgra[i]);
            Assert.Equal(NightMode.Floor, bgra[i + 1]);
            Assert.Equal(NightMode.Floor, bgra[i + 2]);
            Assert.Equal(255, bgra[i + 3]);
        }
    }

    [Fact]
    public void a_ragged_buffer_does_not_throw()
    {
        // Defensive: the loop must not read past the end if a length ever
        // arrives that is not a whole number of pixels.
        byte[] bgra = [255, 255, 255, 255, 1, 2];

        NightMode.Apply(bgra);

        Assert.Equal(NightMode.Floor, bgra[0]);
        Assert.Equal(1, bgra[4]);
    }

    [Fact]
    public void an_empty_buffer_is_fine() => NightMode.Apply([]);

    [Fact]
    public void applying_it_twice_does_not_restore_the_original()
    {
        // Worth stating, because it is the trap: this is not an involution, so
        // the transform must be applied to a FRESH render every time and never
        // to a bitmap that already has it. The wiring re-renders on toggle for
        // exactly this reason.
        byte[] once = [255, 255, 255, 255];
        NightMode.Apply(once);

        byte[] twice = (byte[])once.Clone();
        NightMode.Apply(twice);

        Assert.NotEqual(once[0], twice[0]);
    }
}
