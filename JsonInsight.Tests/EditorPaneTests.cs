using System.Text.Json.Nodes;
using JsonInsight.Editing;
using JsonInsight.ViewModels;

namespace JsonInsight.Tests;

/// <summary>
/// The Tier editor's pane: how it commits, what the tree says afterwards, and find/replace over the
/// text. Built against real view models rather than the editor alone, because two of these are about
/// what happens between the pane and the tree beside it.
/// </summary>
public sealed class EditorPaneTests
{
    private static readonly SampleFiles Fixtures = new();

    private static JsonEditorVm Open(string tierId = "dev")
    {
        // Seeded rather than loaded: a tier is a Vault secret, and nothing in this suite reaches one.
        var main = new MainVm(vaultAtStartup: false);
        main.Seed(Fixtures.Documents);

        var vm = main.JsonEditor!;
        vm.Tier = vm.Tiers.First(t => t.Id.Equals(tierId, StringComparison.OrdinalIgnoreCase));
        return vm;
    }

    private static (JsonEditorVm Vm, string Victim) RemoveKeyFromParentText(string parent)
    {
        var vm = Open();
        vm.SelectedNode = vm.Nodes.First(n => n.Path == parent);

        var obj = (JsonObject)vm.Editor!.Find(parent)!;
        var victim = obj.Select(p => p.Key).First();

        var copy = (JsonObject)obj.DeepClone();
        copy.Remove(victim);

        vm.EditorText = copy.ToJsonString();
        vm.ApplyCommand.Execute(null);

        return (vm, victim);
    }

    /// <summary>
    /// A key deleted by retyping its parent has to read as a tombstone, exactly like one deleted with
    /// the Remove node button. It is the same edit; which route it took is not something the tree
    /// should be able to tell you apart by hiding one of them.
    /// </summary>
    [Fact]
    public void A_key_removed_by_retyping_its_parent_shows_as_a_tombstone()
    {
        var (vm, victim) = RemoveKeyFromParentText("Redis");

        var row = vm.Nodes.FirstOrDefault(n => n.Path == $"Redis:{victim}");

        Assert.NotNull(row);
        Assert.Equal(NodeChange.Removed, row!.Change);
        Assert.True(row.IsRemoved);
    }

    /// <summary>
    /// The filter searched the edited document only, so a removed key — which is by definition not in
    /// it — could not match, and typing anything in the search box made every tombstone disappear.
    /// The one edit you cannot see was also the one you could not take back.
    /// </summary>
    [Fact]
    public void A_search_filter_does_not_hide_tombstones()
    {
        var (vm, victim) = RemoveKeyFromParentText("Redis");

        vm.Filter = "Redis";

        Assert.Contains(vm.Nodes, n => n.Path == $"Redis:{victim}" && n.IsRemoved);
    }

    /// <summary>
    /// Find and Holds disagree about a key set to JSON null, and Holds is the one that is right.
    /// Every one of these tiers has two such keys, and selecting one used to report it as missing
    /// from a document it is plainly in.
    /// </summary>
    [Fact]
    public void A_key_holding_json_null_opens_in_the_pane()
    {
        var vm = Open();
        var row = vm.Nodes.First(n => n.Path.EndsWith(":Branch", StringComparison.Ordinal));

        vm.SelectedNode = row;

        Assert.Null(vm.Error);
        Assert.Equal("null", vm.EditorText);
        Assert.False(vm.CanApply);
    }

    [Fact]
    public void A_scalar_applies_as_it_is_typed_without_the_button()
    {
        var vm = Open();
        var path = vm.Nodes.First(n => n.Path == "Redis:Database").Path;
        vm.SelectedNode = vm.Nodes.First(n => n.Path == path);

        Assert.True(vm.SelectedIsScalar);

        vm.EditorText = "2";

        Assert.Equal("2", vm.Editor!.TextAt(path));
        Assert.True(vm.IsModified);

        // The button goes quiet the moment the pane matches the document again, which is what says
        // it landed - and the row it edited carries the mark.
        Assert.False(vm.CanApply);
        Assert.Equal(NodeChange.Edited, vm.Nodes.First(n => n.Path == path).Change);
    }

    /// <summary>
    /// The rows must survive an as-you-type edit. Rebuilding the tree replaces every row, which
    /// reselects, which reloads the pane — and throws the caret back to the start of the value being
    /// typed after every keystroke.
    /// </summary>
    [Fact]
    public void Typing_into_a_scalar_does_not_rebuild_the_tree()
    {
        var vm = Open();
        vm.SelectedNode = vm.Nodes.First(n => n.Path == "Redis:Database");

        var rowsBefore = vm.Nodes.ToArray();
        var selected = vm.SelectedNode;

        vm.EditorText = "7";

        Assert.Same(selected, vm.SelectedNode);
        Assert.Equal(rowsBefore.Length, vm.Nodes.Count);
        Assert.True(rowsBefore.SequenceEqual(vm.Nodes), "the tree was rebuilt");
    }

