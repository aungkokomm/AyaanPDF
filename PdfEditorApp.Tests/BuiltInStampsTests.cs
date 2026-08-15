using System;
using System.Globalization;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The built-in stamp library, and the arithmetic that draws it.
///
/// Pure by design, for the reason StampPlacement already records: the geometry
/// mistakes that reached the user were all in code no test could reach. The
/// pixels are Win2D's, and everything that decides what it paints is here.
/// </summary>
public class BuiltInStampsTests
{
    private static StampTheme Theme => StampTheme.Default;

    private static BuiltInStamp Get(string id) =>
        BuiltInStamps.ById(id) ?? throw new InvalidOperationException($"no stamp '{id}'");

    // ---------------- The registry ----------------

    [Fact]
    public void the_library_is_tier_one_and_nothing_else()
    {
        // Twelve words and five marks, agreed before any of this was written.
        // Tier 2 is a row each in the same table, so this is what stops it
        // arriving early and unreviewed.
        var words = BuiltInStamps.All.Where(s => s.Style != StampStyle.Mark).ToList();
        var marks = BuiltInStamps.All.Where(s => s.Style == StampStyle.Mark).ToList();

        Assert.Equal(12, words.Count);
        Assert.Equal(5, marks.Count);
    }

    [Fact]
    public void every_stamp_has_its_own_id()
    {
        // Ids are how a stamp is remembered between sessions. Two the same and
        // the picker would restore the wrong one, silently.
        var ids = BuiltInStamps.All.Select(s => s.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void a_word_stamp_has_words_and_a_mark_has_a_path()
    {
        // The two halves of the library have opposite requirements, and a row
        // added to the table with neither would draw an empty box.
        foreach (var stamp in BuiltInStamps.All)
        {
            if (stamp.Style == StampStyle.Mark)
            {
                Assert.Equal("", stamp.Label);
                Assert.NotEqual("", stamp.MarkPath);
            }
            else
            {
                Assert.NotEqual("", stamp.Label);
            }
        }
    }

    [Fact]
    public void the_flag_style_carries_the_mark_it_needs()
    {
        // SIGN HERE is a word AND an arrow. Losing the path would leave it
        // looking like an ordinary badge with an odd aspect.
        var sign = Get("sign-here");

        Assert.Equal(StampStyle.Flag, sign.Style);
        Assert.Equal(BuiltInStamps.Marks.Arrow, sign.MarkPath);
    }

    [Fact]
    public void labels_are_upper_case()
    {
        // Every stamp anyone has seen is. A lower-case one would read as a
        // caption rather than a stamp.
        foreach (var stamp in BuiltInStamps.All.Where(s => s.Label.Length > 0))
        {
            Assert.Equal(stamp.Label.ToUpperInvariant(), stamp.Label);
        }
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("final")]
    [InlineData("paid")]
    public void approval_is_green(string id) =>
        Assert.True(Get(id).Color.G > Get(id).Color.R && Get(id).Color.G > Get(id).Color.B,
            "an approving stamp that is not green breaks the convention everyone reads these by");

    [Theory]
    [InlineData("not-approved")]
    [InlineData("void")]
    [InlineData("urgent")]
    [InlineData("confidential")]
    public void refusal_and_warning_are_red(string id) =>
        Assert.True(Get(id).Color.R > Get(id).Color.G && Get(id).Color.R > Get(id).Color.B,
            "a refusing stamp that is not red breaks the convention everyone reads these by");

    [Fact]
    public void only_the_stamps_that_should_be_dated_are()
    {
        // A date on APPROVED would be wrong: it is the reviewer's date, not
        // today's. RECEIVED and PAID are the two where today is the point.
        var dated = BuiltInStamps.All.Where(s => s.DateSuffix).Select(s => s.Id).ToList();

        Assert.Equal(new[] { "received", "paid" }, dated);
    }

    // ---------------- Identity ----------------

    [Fact]
    public void a_built_in_id_can_never_be_mistaken_for_a_file()
    {
        // This is the whole reason for the prefix. The picker remembers the
        // last stamp by writing a name to a file, and that file has to be able
        // to hold either kind. A colon is illegal in a Windows file name, so
        // nothing in the user's folder can ever produce this string.
        foreach (var stamp in BuiltInStamps.All)
        {
            string id = BuiltInStamps.EntryId(stamp);

            Assert.Contains(':', id);
            Assert.Contains(id.IndexOf(':'), System.IO.Path.GetInvalidFileNameChars()
                .Select(c => id.IndexOf(c))
                .Where(i => i >= 0)
                .ToList());
        }
    }

    [Fact]
    public void an_entry_id_round_trips()
    {
        foreach (var stamp in BuiltInStamps.All)
        {
            Assert.Same(stamp, BuiltInStamps.FromEntryId(BuiltInStamps.EntryId(stamp)));
        }
    }

    [Theory]
    [InlineData("signature.png")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("builtin:no-such-stamp")]
    public void anything_that_is_not_a_built_in_resolves_to_nothing(string? entryId) =>
        Assert.Null(BuiltInStamps.FromEntryId(entryId));

    // ---------------- Geometry ----------------

    [Fact]
    public void height_follows_the_style_rather_than_being_asked_for()
    {
        // The caller wants a stamp a certain width; the proportions ARE the
        // design. A banner is wider than a badge, and a mark is square.
        Assert.Equal(
            (int)Math.Round(600 / Theme.BadgeAspect),
            BuiltInStamps.Measure(Get("approved"), 600, Theme).Height);

        Assert.Equal(
            (int)Math.Round(600 / Theme.BannerAspect),
            BuiltInStamps.Measure(Get("draft"), 600, Theme).Height);

        var mark = BuiltInStamps.Measure(Get("mark-check"), 600, Theme);
        Assert.Equal(mark.Width, mark.Height);
    }

    [Fact]
    public void the_same_stamp_at_two_sizes_is_the_same_design()
    {
        // Everything is a fraction of the height for this reason. When the
        // border was a fixed pixel count, a small stamp was mostly border and a
        // large one had a hairline.
        var small = BuiltInStamps.Measure(Get("approved"), 200, Theme);
        var large = BuiltInStamps.Measure(Get("approved"), 800, Theme);

        double scale = (double)large.Height / small.Height;

        Assert.Equal(small.Border * scale, large.Border, 3);
        Assert.Equal(small.CornerRadius * scale, large.CornerRadius, 3);
        Assert.Equal(small.Inset * scale, large.Inset, 3);
        Assert.Equal(small.LabelSize * scale, large.LabelSize, 3);
    }

    [Fact]
    public void a_dated_stamp_gives_the_word_less_room()
    {
        // Two lines in the same box. Without this the label kept its full size
        // and the date was pushed out through the bottom border.
        var plain = BuiltInStamps.Measure(Get("reviewed"), 600, Theme);
        var dated = BuiltInStamps.Measure(Get("received"), 600, Theme);

        Assert.True(dated.LabelSize < plain.LabelSize);
        Assert.True(dated.DateSize > 0);
        Assert.Equal(0, plain.DateSize);

        // And the two lines together still fit between the borders.
        Assert.True(
            dated.LabelSize + dated.DateSize < dated.Height - (2 * dated.Border),
            "the label and its date do not fit inside the stamp");
    }

    [Fact]
    public void only_a_badge_is_rounded()
    {
        // A banner with rounded corners reads as a button.
        Assert.True(BuiltInStamps.Measure(Get("approved"), 600, Theme).CornerRadius > 0);
        Assert.Equal(0, BuiltInStamps.Measure(Get("draft"), 600, Theme).CornerRadius);
    }

    [Fact]
    public void a_stamp_cannot_be_measured_at_no_width() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BuiltInStamps.Measure(Get("approved"), 0, Theme));

    [Fact]
    public void a_tiny_stamp_still_has_a_height()
    {
        // Rounding a banner's height down to zero would hand Win2D a zero-sized
        // render target, which throws rather than drawing nothing.
        Assert.True(BuiltInStamps.Measure(Get("draft"), 1, Theme).Height >= 1);
    }

    // ---------------- Fitting text ----------------

    [Fact]
    public void text_that_fits_is_left_alone()
    {
        // The common case, and it must not shrink by a rounding error: every
        // short label would come out fractionally smaller than designed.
        Assert.Equal(48, BuiltInStamps.FitSize(48, measuredWidth: 300, availableWidth: 400));
        Assert.Equal(48, BuiltInStamps.FitSize(48, measuredWidth: 400, availableWidth: 400));
    }

    [Fact]
    public void text_that_does_not_fit_is_shrunk_until_it_does()
    {
        // NOT APPROVED and CONFIDENTIAL are the ones this exists for.
        double size = BuiltInStamps.FitSize(48, measuredWidth: 800, availableWidth: 400);

        Assert.Equal(24, size);

        // The property that matters, rather than the number: scaling the
        // measurement by the same factor now fits.
        Assert.True(800 * (size / 48) <= 400 + 0.001);
    }

    [Theory]
    [InlineData(0, 100, 100)]
    [InlineData(48, 0, 100)]
    [InlineData(48, 100, 0)]
    public void a_degenerate_measurement_changes_nothing(double size, double measured, double available) =>
        Assert.Equal(size, BuiltInStamps.FitSize(size, measured, available));

    // ---------------- Dates ----------------

    [Fact]
    public void the_date_is_written_the_way_the_reader_writes_dates()
    {
        // 15/08/2026 and 8/15/2026 mean different things to different people,
        // and a stamp that reads wrong is worse than one that reads unfamiliar.
        //
        // useUserOverride: false, or this test asserts on whatever the machine
        // running it has in Regional Settings. It caught this one out already:
        // "en-US" came back as 15-Aug-26 here. Production deliberately does the
        // opposite and passes CurrentCulture, overrides and all, because there
        // the user's own format is the right answer.
        var day = new DateTime(2026, 8, 15);

        Assert.Equal("15/08/2026", BuiltInStamps.DateLine(
            Get("received"), day, new CultureInfo("en-GB", useUserOverride: false)));
        Assert.Equal("8/15/2026", BuiltInStamps.DateLine(
            Get("received"), day, new CultureInfo("en-US", useUserOverride: false)));
    }

    [Fact]
    public void a_stamp_without_a_date_gets_no_date() =>
        Assert.Equal("", BuiltInStamps.DateLine(
            Get("approved"), new DateTime(2026, 8, 15), CultureInfo.InvariantCulture));

    // ---------------- Centring the ink ----------------

    [Fact]
    public void ink_with_no_offset_centres_the_obvious_way()
    {
        // 20 wide inside 100 leaves 40 either side.
        Assert.Equal(40, BuiltInStamps.CenterOffset(0, 100, 0, 20));
    }

    [Fact]
    public void the_inks_own_offset_is_taken_back_out()
    {
        // The heart of the bug this fixes. Oswald's line box carries far more
        // space above the capitals than below, so the ink starts well down the
        // layout. Centring the BOX put the visible word low in the frame; the
        // offset has to be subtracted so it is the INK that ends up centred.
        double y = BuiltInStamps.CenterOffset(0, 100, inkOffset: 30, inkSize: 20);

        Assert.Equal(10, y);

        // Which is the property that matters: the ink lands at 40, not 70.
        Assert.Equal(40, y + 30);
    }

    [Fact]
    public void a_band_that_does_not_start_at_zero_is_respected()
    {
        // Both bands of a dated stamp start part-way down the frame.
        Assert.Equal(240, BuiltInStamps.CenterOffset(200, 100, 0, 20));
    }

    [Fact]
    public void ink_bigger_than_its_band_overhangs_evenly()
    {
        // Rather than being pinned to one edge, which would look like a
        // mistake instead of a tight fit.
        Assert.Equal(-10, BuiltInStamps.CenterOffset(0, 100, 0, 120));
    }

    // ---------------- The strip's width ----------------

    [Fact]
    public void the_strip_grows_with_the_window()
    {
        // The complaint this answers: a fixed 320 showed about four tiles of
        // seventeen and never changed, however big the window got.
        double narrow = BuiltInStamps.StripMaxWidth(900);
        double wide = BuiltInStamps.StripMaxWidth(1800);

        Assert.True(wide > narrow);
        Assert.Equal(900 - BuiltInStamps.StripReserve, narrow);
    }

    [Fact]
    public void the_strip_never_collapses_on_a_narrow_window()
    {
        // A few tiles still reachable beats none. Without the floor a small
        // window gives a negative width, which is a layout exception rather
        // than a small strip.
        Assert.Equal(BuiltInStamps.MinStripWidth, BuiltInStamps.StripMaxWidth(100));
        Assert.Equal(BuiltInStamps.MinStripWidth, BuiltInStamps.StripMaxWidth(0));
    }

    [Fact]
    public void the_strip_leaves_room_for_the_rest_of_the_row()
    {
        // The label, the separator, the two buttons and the bar's padding all
        // share this row. Taking the whole viewport would push them off the
        // edge of the property bar, which sizes itself to its content.
        Assert.True(
            BuiltInStamps.StripMaxWidth(1600) < 1600,
            "the strip has taken the whole width and left nothing for the buttons");
    }

    // ---------------- Alpha ----------------

    [Fact]
    public void undoing_the_premultiply_restores_the_colour()
    {
        // A half-transparent pure red arrives from Win2D as (0,0,128,128) and
        // has to reach PDFium as (0,0,255,128). Without this every antialiased
        // letter edge is darkened against the soft mask, which on a stamp is
        // every letter.
        byte[] bgra = [0, 0, 128, 128];

        StampAlpha.Unpremultiply(bgra);

        Assert.Equal(255, bgra[2]);
        Assert.Equal(128, bgra[3]);
    }

    [Fact]
    public void opaque_and_transparent_pixels_are_left_exactly_as_they_are()
    {
        // Opaque needs no division, and transparent would be a division by
        // zero. Both are the overwhelming majority of a stamp's pixels.
        byte[] bgra = [10, 20, 30, 255, 77, 88, 99, 0];
        byte[] before = (byte[])bgra.Clone();

        StampAlpha.Unpremultiply(bgra);

        Assert.Equal(before, bgra);
    }

    [Fact]
    public void a_channel_can_never_come_out_over_255()
    {
        // Rounding in the original multiply can leave a channel fractionally
        // above its own alpha. Dividing that without clamping wraps a bright
        // edge round to black, which shows as a dark rim on every glyph.
        byte[] bgra = [200, 200, 200, 100];

        StampAlpha.Unpremultiply(bgra);

        Assert.Equal(255, bgra[0]);
        Assert.Equal(255, bgra[1]);
        Assert.Equal(255, bgra[2]);
    }

    [Fact]
    public void a_buffer_with_a_ragged_tail_does_not_throw()
    {
        // Defensive only: the loop must not read past the end if a caller ever
        // hands over a length that is not a whole number of pixels.
        byte[] bgra = [0, 0, 128, 128, 1, 2];

        StampAlpha.Unpremultiply(bgra);

        Assert.Equal(255, bgra[2]);
    }

    // ---------------- Rotation ----------------

    [Fact]
    public void a_diagonal_stamp_is_given_room_for_its_corners()
    {
        // Rotating inside the original box clips the corners off, which on
        // DRAFT takes the D and the T with them.
        var (w, h) = BuiltInStamps.RotatedBounds(400, 100, -12);

        Assert.True(w > 400);
        Assert.True(h > 100);
    }

    [Fact]
    public void an_unrotated_stamp_is_not_padded() =>
        Assert.Equal((400, 100), BuiltInStamps.RotatedBounds(400, 100, 0));

    [Fact]
    public void rotating_either_way_needs_the_same_room() =>
        Assert.Equal(
            BuiltInStamps.RotatedBounds(400, 100, -12),
            BuiltInStamps.RotatedBounds(400, 100, 12));
}
