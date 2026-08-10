using JsonInsight.Classify;
using JsonInsight.Loading;
using JsonInsight.Model;
using JsonInsight.Sources;
using JsonInsight.Vault;
using JsonInsight.ViewModels;

namespace JsonInsight.Tests;

/// <summary>
/// The Sources tab, which is what actually writes <see cref="VaultSettings.ActiveSources"/> — until it
/// existed, <see cref="SourceCatalog"/> was a rule with nothing on the other end of it.
///
/// <para>
/// Nothing here saves. <see cref="VaultSettingsStore.Save"/> writes the developer's real
/// appsettings.json and user secrets, so these drive <see cref="VaultVm.BuildSettings"/> — the same
/// object Save is handed — and stop there. For the same reason every assertion names the rows it
/// cares about rather than counting them: the tab is built partly from whatever settings this machine
/// happens to hold, and a test that assumed an empty one would pass or fail by accident.
/// </para>
/// </summary>
public sealed class SourcesTabTests
{
    private static VaultVm NewTab()
    {
        // Not seeded: this tab is settings, and the only thing it asks the main view model is which
        // document is on screen — which is the appsettings root until something changes it.
        var main = new MainVm(vaultAtStartup: false);
        var tab = new VaultVm(main, new Flattener(ArrayStrategies.Load(), Classifier.Load()));

        // Whatever this machine's settings — or tiers.json — put on screen is not this suite's
        // subject. Each test ticks what it means to test.
        foreach (var row in tab.Connections)
        {
            row.Active = false;
        }

        return tab;
    }

