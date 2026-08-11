using System;
using System.Collections.Generic;
using System.Linq;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// The new picker against the old one, on the cases where they must agree.
///
/// Selection is moving from <see cref="LoadedAnnotationPicker.PickTopmost"/>,
/// which tests an annotation's /Rect, to <see cref="ObjectHitTest.PickTopmost"/>,
/// which tests the object. The whole point is that they DISAGREE about a
/// diagonal arrow, an ellipse's corners and a turned box. Everything else is
/// supposed to be unchanged, and "everything else" is most of what a user
/// clicks: upright rectangles, text boxes, stamps, foreign annotations.
///
/// So these sweep a grid of points over pages of such objects and assert the
/// two pickers name the same object at every one. A migration that quietly
/// changed which text box a click selects would not be caught by the geometry
/// tests, which only ever look at one object at a time.
///
/// Points within the pick tolerance of any edge are skipped, because there the
/// two are MEANT to differ: the old picker required strict containment and the
/// new one is deliberately generous by
/// <see cref="AnnotationHitTester.DefaultTolerance"/>. That difference is
/// tested on its own at the bottom.
/// </summary>
public class PickerEquivalenceTests
{
    private const double PageWidthPts = 1000;
    private const double Tol = AnnotationHitTester.DefaultTolerance;

    /// <summary>A shape's /Rect, i.e. the drag grown by the writer's pad.</summary>
    private static TextRect Padded(double l, double t, double r, double b, double strokePts = 2)
    {
        double pad = ((strokePts / 2) + 1) / PageWidthPts;
        return new TextRect(l - pad, t - pad, r + pad, b + pad);
    }

    private sealed record Mark(string? Tag, TextRect Rect, int Subtype = 4)
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    private static PageModel ModelOf(IReadOnlyList<Mark> marks) =>
        DocumentModelBuilder.BuildPage(
            0,
            marks.Select((m, i) => new AnnotationSnapshot(
                i, m.Subtype, m.Rect.Left, m.Rect.Top, m.Rect.Right, m.Rect.Bottom,
                1.0, m.Id, m.Tag)).ToList(),
            PageWidthPts);

    private static List<AnnotationBox> BoxesOf(IReadOnlyList<Mark> marks) =>
        marks.Select((m, i) =>
            new AnnotationBox(i, m.Rect.Left, m.Rect.Top, m.Rect.Right, m.Rect.Bottom, m.Id))
            .ToList();

    /// <summary>True when the point sits close enough to some object's edge that
    /// the two pickers are expected to differ by design.</summary>
    private static bool NearAnyEdge(IReadOnlyList<Mark> marks, double x, double y)
    {
        foreach (var m in marks)
        {
            var r = m.Rect;
            bool nearX = Math.Abs(x - r.Left) <= Tol * 2 || Math.Abs(x - r.Right) <= Tol * 2;
            bool nearY = Math.Abs(y - r.Top) <= Tol * 2 || Math.Abs(y - r.Bottom) <= Tol * 2;
            bool insideX = x >= r.Left - (Tol * 2) && x <= r.Right + (Tol * 2);
            bool insideY = y >= r.Top - (Tol * 2) && y <= r.Bottom + (Tol * 2);
            if ((nearX && insideY) || (nearY && insideX)) { return true; }
        }
        return false;
    }

