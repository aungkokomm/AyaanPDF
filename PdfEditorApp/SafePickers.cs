using System;
using System.Threading.Tasks;
using Windows.Foundation;

namespace PdfEditorApp;

/// <summary>
/// Runs a Windows file or folder picker so that a failure is an answer, not a
/// crash.
/// </summary>
/// <remarks>
/// ⚠️ THE PICKERS CAN THROW. On the reader's PC File > Open failed inside
/// PickSingleFileAsync with E_FAIL (0x80004005), and the exception, unhandled
/// in an async void click, closed the app with whatever was open in it. The
/// cause could not be reproduced: the same picker on the same file, and two
/// pickers at once, both worked here. So every picker goes through this, and a
/// failure becomes null, a line in the log and a message for the reader.
/// </remarks>
internal static class SafePickers
{
    public const string FailedMessage = "The file dialog didn't work this time. Please try again.";

    /// <summary>
    /// What the picker chose, or null when the reader cancelled or the picker
    /// failed; <paramref name="tell"/> hears about a failure, not a cancel.
    /// </summary>
    public static async Task<T?> PickAsync<T>(Func<IAsyncOperation<T>> pick, string what, Action<string> tell)
        where T : class
    {
        try
        {
            return await pick();
        }
        catch (Exception ex)
        {
            Diag.Log($"picker: {what} failed, {ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message.ReplaceLineEndings(" ")}");
            tell(FailedMessage);
            return null;
        }
    }
}
