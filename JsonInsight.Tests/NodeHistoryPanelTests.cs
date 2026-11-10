using System.Text.Json;
using System.Text.Json.Nodes;
using JsonInsight.Editing;
using JsonInsight.History;
using JsonInsight.Loading;
using JsonInsight.Model;
using JsonInsight.Promote;
using JsonInsight.Vault;
using JsonInsight.ViewModels;

namespace JsonInsight.Tests;

/// <summary>
/// Three versions derived from a real tier, around one key: the oldest kept version does not have it,
/// the middle one holds "old-value", and the newest is the tier exactly as loaded. Counts every query.
/// </summary>
internal sealed class DerivedHistoryReader : IHistoryReader
{
    private readonly Flattener _flattener;
    private readonly Dictionary<int, JsonNode> _roots = [];

    public DerivedHistoryReader(TierDocument tier, string path, Flattener flattener)
    {
        _flattener = flattener;
        Newest = tier.VaultVersion ?? 3;

        var without = new DocumentEditor(tier);
        without.Remove(path);

        var old = new DocumentEditor(tier);
        old.Replace(path, "\"old-value\"");

        _roots[Newest - 2] = without.Working;
        _roots[Newest - 1] = old.Working;
        _roots[Newest] = tier.Root;
    }

    public int Newest { get; }

    public int Queries { get; private set; }

    public string? Unavailable(TierDocument tier) => tier.IsFromVault ? null : "Files have no version history.";

    public Task<VaultSecretMetadata> ListAsync(TierDocument tier, CancellationToken cancellationToken)
    {
        Queries++;
        return Task.FromResult(new VaultSecretMetadata(
            "kv/app/" + tier.Id, "https://vault.test", Newest, Newest - 2, 10,
            _roots.Keys.OrderDescending()
                .Select(v => new VaultVersionInfo(v, new DateTimeOffset(2026, 11, 1, v % 24, 0, 0, TimeSpan.Zero), null, false))
                .ToArray()));
    }

    public Task<SecretVersion> ReadAsync(TierDocument tier, int version, CancellationToken cancellationToken)
    {
        Queries++;
        var root = OrdinalJsonWriter.Parse(OrdinalJsonWriter.SerializeToText(_roots[version]));
        return Task.FromResult(new SecretVersion(
            version, null, root, _flattener.Flatten(tier.Id, root), "kv/app/" + tier.Id, "https://vault.test"));
    }
}

/// <summary>
/// The Tier editor's history panel. In the sample-files collection because the copy test swaps the
/// static <c>Platform.Clipboard</c>, as <c>EditorPaneTests</c> does.
/// </summary>
[Collection("sample-files")]
public sealed class NodeHistoryPanelTests
{
    private static readonly SampleFiles Fixtures = new();

    private sealed record Opened(JsonEditorVm Editor, DerivedHistoryReader Reader, string Path)
    {
        public NodeHistoryVm History => Editor.History!;
    }

    private static async Task<Opened> Open(string tierId = "dev")
    {
        var main = new MainVm(vaultAtStartup: false);
        main.Seed(Fixtures.Documents);

        var vm = main.JsonEditor!;
        vm.Tier = vm.Tiers.First(t => t.Id.Equals(tierId, StringComparison.OrdinalIgnoreCase));
        vm.ExpandAllCommand.Execute(null);

        // A plain business string outside any array, so the derived versions are easy to reason about.
        var leaf = vm.Tier.Flat.Leaves.Values.First(l =>
            !l.Path.Contains('[') && l.Class == ValueClass.Business && l.Kind == JsonValueKind.String &&
            vm.Nodes.Any(n => n.Path == l.Path));

        var reader = new DerivedHistoryReader(vm.Tier, leaf.Path, Fixtures.Flattener);
        vm.HistoryReader = reader;

        vm.ShowHistoryCommand.Execute(vm.Nodes.First(n => n.Path == leaf.Path));
        await vm.History!.Loading;

        return new Opened(vm, reader, leaf.Path);
    }

