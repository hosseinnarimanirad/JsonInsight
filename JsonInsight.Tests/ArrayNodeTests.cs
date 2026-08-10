using System.Text.Json.Nodes;
using JsonInsight.Editing;
using JsonInsight.ViewModels;

namespace JsonInsight.Tests;

/// <summary>
/// Arrays in the Tier editor's hierarchy. The tree used to stop at one and show only its length,
/// which for these documents meant several hundred lines of JSON were reachable only as text.
/// </summary>
[Collection("sample-files")]
public sealed class ArrayNodeTests(SampleFiles files)
{
    private static readonly SampleFiles Fixtures = new();

    private static JsonEditorVm Open(string tierId)
    {
        // Seeded rather than loaded: a tier is a Vault secret, and nothing in this suite reaches one.
        var main = new MainVm(vaultAtStartup: false);
        main.Seed(Fixtures.Documents);

        var vm = main.JsonEditor!;
        vm.Tier = vm.Tiers.First(t => t.Id.Equals(tierId, StringComparison.OrdinalIgnoreCase));
        return vm;
    }

    /// <summary>
    /// Serilog:WriteTo declares an identity field, so its elements are named by it — the same path
    /// the flattener produces and the All tiers grid shows, rather than a second spelling of it.
    /// </summary>
    [Fact]
    public void A_keyed_array_shows_its_elements_by_identity()
    {
        var vm = Open("stage");

        var console = vm.Nodes.FirstOrDefault(n => n.Path == "Serilog:WriteTo[Name=Console]");

        Assert.NotNull(console);
        Assert.Equal("[Name=Console]", console!.Name);

        // Exactly the paths the flattener produced for the same elements.
        var fromGrid = files.Stage.Flat.Paths
            .Where(p => p.StartsWith("Serilog:WriteTo[", StringComparison.Ordinal))
            .Select(p => p.Split(':')[1])
            .Distinct(StringComparer.Ordinal);

        var fromTree = vm.Nodes
            .Where(n => n.Path.StartsWith("Serilog:WriteTo[", StringComparison.Ordinal))
            .Select(n => n.Path.Split(':')[1])
            .Distinct(StringComparer.Ordinal);

        Assert.Equal(fromGrid.Order(StringComparer.Ordinal), fromTree.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void An_element_opens_in_the_pane_like_any_other_node()
    {
        var vm = Open("stage");
        vm.SelectedNode = vm.Nodes.First(n => n.Path == "Serilog:WriteTo[Name=Console]");

        Assert.Null(vm.Error);
        Assert.Contains("\"Console\"", vm.EditorText, StringComparison.Ordinal);
        Assert.True(vm.SelectedIsElement);
    }

    /// <summary>
    /// Replacing an element is in place, so nothing after it moves — which is what makes it safe
    /// where removing one is not.
    /// </summary>
    [Fact]
    public void An_element_can_be_replaced_in_place()
    {
        var vm = Open("stage");
        var path = "Serilog:WriteTo[Name=Console]";
        vm.SelectedNode = vm.Nodes.First(n => n.Path == path);

        var before = ((JsonArray)vm.Editor!.Find("Serilog:WriteTo")!).Count;

        vm.EditorText = """{ "Name": "Console", "Args": { "theme": "none" } }""";
        Assert.True(vm.ApplyCommand.CanExecute(null));
        vm.ApplyCommand.Execute(null);

        Assert.Null(vm.Error);
        Assert.Equal(before, ((JsonArray)vm.Editor.Find("Serilog:WriteTo")!).Count);
        Assert.Contains("\"theme\"", vm.Editor.TextAt(path), StringComparison.Ordinal);

        // The element carries its own mark, and so does the array above it.
        Assert.Equal(NodeChange.Mixed, vm.Nodes.First(n => n.Path == path).Change);
        Assert.NotEqual(NodeChange.None, vm.Nodes.First(n => n.Path == "Serilog:WriteTo").Change);
    }

    [Fact]
    public void An_untouched_element_carries_no_mark()
    {
        var vm = Open("stage");
        vm.SelectedNode = vm.Nodes.First(n => n.Path == "Serilog:WriteTo[Name=Console]");

        vm.EditorText = """{ "Name": "Console", "Args": { "theme": "none" } }""";
        vm.ApplyCommand.Execute(null);

        var siblings = vm.Nodes
            .Where(n => n.Path.StartsWith("Serilog:WriteTo[", StringComparison.Ordinal) &&
                        n.Path != "Serilog:WriteTo[Name=Console]" &&
                        n.Depth == vm.Nodes.First(x => x.Path == "Serilog:WriteTo[Name=Console]").Depth)
            .ToArray();

        Assert.NotEmpty(siblings);
        Assert.All(siblings, s => Assert.Equal(NodeChange.None, s.Change));
    }

    /// <summary>
    /// Removing from the middle of an array moves every element after it, and this editor shows a
    /// deletion as a tombstone measured against the document as opened — so one removal would
    /// present as "one gone and all the rest rewritten". It is refused, and says why.
    /// </summary>
    [Fact]
    public void An_element_cannot_be_removed_on_its_own()
    {
        var vm = Open("stage");
        vm.SelectedNode = vm.Nodes.First(n => n.Path == "Serilog:WriteTo[Name=Console]");

        Assert.False(vm.CanRemoveNode);

        var direct = Assert.Throws<InvalidOperationException>(
            () => vm.Editor!.Remove("Serilog:WriteTo[Name=Console]"));

        Assert.Contains("shifts every element after it", direct.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_element_reverts_without_disturbing_its_siblings()
    {
        var vm = Open("stage");
        var path = "Serilog:WriteTo[Name=Console]";
        var editor = vm.Editor!;
        var before = editor.TextAt(path);

        vm.SelectedNode = vm.Nodes.First(n => n.Path == path);
        vm.EditorText = """{ "Name": "Console", "Args": { "theme": "none" } }""";
        vm.ApplyCommand.Execute(null);

        vm.SelectedNode = vm.Nodes.First(n => n.Path == path);
        vm.RevertNodeCommand.Execute(null);

        Assert.Null(vm.Error);
        Assert.Equal(before, vm.Editor!.TextAt(path));
        Assert.False(vm.IsModified);
    }

    /// <summary>
    /// A declared <c>stringSet</c> is one value, so it is one row — not a row per string.
    ///
    /// <para>
    /// This is the flattener's rule rather than the tree's own: a Couchbase <c>Scopes</c> list
    /// flattens to a single leaf, so the All tiers grid has one row for it. The tree expanded it
    /// anyway, which meant the same array was one row on one tab and four on another. It now asks
    /// <see cref="ArrayStrategies.IsSingleLeaf"/> — the question the flattener asks — so the two
    /// cannot drift apart again.
    /// </para>
    /// </summary>
    [Fact]
    public void A_string_set_array_is_one_leaf_rather_than_a_row_per_element()
    {
        var vm = Open("dev");
        var scopes = vm.Nodes.FirstOrDefault(n => n.Path.EndsWith(":Scopes:account", StringComparison.Ordinal));

        Assert.NotNull(scopes);
        Assert.False(scopes!.IsContainer);
        Assert.DoesNotContain(vm.Nodes, n => n.Path.StartsWith(scopes.Path + "[", StringComparison.Ordinal));
    }

    /// <summary>
    /// The other half of the same rule: an array the flattener splits into elements still expands, so
    /// nothing is lost for the arrays that behaviour was written for.
    /// </summary>
    [Fact]
    public void An_array_of_objects_still_expands()
    {
        var vm = Open("dev");
        var sinks = vm.Nodes.First(n => n.Path == "Serilog:WriteTo");

        Assert.True(sinks.IsContainer);
        Assert.Contains(vm.Nodes, n => n.Path.StartsWith("Serilog:WriteTo[", StringComparison.Ordinal));
    }

    /// <summary>
    /// The clipboard is apartment-threaded, so this runs where a WPF app runs. Without it the copy
    /// fails on the test thread for a reason that has nothing to do with the copy.
    ///
    /// <para>
    /// The host's clipboard is registered here the same way <c>App.OnStartup</c> registers it, so what
    /// this asserts is still a real round-trip through Windows rather than a fake agreeing with
    /// itself. Registering nothing would leave <c>Platform</c> on its do-nothing default and the test
    /// would fail for the one reason that would not be a bug.
    /// </para>
    /// </summary>
    [Fact]
    public void Copying_a_node_puts_its_json_on_the_clipboard()
    {
        var (error, message, expected, actual) = OnStaThread(() =>
        {
            JsonInsight.Themes.WpfPlatform.Register();

            try
            {
                var vm = Open("stage");
                vm.SelectedNode = vm.Nodes.First(n => n.Path == "Serilog:WriteTo[Name=Console]");

                var text = vm.EditorText;
                vm.CopyNodeCommand.Execute(null);

                return (vm.Error, vm.Message, text, System.Windows.Clipboard.GetText());
            }
            finally
            {
                // Static for the life of the process, so a host left registered here would still be
                // registered in whichever test ran next.
                JsonInsight.Platform.Platform.Reset();
            }
        });

        Assert.Null(error);
        Assert.Contains("Copied Serilog:WriteTo[Name=Console]", message!, StringComparison.Ordinal);
        Assert.Equal(expected, actual);
    }

    private static T OnStaThread<T>(Func<T> action)
    {
        T? result = default;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return failure is null ? result! : throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