    private static VaultConnectionVm Row(VaultVm tab, string id) =>
        tab.Connections.Single(c => c.TierId.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// With no project open the tab reads nothing — not appsettings.json, not user secrets, and
    /// certainly not Vault.
    ///
    /// <para>
    /// The workspace still names whichever project was open last, so the tempting thing is to show
    /// that one. It would mean the app going to user secrets on launch to fill a tab nobody has asked
    /// for, and showing one project's servers and tokens while the projects list is what is on screen.
    /// The rows are here so the tab has its shape; what goes in them arrives with a project.
    /// </para>
    /// </summary>
    [Fact]
    public void With_no_project_open_the_tab_shows_empty_rows_rather_than_the_last_projects()
    {
        // NewTab builds a MainVm that stops on the projects list, which is what the app does.
        var tab = NewTab();

        Assert.NotEmpty(tab.Connections);
        Assert.All(tab.Connections, row => Assert.True(row.IsEmpty));
        Assert.All(tab.Connections, row => Assert.Empty(row.SecretPath));
        Assert.All(tab.Connections, row => Assert.Empty(row.Address));
        Assert.All(tab.Connections, row => Assert.Empty(row.Token));

        Assert.Empty(tab.BuildSettings().Connections);
        Assert.Contains("No project is open", tab.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_predefined_environment_gets_a_row_whether_or_not_it_is_configured()
    {
        var tab = NewTab();

        foreach (var environment in SourceEnvironmentExtensions.All)
        {
            Assert.Contains(tab.Connections, c => c.TierId == environment.Id());
        }
    }

    /// <summary>An id, a label and a rule about comparison, which are three different things.</summary>
    [Theory]
    [InlineData("test-qa", "test/qa", true)]
    [InlineData("stage", "stage", true)]
    [InlineData("something-someone-added-by-hand", "something-someone-added-by-hand", false)]
    public void A_row_can_be_compared_only_when_its_id_is_a_predefined_environment(
        string id, string label, bool canActivate)
    {
        var row = new VaultConnectionVm(id, new VaultConnection());

        Assert.Equal(label, row.Label);
        Assert.Equal(canActivate, row.CanActivate);
    }

    [Fact]
    public void A_fifth_active_source_is_refused_where_it_is_ticked_rather_than_dropped_later()
    {
        var tab = NewTab();

        foreach (var id in new[] { "dev", "test-qa", "stage", "prod" })
        {
            Row(tab, id).Active = true;
        }

        var fifth = Row(tab, "beta");
        fifth.Active = true;

        Assert.False(fifth.Active);
        Assert.Contains("already active", tab.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SourceCatalog.MaxActive, tab.BuildSettings().ActiveSources.Count);
    }

    [Fact]
    public void The_active_set_persists_in_environment_order_not_the_order_it_was_ticked()
    {
        var tab = NewTab();

        Row(tab, "beta").Active = true;
        Row(tab, "dev").Active = true;

        Assert.Equal(["dev", "beta"], tab.BuildSettings().ActiveSources);
    }

    /// <summary>
    /// The gate this whole feature ships behind: a tab nobody has ticked anything on must leave
    /// <c>tiers.json</c> in charge, so installing this build changes nothing until it is used.
    /// </summary>
    [Fact]
    public void Ticking_nothing_leaves_tiers_json_authoritative()
    {
        var tab = NewTab();
        var seed = new TiersConfig { Tiers = [] };

        var settings = tab.BuildSettings();
        Assert.Empty(settings.ActiveSources);

        var (config, problems) = SourceCatalog.Build(settings, seed);

        Assert.Same(seed, config);
        Assert.Empty(problems);
    }

    /// <summary>
    /// A row is a whole path now, so it carries its own address and token too. What the tab saves for
    /// one row must be exactly what was typed into that row - there is no shared default left for a
    /// blank field to fall back to, and a row that quietly borrowed another's server would be the
    /// thing this arrangement exists to prevent.
    /// </summary>
    [Fact]
    public void A_vault_row_saves_the_whole_path_and_its_own_credentials()
    {
        var tab = NewTab();

        var row = Row(tab, "stage");
        row.Kind = SourceKind.Vault;
        row.SecretPath = "kv/app/stage/resources/config/ui.json";
        row.Address = "https://vault.example.com";
        row.Namespace = "team";
        row.AllowInsecureTls = true;
        row.Active = true;

        // Every field stated, including the two asserted as unset below: this row exists on the
        // developer's real machine too — with Insecure TLS on — and inheriting that would make the
        // test pass or fail by accident rather than by what BuildSettings did.
        var other = Row(tab, "beta");
        other.Kind = SourceKind.Vault;
        other.SecretPath = "kv/app/beta/resources/config/ui.json";
        other.Address = "https://beta-vault.example.com";
        other.Namespace = string.Empty;
        other.AllowInsecureTls = false;
        other.Active = true;

        var saved = tab.BuildSettings().Connections;

        Assert.Equal("kv/app/stage/resources/config/ui.json", saved["stage"].SecretPath);
        Assert.Equal("https://vault.example.com", saved["stage"].Address);
        Assert.Equal("team", saved["stage"].Namespace);
        Assert.True(saved["stage"].AllowInsecureTls);

        // Two rows, two servers, neither borrowing from the other.
        Assert.Equal("https://beta-vault.example.com", saved["beta"].Address);
        Assert.Empty(saved["beta"].Namespace);
        Assert.False(saved["beta"].AllowInsecureTls);
    }

    /// <summary>
    /// The picker always contains the path the row actually reads, whatever a search did or did not
    /// return: a token that cannot list a mount still reads the secret perfectly well, and a picker
    /// that dropped the current answer would make a working row look misconfigured.
    /// </summary>
    [Fact]
    public void A_configured_row_opens_with_its_own_path_already_in_the_picker()
    {
        var connection = new VaultConnection
        {
            SecretPath = "kv/app/stage/resources/config/ui.json",
        };

        var row = new VaultConnectionVm("stage", connection);

        Assert.Contains("kv/app/stage/resources/config/ui.json", row.KnownPaths);
    }

    [Fact]
    public void A_local_file_row_persists_its_kind_and_its_file()
    {
        var tab = NewTab();

        var row = Row(tab, "test-qa");
        row.Kind = SourceKind.LocalFile;
        row.LocalFilePath = @"C:\snapshots\test-qa.json";
        row.Active = true;

        var connection = tab.BuildSettings().Connections["test-qa"];

        Assert.Equal(SourceKind.LocalFile, connection.Kind);
        Assert.Equal(@"C:\snapshots\test-qa.json", connection.LocalFilePath);
    }

    /// <summary>
    /// A row is written back only once it says something, and what counts as saying something depends
    /// on the kind — a local source names a file, not a secret path.
    /// </summary>
    [Fact]
    public void A_local_file_row_counts_as_configured_once_it_names_a_file()
    {
        var row = new VaultConnectionVm("dev", new VaultConnection()) { Kind = SourceKind.LocalFile };
        Assert.True(row.IsEmpty);

        row.LocalFilePath = @"C:\snapshots\dev.json";
        Assert.False(row.IsEmpty);

        // The same row as a Vault source is empty again: it has no secret path, and its file is not
        // one. Switching kind changes which field is the answer, not just which one is on screen.
        row.Kind = SourceKind.Vault;
        Assert.True(row.IsEmpty);
    }

    /// <summary>
    /// A local file is one document, so it is written down as a source but excluded from any
    /// comparison that has moved off the appsettings root — the rule <see cref="DocumentTiers"/>
    /// enforces, reached the way the tab actually reaches it.
    /// </summary>
    [Fact]
    public void A_ticked_local_file_source_survives_the_catalog_but_not_a_non_root_document()
    {
        var tab = NewTab();

        Row(tab, "dev").Kind = SourceKind.LocalFile;
        Row(tab, "dev").LocalFilePath = @"C:\snapshots\dev.json";
        Row(tab, "dev").Active = true;

        Row(tab, "stage").Kind = SourceKind.Vault;
        Row(tab, "stage").SecretPath = "kv/app/stage";
        Row(tab, "stage").Active = true;

        var settings = tab.BuildSettings();
        var (catalog, catalogProblems) = SourceCatalog.Build(settings, new TiersConfig { Tiers = [] });

        Assert.Empty(catalogProblems);
        Assert.Equal(2, catalog.Tiers.Count);

        var (atRoot, _) = DocumentTiers.For(catalog, settings, ConfigDocument.Root);
        Assert.Equal(2, atRoot.Tiers.Count);

        var (elsewhere, problems) = DocumentTiers.For(
            catalog, settings, ConfigDocument.Parse("resources/config/features.json"));

        Assert.DoesNotContain(elsewhere.Tiers, t => t.Id == "dev");
        Assert.Contains(problems, p => p.Contains("local file", StringComparison.Ordinal));
    }
}
