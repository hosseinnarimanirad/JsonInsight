using JsonInsight.Loading;
using JsonInsight.Sources;
using JsonInsight.Vault;

namespace JsonInsight.Tests;

/// <summary>
/// The dispatch <see cref="TierRefresher"/> now does by <see cref="SourceKind"/> instead of hardcoding
/// a Vault read. <see cref="VaultSourceProvider"/> itself is exercised through the existing Vault
/// tests — this file pins the registry seam: a kind with no provider is reported rather than throwing,
/// and the default constructor still behaves exactly as the Vault-only one did.
/// </summary>
[Collection("sample-files")]
public sealed class SourceProviderTests(SampleFiles files)
{
    [Fact]
    public async Task A_tier_whose_kind_has_no_registered_provider_is_reported_not_configured()
    {
        // No SourceKind.Vault provider at all — every tier in this catalog is unreadable by
        // construction, and that has to show up as a report rather than an exception.
        var refresher = new TierRefresher(Array.Empty<ISourceProvider>());
        var settings = SampleFiles.Settings();

        var report = await refresher.RefreshAsync(files.Tiers, settings);

        Assert.Empty(report.Documents);
        Assert.Equal(files.Tiers.Tiers.Count, report.Unavailable.Count);
        Assert.All(report.Unavailable, u => Assert.Contains("no provider", u.Reason, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_default_constructor_registers_both_provider_kinds()
    {
        Assert.Equal(SourceKind.Vault, new VaultSourceProvider(files.Flattener).Kind);
        Assert.Equal(SourceKind.LocalFile, new LocalFileSourceProvider(files.Flattener).Kind);
    }
}
