using JsonInsight.Diff;
using JsonInsight.Promote;
using JsonInsight.ViewModels;

namespace JsonInsight.Tests;

/// <summary>
/// One in-memory document per tier, shared by every tab.
///
/// <para>
/// Before this, the Tier editor kept its edits in a private editor inside a view model that was
/// rebuilt on every pull and every per-source load: an edit there was invisible to the All tiers
/// grid, to the Text diff and to a push started anywhere else, and was silently discarded by events
/// that looked like navigation. These tests are about the two halves of the fix — that an edit
/// reaches the other tabs, and that it never reaches the baseline a save checks the source against.
/// </para>
/// </summary>
public sealed class LiveDocumentTests
{
    private static readonly SampleFiles Fixtures = new();

    private const string Path = "Redis:Database";

    private static MainVm Seeded()
    {
        var main = new MainVm(vaultAtStartup: false);
        main.Seed(Fixtures.Documents);
        return main;
    }

    /// <summary>Edits a scalar the way the pane does: select the node, retype it, press Update node.</summary>
    private static JsonEditorVm EditDev(MainVm main, string value)
    {
        var editor = main.JsonEditor!;
        editor.Tier = editor.Tiers.First(t => t.Id.Equals("dev", StringComparison.OrdinalIgnoreCase));
        editor.SelectedNode = editor.Nodes.First(n => n.Path == Path);
        editor.EditorText = value;
        editor.ApplyCommand.Execute(null);

        Assert.Null(editor.Error);
        return editor;
    }

    [Fact]
    public void An_edit_in_the_tier_editor_reaches_the_other_tabs()
    {
        var main = Seeded();
        EditDev(main, "42");

        Assert.True(main.PublishEdits());

        var dev = main.Documents.First(d => d.Id == "dev");
        Assert.Equal("42", dev.Flat.Find(Path)!.Value);
        Assert.True(dev.IsEdited);
    }

    /// <summary>
    /// The half that keeps a save honest. A local-file save decides whether the file moved underneath
    /// the app by comparing it against <c>Root</c>; if an in-memory edit were published into that,
    /// every save would refuse, claiming the source had changed under it.
    /// </summary>
    [Fact]
    public void An_edit_never_touches_the_baseline_a_save_checks_against()
    {
        var main = Seeded();
        var before = OrdinalJsonWriter.SerializeToText(main.Documents.First(d => d.Id == "dev").Root);

        EditDev(main, "42");
        main.PublishEdits();

        var dev = main.Documents.First(d => d.Id == "dev");

        Assert.Equal(before, OrdinalJsonWriter.SerializeToText(dev.Root));
        Assert.NotEqual(before, OrdinalJsonWriter.SerializeToText(dev.Live));
    }

    /// <summary>
    /// Publishing is deferred to the tab change, so the grid is not re-flattened per keystroke — but
    /// the edit is held, not lost, and is there the moment anything asks for it.
    /// </summary>
    [Fact]
    public void The_edit_is_held_until_it_is_published()
    {
        var main = Seeded();
        EditDev(main, "42");

        Assert.Equal("0", main.Documents.First(d => d.Id == "dev").Flat.Find(Path)!.Value);
        Assert.Contains("dev", main.Store.ModifiedTiers, StringComparer.OrdinalIgnoreCase);

        main.PublishEdits();

        Assert.Equal("42", main.Documents.First(d => d.Id == "dev").Flat.Find(Path)!.Value);
    }

    /// <summary>Nothing to publish is not an error, and does not rebuild anything.</summary>
    [Fact]
    public void Publishing_with_nothing_edited_does_nothing()
    {
        var main = Seeded();

        Assert.False(main.PublishEdits());
        Assert.False(main.Documents.First(d => d.Id == "dev").IsEdited);
    }

    /// <summary>
    /// Taking the edit back takes the tier back with it: an editor returned to the state it was
    /// opened in is not an edit, and a document still carrying one would keep saying otherwise.
    /// </summary>
    [Fact]
    public void Reverting_everything_puts_the_tier_back()
    {
        var main = Seeded();
        var editor = EditDev(main, "42");
        main.PublishEdits();

        editor.RevertAllCommand.Execute(null);
        main.PublishEdits();

        var dev = main.Documents.First(d => d.Id == "dev");

        Assert.False(dev.IsEdited);
        Assert.Equal("0", dev.Flat.Find(Path)!.Value);
        Assert.Empty(main.Store.ModifiedTiers);
    }

