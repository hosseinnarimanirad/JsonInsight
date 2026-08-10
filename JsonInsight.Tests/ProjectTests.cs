using JsonInsight.Sources;
using JsonInsight.Vault;
using JsonInsight.ViewModels;

namespace JsonInsight.Tests;

/// <summary>
/// The workspace: every project side by side, and the migration that turns a pre-projects file into
/// the first of them — folding the old app-wide document into each row's path and pushing the old
/// shared credentials down onto the rows that relied on them.
///
/// <para>
/// All in memory. <see cref="VaultWorkspace"/> is a plain object and the interesting behaviour is in
/// how it splits and rejoins — which part of a <see cref="VaultSettings"/> belongs to a project — so
/// none of this needs, or touches, the two real settings files.
/// </para>
/// </summary>
public sealed class ProjectTests
{
    private static VaultWorkspace Legacy() => new()
    {
        Address = "https://vault.example.com",
        Namespace = "team",
        Token = "shared-token",
        Document = "resources/config/features.json",
        ActiveSources = ["stage", "beta"],
        BrowseFrom = "stage",
        Documents = ["resources/config/features.json"],
        Connections = new Dictionary<string, VaultConnection>(StringComparer.OrdinalIgnoreCase)
        {
            ["stage"] = new() { SecretPath = "kv/app/stage", Token = "stage-token" },
            ["beta"] = new() { SecretPath = "kv/app/beta" },
        },
    };

    [Fact]
    public void A_pre_projects_file_becomes_one_project_and_opens_on_it()
    {
        var workspace = Legacy();

        Assert.True(workspace.Migrate());

        Assert.Equal(VaultWorkspace.MigratedProjectName, workspace.ActiveProject);
        var project = workspace.Projects[VaultWorkspace.MigratedProjectName];

        Assert.Equal(["stage", "beta"], project.ActiveSources);
        Assert.Equal(2, project.Connections.Count);
    }

    /// <summary>
    /// The old shape was a root per row plus one document appended to all of them. A row is a whole
    /// path now, so the append has to happen once during migration - otherwise every row would point
    /// at the environment secret and quietly compare the wrong document.
    /// </summary>
    [Fact]
    public void The_app_wide_document_is_folded_into_each_rows_path()
    {
        var workspace = Legacy();
        workspace.Migrate();

        var connections = workspace.Projects[VaultWorkspace.MigratedProjectName].Connections;

        Assert.Equal("kv/app/stage/resources/config/features.json", connections["stage"].SecretPath);
        Assert.Equal("kv/app/beta/resources/config/features.json", connections["beta"].SecretPath);
    }

    /// <summary>
    /// The shared address and namespace are pushed into every row that did not override them before
    /// being dropped. Removing them without this would take three rows out of four offline.
    ///
    /// <para>
    /// The shared <em>token</em> is not handled here, deliberately: it lives in user secrets, which
    /// are read after this runs, so at this point there is nothing to push down.
    /// <c>VaultSettingsStore.MergeSecrets</c> does that half once it has the value — and nulling
    /// <see cref="VaultWorkspace.Token"/> here would drop it before it ever arrived.
    /// </para>
    /// </summary>
    [Fact]
    public void The_shared_address_is_pushed_down_into_every_row_that_relied_on_it()
    {
        var workspace = Legacy();
        workspace.Migrate();

        var connections = workspace.Projects[VaultWorkspace.MigratedProjectName].Connections;

        Assert.Equal("https://vault.example.com", connections["stage"].Address);
        Assert.Equal("team", connections["stage"].Namespace);

        // A row's own token is untouched by migration.
        Assert.Equal("stage-token", connections["stage"].Token);
    }

    [Fact]
    public void The_shared_token_survives_migration_for_the_secrets_merge_to_use()
    {
        var workspace = Legacy();
        workspace.Migrate();

        Assert.Equal("shared-token", workspace.Token);
    }

    [Fact]
    public void A_row_that_overrode_the_shared_address_keeps_its_own()
    {
        var workspace = Legacy();
        workspace.Connections!["beta"].Address = "https://beta-vault.example.com";

        workspace.Migrate();

        Assert.Equal(
            "https://beta-vault.example.com",
            workspace.Projects[VaultWorkspace.MigratedProjectName].Connections["beta"].Address);
    }

    /// <summary>
    /// The legacy fields are nulled so the serializer — which drops nulls — stops writing them. Left
    /// behind, they would be read again on the next load and be a second, stale answer to every
    /// question the project already answers.
    /// </summary>
    [Fact]
    public void Migration_leaves_nothing_of_the_old_shape_to_be_written_again()
    {
        var workspace = Legacy();
        workspace.Migrate();

        Assert.Null(workspace.Connections);
        Assert.Null(workspace.Document);
        Assert.Null(workspace.ActiveSources);
        Assert.Null(workspace.Documents);
        Assert.Null(workspace.BrowseFrom);
        Assert.Null(workspace.Address);
        Assert.Null(workspace.Namespace);
    }

