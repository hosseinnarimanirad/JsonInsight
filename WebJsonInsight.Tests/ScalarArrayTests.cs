using Bunit;
using JsonInsight.Editing;
using JsonInsight.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using WebJsonInsight.Components.Tabs;
using WebJsonInsight.Platform;

namespace WebJsonInsight.Tests;

/// <summary>
/// An array of scalars is one value, not a set of rows.
///
/// <para>
/// The case these are written against is real:
/// <c>configuration:app:android:forceUpdate:body</c> is five strings of release-note text. It used to
/// become five tree rows called <c>[0]</c> to <c>[4]</c> and five grid rows, and every one of those
/// names said nothing — the elements have no identity, nothing hangs below them, and the only useful
/// way to read or change the thing is as one list.
/// </para>
///
/// <para>
/// Arrays of objects are the opposite case and keep their rows: <c>Serilog:WriteTo[Name=Console]</c>
/// names something, and there is a <c>Path</c> underneath it worth navigating to.
/// </para>
/// </summary>
public sealed class ScalarArrayTests : TestContext
{
    private const string BodyPath = "configuration:app:android:forceUpdate:body";

    public ScalarArrayTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private MainVm Seeded()
    {
        var main = Fixtures.NewMain();
        Services.AddSingleton(main);
        Services.AddSingleton(new DialogService(main));
        return main;
    }

    // ------------------------------------------------------------------ tree

    /// <summary>The one the change was asked for: body is a leaf, and there is no [0]…[4] under it.</summary>
    [Fact]
    public void A_string_array_is_one_leaf_in_the_hierarchy()
    {
        var editor = Seeded().JsonEditor!;
        editor.Filter = "forceUpdate";

        var body = editor.Nodes.Single(n => n.Path == BodyPath);

        Assert.False(body.IsContainer);
        Assert.True(body.IsScalarArray);
        Assert.DoesNotContain(editor.Nodes, n => n.Path.StartsWith($"{BodyPath}[", StringComparison.Ordinal));
    }

    /// <summary>Numbers and booleans too — a list is a list whatever it holds, as long as it holds no structure.</summary>
    [Theory]
    [InlineData("configuration:ports")]
    public void A_number_array_is_one_leaf_too(string path)
    {
        var editor = Seeded().JsonEditor!;
        editor.Filter = "ports";

        var node = editor.Nodes.Single(n => n.Path == path);

        Assert.True(node.IsScalarArray);
        Assert.DoesNotContain(editor.Nodes, n => n.Path.StartsWith($"{path}[", StringComparison.Ordinal));
    }

    /// <summary>
    /// An array of objects keeps its element rows. That is the case the expander earns its place in:
    /// the elements have identity and there is something underneath them.
    /// </summary>
    [Fact]
    public void An_object_array_still_expands()
    {
        var editor = Seeded().JsonEditor!;

        var sinks = editor.Nodes.Single(n => n.Path == "Serilog:WriteTo");

        Assert.True(sinks.IsContainer);
        Assert.False(sinks.IsScalarArray);
        Assert.Contains(editor.Nodes, n => n.Path.StartsWith("Serilog:WriteTo[", StringComparison.Ordinal));
    }

    /// <summary>An unkeyed array of objects keeps its rows as well, named by position.</summary>
    [Fact]
    public void An_unkeyed_object_array_still_expands()
    {
        var editor = Seeded().JsonEditor!;
        editor.Filter = "banners";

        Assert.Contains(editor.Nodes, n => n.Path.StartsWith("configuration:banners[", StringComparison.Ordinal));
    }

