using JsonInsight.Loading;
using JsonInsight.Sources;
using JsonInsight.Vault;
using JsonInsight.ViewModels;

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

    /// <summary>
    /// Everything configured is read; the cap belongs to the grid, not to the read.
    ///
    /// <para>
    /// Loading and comparing used to be one list, so ON meant both "compare this" and "read this at
    /// all" — and a fifth environment could not be opened in the Tier editor without unticking one of
    /// the four. Reading it costs one request. Only the comparison is capped, because only the grid
    /// is four columns wide.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_configured_source_is_read_and_only_the_comparison_is_capped()
    {
        var settings = new VaultSettings { ActiveSources = ["dev", "test-qa", "stage", "beta", "prod"] };

        foreach (var id in settings.ActiveSources)
        {
            settings.Connections[id] = new VaultConnection { Kind = SourceKind.Vault, SecretPath = $"kv/{id}" };
        }

        var (config, problems) = SourceCatalog.Build(settings, Seed());

        Assert.Equal(5, config.Tiers.Count);
        Assert.Empty(problems);

        // Four columns, in environment order rather than the order they were ticked.
        var compared = SourceCatalog.Compared(settings, config);
        Assert.Equal(["dev", "test-qa", "stage", "beta"], compared);
    }

    /// <summary>A source nobody ticked is still read — it is simply not one of the compared columns.</summary>
    [Fact]
    public void A_configured_source_that_is_not_ticked_is_read_but_not_compared()
    {
        var settings = new VaultSettings
        {
            ActiveSources = ["stage"],
            Connections =
            {
                ["stage"] = new VaultConnection { SecretPath = "kv/app/stage" },
                ["prod"] = new VaultConnection { SecretPath = "kv/app/prod" },
            },
        };

        var (config, problems) = SourceCatalog.Build(settings, Seed());

        Assert.Empty(problems);
        Assert.Equal(["stage", "prod"], config.Tiers.Select(t => t.Id));
        Assert.Equal(["stage"], SourceCatalog.Compared(settings, config));
    }

    /// <summary>
    /// An environment ticked ON with nothing behind it stops a read rather than being skipped.
    ///
    /// <para>
    /// It used to be dropped with a note, which meant the honest outcome — a comparison missing the
    /// column somebody had just asked for — arrived looking exactly like a successful one, three
    /// columns wide. There is no reading around it and no default to substitute.
    /// </para>
    /// </summary>
    [Fact]
    public void An_active_environment_with_no_configured_source_blocks_the_read()
    {
        var settings = new VaultSettings { ActiveSources = ["prod"] };

        var (config, problems) = SourceCatalog.Build(settings, Seed());

        Assert.Empty(config.Tiers);
        Assert.Contains(problems, p => p.Contains("prod", StringComparison.Ordinal));

        var blocked = SourceCatalog.Incomplete(settings);
        Assert.NotNull(blocked);
        Assert.Contains("prod", blocked!, StringComparison.Ordinal);
        Assert.Contains("Sources tab", blocked, StringComparison.Ordinal);
    }

    /// <summary>Nothing ticked-but-empty means nothing in the way, whether or not anything is ticked.</summary>
    [Fact]
    public void A_fully_configured_active_set_blocks_nothing()
    {
        Assert.Null(SourceCatalog.Incomplete(new VaultSettings()));

        var settings = new VaultSettings
        {
            ActiveSources = ["stage"],
            Connections = { ["stage"] = new VaultConnection { SecretPath = "kv/app/stage" } },
        };

        Assert.Null(SourceCatalog.Incomplete(settings));
    }

    /// <summary>All of them named at once, so fixing them is one trip to the Sources tab rather than three.</summary>
    [Fact]
    public void Several_unconfigured_ticks_are_named_together()
    {
        var settings = new VaultSettings { ActiveSources = ["dev", "beta", "prod"] };

        var blocked = SourceCatalog.Incomplete(settings);

        Assert.NotNull(blocked);
        Assert.Contains("dev, beta, prod", blocked!, StringComparison.Ordinal);
        Assert.Contains("are ticked ON", blocked, StringComparison.Ordinal);
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

/// <summary>
/// What the split between loading and comparing looks like from the tabs: the grid narrows to the
/// ticked columns, and every other tab keeps the whole loaded set.
/// </summary>
[Collection("sample-files")]
public sealed class ComparedSetTests(SampleFiles files)
{
    [Fact]
    public void The_grid_compares_the_ticked_set_and_the_editor_offers_everything_loaded()
    {
        var main = new MainVm(vaultAtStartup: false);

        main.Seed([files.Dev, files.Stage, files.Beta], compared: ["dev", "beta"]);

        // Two columns, in the order they were loaded rather than the order they were named.
        Assert.Equal(["dev", "beta"], main.Tiers!.Diff.TierIds);

        // Everything read is still reachable where a single tier is worked on.
        Assert.Equal(["dev", "stage", "beta"], main.JsonEditor!.Tiers.Select(t => t.Id));
        Assert.Equal(3, main.Documents.Count);
    }

    /// <summary>
    /// The pill above the grid says so, rather than reporting a read count beside a narrower grid and
    /// leaving the tier that is missing from it looking like it failed.
    /// </summary>
    [Fact]
    public void The_toolbar_says_when_more_was_read_than_is_compared()
    {
        var main = new MainVm(vaultAtStartup: false);

        main.Seed([files.Dev, files.Stage, files.Beta], compared: ["dev", "beta"]);
        Assert.Contains("2 compared here", main.Tiers!.SourceLabel, StringComparison.Ordinal);

        // And says nothing extra when the two sets are the same.
        main.Seed([files.Dev, files.Stage]);
        Assert.DoesNotContain("compared here", main.Tiers!.SourceLabel, StringComparison.Ordinal);
    }

    /// <summary>An empty compared set is "all of them" — what a pre-Sources-tab install runs in.</summary>
    [Fact]
    public void No_active_set_compares_everything_loaded()
    {
        var main = new MainVm(vaultAtStartup: false);

        main.Seed([files.Dev, files.Stage, files.Beta]);

        Assert.Equal(["dev", "stage", "beta"], main.Tiers!.Diff.TierIds);
    }

    /// <summary>Busy and blocked are separate reasons, and either one is enough to hold the button.</summary>
    [Fact]
    public void Pull_needs_a_project_open_a_read_not_in_flight_and_nothing_ticked_but_empty()
    {
        var main = new MainVm(vaultAtStartup: false);

        // No project: nothing to read, whatever the settings say.
        Assert.False(main.CanPull);

        main.Seed([files.Dev]);
        main.ActiveProject = "test";
        main.PullBlocked = null;
        Assert.True(main.CanPull);

        main.VaultBusy = true;
        Assert.False(main.CanPull);

        main.VaultBusy = false;
        main.PullBlocked = "beta is ticked ON but names no source.";
        Assert.False(main.CanPull);
    }
}
