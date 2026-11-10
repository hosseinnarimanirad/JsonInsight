using JsonInsight.Model;
using JsonInsight.ViewModels;

namespace JsonInsight.Tests;

/// <summary>
/// History opened from the All tiers grid: the selected row's key, on one tier, in the Tier editor's
/// panel — including a key that tier no longer has.
/// </summary>
[Collection("sample-files")]
public sealed class GridHistoryTests
{
    private static readonly SampleFiles Fixtures = new();

    private static MainVm Seeded()
    {
        var main = new MainVm(vaultAtStartup: false);
        main.Seed(Fixtures.Documents);
        return main;
    }

    [Fact]
    public void Every_vault_column_offers_history()
    {
        var main = Seeded();

        Assert.Equal(
            main.Tiers!.Documents.Where(d => d.IsFromVault).Select(d => d.Id),
            main.Tiers.HistoryTiers.Select(d => d.Id));
        Assert.NotEmpty(main.Tiers.HistoryTiers);
    }

    [Fact]
    public async Task History_on_a_row_opens_the_editor_on_that_tier_and_key()
    {
        var main = Seeded();
        var grid = main.Tiers!;
        var row = grid.Rows.First(r => !r.IsGroup && !r.Path.Contains('[') && r.Path.Contains(':'));
        var tier = grid.HistoryTiers.Last();

        var editor = main.JsonEditor!;
        var leafTier = editor.Tiers.First(t => t.Id == tier.Id);
        if (!leafTier.Flat.Contains(row.Path))
        {
            row = grid.Rows.First(r => !r.IsGroup && leafTier.Flat.Contains(r.Path) && !r.Path.Contains('['));
        }

        editor.HistoryReader = new DerivedHistoryReader(leafTier, row.Path, Fixtures.Flattener);
        var requested = 0;
        main.EditorRequested += (_, _) => requested++;

        grid.SelectedRow = row;
        grid.ShowHistoryCommand.Execute(tier);
        await editor.History!.Loading;

        Assert.Equal(1, requested);
        Assert.Equal(tier.Id, editor.Tier!.Id);
        Assert.Equal(row.Path, editor.SelectedNode!.Path);
        Assert.Equal(row.Path, editor.History.Path);
        Assert.NotEmpty(editor.History.Rows);
    }

    [Fact]
    public void A_key_the_tier_no_longer_has_still_gets_its_history()
    {
        var main = Seeded();
        var editor = main.JsonEditor!;
        var tier = editor.Tiers.First(t => t.IsFromVault);
        var missing = "PaymentSettings:NoLongerHere";
        Assert.False(tier.Flat.Contains(missing));

        editor.HistoryReader = new FakeHistoryReader(Fixtures.Flattener);
        main.ShowHistory(tier.Id, missing);

        Assert.NotNull(editor.History);
        Assert.Equal(missing, editor.History!.Path);
        Assert.Null(editor.SelectedNode);
        Assert.Contains("is not in", editor.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_happens_without_a_selected_row()
    {
        var main = Seeded();
        var requested = 0;
        main.EditorRequested += (_, _) => requested++;

        main.Tiers!.SelectedRow = null;
        main.Tiers.ShowHistoryCommand.Execute(main.Tiers.HistoryTiers[0]);

        Assert.Equal(0, requested);
        Assert.Null(main.JsonEditor!.History);
    }
}
