using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PdfEditorApp.Ocr;
using PdfEditorApp.Viewport;

namespace PdfEditorApp;

/// <summary>
/// The Recognize text languages: the three that ship, the ones downloaded,
/// the ones worth suggesting, and every other one Tesseract offers, each with
/// what can be done to it now.
/// </summary>
/// <remarks>
/// The lists are rebuilt whenever <see cref="OcrLanguageStore"/> says a
/// language changed, which is rare (a queue, a start, an end); a download's
/// progress only moves its own row's bar.
/// </remarks>
public sealed partial class OcrLanguagesWindow : Window
{
    private static OcrLanguagesWindow? _open;

    /// <summary>The shipped three, with their own names, for the Installed list and for search.</summary>
    private static readonly (string Code, string Name, string Native)[] Shipped =
    [
        ("eng", "English", string.Empty),
        ("hin", "Hindi", "हिन्दी"),
        ("mya", "Myanmar", "မြန်မာ"),
    ];

    private readonly Dictionary<string, (ProgressBar Bar, TextBlock Text, long Size)> _progressRows = new();
    private int _documentScript;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>Opens the window, or brings forward the one already open.</summary>
    /// <param name="documentScript">
    /// The script the open document's text is in (dominant_text_script), to
    /// suggest its languages; null when there is no document to ask.
    /// </param>
    public static void Open(Task<int>? documentScript)
    {
        if (_open is not null)
        {
            _open.Activate();
            return;
        }
        var window = new OcrLanguagesWindow(documentScript);
        window.Closed += (_, _) => _open = null;
        _open = window;
        window.Activate();
    }

