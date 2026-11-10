using Bunit;
using System.Text.Json.Nodes;
using JsonInsight.Classify;
using JsonInsight.Editing;
using JsonInsight.History;
using JsonInsight.Loading;
using JsonInsight.Model;
using JsonInsight.Promote;
using JsonInsight.Vault;
using JsonInsight.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using WebJsonInsight.Components.Tabs;
using WebJsonInsight.Platform;

namespace WebJsonInsight.Tests;

/// <summary>
/// The Tier editor's history panel, rendered: it appears beside the pane when History is pressed,
/// lists the version that changed the node, restores it as an edit, and goes away on Esc.
/// </summary>
public sealed class HistoryPanelTests : TestContext
{
    private const string NodePath = "PaymentSettings:Hub:Timeout";

    private static readonly Flattener Flattener = new(ArrayStrategies.Load(), Classifier.Load());

    /// <summary>v2 holds 5 at <see cref="NodePath"/>; v3 is the tier as loaded.</summary>
    private sealed class Reader(TierDocument tier) : IHistoryReader
    {
        private readonly Dictionary<int, JsonNode> _roots = Build(tier);

        private static Dictionary<int, JsonNode> Build(TierDocument tier)
        {
            var old = new DocumentEditor(tier);
            old.Replace(NodePath, "5");
            return new() { [2] = old.Working, [3] = tier.Root };
        }

        public string? Unavailable(TierDocument tier) => null;

        public Task<VaultSecretMetadata> ListAsync(TierDocument tier, CancellationToken cancellationToken) =>
            Task.FromResult(new VaultSecretMetadata("kv/app/" + tier.Id, "https://vault.test", 3, 2, 10,
            [
                new VaultVersionInfo(3, null, null, false),
                new VaultVersionInfo(2, null, null, false),
            ]));

        public Task<SecretVersion> ReadAsync(TierDocument tier, int version, CancellationToken cancellationToken)
        {
            var root = OrdinalJsonWriter.Parse(OrdinalJsonWriter.SerializeToText(_roots[version]));
            return Task.FromResult(new SecretVersion(
                version, null, root, Flattener.Flatten(tier.Id, root), "kv/app/" + tier.Id, "https://vault.test"));
        }
    }

    [Fact]
    public async Task History_opens_beside_the_pane_restores_and_closes_on_escape()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var main = Fixtures.NewMain();
        Services.AddSingleton(main);
        Services.AddSingleton(new DialogService(main));

        var editor = main.JsonEditor!;
        editor.ExpandAllCommand.Execute(null);
        editor.SelectedNode = editor.Nodes.First(n => n.Path == NodePath);
        editor.HistoryReader = new Reader(editor.Tier!);

        var page = RenderComponent<TierEditorTab>(p => p.Add(c => c.Vm, editor));
        Assert.Empty(page.FindAll(".history"));

        page.FindAll(".editor-toolbar button").First(b => b.TextContent.Contains("History")).Click();
        await editor.History!.Loading;

        page.WaitForAssertion(() =>
        {
            Assert.Single(page.FindAll(".history"));
            Assert.Contains("with-history", page.Find(".editor-body").ClassName, StringComparison.Ordinal);
            Assert.Single(page.FindAll(".history-row.selected"));
        });

        page.FindAll(".history-actions button").First(b => b.TextContent.Contains("Restore v2")).Click();
        Assert.Equal("5", editor.Editor!.TextAt(NodePath));

        page.Find(".history").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".history")));
        Assert.Null(editor.History);
    }
}
