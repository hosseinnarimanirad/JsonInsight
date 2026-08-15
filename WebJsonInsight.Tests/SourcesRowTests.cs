using Bunit;
using Microsoft.Extensions.DependencyInjection;
using JsonInsight.Sources;
using JsonInsight.ViewModels;
using WebJsonInsight.Platform;

namespace WebJsonInsight.Tests;

/// <summary>
/// The Sources row: what it shows without being opened, and what loading one source does to the
/// other tabs.
/// </summary>
public sealed class SourcesRowTests : TestContext
{
    private Bunit.IRenderedComponent<WebJsonInsight.Components.Tabs.SourcesTab> Render(MainVm main)
    {
        Services.AddSingleton(main);
        Services.AddSingleton(new DialogService(main));
        return RenderComponent<WebJsonInsight.Components.Tabs.SourcesTab>(
            p => p.Add(c => c.Vm, main.Vault));
    }

    /// <summary>
    /// Certificate checking being off has to be visible without opening anything. A setting you
    /// cannot see is one you forget you turned on — and this one is a security downgrade.
    /// </summary>
    [Fact]
    public void Insecure_tls_shows_on_the_row_itself()
    {
        var main = Fixtures.NewMain();
        var page = Render(main);
        var row = main.Vault!.Connections.First(c => c.Kind == SourceKind.Vault);

        Assert.DoesNotContain("TLS off", page.Markup, StringComparison.Ordinal);

        row.AllowInsecureTls = true;
        page.Render();

        Assert.Contains("TLS off", page.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// In the menu it says its state twice — a tick and the word. A bare checkbox beside "Insecure
    /// TLS" required knowing that ticked meant certificate checking was off.
    /// </summary>
    [Fact]
    public void The_tls_menu_item_names_its_state()
    {
        var main = Fixtures.NewMain();
        var page = Render(main);
        var row = main.Vault!.Connections.First(c => c.Kind == SourceKind.Vault);

        page.FindAll(".rowmenu > button")[0].Click();
        Assert.Contains("OFF", page.Find(".rowmenu-toggle").TextContent, StringComparison.Ordinal);

        page.Find(".rowmenu-toggle").Click();

        Assert.True(row.AllowInsecureTls);
        Assert.Contains("ON", page.Find(".rowmenu-toggle").TextContent, StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole duplicate chain through the real markup: two rows come to read the same secret,
    /// the red notice appears, and the Save settings button the user would press is disabled — not
    /// merely the command behind it. Undone, both go back.
    /// </summary>
    [Fact]
    public void A_duplicate_source_disables_the_save_button_and_shows_the_error()
    {
        var main = Fixtures.NewMain();
        var page = Render(main);
        var vault = main.Vault!;

        var rows = vault.Connections.Where(c => c.Kind == SourceKind.Vault).Take(2).ToArray();
        rows[0].Address = "https://vault.example.com:8200";
        rows[0].SecretPath = "kv/app/config.json";
        rows[1].Address = "https://vault.example.com:8200/";
        rows[1].SecretPath = "kv/app/config.json";
        page.Render();

        var save = page.FindAll(".card-head button").Single(b => b.TextContent.Contains("Save settings"));
        Assert.True(save.HasAttribute("disabled"));
        Assert.Contains(vault.DuplicateError, page.Find(".notice-danger").TextContent, StringComparison.Ordinal);

        rows[1].SecretPath = "kv/app/other.json";
        page.Render();

        save = page.FindAll(".card-head button").Single(b => b.TextContent.Contains("Save settings"));
        Assert.False(save.HasAttribute("disabled"));
    }

    [Fact]
    public void Every_row_offers_load_as_its_own_button()
    {
        var main = Fixtures.NewMain();
        var page = Render(main);

        var loads = page.FindAll(".src-c-act button")
            .Count(b => b.TextContent.Contains("Load", StringComparison.Ordinal));

        Assert.Equal(main.Vault!.Connections.Count, loads);
    }

    /// <summary>
    /// Test is on the row, beside Load and before it. It was in the ⋮ menu while it was optional; it
    /// is the thing that turns Load on now, and a button you have to press before the next one works
    /// is not an occasional option.
    /// </summary>
    [Fact]
    public void Test_is_on_the_row_and_comes_before_load()
    {
        var main = Fixtures.NewMain();
        var page = Render(main);

        var actions = page.FindAll(".src-row")[0]
            .QuerySelectorAll(".src-c-act > button")
            .Select(b => b.TextContent.Trim())
            .ToArray();

        Assert.Contains("Test", actions);
        Assert.True(Array.IndexOf(actions, "Test") < Array.IndexOf(actions, "Load"));

        // And not left behind in the menu it came out of, where it would be the same act twice.
        page.FindAll(".rowmenu > button")[0].Click();
        Assert.DoesNotContain("Test", page.Find(".rowmenu-pop").TextContent, StringComparison.Ordinal);
    }

    /// <summary>
    /// Test is off until the row says where to read from. Pressing it on a half-filled row could only
    /// ever answer "missing: address, token, secret path", which is not an answer worth a round trip.
    /// </summary>
    [Fact]
    public void Test_is_off_until_the_row_says_where_to_read_from()
    {
        var main = Fixtures.NewMain();
        var row = main.Vault!.Connections.First(c => c.Kind == SourceKind.Vault);

        // Stated rather than assumed: these rows are built from whatever settings this machine holds.
        row.Address = string.Empty;
        row.SecretPath = string.Empty;
        row.Token = string.Empty;

        var page = Render(main);
        Assert.True(Action(page, row, "Test").HasAttribute("disabled"));

        row.Address = "https://vault.test";
        row.Token = "hvs.test";
        page.Render();
        Assert.True(Action(page, row, "Test").HasAttribute("disabled"));

        row.SecretPath = "kv/app/stage/resources/config/ui.json";
        page.Render();
        Assert.False(Action(page, row, "Test").HasAttribute("disabled"));
    }

    /// <summary>
    /// A local-file row asks for its file and nothing else — it has no server to reach and no
    /// credentials to reach it with.
    /// </summary>
    [Fact]
    public void A_local_file_row_needs_only_its_file_before_test_turns_on()
    {
        var main = Fixtures.NewMain();
        var row = main.Vault!.Connections.First(c => c.Kind == SourceKind.Vault);

        row.Kind = SourceKind.LocalFile;
        row.LocalFilePath = string.Empty;

        var page = Render(main);
        Assert.True(Action(page, row, "Test").HasAttribute("disabled"));

        row.LocalFilePath = @"C:\snapshots\stage.json";
        page.Render();
        Assert.False(Action(page, row, "Test").HasAttribute("disabled"));
    }

    /// <summary>
    /// Load waits on a Test that passed, and waits again after any edit to the row.
    ///
    /// <para>
    /// Load puts what it reads onto the Tier editor, All tiers and Text diff, so it is the button
    /// that says "this is what that environment holds". A test that passed against a path since
    /// retyped is not evidence for that, which is why the flag is cleared rather than kept.
    /// </para>
    /// </summary>
    [Fact]
    public void Load_waits_for_a_test_and_waits_again_after_an_edit()
    {
        var main = Fixtures.NewMain();
        var row = main.Vault!.Connections.First(c => c.Kind == SourceKind.Vault);

        row.Address = "https://vault.test";
        row.Token = "hvs.test";
        row.SecretPath = "kv/app/stage/resources/config/ui.json";

        var page = Render(main);
        Assert.True(Action(page, row, "Load").HasAttribute("disabled"));

        // What a successful TestCommand leaves behind; the command itself would need a Vault.
        row.TestPassed = true;
        page.Render();
        Assert.False(Action(page, row, "Load").HasAttribute("disabled"));

        row.SecretPath = "kv/app/stage/resources/config/config.json";
        page.Render();

        Assert.False(row.TestPassed);
        Assert.True(Action(page, row, "Load").HasAttribute("disabled"));
    }

    /// <summary>One row's action button by its word, found in that row rather than by position.</summary>
    private static AngleSharp.Dom.IElement Action(
        Bunit.IRenderedComponent<WebJsonInsight.Components.Tabs.SourcesTab> page,
        VaultConnectionVm row,
        string word)
    {
        var index = page.Instance.Vm!.Connections.IndexOf(row);

        return page.FindAll(".src-row")[index]
            .QuerySelectorAll(".src-c-act > button")
            .Single(b => b.TextContent.Trim().StartsWith(word, StringComparison.Ordinal));
    }

    /// <summary>
    /// Loading one source replaces that source and leaves the others alone — the point of it being a
    /// per-row act rather than a Pull.
    /// </summary>
    [Fact]
    public void Adopting_a_document_replaces_only_that_source()
    {
        var main = Fixtures.NewMain();
        var before = main.Documents.Select(d => d.Id).ToArray();

        main.AdoptDocument(Fixtures.AsTier("stage", 99, """{"AdminSettings":{"Only":1}}"""));

        Assert.Equal(before, main.Documents.Select(d => d.Id));
        Assert.Equal(99, main.Documents.Single(d => d.Id == "stage").VaultVersion);

        // The others are the objects they were, not re-read copies of them.
        Assert.Equal(12, main.Documents.Single(d => d.Id == "dev").VaultVersion);
        Assert.Equal(8, main.Documents.Single(d => d.Id == "beta").VaultVersion);
    }

    /// <summary>
    /// A reloaded source keeps its column. Dropping and re-appending it would move it to the end,
    /// so loading dev would quietly reorder the grid every other tab is read in.
    /// </summary>
    [Fact]
    public void Adopting_keeps_the_column_order()
    {
        var main = Fixtures.NewMain();

        main.AdoptDocument(Fixtures.AsTier("dev", 13, """{"AdminSettings":{"Only":1}}"""));

        Assert.Equal("dev", main.Documents[0].Id);
        Assert.Equal(["dev", "stage", "beta"], main.Documents.Select(d => d.Id));
    }

    /// <summary>A source that failed to load before stops being reported as unavailable once it loads.</summary>
    [Fact]
    public void Adopting_clears_that_source_from_unavailable()
    {
        var main = Fixtures.NewMain(
            documents: [Fixtures.Stage, Fixtures.Beta],
            unavailable: [Fixtures.Unavailable("dev", "connection refused")]);

        Assert.Contains(main.Unavailable, u => u.Id == "dev");

        main.AdoptDocument(Fixtures.Dev);

        Assert.DoesNotContain(main.Unavailable, u => u.Id == "dev");
        Assert.Contains(main.Documents, d => d.Id == "dev");
    }
}
