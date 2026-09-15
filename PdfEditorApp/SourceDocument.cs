using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PdfEditorApp.Interop;

namespace PdfEditorApp;

/// <summary>Why a file could not be opened to take pages from.</summary>
internal enum SourceOpenFailure
{
    None,

    /// <summary>It needs a password and the prompt was cancelled.</summary>
    Cancelled,

    /// <summary>Not a PDF, damaged, or gone.</summary>
    Unreadable,
}

/// <summary>
/// A PDF opened only to take pages from: its thumbnails shown in the page
/// picker, its pages copied into a document. It never becomes a tab's
/// document, so none of opening's bookkeeping (the recent list, recovery,
/// annotations) applies, and its handle is closed when it is disposed.
/// </summary>
internal sealed class SourceDocument : IDisposable
{
    private SourceDocument(string path, ulong handle, int pageCount)
    {
        Path = path;
        Handle = handle;
        PageCount = pageCount;
    }

    public string Path { get; }

    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>The render_core handle, or zero once disposed.</summary>
    public ulong Handle { get; private set; }

    public int PageCount { get; }

    /// <summary>
    /// Opens a file without asking anything. Null on failure, with the render
    /// status saying whether it was a password (<see cref="RenderStatus.NeedsPassword"/>)
    /// or anything else. Safe off the UI thread.
    /// </summary>
    public static SourceDocument? TryOpen(string path, string? password, out int status)
    {
        var opened = RenderCoreNative.open_document_protected(path, password);
        status = opened.Status;
        if (opened.Handle == 0)
        {
            return null;
        }

        int pageCount = RenderCoreNative.get_page_count(opened.Handle);
        if (pageCount <= 0)
        {
            RenderCoreNative.close_document(opened.Handle);
            status = RenderStatus.InvalidInput;
            return null;
        }

        return new SourceDocument(path, opened.Handle, pageCount);
    }

    /// <summary>
    /// Opens a file off the UI thread, asking for its password on
    /// <paramref name="root"/> when it has one.
    /// </summary>
    public static async Task<(SourceDocument? Document, SourceOpenFailure Failure)> OpenAsync(string path, XamlRoot root)
    {
        var (document, status) = await Task.Run(() =>
        {
            var opened = TryOpen(path, null, out int s);
            return (opened, s);
        });

        if (document is not null)
        {
            return (document, SourceOpenFailure.None);
        }

        if (status != RenderStatus.NeedsPassword)
        {
            return (null, SourceOpenFailure.Unreadable);
        }

        var unlocked = await UnlockAsync(path, root);
        return unlocked is null ? (null, SourceOpenFailure.Cancelled) : (unlocked, SourceOpenFailure.None);
    }

    /// <summary>
    /// Asks for a protected file's password until it opens or the prompt is
    /// cancelled. Like opening a document, nothing typed is kept: the box is
    /// cleared after every try.
    /// </summary>
    public static async Task<SourceDocument?> UnlockAsync(string path, XamlRoot root)
    {
        var entry = new PasswordBox();
        AutomationProperties.SetName(entry, "Password");

        var error = new TextBlock
        {
            Text = "That password didn't open the file. Try again.",
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
        };

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Password needed",
            PrimaryButtonText = "Open",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"“{System.IO.Path.GetFileName(path)}” is protected. Enter its password to use its pages.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    entry,
                    error,
                },
            },
        };

        while (true)
        {
            ContentDialogResult answer;
            try
            {
                answer = await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                // Another dialog already open on this root. Treated as a cancel,
                // for the same reason as the document password prompt.
                Diag.Log($"source password prompt could not be shown: {ex.GetType().Name}");
                return null;
            }

            if (answer != ContentDialogResult.Primary)
            {
                return null;
            }

            string password = entry.Password;
            entry.Password = string.Empty;
            var (document, status) = await Task.Run(() =>
            {
                var opened = TryOpen(path, password, out int s);
                return (opened, s);
            });

            if (document is not null || status != RenderStatus.NeedsPassword)
            {
                return document;
            }

            error.Visibility = Visibility.Visible;
        }
    }

    public void Dispose()
    {
        if (Handle != 0)
        {
            RenderCoreNative.close_document(Handle);
            Handle = 0;
        }
    }
}