    /// <summary>
    /// Sweeps a grid and asserts both pickers name the same object everywhere
    /// that is not deliberately ambiguous. Returns how many points were compared,
    /// so a test cannot pass by skipping everything.
    /// </summary>
    private static int AssertPickersAgree(IReadOnlyList<Mark> marks)
    {
        var model = ModelOf(marks);
        var boxes = BoxesOf(marks);
        int compared = 0;

        for (int gx = 0; gx <= 60; gx++)
        {
            for (int gy = 0; gy <= 60; gy++)
            {
                double x = gx / 60.0;
                double y = gy / 60.0;
                if (NearAnyEdge(marks, x, y)) { continue; }

                Guid? oldPick = LoadedAnnotationPicker.PickTopmost(boxes, x, y)?.Id;
                Guid? newPick = ObjectHitTest.PickTopmost(model, x, y, Tol)?.Id;

                Assert.True(oldPick == newPick,
                    $"pickers disagreed at ({x:F4},{y:F4}): old={oldPick:N} new={newPick:N}");
                compared++;
            }
        }

        Assert.True(compared > 1000, $"only {compared} points were actually compared");
        return compared;
    }

    // ---------------- The cases that must not change ----------------

    [Fact]
    public void upright_rectangles_are_picked_identically()
    {
        AssertPickersAgree(
        [
            new("AyaanShape:0:FF0000FF:2.0000:1:1", Padded(0.10, 0.10, 0.50, 0.30)),
            new("AyaanShape:0:00FF00FF:2.0000:1:1", Padded(0.30, 0.20, 0.70, 0.45)),
            new("AyaanShape:0:0000FFFF:2.0000:1:1", Padded(0.55, 0.55, 0.90, 0.80)),
        ]);
    }

    [Fact]
    public void text_boxes_are_picked_identically()
    {
        string words = Convert.ToBase64String("hello"u8.ToArray());
        AssertPickersAgree(
        [
            new("AyaanText:24:FF0000FF:" + words, new TextRect(0.10, 0.10, 0.45, 0.20)),
            new("AyaanText:24:FF0000FF:" + words, new TextRect(0.30, 0.30, 0.80, 0.42)),
        ]);
    }

    [Fact]
    public void stamps_are_picked_identically()
    {
        AssertPickersAgree(
        [
            new("AyaanStamp:0.00:100.0000:100.0000:500.0000:300.0000",
                new TextRect(0.10, 0.10, 0.50, 0.30)),
            new("AyaanStamp:0.00:600.0000:400.0000:900.0000:700.0000",
                new TextRect(0.60, 0.40, 0.90, 0.70)),
        ]);
    }

    [Fact]
    public void annotations_this_app_did_not_write_are_picked_identically()
    {
        // A mark from Acrobat has nothing but a rectangle, and must stay exactly
        // as selectable as it was. Making a foreign annotation harder to pick
        // would make it unremovable.
        AssertPickersAgree(
        [
            new("Please review this", new TextRect(0.10, 0.10, 0.40, 0.25),
                Subtype: PdfAnnotationSubtype.Text),
            new(null, new TextRect(0.50, 0.50, 0.80, 0.75),
                Subtype: PdfAnnotationSubtype.Square),
        ]);
    }

    [Fact]
    public void a_stack_of_overlapping_marks_resolves_the_same_way()
    {
        // Where the two disagree about ORDER rather than about geometry, every
        // click on a pile of marks would select the wrong one.
        AssertPickersAgree(
        [
            new("AyaanShape:0:FF0000FF:2.0000:1:1", Padded(0.10, 0.10, 0.60, 0.60)),
            new("AyaanText:24:FF0000FF:" + Convert.ToBase64String("x"u8.ToArray()),
                new TextRect(0.20, 0.20, 0.50, 0.50)),
            new("AyaanStamp:0.00:300.0000:300.0000:400.0000:400.0000",
                new TextRect(0.30, 0.30, 0.40, 0.40)),
        ]);
    }

    // ---------------- Ordering ----------------

