using JsonInsight.Diff;

namespace JsonInsight.Tests;

[Collection("sample-files")]
public sealed class DiffTests(SampleFiles files)
{
    [Theory]
    [InlineData("dev", 427)]
    // stage and beta each carry 8 AdminSettings leaves that dev has never gained; see
    // Dev_to_stage_drift_matches_known_counts, whose OnlyInRight moved by the same 8.
    [InlineData("stage", 426)]
    // 428, not the 422 these were before tiers.json was repointed at the current snapshots: beta
    // v08 and prod v06 both gained the 6-key Redis object that until then only stage had. They now
    // carry it alongside the packed RedisCache:Configuration, which is why the redis alias still
    // engages - see Aliases_collapse_stage_to_beta_noise.
    [InlineData("beta", 428)]
    // prod carries the same key set as beta, key for key and in the same order.
    [InlineData("prod", 428)]
    public void Leaf_counts_are_stable(string tierId, int expected)
    {
        // These are lower than a naive count because the Couchbase Scopes arrays collapse to one
        // set-valued leaf each rather than one leaf per element.
        Assert.Equal(expected, files[tierId].Flat.Count);
    }

    /// <summary>
    /// A flipped boolean is a value difference, not a type one. True and False are distinct
    /// JsonValueKind members, so comparing the members directly mislabelled every such row.
    /// </summary>
    [Fact]
    public void A_boolean_that_flips_is_a_value_difference_not_a_type_one()
    {
        var entry = files.Stage.Flat.Find("AdminSettings:TrustGatewayHeaders") is { } left &&
                    files.Beta.Flat.Find("AdminSettings:TrustGatewayHeaders") is { } right
            ? DiffEntry.Compare("AdminSettings:TrustGatewayHeaders", left, right)
            : throw new InvalidOperationException("Both tiers should carry AdminSettings:TrustGatewayHeaders.");

        Assert.Equal(DiffKind.ValueDiffers, entry.Kind);
    }

    [Fact]
    public void No_array_is_left_unclassified()
    {
        foreach (var document in files.Documents)
        {
            Assert.DoesNotContain(document.Warnings, w => w.Contains("no declared strategy"));
        }
    }

    [Fact]
    public void Dev_to_stage_drift_matches_known_counts()
    {
        var diff = new TierDiffer(files.Aliases).Compare(files.Dev.Flat, files.Stage.Flat);

        Assert.Equal(23, diff.OnlyInLeft);

        // 22, not 14: stage gained the 8 AdminSettings keys (inline admin RS256 PEMs) that keep
        // the consuming application from crashing at startup. dev has no AdminSettings at all and relies on the
        // compiled defaults plus the gitignored Config/admin-*.key files.
        Assert.Equal(22, diff.OnlyInRight);
    }

    [Theory]
    // Present in dev, absent from both vault tiers - the "added to dev, never promoted" case.
    [InlineData("AccountSettings:NightlyApprovalJob:Enabled", "dev")]
    [InlineData("PaymentSettings:Hub:Payment:AcceptorCode", "dev")]
    [InlineData("PaymentSettings:GatewayB:BillPrivateKey", "dev")]
    [InlineData("Elasticsearch:AllowInsecureCertificate", "dev")]
    [InlineData("ProfileSettings:WalletBalanceId", "dev")]
    [InlineData("AccountSettings:WalletProvider:FindTransactionUrl", "dev")]
    public void Known_dev_only_paths_are_absent_from_every_vault_tier(string path, string presentIn)
    {
        Assert.True(files[presentIn].Flat.Contains(path), $"{path} should exist in {presentIn}.");
        Assert.False(files.Stage.Flat.Contains(path), $"{path} should be absent from stage.");
        Assert.False(files.Beta.Flat.Contains(path), $"{path} should be absent from beta.");
        Assert.False(files.Prod.Flat.Contains(path), $"{path} should be absent from prod.");
    }

