using System.Text;
using System.Text.Json;
using JsonInsight.Editing;
using JsonInsight.Model;
using JsonInsight.Promote;
using JsonInsight.Vault;

namespace JsonInsight.Tests;

[Collection("sample-files")]
public sealed class EditTests(SampleFiles files)
{
    private const string AuthUrl = "ConnectionStrings:Couchbase:Modules:Auth:Url";
    private const string ModuleUrls = "ConnectionStrings:Couchbase:Modules:*:Url";

    private static PendingEdit Update(TierDocument tier, string path, string value) => new()
    {
        TierId = tier.Id,
        Path = path,
        Kind = EditKind.Update,
        BaseValue = tier.Flat.Find(path)!.ComparableValue,
        NewValue = value,
        NewKind = tier.Flat.Find(path)!.Kind,
        Class = tier.Flat.Find(path)!.Class,
    };

    /// <summary>
    /// The change that prompted the whole feature: six sibling Couchbase URLs widened from one seed
    /// host to the full node list, in one commit. It is also the strongest correctness check
    /// available - the file still round-trips afterwards, and nothing but those six lines moved.
    /// </summary>
    [Fact]
    public void Widening_the_six_module_urls_is_one_clean_commit()
    {
        var beta = files.Beta;

        var targets = beta.Flat.Paths
            .Where(p => JsonInsight.Diff.PathGlob.IsMatch(p, ModuleUrls))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(6, targets.Length);

        // A value none of the six can already hold, so the assertions below do not quietly depend on
        // what the snapshot happens to contain today - these URLs have been widened by hand once
        // already, and a test that passed only while they had not would be worthless.
        const string widened = "couchbase://10.0.0.1,10.0.0.2,10.0.0.3,10.0.0.4";
        Assert.DoesNotContain(targets, p => beta.Flat.Find(p)!.Value == widened);

        var edits = targets.Select(p => Update(beta, p, widened)).ToArray();
        var updated = EditApplier.Apply(beta, edits);
        var flat = files.Flattener.Flatten("beta", updated);

        // Same key count, six new values, and - the invariant that matters - nothing outside those
        // six moved by so much as a character.
        Assert.Equal(beta.Flat.Count, flat.Count);
        Assert.All(targets, p => Assert.Equal(widened, flat.Find(p)!.Value));

        var changed = beta.Flat.Paths
            .Where(p => beta.Flat.Find(p)!.ComparableValue != flat.Find(p)!.ComparableValue)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(targets, changed);

        // And it is something the pusher will accept, which is the only way it can leave the app.
        Assert.Null(new VaultPusher(files.Flattener).Payload("beta", updated).Problem);
    }

    [Fact]
    public void An_added_key_lands_in_its_ordinal_position_and_creates_its_parents()
    {
        var beta = files.Beta;

        var edit = new PendingEdit
        {
            TierId = beta.Id,
            Path = "ConnectionStrings:Couchbase:Modules:Aardvark:Url",
            Kind = EditKind.Add,
            NewValue = "couchbase://10.0.0.34",
            NewKind = JsonValueKind.String,
        };

        var updated = EditApplier.Apply(beta, [edit]);
        var text = OrdinalJsonWriter.SerializeToText(updated);

        // Ordinal sorting is not optional: the writer sorts every object, and this text is what goes
        // to Vault. Aardvark sorts before Account.
        var aardvark = text.IndexOf("\"Aardvark\":", StringComparison.Ordinal);
        var account = text.IndexOf("\"Account\":", StringComparison.Ordinal);
        Assert.True(aardvark >= 0 && account >= 0);
        Assert.True(aardvark < account, "Aardvark must sort before Account.");

        Assert.Equal(beta.Flat.Count + 1, files.Flattener.Flatten("beta", updated).Count);
    }