    /// <summary>
    /// A value typed one character at a time is one edit, not one per pause in typing. Undo has to
    /// reach what the value was before the typing started.
    /// </summary>
    [Fact]
    public void A_run_of_keystrokes_on_one_value_is_a_single_undo_step()
    {
        var vm = Open();
        vm.SelectedNode = vm.Nodes.First(n => n.Path == "Redis:Database");

        var before = vm.Editor!.TextAt("Redis:Database");

        vm.EditorText = "1";
        vm.EditorText = "12";
        vm.EditorText = "123";

        Assert.Single(vm.Editor.History);

        vm.UndoCommand.Execute(null);
        Assert.Equal(before, vm.Editor.TextAt("Redis:Database"));
        Assert.False(vm.IsModified);
    }

    /// <summary>
    /// Moving away and coming back is a second edit. Folding it into the first would make one undo
    /// step cover two separate things someone did minutes apart.
    /// </summary>
    [Fact]
    public void Leaving_the_node_and_returning_starts_a_new_undo_step()
    {
        var vm = Open();

        vm.SelectedNode = vm.Nodes.First(n => n.Path == "Redis:Database");
        vm.EditorText = "3";

        vm.SelectedNode = vm.Nodes.First(n => n.Path == "Redis:Enabled");

        vm.SelectedNode = vm.Nodes.First(n => n.Path == "Redis:Database");
        vm.EditorText = "4";

        Assert.Equal(2, vm.Editor!.History.Count);
    }

    /// <summary>
    /// A container is invalid JSON for as long as it takes to type one, so it keeps the button —
    /// applying as you type would either fail on every keystroke or destroy the node.
    /// </summary>
    [Fact]
    public void A_container_still_waits_for_update_node()
    {
        var vm = Open();
        vm.SelectedNode = vm.Nodes.First(n => n.Path == "Redis");

        Assert.False(vm.SelectedIsScalar);

        var was = vm.Editor!.TextAt("Redis");
        vm.EditorText = "{ \"Database\": 9 }";

        Assert.Equal(was, vm.Editor.TextAt("Redis"));
        Assert.True(vm.CanApply);
        Assert.False(vm.IsModified);
    }

    /// <summary>Half-typed text is not an error, it is a value on its way in. It just does not commit.</summary>
    [Fact]
    public void Text_that_does_not_parse_yet_commits_nothing_and_raises_nothing()
    {
        var vm = Open();
        vm.SelectedNode = vm.Nodes.First(n => n.Path == "Redis:ConnectionString");

        var was = vm.Editor!.TextAt("Redis:ConnectionString");
        vm.EditorText = "\"unterminated";

        Assert.Equal(was, vm.Editor.TextAt("Redis:ConnectionString"));
        Assert.Null(vm.Error);
        Assert.True(vm.CanApply);
    }

    // ------------------------------------------------------------ find/replace

    [Theory]
    [InlineData("aXbXc", "X", 0, 1)]
    [InlineData("aXbXc", "X", 2, 3)]
    [InlineData("aXbXc", "X", 4, 1)]   // wraps
    [InlineData("aXbXc", "z", 0, -1)]
    public void Find_next_wraps_at_the_end(string text, string term, int from, int expected)
    {
        Assert.Equal(expected, TextFinder.Next(text, term, from, matchCase: true));
    }

    [Theory]
    [InlineData("aXbXc", "X", 5, 3)]
    [InlineData("aXbXc", "X", 3, 1)]
    [InlineData("aXbXc", "X", 1, 3)]   // wraps
    public void Find_previous_wraps_at_the_start(string text, string term, int before, int expected)
    {
        Assert.Equal(expected, TextFinder.Previous(text, term, before, matchCase: true));
    }

    [Fact]
    public void Case_sensitivity_is_a_real_choice_here()
    {
        const string text = "\"Url\": \"x\", \"URL\": \"y\"";

        Assert.Equal(1, TextFinder.Count(text, "Url", matchCase: true));
        Assert.Equal(2, TextFinder.Count(text, "Url", matchCase: false));
    }

    [Fact]
    public void Replace_all_reports_what_it_replaced()
    {
        var (text, count) = TextFinder.ReplaceAll("a.b.c", ".", "-", matchCase: true);

        Assert.Equal("a-b-c", text);
        Assert.Equal(2, count);
    }

    /// <summary>Replacing a term with something containing it must terminate rather than run away.</summary>
    [Fact]
    public void Replace_all_does_not_reprocess_what_it_inserted()
    {
        var (text, count) = TextFinder.ReplaceAll("aaa", "a", "aa", matchCase: true);

        Assert.Equal("aaaaaa", text);
        Assert.Equal(3, count);
    }

    [Fact]
    public void The_match_counter_reads_as_position_of_total()
    {
        const string text = "xAxAxA";

        Assert.Equal(3, TextFinder.Count(text, "A", matchCase: true));
        Assert.Equal(2, TextFinder.Ordinal(text, "A", 3, matchCase: true));
        Assert.Equal(0, TextFinder.Ordinal(text, "A", 2, matchCase: true));
    }
}
