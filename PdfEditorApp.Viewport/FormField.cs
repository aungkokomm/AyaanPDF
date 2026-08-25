using System;
using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>The interactive control an AcroForm field draws as. Mirrors the
/// FIELD_* constants in render_core.</summary>
public enum FormFieldKind
{
    Other = 0,
    Text = 1,
    Checkbox = 2,
    Radio = 3,
    Combo = 4,
    ListBox = 5,
    PushButton = 6,
    Signature = 7,
}

/// <summary>
/// One choice a combo box or list box offers.
///
/// The label is the DISPLAY half of the field's /Opt entry, which is all the
/// picker needs. The export half never crosses the FFI boundary on purpose: the
/// app sends back an <paramref name="Index"/> and the core resolves the value
/// from the file, so there is no way for the app to write "United Kingdom" into
/// a field whose form expects "UK".
/// </summary>
public readonly record struct FormFieldOption(int Index, string Label, bool Selected);

/// <summary>
/// One form-field widget read from a PDF. Rects are in the same normalized
/// top-left space the app uses everywhere else: both axes divided by the PAGE
/// WIDTH, origin at the page's top-left, so they drop straight onto the overlay.
///
/// <paramref name="GroupIndex"/> is only meaningful for checkbox/radio widgets
/// (a radio group's widgets share <paramref name="Name"/> but each has a unique
/// index). <paramref name="Value"/> is the field's current value; for a radio it
/// is the group's currently-selected export value, shared across the group.
/// </summary>
public readonly record struct FormField(
    int PageIndex,
    FormFieldKind Kind,
    bool ReadOnly,
    bool Checked,
    int GroupIndex,
    bool MultiSelect,
    double Left,
    double Top,
    double Right,
    double Bottom,
    string Name,
    string Value,
    IReadOnlyList<FormFieldOption> Options)
{
    /// <summary>
    /// The kinds this app can edit: a text field, the two button kinds and the
    /// two choice kinds.
    ///
    /// Signature fields are read-only by decision, not by limitation, and
    /// pushbuttons do nothing to fill. Anything the core could not classify is
    /// left alone rather than guessed at.
    ///
    /// ⚠️ A MULTI-SELECT list is excluded, and the core refuses one too. It
    /// holds a LIST of values, and writing a single one over it would discard
    /// every other selection the reader had made. Offering a picker that the
    /// write then declines would be worse than not offering it.
    /// </summary>
    public bool IsFillable => !ReadOnly && !MultiSelect && Kind is FormFieldKind.Text
        or FormFieldKind.Checkbox or FormFieldKind.Radio
        or FormFieldKind.Combo or FormFieldKind.ListBox;

    /// <summary>
    /// True when clicking simply flips a state, with nothing to ask the user.
    /// </summary>
    public bool IsToggle => Kind is FormFieldKind.Checkbox or FormFieldKind.Radio;

    /// <summary>
    /// True when clicking has to offer a list of choices first.
    /// </summary>
    public bool IsChoice => Kind is FormFieldKind.Combo or FormFieldKind.ListBox;

    /// <summary>Whether a normalized page-local point falls inside this widget.</summary>
    public bool Contains(double x, double y)
        => x >= Left && x <= Right && y >= Top && y <= Bottom;
}

/// <summary>
/// Decodes the buffer <c>get_form_fields</c> returns. The C# side of the layout
/// documented in render_core: a u32 count, then per field an i32 page index,
/// kind, flags and group index, four f32 rect edges, and two length-prefixed
/// UTF-8 strings (name, value) — all little-endian. Kept here in the testable
/// Viewport library so the byte layout can be checked without a UI thread.
/// </summary>
public static class FormFieldReader
{
    private const int FlagReadOnly = 1;
    private const int FlagChecked = 2;
    private const int FlagMultiSelect = 4;

    /// <summary>
    /// Parses the whole buffer into fields. Throws nothing on a well-formed,
    /// possibly empty buffer; a truncated buffer throws
    /// <see cref="ArgumentException"/> rather than reading out of bounds.
    /// </summary>
    public static IReadOnlyList<FormField> Parse(ReadOnlySpan<byte> bytes)
    {
        var fields = new List<FormField>();
        if (bytes.Length < 4)
        {
            // An empty document can legitimately return a zero-length buffer.
            return fields;
        }

        int p = 0;
        uint count = ReadU32(bytes, ref p);
        for (uint i = 0; i < count; i++)
        {
            int pageIndex = ReadI32(bytes, ref p);
            int kind = ReadI32(bytes, ref p);
            int flags = ReadI32(bytes, ref p);
            int groupIndex = ReadI32(bytes, ref p);
            float left = ReadF32(bytes, ref p);
            float top = ReadF32(bytes, ref p);
            float right = ReadF32(bytes, ref p);
            float bottom = ReadF32(bytes, ref p);
            string name = ReadString(bytes, ref p);
            string value = ReadString(bytes, ref p);

            int optionCount = (int)ReadU32(bytes, ref p);
            var options = optionCount == 0
                ? (IReadOnlyList<FormFieldOption>)Array.Empty<FormFieldOption>()
                : new List<FormFieldOption>(optionCount);
            for (int o = 0; o < optionCount; o++)
            {
                string label = ReadString(bytes, ref p);
                Require(bytes, p, 1);
                bool selected = bytes[p] != 0;
                p += 1;
                ((List<FormFieldOption>)options).Add(new FormFieldOption(o, label, selected));
            }

            fields.Add(new FormField(
                pageIndex,
                ToKind(kind),
                (flags & FlagReadOnly) != 0,
                (flags & FlagChecked) != 0,
                groupIndex,
                (flags & FlagMultiSelect) != 0,
                left, top, right, bottom,
                name, value, options));
        }

        return fields;
    }

    private static FormFieldKind ToKind(int kind) =>
        Enum.IsDefined(typeof(FormFieldKind), kind) ? (FormFieldKind)kind : FormFieldKind.Other;

    private static void Require(ReadOnlySpan<byte> bytes, int p, int need)
    {
        if (p + need > bytes.Length)
        {
            throw new ArgumentException("form-field buffer is truncated");
        }
    }

    private static uint ReadU32(ReadOnlySpan<byte> b, ref int p)
    {
        Require(b, p, 4);
        uint v = (uint)(b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24));
        p += 4;
        return v;
    }

    private static int ReadI32(ReadOnlySpan<byte> b, ref int p) => unchecked((int)ReadU32(b, ref p));

    private static float ReadF32(ReadOnlySpan<byte> b, ref int p)
    {
        Require(b, p, 4);
        float v = BitConverter.ToSingle(b.Slice(p, 4));
        p += 4;
        return v;
    }

    private static string ReadString(ReadOnlySpan<byte> b, ref int p)
    {
        int len = (int)ReadU32(b, ref p);
        if (len == 0)
        {
            return string.Empty;
        }
        Require(b, p, len);
        string s = System.Text.Encoding.UTF8.GetString(b.Slice(p, len));
        p += len;
        return s;
    }
}
