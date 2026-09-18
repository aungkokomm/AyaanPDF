using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace PdfEditorApp;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// The main application window. Use <c>App.Window</c> from any class that needs
    /// the window reference (for dialogs, pickers, interop, etc.).
    /// </summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>
    /// The UI thread dispatcher. Use <c>App.DispatcherQueue</c> to marshal calls
    /// to the UI thread. Fully qualified to avoid CS0104 ambiguity with
    /// <see cref="Windows.System.DispatcherQueue"/>.
    /// </summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    /// <summary>
    /// The native window handle (HWND). Use for file pickers,
    /// <c>DataTransferManager</c>, and any WinRT interop that requires
    /// <c>InitializeWithWindow</c>.
    /// </summary>
    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    /// <summary>Held for the life of the process: it is what makes this the one window.</summary>
    private static PdfEditorApp.Viewport.SingleInstance? _instance;

    private const int AnyProcess = -1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>Another launch's files, opened as tabs here. Called off the UI thread.</summary>
    private static void FilesFromAnotherLaunch(System.Collections.Generic.IReadOnlyList<string> files)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            Diag.Log($"launch: another launch sent {files.Count} file(s)");
            (Window as MainWindow)?.OpenLaunchedFiles(files);
        });
    }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => Crash.Record("unhandled", e.Exception);
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // Startup is caught separately, because a failure here happens BEFORE
        // there is a window to show anything in. Every startup crash so far has
        // been visible only as a Windows error dialog with a hex code in it,
        // which says nothing about which line failed.
        try
        {
            DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

            // The PDFs Explorer asked for, by double-click or Open with.
            var files = PdfEditorApp.Viewport.LaunchFiles.From(
                System.Environment.GetCommandLineArgs().Skip(1), System.Environment.CurrentDirectory);

            // One window: a launch while Ayaan PDF is already open hands its
            // files to that window and quits. If that window does not answer,
            // it is closing or stuck, and this launch carries on as the one.
            string name = PdfEditorApp.Viewport.SingleInstance.NameFor(AppInfo.Name, System.AppContext.BaseDirectory);
            _instance = PdfEditorApp.Viewport.SingleInstance.TryClaim(name, FilesFromAnotherLaunch);
            if (_instance is null)
            {
                // Lets the running window come to the front, which Windows
                // refuses to a process the user did not just start.
                AllowSetForegroundWindow(AnyProcess);
                if (PdfEditorApp.Viewport.SingleInstance.TrySend(name, files, System.TimeSpan.FromSeconds(5)))
                {
                    Diag.Log($"launch: handed {files.Count} file(s) to the running window");
                    System.Environment.Exit(0);
                    return;
                }
                Diag.Log("launch: the running window did not answer, opening a window of our own");
                _instance = PdfEditorApp.Viewport.SingleInstance.TryClaim(name, FilesFromAnotherLaunch);
            }

            Window = new MainWindow(files);
            Window.Activate();
        }
        catch (System.Exception ex)
        {
            Crash.Record("startup", ex);
            throw;
        }
    }
}
