using System;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

using Windows.Foundation;
using Windows.UI.Text.Core;

using PdfEditorApp.ViewModels;
using PdfEditorApp.Viewport;

namespace PdfEditorApp;

/// <summary>
/// Gives the page a TEXT DOCUMENT, so a composing input method can type into it.
/// </summary>
/// <remarks>
/// ⚠️ THIS IS WHY HINDI WOULD NOT TYPE INTO THE PAGE. The page is a `Grid`, and
/// a `Grid` is not something Windows Text Services can talk to: it has no text
/// to offer, no selection to move and nowhere to put a composition. So a
/// phonetic keyboard, which works by composing a word and revising it as the
/// reader types, had nothing to compose INTO. Measured against the running app:
/// the same keyboard types Devanagari into Ayaan's own Find box, which is an
/// ordinary `TextBox`, and produces nothing at all on the page.
///
/// ⚠️ AND THAT IS WHY KEYMAGIC ALWAYS WORKED. The reader's Burmese keyboard
/// does not compose; it INJECTS finished characters, which arrive as ordinary
/// `CharacterReceived` events and always did. Burmese typing being fine was
/// never evidence that the page could host an input method.
///
/// ⚠️ `CharacterReceived` MUST STAND DOWN WHILE THIS IS ACTIVE. Text Services
/// delivers what was typed through <see cref="CoreTextEditContext.TextUpdating"/>,
/// and the same keystroke still reaches the window as a character. Letting both
/// through types everything twice.
///
/// ⚠️ AND IT DEGRADES TO EXACTLY WHAT WAS THERE BEFORE. If the manager cannot
/// be had, this stays inactive, `CharacterReceived` keeps doing the work, and
/// the reader is no worse off than before this existed.
/// </remarks>
public sealed class PageTextInput : IDisposable
{
    private readonly ViewportViewModel _model;
    private readonly UIElement _host;
    private readonly Func<Rect?> _caretOnScreen;
    private readonly CoreTextEditContext? _context;

    /// <summary>
    /// How long Text Services last believed the line to be. Its ranges are
    /// offsets into THAT, so a change made anywhere else has to be reported
    /// against it before the next one can be understood.
    /// </summary>
    private int _known;

    /// <summary>
    /// True while a Text Services update is being applied to the buffer.
    /// </summary>
    /// <remarks>
    /// ⚠️ OR THE CHANGE IS REPORTED BACK TO WHOEVER MADE IT. Applying an update
    /// raises the view model's changed event, and answering that by telling
    /// Text Services the text changed would describe a change it is in the
    /// middle of making. That is how a composition gets torn in half.
    /// </remarks>
    private bool _applying;

    public PageTextInput(ViewportViewModel model, UIElement host, Func<Rect?> caretOnScreen)
    {
        _model = model;
        _host = host;
        _caretOnScreen = caretOnScreen;

        if (SettingsStore.Current.TextServicesSilent)
        {
            s_watch.KnownSilent();
        }

        try
        {
            var manager = CoreTextServicesManager.GetForCurrentView();
            _context = manager.CreateEditContext();
        }
        catch (Exception ex)
        {
            // Nothing is broken by this: the character path still works.
            Diag.Log($"text services unavailable, typing stays on CharacterReceived: {ex.Message}");
            return;
        }

        _context.InputPaneDisplayPolicy = CoreTextInputPaneDisplayPolicy.Manual;
        _context.InputScope = CoreTextInputScope.Text;

        _context.TextRequested += OnTextRequested;
        _context.SelectionRequested += OnSelectionRequested;
        _context.TextUpdating += OnTextUpdating;
        _context.SelectionUpdating += OnSelectionUpdating;
        _context.LayoutRequested += OnLayoutRequested;
        _context.FocusRemoved += OnFocusRemoved;
    }

    /// <summary>
    /// Whether an input method is being hosted, and so whether the character
    /// path must stand down.
    /// </summary>
    public bool IsActive { get; private set; }

