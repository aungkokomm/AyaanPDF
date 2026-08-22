using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using PdfEditorApp.Rendering.Skia;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// A shadow and a glow on one shape, end to end through the real core.
///
/// The pieces are covered on their own elsewhere: the model reads and writes an
/// effect list, the rasteriser draws several into one picture, and render_core
/// carries effects it does not understand. What is NOT covered by any of those
/// is the whole path together, which is where a shape actually loses its glow:
/// an edit that sends half the list, a rebuild that drops a field, a save that
/// writes a tag the next open cannot read back.
///
/// Every effect crosses as the tag's own text, so these go through
/// <see cref="ShapeEffectsTag.TextOf"/> exactly as the view model does rather
/// than hand-writing the fields, and the assertions read the tag back through
/// <see cref="ShapeTagReader"/>. The struct is mirrored here on purpose; see
/// AnnotationInteropTests for why.
/// </summary>
public class EffectInteropTests
{
    private const string Lib = "render_core";
    private const int OkPdfium = 0;
    private const int Cap = 1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ShapeSpec
    {
        public int PageIndex;
        public int Kind;
        public float X1;
        public float Y1;
        public float X2;
        public float Y2;
        public byte R;
        public byte G;
        public byte B;
        public byte A;
        public float WidthPx;
        public float RotationDeg;
        public uint FillRgba;
        public float CornerRadiusPx;
        public IntPtr EffectsUtf8;
        public nuint EffectsLen;
    }

    /// <summary>Mirrors render_core::PageSize.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PageSize
    {
        public float Width;
        public float Height;
    }

    /// <summary>Mirrors render_core::PageSizeArray.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PageSizeArray
    {
        public IntPtr Sizes;
        public nuint Len;
        public int Status;
    }

