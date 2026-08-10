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
    /// Test moved into the menu: it answers a question about a row without changing what any other
    /// tab is showing, which makes it the rarer of the two acts.
    /// </summary>
    [Fact]
    public void Test_is_in_the_menu_rather_than_on_the_row()
    {
        var main = Fixtures.NewMain();
        var page = Render(main);

        Assert.DoesNotContain(page.FindAll(".src-c-act > button"),
            b => b.TextContent.Contains("Test", StringComparison.Ordinal));

        page.FindAll(".rowmenu > button")[0].Click();

        Assert.Contains("Test connection", page.Find(".rowmenu-pop").TextContent, StringComparison.Ordinal);
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