    /// <summary>
    /// Whether Text Services is delivering at all. Shared by every tab, because
    /// what it decides is about this run of the app, not about one document.
    /// </summary>
    private static readonly TypingWatch s_watch = new();

    /// <summary>
    /// Whether the page's text document cannot host input on this PC, so a
    /// line must be typed through <see cref="PageTextSink"/> instead.
    /// </summary>
    public bool IsSilent => _context is null || s_watch.Silent;

    /// <summary>The line, as Text Services is entitled to see it.</summary>
    private string Line => _model.InPlaceText ?? string.Empty;

    private CoreTextRange Selected()
    {
        if (_model.InPlaceSelection is { } s)
        {
            return new CoreTextRange { StartCaretPosition = s.Start, EndCaretPosition = s.End };
        }
        int caret = Math.Max(0, _model.InPlaceCaret);
        return new CoreTextRange { StartCaretPosition = caret, EndCaretPosition = caret };
    }

    // ---------------- what the app tells Text Services ----------------

    /// <summary>The reader has started editing a line: input belongs here now.</summary>
    public void Enter()
    {
        if (_context is null || IsActive || s_watch.Silent) { return; }

        s_watch.EditStarted();
        IsActive = true;
        _known = Line.Length;
        _context.NotifyFocusEnter();
        _context.NotifyLayoutChanged();
    }

    /// <summary>
    /// A character that reached the window while this was active. Null leaves
    /// it to Text Services. Text means Text Services has stayed silent: it is
    /// let go, on this PC for good, and the caller inserts the text, which
    /// carries the characters held while that was being decided. The line is
    /// then typed through <see cref="PageTextSink"/>.
    /// </summary>
    public string? Unclaimed(char c)
    {
        if (!IsActive) { return null; }

        string? held = s_watch.Received(c);
        if (held is not null)
        {
            Diag.Log($"text services: silent for {held.Length} characters, so the line is typed through a text box from now on");
            SettingsStore.Update(s => s with { TextServicesSilent = true });
            Leave();
        }
        return held;
    }

    /// <summary>The edit is over.</summary>
    public void Leave()
    {
        if (_context is null || !IsActive) { return; }

        IsActive = false;
        _context.NotifyFocusLeave();
    }

    /// <summary>
    /// The line changed for a reason Text Services did not cause: a paste, a
    /// backspace, an arrow key.
    /// </summary>
    public void Changed()
    {
        if (_context is null || !IsActive || _applying) { return; }

        // ⚠️ THE WHOLE LINE, NOT THE PART THAT REALLY CHANGED. Naming a smaller
        // range would be a claim about which characters moved, and the buffer
        // does not report that. A line is short enough that saying "all of it"
        // costs nothing and cannot be wrong.
        string now = Line;
        _context.NotifyTextChanged(
            new CoreTextRange { StartCaretPosition = 0, EndCaretPosition = _known },
            now.Length,
            Selected());
        _known = now.Length;
        _context.NotifyLayoutChanged();
    }

    // ---------------- what Text Services asks of the app ----------------

    private void OnTextRequested(CoreTextEditContext sender, CoreTextTextRequestedEventArgs args)
    {
        var request = args.Request;
        string line = Line;

        // ⚠️ CLAMPED, BECAUSE THE RANGE IS THEIRS AND THE TEXT IS OURS. A
        // request made just before a paste shortened the line names offsets that
        // no longer exist, and the range itself cannot be narrowed in the reply:
        // it is read only, so all that can be done is to not read off the end.
        int start = Math.Clamp(request.Range.StartCaretPosition, 0, line.Length);
        int end = Math.Clamp(request.Range.EndCaretPosition, start, line.Length);

        request.Text = line[start..end];
    }

    private void OnSelectionRequested(
        CoreTextEditContext sender, CoreTextSelectionRequestedEventArgs args)
    {
        args.Request.Selection = Selected();
    }

