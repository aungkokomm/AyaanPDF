using System;
using System.IO;
using System.IO.Compression;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Define's Myanmar meanings: found under the English meaning they translate,
/// and read from the file the app really ships.
/// </summary>
public class MyanmarGlossesTests
{
    private static readonly string Us = ((char)0x1F).ToString();

    private static MyanmarGlosses Sample() => MyanmarGlosses.Load(new StringReader(string.Join("\n",
        "# sample",
        "G\tbook\tn\tစာအုပ်" + Us + "ကျမ်း",
        "G\tbook\tv\tကြိုတင်မှာသည်",
        "G\trun\tv\tပြေးသည်",
        "not a record",
        "G\tbroken\tn")));

    [Fact]
    public void glosses_are_found_by_headword_and_part_of_speech_in_order()
    {
        var glosses = Sample();

        Assert.Equal(["စာအုပ်", "ကျမ်း"], glosses.For("book", "noun"));
        Assert.Equal(["ကြိုတင်မှာသည်"], glosses.For("Book", "verb"));
        Assert.Equal(3, glosses.Count);
    }

    [Theory]
    [InlineData("book", "adjective")]
    [InlineData("book", "preposition")]
    [InlineData("run", "noun")]
    [InlineData("xyzzy", "noun")]
    [InlineData("broken", "noun")]
    public void a_part_of_speech_or_word_with_no_glosses_gives_none(string headword, string pos)
    {
        Assert.Empty(Sample().For(headword, pos));
    }

    [Fact]
    public void a_word_as_written_reaches_its_glosses_through_the_english_headword()
    {
        // "running" is not in the Myanmar file; the English lookup turns it
        // into the verb "run", which is.
        var english = WordDefinitions.Load(new StringReader("D\trun\tv\tmove fast by using one's feet"));
        var sense = english.Lookup("running")!.Senses[0];

        Assert.Equal(["ပြေးသည်"], Sample().For(sense.Headword, sense.PartOfSpeech));
    }

    // ---------------- The file the app ships ----------------

    private static string PathTo(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string path = Path.Combine(relative);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, path)))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, path);
    }

    private static readonly Lazy<MyanmarGlosses> Shipped = new(() =>
    {
        using var file = File.OpenRead(PathTo("PdfEditorApp", "Assets", "Dictionary", "akk-en-my.tsv.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return MyanmarGlosses.Load(reader);
    });

    [Theory]
    [InlineData("book", "noun", "စာအုပ်")]
    [InlineData("discipline", "noun", "စည်းကမ်း")]
    [InlineData("ordinary", "adjective", "သာမန်")]
    [InlineData("beautiful", "adjective", "လှသော")]
    [InlineData("meditation", "noun", "တရားအားထုတ်ခြင်း")]
    public void everyday_words_have_their_myanmar_meaning_first(string headword, string pos, string first)
    {
        Assert.Equal(first, Shipped.Value.For(headword, pos)[0]);
    }

    [Theory]
    [InlineData("run", "verb")]
    [InlineData("light", "noun")]
    [InlineData("go", "verb")]
    [InlineData("discipline", "noun")]
    public void a_popup_never_gets_more_than_three_short_glosses(string headword, string pos)
    {
        var glosses = Shipped.Value.For(headword, pos);

        Assert.InRange(glosses.Count, 1, MyanmarGlosses.MaxGlosses);
        Assert.All(glosses, g => Assert.InRange(g.Length, 1, 60));
    }

    [Fact]
    public void the_akk_licence_ships_with_its_data()
    {
        Assert.True(File.Exists(PathTo("PdfEditorApp", "Assets", "Dictionary", "LICENSE-AKK.txt")));
    }
}
