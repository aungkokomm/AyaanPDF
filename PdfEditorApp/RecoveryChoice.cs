using PdfEditorApp.Viewport;

namespace PdfEditorApp;

/// <summary>
/// One line in the recovery offer.
///
/// The record carries the paths and the timestamp; this carries the two strings
/// the reader actually reads, worked out once rather than by a converter, so
/// the template stays a template.
/// </summary>
/// <param name="Name">The ORIGINAL document's name, which is what they will look for.</param>
/// <param name="Detail">Size and age, so they can judge whether it is worth taking back.</param>
public sealed record RecoveryChoice(string Name, string Detail, RecoveryRecord Record);