    /// <summary>Mirrors render_core::AnnotationArray, for its length alone.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AnnotationArray
    {
        public IntPtr Items;
        public nuint Len;
        public int Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByteBuffer
    {
        public IntPtr Data;
        public nuint Len;
        public int Status;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong open_document([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void close_document(ulong docHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int save_document(
        ulong docHandle, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int add_shape_annotations(
        ulong docHandle, int captureWidth, [In] ShapeSpec[]? specs, nuint specCount);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int restyle_shape_effects_annotation(
        ulong docHandle, int pageIndex, int index, int captureWidth,
        [In] byte[]? effectsUtf8, nuint effectsLen, out int newIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int move_shape_annotation(
        ulong docHandle, int pageIndex, int index, int captureWidth,
        float left, float top, float right, float bottom, out int newIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int resize_shape_annotation(
        ulong docHandle, int pageIndex, int index, int captureWidth,
        float left, float top, float right, float bottom, out int newIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int rotate_shape_annotation(
        ulong docHandle, int pageIndex, int index, int captureWidth,
        float rotationDeg, out int newIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int set_shape_shadow_image(
        ulong docHandle, int pageIndex, int index, int captureWidth,
        float left, float top, float right, float bottom,
        [In] byte[] bgra, nuint bgraLen, int pixelWidth, int pixelHeight, out int newIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int shape_has_shadow_image(ulong docHandle, int pageIndex, int index);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern ByteBuffer get_annotation_contents(
        ulong docHandle, int pageIndex, int index);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void free_byte_buffer(ByteBuffer buffer);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern AnnotationArray get_annotations(ulong docHandle, int pageIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void free_annotation_array(AnnotationArray array);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern PageSizeArray get_page_sizes(ulong docHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void free_page_size_array(PageSizeArray array);

    // ---------------- the harness ----------------

    private static ulong OpenFixture()
    {
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "sample_20pages.pdf");
        Assert.True(System.IO.File.Exists(path), $"fixture missing at {path}");
        ulong handle = open_document(path);
        Assert.NotEqual(0UL, handle);

        return handle;
    }

    /// <summary>
    /// The first page's width in points, which is what turns the tag's lengths
    /// back into the model's.
    ///
    /// ASKED, not assumed. The fixture is not letter-sized, and hard-coding 612
    /// made every length come out a third of what it should be, which reads as
    /// a conversion bug in the code under test rather than in the test.
    /// </summary>
    private static double PageWidthPts(ulong handle)
    {
        var sizes = get_page_sizes(handle);
        try
        {
            Assert.Equal(OkPdfium, sizes.Status);
            Assert.True(sizes.Len > 0, "the document reports no pages");

            return Marshal.PtrToStructure<PageSize>(sizes.Sizes).Width;
        }
        finally
        {
            free_page_size_array(sizes);
        }
    }

    private static string? Tag(ulong handle, int index)
    {
        var buffer = get_annotation_contents(handle, 0, index);
        try
        {
            if (buffer.Status != OkPdfium || buffer.Data == IntPtr.Zero || buffer.Len == 0)
            {
                return null;
            }

            byte[] bytes = new byte[(int)buffer.Len];
            Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);

            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            free_byte_buffer(buffer);
        }
    }

    private static ShapeTag Parsed(ulong handle, int index)
    {
        Assert.True(ShapeTagReader.TryParse(Tag(handle, index), out var tag), "the tag did not parse");

        return tag;
    }

    private static ShapeEffects? EffectsOn(ulong handle, int index) =>
        ShapeEffectsTag.From(Parsed(handle, index), PageWidthPts(handle));

    private static int AddRectangle(ulong handle)
    {
        var spec = new ShapeSpec
        {
            PageIndex = 0,
            Kind = 0,
            X1 = 200, Y1 = 200, X2 = 500, Y2 = 400,
            R = 0x3B, G = 0x82, B = 0xF6, A = 0xFF,
            WidthPx = 4,
        };

        Assert.Equal(OkPdfium, add_shape_annotations(handle, Cap, new[] { spec }, 1));

        // APPENDED, so it is the last annotation on the page. Returning a
        // constant zero worked right up until a test added a second shape, at
        // which point it silently kept editing the first one.
        return Annotations(handle) - 1;
    }

    private static int Annotations(ulong handle)
    {
        var array = get_annotations(handle, 0);
        try
        {
            Assert.Equal(OkPdfium, array.Status);

            return (int)array.Len;
        }
        finally
        {
            free_annotation_array(array);
        }
    }

    /// <summary>Writes a whole effect list the way the view model does.</summary>
    private static int SetEffects(ulong handle, int index, ShapeEffects? effects)
    {
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(
            ShapeEffectsTag.TextOf(effects, Cap));

        Assert.Equal(
            OkPdfium,
            restyle_shape_effects_annotation(
                handle, 0, index, Cap, utf8, (nuint)utf8.Length, out int newIndex));

        return newIndex;
    }

    /// <summary>A shadow whose lengths are round numbers of POINTS on the
    /// fixture's own page, so what comes back is comparable.</summary>
    private static DropShadow ShadowOn(ulong handle) =>
        new(135, 24.0 / PageWidthPts(handle), new RenderColor(0x80, 0, 0, 0),
            6.0 / PageWidthPts(handle));

    private static Glow HaloOn(ulong handle) =>
        new(new RenderColor(0xFF, 0, 0xFF, 0), 8.0 / PageWidthPts(handle));

    // ---------------- one at a time ----------------

    [Fact]
    public void a_shadow_alone_reaches_the_file_and_comes_back()
    {
        ulong handle = OpenFixture();
        try
        {
            int index = SetEffects(handle, AddRectangle(handle), new ShapeEffects().With(ShadowOn(handle)));
            var effects = EffectsOn(handle, index);

            Assert.NotNull(effects!.Shadow);
            Assert.Null(effects.Glow);
            Assert.Equal(135, effects.Shadow!.Value.AngleDeg);
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void a_glow_alone_reaches_the_file_and_comes_back()
    {
        ulong handle = OpenFixture();
        try
        {
            int index = SetEffects(handle, AddRectangle(handle), new ShapeEffects().With(HaloOn(handle)));
            var effects = EffectsOn(handle, index);

            Assert.NotNull(effects!.Glow);
            Assert.Null(effects.Shadow);
            Assert.Equal(8.0 / PageWidthPts(handle), effects.Glow!.Value.Softness, 6);
        }
        finally
        {
            close_document(handle);
        }
    }

    // ---------------- and both, either way round ----------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void adding_the_second_effect_keeps_the_first(bool shadowFirst)
    {
        // THE ONE THIS STEP EXISTS FOR. The core replaces the list wholesale, so
        // the second edit has to start from what the shape already has. Sending
        // only its own effect would leave the shape with whichever was set last.
        ulong handle = OpenFixture();
        try
        {
            int index = AddRectangle(handle);

            index = shadowFirst
                ? SetEffects(handle, index, new ShapeEffects().With(ShadowOn(handle)))
                : SetEffects(handle, index, new ShapeEffects().With(HaloOn(handle)));

            // Read the shape's CURRENT list and add to it, which is what
            // ApplyShadowToSelectedShape and ApplyGlowToSelectedShape do.
            var now = EffectsOn(handle, index) ?? new ShapeEffects();
            index = SetEffects(handle, index, shadowFirst ? now.With(HaloOn(handle)) : now.With(ShadowOn(handle)));

            var both = EffectsOn(handle, index);
            Assert.NotNull(both!.Shadow);
            Assert.NotNull(both.Glow);
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void the_two_orders_produce_the_same_file()
    {
        // Canonical order, all the way down to the bytes in the tag: whichever
        // way round they were added, the document is the same document.
        ulong handle = OpenFixture();
        try
        {
            int shadowFirst = AddRectangle(handle);
            shadowFirst = SetEffects(handle, shadowFirst, new ShapeEffects().With(ShadowOn(handle)));
            shadowFirst = SetEffects(
                handle, shadowFirst, EffectsOn(handle, shadowFirst)!.With(HaloOn(handle)));

            int glowFirst = AddRectangle(handle);
            glowFirst = SetEffects(handle, glowFirst, new ShapeEffects().With(HaloOn(handle)));
            glowFirst = SetEffects(handle, glowFirst, EffectsOn(handle, glowFirst)!.With(ShadowOn(handle)));

            Assert.Equal(
                Parsed(handle, shadowFirst).EffectsText,
                Parsed(handle, glowFirst).EffectsText);
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void taking_one_effect_off_leaves_the_other_on()
    {
        ulong handle = OpenFixture();
        try
        {
            int index = SetEffects(
                handle, AddRectangle(handle), new ShapeEffects().With(ShadowOn(handle)).With(HaloOn(handle)));

            index = SetEffects(handle, index, EffectsOn(handle, index)!.With((DropShadow?)null));

            var left = EffectsOn(handle, index);
            Assert.Null(left!.Shadow);
            Assert.NotNull(left.Glow);
        }
        finally
        {
            close_document(handle);
        }
    }

    // ---------------- and they ride through every edit ----------------

    [Fact]
    public void both_effects_survive_a_move_a_resize_and_a_turn()
    {
        ulong handle = OpenFixture();
        try
        {
            int index = SetEffects(
                handle, AddRectangle(handle), new ShapeEffects().With(ShadowOn(handle)).With(HaloOn(handle)));

            Assert.Equal(
                OkPdfium,
                resize_shape_annotation(handle, 0, index, Cap, 200, 200, 700, 500, out index));
            AssertBoth(handle, index, "the resize");

            Assert.Equal(
                OkPdfium, rotate_shape_annotation(handle, 0, index, Cap, 30, out index));
            AssertBoth(handle, index, "the turn");

            Assert.Equal(
                OkPdfium,
                move_shape_annotation(handle, 0, index, Cap, 300, 300, 800, 600, out index));
            AssertBoth(handle, index, "the move");
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void both_effects_survive_a_save_and_a_reopen()
    {
        string saved = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"ayaan-effects-{Guid.NewGuid():N}.pdf");

        ulong handle = OpenFixture();
        try
        {
            int index = SetEffects(
                handle, AddRectangle(handle), new ShapeEffects().With(ShadowOn(handle)).With(HaloOn(handle)));
            string before = Parsed(handle, index).EffectsText;

            Assert.Equal(OkPdfium, save_document(handle, saved));
            close_document(handle);
            handle = 0;

            ulong reopened = open_document(saved);
            Assert.NotEqual(0UL, reopened);
            try
            {
                AssertBoth(reopened, index, "the reopen");
                Assert.Equal(before, Parsed(reopened, index).EffectsText);
            }
            finally
            {
                close_document(reopened);
            }
        }
        finally
        {
            if (handle != 0) { close_document(handle); }
            try { System.IO.File.Delete(saved); } catch (System.IO.IOException) { }
        }
    }

    [Fact]
    public void an_effect_this_build_cannot_name_survives_an_edit_to_the_ones_it_can()
    {
        // The core preserves it, and this side has to hand it back for the core
        // to preserve. A shape written by a later build must not lose what that
        // build put there just because somebody moved a slider here.
        ulong handle = OpenFixture();
        try
        {
            int index = AddRectangle(handle);

            string text = ShapeEffectsTag.TextOf(new ShapeEffects().With(ShadowOn(handle)), Cap)
                + ":q(b=15.0000,c=FF0000FF,zz=7)";
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(text);
            Assert.Equal(
                OkPdfium,
                restyle_shape_effects_annotation(
                    handle, 0, index, Cap, utf8, (nuint)utf8.Length, out index));

            // Now edit the shadow the way a row would, through the model.
            var loaded = EffectsOn(handle, index);
            Assert.Single(loaded!.Carried);
            index = SetEffects(
                handle, index, loaded.With(new DropShadow(90, 0.05, new RenderColor(0xFF, 0, 0, 0))));

            string tag = Tag(handle, index)!;
            Assert.Contains("q(", tag, StringComparison.Ordinal);
            Assert.Contains("zz=7", tag, StringComparison.Ordinal);
            Assert.Contains("a=90.00", tag, StringComparison.Ordinal);
        }
        finally
        {
            close_document(handle);
        }
    }

    // ---------------- one picture, attached once ----------------

    [Fact]
    public void a_shadow_and_a_glow_attach_as_one_picture()
    {
        ulong handle = OpenFixture();
        try
        {
            int index = SetEffects(
                handle, AddRectangle(handle), new ShapeEffects().With(ShadowOn(handle)).With(HaloOn(handle)));

            Assert.Equal(0, shape_has_shadow_image(handle, 0, index));

            var effects = EffectsOn(handle, index)!;
            var tag = Parsed(handle, index);
            var casters = ShadowRasterizer.CasterItemsFor(
                tag, 0.2, 0.2, 0.5, 0.4, PageWidthPts(handle));
            var raster = ShadowRasterizer.Rasterize(casters, effects.Specs, PageWidthPts(handle));

            Assert.NotNull(raster);

            Assert.Equal(
                OkPdfium,
                set_shape_shadow_image(
                    handle, 0, index, Cap,
                    (float)(raster!.Value.Left * Cap), (float)(raster.Value.Top * Cap),
                    (float)(raster.Value.Right * Cap), (float)(raster.Value.Bottom * Cap),
                    raster.Value.Bgra, (nuint)raster.Value.Bgra.Length,
                    raster.Value.PixelWidth, raster.Value.PixelHeight,
                    out int withPicture));

            Assert.Equal(1, shape_has_shadow_image(handle, 0, withPicture));

            // And the effects are still both on it: attaching rebuilds the
            // annotation from its tag, which is the step that drops things.
            AssertBoth(handle, withPicture, "attaching the picture");
        }
        finally
        {
            close_document(handle);
        }
    }

    private static void AssertBoth(ulong handle, int index, string what)
    {
        var effects = EffectsOn(handle, index);

        Assert.True(effects?.Shadow is not null, $"{what} dropped the shadow");
        Assert.True(effects?.Glow is not null, $"{what} dropped the glow");
    }
}
