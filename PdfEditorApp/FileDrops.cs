using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace PdfEditorApp;

/// <summary>
/// The classic Windows file drop, WM_DROPFILES, as a second way in for files
/// dragged from Explorer.
/// </summary>
/// <remarks>
/// ⚠️ ON THE READER'S PC THE WINUI DROP NEVER FIRED. UAC is off there, so the
/// app runs elevated, and not one drop reached the page's Drop handler, not
/// even on the welcome screen that has always taken them. This older route
/// does not depend on that machinery. Where WinUI's own drop works it claims
/// the drag first and WM_DROPFILES never arrives, so a file is never opened
/// twice.
/// </remarks>
internal static class FileDrops
{
    private const uint WM_DROPFILES = 0x0233;
    private const uint WM_COPYDATA = 0x004A;
    private const uint WM_COPYGLOBALDATA = 0x0049;
    private const uint MSGFLT_ALLOW = 1;

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);

    [DllImport("shell32.dll")] private static extern void DragAcceptFiles(IntPtr hwnd, bool accept);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder? file, uint size);
    [DllImport("shell32.dll")] private static extern void DragFinish(IntPtr drop);
    [DllImport("user32.dll")] private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint msg, uint action, IntPtr info);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr lParam);

    /// <summary>Held for the life of the app: Windows keeps calling it.</summary>
    private static readonly SubclassProc s_proc = OnMessage;

    private static Action<IReadOnlyList<string>>? s_open;

    /// <summary>
    /// Takes file drops on the window and the windows WinUI made inside it,
    /// and hands the PDFs among them to <paramref name="open"/>.
    /// </summary>
    public static void Accept(IntPtr window, Action<IReadOnlyList<string>> open)
    {
        s_open = open;

        var targets = new List<IntPtr> { window };
        EnumChildWindows(window, (child, _) => { targets.Add(child); return true; }, IntPtr.Zero);

        foreach (IntPtr hwnd in targets)
        {
            // Lets a drop in from a process with fewer rights, which is what
            // Explorer is when UAC is on and the app was run as administrator.
            ChangeWindowMessageFilterEx(hwnd, WM_DROPFILES, MSGFLT_ALLOW, IntPtr.Zero);
            ChangeWindowMessageFilterEx(hwnd, WM_COPYDATA, MSGFLT_ALLOW, IntPtr.Zero);
            ChangeWindowMessageFilterEx(hwnd, WM_COPYGLOBALDATA, MSGFLT_ALLOW, IntPtr.Zero);
            DragAcceptFiles(hwnd, true);
            SetWindowSubclass(hwnd, s_proc, (UIntPtr)1, UIntPtr.Zero);
        }
        Diag.Log($"drop: classic file drop taken on {targets.Count} window(s)");
    }

    private static IntPtr OnMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        if (msg != WM_DROPFILES)
        {
            return DefSubclassProc(hwnd, msg, wParam, lParam);
        }

        var files = new List<string>();
        uint count = DragQueryFile(wParam, uint.MaxValue, null, 0);
        for (uint i = 0; i < count; i++)
        {
            var path = new StringBuilder((int)DragQueryFile(wParam, i, null, 0) + 1);
            DragQueryFile(wParam, i, path, (uint)path.Capacity);
            files.Add(path.ToString());
        }
        DragFinish(wParam);

        var pdfs = files.Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).ToList();
        Diag.Log($"drop (classic): {files.Count} file(s), {pdfs.Count} PDF(s)");
        if (pdfs.Count > 0)
        {
            s_open?.Invoke(pdfs);
        }
        return IntPtr.Zero;
    }
}
