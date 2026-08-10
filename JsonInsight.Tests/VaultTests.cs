using System.Text.Json;
using JsonInsight.Vault;

namespace JsonInsight.Tests;

public sealed class VaultPathTests
{
    [Theory]
    [InlineData("kv/app/stage", "kv", "app/stage")]
    [InlineData("/kv/x/", "kv", "x")]
    [InlineData("  kv/deep/nested/path  ", "kv", "deep/nested/path")]
    public void Mount_and_path_split_on_the_first_slash(string full, string mount, string path)
    {
        var (m, p) = VaultClient.ParseMountAndPath(full);

        Assert.Equal(mount, m);
        Assert.Equal(path, p);
    }

    [Theory]
    [InlineData("")]
    [InlineData("kv")]
    [InlineData("kv/")]
    [InlineData("/stage")]
    public void A_path_without_both_halves_is_rejected(string full)
    {
        Assert.Throws<InvalidOperationException>(() => VaultClient.ParseMountAndPath(full));
    }

    /// <summary>
    /// Listing accepts a bare mount, because the top of a mount is where browsing a Vault starts.
    /// Reading must not — a mount root holds no secret — which is what the theory above pins, and
    /// the two must not drift into each other.
    /// </summary>
    [Theory]
    [InlineData("kv", "kv", "")]
    [InlineData("/kv/", "kv", "")]
    [InlineData("kv/app", "kv", "app")]
    public void Listing_accepts_a_mount_with_nothing_under_it(string full, string mount, string path)
    {
        var (m, p) = VaultClient.ParseMountAndOptionalPath(full);

        Assert.Equal(mount, m);
        Assert.Equal(path, p);
    }

    [Fact]
    public void Nothing_at_all_is_still_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => VaultClient.ParseMountAndOptionalPath("  "));
    }
}

public sealed class VaultEnvelopeTests
{
    private static (JsonElement Data, int Version, DateTimeOffset? Created) Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        // The element must be consumed before the document is disposed, so clone what leaves.
        var (data, version, created) = VaultClient.ParseEnvelope(document.RootElement, "test");
        return (data.Clone(), version, created);
    }

    [Fact]
    public void A_kv2_envelope_yields_payload_version_and_creation_time()
    {
        var (data, version, created) = Parse("""
            {
              "data": {
                "data": { "PaymentSettings": { "Enabled": true } },
                "metadata": { "version": 34, "created_time": "2026-07-28T10:00:00Z" }
              }
            }
            """);

        Assert.Equal(JsonValueKind.Object, data.ValueKind);
        Assert.True(data.TryGetProperty("PaymentSettings", out _));
        Assert.Equal(34, version);
        Assert.Equal(new DateTimeOffset(2026, 7, 28, 10, 0, 0, TimeSpan.Zero), created);
    }

    [Fact]
    public void A_vault_error_body_throws_with_the_error_text()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Parse("""{ "errors": ["permission denied"] }"""));

        Assert.Contains("permission denied", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_kv1_shaped_response_is_rejected()
    {
        // KV v1 puts the payload directly under "data" with no second nesting level.
        Assert.Throws<InvalidOperationException>(() =>
            Parse("""{ "data": { "PaymentSettings": {} } }"""));
    }

    [Fact]
    public void A_response_without_metadata_version_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Parse("""{ "data": { "data": {}, "metadata": {} } }"""));
    }
}

public sealed class VaultSettingsTests
{
    private static VaultSettings Settings() => new()
    {
        Connections =
        {
            ["stage"] = new VaultConnection
            {
                SecretPath = "kv/app/stage/resources/config/ui.json",
                Address = "https://vault.default:8200",
                Namespace = "root-ns",
                Token = "stage-token",
            },
            ["beta"] = new VaultConnection
            {
                SecretPath = "kv/app/beta/resources/config/ui.json",
                Address = "https://vault.beta:8200",
                Token = "beta-token",
            },
        },
    };

    /// <summary>
    /// A row carries its own credentials, and resolving one is a copy of it and nothing else. There
    /// used to be a shared address, namespace and token that a blank field fell back to; the fallback
    /// is gone, and with it the question of which of two answers a row was actually using.
    /// </summary>
    [Fact]
    public void A_row_resolves_to_exactly_what_it_carries()
    {
        var resolved = Settings().Resolve("stage");

        Assert.Equal("https://vault.default:8200", resolved.Address);
        Assert.Equal("root-ns", resolved.Namespace);
        Assert.Equal("stage-token", resolved.Token);
        Assert.Equal("kv/app/stage/resources/config/ui.json", resolved.SecretPath);
    }

    [Fact]
    public void Two_rows_can_name_different_servers()
    {
        Assert.Equal("https://vault.beta:8200", Settings().Resolve("beta").Address);
    }

    /// <summary>Resolving is a copy: filling in a blank for a probe must not edit the settings.</summary>
    [Fact]
    public void Resolving_hands_back_a_copy_rather_than_the_stored_row()
    {
        var settings = Settings();
        settings.Resolve("stage").Token = "scribbled-on";

        Assert.Equal("stage-token", settings.Connections["stage"].Token);
    }

    [Fact]
    public void A_source_with_no_row_of_its_own_is_missing_everything()
    {
        Assert.Equal(["address", "token", "secret path"], Settings().Incomplete("prod"));
    }

    [Fact]
    public void Incomplete_names_every_missing_piece()
    {
        var settings = new VaultSettings
        {
            Connections = { ["stage"] = new VaultConnection() },
        };

        Assert.Equal(["address", "token", "secret path"], settings.Incomplete("stage"));
    }

    /// <summary>
    /// The structural guarantee the settings store relies on: serializing the model - which is
    /// exactly what produces appsettings.json - must not be able to emit a token.
    /// </summary>
    [Fact]
    public void Tokens_never_survive_serialization()
    {
        var json = JsonSerializer.Serialize(Settings());

        Assert.DoesNotContain("shared-token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("beta-token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Token", json, StringComparison.Ordinal);
    }
}
