using Microsoft.UI.Xaml;

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
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Closing += OnClosing;

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
    }

    /// <summary>
    /// Shows which file is open, and whether it has unsaved work.
    ///
    /// Both the strip in the extended title bar and the real Window.Title, so
    /// the taskbar and Alt+Tab say it too. Before this the window said only
    /// "Ayaan PDF" and there was no way to tell which of two open documents
    /// you were looking at, or that either had unsaved edits.
    /// </summary>
    public void SetDocumentTitle(string title)
    {
        TitleText.Text = title;
        Title = title;
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
        if (_closeConfirmed || RootFrame.Content is not MainPage page)
        {
            return;
        }

        args.Cancel = true;

        if (await page.ConfirmCloseAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }
}
