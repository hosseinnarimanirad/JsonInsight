using JsonInsight.Classify;
using JsonInsight.Loading;
using JsonInsight.Model;
using JsonInsight.ViewModels;
using System.Text.Json.Nodes;

namespace WebJsonInsight.Tests;

/// <summary>
/// How many warnings a document produces, which is a separate question from whether it produces the
/// right ones.
///
/// <para>
/// A document with 68 undeclared arrays used to produce 68 warnings, and four tiers of it produced
/// 272 — the same sentence over and over with only the path changing. Every one of them was true and
/// the set of them was useless: the banner scrolls, so the count above it read "272 problems" and the
/// first two lines, which were the point, were indistinguishable from the other 270.
/// </para>
/// </summary>
public sealed class WarningTests
{
    private static readonly Flattener Flattener = new(ArrayStrategies.Load(), Classifier.Load());

    /// <summary>A document with many undeclared arrays warns once, and says how many.</summary>
    [Fact]
    public void Many_undeclared_arrays_produce_one_warning()
    {
        var document = WithUndeclaredArrays(68);

        var flat = Flattener.Flatten("dev", document);
        var undeclared = flat.Warnings.Where(w => w.Contains("no declared strategy", StringComparison.Ordinal)).ToArray();

        Assert.Single(undeclared);
        Assert.Contains("68 arrays have", undeclared[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// The paths are not dropped — a few are named and the rest counted. arrays.json is edited by
    /// pattern, so the first few are usually enough to write the rule that covers all of them.
    /// </summary>
    [Fact]
    public void The_summary_names_a_few_paths_and_counts_the_rest()
    {
        var flat = Flattener.Flatten("dev", WithUndeclaredArrays(68));
        var warning = flat.Warnings.Single(w => w.Contains("no declared strategy", StringComparison.Ordinal));

        Assert.Contains("The first 3 are:", warning, StringComparison.Ordinal);
        Assert.Contains("(+65 more)", warning, StringComparison.Ordinal);
    }

    /// <summary>One is still named outright, and reads as a sentence about that one rather than a count.</summary>
    [Fact]
    public void A_single_undeclared_array_names_itself()
    {
        var flat = Flattener.Flatten("dev", WithUndeclaredArrays(1));
        var warning = flat.Warnings.Single(w => w.Contains("no declared strategy", StringComparison.Ordinal));

        Assert.Contains("array undeclared0 has no declared strategy", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("more)", warning, StringComparison.Ordinal);
    }

    /// <summary>A document whose arrays are all declared says nothing about them at all.</summary>
    [Fact]
    public void A_document_with_no_undeclared_arrays_is_silent()
    {
        var flat = Flattener.Flatten("dev", JsonNode.Parse("""{"A":{"B":1}}""")!);

        Assert.DoesNotContain(flat.Warnings, w => w.Contains("no declared strategy", StringComparison.Ordinal));
    }

    /// <summary>
    /// Four tiers of the same shape produce one line, not four. These documents are four copies of one
    /// thing, so a warning about it is almost always true of all four, and repeating the sentence per
    /// tier puts the interesting word behind the boring one.
    /// </summary>
    [Fact]
    public void The_same_warning_across_every_source_is_named_once()
    {
        var documents = new[] { "dev", "stage", "beta", "prod" }
            .Select(id => AsTier(id, WithUndeclaredArrays(68)))
            .ToArray();

        var main = new MainVm(vaultAtStartup: false);
        main.Seed(documents);

        var undeclared = main.Problems
            .Where(p => p.Contains("no declared strategy", StringComparison.Ordinal))
            .ToArray();

        Assert.Single(undeclared);
        Assert.StartsWith("every source:", undeclared[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// A warning true of only some sources still names which. Collapsing to "every source" when it is
    /// not every source would be the one thing worse than repeating it.
    /// </summary>
    [Fact]
    public void A_warning_true_of_some_sources_names_those_sources()
    {
        var documents = new[]
        {
            AsTier("dev", WithUndeclaredArrays(68)),
            AsTier("stage", WithUndeclaredArrays(68)),
            AsTier("beta", JsonNode.Parse("""{"A":{"B":1}}""")!),
        };

        var main = new MainVm(vaultAtStartup: false);
        main.Seed(documents);

        var undeclared = main.Problems
            .Where(p => p.Contains("no declared strategy", StringComparison.Ordinal))
            .ToArray();

        Assert.Single(undeclared);
        Assert.StartsWith("dev, stage:", undeclared[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole point, stated as a number: what used to be 272 lines is now one. Written against the
    /// count rather than the text so it keeps failing if the collapse is ever undone.
    /// </summary>
    [Fact]
    public void Four_tiers_of_sixty_eight_arrays_produce_one_problem_not_two_hundred_and_seventy_two()
    {
        var documents = new[] { "dev", "stage", "beta", "prod" }
            .Select(id => AsTier(id, WithUndeclaredArrays(68)))
            .ToArray();

        var main = new MainVm(vaultAtStartup: false);
        main.Seed(documents);

        Assert.Single(main.Problems);
        Assert.Equal("1 problem", main.ProblemsHeading);
    }

    /// <summary>
    /// A document holding <paramref name="count"/> arrays that arrays.json says nothing about. Named
    /// so they cannot collide with a real pattern in the shipped rules.
    ///
    /// <para>
    /// Arrays of <em>objects</em>, deliberately. An array of scalars is one value now, declared or
    /// not, so it produces no warning at all — index comparison is only dangerous when there is
    /// structure below the elements to be shifted, which is exactly the case a keyedObjects strategy
    /// exists to fix. Building these out of numbers, as this fixture first did, tested nothing once
    /// that rule landed.
    /// </para>
    /// </summary>
    private static JsonNode WithUndeclaredArrays(int count)
    {
        var root = new JsonObject();

        for (var i = 0; i < count; i++)
        {
            root[$"undeclared{i}"] = new JsonArray(
                new JsonObject { ["name"] = "first" },
                new JsonObject { ["name"] = "second" });
        }

        return root;
    }

    /// <summary>
    /// The other half of the same rule: a list of scalars is one value, so it warns about nothing. It
    /// is the common case in these documents — release notes, feature flags, a list of ports — and it
    /// was the bulk of the 272.
    /// </summary>
    [Fact]
    public void Scalar_arrays_produce_no_warning_at_all()
    {
        var root = new JsonObject
        {
            ["body"] = new JsonArray("first note", "second note", "third note"),
            ["ports"] = new JsonArray(8080, 8443),
            ["flags"] = new JsonArray(true, false),
            ["empty"] = new JsonArray(),
        };

        var flat = Flattener.Flatten("dev", root);

        Assert.Empty(flat.Warnings);

        // And each is one leaf rather than one per element.
        Assert.Equal(4, flat.Count);
    }

    private static TierDocument AsTier(string id, JsonNode root) =>
        Fixtures.AsTier(id, 1, root.ToJsonString());
}
