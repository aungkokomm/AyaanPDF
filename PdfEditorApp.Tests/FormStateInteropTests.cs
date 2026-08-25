using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The four field kinds the Forms milestone finishes, across the real FFI.
///
/// The fixture is built to be awkward on purpose: the checkbox's on state is
/// <c>/On</c> rather than <c>/Yes</c>, each radio has its own on state
/// (<c>/Red</c>, <c>/Green</c>, <c>/Blue</c>), and <c>/Opt</c> is in the
/// <c>[export display]</c> pair form. Anything that assumes a name or writes a
/// display label where the export value belongs fails here rather than in
/// someone's document.
/// </summary>
public class FormStateInteropTests : IDisposable
{
    private const string Lib = "render_core";
    private const int OkPdfium = 0;
    private const int InvalidInput = 1;

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
    private static extern ByteBuffer get_form_fields(ulong docHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void free_byte_buffer(ByteBuffer buffer);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int set_form_field_state(
        ulong docHandle, byte[] fieldNameUtf8, nuint fieldNameLen, int kind, int index, int on);

    private readonly List<string> _temporary = new();

    public void Dispose()
    {
        foreach (string path in _temporary)
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private static ulong OpenChoiceForm()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "sample_form_choice.pdf");
        Assert.True(File.Exists(path), $"fixture missing at {path}");
        ulong handle = open_document(path);
        Assert.NotEqual(0UL, handle);
        return handle;
    }

    private static IReadOnlyList<FormField> Fields(ulong handle)
    {
        var buf = get_form_fields(handle);
        try
        {
            Assert.Equal(OkPdfium, buf.Status);
            byte[] bytes = new byte[(int)buf.Len];
            if (bytes.Length > 0)
            {
                Marshal.Copy(buf.Data, bytes, 0, bytes.Length);
            }
            return FormFieldReader.Parse(bytes);
        }
        finally
        {
            free_byte_buffer(buf);
        }
    }

    private static FormField Named(ulong handle, string name)
        => Fields(handle).First(f => f.Name == name);

    private static int Set(ulong handle, FormField field, int index, bool on)
    {
        byte[] name = Encoding.UTF8.GetBytes(field.Name);
        return set_form_field_state(
            handle, name, (nuint)name.Length, (int)field.Kind, index, on ? 1 : 0);
    }

    /// <summary>Saves and reopens, which is the acceptance criterion that matters.</summary>
    private ulong RoundTrip(ulong handle, string name)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ayaan-{name}-{Guid.NewGuid():N}.pdf");
        _temporary.Add(path);

        Assert.Equal(OkPdfium, save_document(handle, path));
        close_document(handle);

