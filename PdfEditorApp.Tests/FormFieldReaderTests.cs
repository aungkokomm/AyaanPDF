using System;
using System.Collections.Generic;
using System.Linq;
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
            float left, float top, float right, float bottom, string name, string value,
            params (string Label, bool Selected)[] options)
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

            // The choices a combo or list offers, empty for every other kind.
            _bytes.AddRange(BitConverter.GetBytes((uint)options.Length));
            foreach (var (label, selected) in options)
            {
                AddString(label);
                _bytes.Add(selected ? (byte)1 : (byte)0);
            }

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
    public void read_only_and_signature_and_pushbutton_report_as_not_fillable()
    {
        // A radio USED to be here. It is editable now, and that is the point of
        // the Forms milestone; what stays out is a field the document itself
        // locked, a signature (read-only by decision, not by limitation) and a
        // pushbutton, which has nothing to fill.
        byte[] buffer = new BufferBuilder()
            .Add(0, (int)FormFieldKind.Text, 0b01 /* read-only */, 0, 0f, 0f, 1f, 0.1f, "Locked", "x")
            .Add(0, (int)FormFieldKind.Signature, 0, 0, 0f, 0f, 0.3f, 0.1f, "Sig", "")
            .Add(0, (int)FormFieldKind.PushButton, 0, 0, 0f, 0f, 0.3f, 0.1f, "Go", "")
            .Build();

        var fields = FormFieldReader.Parse(buffer);

        Assert.True(fields[0].ReadOnly);
        Assert.False(fields[0].IsFillable);
        Assert.False(fields[1].IsFillable);
        Assert.False(fields[2].IsFillable);
    }

    [Fact]
    public void the_four_kinds_this_milestone_finishes_are_fillable()
    {
        byte[] buffer = new BufferBuilder()
            .Add(0, (int)FormFieldKind.Checkbox, 0, 0, 0f, 0f, 0.02f, 0.02f, "Agree", "Off")
            .Add(0, (int)FormFieldKind.Radio, 0, 1, 0f, 0f, 0.02f, 0.02f, "Colour", "Off")
            .Add(0, (int)FormFieldKind.Combo, 0, 0, 0f, 0f, 0.3f, 0.05f, "Country", "UK")
            .Add(0, (int)FormFieldKind.ListBox, 0, 0, 0f, 0f, 0.3f, 0.2f, "Size", "M")
            .Build();

        var fields = FormFieldReader.Parse(buffer);

        Assert.All(fields, f => Assert.True(f.IsFillable, $"{f.Kind} is not fillable"));
        Assert.True(fields[0].IsToggle);
        Assert.True(fields[1].IsToggle);
        Assert.Equal(1, fields[1].GroupIndex);
        Assert.True(fields[2].IsChoice);
        Assert.True(fields[3].IsChoice);
    }

    [Fact]
    public void a_choice_fields_options_decode_in_order_with_the_one_selected()
    {
        byte[] buffer = new BufferBuilder()
            .Add(0, (int)FormFieldKind.Combo, 0, 0, 0f, 0f, 0.3f, 0.05f, "Country", "United Kingdom",
                 ("United States", false), ("United Kingdom", true), ("Myanmar", false))
            .Build();

        var combo = Assert.Single(FormFieldReader.Parse(buffer));

        Assert.Equal(3, combo.Options.Count);
        Assert.Equal(new[] { 0, 1, 2 }, combo.Options.Select(o => o.Index));
        Assert.Equal("Myanmar", combo.Options[2].Label);
        Assert.True(combo.Options[1].Selected);
        Assert.False(combo.Options[0].Selected);
    }

    [Fact]
    public void a_field_with_no_choices_carries_an_empty_list_rather_than_null()
    {
        byte[] buffer = new BufferBuilder()
            .Add(0, (int)FormFieldKind.Checkbox, 0, 0, 0f, 0f, 0.02f, 0.02f, "Agree", "Off")
            .Build();

        Assert.Empty(Assert.Single(FormFieldReader.Parse(buffer)).Options);
    }

    [Fact]
    public void a_buffer_truncated_inside_its_options_throws_rather_than_reading_on()
    {
        byte[] whole = new BufferBuilder()
            .Add(0, (int)FormFieldKind.Combo, 0, 0, 0f, 0f, 0.3f, 0.05f, "Country", "UK",
                 ("United States", false), ("United Kingdom", true))
            .Build();

        Assert.Throws<ArgumentException>(
            () => FormFieldReader.Parse(whole[..(whole.Length - 3)]));
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