    /// <summary>The row says how many and shows enough of the list to recognise it.</summary>
    [Fact]
    public void The_row_says_how_many_items_and_previews_them()
    {
        var editor = Seeded().JsonEditor!;
        editor.Filter = "forceUpdate";

        var body = editor.Nodes.Single(n => n.Path == BodyPath);

        Assert.Equal("5 items", body.Summary);
        Assert.StartsWith("[", body.Preview, StringComparison.Ordinal);
        Assert.Contains("+3 more", body.Preview, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- the pane

    /// <summary>
    /// A leaf in the tree, but NOT applied as you type. An array is invalid JSON for as long as it
    /// takes to type one, so committing on every keystroke would either fail constantly or destroy the
    /// node. This is the distinction IsScalarArray exists for.
    /// </summary>
    [Fact]
    public void A_collapsed_array_commits_with_Update_node_rather_than_as_you_type()
    {
        var main = Seeded();
        var editor = main.JsonEditor!;
        editor.Filter = "forceUpdate";
        editor.SelectedNode = editor.Nodes.Single(n => n.Path == BodyPath);

        Assert.False(editor.SelectedIsScalar);
        Assert.Contains("Press Update node", editor.CommitHint, StringComparison.Ordinal);

        var page = RenderComponent<TierEditorTab>(p => p.Add(c => c.Vm, editor));
        Assert.Contains("Press Update node", page.Markup, StringComparison.Ordinal);
    }

    /// <summary>Selecting it puts the whole array in the pane, which is where it is edited.</summary>
    [Fact]
    public void Selecting_it_shows_the_whole_array_as_json()
    {
        var editor = Seeded().JsonEditor!;
        editor.Filter = "forceUpdate";
        editor.SelectedNode = editor.Nodes.Single(n => n.Path == BodyPath);

        Assert.StartsWith("[", editor.EditorText.TrimStart(), StringComparison.Ordinal);
        Assert.EndsWith("]", editor.EditorText.TrimEnd(), StringComparison.Ordinal);
    }

    /// <summary>
    /// And it is still editable as one node: replacing the list applies, and marks the row edited.
    /// A leaf you cannot change would be worse than five rows you can.
    /// </summary>
    [Fact]
    public void The_whole_array_can_be_replaced_in_one_edit()
    {
        var editor = Seeded().JsonEditor!;
        editor.Filter = "forceUpdate";
        editor.SelectedNode = editor.Nodes.Single(n => n.Path == BodyPath);

        editor.EditorText = """["only one note"]""";
        Assert.True(editor.CanApply);

        editor.ApplyCommand.Execute(null);

        Assert.Null(editor.Error);
        Assert.Equal(NodeChange.Edited, editor.Nodes.Single(n => n.Path == BodyPath).Change);
        Assert.Equal("1 item", editor.Nodes.Single(n => n.Path == BodyPath).Summary);
    }

    /// <summary>
    /// Removable, unlike an array element. body is a key, and deleting a key shifts nothing — which is
    /// the reason Remove node is refused for [0] and allowed here.
    /// </summary>
    [Fact]
    public void A_collapsed_array_can_be_removed()
    {
        var editor = Seeded().JsonEditor!;
        editor.Filter = "forceUpdate";
        editor.SelectedNode = editor.Nodes.Single(n => n.Path == BodyPath);

        Assert.True(editor.CanRemoveNode);
    }

    // ------------------------------------------------------------- the grid

    /// <summary>
    /// The grid agrees with the tree, which was the point of doing this in the flattener rather than
    /// only in the editor: one row for body, not five, so the two screens name it the same way.
    /// </summary>
    [Fact]
    public void The_grid_shows_one_row_for_it_too()
    {
        var main = Seeded();

        var leaves = main.Documents
            .First(d => d.Id == "dev")
            .Flat
            .Subtree(BodyPath)
            .ToArray();

        Assert.Single(leaves);
        Assert.Equal(BodyPath, leaves[0].Path);
        Assert.True(leaves[0].IsSet);
    }

    /// <summary>
    /// The document is written back exactly as it was read. Sorting happens for comparison only, so a
    /// round trip must not reorder the list — these are release notes, and their order is the order
    /// somebody wrote them in.
    /// </summary>
    [Fact]
    public void Comparison_sorts_but_the_document_keeps_its_order()
    {
        var main = Seeded();
        var dev = main.Documents.First(d => d.Id == "dev");

        var written = JsonInsight.Promote.OrdinalJsonWriter.SerializeToText(dev.Root);
        var first = written.IndexOf("مورد اول", StringComparison.Ordinal);
        var second = written.IndexOf("مورد دوم", StringComparison.Ordinal);

        Assert.True(first > 0 && second > first, "the array was reordered on the way out");

        // And the text survived unescaped on the way through: the default JavaScriptEncoder would
        // have rewritten every one of these as \uXXXX, which is a second way this could pass while
        // the bytes were wrong.
        Assert.DoesNotContain("\\u", written, StringComparison.Ordinal);
    }
}
