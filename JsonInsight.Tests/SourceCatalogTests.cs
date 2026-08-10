using JsonInsight.Loading;
using JsonInsight.Sources;
using JsonInsight.Vault;

namespace JsonInsight.Tests;

/// <summary>
/// <see cref="SourceCatalog"/> is what lets the Sources tab replace <c>tiers.json</c> without ever
/// requiring it to — these pin the gate (untouched settings mean untouched behavior) and the rules
/// once a user has actually picked an active set: at most four, skipping what is not configured or
/// not a recognized environment name, rather than throwing.
/// </summary>
public sealed class SourceCatalogTests
{
    private static TiersConfig Seed() => new()
    {
        Tiers =
        [
            new TierDefinition { Id = "dev", Label = "dev", Writable = true, VaultPath = "kv/dev" },
            new TierDefinition { Id = "stage", Label = "stage", Writable = true, VaultPath = "kv/stage" },
        ],
    };

    [Fact]
    public void An_empty_active_set_leaves_the_seed_exactly_as_it_was()
    {
        var settings = new VaultSettings();
        var seed = Seed();

        var (config, problems) = SourceCatalog.Build(settings, seed);

        Assert.Same(seed, config);
        Assert.Empty(problems);
    }

    [Fact]
    public void An_active_vault_and_local_file_source_both_resolve()
    {
        var settings = new VaultSettings
        {
            ActiveSources = ["stage", "dev"],
            Connections =
            {
                ["stage"] = new VaultConnection { Kind = SourceKind.Vault, SecretPath = "kv/app/stage" },
                ["dev"] = new VaultConnection { Kind = SourceKind.LocalFile, LocalFilePath = @"C:\snapshots\dev.json" },
            },
        };

        var (config, problems) = SourceCatalog.Build(settings, Seed());

        Assert.Empty(problems);
        Assert.Equal(2, config.Tiers.Count);

        var stage = config.Tiers.Single(t => t.Id == "stage");
        Assert.Equal(SourceKind.Vault, stage.Kind);
        Assert.Equal("kv/app/stage", stage.VaultPath);
        Assert.True(stage.Writable);

        var dev = config.Tiers.Single(t => t.Id == "dev");
        Assert.Equal(SourceKind.LocalFile, dev.Kind);
        Assert.Equal(@"C:\snapshots\dev.json", dev.LocalFilePath);

        // Every configured source is writable, whichever kind it is. The only document that arrives
        // read-only is one browsed on the Compare files tab, which is not a configured source at all.
        Assert.True(dev.Writable);
    }

    [Fact]
    public void More_than_four_active_sources_are_capped_with_a_reported_reason()
    {
        var settings = new VaultSettings { ActiveSources = ["dev", "test-qa", "stage", "beta", "prod"] };

        foreach (var id in settings.ActiveSources)
        {
            settings.Connections[id] = new VaultConnection { Kind = SourceKind.Vault, SecretPath = $"kv/{id}" };
        }

        var (config, problems) = SourceCatalog.Build(settings, Seed());

        Assert.Equal(SourceCatalog.MaxActive, config.Tiers.Count);
        Assert.Contains(problems, p => p.Contains("more than the 4", StringComparison.Ordinal));
    }

    [Fact]
    public void An_active_environment_with_no_configured_source_is_skipped_and_reported()
    {
        var settings = new VaultSettings { ActiveSources = ["prod"] };

        var (config, problems) = SourceCatalog.Build(settings, Seed());

        Assert.Empty(config.Tiers);
        Assert.Contains(problems, p => p.Contains("prod", StringComparison.Ordinal) && p.Contains("no source configured", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unrecognized_active_id_is_skipped_and_reported_rather_than_throwing()
    {
        var settings = new VaultSettings { ActiveSources = ["not-a-real-environment"] };

        var (config, problems) = SourceCatalog.Build(settings, Seed());

        Assert.Empty(config.Tiers);
        Assert.Contains(problems, p => p.Contains("not-a-real-environment", StringComparison.Ordinal));
    }
}
