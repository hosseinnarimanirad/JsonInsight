using Bunit;
using JsonInsight.Sources;
using JsonInsight.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using WebJsonInsight.Components.Shared;

namespace WebJsonInsight.Tests;

/// <summary>
/// The JSON-path control on a Vault row: the field, the Search, and the list of what Search found.
///
/// <para>
/// These exist because the first version of this cell was an <c>&lt;input list&gt;</c> with a
/// <c>&lt;datalist&gt;</c>, which compiled, rendered, and was useless: neither WebView2 nor WebKitGTK
/// draws a visible affordance for one, so a search that found 180 secrets looked exactly like a
/// search that found nothing. Every test below asserts something is <em>on screen</em>, because "the
/// data was in the DOM" was the defect, not the fix.
/// </para>
/// </summary>
public sealed class PathPickerTests : TestContext
{
    public PathPickerTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private (VaultVm Vm, VaultConnectionVm Row) Row()
    {
        var main = Fixtures.NewMain();
        Services.AddSingleton(main);

        var vm = main.Vault!;
        var row = vm.Connections.First(c => c.Kind == SourceKind.Vault);
        return (vm, row);
    }

    private IRenderedComponent<PathPicker> Render(VaultVm vm, VaultConnectionVm row) =>
        RenderComponent<PathPicker>(p => p.Add(c => c.Row, row).Add(c => c.Vm, vm));

    /// <summary>The Search button is on the row, and it is not the thing that was missing.</summary>
    [Fact]
    public void The_row_has_a_search_button()
    {
        var (vm, row) = Row();

        var page = Render(vm, row);

        Assert.Contains(page.FindAll("button"), b => b.TextContent.Contains("Search", StringComparison.Ordinal));
    }

    /// <summary>
    /// With nothing found yet the dropdown cannot be opened, and says why rather than opening onto
    /// an empty box.
    /// </summary>
    [Fact]
    public void The_dropdown_is_disabled_until_something_has_been_found()
    {
        var (vm, row) = Row();
        row.KnownPaths.Clear();

        var page = Render(vm, row);
        var toggle = page.Find(".pathpicker-toggle");

        Assert.True(toggle.HasAttribute("disabled"));
        Assert.Contains("press Search", toggle.GetAttribute("title"), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What the datalist could not do: found paths are visible, as rows, without any browser having
    /// to volunteer a picker.
    /// </summary>
    [Fact]
    public void Found_paths_are_listed_as_rows_when_the_dropdown_is_opened()
    {
        var (vm, row) = Row();
        row.KnownPaths.Clear();
        row.KnownPaths.Add("kv/app/stage");
        row.KnownPaths.Add("kv/app/stage/resources/config/ui.json");
        row.KnownPaths.Add("kv/app/stage/resources/config/config.json");

        var page = Render(vm, row);
        page.Find(".pathpicker-toggle").Click();

        Assert.Equal(3, page.FindAll(".pathpicker-item").Count);
        Assert.Contains("ui.json", page.Markup, StringComparison.Ordinal);

        // The count is on the toggle, so "did it find anything" is answerable without opening it.
        Assert.Equal("3", page.Find(".pathpicker-count").TextContent);
    }

    /// <summary>A .json is tagged, because it is what anyone opening this list is looking for.</summary>
    [Fact]
    public void Json_paths_are_tagged()
    {
        var (vm, row) = Row();
        row.KnownPaths.Clear();
        row.KnownPaths.Add("kv/app/stage");
        row.KnownPaths.Add("kv/app/stage/resources/config/ui.json");

        var page = Render(vm, row);
        page.Find(".pathpicker-toggle").Click();

        Assert.Single(page.FindAll(".pp-tag"));
    }

    /// <summary>
    /// A Vault walk is bounded at 400 listings, so the list can run to hundreds. Narrowing it is not
    /// a luxury.
    /// </summary>
    [Fact]
    public void The_list_can_be_narrowed()
    {
        var (vm, row) = Row();
        row.KnownPaths.Clear();
        for (var i = 0; i < 40; i++)
        {
            row.KnownPaths.Add($"kv/app/stage/thing{i:00}");
        }

        row.KnownPaths.Add("kv/app/stage/resources/config/ui.json");

        var page = Render(vm, row);
        page.Find(".pathpicker-toggle").Click();
        Assert.Equal(41, page.FindAll(".pathpicker-item").Count);

        page.FindComponent<SearchBox>().Find("input").Input("ui.json");

        Assert.Single(page.FindAll(".pathpicker-item"));
        Assert.Contains("1 of 41 shown", page.Markup, StringComparison.Ordinal);
    }

    /// <summary>Choosing one fills the field in and closes the list.</summary>
    [Fact]
    public void Choosing_a_path_sets_the_row_and_closes()
    {
        var (vm, row) = Row();
        row.KnownPaths.Clear();
        row.KnownPaths.Add("kv/app/stage/resources/config/ui.json");

        var page = Render(vm, row);
        page.Find(".pathpicker-toggle").Click();
        page.Find(".pathpicker-item").Click();

        Assert.Equal("kv/app/stage/resources/config/ui.json", row.SecretPath);
        Assert.Empty(page.FindAll(".pathpicker-menu"));
    }

    /// <summary>
    /// The field stays typeable. A secret whose mount the token cannot list never appears in the list
    /// and must still be reachable — the README is explicit that a picker which dropped the current
    /// answer would make a working row look misconfigured.
    /// </summary>
    [Fact]
    public void The_path_can_still_be_typed()
    {
        var (vm, row) = Row();

        var page = Render(vm, row);
        page.Find(".pathpicker-field input").Input("kv_other/typed/by/hand.json");

        Assert.Equal("kv_other/typed/by/hand.json", row.SecretPath);
    }

    /// <summary>Clicking away dismisses it, rather than leaving it over the rows below.</summary>
    [Fact]
    public void Clicking_outside_closes_the_dropdown()
    {
        var (vm, row) = Row();
        row.KnownPaths.Clear();
        row.KnownPaths.Add("kv/app/stage");

        var page = Render(vm, row);
        page.Find(".pathpicker-toggle").Click();
        Assert.NotEmpty(page.FindAll(".pathpicker-menu"));

        page.Find(".pathpicker-veil").Click();
        Assert.Empty(page.FindAll(".pathpicker-menu"));
    }

    /// <summary>
    /// A local-file row has no Vault to walk, so it gets Browse instead and never renders this at all.
    /// </summary>
    [Fact]
    public void A_local_file_row_has_no_path_picker()
    {
        var main = Fixtures.NewMain();
        Services.AddSingleton(main);
        Services.AddSingleton(new WebJsonInsight.Platform.DialogService(main));

        var vm = main.Vault!;
        foreach (var row in vm.Connections)
        {
            row.Kind = SourceKind.LocalFile;
        }

        var page = RenderComponent<WebJsonInsight.Components.Tabs.SourcesTab>(p => p.Add(c => c.Vm, vm));

        Assert.Empty(page.FindAll(".pathpicker"));
        Assert.Contains(page.FindAll("button"), b => b.TextContent.Contains("Browse", StringComparison.Ordinal));
    }
}
