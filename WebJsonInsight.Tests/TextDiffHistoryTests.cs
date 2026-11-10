using Bunit;
using JsonInsight.Classify;
using JsonInsight.History;
using JsonInsight.Loading;
using JsonInsight.Model;
using JsonInsight.Promote;
using JsonInsight.Vault;
using JsonInsight.ViewModels;
using WebJsonInsight.Components.Tabs;

namespace WebJsonInsight.Tests;

/// <summary>
/// The Text diff tab's version pickers, rendered: the list arrives only after "Earlier versions…" is
/// picked, a destroyed version is listed but cannot be chosen, and an old version names itself in its
/// column heading.
/// </summary>
public sealed class TextDiffHistoryTests : TestContext
{
    private static readonly Flattener Flattener = new(ArrayStrategies.Load(), Classifier.Load());

    private sealed class Reader : IHistoryReader
    {
        public int Queries { get; private set; }

        public string? Unavailable(TierDocument tier) => null;

        public Task<VaultSecretMetadata> ListAsync(TierDocument tier, CancellationToken cancellationToken)
        {
            Queries++;
            return Task.FromResult(new VaultSecretMetadata(
                "kv/app/" + tier.Id, "https://vault.test", 12, 10, 10,
                [
                    new VaultVersionInfo(12, null, null, false),
                    new VaultVersionInfo(11, null, null, Destroyed: true),
                    new VaultVersionInfo(10, null, null, false),
                ]));
        }

        public Task<SecretVersion> ReadAsync(TierDocument tier, int version, CancellationToken cancellationToken)
        {
            Queries++;
            var root = OrdinalJsonWriter.Parse("""{ "Old": true }""");
            return Task.FromResult(new SecretVersion(
                version, null, root, Flattener.Flatten(tier.Id, root), "kv/app/" + tier.Id, "https://vault.test"));
        }
    }

    [Fact]
    public async Task Earlier_versions_are_listed_on_request_and_an_old_one_names_its_column()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var reader = new Reader();
        var vm = new RawDiffVm(Fixtures.Documents, reader);

        var page = RenderComponent<TextDiffTab>(p => p.Add(c => c.Vm, vm));
        var version = page.Find("#diff-left-version");

        Assert.Equal(0, reader.Queries);
        Assert.Equal(2, version.QuerySelectorAll("option").Length);

        // Index 1 is "Earlier versions…".
        version.Change("1");
        await vm.LeftSide.Pending;

        page.WaitForAssertion(() =>
        {
            var options = page.Find("#diff-left-version").QuerySelectorAll("option");
            Assert.Equal(4, options.Length);
            Assert.True(options[2].HasAttribute("disabled"), "v11 is destroyed and must not be pickable.");
        });

        page.Find("#diff-left-version").Change("3");
        await vm.LeftSide.Pending;

        page.WaitForAssertion(() =>
            Assert.Contains($"AS {vm.Left!.Id.ToUpperInvariant()} V10", page.Markup, StringComparison.Ordinal));
        Assert.Equal(2, reader.Queries);
    }
}