    [Theory]
    // Drift in the other direction: the vaults have keys dev never gained.
    [InlineData("AuthSettings:GatewayA:Host")]
    [InlineData("AccountSettings:WalletProvider:UpdateLevelUrl")]
    [InlineData("PaymentSettings:GatewayA:CardBalanceInquiryUrl")]
    [InlineData("PaymentSettings:BillProvider:MtnBillInquiryUrl")]
    public void Known_vault_only_paths_are_absent_from_dev(string path)
    {
        Assert.False(files.Dev.Flat.Contains(path));
        Assert.True(files.Stage.Flat.Contains(path));
    }

    [Theory]
    [InlineData("PaymentSettings:BillWalletLock:KeyPrefix")]
    [InlineData("PaymentSettings:BillWalletLock:TtlSeconds")]
    public void Landed_in_dev_and_stage_but_not_beta_or_prod(string path)
    {
        Assert.True(files.Dev.Flat.Contains(path));
        Assert.True(files.Stage.Flat.Contains(path));
        Assert.False(files.Beta.Flat.Contains(path));
        Assert.False(files.Prod.Flat.Contains(path));
    }

    /// <summary>
    /// The acceptance test for the alias and array machinery. Without them the raw stage-to-beta
    /// comparison shows a handful either way; with them it collapses further, into explicit shape
    /// rows. If this test stops distinguishing the two, the machinery has stopped doing anything.
    ///
    /// <para>
    /// The raw left-hand count was 10 while tiers.json still pointed at beta v06: the 6 Redis keys
    /// were then genuinely stage-only. beta v08 has them too, so those 6 stopped being drift and the
    /// raw count fell to 4. The aliased numbers did not move at all, which is the point - the alias
    /// was already describing them as one concept in two shapes rather than as six missing keys.
    /// </para>
    /// </summary>
    [Fact]
    public void Aliases_collapse_stage_to_beta_noise()
    {
        var raw = new TierDiffer(AliasSet.Empty()).Compare(files.Stage.Flat, files.Beta.Flat);
        var aliased = new TierDiffer(files.Aliases).Compare(files.Stage.Flat, files.Beta.Flat);

        Assert.Equal(4, raw.OnlyInLeft);
        Assert.Equal(6, raw.OnlyInRight);
        Assert.Equal(0, raw.ShapeDifferences);

        Assert.Equal(3, aliased.OnlyInLeft);
        Assert.Equal(1, aliased.OnlyInRight);
        Assert.Equal(4, aliased.ShapeDifferences);
    }

    [Fact]
    public void Redis_and_rediscache_are_one_shape_row_not_a_rename()
    {
        var diff = new TierDiffer(files.Aliases).Compare(files.Stage.Flat, files.Beta.Flat);

        var shape = diff.Entries.Single(e =>
            e.Kind == DiffKind.ShapeDiffers && e.Path.Contains("Redis", StringComparison.Ordinal));

        Assert.Equal("Redis / RedisCache", shape.Path);

        // And the individual Redis keys must not also appear as six separate "missing" rows.
        Assert.DoesNotContain(diff.Entries, e =>
            e.Path.StartsWith("Redis:", StringComparison.Ordinal));
        Assert.DoesNotContain(diff.Entries, e =>
            e.Path.StartsWith("RedisCache:", StringComparison.Ordinal));
    }

    /// <summary>
    /// The N-tier alias path has its own failure mode the pairwise tests above cannot see: a tier with
    /// no entry in an alias's members block silently disables that alias for <em>every</em> tier, so
    /// adding a tier to tiers.json and forgetting aliases.json degrades the whole side-by-side view
    /// with nothing failing. Asserting both aliases engage across all configured tiers is the check.
    /// </summary>
    [Fact]
    public void Every_alias_engages_across_all_configured_tiers()
    {
        var applied = files.Multi.AppliedAliases;

        Assert.Contains(applied, a => a.Id == "redis");
        Assert.Contains(applied, a => a.Id == "couchbase-scopes");

        var tierIds = files.Documents.Select(d => d.Id).ToArray();
        Assert.All(applied, a => Assert.Equal(tierIds.Order(StringComparer.Ordinal),
            a.RootsByTier.Keys.Order(StringComparer.Ordinal)));
    }

