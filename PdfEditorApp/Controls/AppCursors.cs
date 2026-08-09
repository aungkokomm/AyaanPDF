using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using PdfEditorApp.Viewport;

namespace PdfEditorApp.Controls;

/// <summary>
/// The app's own cursors, loaded from AyaanCursors.dll.
///
/// Windows has no open-palm/closed-fist pair to borrow: IDC_HAND is a pointing
/// finger and IDC_SIZEALL is four arrows, so an Acrobat-style hand tool has to
/// bring its own art. The pictures live as Win32 CURSOR resources in a tiny
/// resource-only DLL shipped beside the exe, because InputDesktopResourceCursor
/// can only load a cursor out of a module and adding resources to a .NET
/// apphost means hand-authoring the exe's entire resource script.
///
/// Everything here degrades rather than throws. A missing or unreadable DLL
/// costs the hand its picture, not the viewport its pan.
/// </summary>
internal static class AppCursors
{
    private const string ModuleName = "AyaanCursors.dll";

    // Must match cursors.rc.
    private const uint HandOpenResource = 1;
    private const uint HandClosedResource = 2;

    private static bool _moduleTried;
    private static bool _moduleLoaded;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string fileName);

    /// <summary>
    /// A fresh platform cursor for a resolved <see cref="ViewportCursor"/>.
    ///
    /// Deliberately NOT cached. An InputCursor is disposable and assigning one
    /// to ProtectedCursor hands it to the framework, so handing the same
    /// instance over repeatedly is not something the platform promises to
    /// tolerate. Every call site that has ever worked in this app - the whole
    /// InputSystemCursor.Create switch this replaced - built a new one each
    /// time. Callers avoid the churn by only asking when the cursor changes.
    /// </summary>
    public static InputCursor Create(ViewportCursor cursor)
    {
        if (cursor is ViewportCursor.HandOpen or ViewportCursor.HandGrab)
        {
            uint id = cursor == ViewportCursor.HandGrab ? HandClosedResource : HandOpenResource;
            if (LoadModule() && TryCustom(id) is InputCursor custom)
            {
                return custom;
            }

            // Fallback when the DLL is missing: still two clearly different
            // pictures, so the tool reads as alive even without its own art.
            return InputSystemCursor.Create(cursor == ViewportCursor.HandGrab
                ? InputSystemCursorShape.SizeAll
                : InputSystemCursorShape.Hand);
        }

        return InputSystemCursor.Create(cursor switch
        {
            ViewportCursor.IBeam => InputSystemCursorShape.IBeam,
            ViewportCursor.Cross => InputSystemCursorShape.Cross,
            _ => InputSystemCursorShape.Arrow,
        });
    }

    private static InputCursor? TryCustom(uint resourceId)
    {
        try
        {
            return InputDesktopResourceCursor.CreateFromModule(ModuleName, resourceId);
        }
        catch (Exception ex)
        {
            Diag.Log($"AppCursors: resource {resourceId} unavailable, {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Pulls the cursor DLL into the process once.
    ///
    /// CreateFromModule takes a module NAME, and whether it resolves that by
    /// LoadLibrary or by GetModuleHandle is not documented. Loading it here
    /// from a full path makes both readings work and pins the lookup to the
    /// app's own folder rather than the DLL search order.
    /// </summary>
    private static bool LoadModule()
    {
        if (_moduleTried)
        {
            return _moduleLoaded;
        }
        _moduleTried = true;

        string path = Path.Combine(AppContext.BaseDirectory, ModuleName);
        if (!File.Exists(path))
        {
            Diag.Log($"AppCursors: {ModuleName} not found beside the exe, falling back to system cursors");
            return false;
        }

        _moduleLoaded = LoadLibraryW(path) != IntPtr.Zero;
        if (!_moduleLoaded)
        {
            Diag.Log($"AppCursors: LoadLibrary({ModuleName}) failed, error {Marshal.GetLastWin32Error()}");
        }
        return _moduleLoaded;
    }
}