    /// <summary>Safe to run on every load, which is what lets it run without anything having to save.</summary>
    [Fact]
    public void Migration_is_idempotent()
    {
        var workspace = Legacy();
        workspace.Migrate();

        Assert.False(workspace.Migrate());
        Assert.Single(workspace.Projects);

        // And the second pass must not append the document a second time.
        Assert.Equal(
            "kv/app/stage/resources/config/features.json",
            workspace.Projects[VaultWorkspace.MigratedProjectName].Connections["stage"].SecretPath);
    }

    [Fact]
    public void A_file_with_projects_already_is_left_alone_even_if_it_has_legacy_leftovers()
    {
        var workspace = Legacy();
        workspace.Projects["config"] = new SourceProject();
        workspace.ActiveProject = "config";

        workspace.Migrate();

        Assert.Single(workspace.Projects);
        Assert.Equal("config", workspace.ActiveProject);
        Assert.Null(workspace.Connections);
    }

    [Fact]
    public void An_empty_file_migrates_to_nothing_rather_than_an_empty_project()
    {
        var workspace = new VaultWorkspace();

        Assert.False(workspace.Migrate());
        Assert.Empty(workspace.Projects);
        Assert.Equal(string.Empty, workspace.ActiveProject);
    }

    /// <summary>
    /// The split that makes projects work: credentials are the workspace's, everything about what is
    /// being compared is the project's.
    /// </summary>
    [Fact]
    public void A_project_is_read_back_with_the_shared_credentials_folded_in()
    {
        var workspace = Legacy();
        workspace.Migrate();

        var settings = workspace.SettingsFor(VaultWorkspace.MigratedProjectName);

        Assert.Equal(["stage", "beta"], settings.ActiveSources);
        Assert.Equal("https://vault.example.com", settings.Connections["stage"].Address);
        Assert.Equal("kv/app/stage/resources/config/features.json", settings.Connections["stage"].SecretPath);
    }

    [Fact]
    public void An_unknown_project_name_yields_empty_settings_rather_than_throwing()
    {
        var workspace = Legacy();
        workspace.Migrate();

        var settings = workspace.SettingsFor("never-created");

        Assert.Empty(settings.Connections);
        Assert.Empty(settings.ActiveSources);
    }

    /// <summary>
    /// Saving the Sources tab must not be able to disturb a project that is not open. This is the
    /// reason <see cref="VaultSettingsStore.Save"/> reads the workspace and applies to it rather than
    /// writing the settings object straight out.
    /// </summary>
    [Fact]
    public void Applying_one_project_leaves_every_other_project_untouched()
    {
        var workspace = new VaultWorkspace
        {
            Projects =
            {
                ["appsettings"] = new SourceProject
                {
                    ActiveSources = ["dev"],
                    Connections = { ["dev"] = new VaultConnection { SecretPath = "kv/app/dev" } },
                },
                ["config"] = new SourceProject(),
            },
        };

        var edited = workspace.SettingsFor("config");
        edited.ActiveSources = ["stage", "prod"];
        edited.Connections["stage"] = new VaultConnection { SecretPath = "kv/app/stage" };

        workspace.Apply("config", edited);

        Assert.Equal(["stage", "prod"], workspace.Projects["config"].ActiveSources);

        var untouched = workspace.Projects["appsettings"];
        Assert.Equal(["dev"], untouched.ActiveSources);
        Assert.Equal("kv/app/dev", untouched.Connections["dev"].SecretPath);
    }

    [Fact]
    public void Applying_an_unknown_project_creates_it()
    {
        var workspace = new VaultWorkspace();
        var settings = workspace.SettingsFor("brand-new");
        settings.Connections["stage"] = new VaultConnection { SecretPath = "kv/app/stage/ui.json" };

        workspace.Apply("brand-new", settings);

        Assert.Equal("kv/app/stage/ui.json", workspace.Projects["brand-new"].Connections["stage"].SecretPath);
    }

    /// <summary>
    /// Copying is how a second project starts, and the copy has to be able to diverge — a shared
    /// connection object would mean editing stage in one project edited it in the other.
    /// </summary>
    [Fact]
    public void A_copied_project_shares_no_state_with_the_one_it_came_from()
    {
        var original = new SourceProject
        {
            ActiveSources = ["stage"],
            Connections = { ["stage"] = new VaultConnection { SecretPath = "kv/app/stage" } },
            LastOpenedUtc = DateTimeOffset.UnixEpoch,
        };

        var copy = original.Clone();
        copy.ActiveSources.Add("prod");
        copy.Connections["stage"].SecretPath = "kv/other/stage";

        Assert.Equal(["stage"], original.ActiveSources);
        Assert.Equal("kv/app/stage", original.Connections["stage"].SecretPath);
    }

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(60 * 25, "25 minutes ago")]
    [InlineData(60 * 60 * 5, "5 hours ago")]
    [InlineData(60 * 60 * 30, "yesterday")]
    [InlineData(60 * 60 * 24 * 4, "4 days ago")]
    public void Recency_is_said_the_way_someone_would_say_it(int secondsAgo, string expected)
    {
        var when = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(secondsAgo);

        Assert.Equal(expected, ProjectsVm.Ago(when));
    }

    [Fact]
    public void A_project_nobody_has_opened_says_so_rather_than_claiming_a_date()
    {
        Assert.Equal("never opened", ProjectsVm.Ago(null));
    }
}
