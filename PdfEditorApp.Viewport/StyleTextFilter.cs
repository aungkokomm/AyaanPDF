using System;
using System.Text.RegularExpressions;

namespace PdfEditorApp.Viewport;

/// <summary>How the text of a candidate heading is tested.</summary>
public enum TextFilterKind
{
    /// <summary>No restriction. Every run that matches a style is a heading.</summary>
    Any,
    Contains,
    StartsWith,
    /// <summary>The opposite of <see cref="Contains"/>: the way to drop a running header.</summary>
    DoesNotContain,
    Matches,
}

/// <summary>
/// A second opinion on a run that already matched a style.
///
/// Style alone is often not enough. A book sets its running header in the same
/// font and size as its chapter titles, so matching the style alone produces a
/// bookmark on every page reading "A HISTORY OF THE WORLD"; excluding that one
/// phrase is the difference between a usable outline and a useless one. In the
/// other direction, a document that numbers its headings can be narrowed to
/// just the numbered ones.
/// </summary>
public sealed class StyleTextFilter
{
    /// <summary>Lets everything through.</summary>
    public static readonly StyleTextFilter Any = new(TextFilterKind.Any, string.Empty, null);

    private readonly Regex? _regex;

    public TextFilterKind Kind { get; }

    public string Text { get; }

    private StyleTextFilter(TextFilterKind kind, string text, Regex? regex)
    {
        Kind = kind;
        Text = text;
        _regex = regex;
    }

    /// <summary>
    /// Builds a filter, compiling the pattern once rather than per run.
    ///
    /// An empty box is <see cref="Any"/> whatever the kind says: half-typed is
    /// the normal state of a text box, and a filter that matched nothing while
    /// it was empty would look like the scan had broken.
    /// </summary>
    /// <exception cref="ArgumentException">The regular expression is not valid.</exception>
    public static StyleTextFilter Create(TextFilterKind kind, string? text)
    {
        if (kind == TextFilterKind.Any || string.IsNullOrWhiteSpace(text))
        {
            return Any;
        }

        if (kind != TextFilterKind.Matches)
        {
            return new StyleTextFilter(kind, text, null);
        }

        try
        {
            // Case-insensitive like the other kinds, so the three behave the
            // same way and nobody has to know which one changed the rules.
            return new StyleTextFilter(
                kind, text,
                new Regex(text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        }
        catch (ArgumentException e)
        {
            throw new ArgumentException($"Not a valid pattern: {e.Message}", nameof(text));
        }
    }

    /// <summary>Whether this title survives the filter.</summary>
    public bool Allows(string title)
    {
        title ??= string.Empty;

        return Kind switch
        {
            TextFilterKind.Contains =>
                title.Contains(Text, StringComparison.InvariantCultureIgnoreCase),
            TextFilterKind.DoesNotContain =>
                !title.Contains(Text, StringComparison.InvariantCultureIgnoreCase),
            TextFilterKind.StartsWith =>
                title.TrimStart().StartsWith(Text, StringComparison.InvariantCultureIgnoreCase),
            TextFilterKind.Matches => _regex?.IsMatch(title) ?? true,
            _ => true,
        };
    }
}
