using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Reads a real form across the FFI boundary: get_form_fields marshals a
/// ByteBuffer, and FormFieldReader has to decode exactly the bytes render_core
/// wrote. A wrong struct layout or string encoding shows up here as a wrong
/// field count, a garbled name, or an access violation.
/// </summary>
public class FormFieldsInteropTests
{
    private const string Lib = "render_core";

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
    private static extern ByteBuffer get_form_fields(ulong docHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void free_byte_buffer(ByteBuffer buffer);

    private static ulong OpenFixture()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "sample_form_rich.pdf");
        Assert.True(File.Exists(path), $"fixture missing at {path}");
        ulong handle = open_document(path);
        Assert.NotEqual(0UL, handle);
        return handle;
    }

    private static System.Collections.Generic.IReadOnlyList<FormField> ReadFields(ulong handle)
    {
        var buf = get_form_fields(handle);
        try
        {
            Assert.Equal(0, buf.Status);
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

    [Fact]
    public void the_rich_form_marshals_back_with_every_field_named_and_typed()
    {
        ulong handle = OpenFixture();
        try
        {
            var fields = ReadFields(handle);

            Assert.Equal(5, fields.Count);

            var text = fields.Single(f => f.Name == "FullName");
            Assert.Equal(FormFieldKind.Text, text.Kind);
            Assert.True(text.IsFillable);
            Assert.True(text.Right > text.Left && text.Bottom > text.Top);

            Assert.Equal(FormFieldKind.Checkbox, fields.Single(f => f.Name == "Subscribe").Kind);

            var radios = fields.Where(f => f.Name == "Plan").ToList();
            Assert.Equal(2, radios.Count);
            Assert.All(radios, r => Assert.Equal(FormFieldKind.Radio, r.Kind));
            Assert.Equal(new[] { 0, 1 }, radios.Select(r => r.GroupIndex).OrderBy(x => x));
            Assert.Single(radios, r => r.Checked); // exactly one selected

            Assert.Equal(FormFieldKind.Combo, fields.Single(f => f.Name == "Country").Kind);
        }
        finally
        {
            close_document(handle);
        }
    }
}
