# Ayaan PDF

A free PDF reader and editor for Windows, made with English, Hindi and Myanmar (Burmese) documents in mind.

Ayaan PDF opens, reads, marks up and edits PDF files. It can also change the words already in a PDF, including Hindi and Myanmar text, and it can turn scanned pages into searchable text. Everything runs on your own computer, with no account and no internet connection needed.

[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)
![Platform: Windows 10 and 11, x64](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011%20x64-blue.svg)

## Highlights

- **Edit the text already in a PDF**, in place on the page. Hindi (Devanagari) and Myanmar text are shaped properly, so conjuncts, vowel signs and stacked consonants come out right. Burmese typing through KeyMagic works too.
- **Make scanned pages searchable.** Recognize text reads English, Hindi and Myanmar out of the box, and more than 100 other languages can be downloaded from inside the app.
- **Look up words without leaving the page.** Right-click an English word to see its meaning, with Myanmar and Hindi meanings under it, all offline.
- **Spot old fonts.** Pages typed in pre-Unicode Hindi or Burmese fonts are flagged, and those pages can be recognised into real text.

## Features

### Reading

- Several documents at once, each in its own tab
- Continuous scrolling or one page at a time
- Fit page, fit width, actual size, and zoom up to 800%
- Turn the view without changing the file
- Find text, with F3 for the next match
- Links you can follow, with Back and Forward (Alt+Left and Alt+Right)
- Page thumbnails and a bookmarks panel
- Full screen, night mode, and light or dark themes
- Recent files, and double-clicking a PDF in File Explorer opens it as a new tab in the open window

### Marking up

- Highlight, underline and strike out text
- Freehand drawing
- Shapes: rectangle, rounded rectangle, ellipse, line and arrow, with colours, gradient fills and shadows
- Text boxes with your choice of font, colour, fill, outline, alignment, underline and strikethrough, and they can be rotated
- Sticky notes
- Stamps: APPROVED, NOT APPROVED, DRAFT, FINAL, CONFIDENTIAL, REVIEWED, RECEIVED, VOID, URGENT, COPY, PAID and SIGN HERE, marks such as a tick, a cross and a star, or your own pictures
- Signatures: draw one once, save it, and place it on any document
- Links to web and email addresses
- Group, align, distribute, duplicate, rotate, and bring forward or send back
- Rulers and guides, including margin and column guides

### Editing text

- In Edit mode, click a line of text to change it right on the page
- English, Hindi and Myanmar are shaped and written back as real, searchable text
- Paragraphs reflow as you type
- For Myanmar, editing works on text set in Myanmar Text or Pyidaungsu

### Pages

- Reorder pages by dragging their thumbnails, and act on several at once
- Insert pages from another file, or a blank page
- Extract, rotate and delete pages
- Merge PDFs and pictures into a new document

### Recognize text (OCR)

- English, Hindi and Myanmar are included
- More than 100 other languages can be downloaded in Recognition languages. Each download is checked before it is kept. Chinese, Japanese and Korean are not available yet.
- The recognised words are written as an invisible layer, so the page looks the same but can be searched, selected and copied
- A Fast option uses the recognition built into Windows, for English

### Define

- Right-click an English word for its definition, from WordNet
- Myanmar and Hindi meanings are shown under it, and each can be turned off in Settings
- Works entirely offline

### Document

- Document properties (Alt+Enter): title, author, subject, keywords and language
- Choose how the file opens: page, zoom, panel and layout
- Remove personal information when saving
- See, save and remove attached files
- Check pages for missing text, old fonts, or text that was recognised
- Build bookmarks from the document's headings, by how they look or by what they say
- Fill in forms
- Open password-protected PDFs
- Print, and save a flattened copy

### Your work is safe

- Saving happens in the background, so the app stays usable
- If Ayaan PDF closes unexpectedly, your unsaved work is offered back the next time it starts
- Undo and redo for your changes

## Install

1. Download `AyaanPDF-Setup-<version>.exe` from the [Releases](../../releases) page.
2. Run it. It installs for your Windows account only, so it doesn't need administrator rights.
3. Leave "Offer Ayaan PDF for PDF files" ticked if you want it in Open with. To make it your default PDF app, choose it in Settings > Apps > Default apps.

The installer isn't code-signed, so Windows SmartScreen may warn that it doesn't recognise the app. Choose **More info**, then **Run anyway**.

### Requirements

- Windows 10 version 1809 or later, or Windows 11
- A 64-bit (x64) PC
- For Myanmar, the Myanmar Text font, which comes with Windows, or Pyidaungsu

## Privacy

Ayaan PDF works offline. It connects to the internet only when you ask it to:

- when you download a recognition language, from the Tesseract project on GitHub
- when you click a web link in a document, which opens in your browser

There is no account, no telemetry and no automatic update check.

## Keyboard shortcuts

Press **F1** in the app to see all of them. The most used are:

