using System.Collections.Generic;

namespace PdfEditorApp.Viewport;

/// <summary>A committed highlight over a run of text — the rects are a snapshot from PageTextLayer at the time it was created.</summary>
public sealed record HighlightAnnotation(int PageIndex, IReadOnlyList<TextRect> Rects, string ColorHex);

/// <summary>A single freehand stroke, as content-space (page-bitmap-pixel) points in drawing order.</summary>
public sealed record InkStrokeAnnotation(int PageIndex, IReadOnlyList<(double X, double Y)> Points, string ColorHex, double StrokeWidth);