    /// <summary>
    /// The editor survives the Tier editor being rebuilt. That rebuild happens on every pull and
    /// every per-source load, and it used to be where in-progress edits went to die.
    /// </summary>
    [Fact]
    public void An_edit_survives_the_tier_editor_being_rebuilt()
    {
        var main = Seeded();
        EditDev(main, "42");

        // What BuildTabs does to this tab on a refresh: a brand new view model over the same store.
        main.Seed(Fixtures.Documents);

        Assert.Contains("dev", main.Store.ModifiedTiers, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("42", main.Store.Find("dev")!.TextAt(Path));
    }

    // ------------------------------------------------- the other direction

    /// <summary>
    /// A key edit made on the All tiers tab lands in the same document the Tier editor is showing,
    /// rather than in a queue of its own that only became real at push time.
    /// </summary>
    [Fact]
    public void A_batch_edit_lands_in_the_document_the_tier_editor_shows()
    {
        var main = Seeded();

        var edit = new EditVm(main, [Path]);
        var row = edit.Rows.First(r => r.TierId == "dev");
        row.Action = RowAction.Set;
        row.NewValue = "7";
        row.Kind = System.Text.Json.JsonValueKind.Number;

        edit.QueueCommand.Execute(null);

        Assert.Equal("7", main.Documents.First(d => d.Id == "dev").Flat.Find(Path)!.Value);
        Assert.Equal("7", main.Store.Find("dev")!.TextAt(Path));
    }

    /// <summary>
    /// Two edits to the same tier from the two tabs stack rather than overwrite. This is the failure
    /// the refactor was for: the two models each held an answer, and whichever was pushed first won
    /// silently.
    /// </summary>
    [Fact]
    public void A_batch_edit_stacks_onto_a_tier_editor_change()
    {
        var main = Seeded();
        EditDev(main, "42");
        main.PublishEdits();

        const string other = "Redis:ConnectionString";
        var edit = new EditVm(main, [other]);
        var row = edit.Rows.First(r => r.TierId == "dev");
        row.Action = RowAction.Set;
        row.NewValue = "redis://edited";
        row.Kind = System.Text.Json.JsonValueKind.String;

        edit.QueueCommand.Execute(null);

        var dev = main.Documents.First(d => d.Id == "dev");

        // Both, not one or the other.
        Assert.Equal("42", dev.Flat.Find(Path)!.Value);
        Assert.Equal("redis://edited", dev.Flat.Find(other)!.Value);
    }

    /// <summary>
    /// A key inside an array element, set to the same value everywhere through Apply to all, stays on
    /// the All tiers grid — with its pending mark, its number still a number.
    ///
    /// <para>
    /// The reported arrangement, exactly: a services array whose duplicate <c>code</c> forces index
    /// paths, an <c>amount:max</c> edited through the dialog. Three things used to go wrong at once.
    /// The bulk type box defaulted to string, so the number was retyped as <c>"90000000"</c> in every
    /// tier; the change tracker compared the array as one blob, so no per-path consumer saw the edit;
    /// and the grid, having made the row identical across tiers, hid it — the edit's only visible
    /// result was the row that showed it disappearing.
    /// </para>
    /// </summary>
    [Fact]
    public void An_edit_inside_an_array_element_stays_on_the_grid_and_keeps_its_type()
    {
        static string Doc(long max) => $$"""
            {
              "services": [
                { "code": "bill", "inputDigits": { "max": 13, "min": 6 } },
                { "amount": { "max": {{max}}, "min": 10000 }, "code": "charity", "status": "active" },
                { "code": "charity", "status": "inactive" }
              ]
            }
            """;

        var main = new MainVm(vaultAtStartup: false);
        main.Seed([
            Fixtures.AsTier("dev", 1, Doc(50000000)),
            Fixtures.AsTier("stage", 2, Doc(70000000)),
        ]);
        var tiers = main.Tiers!;

        // The filter that hides identical rows is on, because it is what used to remove the row.
        tiers.OnlyChanges = true;

        var maxRow = tiers.Diff.Rows.Single(r => r.Path.EndsWith("amount:max", StringComparison.Ordinal));
        var path = maxRow.Path;
        Assert.Equal("services[1]:amount:max", path);
        Assert.Contains(tiers.Rows, r => r.Path == path);

        // The tiers disagree on a business value, so its cells carry the drift colouring.
        Assert.All(maxRow.Cells, c => Assert.Equal(CellVariance.Drift, c.Variance));

        var edit = new EditVm(main, [path]) { BulkValue = "90000000" };

        // The bulk type follows the keys being edited: these are numbers, so it starts on number.
        Assert.Equal(System.Text.Json.JsonValueKind.Number, edit.BulkKind);

        edit.ApplyToAllCommand.Execute(null);
        edit.QueueCommand.Execute(null);

        // The change tracker names the element precisely, in the grid's own spelling.
        Assert.Contains(path, main.Store.ChangedPaths());

        var row = tiers.Rows.SingleOrDefault(r => r.Path == path);
        Assert.NotNull(row);
        Assert.True(row.HasPendingEdit);

        var leaf = main.Documents.First(d => d.Id == "dev").Flat.Find(path)!;
        Assert.Equal("90000000", leaf.Value);
        Assert.Equal(System.Text.Json.JsonValueKind.Number, leaf.Kind);
    }

    /// <summary>An edit that sets a key to what it already holds is not a change.</summary>
    [Fact]
    public void A_batch_edit_that_changes_nothing_leaves_the_tier_unmodified()
    {
        var main = Seeded();

        var edit = new EditVm(main, [Path]);
        var row = edit.Rows.First(r => r.TierId == "dev");
        row.Action = RowAction.Set;
        row.NewValue = row.Current!.ComparableValue;
        row.Kind = System.Text.Json.JsonValueKind.Number;

        edit.QueueCommand.Execute(null);

        Assert.Empty(main.Store.ModifiedTiers);
        Assert.False(main.Documents.First(d => d.Id == "dev").IsEdited);
    }

    /// <summary>
    /// The review screen lists what is held in memory rather than a queue, and taking a tier back
    /// removes it from the list.
    /// </summary>
    [Fact]
    public void The_review_screen_lists_what_is_held_in_memory()
    {
        var main = Seeded();
        EditDev(main, "42");

        var changes = new ChangesVm(main);

        Assert.Equal("dev", changes.Tier!.Id);
        Assert.Contains(changes.Changes, c => c.Path == Path);
        Assert.True(changes.CanPush);

        changes.DiscardTierCommand.Execute(null);

        Assert.Empty(main.Store.ModifiedTiers);
        Assert.Null(changes.Tier);
        Assert.False(main.HasUnsavedChanges);
    }

    /// <summary>The top bar's push button is off until something is actually unsaved.</summary>
    [Fact]
    public void The_push_button_turns_on_only_once_something_is_unsaved()
    {
        var main = Seeded();
        Assert.False(main.HasUnsavedChanges);

        EditDev(main, "42");
        Assert.True(main.HasUnsavedChanges);
    }

    /// <summary>
    /// The Tier editor is kept rather than rebuilt when edits are published, so it has to be told
    /// when a change lands in the document it is showing — otherwise it goes on rendering the tree
    /// it built before that happened, which is the same staleness the refactor exists to remove.
    /// </summary>
    [Fact]
    public void The_tier_editor_picks_up_a_change_made_on_the_all_tiers_tab()
    {
        var main = Seeded();

        // Open dev in the editor and stand on a node, as somebody switching tabs would leave it.
        var editor = main.JsonEditor!;
        editor.Tier = editor.Tiers.First(t => t.Id.Equals("dev", StringComparison.OrdinalIgnoreCase));
        editor.SelectedNode = editor.Nodes.First(n => n.Path == Path);

        var edit = new EditVm(main, [Path]);
        var row = edit.Rows.First(r => r.TierId == "dev");
        row.Action = RowAction.Set;
        row.NewValue = "7";
        row.Kind = System.Text.Json.JsonValueKind.Number;
        edit.QueueCommand.Execute(null);

        // The tree, and the pane, both show it — and the place in the tree is kept.
        Assert.Equal("7", editor.Editor!.TextAt(Path));
        Assert.Equal(Path, editor.SelectedNode?.Path);
        Assert.Equal("7", editor.EditorText);
    }
}
