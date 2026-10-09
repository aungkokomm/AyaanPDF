using System;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.Foundation;

using PdfEditorApp.ViewModels;
using PdfEditorApp.Viewport;

namespace PdfEditorApp;

/// <summary>
/// Types a line through a hidden <see cref="TextBox"/>, for a PC where Windows
/// Text Services does not answer the page's own text document.
/// </summary>
/// <remarks>
/// ⚠️ THE READER'S HINDI PHONETIC KEYBOARD TYPED ENGLISH INTO THE PAGE. With
/// UAC off, <see cref="PageTextInput"/>'s document took focus and was never
/// spoken to, so the keyboard had nothing to compose into: its keys reached the
/// window as plain letters and its floating list of suggestions never opened.
/// In the Find box, an ordinary <c>TextBox</c>, the same keyboard typed कैसा.
/// So the line is typed in a box of that kind: invisible, sitting at the caret
/// so the suggestions open beside the word, and everything it does carried into
/// the line as one replaced range at a time. See <see cref="TextSink"/>.
///
/// ⚠️ THE LINE IS THE TRUTH AND THE BOX IS A COPY OF IT. Every key that is not
/// text goes to the page (<see cref="TextSink.PageKey"/>), and whatever the page
/// does to the line is copied back into the box. The box is only ever where
/// letters and compositions arrive.
///
/// ⚠️ NEVER FOR A BURMESE LINE. KeyMagic injects finished characters and its
/// corrections count on a backspace taking one character; it has always typed
/// through the character path and still does. See <see cref="TypingRoute"/>.
/// </remarks>
public sealed class PageTextSink
{
    private readonly ViewportViewModel _model;
    private readonly TextBox _box;
    private readonly Func<Rect?> _caretInHost;

    /// <summary>
    /// True while a change the box made is being applied to the line.
    /// </summary>
    /// <remarks>
    /// ⚠️ OR THE LINE IS COPIED BACK INTO THE BOX IN THE MIDDLE OF A WORD.
    /// Applying a change raises the view model's changed event, and answering
    /// that by setting the box's text would end the composition the keyboard
    /// is in the middle of.
    /// </remarks>
    private bool _applying;

    public PageTextSink(ViewportViewModel model, TextBox box, Func<Rect?> caretInHost)
    {
        _model = model;
        _box = box;
        _caretInHost = caretInHost;

        _box.TextChanged += OnTextChanged;
        _box.SelectionChanged += OnSelectionChanged;
    }

    /// <summary>Whether the line is being typed through the box.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Whether the box has the keyboard.</summary>
    public bool HasFocus => _box.FocusState != FocusState.Unfocused;

    private string Line => _model.InPlaceText ?? string.Empty;

    /// <summary>The reader has started editing a line: input goes to the box.</summary>
    public void Enter()
    {
        IsActive = true;
        CopyLine();
        Place();

        // ⚠️ A CONTROL THAT IS NOT A TAB STOP CANNOT BE FOCUSED AT ALL, and
        // one that always is would be found by Tab while nothing is being
        // edited, and typed into for nothing.
        _box.IsTabStop = true;
        _box.Focus(FocusState.Programmatic);
        Diag.Log($"text box: typing goes through it, focused={HasFocus}");
    }

    /// <summary>
    /// The line changed for a reason the box did not cause: a key the page
    /// acted on, a paste, a click that moved the caret.
    /// </summary>
    public void Changed()
    {
        if (!IsActive || _applying) { return; }

        CopyLine();
        Place();
        if (!HasFocus)
        {
            _box.Focus(FocusState.Programmatic);
        }
    }

    /// <summary>The edit is over.</summary>
    public void Leave()
    {
        if (!IsActive) { return; }

        IsActive = false;
        _box.IsTabStop = false;
        _box.Text = string.Empty;
    }

    private void CopyLine()
    {
        string line = Line;
        if (_box.Text != line)
        {
            _box.Text = line;
        }

        var (start, end) = _model.InPlaceSelection ?? (Caret(), Caret());
        start = Math.Clamp(start, 0, line.Length);
        end = Math.Clamp(end, start, line.Length);
        if (_box.SelectionStart != start || _box.SelectionLength != end - start)
        {
            _box.Select(start, end - start);
        }
    }

    private int Caret() => Math.Max(0, _model.InPlaceCaret);

    /// <summary>
    /// Sits the box on the caret, so a keyboard's suggestions open beside the
    /// word. Where the caret cannot be placed, the box stays where it was.
    /// </summary>
    private void Place()
    {
        if (_caretInHost() is not { } caret) { return; }

        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(_box, caret.X);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(_box, caret.Y);
        _box.Height = Math.Max(1, caret.Height);
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsActive || _applying || !_model.IsEditingInPlace) { return; }

        string before = Line;
        string after = _box.Text;
        if (after == before)
        {
            TakeSelection();
            return;
        }

        int caret = _model.InPlaceSelection?.Start ?? Caret();
        var (start, end, inserted) = TextSink.Edit(before, after, caret);

        _applying = true;
        try
        {
            _model.InPlaceReplaceRange(start, end, inserted);
            _model.InPlaceSetSelection(
                _box.SelectionStart, _box.SelectionStart + _box.SelectionLength);
        }
        finally
        {
            _applying = false;
        }
        Place();
    }

    private void OnSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsActive || _applying || !_model.IsEditingInPlace) { return; }

        // ⚠️ ONLY WHEN THE BOX AND THE LINE SAY THE SAME. A letter typed into
        // the box moves its caret before the text change is reported, and a
        // caret taken from the box then would point past the end of the line.
        if (_box.Text == Line)
        {
            TakeSelection();
        }
    }

    private void TakeSelection()
    {
        int start = _box.SelectionStart;
        int end = start + _box.SelectionLength;
        var now = _model.InPlaceSelection ?? (Caret(), Caret());
        if (now.Start == start && now.End == end) { return; }

        _applying = true;
        try
        {
            _model.InPlaceSetSelection(start, end);
        }
        finally
        {
            _applying = false;
        }
    }
}
