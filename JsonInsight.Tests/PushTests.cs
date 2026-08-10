using System.Net;
using System.Text.Json;
using JsonInsight.Loading;
using JsonInsight.Model;
using JsonInsight.Promote;
using JsonInsight.Vault;

namespace JsonInsight.Tests;

/// <summary>
/// The request a push actually sends, and the answers it has to understand.
///
/// <para>
/// Nothing here touches the network — the body and the response envelope are built and read by
/// static methods for exactly that reason. A test that reached a real Vault would be worse than no
/// test at all: this is the one code path in the app that changes something outside this machine.
/// </para>
/// </summary>
public sealed class VaultWriteRequestTests
{
    [Fact]
    public void The_body_carries_the_payload_verbatim_under_a_check_and_set_version()
    {
        var payload = """{"A":{"B":1}}""";

        var body = VaultClient.BuildWriteBody(payload, 34, "kv/x");

        Assert.Equal("""{"options":{"cas":34},"data":{"A":{"B":1}}}""", body);

        // And it is a document Vault can parse, not just a string that looks like one.
        using var parsed = JsonDocument.Parse(body);
        Assert.Equal(34, parsed.RootElement.GetProperty("options").GetProperty("cas").GetInt32());
        Assert.Equal(1, parsed.RootElement.GetProperty("data").GetProperty("A").GetProperty("B").GetInt32());
    }

