using JsonInsight.ViewModels;

namespace JsonInsight.Tests;

/// <summary>
/// The Tier editor's pane header names the selected node by its path, the way the All tiers strip
/// does, and copies the path — never the value. In the sample-files collection because it swaps the
/// static <c>Platform.Clipboard</c>.
/// </summary>
[Collection("sample-files")]
public sealed class EditorPathTests
{
    private static readonly SampleFiles Fixtures = new();

    private static JsonEditorVm Open()
    {
        var main = new MainVm(vaultAtStartup: false);
        main.Seed(Fixtures.Documents);
        var vm = main.JsonEditor!;
        vm.ExpandAllCommand.Execute(null);
        return vm;
    }

    [Fact]
    public void The_header_names_the_selected_node_by_its_path()
    {
        var vm = Open();
        Assert.StartsWith("No node selected", vm.PathLabel, StringComparison.Ordinal);

        var node = vm.Nodes.First(n => n.Path.Contains(':') && !n.IsContainer);
        vm.SelectedNode = node;

        Assert.Equal(node.Path, vm.PathLabel);
        Assert.Equal(node.Path, vm.SelectedPath);

        vm.SelectedNode = vm.Nodes.First(n => n.Path.Length == 0);
        Assert.EndsWith("whole document", vm.PathLabel, StringComparison.Ordinal);
        Assert.Equal(string.Empty, vm.SelectedPath);
    }

    [Fact]
    public void Copying_takes_the_path_not_the_value()
    {
        var clipboard = new Capturing();
        JsonInsight.Platform.Platform.Clipboard = clipboard;

        try
        {
            var vm = Open();
            var node = vm.Nodes.First(n => n.Path.Contains(':') && !n.IsContainer);
            vm.SelectedNode = node;

            vm.CopySelectedPathCommand.Execute(null);

            Assert.Equal(node.Path, clipboard.Text);
        }
        finally
        {
            JsonInsight.Platform.Platform.Reset();
        }
    }

    private sealed class Capturing : JsonInsight.Platform.IClipboard
    {
        public string Text { get; private set; } = string.Empty;

        public void SetText(string text) => Text = text;
    }
}
