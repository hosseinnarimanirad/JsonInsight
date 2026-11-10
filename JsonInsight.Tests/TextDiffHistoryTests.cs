using JsonInsight.History;
using JsonInsight.Loading;
using JsonInsight.Model;
using JsonInsight.Promote;
using JsonInsight.Vault;
using JsonInsight.ViewModels;

namespace JsonInsight.Tests;

/// <summary>
/// A Vault history with no Vault behind it: v34 (in Vault now), v33 destroyed, v32. Counts every
/// query, because "nothing is read until asked" is the property these tests most need to pin.
/// </summary>
internal sealed class FakeHistoryReader(Flattener flattener) : IHistoryReader
{
    public Dictionary<int, string> Payloads { get; } = new()
    {
        [34] = """{ "PaymentSettings": { "TransactionExpiry": 3600 } }""",
        [32] = """{ "PaymentSettings": { "TransactionExpiry": 1500 } }""",
    };

    public int Lists { get; private set; }

    public int Reads { get; private set; }

    public string? Unavailable(TierDocument tier) => tier.IsFromVault ? null : "Files have no version history.";

    public Task<VaultSecretMetadata> ListAsync(TierDocument tier, CancellationToken cancellationToken)
    {
        Lists++;
        var at = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        return Task.FromResult(new VaultSecretMetadata(
            "kv/app/stage", "https://vault.test", 34, 32, 10,
            [
                new VaultVersionInfo(34, at.AddDays(2), null, false),
                new VaultVersionInfo(33, at.AddDays(1), null, Destroyed: true),
                new VaultVersionInfo(32, at, null, false),
            ]));
    }

    public Task<SecretVersion> ReadAsync(TierDocument tier, int version, CancellationToken cancellationToken)
    {
        Reads++;
        var root = OrdinalJsonWriter.Parse(Payloads[version]);
        return Task.FromResult(new SecretVersion(
            version, null, root, flattener.Flatten(tier.Id, root), "kv/app/stage", "https://vault.test"));
    }
}

/// <summary>
/// The Text diff tab's version pickers. In the sample-files collection because the copy test swaps
/// the static <c>Platform.Clipboard</c>, as <c>EditorPaneTests</c> and <c>ArrayNodeTests</c> do.
/// </summary>
[Collection("sample-files")]
public sealed class TextDiffHistoryTests
{
    private static readonly SampleFiles Fixtures = new();

    private static (RawDiffVm Vm, FakeHistoryReader Reader) Open()
    {
        var reader = new FakeHistoryReader(Fixtures.Flattener);
        var stage = Fixtures.AsTier("stage", 34, """{ "PaymentSettings": { "TransactionExpiry": 3600 } }""");
        var dev = Fixtures.AsTier("dev", 25, """{ "PaymentSettings": { "TransactionExpiry": 3600 } }""");
        return (new RawDiffVm([stage, dev], reader), reader);
    }

    private static async Task Pick(DiffSideVm side, Func<VersionChoice, bool> which)
    {
        side.Selected = side.Versions.First(which);
        await side.Pending;
    }

    [Fact]
    public void Nothing_is_asked_of_Vault_until_earlier_versions_are_picked()
    {
        var (vm, reader) = Open();

        vm.Left = vm.Documents[1];
        vm.Left = vm.Documents[0];

        Assert.Equal(0, reader.Lists + reader.Reads);
        Assert.Equal(["Current (v34)", "Earlier versions…"], vm.LeftSide.Versions.Select(v => v.Label));
        Assert.True(vm.LeftSide.Selected!.IsCurrent);
    }

