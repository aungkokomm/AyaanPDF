using System;
using System.Runtime.InteropServices;

namespace PdfEditorApp.Interop;

/// <summary>
/// Mirrors render_core::ocr::OcrWord (src/ocr.rs): one recognised word, its box
/// as fractions of the page as rendered, its text as UTF-8 the caller keeps
/// pinned for the duration of the call.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct OcrWordNative
{
    public float Left;
    public float Top;
    public float Right;
    public float Bottom;
    public IntPtr Text;
    public nuint TextLen;
}