    /// <summary>
    /// Adding the Seq sink must read as one added sink. Index matching would report it as
    /// "element 2 added and elements 0 and 1 rewritten", which is three findings for one change.
    /// </summary>
    [Fact]
    public void Serilog_sinks_are_matched_by_name_not_index()
    {
        var diff = new TierDiffer(files.Aliases).Compare(files.Dev.Flat, files.Stage.Flat);

        var serilog = diff.Differences
            .Where(e => e.Path.StartsWith("Serilog:WriteTo", StringComparison.Ordinal))
            .ToArray();

        Assert.All(serilog, e => Assert.Contains("[Name=", e.Path, StringComparison.Ordinal));
        Assert.Contains(serilog, e => e.Path.Contains("[Name=Seq]") && e.Kind == DiffKind.OnlyInRight);

        // The Console sink is identical in both tiers and must not appear as a difference at all.
        Assert.DoesNotContain(serilog, e => e.Path.Contains("[Name=Console]"));
    }

    [Fact]
    public void Scope_arrays_compare_as_unordered_sets()
    {
        var path = "ConnectionStrings:Couchbase:Modules:Package:Scopes:payment";

        var dev = files.Dev.Flat.Find(path);
        var stage = files.Stage.Flat.Find(path);

        Assert.NotNull(dev);
        Assert.NotNull(stage);
        Assert.True(dev!.IsSet);

        var entry = DiffEntry.Compare(path, dev, stage);
        Assert.Equal(DiffKind.Same, entry.Kind);
    }

    /// <summary>
    /// The rollup is what makes a subtree one promote unit. Eleven absent keys are one absent
    /// feature, and the node - not the individual keys - is what a Promote button acts on.
    /// </summary>
    [Fact]
    public void Nightly_approval_job_rolls_up_to_a_single_node()
    {
        var tree = DiffNode.Build(files.Multi);

        var node = tree.DescendantsAndSelf()
            .Single(n => n.Path == "AccountSettings:NightlyApprovalJob");

        Assert.True(node.IsUniformlyMissing);
        Assert.Equal(11, node.LeafCount);
        Assert.Equal(["beta", "prod", "stage"], node.UniformlyMissingFrom);
    }

    [Fact]
    public void A_partially_present_subtree_does_not_roll_up()
    {
        var tree = DiffNode.Build(files.Multi);

        // WalletProvider has keys missing from different tiers, so collapsing it would hide the shape
        // of the gap. It must stay expanded.
        var node = tree.DescendantsAndSelf()
            .Single(n => n.Path == "AccountSettings:WalletProvider");

        Assert.False(node.IsUniformlyMissing);
    }

    [Fact]
    public void Path_splitting_keeps_keyed_array_segments_intact()
    {
        var segments = ConfigPath.Split("Serilog:WriteTo[Name=Seq]:Args:serverUrl");

        Assert.Equal(["Serilog", "WriteTo[Name=Seq]", "Args", "serverUrl"], segments);
    }

    [Theory]
    [InlineData("PaymentSettings:Hub:CardTransfer:Banks:BANK_A:Terminal", "**:Banks:*:*", true)]
    [InlineData("Elasticsearch:Password", "**:*Password", true)]
    [InlineData("Elasticsearch:Url", "**:*Password", false)]
    [InlineData("Encryption:Profile:Key", "Encryption:**", true)]
    [InlineData("ConnectionStrings:Couchbase:Modules:Auth:Scopes:auth",
        "ConnectionStrings:Couchbase:Modules:*:Scopes:*", true)]
    public void Glob_matching_behaves(string path, string pattern, bool expected)
    {
        Assert.Equal(expected, PathGlob.IsMatch(path, pattern));
    }
}