    private void OnTextUpdating(CoreTextEditContext sender, CoreTextTextUpdatingEventArgs args)
    {
        if (!_model.IsEditingInPlace)
        {
            args.Result = CoreTextTextUpdatingResult.Failed;
            return;
        }

        s_watch.Delivered();
        _applying = true;
        try
        {
            _model.InPlaceReplaceRange(
                args.Range.StartCaretPosition, args.Range.EndCaretPosition, args.Text ?? string.Empty);

            // ⚠️ AND THE CARET GOES WHERE THEY SAY, NOT AFTER THE TEXT. While a
            // word is being composed the caret is often INSIDE the range that
            // was just written, and putting it at the end instead makes the
            // next keystroke revise the wrong part of the word.
            _model.InPlaceSetSelection(
                args.NewSelection.StartCaretPosition, args.NewSelection.EndCaretPosition);

            _known = Line.Length;
        }
        finally
        {
            _applying = false;
        }

        _context?.NotifyLayoutChanged();
    }

    private void OnSelectionUpdating(
        CoreTextEditContext sender, CoreTextSelectionUpdatingEventArgs args)
    {
        if (!_model.IsEditingInPlace)
        {
            args.Result = CoreTextSelectionUpdatingResult.Failed;
            return;
        }

        _applying = true;
        try
        {
            _model.InPlaceSetSelection(
                args.Selection.StartCaretPosition, args.Selection.EndCaretPosition);
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>
    /// Where the candidate window should appear.
    /// </summary>
    /// <remarks>
    /// ⚠️ IN SCREEN PIXELS, WHICH THE PAGE IS NOT. Everything the editor draws
    /// is in the window's own device-independent pixels, and a candidate list is
    /// placed by the shell against the whole desktop. Getting this wrong puts
    /// the suggestion list in the corner of the screen, which is ugly but does
    /// not stop a word being typed, so it never refuses.
    /// </remarks>
    private void OnLayoutRequested(
        CoreTextEditContext sender, CoreTextLayoutRequestedEventArgs args)
    {
        if (_caretOnScreen() is not { } caret) { return; }

        args.Request.LayoutBounds.TextBounds = caret;
        args.Request.LayoutBounds.ControlBounds = caret;
    }

    private void OnFocusRemoved(CoreTextEditContext sender, object args)
    {
        // ⚠️ NOT AN END TO THE EDIT. Text Services has taken input away, so this
        // stops hosting it, and the character path picks the typing back up.
        // Cancelling the reader's half-typed line here would throw their work
        // away over something they never did.
        IsActive = false;
    }

    public void Dispose()
    {
        if (_context is null) { return; }

        Leave();
        _context.TextRequested -= OnTextRequested;
        _context.SelectionRequested -= OnSelectionRequested;
        _context.TextUpdating -= OnTextUpdating;
        _context.SelectionUpdating -= OnSelectionUpdating;
        _context.LayoutRequested -= OnLayoutRequested;
        _context.FocusRemoved -= OnFocusRemoved;
    }

    /// <summary>
    /// Turns a rectangle drawn on <paramref name="element"/> into the screen
    /// pixels a candidate window is placed in.
    /// </summary>
    public static Rect? OnScreen(UIElement element, Rect local)
    {
        try
        {
            GeneralTransform toWindow = element.TransformToVisual(null);
            Rect inWindow = toWindow.TransformBounds(local);

            double scale = element.XamlRoot?.RasterizationScale ?? 1.0;
            var origin = App.Window?.AppWindow?.Position;
            double left = (origin?.X ?? 0) + (inWindow.X * scale);
            double top = (origin?.Y ?? 0) + (inWindow.Y * scale);

            return new Rect(left, top, inWindow.Width * scale, inWindow.Height * scale);
        }
        catch (Exception ex)
        {
            Diag.Log($"caret has no screen position: {ex.Message}");
            return null;
        }
    }
}
