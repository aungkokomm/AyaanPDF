using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Every Windows picker in the app goes through SafePickers, because one that
/// throws inside an async void click closes the app. File > Open did exactly
/// that on the reader's PC.
/// </summary>
public class SafePickersWiringTests
{
    private static DirectoryInfo AppFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PdfEditorApp", "SafePickers.cs")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return new DirectoryInfo(Path.Combine(dir!.FullName, "PdfEditorApp"));
    }

    [Fact]
    public void no_picker_is_awaited_outside_safe_pickers()
    {
        var pick = new Regex(@"\.(PickSingleFileAsync|PickSaveFileAsync|PickMultipleFilesAsync|PickSingleFolderAsync)\b");
        var sources = AppFolder()
            .EnumerateFiles("*.cs", SearchOption.AllDirectories)
            .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && f.Name != "SafePickers.cs")
            .ToList();

        int calls = 0;
        foreach (var file in sources)
        {
            foreach (string line in File.ReadLines(file.FullName))
            {
                if (!pick.IsMatch(line) || line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                calls++;
                Assert.True(line.Contains("SafePickers.PickAsync(", StringComparison.Ordinal),
                    $"{file.Name}: a picker called without SafePickers: {line.Trim()}");
            }
        }

        // All ten of today's pickers were found, so the scan is looking at the right files.
        Assert.True(calls >= 10, $"only {calls} picker calls found");
    }

    [Fact]
    public void a_failed_picker_is_logged_and_told_rather_than_thrown()
    {
        string code = File.ReadAllText(Path.Combine(AppFolder().FullName, "SafePickers.cs"));
        int body = code.IndexOf("public static async Task<T?> PickAsync<T>", StringComparison.Ordinal);
        Assert.True(body >= 0);

        int tryAt = code.IndexOf("return await pick();", body, StringComparison.Ordinal);
        int catchAt = code.IndexOf("catch (Exception ex)", body, StringComparison.Ordinal);
        int log = code.IndexOf("Diag.Log(", catchAt, StringComparison.Ordinal);
        int tell = code.IndexOf("tell(FailedMessage);", catchAt, StringComparison.Ordinal);
        int nothing = code.IndexOf("return null;", catchAt, StringComparison.Ordinal);
        Assert.True(body < tryAt && tryAt < catchAt && catchAt < log && log < tell && tell < nothing);
    }
}
