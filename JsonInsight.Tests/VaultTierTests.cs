using JsonInsight.Loading;
using JsonInsight.Model;
using JsonInsight.Promote;
using JsonInsight.Vault;

namespace JsonInsight.Tests;

/// <summary>
/// The Vault path, exercised without a network: a payload is handed straight to the loader, which is
/// the same object a live read hands it to. That is the whole of loading now — a tier is a secret —
/// so what these pin is what a tier becomes on the way in, and what happens to one that cannot be
/// read at all.
/// </summary>
[Collection("sample-files")]
public sealed class VaultTierTests(SampleFiles files)
{
    private static VaultReadResult Pulled(string json, int version) =>
        new(json, version, DateTimeOffset.UtcNow, "kv/app/test", "https://vault.test");

    private TierDocument BuildFromVault(string tierId, int version) =>
        new VaultTierLoader(files.Flattener)
            .Build(files[tierId].Definition, Pulled(files[tierId].Root.ToJsonString(), version));

    [Fact]
    public void A_tier_carries_its_provenance_and_canonical_content()
    {
        var beta = BuildFromVault("beta", 8);

        Assert.Equal(TierOrigin.Vault, beta.Origin);
        Assert.True(beta.IsFromVault);
        Assert.Equal(8, beta.VaultVersion);
        Assert.Contains("Vault v08", beta.SourceLine, StringComparison.Ordinal);

        // Same leaves as the payload it was built from, and content in this app's canonical form so
        // the Text diff tab compares content rather than however the payload was uploaded.
        Assert.Equal(files.Beta.Flat.Count, beta.Flat.Count);
        Assert.Equal(
            OrdinalJsonWriter.SerializeToText(files.Beta.Root),
            OrdinalJsonWriter.SerializeToText(beta.Root));
    }

    /// <summary>
    /// A tier's own declaration is the gate, not the presence of a connection. Every tier in
    /// tiers.json names a vaultPath — a tier without one cannot be read, which is now the same thing
    /// as saying it cannot exist — so the rule is pinned against a tier built without one.
    /// </summary>
    [Fact]
    public void Only_tiers_that_name_a_vault_path_are_read_from_vault()
    {
        var settings = SampleFiles.Settings();
        var pathless = new TierDefinition { Id = "local", Label = "local" };

        Assert.False(VaultTierLoader.IsVaultBacked(pathless, settings));
        Assert.True(VaultTierLoader.IsVaultBacked(files.Tiers["beta"], settings));
        Assert.All(files.Tiers.Tiers, t => Assert.False(string.IsNullOrWhiteSpace(t.VaultPath)));
    }

    [Fact]
    public async Task A_tier_with_no_token_reports_what_is_missing_rather_than_reaching_out()
    {
        var settings = new VaultSettings
        {
            Connections =
            {
                ["beta"] = new VaultConnection
                {
                    SecretPath = "kv/x/beta",
                    Address = "https://vault.test",
                },
            },
        };

        var result = await new VaultTierLoader(files.Flattener).LoadAsync(files.Tiers["beta"], settings);

        Assert.False(result.Succeeded);
        Assert.True(result.NotConfigured);
        Assert.Contains("token", result.Problem!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The contract that replaced the fallback: a tier Vault cannot serve has no values at all. It
    /// keeps its place in the report and says why, and nothing is substituted for it — there is
    /// nothing to substitute, which is exactly the point of keeping nothing on disk.
    /// </summary>
    [Fact]
    public async Task An_unreachable_tier_is_reported_as_unavailable_and_nothing_stands_in_for_it()
    {
        var settings = new VaultSettings
        {
            Connections =
            {
                ["beta"] = new VaultConnection
                {
                    SecretPath = "kv/app/beta",

                    // Reserved for documentation, so this resolves and then fails to connect rather
                    // than reaching anything real.
                    Address = "https://192.0.2.1:8200",
                    Token = "not-a-token",
                },
            },
        };

        var onlyBeta = new TiersConfig { Tiers = [files.Tiers["beta"]] };

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var report = await new TierRefresher(files.Flattener)
            .RefreshAsync(onlyBeta, settings, cancellation.Token);

        Assert.Empty(report.Documents);
        Assert.Equal(0, report.RefreshedCount);

        var unavailable = Assert.Single(report.Unavailable);
        Assert.Equal("beta", unavailable.Id);
        Assert.False(string.IsNullOrWhiteSpace(unavailable.Reason));

        Assert.Single(report.Failures);
        Assert.Contains("unavailable", report.Summary, StringComparison.OrdinalIgnoreCase);
    }
}
