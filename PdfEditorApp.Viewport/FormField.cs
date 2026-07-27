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
    double Left,
    double Top,
    double Right,
    double Bottom,
    string Name,
    string Value)
{
    /// <summary>True for the kinds this app can currently fill: text and
    /// checkbox. Radio, choice, signature and buttons are enumerated and shown
    /// but not yet editable.</summary>
    public bool IsFillable => !ReadOnly && Kind is FormFieldKind.Text or FormFieldKind.Checkbox;
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

            fields.Add(new FormField(
                pageIndex,
                ToKind(kind),
                (flags & FlagReadOnly) != 0,
                (flags & FlagChecked) != 0,
                groupIndex,
                left, top, right, bottom,
                name, value));
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
