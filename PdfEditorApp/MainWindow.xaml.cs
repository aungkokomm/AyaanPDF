using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace PdfEditorApp;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    // Set once the user has confirmed closing with unsaved changes, so the
    // re-issued Close() sails through instead of prompting again.
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        // Only the empty remainder, NOT the whole bar: whatever is handed to
        // SetTitleBar becomes the drag region, and its children stop receiving
        // clicks. Passing AppTitleBar here would leave the quick actions
        // looking like buttons and behaving like a title bar.
        SetTitleBar(TitleDragArea);

        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Closing += OnClosing;

        // Open maximized. A document editor is used full screen, and the
        // default restored size is small enough that the first thing anyone did
        // was resize it. Guarded on the presenter type because a window can be
        // full-screen or compact-overlay, and neither of those can be maximized.
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }

        AddDocumentTab(null);
    }

    /// <summary>The document the user is looking at, or null before the first tab exists.</summary>
    public MainPage? ActivePage => (Tabs.SelectedItem as TabViewItem)?.Content as MainPage;

    /// <summary>
    /// Ctrl+Q quits.
    ///
    /// Goes through Close(), so the unsaved-changes prompt in OnClosing is the
    /// same one every other route to quitting gets. Nothing here decides
    /// whether it is safe to leave.
    /// </summary>
    private void Quit_Invoked(
        Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Close();
    }

    /// <summary>
    /// Opens a document in a new tab.
    ///
    /// A null path means no document: the tab shows the empty state, which is
    /// where launching and the + button both land. <paramref name="startBlank"/>
    /// is the one exception, for File > New, which is an explicit request for a
    /// document to draw on.
    ///
    /// The page is created here rather than navigated to, because a Page hosted
    /// in a Frame gets recycled by navigation and each tab needs its own live
    /// instance for as long as its tab exists.
    /// </summary>
    public MainPage AddDocumentTab(string? path, bool startBlank = false)
    {
        var page = new MainPage { InitialDocumentPath = path, StartBlank = startBlank };
        var item = new TabViewItem
        {
            Content = page,
            Header = System.IO.Path.GetFileName(path) ?? "Untitled",
            IconSource = new SymbolIconSource { Symbol = Symbol.Document },
        };

        // The page tells us its title; we decide where it belongs. A background
        // document must be able to retitle its own tab without touching the
        // window.
        page.DocumentTitleChanged += p =>
        {
            item.Header = p.DocumentTitle;
            if (ReferenceEquals(ActivePage, p))
            {
                SetDocumentTitle(p.DocumentTitle);
            }
        };

        // Only the front document drives the buttons; a background tab
        // finishing an edit must not enable Undo for the one on screen.
        page.CommandStateChanged += p =>
        {
            if (ReferenceEquals(ActivePage, p))
            {
                RefreshQuickActions();
            }
        };

        Tabs.TabItems.Add(item);
        Tabs.SelectedItem = item;
        return page;
    }

    /// <summary>Closes a document's tab, offering to save it first.</summary>
    public async Task CloseDocumentTab(MainPage page)
    {
        var item = Tabs.TabItems
            .OfType<TabViewItem>()
            .FirstOrDefault(t => ReferenceEquals(t.Content, page));
        if (item is not null)
        {
            await CloseTab(item);
        }
    }

    private async Task CloseTab(TabViewItem item)
    {
        if (item.Content is MainPage page && !await page.ConfirmCloseAsync())
        {
            return;
        }

        Tabs.TabItems.Remove(item);

        // Never leave an empty window: closing the last document lands on the
        // empty state, the same place the app starts, rather than a grey void
        // with a menu bar over it.
        if (Tabs.TabItems.Count == 0)
        {
            AddDocumentTab(null);
        }
    }

    private void Tabs_AddTabButtonClick(TabView sender, object args) => AddDocumentTab(null);

    private async void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab is TabViewItem item)
        {
            await CloseTab(item);
        }
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The window's title follows whichever document is in front.
        if (ActivePage is { } page)
        {
            SetDocumentTitle(page.DocumentTitle);
        }
        RefreshQuickActions();
    }

    /// <summary>
    /// Shows which file is open, and whether it has unsaved work.
    ///
    /// Both the strip in the extended title bar and the real Window.Title, so
    /// the taskbar and Alt+Tab say it too. Before this the window said only
    /// "Ayaan PDF" and there was no way to tell which of two open documents
    /// you were looking at, or that either had unsaved edits.
    /// </summary>
    /// <summary>
    /// The document's name goes to the WINDOW, which is what the taskbar and
    /// Alt+Tab read. The strip itself keeps saying "Ayaan PDF": the tabs below
    /// already name every open file, and repeating the active one above them
    /// says nothing new.
    /// </summary>
    public void SetDocumentTitle(string title) => Title = title;

    /// <summary>
    /// Tints the title strip and the tab row.
    ///
    /// The window owns these, and the theme is chosen inside a page, so the
    /// page calls in. Without it the invented themes coloured the document
    /// area and left the whole top of the app in Fluent's default grey, which
    /// is what made them look half-applied.
    /// </summary>
    public void ApplyThemeChrome(PdfEditorApp.Viewport.AppTheme theme)
    {
        var brush = Theming.WindowBrush(theme);

        // The strip behind the tabs is painted on the ROOT, not on the TabView:
        // TabView.Background does not reach its tab strip, which is why the
        // tabs stayed grey in a blue window. Everything above it is left
        // transparent so this one brush shows through the lot.
        Shell.Background = brush;
        AppTitleBar.Background = new SolidColorBrush(Colors.Transparent);
        Tabs.Background = new SolidColorBrush(Colors.Transparent);

        // The tab in front is a raised surface on the strip, so it takes the
        // CHROME colour, the same one the tool rail and the rulers use. The
        // ones behind stay transparent and let the strip through.
        //
        // RECOLOURED, not replaced. Putting a new brush in the dictionary left
        // the tab showing the previous theme's colour, because the tab is
        // holding the brush object its template resolved at expansion time and
        // never looks the key up again.
        if (Tabs.Resources["TabViewItemHeaderBackgroundSelected"] is SolidColorBrush selected)
        {
            selected.Color = Theming.TabColor(theme);
        }
    }

    // ---------------- Quick actions ----------------

    private void QuickOpen_Click(object sender, RoutedEventArgs e) => ActivePage?.RunOpen();

    private void QuickSave_Click(object sender, RoutedEventArgs e) => ActivePage?.RunSave();

    private void QuickUndo_Click(object sender, RoutedEventArgs e) => ActivePage?.RunUndo();

    private void QuickRedo_Click(object sender, RoutedEventArgs e) => ActivePage?.RunRedo();

    /// <summary>
    /// Greys the quick actions for the document in FRONT.
    ///
    /// Called on tab switch as well as on the active page's own changes,
    /// because switching tabs changes what the buttons act on without anything
    /// about either document having changed.
    /// </summary>
    private void RefreshQuickActions()
    {
        var page = ActivePage;
        QuickSave.IsEnabled = page?.CanSave ?? false;
        QuickUndo.IsEnabled = page?.CanUndo ?? false;
        QuickRedo.IsEnabled = page?.CanRedo ?? false;
    }

    /// <summary>
    /// Intercepts window close to offer saving unsaved edits. A Closing
    /// handler cannot await, so it cancels the close, awaits the prompt, and
    /// re-issues Close() only if the user chose to proceed.
    /// </summary>
    private async void OnClosing(
        Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed)
        {
            return;
        }

        args.Cancel = true;

        // EVERY tab, not just the one in front. Asking only about the visible
        // document is how closing the window quietly threw away edits in the
        // ones behind it. Selecting each tab first means the prompt appears
        // over the document it is asking about, rather than over an unrelated
        // page.
        foreach (var item in Tabs.TabItems.OfType<TabViewItem>().ToList())
        {
            if (item.Content is not MainPage page)
            {
                continue;
            }

            Tabs.SelectedItem = item;
            if (!await page.ConfirmCloseAsync())
            {
                return;
            }
        }

        _closeConfirmed = true;
        Close();
    }
}
