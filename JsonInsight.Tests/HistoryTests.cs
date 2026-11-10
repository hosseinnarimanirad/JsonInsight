using System.Text.Json;
using JsonInsight.Classify;
using JsonInsight.History;
using JsonInsight.Loading;
using JsonInsight.Model;
using JsonInsight.Promote;
using JsonInsight.Vault;

namespace JsonInsight.Tests;

public sealed class VaultMetadataTests
{
    private static VaultSecretMetadata Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return VaultClient.ParseMetadata(document.RootElement, "kv/app/stage", "https://vault.test:8200");
    }

    private const string Sample = """
        {
          "data": {
            "current_version": 3,
            "oldest_version": 1,
            "max_versions": 10,
            "versions": {
              "1": { "created_time": "2026-10-01T08:00:00.123456789Z", "deletion_time": "", "destroyed": false },
              "3": { "created_time": "2026-11-09T12:30:00Z", "deletion_time": "", "destroyed": false },
              "2": { "created_time": "2026-10-20T09:15:00Z", "deletion_time": "2026-10-21T00:00:00Z", "destroyed": true }
            }
          }
        }
        """;

    [Fact]
    public void Versions_come_back_newest_first_with_the_secret_counters()
    {
        var metadata = Parse(Sample);

        Assert.Equal([3, 2, 1], metadata.Versions.Select(v => v.Version));
        Assert.Equal(3, metadata.CurrentVersion);
        Assert.Equal(1, metadata.OldestVersion);
        Assert.Equal(10, metadata.MaxVersions);
    }

    [Fact]
    public void Vault_nanosecond_timestamps_and_empty_deletion_times_are_read()
    {
        var oldest = Parse(Sample).Versions.Single(v => v.Version == 1);

        var expected = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        Assert.NotNull(oldest.CreatedTime);
        Assert.InRange(oldest.CreatedTime.Value, expected, expected.AddSeconds(1));
        Assert.Null(oldest.DeletionTime);
        Assert.False(oldest.Destroyed);
    }

    [Fact]
    public void Destroyed_deleted_and_scheduled_versions_say_whether_they_can_be_read()
    {
        var now = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);

        var destroyed = new VaultVersionInfo(2, null, null, Destroyed: true);
        var deleted = new VaultVersionInfo(4, null, now.AddDays(-1), Destroyed: false);
        var scheduled = new VaultVersionInfo(5, null, now.AddDays(1), Destroyed: false);
        var live = new VaultVersionInfo(6, null, null, Destroyed: false);

        Assert.Equal("destroyed", destroyed.Unreadable(now));
        Assert.Equal("deleted", deleted.Unreadable(now));
        Assert.True(scheduled.Readable(now));
        Assert.True(live.Readable(now));
        Assert.Null(live.Unreadable(now));
    }

    [Fact]
    public void A_response_without_a_version_list_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => Parse("""{ "data": { "keys": ["a"] } }"""));
    }

    [Fact]
    public async Task A_version_below_one_is_refused_before_anything_is_sent()
    {
        using var client = new VaultClient(new VaultConnection
        {
            Address = "https://vault.test:8200",
            Token = "test-token",
        });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ReadVersionAsync("kv/app/stage", 0));
    }
}

public sealed class NodeHistoryTests
{
    private static readonly Flattener Flattener = new(ArrayStrategies.Load(), Classifier.Load());

    private static SecretVersion Version(int number, string json)
    {
        var root = OrdinalJsonWriter.Parse(json);
        return new SecretVersion(
            number,
            new DateTimeOffset(2026, 11, number, 0, 0, 0, TimeSpan.Zero),
            root,
            Flattener.Flatten($"stage@v{number}", root),
            "kv/app/stage",
            "https://vault.test:8200");
    }

    [Fact]
    public void Each_version_is_compared_with_the_one_before_it()
    {
        var history = NodeHistory.Build("PaymentSettings:TransactionExpiry",
        [
            Version(1, """{ "PaymentSettings": { "TransactionExpiry": 1500 } }"""),
            Version(2, """{ "PaymentSettings": { "TransactionExpiry": 1500, "Other": 1 } }"""),
            Version(3, """{ "PaymentSettings": { "TransactionExpiry": 3600 } }"""),
            Version(4, """{ "PaymentSettings": { } }"""),
            Version(5, """{ "PaymentSettings": { } }"""),
            Version(6, """{ "PaymentSettings": { "TransactionExpiry": 3600 } }"""),
        ]);

        Assert.Equal([6, 5, 4, 3, 2, 1], history.Select(r => r.Version));
        Assert.Equal(
            [NodeChange.Added, NodeChange.Absent, NodeChange.Removed, NodeChange.Changed, NodeChange.Unchanged, NodeChange.Added],
            history.Select(r => r.Change));
        Assert.Equal([6, 4, 3, 1], history.Where(r => r.IsChangePoint).Select(r => r.Version));
        Assert.Equal("3600", history[0].Text);
        Assert.Null(history[1].Text);
    }

    [Fact]
    public void The_oldest_kept_version_is_not_claimed_to_have_added_the_node()
    {
        var history = NodeHistory.Build("A",
        [
            Version(7, """{ "A": 1 }"""),
            Version(8, """{ "A": 1 }"""),
        ]);

        Assert.Equal(NodeChange.FirstKept, history[^1].Change);
        Assert.True(history[^1].IsChangePoint);
    }