    [Fact]
    public async Task Opening_reads_the_list_and_each_version_once_and_shows_only_the_changes()
    {
        var o = await Open();

        Assert.Equal(1 + 3, o.Reader.Queries);
        Assert.Equal(o.Path, o.Editor.SelectedNode!.Path);
        Assert.Equal([o.Reader.Newest, o.Reader.Newest - 1], o.History.Rows.Select(r => r.Version));
        Assert.Equal(["changed", "added"], o.History.Rows.Select(r => r.ChangeText));
        Assert.True(o.History.Rows[0].InVaultNow);
        Assert.False(o.History.Rows[1].InVaultNow);
        Assert.Contains($"no longer keeps v1–v{o.Reader.Newest - 3}", o.History.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_newest_older_version_is_picked_and_diffed_against_the_editor()
    {
        var o = await Open();

        Assert.Equal(o.Reader.Newest - 1, o.History.Selected!.Version);
        Assert.Contains(o.History.Lines, l => l.LeftText.Contains("old-value", StringComparison.Ordinal));
        Assert.StartsWith($"V{o.Reader.Newest - 1}", o.History.DiffHeading, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_puts_the_old_value_back_as_an_unpushed_edit()
    {
        var o = await Open();
        Assert.True(o.History.CanRestore);

        o.History.RestoreCommand.Execute(null);

        Assert.Equal("\"old-value\"", o.Editor.Editor!.TextAt(o.Path));
        Assert.True(o.Editor.IsModified);
        Assert.Contains("Nothing has been written yet", o.History.Note, StringComparison.Ordinal);
        Assert.False(o.History.CanRestore);
        Assert.Equal("Identical.", o.History.DiffSummary);
        Assert.Equal(o.Path, o.Editor.SelectedNode!.Path);
    }

    [Fact]
    public async Task Restoring_a_version_without_the_node_removes_it()
    {
        var o = await Open();
        o.History.ShowAll = true;
        o.History.Selected = o.History.Rows.Single(r => r.Version == o.Reader.Newest - 2);

        Assert.StartsWith("Remove (absent in", o.History.RestoreLabel, StringComparison.Ordinal);
        o.History.RestoreCommand.Execute(null);

        Assert.False(o.Editor.Editor!.Holds(o.Path));
    }

    [Fact]
    public async Task The_panel_follows_the_tree_without_asking_Vault_again()
    {
        var o = await Open();
        var before = o.Reader.Queries;
        var other = o.Editor.Nodes.First(n => n.Path.Length > 0 && n.Path != o.Path && !n.IsContainer);

        o.Editor.SelectedNode = other;

        Assert.Equal(other.Path, o.History.Path);
        Assert.Equal(before, o.Reader.Queries);
    }

    [Fact]
    public async Task Closing_drops_the_versions_and_reopening_reads_Vault_again()
    {
        var o = await Open();
        var before = o.Reader.Queries;

        o.Editor.CloseHistoryCommand.Execute(null);
        Assert.Null(o.Editor.History);
        Assert.False(o.Editor.IsHistoryOpen);

        o.Editor.ShowHistoryCommand.Execute(null);
        await o.Editor.History!.Loading;

        Assert.Equal(before * 2, o.Reader.Queries);
    }

    [Fact]
    public async Task The_whole_document_can_be_diffed_but_not_restored()
    {
        var o = await Open();

        o.History.WholeDocument = true;

        Assert.Equal(string.Empty, o.History.Path);
        Assert.NotEmpty(o.History.Rows);
        Assert.False(o.History.CanRestore);
    }

    [Fact]
    public async Task Compared_with_previous_the_newest_version_shows_what_it_changed()
    {
        var o = await Open();
        o.History.Selected = o.History.Rows.First();

        o.History.ComparePrevious = true;

        Assert.Equal($"V{o.Reader.Newest - 1}", o.History.LeftHeader);
        Assert.Equal($"V{o.Reader.Newest}", o.History.RightHeader);
        Assert.Contains(o.History.Lines, l => l.LeftText.Contains("old-value", StringComparison.Ordinal));
    }

    /// <summary>
    /// The diff takes the editor pane while the panel is open, so the pane's other modes step aside
    /// and the header names the diff; turning the comparison on gives the pane back by closing it.
    /// </summary>
    [Fact]
    public async Task The_history_diff_takes_the_pane_and_the_comparison_takes_it_back()
    {
        var o = await Open();

        Assert.False(o.Editor.ShowingEditorText);
        Assert.Contains(o.Path, o.Editor.PaneHeader, StringComparison.Ordinal);
        Assert.StartsWith($"V{o.Reader.Newest - 1}", o.Editor.PaneHeader, StringComparison.Ordinal);

        o.Editor.ShowingComparison = true;

        Assert.Null(o.Editor.History);
    }

    [Fact]
    public async Task The_whole_document_diff_shows_only_changed_lines()
    {
        var o = await Open();

        o.History.WholeDocument = true;

        Assert.NotEmpty(o.History.Lines);
        Assert.DoesNotContain(o.History.Lines, l => l.Type == DiffPlex.DiffBuilder.Model.ChangeType.Unchanged);
    }

    [Fact]
    public async Task Switching_tier_closes_the_panel()
    {
        var o = await Open();

        o.Editor.Tier = o.Editor.Tiers.First(t => !ReferenceEquals(t, o.Editor.Tier));

        Assert.Null(o.Editor.History);
    }

    [Fact]
    public async Task Copy_value_copies_the_chosen_version()
    {
        var clipboard = new Capturing();
        JsonInsight.Platform.Platform.Clipboard = clipboard;

        try
        {
            var o = await Open();

            o.History.CopyCommand.Execute(null);

            Assert.Equal("\"old-value\"", clipboard.Text);
        }
        finally
        {
            JsonInsight.Platform.Platform.Reset();
        }
    }

    [Fact]
    public async Task Arrow_keys_skip_nothing_and_stop_at_the_ends()
    {
        var o = await Open();
        o.History.Selected = o.History.Rows[0];

        o.History.MoveCommand.Execute(1);
        Assert.Same(o.History.Rows[1], o.History.Selected);

        o.History.MoveCommand.Execute(1);
        Assert.Same(o.History.Rows[1], o.History.Selected);

        o.History.MoveCommand.Execute(-1);
        Assert.Same(o.History.Rows[0], o.History.Selected);
    }

    private sealed class Capturing : JsonInsight.Platform.IClipboard
    {
        public string Text { get; private set; } = string.Empty;

        public void SetText(string text) => Text = text;
    }
}
