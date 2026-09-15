using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Define's Hindi meanings: looked up by the English dictionary word, and read
/// from the file the app really ships, which was converted from Kruti Dev.
/// </summary>
/// <remarks>
/// ⚠️ THE CONVERSION IS WHAT CAN GO WRONG. A Kruti Dev code left unconverted
/// reads as Latin letters or a stray quote, and a rule applied in the wrong
/// order leaves marks Unicode never puts together. The shipped file is held
/// to both, so a rebuilt file with a regression fails here rather than on a
/// reader's screen.
/// </remarks>
public class HindiGlossesTests
{
    private static readonly string Us = ((char)0x1F).ToString();

    private static HindiGlosses Sample() => HindiGlosses.Load(new StringReader(string.Join("\n",
        "# sample",
        "H\tjourney\tयात्रा" + Us + "सफर",
        "H\trun\tदौड़ना",
        "not a record",
        "H\tbroken\t")));

    [Fact]
    public void meanings_are_found_by_word_in_order_whatever_its_case()
    {
        var hindi = Sample();

        Assert.Equal(["यात्रा", "सफर"], hindi.For("Journey"));
        Assert.Equal(2, hindi.Count);
        Assert.Empty(hindi.For("broken"));
        Assert.Empty(hindi.For("xyzzy"));
    }

    [Fact]
    public void a_word_as_written_reaches_its_hindi_through_the_english_headword()
    {
        var english = WordDefinitions.Load(new StringReader("D\trun\tv\tmove fast by using one's feet"));

        Assert.Equal(["दौड़ना"], Sample().For(english.Lookup("running")!.Senses[0].Headword));
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

    private static StreamReader OpenShipped()
    {
        var file = File.OpenRead(PathTo("PdfEditorApp", "Assets", "Dictionary", "hindi-en-hi.tsv.gz"));
        return new StreamReader(new GZipStream(file, CompressionMode.Decompress));
    }

    private static readonly Lazy<HindiGlosses> Shipped = new(() =>
    {
        using var reader = OpenShipped();
        return HindiGlosses.Load(reader);
    });

    [Theory]
    [InlineData("journey", "यात्रा")]   // ;k=k: "=" is a full त्र
    [InlineData("friend", "मित्र")]     // fe=
    [InlineData("book", "पुस्तक")]
    [InlineData("say", "कहना")]
    [InlineData("discipline", "अनुशासन")]
    public void everyday_words_have_their_hindi_meaning_first(string word, string first)
    {
        Assert.Equal(first, Shipped.Value.For(word)[0]);
    }

    [Theory]
    [InlineData("night", "निशा")]       // fu’kk: Excel's curly apostrophe is श
    [InlineData("letter", "पत्र")]      // i=
    public void the_file_s_own_typing_habits_are_converted(string word, string meaning)
    {
        Assert.Contains(meaning, Shipped.Value.For(word));
    }

    [Fact]
    public void every_shipped_meaning_is_clean_unicode_devanagari()
    {
        var plain = new Regex(@"^[ऀ-ॿ\s,.;:()\-/?!{}=0-9]+$");
        var faults = new Regex(@"[ंँ][ा-ौ]|[ा-ौ]़|([ा-ौ])\1|ॅ");
        int words = 0;

        using var reader = OpenShipped();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            string[] fields = line.Split('\t');
            if (fields[0] != "H")
            {
                continue;
            }

            words++;
            string[] meanings = fields[2].Split((char)0x1F);
            Assert.InRange(meanings.Length, 1, 4);
            Assert.All(meanings, m => Assert.Matches(plain, m));
            Assert.All(meanings, m => Assert.DoesNotMatch(faults, m));
        }

        Assert.True(words > 20000, $"only {words} words shipped");
    }
}