    [Fact]
    public void Formatting_and_key_order_are_never_a_change()
    {
        var history = NodeHistory.Build("S",
        [
            Version(1, """{ "S": { "a": 1, "b": 2 } }"""),
            Version(2, """{"S":{"b":2,"a":1}}"""),
        ]);

        Assert.Equal(NodeChange.Unchanged, history[0].Change);
    }

    [Fact]
    public void Reordering_a_declared_set_is_not_a_change_but_editing_it_is()
    {
        const string path = "ConnectionStrings:Couchbase:Modules:Payment:Scopes:payment";

        var history = NodeHistory.Build(path,
        [
            Version(1, """{ "ConnectionStrings": { "Couchbase": { "Modules": { "Payment": { "Scopes": { "payment": ["a", "b"] } } } } } }"""),
            Version(2, """{ "ConnectionStrings": { "Couchbase": { "Modules": { "Payment": { "Scopes": { "payment": ["b", "a"] } } } } } }"""),
            Version(3, """{ "ConnectionStrings": { "Couchbase": { "Modules": { "Payment": { "Scopes": { "payment": ["b", "a", "c"] } } } } } }"""),
        ]);

        Assert.Equal([NodeChange.Changed, NodeChange.Unchanged, NodeChange.Added], history.Select(r => r.Change));
    }

    [Fact]
    public void A_change_inside_an_array_element_counts_against_the_array_node()
    {
        var history = NodeHistory.Build("Serilog:WriteTo",
        [
            Version(1, """{ "Serilog": { "WriteTo": [ { "Name": "Seq", "Args": { "serverUrl": "http://a" } } ] } }"""),
            Version(2, """{ "Serilog": { "WriteTo": [ { "Name": "Seq", "Args": { "serverUrl": "http://b" } } ] } }"""),
        ]);

        Assert.Equal(NodeChange.Changed, history[0].Change);
        Assert.Equal("2 keys", history[0].Preview);
    }

    [Fact]
    public void An_element_path_resolves_like_it_does_on_the_tree()
    {
        var history = NodeHistory.Build("Serilog:WriteTo[Name=Seq]",
        [
            Version(1, """{ "Serilog": { "WriteTo": [ { "Name": "Seq", "Args": { "serverUrl": "http://a" } } ] } }"""),
            Version(2, """{ "Serilog": { "WriteTo": [ { "Name": "Console" } ] } }"""),
        ]);

        Assert.Equal([NodeChange.Removed, NodeChange.Added], history.Select(r => r.Change));
    }

    [Fact]
    public void Going_from_an_empty_object_to_an_empty_array_is_a_change()
    {
        var history = NodeHistory.Build("X",
        [
            Version(1, """{ "X": {} }"""),
            Version(2, """{ "X": [] }"""),
        ]);

        Assert.Equal(NodeChange.Changed, history[0].Change);
    }

    [Fact]
    public void The_empty_path_is_the_whole_document()
    {
        var history = NodeHistory.Build("",
        [
            Version(1, """{ "A": 1 }"""),
            Version(2, """{ "A": 1 }"""),
            Version(3, """{ "A": 2 }"""),
        ]);

        Assert.Equal([NodeChange.Changed, NodeChange.Unchanged, NodeChange.Added], history.Select(r => r.Change));
        Assert.Equal("1 key", history[0].Preview);
    }

    [Fact]
    public void A_secret_preview_is_masked_but_its_text_is_the_real_value()
    {
        var history = NodeHistory.Build("Db:Password",
        [
            Version(1, """{ "Db": { "Password": "hunter2-old" } }"""),
            Version(2, """{ "Db": { "Password": "hunter2-new" } }"""),
        ]);

        Assert.Equal(NodeChange.Changed, history[0].Change);
        Assert.DoesNotContain("hunter2", history[0].Preview, StringComparison.Ordinal);
        Assert.StartsWith(Leaf.SecretPlaceholder, history[0].Preview, StringComparison.Ordinal);
        Assert.NotEqual(history[0].Preview, history[1].Preview);
        Assert.Contains("hunter2-new", history[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_value_is_cut_short_in_the_preview()
    {
        var history = NodeHistory.Build("Note", [Version(1, $$"""{ "Note": "{{new string('x', 200)}}" }""")]);

        Assert.EndsWith("…", history[0].Preview, StringComparison.Ordinal);
        Assert.True(history[0].Preview.Length <= 81);
    }
}

public sealed class SecretHistoryTests
{
    private static TierDocument Tier(TierOrigin origin) => new()
    {
        Definition = new TierDefinition
        {
            Id = "stage",
            Label = "stage",
            VaultPath = origin == TierOrigin.Vault ? "kv/app/stage" : null,
            Kind = origin == TierOrigin.Vault ? Sources.SourceKind.Vault : Sources.SourceKind.LocalFile,
        },
        Root = OrdinalJsonWriter.Parse("{}"),
        Flat = new FlatConfig("stage", []),
        Origin = origin,
    };

    [Fact]
    public void A_local_file_has_no_version_history()
    {
        Assert.Equal("Files have no version history.",
            SecretHistory.Unavailable(Tier(TierOrigin.LocalFile), SampleFiles.Settings()));
    }

    [Fact]
    public void A_vault_tier_with_a_complete_connection_can_be_asked()
    {
        Assert.Null(SecretHistory.Unavailable(Tier(TierOrigin.Vault), SampleFiles.Settings()));
    }

    [Fact]
    public void An_incomplete_connection_is_named_rather_than_attempted()
    {
        var reason = SecretHistory.Unavailable(Tier(TierOrigin.Vault), new VaultSettings());

        Assert.NotNull(reason);
        Assert.Contains("incomplete", reason, StringComparison.Ordinal);
    }
}