    [Fact]
    public async Task Picking_earlier_versions_lists_them_and_stays_on_current()
    {
        var (vm, reader) = Open();

        await Pick(vm.LeftSide, v => v.IsLoadRequest);

        Assert.Equal(1, reader.Lists);
        Assert.Equal(0, reader.Reads);
        Assert.True(vm.LeftSide.Selected!.IsCurrent);
        Assert.Equal([null, 34, 33, 32], vm.LeftSide.Versions.Select(v => v.Version));
        Assert.EndsWith("in Vault now", vm.LeftSide.Versions[1].Label, StringComparison.Ordinal);
        Assert.False(vm.LeftSide.Versions[2].IsEnabled);
        Assert.Contains("no longer keeps v1–v31", vm.LeftSide.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_old_version_is_diffed_against_the_other_side_and_named_as_old()
    {
        var (vm, reader) = Open();
        await Pick(vm.LeftSide, v => v.IsLoadRequest);

        await Pick(vm.LeftSide, v => v.Version == 32);

        Assert.Equal(1, reader.Reads);
        Assert.Equal("AS STAGE V32", vm.LeftSide.Header);
        Assert.StartsWith("stage v32 → dev:", vm.Summary, StringComparison.Ordinal);
        Assert.Contains(vm.Lines, l => l.LeftText.Contains("1500", StringComparison.Ordinal) &&
                                       l.RightText.Contains("3600", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Going_back_to_current_drops_the_old_version()
    {
        var (vm, _) = Open();
        await Pick(vm.LeftSide, v => v.IsLoadRequest);
        await Pick(vm.LeftSide, v => v.Version == 32);

        await Pick(vm.LeftSide, v => v.IsCurrent);

        Assert.Null(vm.LeftSide.Old);
        Assert.Equal("AS STAGE", vm.LeftSide.Header);
        Assert.Empty(vm.Lines);
    }

    [Fact]
    public async Task A_destroyed_version_is_refused_with_its_reason()
    {
        var (vm, reader) = Open();
        await Pick(vm.LeftSide, v => v.IsLoadRequest);

        await Pick(vm.LeftSide, v => v.Version == 33);

        Assert.Equal(0, reader.Reads);
        Assert.True(vm.LeftSide.Selected!.IsCurrent);
        Assert.Contains("destroyed", vm.LeftSide.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Picking_another_source_forgets_the_history()
    {
        var (vm, _) = Open();
        await Pick(vm.LeftSide, v => v.IsLoadRequest);
        await Pick(vm.LeftSide, v => v.Version == 32);

        vm.Left = vm.Documents[1];

        Assert.Null(vm.LeftSide.Old);
        Assert.Equal(2, vm.LeftSide.Versions.Count);
        Assert.Null(vm.LeftSide.Status);
    }

    [Fact]
    public async Task Swap_carries_the_version_with_its_side()
    {
        var (vm, _) = Open();
        await Pick(vm.LeftSide, v => v.IsLoadRequest);
        await Pick(vm.LeftSide, v => v.Version == 32);

        vm.SwapCommand.Execute(null);

        Assert.Equal("stage v32", vm.RightSide.Name);
        Assert.Equal("dev", vm.LeftSide.Name);
        Assert.StartsWith("dev → stage v32:", vm.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_local_file_offers_no_earlier_versions()
    {
        var reader = new FakeHistoryReader(Fixtures.Flattener);
        var file = new TierDocument
        {
            Definition = new TierDefinition { Id = "dev", Label = "dev", Kind = Sources.SourceKind.LocalFile },
            Root = OrdinalJsonWriter.Parse("{}"),
            Flat = new FlatConfig("dev", []),
            Origin = TierOrigin.LocalFile,
        };

        var vm = new RawDiffVm([file, file], reader);

        Assert.DoesNotContain(vm.LeftSide.Versions, v => v.IsLoadRequest);
    }

    [Fact]
    public async Task Copy_puts_the_version_on_screen_on_the_clipboard()
    {
        var clipboard = new Capturing();
        JsonInsight.Platform.Platform.Clipboard = clipboard;

        try
        {
            var (vm, _) = Open();
            await Pick(vm.LeftSide, v => v.IsLoadRequest);
            await Pick(vm.LeftSide, v => v.Version == 32);

            vm.CopyCommand.Execute("left");

            Assert.Contains("1500", clipboard.Text, StringComparison.Ordinal);
            Assert.Equal("Copied stage v32.", vm.CopyNote);
        }
        finally
        {
            JsonInsight.Platform.Platform.Reset();
        }
    }

    private sealed class Capturing : JsonInsight.Platform.IClipboard
    {
        public string Text { get; private set; } = string.Empty;

        public void SetText(string text) => Text = text;
    }
}