    /// <summary>
    /// Deleting the last child must not leave <c>{}</c> behind. The flattener treats an empty object
    /// as a real comparable leaf, so an emptied parent would turn "I removed this key" into "this
    /// tier now declares an empty section" - a different statement, and a new grid row rather than
    /// the removal that was asked for.
    /// </summary>
    [Fact]
    public void Deleting_the_last_child_prunes_the_emptied_parent()
    {
        var stage = files.Stage;

        var lockKeys = stage.Flat.Paths
            .Where(p => p.StartsWith("PaymentSettings:BillWalletLock:", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(lockKeys);

        var edits = lockKeys.Select(p => new PendingEdit
        {
            TierId = stage.Id,
            Path = p,
            Kind = EditKind.Delete,
            BaseValue = stage.Flat.Find(p)!.ComparableValue,
        }).ToArray();

        var updated = EditApplier.Apply(stage, edits);
        var flat = files.Flattener.Flatten("stage", updated);

        Assert.Equal(stage.Flat.Count - lockKeys.Length, flat.Count);

        // Neither the keys nor an empty husk of their parent survives.
        Assert.DoesNotContain(flat.Paths, p =>
            p.StartsWith("PaymentSettings:BillWalletLock", StringComparison.Ordinal));

        Assert.DoesNotContain("BillWalletLock",
            OrdinalJsonWriter.SerializeToText(updated), StringComparison.Ordinal);
    }

    [Fact]
    public void A_number_stays_a_number_and_a_bool_stays_a_bool()
    {
        var beta = files.Beta;

        var edits = new[]
        {
            new PendingEdit
            {
                TierId = beta.Id, Path = "Edited:Count", Kind = EditKind.Add,
                NewValue = "1.50", NewKind = JsonValueKind.Number,
            },
            new PendingEdit
            {
                TierId = beta.Id, Path = "Edited:Flag", Kind = EditKind.Add,
                NewValue = "false", NewKind = JsonValueKind.True,
            },
            new PendingEdit
            {
                TierId = beta.Id, Path = "Edited:Text", Kind = EditKind.Add,
                NewValue = "10", NewKind = JsonValueKind.String,
            },
        };

        var updated = EditApplier.Apply(beta, edits);
        var text = OrdinalJsonWriter.SerializeToText(updated);

        // The literal form survives: 1.50 does not become 1.5, and "10" stays quoted.
        Assert.Contains("\"Count\": 1.50", text, StringComparison.Ordinal);
        Assert.Contains("\"Flag\": false", text, StringComparison.Ordinal);
        Assert.Contains("\"Text\": \"10\"", text, StringComparison.Ordinal);

        var flat = files.Flattener.Flatten("beta", updated);
        Assert.Equal(JsonValueKind.Number, flat.Find("Edited:Count")!.Kind);
        Assert.Equal(JsonValueKind.False, flat.Find("Edited:Flag")!.Kind);
        Assert.Equal(JsonValueKind.String, flat.Find("Edited:Text")!.Kind);
    }

    /// <summary>
    /// The read-only fence, at the one place a change can now leave the app. Applying edits to a
    /// read-only tier in memory harms nothing; getting them out is what is refused.
    /// </summary>
    [Fact]
    public void Pushing_a_read_only_tier_is_refused()
    {
        var blocked = VaultPusher.Blocked(files.ReadOnly(), SampleFiles.Settings());

        Assert.NotNull(blocked);
        Assert.Contains("read-only", blocked, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_applied_document_is_what_would_be_pushed()
    {
        var edit = Update(files.Beta, AuthUrl, "couchbase://10.0.0.99");

        var (payload, problem) = new VaultPusher(files.Flattener)
            .Payload("beta", EditApplier.Apply(files.Beta, [edit]));

        Assert.Null(problem);
        Assert.Contains("couchbase://10.0.0.99", payload!, StringComparison.Ordinal);
        Assert.DoesNotContain("couchbase://10.0.0.99", SampleFiles.Canonical(files.Beta), StringComparison.Ordinal);
    }

    // The change-set tests lived here: a queue keyed by (tier, path), and the staleness and re-basing
    // that kept it honest across a pull. Both are gone with the queue — an edit lands in the tier's
    // own in-memory document as it is made, and a document cannot go stale against itself. The
    // question that genuinely remains, whether the *source* moved since it was read, belongs to the
    // push preflight and is tested in PushTests and LocalFileProviderTests.

    // -------------------------------------------------------------- validation

    private EditValidator Validator() => new(files.Documents, files.Classifier);

    [Fact]
    public void A_key_that_exists_nowhere_is_flagged_with_a_suggestion()
    {
        var edit = new PendingEdit
        {
            TierId = "beta",
            // One character out from the real AuthSettings:GatewayA:Host.
            Path = "AuthSettings:GatewayW:Host",
            Kind = EditKind.Add,
            NewValue = "x",
        };

        var warnings = Validator().Validate([edit]);

        Assert.Contains(warnings, w => w.Message.Contains("exists in no tier", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Message.Contains("AuthSettings:GatewayA:Host", StringComparison.Ordinal));
    }

    /// <summary>
    /// The ordinal comparer that orders these files treats Url and URL as two keys; the .NET
    /// configuration binder treats them as one. That pair is a defect however it is written, so it
    /// is the one validation that blocks rather than warns.
    /// </summary>
    [Fact]
    public void A_casing_only_collision_is_blocking()
    {
        var edit = new PendingEdit
        {
            TierId = "beta",
            Path = "ConnectionStrings:Couchbase:Modules:Auth:URL",
            Kind = EditKind.Add,
            NewValue = "couchbase://10.0.0.34",
        };

        var warnings = Validator().Validate([edit]);

        Assert.Contains(warnings, w => w.IsBlocking && w.Message.Contains("casing", StringComparison.Ordinal));
    }

    [Fact]
    public void Deleting_from_one_tier_while_others_keep_the_key_is_flagged_as_drift()
    {
        var edit = new PendingEdit
        {
            TierId = "beta",
            Path = AuthUrl,
            Kind = EditKind.Delete,
            BaseValue = files.Beta.Flat.Find(AuthUrl)!.ComparableValue,
        };

        var warnings = Validator().Validate([edit]);

        Assert.Contains(warnings, w => w.Message.Contains("drift", StringComparison.Ordinal));
    }

    [Fact]
    public void A_type_that_disagrees_with_the_other_tiers_is_flagged()
    {
        const string path = "ConnectionStrings:Couchbase:KvTimeoutSeconds";
        Assert.Equal(JsonValueKind.Number, files.Beta.Flat.Find(path)!.Kind);

        var edit = new PendingEdit
        {
            TierId = "beta",
            Path = path,
            Kind = EditKind.Update,
            BaseValue = files.Beta.Flat.Find(path)!.ComparableValue,
            NewValue = "10",
            NewKind = JsonValueKind.String,
        };

        var warnings = Validator().Validate([edit]);

        Assert.Contains(warnings, w => w.Message.Contains("written as string", StringComparison.Ordinal));
    }

    [Fact]
    public void A_promote_placeholder_left_in_a_value_is_flagged()
    {
        var edit = Update(files.Beta, AuthUrl, "<<SET-FOR-beta>>");

        Assert.Contains(Validator().Validate([edit]),
            w => w.Message.Contains("placeholder", StringComparison.Ordinal));
    }

    [Fact]
    public void Nearest_match_declines_to_guess_when_nothing_is_close()
    {
        Assert.Null(EditValidator.NearestMatch("Totally:Unrelated:Nonsense",
            ["AuthSettings:GatewayA:Host", "PaymentSettings:GatewayA:Mpg:Terminal"]));
    }
}