| Keys | What they do |
|---|---|
| Ctrl+O | Open |
| Ctrl+S / Ctrl+Shift+S | Save / Save as |
| Ctrl+P | Print |
| Ctrl+F, F3 | Find, next match |
| Ctrl+Z / Ctrl+Y | Undo / Redo |
| Ctrl+0 / Ctrl+1 / Ctrl+2 | Fit page / Actual size / Fit width |
| Ctrl+Tab | Next tab |
| F4 / F6 | Pages panel / Bookmarks panel |
| F11 | Full screen |
| Alt+Enter | Document properties |
| H, V, U, D, R, T, N, S, L | Hand, Select, Highlight, Draw, Shape, Text, Note, Stamp, Link |

## Building from source

### What you need

- Windows 10 or 11, x64
- [Visual Studio 2026](https://visualstudio.microsoft.com/) with the **.NET desktop development** workload. Build with Visual Studio's MSBuild: the `dotnet` command line lacks a WinUI step the app needs.
- The [.NET 10 SDK](https://dotnet.microsoft.com/)
- [Rust](https://rustup.rs/), stable, for `x86_64-pc-windows-msvc`
- [Python 3](https://www.python.org/), to fetch the recognition models
- [Inno Setup 6](https://jrsoftware.org/isinfo.php), only to build the installer

### Steps

1. **Get PDFium.** Download `pdfium-win-x64.tgz` from the [pdfium-binaries release chromium/7961](https://github.com/bblanchon/pdfium-binaries/releases/tag/chromium%2F7961) and put its `bin/pdfium.dll` in `render_core/vendor/pdfium/`. Its licences are already in that folder.

2. **Get the recognition models** (about 35 MB, each one checked against a pinned hash):

   ```
   python tools/ocr/fetch_ocr_models.py
   ```

3. **Build the app** from a Developer PowerShell for Visual Studio. The Rust core, `render_core.dll`, is built by `cargo` as part of this step.

   ```
   MSBuild PdfEditorApp\PdfEditorApp.csproj -t:Build -restore -p:Configuration=Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true -p:SelfContained=true
   ```

4. **Run the tests:**

   ```
   dotnet test PdfEditorApp.Tests/PdfEditorApp.Tests.csproj
   cd render_core
   cargo test --release --lib
   ```

5. **Build the installer** into `dist\`:

   ```
   pwsh -File tools\build_installer.ps1
   ```

After changing a dependency, regenerate the licence notices with `python tools/make_third_party_notices.py`.

## How it is built

- **The window** is WinUI 3 on the Windows App SDK, written in C# on .NET 10. It is unpackaged and self-contained, so it carries its own .NET runtime and needs nothing installed.
- **The core**, `render_core.dll`, is written in Rust. It draws and edits pages through [PDFium](https://pdfium.googlesource.com/pdfium/), using a patched copy of [pdfium-render](https://github.com/ajrcarey/pdfium-render). It shapes Hindi and Myanmar text with [rustybuzz](https://github.com/harfbuzz/rustybuzz) and writes bookmarks with [lopdf](https://github.com/J-F-Liu/lopdf).
- **Live previews** of shapes and effects are drawn with [SkiaSharp](https://github.com/mono/SkiaSharp). What is saved is always drawn by PDFium.
- **Text recognition** uses [Tesseract](https://github.com/tesseract-ocr/tesseract). For Myanmar, a line recognition model runs on ONNX Runtime.

### Project layout

| Folder | What is in it |
|---|---|
| `PdfEditorApp/` | The app: windows, dialogs, and the view model |
| `PdfEditorApp.Viewport/` | Logic with no user interface, which the tests cover |
| `PdfEditorApp.Rendering.Skia/` | The live previews |
| `PdfEditorApp.Tests/` | The C# tests (xUnit) |
| `render_core/` | The Rust core and its tests |
| `vendor/pdfium-render/` | The patched copy of pdfium-render |
| `installer/` | The Inno Setup script |
| `tools/` | Build, dictionary, OCR and licence scripts |

## Licence

Ayaan PDF is released under the [MIT License](LICENSE), and so are its Hindi and Myanmar dictionaries.

It is built on the work of others, each under its own licence:

- [THIRD-PARTY-NOTICES.txt](PdfEditorApp/THIRD-PARTY-NOTICES.txt): PDFium and the libraries built into it, the Rust libraries, the .NET libraries and the .NET runtime
- [Assets/Ocr/THIRD-PARTY-NOTICES-OCR.txt](PdfEditorApp/Assets/Ocr/THIRD-PARTY-NOTICES-OCR.txt): Tesseract, Leptonica and the recognition models
- [Assets/Fonts/THIRD-PARTY-NOTICES.txt](PdfEditorApp/Assets/Fonts/THIRD-PARTY-NOTICES.txt): the Oswald font and the stamp icons
- [Assets/Dictionary/](PdfEditorApp/Assets/Dictionary/): WordNet's licence and the dictionaries' own

## Thanks

To the people behind PDFium, pdfium-render, rustybuzz, lopdf, SkiaSharp, Tesseract and Leptonica; to Kaung Sithu for mmpdfkit and the Myanmar recognition model; to Princeton University for WordNet; and to the makers of the Oswald font and Tabler Icons.
