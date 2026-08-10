using Bunit;
using JsonInsight.Editing;
using JsonInsight.Vault;
using JsonInsight.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using WebJsonInsight.Components.Tabs;
using WebJsonInsight.Platform;

namespace WebJsonInsight.Tests;

/// <summary>
/// Every screen rendered against real view models, which is the Blazor half of what
/// <c>JsonInsight.Tests.UiSmokeTests</c> does for WPF and exists for the same reason: a green compile
/// proves nothing about whether a component renders. A missing property, a null the markup did not
/// expect, a cast that throws in a loop body — all of them are runtime faults, and all of them are
/// caught here rather than by opening the window.
///
/// <para>
/// The WPF suite has to show its views off-screen, because an unshown window never realises its
/// DataGrid rows and a cell template that throws cannot fail. bUnit has no such gap: rendering
/// produces the markup, so every row of every grid really is built.
/// </para>
/// </summary>
public sealed class RenderTests : TestContext
{
    public RenderTests()
    {
        // Loose mode: these components call into jsonInsight.* for the things only a browser can do —
        // caret position, selecting a find result, focusing the find bar. None of them is what a
        // render test is checking, and a strict harness would turn every one into a failure about
        // interop rather than about the component.
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private MainVm Seeded()
    {
        var main = Fixtures.NewMain();
        Services.AddSingleton(main);

        // The All tiers and Tier editor tabs inject it for their Edit / Promote / Push buttons. It
        // opens nothing on its own, so a render test gets a real one rather than a stand-in.
        Services.AddSingleton(new DialogService(main));
        return main;
    }

    [Fact]
    public void All_tiers_renders_a_row_per_path_and_a_column_per_source()
    {
        var main = Seeded();

        var page = RenderComponent<AllTiersTab>(p => p.Add(c => c.Vm, main.Tiers));

        // One header cell per source, plus the path column and the actions column.
        var headers = page.FindAll("thead th");
        Assert.Equal(main.Tiers!.Diff.TierIds.Count + 2, headers.Count);
        Assert.Contains("dev", page.Markup, StringComparison.Ordinal);
        Assert.Contains("stage", page.Markup, StringComparison.Ordinal);
        Assert.Contains("beta", page.Markup, StringComparison.Ordinal);

        Assert.NotEmpty(page.FindAll("tbody tr"));
    }

    /// <summary>
    /// The rolled-up row: a subtree missing wholesale from the same tiers is one finding, so it is one
    /// row saying how many keys and where they are — not eleven rows each saying the same thing.
    /// </summary>
    [Fact]
    public void A_subtree_missing_from_every_other_tier_collapses_to_one_row()
    {
        var main = Seeded();

        var page = RenderComponent<AllTiersTab>(p => p.Add(c => c.Vm, main.Tiers));

        Assert.Contains("only in dev", page.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// A source Vault could not serve keeps its column and its cells read "?" rather than the em dash
    /// that means missing. "I could not ask" is the absence of a finding; rendering it like a gap
    /// would fill the grid with hundreds of differences nobody has established.
    /// </summary>
    [Fact]
    public void An_unavailable_source_keeps_its_column_and_reads_as_unknown()
    {
        var main = Fixtures.NewMain(
            [Fixtures.Dev, Fixtures.Stage],
            [Fixtures.Unavailable("beta", "403 from Vault: permission denied")]);

        Services.AddSingleton(main);
        Services.AddSingleton(new DialogService(main));

        var page = RenderComponent<AllTiersTab>(p => p.Add(c => c.Vm, main.Tiers));

        Assert.Contains("UNAVAILABLE", page.Markup, StringComparison.Ordinal);
        Assert.NotEmpty(page.FindAll("td.cell-unavailable"));

        // And it takes no part in the comparison: nothing reports it as a gap.
        Assert.DoesNotContain("only in dev, stage", page.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// A secret is never rendered — not in the grid, not in a tooltip. The masked form still carries a
    /// short hash, which is what lets you see whether two tiers hold the same secret.
    /// </summary>
    [Fact]
    public void Secrets_are_masked_in_the_grid()
    {
        var main = Seeded();
        main.Tiers!.Filter = "Password";

        var page = RenderComponent<AllTiersTab>(p => p.Add(c => c.Vm, main.Tiers));

        Assert.DoesNotContain("dev-couchbase-password-value", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("stage-couchbase-password-value", page.Markup, StringComparison.Ordinal);
        Assert.Contains("••••••", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_diff_renders_both_sides()
    {
        var main = Seeded();

        var page = RenderComponent<TextDiffTab>(p => p.Add(c => c.Vm, main.RawDiff));

        Assert.NotEmpty(page.FindAll(".dl-row"));
        Assert.Contains("AS DEV", page.Markup, StringComparison.Ordinal);
        Assert.Contains("AS STAGE", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_files_renders_with_nothing_picked()
    {
        var main = Seeded();

        var page = RenderComponent<CompareFilesTab>(p => p.Add(c => c.Vm, main.JsonCompare));

        Assert.Contains("Pick two JSON files.", page.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every predefined environment gets a row whether or not it has a source, because this tab is
    /// where an environment that has none gets one.
    /// </summary>
    [Fact]
    public void Sources_renders_a_row_per_environment()
    {
        var main = Seeded();

        var page = RenderComponent<SourcesTab>(p => p.Add(c => c.Vm, main.Vault));

        Assert.Equal(main.Vault!.Connections.Count, page.FindAll(".src-row").Count);
        Assert.Contains("test/qa", page.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// A local-file row shows a file picker and no server or token, because it has neither — four
    /// empty boxes that can never be filled in would be worse than saying so.
    /// </summary>
    [Fact]
    public void A_local_file_source_row_shows_no_address_or_token()
    {
        var main = Seeded();
        var row = main.Vault!.Connections.First();
        row.Kind = JsonInsight.Sources.SourceKind.LocalFile;

        var page = RenderComponent<SourcesTab>(p => p.Add(c => c.Vm, main.Vault));

        Assert.NotEmpty(page.FindAll(".src-na"));
    }

    // ------------------------------------------------------------- Tier editor

    /// <summary>
    /// The hierarchy, with array elements as rows of their own. The tree used to stop at an array and
    /// show only "[2]", which meant the only way to see what was in one was to read the whole thing
    /// as text.
    /// </summary>
    [Fact]
    public void Tier_editor_renders_the_hierarchy_including_array_elements()
    {
        var main = Seeded();

        var page = RenderComponent<TierEditorTab>(p => p.Add(c => c.Vm, main.JsonEditor));

        Assert.NotEmpty(page.FindAll(".tree-row"));

        // arrays.json keys Serilog:WriteTo on Name, so the elements are named by identity rather than
        // by position - the same paths the flattener produces.
        Assert.Contains("Serilog", page.Markup, StringComparison.Ordinal);
        Assert.Contains(main.JsonEditor!.Nodes, n => n.Path.StartsWith("Serilog:WriteTo[", StringComparison.Ordinal));
    }

    /// <summary>
    /// Each column says which Vault version it is showing, not merely that it came from Vault.
    ///
    /// <para>
    /// The number is the whole point of it: which KV version a column holds is the difference between
    /// "stage does not have this key" and "stage did not have it an hour ago", and it is what the
    /// push screen refuses on when somebody has uploaded in between.
    /// </para>
    /// </summary>
    [Fact]
    public void All_tiers_headers_name_the_vault_version_each_column_is_showing()
    {
        var main = Seeded();

        var page = RenderComponent<AllTiersTab>(p => p.Add(c => c.Vm, main.Tiers));

        var subs = page.FindAll("thead .th-sub").Select(e => e.TextContent.Trim()).ToArray();

        // The fixtures are read at v12, v34 and v08 — see Fixtures.Dev/Stage/Beta.
        Assert.Contains("Vault v12", subs);
        Assert.Contains("Vault v34", subs);
        Assert.Contains("Vault v08", subs);
    }

    /// <summary>
    /// The picker names every tier's version in the list, not just the selected one's beside it.
    ///
    /// <para>
    /// Which version you are about to edit is part of choosing what to edit — an edit is built
    /// against a version, and a push is refused when somebody has replaced it since — so it belongs
    /// in the options rather than in a chip that can only ever describe one of them.
    /// </para>
    /// </summary>
    [Fact]
    public void The_tier_picker_names_every_tiers_version()
    {
        var main = Seeded();
        var editor = main.JsonEditor!;

        var page = RenderComponent<TierEditorTab>(p => p.Add(c => c.Vm, editor));

        var options = page.FindAll("#ed-tier option").Select(o => o.TextContent.Trim()).ToArray();

        // The fixtures are read at v12, v34 and v08 — see Fixtures.Dev/Stage/Beta.
        Assert.Contains("dev v12", options);
        Assert.Contains("stage v34", options);
        Assert.Contains("beta v08", options);
    }

    /// <summary>A local-file tier has no version to name, and says so rather than inventing one.</summary>
    [Fact]
    public void A_local_file_tier_is_named_as_a_file_rather_than_a_version()
    {
        var document = Fixtures.AsTier("dev", 1, """{"A":1}""");

        var local = new JsonInsight.Model.TierDocument
        {
            Definition = document.Definition,
            Root = document.Root,
            Flat = document.Flat,
            Origin = JsonInsight.Model.TierOrigin.LocalFile,
            FilePath = @"C:\snapshots\dev.json",
        };

        Assert.Equal("dev (file)", local.PickerLabel);
    }

    /// <summary>
    /// The tab opens with nothing selected, because the first row is the whole document and landing on
    /// it would fill the pane with 28 KB of JSON before anyone had asked for anything.
    /// </summary>
    [Fact]
    public void Tier_editor_opens_with_nothing_selected()
    {
        var main = Seeded();

        var page = RenderComponent<TierEditorTab>(p => p.Add(c => c.Vm, main.JsonEditor));

        Assert.Null(main.JsonEditor!.SelectedNode);
        Assert.Empty(page.FindAll(".tree-row.selected"));
    }

    /// <summary>
    /// The hierarchy masks secrets even though the pane beside it does not. That split is deliberate
    /// and is the one place in the app a credential is rendered in clear, so it is worth a test that
    /// the masked half stays masked.
    /// </summary>
    [Fact]
    public void Tier_editor_masks_secrets_in_the_tree()
    {
        var main = Seeded();
        main.JsonEditor!.Filter = "Password";

        var page = RenderComponent<TierEditorTab>(p => p.Add(c => c.Vm, main.JsonEditor));

        Assert.DoesNotContain("dev-couchbase-password-value", page.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// An edit marks its own node and every ancestor, and the ancestors are marked "holds changes"
    /// rather than "edited" — a parent is not an edit, and labelling it as one would be a lie told at
    /// exactly the level you are scanning.
    /// </summary>
    [Fact]
    public void An_edit_marks_its_node_and_its_ancestors_differently()
    {
        var main = Seeded();
        var editor = main.JsonEditor!;

        editor.SelectedNode = editor.Nodes.First(n => n.Path == "PaymentSettings:Hub:Timeout");
        editor.EditorText = "99";

        var page = RenderComponent<TierEditorTab>(p => p.Add(c => c.Vm, editor));

        Assert.Equal(NodeChange.Edited, editor.Nodes.First(n => n.Path == "PaymentSettings:Hub:Timeout").Change);
        Assert.Equal(NodeChange.Mixed, editor.Nodes.First(n => n.Path == "PaymentSettings").Change);

        Assert.NotEmpty(page.FindAll(".mark-edited"));
        Assert.NotEmpty(page.FindAll(".mark-mixed"));
    }

    /// <summary>
    /// A removed node stays in the tree, struck through, until the tier is saved. Dropping it out the
    /// moment it was deleted would make the one edit you cannot see also the one you cannot take back.
    /// </summary>
    [Fact]
    public void A_removed_node_stays_in_the_tree_as_a_tombstone()
    {
        var main = Seeded();
        var editor = main.JsonEditor!;

        editor.SelectedNode = editor.Nodes.First(n => n.Path == "PaymentSettings:Hub:TerminalId");
        editor.RemoveNodeCommand.Execute(null);

        var page = RenderComponent<TierEditorTab>(p => p.Add(c => c.Vm, editor));

        Assert.Contains(editor.Nodes, n => n.Path == "PaymentSettings:Hub:TerminalId" && n.IsRemoved);
        Assert.NotEmpty(page.FindAll(".tree-row.removed"));

        // Removing reselects the PARENT, not the tombstone — after deleting a key you are looking at
        // the section it was in. So the selection has to be put on the tombstone to see the label it
        // carries.
        Assert.Equal("PaymentSettings:Hub", editor.SelectedNode?.Path);

        editor.SelectedNode = editor.Nodes.First(n => n.Path == "PaymentSettings:Hub:TerminalId");

        // The button that says "Undo node changes" elsewhere says Restore node here, because putting a
        // deleted node back is not the same sentence — and it is the same button, so it is not dressed
        // as a different one.
        Assert.Equal("Restore node", editor.RevertNodeLabel);
        Assert.True(editor.CanRevertNode);

        page.Render();
        Assert.Contains("Restore node", page.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// A value applies as you type; a section waits for Update node. The pane says which it is on its
    /// bottom strip rather than leaving it to be inferred from whether a button greyed itself out.
    /// </summary>
    [Theory]
    [InlineData("PaymentSettings:Hub:Timeout", "Applied as you type")]
    [InlineData("PaymentSettings:Hub", "Press Update node")]
    public void The_pane_says_how_it_commits(string path, string expected)
    {
        var main = Seeded();
        var editor = main.JsonEditor!;
        editor.SelectedNode = editor.Nodes.First(n => n.Path == path);

        var page = RenderComponent<TierEditorTab>(p => p.Add(c => c.Vm, editor));

        Assert.Contains(expected, page.Markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// Update node is off while the pane does not parse, and the strip under the pane says why
    /// instead of the commit hint it usually carries.
    ///
    /// <para>
    /// The button used to stay pressable so that pressing it produced the parse error. That made it
    /// the only way to find out, and made an unpressable state look exactly like a pressable one.
    /// </para>
    /// </summary>
    [Fact]
    public void Update_node_is_off_and_says_why_while_the_pane_does_not_parse()
    {
        var main = Seeded();
        var editor = main.JsonEditor!;
        editor.SelectedNode = editor.Nodes.First(n => n.Path == "PaymentSettings:Hub");

        var page = RenderComponent<TierEditorTab>(p => p.Add(c => c.Vm, editor));

        editor.EditorText = "{ \"Timeout\": 99 ";
        page.Render();

        Assert.True(Update(page).HasAttribute("disabled"));
        Assert.Contains("Not valid JSON", page.Find(".commit-hint").TextContent, StringComparison.Ordinal);
        Assert.NotEmpty(page.FindAll(".commit-hint.is-problem"));

        editor.EditorText = "{ \"Timeout\": 99 }";
        page.Render();

        Assert.False(Update(page).HasAttribute("disabled"));
        Assert.Empty(page.FindAll(".commit-hint.is-problem"));
        Assert.Contains("Press Update node", page.Find(".commit-hint").TextContent, StringComparison.Ordinal);
    }

    private static AngleSharp.Dom.IElement Update(IRenderedComponent<TierEditorTab> page) =>
        page.FindAll(".pane-actions > button")
            .Single(b => b.TextContent.Trim().StartsWith("Update node", StringComparison.Ordinal));

    /// <summary>The find bar renders and closes, and its counter reads in a fixed-width slot.</summary>
    [Fact]
    public void The_find_bar_opens_and_closes()
    {
        var main = Seeded();
        var editor = main.JsonEditor!;
        editor.SelectedNode = editor.Nodes.First(n => n.Path == "PaymentSettings");
        editor.FindOpen = true;

        var page = RenderComponent<TierEditorTab>(p => p.Add(c => c.Vm, editor));
        Assert.NotEmpty(page.FindAll(".findbar"));

        editor.FindOpen = false;
        page.Render();
        Assert.Empty(page.FindAll(".findbar"));
    }

    /// <summary>
    /// A switch opens it, so the bar is reachable without knowing Ctrl+F. A shortcut is not a
    /// control, and nothing on the toolbar said the find bar existed.
    /// </summary>
    [Fact]
    public void A_switch_on_the_toolbar_opens_the_find_bar()
    {
        var main = Seeded();
        var editor = main.JsonEditor!;

        var page = RenderComponent<TierEditorTab>(p => p.Add(c => c.Vm, editor));
        Assert.Empty(page.FindAll(".findbar"));

        FindSwitch(page).Click();

        Assert.True(editor.FindOpen);
        Assert.NotEmpty(page.FindAll(".findbar"));

        FindSwitch(page).Click();
        Assert.False(editor.FindOpen);
    }

    /// <summary>
    /// Every match is highlighted and the current one is marked apart from the rest.
    ///
    /// <para>
    /// A textarea cannot colour part of its own content and an unfocused one paints no selection at
    /// all, so stepping through matches could either steal the caret from the find box or show
    /// nothing. The layer behind it is what makes both unnecessary.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_match_is_highlighted_and_the_current_one_stands_out()
    {
        var main = Seeded();
        var editor = main.JsonEditor!;
        editor.SelectedNode = editor.Nodes.First(n => n.Path == "PaymentSettings:Hub");
        editor.FindOpen = true;

        var page = RenderComponent<TierEditorTab>(p => p.Add(c => c.Vm, editor));
        Assert.Empty(page.FindAll(".pane-highlights"));

        editor.EditorText = """{ "a": 1, "b": 1, "c": 1 }""";
        editor.FindText = "1";
        page.Render();

        Assert.Single(page.FindAll(".pane-highlights"));
        Assert.Equal(3, page.FindAll(".pane-highlights .hl").Count);

        // Nothing stepped to yet, so none of them is the current one.
        Assert.Empty(page.FindAll(".hl-current"));

        editor.StepMatch(forward: true);
        page.Render();
        Assert.Single(page.FindAll(".hl-current"));
    }

    /// <summary>
    /// The layer holds the whole string, not only the matches — it lines up with the text in front of
    /// it only if it holds the same characters. A layer of just the hits would put the first mark at
    /// the top-left corner of the pane.
    /// </summary>
    [Fact]
    public void The_highlight_layer_reproduces_the_whole_pane()
    {
        var main = Seeded();
        var editor = main.JsonEditor!;
        editor.SelectedNode = editor.Nodes.First(n => n.Path == "PaymentSettings:Hub");
        editor.FindOpen = true;
        editor.EditorText = """{ "a": 1, "b": 2 }""";
        editor.FindText = "1";

        var page = RenderComponent<TierEditorTab>(p => p.Add(c => c.Vm, editor));

        Assert.Equal(editor.EditorText, page.Find(".pane-highlights").TextContent.TrimEnd('\n'));
    }

    private static AngleSharp.Dom.IElement FindSwitch(IRenderedComponent<TierEditorTab> page) =>
        page.FindAll(".editor-toolbar .switch")
            .Single(b => b.TextContent.Contains("Find", StringComparison.Ordinal));

    /// <summary>Every tab, including the states reached only by toggling something.</summary>
    [Fact]
    public void Every_tab_renders_without_throwing()
    {
        var main = Seeded();

        RenderComponent<AllTiersTab>(p => p.Add(c => c.Vm, main.Tiers));
        RenderComponent<TextDiffTab>(p => p.Add(c => c.Vm, main.RawDiff));
        RenderComponent<CompareFilesTab>(p => p.Add(c => c.Vm, main.JsonCompare));
        RenderComponent<SourcesTab>(p => p.Add(c => c.Vm, main.Vault));
        RenderComponent<ProjectsScreen>(p => p.Add(c => c.Vm, main.Projects));
        RenderComponent<TierEditorTab>(p => p.Add(c => c.Vm, main.JsonEditor));

        // Show identical turns ~350 hidden rows back on, which is the widest the grid ever gets.
        main.Tiers!.ShowIdentical = true;
        RenderComponent<AllTiersTab>(p => p.Add(c => c.Vm, main.Tiers));

        // The editor's two modes: the pane, and the diff that replaces it.
        main.JsonEditor!.SelectedNode = main.JsonEditor.Nodes.First(n => n.Path == "PaymentSettings:Hub:Timeout");
        main.JsonEditor.EditorText = "77";
        main.JsonEditor.ShowingComparison = true;
        RenderComponent<TierEditorTab>(p => p.Add(c => c.Vm, main.JsonEditor));
    }

    /// <summary>
    /// A component handed no view model says so rather than throwing. Every tab is rendered before a
    /// project is open at least once — that is the state the app launches in.
    /// </summary>
    [Fact]
    public void Every_tab_renders_with_no_project_open()
    {
        Services.AddSingleton(new DialogService(Fixtures.NewMain()));

        RenderComponent<AllTiersTab>(p => p.Add(c => c.Vm, null));
        RenderComponent<TextDiffTab>(p => p.Add(c => c.Vm, null));
        RenderComponent<CompareFilesTab>(p => p.Add(c => c.Vm, null));
        RenderComponent<SourcesTab>(p => p.Add(c => c.Vm, null));
        RenderComponent<ProjectsScreen>(p => p.Add(c => c.Vm, null));
        RenderComponent<TierEditorTab>(p => p.Add(c => c.Vm, null));
    }

}