    private OcrLanguagesWindow(Task<int>? documentScript)
    {
        InitializeComponent();
        AppWindow.SetIcon("Assets/AppIcon.ico");

        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        int width = Math.Min((int)(560 * scale), work.Width);
        int height = Math.Min((int)(700 * scale), (int)(work.Height * 0.9));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));

        Theming.Apply(this, SettingsStore.Current.Theme);

        OcrLanguageStore.Changed += Rebuild;
        OcrLanguageStore.Progress += ShowProgress;
        Closed += (_, _) =>
        {
            OcrLanguageStore.Changed -= Rebuild;
            OcrLanguageStore.Progress -= ShowProgress;
        };

        Rebuild();
        if (documentScript is not null)
        {
            _ = SuggestForDocumentAsync(documentScript);
        }
    }

    private async Task SuggestForDocumentAsync(Task<int> documentScript)
    {
        _documentScript = await documentScript;
        // The window may have been closed while the document was asked.
        if (_documentScript != 0 && _open == this)
        {
            Rebuild();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Rebuild();

    // ---------------- The lists ----------------

    private void Rebuild()
    {
        Lists.Children.Clear();
        _progressRows.Clear();

        string query = SearchBox.Text.Trim();
        bool searching = query.Length > 0;
        var found = OcrLanguageCatalog.Search(query).ToList();
        var foundCodes = found.Select(l => l.Code).ToHashSet();

        // Installed: the shipped three, then what has been downloaded.
        var installed = new List<UIElement>();
        foreach (var (code, name, native) in Shipped)
        {
            if (!searching || Matches(name, native, code, query))
            {
                installed.Add(Row(name, native, Note("Included")));
            }
        }
        foreach (var language in OcrLanguageStore.Installed.Where(l => foundCodes.Contains(l.Code)))
        {
            installed.Add(LanguageRow(language));
        }
        AddSection("Installed", installed);

        // Suggested: Windows' own languages and the document's script. Only
        // while not searching, where a search result would repeat them.
        var suggested = searching
            ? []
            : OcrLanguageCatalog.Suggested(WindowsLanguages(), _documentScript, OcrLanguageStore.IsInstalled);
        AddSection("Suggested", suggested.Select(LanguageRow).ToList());

        var suggestedCodes = suggested.Select(l => l.Code).ToHashSet();
        var available = found
            .Where(l => OcrLanguageStore.StateOf(l.Code) != OcrLanguageStore.State.Installed && !suggestedCodes.Contains(l.Code))
            .Select(LanguageRow)
            .ToList();
        AddSection("Available", available);

        if (searching && installed.Count == 0 && available.Count == 0)
        {
            Lists.Children.Add(Secondary($"No language matches \"{query}\"."));
        }

        var downloaded = OcrLanguageStore.Installed;
        UsageText.Text = downloaded.Count == 0
            ? "No languages downloaded yet."
            : $"{downloaded.Count} downloaded, using {OcrLanguageCatalog.SizeText(downloaded.Sum(l => l.Size))}. Kept in your user folder, so updating Ayaan PDF keeps them.";
    }

    /// <summary>A downloadable language's row, with whatever can be done to it now.</summary>
    private UIElement LanguageRow(OcrLanguage language)
    {
        string size = OcrLanguageCatalog.SizeText(language.Size);
        switch (OcrLanguageStore.StateOf(language.Code))
        {
            case OcrLanguageStore.State.Installed:
            {
                var actions = Actions(Note(size), Action("Remove", () => OcrLanguageStore.Remove(language.Code)));
                return Row(language.Name, language.Native, actions, Problem(language.Code));
            }

            case OcrLanguageStore.State.Waiting:
                return Row(language.Name, language.Native,
                    Actions(Note("Waiting"), Action("Cancel", () => OcrLanguageStore.Cancel(language.Code))));

            case OcrLanguageStore.State.Downloading:
            {
                long done = OcrLanguageStore.DoneOf(language.Code);
                var bar = new ProgressBar { Maximum = language.Size, Value = done, VerticalAlignment = VerticalAlignment.Center };
                var text = Secondary(ProgressText(done, language.Size));
                text.TextWrapping = TextWrapping.NoWrap;
                var progress = new Grid
                {
                    ColumnSpacing = 8,
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                        new ColumnDefinition { Width = GridLength.Auto },
                    },
                };
                Grid.SetColumn(text, 1);
                progress.Children.Add(bar);
                progress.Children.Add(text);
                _progressRows[language.Code] = (bar, text, language.Size);
                return Row(language.Name, language.Native,
                    Action("Cancel", () => OcrLanguageStore.Cancel(language.Code)), progress);
            }

            default:
            {
                // Without a font for its script the words it reads could not
                // be searched, which is the whole point of reading them.
                if (!OcrAssets.HasFontFor(language.Script))
                {
                    return Row(language.Name, language.Native, Note("Needs a font this PC doesn't have"));
                }
                string? problem = OcrLanguageStore.ProblemOf(language.Code);
                var download = Action(problem is null ? "Download" : "Try again", () => OcrLanguageStore.Download(language));
                if (problem is null)
                {
                    download.Content = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        Children = { new FontIcon { Glyph = "\uE896", FontSize = 14 }, new TextBlock { Text = "Download" } },
                    };
                }
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(download, $"Download {language.Name}, {size}");
                return Row(language.Name, language.Native, Actions(Note(size), download), Problem(language.Code));
            }
        }
    }

    private void ShowProgress(string code, long done)
    {
        if (_progressRows.TryGetValue(code, out var row))
        {
            row.Bar.Value = done;
            row.Text.Text = ProgressText(done, row.Size);
        }
    }

    private static string ProgressText(long done, long size) =>
        $"{OcrLanguageCatalog.SizeText(done)} of {OcrLanguageCatalog.SizeText(size)}";

    // ---------------- Building blocks ----------------

    private void AddSection(string title, IReadOnlyList<UIElement> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        Lists.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            Margin = new Thickness(0, Lists.Children.Count == 0 ? 0 : 12, 0, 0),
        });

        var list = new StackPanel();
        for (int i = 0; i < rows.Count; i++)
        {
            list.Children.Add(new Border
            {
                Child = rows[i],
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(0, 0, 0, i == rows.Count - 1 ? 0 : 1),
            });
        }
        Lists.Children.Add(new Border
        {
            Child = list,
            CornerRadius = (CornerRadius)Application.Current.Resources["ControlCornerRadius"],
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        });
    }

    /// <summary>A language's name and its own name, what can be done on the right, and anything more under the name.</summary>
    private static Grid Row(string name, string native, UIElement right, UIElement? below = null)
    {
        var title = new TextBlock { TextWrapping = TextWrapping.Wrap };
        title.Inlines.Add(new Run { Text = name });
        if (native.Length > 0)
        {
            title.Inlines.Add(new Run
            {
                Text = "  " + native,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        }

        var left = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(title);
        if (below is not null)
        {
            left.Children.Add(below);
        }

        var row = new Grid
        {
            Padding = new Thickness(12, 8, 12, 8),
            ColumnSpacing = 12,
            MinHeight = 44,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        if (right is FrameworkElement element)
        {
            element.VerticalAlignment = VerticalAlignment.Center;
        }
        Grid.SetColumn((FrameworkElement)right, 1);
        row.Children.Add(left);
        row.Children.Add(right);
        return row;
    }

    private static StackPanel Actions(params UIElement[] parts)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var part in parts)
        {
            if (part is FrameworkElement element)
            {
                element.VerticalAlignment = VerticalAlignment.Center;
            }
            panel.Children.Add(part);
        }
        return panel;
    }

    private static Button Action(string text, System.Action act)
    {
        var button = new Button { Content = text };
        button.Click += (_, _) => act();
        return button;
    }

    private static TextBlock Note(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    private static TextBlock Secondary(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    /// <summary>Why the last try failed, in the critical colour, or nothing.</summary>
    private static TextBlock? Problem(string code) => OcrLanguageStore.ProblemOf(code) is { } problem
        ? new TextBlock
        {
            Text = problem,
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
        }
        : null;

    private static bool Matches(string name, string native, string code, string query) =>
        name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || native.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || code.StartsWith(query, StringComparison.OrdinalIgnoreCase);

    /// <summary>The languages Windows is set up for, best first. Nothing if Windows won't say.</summary>
    private static IReadOnlyList<string> WindowsLanguages()
    {
        try
        {
            return Windows.System.UserProfile.GlobalizationPreferences.Languages.ToList();
        }
        catch (Exception ex)
        {
            Diag.Log($"ocr languages: Windows languages unavailable: {ex.Message}");
            return [];
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && e.OriginalSource is not TextBox)
        {
            e.Handled = true;
            Close();
        }
    }
}
