using Bunit;
using JsonInsight.Vault;
using JsonInsight.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using WebJsonInsight.Components.Tabs;
using WebJsonInsight.Platform;

namespace WebJsonInsight.Tests;

/// <summary>
/// The All tiers detail strip's "History in" buttons: one per Vault column, and pressing one asks the
/// shell for the Tier editor with that key's history open.
/// </summary>
public sealed class GridHistoryTests : TestContext
{
    private sealed class NoHistory : IHistoryReader
    {
        public string? Unavailable(JsonInsight.Model.TierDocument tier) => "test: no Vault";

        public Task<VaultSecretMetadata> ListAsync(JsonInsight.Model.TierDocument tier, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("not reached");

        public Task<JsonInsight.History.SecretVersion> ReadAsync(JsonInsight.Model.TierDocument tier, int version, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("not reached");
    }

    [Fact]
    public void The_strip_offers_history_per_vault_column_and_asks_for_the_editor()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var main = Fixtures.NewMain();
        Services.AddSingleton(main);
        Services.AddSingleton(new DialogService(main));
        main.JsonEditor!.HistoryReader = new NoHistory();

        var requested = 0;
        main.EditorRequested += (_, _) => requested++;

        var grid = main.Tiers!;
        grid.SelectedRow = grid.Rows.First(r => !r.IsGroup);

        var page = RenderComponent<AllTiersTab>(p => p.Add(c => c.Vm, grid));
        var buttons = page.FindAll(".detail-history button");

        Assert.Equal(grid.HistoryTiers.Count, buttons.Count);
        Assert.Equal(grid.HistoryTiers.Select(t => t.Label), buttons.Select(b => b.TextContent.Trim()));

        buttons[0].Click();

        Assert.Equal(1, requested);
        Assert.Equal(grid.HistoryTiers[0].Id, main.JsonEditor.Tier!.Id);
    }
}
