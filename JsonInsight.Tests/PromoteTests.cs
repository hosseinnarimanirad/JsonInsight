using JsonInsight.Model;
using JsonInsight.Promote;
using JsonInsight.Vault;

namespace JsonInsight.Tests;

[Collection("sample-files")]
public sealed class PromoteTests(SampleFiles files)
{
    private const string NightlyRoot = "AccountSettings:NightlyApprovalJob";

    [Fact]
    public void Plan_defaults_business_values_to_verbatim_and_the_rest_to_placeholder()
    {
        var plan = PromotionPlanner.Plan(files.Dev, files.Beta, NightlyRoot);

        Assert.Equal(11, plan.Leaves.Count);

        // NightlyApprovalJob is entirely tuning knobs - intervals, batch sizes, flags - so every key
        // should copy across. A promote that placeholdered all of them would be useless.
        Assert.All(plan.Leaves.Where(l => l.Class == ValueClass.Business),
            l => Assert.Equal(PromotionAction.CopyVerbatim, l.DefaultAction));

        Assert.All(plan.Leaves.Where(l => l.Class != ValueClass.Business),
            l => Assert.Equal(PromotionAction.CopyPlaceholder, l.DefaultAction));
    }

    [Fact]
    public void Planning_into_a_read_only_tier_is_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PromotionPlanner.Plan(files.Stage, files.ReadOnly(), "PaymentSettings:GatewayA:CardBalanceInquiryUrl"));

        Assert.Contains("read-only", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Placeholder_is_a_loud_sentinel_not_an_empty_string()
    {
        // An empty string is a legitimate deliberate value in these files, so blanking a key would
        // be indistinguishable from someone forgetting to set it.
        var placeholder = PromotionPlan.Placeholder("beta");

        Assert.Equal("<<SET-FOR-beta>>", placeholder);
        Assert.NotEqual(string.Empty, placeholder);
    }

    /// <summary>
    /// The golden test: promote the real subtree into the real beta payload and check every property
    /// that matters — key count, sorted position, and that nothing else moved.
    ///
    /// <para>
    /// It asserts on the document the promote produces rather than on a file, because a document is
    /// what a promote produces now: it is handed to the push screen and becomes one new version of
    /// the destination secret.
    /// </para>
    /// </summary>
    [Fact]
    public void Promoting_nightly_approval_job_into_beta_produces_a_clean_minimal_change()
    {
        var plan = PromotionPlanner.Plan(files.Dev, files.Beta, NightlyRoot);
        var updated = PromotionPlanner.Apply(files.Beta, files.Dev, plan);

        var flat = files.Flattener.Flatten("beta", updated);
        Assert.Equal(files.Beta.Flat.Count + 11, flat.Count);
        Assert.All(plan.Leaves, l => Assert.True(flat.Contains(l.Path), l.Path));

        var text = OrdinalJsonWriter.SerializeToText(updated);

        // Ordinal sorting puts NightlyApprovalJob first inside AccountSettings, ahead of ProxyUrl.
        var nightly = text.IndexOf("\"NightlyApprovalJob\":", StringComparison.Ordinal);
        var proxy = text.IndexOf("\"ProxyUrl\":", StringComparison.Ordinal);
        Assert.True(nightly >= 0 && proxy >= 0);
        Assert.True(nightly < proxy, "NightlyApprovalJob must sort before ProxyUrl.");

        // Every original line still present: the change is additive, not a reformat.
        var beforeLines = SampleFiles.Canonical(files.Beta).Split("\r\n");
        var afterLines = text.Split("\r\n").ToHashSet(StringComparer.Ordinal);
        var lost = beforeLines.Where(l => !afterLines.Contains(l) && l.Trim().Length > 1).ToArray();
        Assert.True(lost.Length == 0, $"Reformatted {lost.Length} line(s), e.g. {lost.FirstOrDefault()}");

        // And the result is something the pusher will accept, which is the only way it can leave here.
        var (payload, problem) = new VaultPusher(files.Flattener).Payload("beta", updated);
        Assert.Null(problem);
        Assert.Equal(text, payload);
    }

    [Fact]
    public void Placeholder_action_writes_the_sentinel_rather_than_the_source_value()
    {
        var plan = PromotionPlanner.Plan(files.Dev, files.Beta, NightlyRoot);
        foreach (var leaf in plan.Leaves)
        {
            leaf.Action = PromotionAction.CopyPlaceholder;
        }

        var flat = files.Flattener.Flatten("beta", PromotionPlanner.Apply(files.Beta, files.Dev, plan));

        Assert.All(plan.Leaves, l => Assert.Equal("<<SET-FOR-beta>>", flat.Find(l.Path)!.Value));
    }

    [Fact]
    public void Skipped_leaves_are_not_created()
    {
        var plan = PromotionPlanner.Plan(files.Dev, files.Beta, NightlyRoot);
        foreach (var leaf in plan.Leaves.Skip(1))
        {
            leaf.Action = PromotionAction.Skip;
        }

        var flat = files.Flattener.Flatten("beta", PromotionPlanner.Apply(files.Beta, files.Dev, plan));

        Assert.Equal(files.Beta.Flat.Count + 1, flat.Count);
    }

    /// <summary>The source document is never touched — it is another tier, and this only reads it.</summary>
    [Fact]
    public void The_source_tier_is_left_alone()
    {
        var before = SampleFiles.Canonical(files.Dev);

        PromotionPlanner.Apply(files.Beta, files.Dev, PromotionPlanner.Plan(files.Dev, files.Beta, NightlyRoot));

        Assert.Equal(before, SampleFiles.Canonical(files.Dev));
    }

    [Fact]
    public void Navigator_resolves_keyed_array_elements()
    {
        var node = JsonNavigator.Find(files.Stage.Root, "Serilog:WriteTo[Name=Seq]:Name");

        Assert.NotNull(node);
        Assert.Equal("Seq", node!.GetValue<string>());
    }

    /// <summary>
    /// An element on the way through is walked, not created. Reverting a key inside an array element
    /// needs the first half; nothing in this app has the information to do the second, since an
    /// element has a position and inventing one would put it somewhere nobody chose.
    /// </summary>
    [Fact]
    public void EnsureObject_walks_through_an_existing_array_element_and_refuses_to_invent_one()
    {
        var reached = JsonNavigator.EnsureObject(files.Stage.Root, "Serilog:WriteTo[Name=Seq]");
        Assert.Equal("Seq", reached["Name"]!.GetValue<string>());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            JsonNavigator.EnsureObject(files.Stage.Root, "Serilog:WriteTo[Name=NoSuchSink]"));

        Assert.Contains("inventing one", ex.Message, StringComparison.Ordinal);
    }
}
