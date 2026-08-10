using System.Runtime.CompilerServices;
using JsonInsight.Vault;

namespace JsonInsight.Tests;

/// <summary>
/// Neutralises the real machine before any test runs.
///
/// <para>
/// <see cref="VaultSettingsStore.AmbientToken"/> reads <c>VAULT_TOKEN</c> and
/// <c>~/.vault-token</c>. Without this, every test that asserts a row has no token would pass on
/// a build agent and fail on the laptop of whoever had last run <c>vault login</c> - the worst
/// kind of failure, because it is invisible to the person who caused it.
/// </para>
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Initialize() => VaultSettingsStore.AmbientTokenLookup = () => null;
}

/// <summary>
/// A token that comes from the environment rather than from this app's own storage.
///
/// <para>
/// These exercise the pure overload rather than swapping the static lookup, so nothing here can
/// leak into a test running beside it.
/// </para>
/// </summary>
public sealed class AmbientTokenTests
{
    [Fact]
    public void A_row_without_a_token_of_its_own_borrows_the_ambient_one()
    {
        var connection = new VaultConnection { Address = "https://vault.test:8200" };

        Assert.Equal("hvs.ambient", connection.WithAmbientToken("hvs.ambient").Token);
    }

    [Fact]
    public void A_rows_own_token_always_wins()
    {
        var connection = new VaultConnection { Token = "hvs.mine" };

        Assert.Equal("hvs.mine", connection.WithAmbientToken("hvs.ambient").Token);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_ambient_leaves_the_row_alone(string? ambient)
    {
        var connection = new VaultConnection { Address = "https://vault.test:8200" };

        Assert.Same(connection, connection.WithAmbientToken(ambient));
        Assert.Equal(string.Empty, connection.WithAmbientToken(ambient).Token);
    }

    /// <summary>
    /// The file <c>vault login</c> writes ends with a newline often enough that not trimming it
    /// produces an <c>X-Vault-Token</c> header Vault rejects, which reads as a permissions problem.
    /// </summary>
    [Fact]
    public void Surrounding_whitespace_is_trimmed()
    {
        Assert.Equal("hvs.ambient", new VaultConnection().WithAmbientToken("  hvs.ambient\n").Token);
    }

    /// <summary>
    /// The borrowed token must land on a copy. If it reached the stored object it would be written
    /// to secrets.json on the next save, forking a credential that <c>vault</c> owns and renews
    /// into a stale duplicate this app would go on presenting.
    /// </summary>
    [Fact]
    public void Borrowing_does_not_modify_the_stored_connection()
    {
        var stored = new VaultConnection { Address = "https://vault.test:8200" };

        var borrowed = stored.WithAmbientToken("hvs.ambient");

        Assert.NotSame(stored, borrowed);
        Assert.Equal(string.Empty, stored.Token);
    }

    [Fact]
    public void Borrowing_survives_a_clone_round_trip()
    {
        var borrowed = new VaultConnection { Address = "https://vault.test:8200" }
            .WithAmbientToken("hvs.ambient");

        Assert.Equal("hvs.ambient", borrowed.Clone().Token);
    }
}

/// <summary>
/// The two cases that can only be shown through the static lookup.
///
/// <para>
/// In the <c>sample-files</c> collection deliberately: it is the collection holding every test that
/// asserts a row is missing a token, and xUnit runs collections in parallel. Sharing theirs is what
/// stops this one's temporary lookup being visible to them.
/// </para>
/// </summary>
[Collection("sample-files")]
public sealed class AmbientTokenLookupTests
{
    [Fact]
    public void Resolve_reports_only_the_address_missing_when_a_token_is_ambient()
    {
        var settings = new VaultSettings();
        settings.Connections["stage"] = new VaultConnection { SecretPath = "kv/app/stage" };

        VaultSettingsStore.AmbientTokenLookup = () => "hvs.ambient";
        try
        {
            Assert.Equal(["address"], settings.Unreachable("stage"));
        }
        finally
        {
            VaultSettingsStore.AmbientTokenLookup = () => null;
        }
    }

    [Fact]
    public void A_saved_workspace_never_carries_the_ambient_token_into_secrets()
    {
        var workspace = new VaultWorkspace();
        workspace.Projects["p"] = new JsonInsight.Sources.SourceProject
        {
            Connections = { ["stage"] = new VaultConnection { SecretPath = "kv/app/stage" } },
        };

        VaultSettingsStore.AmbientTokenLookup = () => "hvs.ambient";
        try
        {
            // Save reads the stored objects, not Resolve's copies. Nothing to write is the point.
            Assert.Equal(string.Empty, workspace.Projects["p"].Connections["stage"].Token);
            Assert.Equal("hvs.ambient", workspace.SettingsFor("p").Resolve("stage").Token);
            Assert.Equal(string.Empty, workspace.Projects["p"].Connections["stage"].Token);
        }
        finally
        {
            VaultSettingsStore.AmbientTokenLookup = () => null;
        }
    }
}
