using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// Define's Hindi meanings: looked up by the English dictionary word and part
/// of speech, and read from the file the app really ships, built from the
/// user's English-Hindi CSV.
/// </summary>
/// <remarks>
/// ⚠️ THE CLEANING IS WHAT CAN GO WRONG. The CSV's rows are not in order of
/// importance, it holds old conversion slips (मिट्र for मित्र), and a few
/// meanings are English or junk. The shipped file is held to the ranking,
/// the slip repair and clean Devanagari, so a rebuilt file with a regression
/// fails here rather than on a reader's screen.
/// </remarks>
public class HindiGlossesTests
{
    private static readonly string Us = ((char)0x1F).ToString();

    private static HindiGlosses Sample() => HindiGlosses.Load(new StringReader(string.Join("\n",
        "# sample",
        "H\tjourney\tn\tयात्रा" + Us + "सफर",
        "H\tjourney\tv\tयात्रा करना",
        "H\trun\tv\tदौड़ना",
        "not a record",
        "H\told\tपुराना",
        "H\tbroken\tn\t")));

    [Fact]
    public void meanings_are_found_by_word_and_part_of_speech_whatever_the_word_s_case()
    {
        var hindi = Sample();

        Assert.Equal(["यात्रा", "सफर"], hindi.For("Journey", "noun"));
        Assert.Equal(["यात्रा करना"], hindi.For("journey", "verb"));
        Assert.Equal(3, hindi.Count);
        Assert.Empty(hindi.For("journey", "adverb"));
        Assert.Empty(hindi.For("run", "noun"));
        Assert.Empty(hindi.For("old", "adjective"));
        Assert.Empty(hindi.For("broken", "noun"));
        Assert.Empty(hindi.For("xyzzy", "noun"));
    }

    [Fact]
    public void a_word_as_written_reaches_its_hindi_through_the_english_headword_and_part_of_speech()
    {
        var english = WordDefinitions.Load(new StringReader("D\trun\tv\tmove fast by using one's feet"));
        var sense = english.Lookup("running")!.Senses[0];

        Assert.Equal(["दौड़ना"], Sample().For(sense.Headword, sense.PartOfSpeech));
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

    private static IEnumerable<string[]> ShippedRecords()
    {
        using var reader = OpenShipped();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            string[] fields = line.Split('\t');
            if (fields[0] == "H")
            {
                yield return fields;
            }
        }
    }

    [Theory]
    [InlineData("book", "noun", "पुस्तक")]          // the CSV lists ढेर first
    [InlineData("friend", "noun", "दोस्त")]
    [InlineData("journey", "noun", "यात्रा")]
    [InlineData("journey", "verb", "प्रवास करना")]
    [InlineData("freedom", "noun", "स्वतंत्रता")]
    [InlineData("discipline", "noun", "अनुशासन")]
    [InlineData("beautiful", "adjective", "सुन्दर")]
    public void everyday_words_have_their_hindi_meaning_first(string word, string partOfSpeech, string first)
    {
        Assert.Equal(first, Shipped.Value.For(word, partOfSpeech)[0]);
    }

    [Fact]
    public void no_tta_ra_slip_ships_where_the_ta_ra_spelling_does()
    {
        string ttaRa = "ट्र";
        string taRa = "त्र";
        var meanings = ShippedRecords().SelectMany(fields => fields[3].Split((char)0x1F)).ToHashSet();

        Assert.DoesNotContain(meanings, m => m.Contains(ttaRa) && meanings.Contains(m.Replace(ttaRa, taRa)));
        Assert.Contains("राष्ट्र", Shipped.Value.For("nation", "noun"));   // a real ट्र is kept
    }

    [Fact]
    public void every_shipped_meaning_is_clean_unicode_devanagari()
    {
        var plain = new Regex("^[ऀ-ॿ" + (char)0x200C + (char)0x200D + @"\s,.;:()\[\]\-/?!{}=0-9]+$");
        var faults = new Regex(@"[ंँ][ा-ौ]|[ा-ौ]़|([ा-ौ])\1|ॅ");
        int records = 0;

        foreach (string[] fields in ShippedRecords())
        {
            records++;
            Assert.Equal(4, fields.Length);
            Assert.Contains(fields[2], new[] { "n", "v", "a", "r" });
            string[] meanings = fields[3].Split((char)0x1F);
            Assert.InRange(meanings.Length, 1, HindiGlosses.MaxMeanings);
            Assert.All(meanings, m => Assert.Matches(plain, m));
            Assert.All(meanings, m => Assert.DoesNotMatch(faults, m));
        }

        Assert.True(records > 40000, $"only {records} word and part-of-speech records shipped");
    }
}
