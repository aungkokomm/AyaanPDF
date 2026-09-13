using System;
using System.IO;
using PdfEditorApp.Viewport;
using Xunit;

namespace PdfEditorApp.Tests;

/// <summary>
/// ⚠️ Burmese typing must keep the route it had on 2026-09-04. KeyMagic injects
/// finished characters that never reach Text Services, so a Burmese line hosted
/// by it dropped every letter typed while its backspaces still deleted.
/// </summary>
public class TypingRouteTests
{
    [Fact]
    public void a_burmese_line_is_typed_through_the_character_path()
    {
        Assert.False(TypingRoute.HostsTextServices("နက်မှောင်သော ညတာသည် သက်ဝင်လှုပ်ရှားသော"));
    }

    [Fact]
    public void a_burmese_line_with_latin_in_it_is_still_burmese()
    {
        Assert.False(TypingRoute.HostsTextServices("PDF ဖိုင် 2026"));
    }

    [Fact]
    public void a_hindi_line_keeps_text_services_so_a_phonetic_keyboard_can_compose()
    {
        Assert.True(TypingRoute.HostsTextServices("जिस दिन धर्म के क्षेत्र में युद्ध"));
    }

    [Fact]
    public void a_latin_line_keeps_text_services()
    {
        Assert.True(TypingRoute.HostsTextServices("The quick brown fox"));
        Assert.True(TypingRoute.HostsTextServices(null));
    }

    [Fact]
    public void the_page_asks_the_route_before_it_hands_input_to_text_services()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string path = Path.Combine("PdfEditorApp", "MainPage.xaml.cs");
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, path)))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        string page = File.ReadAllText(Path.Combine(dir!.FullName, path));

        int sync = page.IndexOf("private void SyncTextInput()", StringComparison.Ordinal);
        Assert.True(sync > 0, "SyncTextInput is gone");

        int route = page.IndexOf("TypingRoute.HostsTextServices(", sync, StringComparison.Ordinal);
        int enter = page.IndexOf("_textInput.Enter()", sync, StringComparison.Ordinal);
        Assert.True(route > 0, "the page never asks which route a line types by");
        Assert.True(route < enter, "text services is entered before the route is asked");
    }
}
