namespace PdfEditorApp.Viewport;

/// <summary>Which of the floating bar's optional groups there is room for.</summary>
public readonly record struct BarGroups(bool ViewModes, bool NavHistory)
{
    public static readonly BarGroups All = new(true, true);
}

/// <summary>
/// What the floating bar drops when the canvas is too narrow for it.
///
/// The bar sits in the canvas column, not the window, so its room is what is
/// left after the thumbnail panel and the property panel have taken theirs. On
/// a laptop with both open and find expanded, the bar is wider than the space
/// it has, and a horizontal StackPanel does not deal with that: it is simply
/// clipped, taking the drag grip off one end and the find box off the other.
///
/// Groups go in a fixed order rather than by whichever combination fits best.
/// A control that moves between two places depending on the window width is
/// harder to use than one that is either there or not, and the order is a
/// statement about what is worth keeping, not an optimisation.
/// </summary>
public static class StatusBarOverflow
{
    /// <summary>
    /// The groups that fit in <paramref name="available"/> DIPs.
    ///
    /// The view toggles go first, because the "..." button sits immediately
    /// beside them and carries every one of them as a named menu item, so
    /// dropping them costs a click and nothing else. Back and forward go last
    /// for the same reason they earned a place at all: a chord cannot be
    /// discovered.
    ///
    /// Every width is the group's NATURAL width, measured while it was
    /// visible, never what it currently occupies. That is what stops the
    /// decision from oscillating: hiding a group must not change the answer to
    /// the question that hid it.
    /// </summary>
    public static BarGroups Decide(double available, double natural, double viewModes, double navHistory)
    {
        // Nothing measured yet. Showing everything is the honest answer: it is
        // what the bar looks like before it has been laid out once.
        if (available <= 0 || natural <= 0)
        {
            return BarGroups.All;
        }

        if (natural <= available)
        {
            return BarGroups.All;
        }

        if (natural - viewModes <= available)
        {
            return new BarGroups(ViewModes: false, NavHistory: true);
        }

        return new BarGroups(ViewModes: false, NavHistory: false);
    }
}