    /// <summary>
    /// Creating rather than replacing. Vault reads cas 0 as "only if this secret does not exist",
    /// which is the correct meaning for a document nobody has uploaded yet.
    /// </summary>
    [Fact]
    public void Version_zero_is_a_create()
    {
        Assert.Contains("\"cas\":0", VaultClient.BuildWriteBody("""{}""", 0, "kv/x"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void A_payload_that_is_not_an_object_is_refused_before_anything_is_sent(string payload)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            VaultClient.BuildWriteBody(payload, 1, "kv/app/stage"));

        Assert.Contains("has to be a JSON object", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_json_never_reaches_the_wire()
    {
        Assert.ThrowsAny<JsonException>(() => VaultClient.BuildWriteBody("{ not json", 1, "kv/x"));
    }

    [Fact]
    public void A_write_response_yields_the_new_version_and_its_creation_time()
    {
        using var document = JsonDocument.Parse("""
            {
              "data": {
                "created_time": "2026-08-05T09:30:00Z",
                "deletion_time": "",
                "destroyed": false,
                "version": 35
              }
            }
            """);

        var (version, created) = VaultClient.ParseWriteEnvelope(document.RootElement, "kv/x");

        Assert.Equal(35, version);
        Assert.Equal(new DateTimeOffset(2026, 8, 5, 9, 30, 0, TimeSpan.Zero), created);
    }

    /// <summary>
    /// A read response must not pass as proof that a write landed. The two envelopes are different
    /// shapes — a read nests data.data, a write returns the metadata alone — and accepting either
    /// here would turn "I read it back" into "I wrote it".
    /// </summary>
    [Fact]
    public void A_read_shaped_response_is_not_accepted_as_a_write_result()
    {
        using var document = JsonDocument.Parse("""
            { "data": { "data": { "A": 1 }, "metadata": { "version": 35 } } }
            """);

        Assert.Throws<InvalidOperationException>(() =>
            VaultClient.ParseWriteEnvelope(document.RootElement, "kv/x"));
    }

    [Fact]
    public void An_error_body_throws_with_the_error_text()
    {
        using var document = JsonDocument.Parse("""{ "errors": ["permission denied"] }""");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            VaultClient.ParseWriteEnvelope(document.RootElement, "kv/x"));

        Assert.Contains("permission denied", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The check-and-set refusal is the one failure that is not a fault, so it is recognised rather
    /// than reported as a generic 400 — the answer is to look at the new version, not to retry.
    /// </summary>
    [Fact]
    public void A_check_and_set_refusal_is_recognised_and_a_plain_bad_request_is_not()
    {
        Assert.True(VaultClient.IsVersionConflict(
            HttpStatusCode.BadRequest,
            """{"errors":["check-and-set parameter did not match the current version"]}"""));

        Assert.False(VaultClient.IsVersionConflict(
            HttpStatusCode.BadRequest, """{"errors":["missing client token"]}"""));

        Assert.False(VaultClient.IsVersionConflict(
            HttpStatusCode.Forbidden,
            """{"errors":["check-and-set parameter did not match the current version"]}"""));
    }
}

[Collection("sample-files")]
public sealed class PushGateTests(SampleFiles files)
{
    private static VaultSettings Configured() => SampleFiles.Settings();

    /// <summary>
    /// A tier marked read-only is refused before anything is read, and this is the operation where
    /// that refusal matters most: it is the only one that changes something, and there is no backup
    /// folder on the other end.
    /// </summary>
    [Fact]
    public void A_read_only_tier_cannot_be_pushed()
    {
        var blocked = VaultPusher.Blocked(files.ReadOnly("stage"), Configured());

        Assert.NotNull(blocked);
        Assert.Contains("read-only", blocked, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tier_with_no_vault_path_has_nowhere_to_push_to()
    {
        var blocked = VaultPusher.Blocked(WithoutVaultPath(files.Stage), Configured());

        Assert.NotNull(blocked);
        Assert.Contains("no vaultPath", blocked, StringComparison.Ordinal);
    }

    /// <summary>What is missing is named, rather than reported as "not configured".</summary>
    [Fact]
    public void An_unusable_connection_names_what_it_is_short_of()
    {
        var blocked = VaultPusher.Blocked(files.Stage, new VaultSettings());

        Assert.NotNull(blocked);
        Assert.Contains("address", blocked, StringComparison.Ordinal);
        Assert.Contains("token", blocked, StringComparison.Ordinal);
    }

    [Fact]
    public void A_writable_tier_with_a_path_and_a_connection_is_allowed_through()
    {
        Assert.Null(VaultPusher.Blocked(files.Stage, Configured()));
    }

    /// <summary>
    /// The payload is the canonical serialization of the document being pushed, verified by
    /// re-parsing it: the leaf set that comes back off the text has to be exactly the leaf set the
    /// tree holds, which catches a serializer that dropped or invented a key.
    /// </summary>
    [Fact]
    public void The_payload_is_the_canonical_document_and_holds_exactly_the_tiers_keys()
    {
        var (text, problem) = new VaultPusher(files.Flattener).Payload("stage", files.Stage.Root);

        Assert.Null(problem);
        Assert.NotNull(text);
        Assert.Equal(OrdinalJsonWriter.SerializeToText(files.Stage.Root), text);

        var reparsed = files.Flattener.Flatten("stage", OrdinalJsonWriter.Parse(text!));
        Assert.Equal(
            files.Stage.Flat.Paths.Order(StringComparer.Ordinal),
            reparsed.Paths.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Pushing the appsettings root and pushing a document under it must not use the same secret.
    /// The path comes from the tier's own definition, which is what DocumentTiers derives per
    /// document, so this pins that a push follows the document the app is showing.
    /// </summary>
    [Fact]
    public void The_secret_pushed_to_is_the_tiers_own_vault_path()
    {
        var document = new ConfigDocument("resources/config/features.json");
        var (tiers, _) = DocumentTiers.For(files.Tiers, WithRoots(), document);

        var stage = tiers.Tiers.Single(t => t.Id.Equals("stage", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("kv/app/stage/resources/config/features.json", stage.VaultPath);
    }

    private static VaultSettings WithRoots()
    {
        var settings = Configured();
        foreach (var tier in new[] { "dev", "stage", "beta", "prod" })
        {
            settings.Connections[tier] = new VaultConnection { SecretPath = $"kv/app/{tier}" };
        }

        return settings;
    }

    private static TierDocument WithoutVaultPath(TierDocument document) => new()
    {
        Definition = new TierDefinition
        {
            Id = document.Id,
            Label = document.Label,
            Writable = true,
            VaultPath = null,
        },
        Root = document.Root,
        Flat = document.Flat,
    };
}

/// <summary>The plan is what the dialog reads and what the write is built from, so its claims are pinned.</summary>
public sealed class PushPlanTests
{
    private static PushPlan Plan(string live, string payload, int liveVersion = 34, int? baseVersion = 34) =>
        new("stage", "https://vault.test:8200", "kv/app/stage",
            liveVersion, null, live, payload, baseVersion, "3 queued key change(s)", []);

    [Fact]
    public void A_payload_vault_already_holds_is_identical_and_not_worth_a_version()
    {
        Assert.True(Plan("{}", "{}").Identical);
        Assert.False(Plan("{}", """{"A":1}""").Identical);
    }

    /// <summary>
    /// Somebody uploaded after this copy was taken. The push would still succeed — the check-and-set
    /// is against the live version — so this is a statement rather than a gate, and the dialog says
    /// it out loud.
    /// </summary>
    [Fact]
    public void A_base_behind_the_live_version_is_reported_rather_than_assumed_away()
    {
        Assert.True(Plan("{}", """{"A":1}""", liveVersion: 34, baseVersion: 34).BaseMatchesLive);
        Assert.False(Plan("{}", """{"A":1}""", liveVersion: 36, baseVersion: 34).BaseMatchesLive);

        // A tier that never came from Vault has nothing to be behind.
        Assert.True(Plan("{}", """{"A":1}""", liveVersion: 36, baseVersion: null).BaseMatchesLive);
    }

    [Fact]
    public void The_destination_names_the_server_the_secret_and_both_versions()
    {
        var destination = Plan("{}", """{"A":1}""").Destination;

        Assert.Contains("https://vault.test:8200", destination, StringComparison.Ordinal);
        Assert.Contains("kv/app/stage", destination, StringComparison.Ordinal);
        Assert.Contains("v34 → v35", destination, StringComparison.Ordinal);
    }
}