        ulong reopened = open_document(path);
        Assert.NotEqual(0UL, reopened);
        return reopened;
    }

    [Fact]
    public void every_kind_this_milestone_finishes_reads_back_as_editable()
    {
        ulong handle = OpenChoiceForm();
        try
        {
            var fields = Fields(handle);

            Assert.Equal(FormFieldKind.Checkbox, Named(handle, "Agree").Kind);
            Assert.Equal(FormFieldKind.Combo, Named(handle, "Country").Kind);
            Assert.Equal(FormFieldKind.ListBox, Named(handle, "Size").Kind);
            Assert.Equal(3, fields.Count(f => f.Name == "Colour"));

            Assert.All(
                fields.Where(f => f.Name is "Agree" or "Colour" or "Country" or "Size"),
                f => Assert.True(f.IsFillable));
        }
        finally
        {
            close_document(handle);
        }
    }

    [Fact]
    public void a_checkbox_ticks_and_clears_and_survives_a_reopen()
    {
        ulong handle = OpenChoiceForm();
        var box = Named(handle, "Agree");
        Assert.False(box.Checked);

        Assert.Equal(OkPdfium, Set(handle, box, box.GroupIndex, true));
        Assert.True(Named(handle, "Agree").Checked);

        handle = RoundTrip(handle, "checkbox");
        Assert.True(Named(handle, "Agree").Checked);

        Assert.Equal(OkPdfium, Set(handle, Named(handle, "Agree"), 0, false));
        Assert.False(Named(handle, "Agree").Checked);

        close_document(handle);
    }

    [Fact]
    public void selecting_a_radio_moves_the_selection_and_survives_a_reopen()
    {
        ulong handle = OpenChoiceForm();
        var radios = Fields(handle).Where(f => f.Name == "Colour").ToList();
        Assert.All(radios, r => Assert.False(r.Checked));

        Assert.Equal(OkPdfium, Set(handle, radios[0], 0, true));
        Assert.Equal(0, Fields(handle).Single(f => f.Name == "Colour" && f.Checked).GroupIndex);

        Assert.Equal(OkPdfium, Set(handle, radios[0], 2, true));
        var after = Fields(handle).Where(f => f.Name == "Colour").ToList();
        Assert.Single(after.Where(r => r.Checked));
        Assert.Equal(2, after.Single(r => r.Checked).GroupIndex);

        handle = RoundTrip(handle, "radio");
        var reopened = Fields(handle).Where(f => f.Name == "Colour").ToList();
        Assert.Single(reopened.Where(r => r.Checked));
        Assert.Equal(2, reopened.Single(r => r.Checked).GroupIndex);

        close_document(handle);
    }

    [Fact]
    public void a_combo_takes_a_new_option_and_survives_a_reopen()
    {
        ulong handle = OpenChoiceForm();
        var combo = Named(handle, "Country");
        Assert.Equal("United Kingdom", combo.Value);
        Assert.True(combo.Options[1].Selected);

        Assert.Equal(OkPdfium, Set(handle, combo, 2, true));

        handle = RoundTrip(handle, "combo");
        var after = Named(handle, "Country");
        Assert.Equal("Myanmar", after.Value);
        Assert.True(after.Options[2].Selected);
        Assert.False(after.Options[1].Selected);

        close_document(handle);
    }

    [Fact]
    public void a_list_box_takes_a_new_option_and_survives_a_reopen()
    {
        ulong handle = OpenChoiceForm();
        var list = Named(handle, "Size");
        Assert.Equal("Medium", list.Value);

        Assert.Equal(OkPdfium, Set(handle, list, 0, true));

        handle = RoundTrip(handle, "list");
        var after = Named(handle, "Size");
        Assert.Equal("Small", after.Value);
        Assert.True(after.Options[0].Selected);

        close_document(handle);
    }

    [Fact]
    public void every_field_is_still_an_interactive_form_field_after_all_four_edits()
    {
        // ⚠️ THE POINT OF DOING THIS PROPERLY. A form "filled" by covering its
        // controls with drawings is not a form: nobody else can change it and no
        // reader can submit it.
        ulong handle = OpenChoiceForm();
        int before = Fields(handle).Count;

        Assert.Equal(OkPdfium, Set(handle, Named(handle, "Agree"), 0, true));
        Assert.Equal(OkPdfium, Set(handle, Named(handle, "Colour"), 1, true));
        Assert.Equal(OkPdfium, Set(handle, Named(handle, "Country"), 0, true));
        Assert.Equal(OkPdfium, Set(handle, Named(handle, "Size"), 2, true));

        handle = RoundTrip(handle, "still-a-form");
        var fields = Fields(handle);

        Assert.Equal(before, fields.Count);
        Assert.Equal(FormFieldKind.Checkbox, Named(handle, "Agree").Kind);
        Assert.Equal(FormFieldKind.Combo, Named(handle, "Country").Kind);
        Assert.Equal(FormFieldKind.ListBox, Named(handle, "Size").Kind);
        Assert.Equal(3, fields.Count(f => f.Name == "Colour"));
        Assert.All(fields.Where(f => f.IsFillable), f => Assert.True(f.Right > f.Left));

        // And all four states came through together.
        Assert.True(Named(handle, "Agree").Checked);
        Assert.Equal(1, fields.Single(f => f.Name == "Colour" && f.Checked).GroupIndex);
        Assert.Equal("United States", Named(handle, "Country").Value);
        Assert.Equal("Large", Named(handle, "Size").Value);

        close_document(handle);
    }

    [Fact]
    public void the_text_field_is_untouched_by_any_of_it()
    {
        ulong handle = OpenChoiceForm();
        var before = Named(handle, "Notes");

        Assert.Equal(OkPdfium, Set(handle, Named(handle, "Agree"), 0, true));

        var after = Named(handle, "Notes");
        Assert.Equal(before.Kind, after.Kind);
        Assert.Equal(before.Value, after.Value);
        Assert.Equal(before.Left, after.Left, 5);

        close_document(handle);
    }

    [Fact]
    public void a_request_that_names_nothing_is_refused_and_changes_nothing()
    {
        ulong handle = OpenChoiceForm();
        var box = Named(handle, "Agree");

        byte[] nowhere = Encoding.UTF8.GetBytes("NoSuchField");
        Assert.Equal(InvalidInput, set_form_field_state(
            handle, nowhere, (nuint)nowhere.Length, (int)FormFieldKind.Checkbox, 0, 1));

        Assert.Equal(InvalidInput, Set(handle, box, 99, true));
        Assert.False(Named(handle, "Agree").Checked);

        close_document(handle);
    }
}