    [Fact]
    public void both_pickers_take_the_last_written_mark_when_marks_coincide()
    {
        // Identical rectangles: the only thing separating them is paint order,
        // so this pins that the new picker searches back to front like the old.
        var marks = new List<Mark>
        {
            new("AyaanShape:0:FF0000FF:2.0000:1:1", Padded(0.2, 0.2, 0.6, 0.6)),
            new("AyaanShape:0:00FF00FF:2.0000:1:1", Padded(0.2, 0.2, 0.6, 0.6)),
            new("AyaanShape:0:0000FFFF:2.0000:1:1", Padded(0.2, 0.2, 0.6, 0.6)),
        };

        Assert.Equal(marks[2].Id, ObjectHitTest.PickTopmost(ModelOf(marks), 0.4, 0.4, Tol)!.Id);
        Assert.Equal(marks[2].Id, LoadedAnnotationPicker.PickTopmost(BoxesOf(marks), 0.4, 0.4)!.Value.Id);
    }

    [Fact]
    public void the_models_z_order_is_the_annotation_index_the_old_picker_used()
    {
        // The migration maps the picked object back to an annotation by its
        // ZOrder. If those two ever stop being the same number, every write
        // after a click would aim at the wrong annotation, which is precisely
        // how identity was destroyed in v2.7.5.
        var marks = new List<Mark>
        {
            new("AyaanShape:0:FF0000FF:2.0000:1:1", Padded(0.10, 0.10, 0.20, 0.20)),
            new("AyaanShape:0:00FF00FF:2.0000:1:1", Padded(0.30, 0.30, 0.40, 0.40)),
            new("AyaanShape:0:0000FFFF:2.0000:1:1", Padded(0.50, 0.50, 0.60, 0.60)),
        };
        var model = ModelOf(marks);
        var boxes = BoxesOf(marks);

        foreach (var (x, y) in new[] { (0.15, 0.15), (0.35, 0.35), (0.55, 0.55) })
        {
            var byModel = ObjectHitTest.PickTopmost(model, x, y, Tol)!;
            var byBox = LoadedAnnotationPicker.PickTopmost(boxes, x, y)!.Value;

            Assert.Equal(byBox.Index, byModel.ZOrder);
            Assert.Equal(byBox.Id, byModel.Id);
        }
    }

    // ---------------- The differences, stated on purpose ----------------

    [Fact]
    public void the_new_picker_is_generous_by_the_tolerance_where_the_old_was_strict()
    {
        // An intended change: loaded marks become as easy to click as marks made
        // this session already are. Thin shapes stop needing precision aiming.
        var marks = new List<Mark>
        {
            new("AyaanStamp:0.00:100.0000:100.0000:500.0000:300.0000",
                new TextRect(0.1, 0.1, 0.5, 0.3)),
        };

        // Just outside the right edge, inside the tolerance.
        Assert.Null(LoadedAnnotationPicker.PickTopmost(BoxesOf(marks), 0.502, 0.2));
        Assert.NotNull(ObjectHitTest.PickTopmost(ModelOf(marks), 0.502, 0.2, Tol));
    }

    [Fact]
    public void a_click_in_a_groups_empty_middle_now_hits_nothing()
    {
        // Two diagonal arrows forming an X's outer arms, with nothing between
        // them. The old picker handed back an arrow for a click in the gap,
        // which is how clicking empty space inside a group's extent selected
        // the whole group. Nothing is hit now, so nothing expands.
        var marks = new List<Mark>
        {
            new("AyaanShape:3:FF0000FF:20.0000:1:1", Padded(0.10, 0.10, 0.45, 0.45, 20)),
            new("AyaanShape:3:00FF00FF:20.0000:1:1", Padded(0.55, 0.55, 0.90, 0.90, 20)),
        };

        // Inside the first arrow's /Rect, a long way from the arrow itself.
        Assert.NotNull(LoadedAnnotationPicker.PickTopmost(BoxesOf(marks), 0.40, 0.15));
        Assert.Null(ObjectHitTest.PickTopmost(ModelOf(marks), 0.40, 0.15, Tol));

        // The arrow itself still picks, so this is not just "nothing is hittable".
        Assert.NotNull(ObjectHitTest.PickTopmost(ModelOf(marks), 0.30, 0.30, Tol));
    }
}
