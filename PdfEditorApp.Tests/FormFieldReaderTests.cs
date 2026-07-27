using System;
using System.Collections.Generic;
using System.Text;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The C# parser has to agree with render_core's byte layout exactly: one wrong
/// offset here and every field's rect and name shifts. These build buffers by
/// hand in that layout and assert the decode, so a layout drift on either side
/// is caught without a PDF or a UI.
/// </summary>
public class FormFieldReaderTests
{
    private sealed class BufferBuilder
    {
        private readonly List<byte> _bytes = new();
        private uint _count;

        public byte[] Build()
        {
            var head = new byte[4];
            BitConverter.TryWriteBytes(head, _count);
            var all = new byte[4 + _bytes.Count];
            head.CopyTo(all, 0);
            _bytes.CopyTo(all, 4);
            return all;
        }

        public BufferBuilder Add(int page, int kind, int flags, int groupIndex,
            float left, float top, float right, float bottom, string name, string value)
        {
            AddI32(page);
            AddI32(kind);
            AddI32(flags);
            AddI32(groupIndex);
            AddF32(left);
            AddF32(top);
            AddF32(right);
            AddF32(bottom);
            AddString(name);
            AddString(value);
            _count++;
            return this;
        }

        private void AddI32(int v) => _bytes.AddRange(BitConverter.GetBytes(v));
        private void AddF32(float v) => _bytes.AddRange(BitConverter.GetBytes(v));
        private void AddString(string s)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(s);
            _bytes.AddRange(BitConverter.GetBytes((uint)utf8.Length));
            _bytes.AddRange(utf8);
        }
    }

    [Fact]
    public void a_text_field_and_a_checked_checkbox_decode_with_their_rects_and_values()
    {
        byte[] buffer = new BufferBuilder()
            .Add(0, (int)FormFieldKind.Text, 0, 0, 0.278f, 0.098f, 0.637f, 0.131f, "FullName", "")
            .Add(1, (int)FormFieldKind.Checkbox, 0b10 /* checked */, 0, 0.1f, 0.2f, 0.13f, 0.23f, "Subscribe", "Yes")
            .Build();

        var fields = FormFieldReader.Parse(buffer);

        Assert.Equal(2, fields.Count);

        var text = fields[0];
        Assert.Equal(FormFieldKind.Text, text.Kind);
        Assert.Equal(0, text.PageIndex);
        Assert.Equal("FullName", text.Name);
        Assert.False(text.Checked);
        Assert.False(text.ReadOnly);
        Assert.True(text.IsFillable);
        Assert.Equal(0.278, text.Left, 3);
        Assert.Equal(0.131, text.Bottom, 3);

        var box = fields[1];
        Assert.Equal(FormFieldKind.Checkbox, box.Kind);
        Assert.Equal(1, box.PageIndex);
        Assert.True(box.Checked);
        Assert.Equal("Yes", box.Value);
        Assert.True(box.IsFillable);
    }

    [Fact]
    public void read_only_and_radio_and_signature_report_as_not_fillable()
    {
        byte[] buffer = new BufferBuilder()
            .Add(0, (int)FormFieldKind.Text, 0b01 /* read-only */, 0, 0f, 0f, 1f, 0.1f, "Locked", "x")
            .Add(0, (int)FormFieldKind.Radio, 0, 1, 0f, 0f, 0.02f, 0.02f, "Plan", "Pro")
            .Add(0, (int)FormFieldKind.Signature, 0, 0, 0f, 0f, 0.3f, 0.1f, "Sig", "")
            .Build();

        var fields = FormFieldReader.Parse(buffer);

        Assert.True(fields[0].ReadOnly);
        Assert.False(fields[0].IsFillable);
        Assert.Equal(FormFieldKind.Radio, fields[1].Kind);
        Assert.Equal(1, fields[1].GroupIndex);
        Assert.False(fields[1].IsFillable);
        Assert.False(fields[2].IsFillable);
    }

    [Fact]
    public void an_empty_or_zerocount_buffer_yields_no_fields()
    {
        Assert.Empty(FormFieldReader.Parse(Array.Empty<byte>()));
        Assert.Empty(FormFieldReader.Parse(new byte[4])); // count = 0
    }

    [Fact]
    public void an_unknown_kind_is_reported_as_other_rather_than_throwing()
    {
        byte[] buffer = new BufferBuilder()
            .Add(0, 999, 0, 0, 0f, 0f, 1f, 1f, "Weird", "")
            .Build();

        var fields = FormFieldReader.Parse(buffer);
        Assert.Equal(FormFieldKind.Other, fields[0].Kind);
    }

    [Fact]
    public void a_truncated_buffer_throws_rather_than_reading_out_of_bounds()
    {
        byte[] full = new BufferBuilder()
            .Add(0, (int)FormFieldKind.Text, 0, 0, 0f, 0f, 1f, 0.1f, "Name", "value")
            .Build();

        // Claim one field but cut the bytes short.
        byte[] truncated = new byte[full.Length - 5];
        Array.Copy(full, truncated, truncated.Length);

        Assert.Throws<ArgumentException>(() => FormFieldReader.Parse(truncated));
    }
}
