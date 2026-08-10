using System.Text.Json.Nodes;
using JsonInsight.Diff;
using JsonInsight.Model;
using JsonInsight.Promote;
using JsonInsight.ViewModels;

namespace JsonInsight.Tests;

/// <summary>
/// The hierarchy on the Tier editor tab: what it shows, and what it still lets you do while a filter
/// is on. Both of these were reported as "the tree does not work", and both were true.
/// </summary>
public sealed class TreeTests
{
    private static readonly SampleFiles Fixtures = new();

    /// <summary>Opens an arbitrary document in the editor, without going near Vault.</summary>
    private static JsonEditorVm Open(string json, string tierId = "fixture")
    {
        var document = Fixtures.AsTier(tierId, 1, json);

        var main = new MainVm(vaultAtStartup: false);
        main.Seed([document]);

        var vm = main.JsonEditor!;
        vm.Tier = vm.Tiers.First();
        return vm;
    }

    // ------------------------------------------------------------ root arrays

    /// <summary>
    /// A document whose root is an array. Not every configuration document is an object: a banner
    /// list or a service catalogue is a list, and the tree used to walk one as an object, find
    /// nothing, and render a hierarchy with a single row in it — the one shape of document where it
    /// said nothing at all.
    /// </summary>
    [Fact]
    public void A_document_whose_root_is_an_array_shows_its_elements()
    {
        var vm = Open("""
            [
              { "code": "bundle-a", "providers": [ { "name": "bank-a" } ] },
              { "code": "charity", "status": "inactive" }
            ]
            """);

        // The root row, then one row per element, then their contents.
        Assert.Contains(vm.Nodes, n => n.Path == "[0]" && n.IsContainer);
        Assert.Contains(vm.Nodes, n => n.Path == "[1]" && n.IsContainer);
        Assert.Contains(vm.Nodes, n => n.Path == "[0]:code");
        Assert.Contains(vm.Nodes, n => n.Path == "[0]:providers[0]:name");

        // The same paths the flattener produces, so a row here and a row on the All tiers grid are
        // the same path rather than two spellings of one.
        Assert.Equal(
            vm.Tier!.Flat.Paths.Order(StringComparer.Ordinal),
            vm.Nodes.Where(n => !n.IsContainer).Select(n => n.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void An_element_of_a_root_array_opens_in_the_pane_and_can_be_replaced()
    {
        var vm = Open("""[ { "code": "a" }, { "code": "b" } ]""");

        vm.SelectedNode = vm.Nodes.First(n => n.Path == "[1]");
        Assert.Null(vm.Error);
        Assert.Contains("\"b\"", vm.EditorText, StringComparison.Ordinal);
        Assert.True(vm.SelectedIsElement);

        vm.EditorText = """{ "code": "b", "status": "inactive" }""";
        vm.ApplyCommand.Execute(null);

        Assert.Null(vm.Error);
        Assert.Equal("inactive", vm.Editor!.Find("[1]:status")!.GetValue<string>());

        // In place: the element before it is untouched and the array is still two long.
        Assert.Equal("a", vm.Editor.Find("[0]:code")!.GetValue<string>());
        Assert.Equal(2, ((JsonArray)vm.Editor.Working).Count);
    }

    /// <summary>
    /// The root keeps the kind it was opened as. An array-rooted document is a list; turning it into
    /// an object is not an edit to that document but a different document in its place.
    /// </summary>
    [Fact]
    public void The_root_of_an_array_document_has_to_stay_an_array()
    {
        var vm = Open("""[ { "code": "a" } ]""");
        var editor = vm.Editor!;

        var ex = Assert.Throws<InvalidOperationException>(() => editor.Replace(string.Empty, """{ "a": 1 }"""));
        Assert.Contains("has to be one too", ex.Message, StringComparison.Ordinal);

        editor.Replace(string.Empty, """[ { "code": "b" } ]""");
        Assert.Equal("b", editor.Find("[0]:code")!.GetValue<string>());
    }

    /// <summary>
    /// An array root is compared whole, so the only true statement about a change in it is about the
    /// root — and without this the one row that could carry the mark was the one row that never did.
    /// </summary>
    [Fact]
    public void An_edited_array_document_marks_its_root()
    {
        var vm = Open("""[ { "code": "a" } ]""");

        vm.SelectedNode = vm.Nodes.First(n => n.Path == "[0]");
        vm.EditorText = """{ "code": "z" }""";
        vm.ApplyCommand.Execute(null);

        Assert.True(vm.IsModified);
        Assert.True(vm.Nodes.Single(n => n.Path == string.Empty).IsChanged);
    }

    // --------------------------------------------------- collapsing while filtering

    /// <summary>
    /// A filtered tree opens expanded, because a match hidden inside a collapsed parent is a filter
    /// that lied about what it found. That used to be done by ignoring collapse state altogether,
    /// which made the expander dead for as long as anything was in the search box — and a search
    /// that finds two hundred rows is exactly when collapsing a section is worth doing.
    /// </summary>
    [Fact]
    public void A_section_can_still_be_collapsed_while_a_search_is_on()
    {
        var vm = Open("""
            {
              "Alpha": { "One": { "Deep": 1 }, "Two": 2 },
              "Beta": { "One": 3 }
            }
            """);

        vm.Filter = "One";

        // Everything matching is on show, expanded, as it should be.
        Assert.Contains(vm.Nodes, n => n.Path == "Alpha:One:Deep");

        vm.ToggleNodeCommand.Execute(vm.Nodes.Single(n => n.Path == "Alpha:One"));

        // The collapse took effect — and only for that node.
        Assert.DoesNotContain(vm.Nodes, n => n.Path == "Alpha:One:Deep");
        Assert.Contains(vm.Nodes, n => n.Path == "Alpha:One");
        Assert.Contains(vm.Nodes, n => n.Path == "Beta:One");
    }

    /// <summary>
    /// The two trees are not the same tree, so what was collapsed in one does not follow you into
    /// the other — in either direction. A section closed while searching that stayed closed
    /// afterwards would be a tree that looks half empty for no visible reason.
    /// </summary>
    [Fact]
    public void Collapse_state_does_not_leak_between_the_filtered_and_unfiltered_trees()
    {
        var vm = Open("""{ "Alpha": { "One": { "Deep": 1 } }, "Beta": { "One": 3 } }""");

        vm.Filter = "One";
        vm.ToggleNodeCommand.Execute(vm.Nodes.Single(n => n.Path == "Alpha:One"));
        Assert.DoesNotContain(vm.Nodes, n => n.Path == "Alpha:One:Deep");

        // Out of the filter: the unfiltered tree never had anything collapsed.
        vm.Filter = string.Empty;
        Assert.Contains(vm.Nodes, n => n.Path == "Alpha:One:Deep");

        // And back into it: a changed filter is a different tree, so it opens expanded again.
        vm.Filter = "One";
        Assert.Contains(vm.Nodes, n => n.Path == "Alpha:One:Deep");
    }

    [Fact]
    public void Collapsing_a_section_outside_a_filter_still_works_the_way_it_did()
    {
        var vm = Open("""{ "Alpha": { "One": { "Deep": 1 } } }""");

        vm.ToggleNodeCommand.Execute(vm.Nodes.Single(n => n.Path == "Alpha"));
        Assert.DoesNotContain(vm.Nodes, n => n.Path == "Alpha:One");

        vm.ExpandAllCommand.Execute(null);
        Assert.Contains(vm.Nodes, n => n.Path == "Alpha:One:Deep");
    }
}

/// <summary>
/// A tier Vault could not serve keeps its column and says so. Dropping it would quietly turn a
/// four-way comparison into a three-way one; filling it with "missing" would invent hundreds of
/// findings that nobody has established.
/// </summary>
[Collection("sample-files")]
public sealed class UnavailableTierTests(SampleFiles files)
{
    private MultiDiff WithBetaUnavailable() => MultiDiff.Build(
        [
            new TierColumn("stage", files.Stage.Flat),
            new TierColumn("beta", null),
            new TierColumn("prod", files.Prod.Flat),
        ],
        files.Aliases);

    [Fact]
    public void It_keeps_its_column_in_its_configured_position()
    {
        Assert.Equal(["stage", "beta", "prod"], WithBetaUnavailable().TierIds);
    }

    [Fact]
    public void Its_cells_read_as_unknown_rather_than_as_missing()
    {
        var row = WithBetaUnavailable().Rows.First(r => r.Path == "AccountSettings:ProxyUrl");

        var beta = row.Cell("beta");
        Assert.Equal(CellState.Unavailable, beta.State);
        Assert.False(beta.IsKnown);
        Assert.Equal("?", beta.Display);

        // And that is not a finding: the row is not reported as having a gap in it.
        Assert.False(row.AnyMissing);
    }

    /// <summary>
    /// The judgement a row makes is over the tiers that answered. A key stage and prod agree on is
    /// identical whether or not beta could be read — the alternative would fill the grid with
    /// differences that were never established.
    /// </summary>
    [Fact]
    public void Rows_are_judged_over_the_tiers_that_answered()
    {
        var diff = WithBetaUnavailable();

        var agreed = diff.Rows.Where(r =>
            r.Cell("stage").State == CellState.Present &&
            r.Cell("prod").State == CellState.Present &&
            string.Equals(r.Cell("stage").Leaf!.ComparableValue, r.Cell("prod").Leaf!.ComparableValue,
                StringComparison.Ordinal)).ToArray();

        Assert.NotEmpty(agreed);
        Assert.All(agreed, r => Assert.True(r.Identical, r.Path));
    }

    /// <summary>
    /// A rolled-up "missing from" never names a tier that was not read, so nothing offers to promote
    /// a subtree into a tier whose contents are unknown.
    /// </summary>
    [Fact]
    public void No_rollup_claims_a_subtree_is_missing_from_an_unavailable_tier()
    {
        var root = DiffNode.Build(WithBetaUnavailable());

        Assert.All(root.DescendantsAndSelf(),
            n => Assert.DoesNotContain("beta", n.UniformlyMissingFrom ?? []));
    }
}
