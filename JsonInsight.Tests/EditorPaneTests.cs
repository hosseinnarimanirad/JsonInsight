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

    /// <summary>
    /// Half-typed text is not an error, it is a value on its way in. It commits nothing, raises no
    /// banner, and says why under the pane rather than through a button.
    /// </summary>
    [Fact]
    public void Text_that_does_not_parse_yet_commits_nothing_and_raises_nothing()
    {
        var vm = Open();
        vm.SelectedNode = vm.Nodes.First(n => n.Path == "Redis:ConnectionString");

        var was = vm.Editor!.TextAt("Redis:ConnectionString");
        vm.EditorText = "\"unterminated";

        Assert.Equal(was, vm.Editor.TextAt("Redis:ConnectionString"));
        Assert.Null(vm.Error);

        Assert.True(vm.HasEditorProblem);
        Assert.Contains("Not valid JSON", vm.EditorProblem, StringComparison.Ordinal);
    }

    /// <summary>
    /// Update node is off while the pane does not parse, and comes back the moment it does.
    ///
    /// <para>
    /// It used to stay lit, so that pressing it produced the real parse error. That made the button
    /// the only way to find out and made an unpressable state look pressable — a button offering to
    /// replace a node with something that cannot be read. The reason is on screen now, and the
    /// button is offered only when it would work.
    /// </para>
    /// </summary>
    [Fact]
    public void Update_node_is_off_while_the_pane_does_not_parse()
    {
        var vm = Open();
        vm.SelectedNode = vm.Nodes.First(n => n.Path == "Redis");

        // A section: this is the pane that commits by button rather than as you type.
        Assert.False(vm.SelectedIsScalar);

        vm.EditorText = "{ \"Database\": 9 ";
        Assert.True(vm.HasEditorProblem);
        Assert.False(vm.CanApply);

        vm.EditorText = "{ \"Database\": 9 }";
        Assert.False(vm.HasEditorProblem);
        Assert.Empty(vm.EditorProblem);
        Assert.True(vm.CanApply);
    }

    /// <summary>
    /// And the problem goes with the selection. Landing on a node whose text is fine must not carry
    /// the last node's parse error — nor keep Update off for a pane that is perfectly readable.
    /// </summary>
    [Fact]
    public void Moving_the_selection_clears_the_previous_panes_problem()
    {
        var vm = Open();
        vm.SelectedNode = vm.Nodes.First(n => n.Path == "Redis");
        vm.EditorText = "{ nonsense";

        Assert.True(vm.HasEditorProblem);

        vm.SelectedNode = vm.Nodes.First(n => n.Path == "Redis:Database");

        Assert.False(vm.HasEditorProblem);
        Assert.Empty(vm.EditorProblem);
    }

    // ------------------------------------------------------------ find/replace

    /// <summary>
    /// Stepping walks the matches instead of searching from the caret.
    ///
    /// <para>
    /// Searching from the caret was the defect: after a step the caret sits <em>at</em> the current
    /// match, and a forward search from there finds the same one again. An index into the match list
    /// cannot land on the entry it is already on.
    /// </para>
    /// </summary>
    [Fact]
    public void Stepping_advances_through_the_matches_rather_than_re_finding_one()
    {
        var vm = Open();
        vm.SelectedNode = vm.Nodes.First(n => n.Path == "Redis");
        vm.EditorText = """{ "a": 1, "b": 1, "c": 1 }""";

        vm.FindOpen = true;
        vm.FindText = "1";

        Assert.Equal(3, vm.Matches.Count);
        Assert.Equal("3 found", vm.FindStatus);

        var first = vm.StepMatch(forward: true);
        Assert.Equal(vm.Matches[0], first);
        Assert.Equal("1 of 3", vm.FindStatus);

        Assert.Equal(vm.Matches[1], vm.StepMatch(forward: true));
        Assert.Equal(vm.Matches[2], vm.StepMatch(forward: true));

        // Wraps, in both directions.
        Assert.Equal(vm.Matches[0], vm.StepMatch(forward: true));
        Assert.Equal(vm.Matches[2], vm.StepMatch(forward: false));
        Assert.Equal("3 of 3", vm.FindStatus);
    }

    /// <summary>
    /// Pressing Replace repeatedly walks the document forwards and replaces every match, rather than
    /// doubling back or stopping on one.
    ///
    /// <para>
    /// Nothing pinned this while each host owned its own copy, and the two had drifted into behaving
    /// differently: the Blazor pane wrote the text straight into the view model, while the WPF pane
    /// assigned the text box, whose binding is delayed — so WPF consulted a stale match list and the
    /// two walked in opposite directions. Both go through <c>ReplaceCurrent</c> now, and this is the
    /// test that says which direction is the right one.
    /// </para>
    /// </summary>
    [Fact]
    public void Replacing_repeatedly_walks_forwards_through_every_match()
    {
        var vm = Open();
        vm.SelectedNode = vm.Nodes.First(n => n.Path == "Redis");
        vm.EditorText = """{ "a": 1, "b": 1, "c": 1 }""";

        vm.FindOpen = true;
        vm.FindText = "1";
        vm.ReplaceText = "9";

        Assert.Equal(3, vm.Matches.Count);

        // Nothing has been stepped to, so the first press takes the first match rather than nothing.
        Assert.True(vm.ReplaceCurrent() > 0);
        Assert.Equal("""{ "a": 9, "b": 1, "c": 1 }""", vm.EditorText);

        Assert.True(vm.ReplaceCurrent() > 0);
        Assert.Equal("""{ "a": 9, "b": 9, "c": 1 }""", vm.EditorText);

        Assert.True(vm.ReplaceCurrent() > 0);
        Assert.Equal("""{ "a": 9, "b": 9, "c": 9 }""", vm.EditorText);

        // Everything is replaced, so there is nothing left to find and nothing left to do.
        Assert.Empty(vm.Matches);
        Assert.False(vm.CanReplace);
        Assert.Equal(-1, vm.ReplaceCurrent());
    }

    /// <summary>
    /// Replace all reports the count and never "not found": it is offered only when there are matches,
    /// and it replaces them in the same text they were found in.
    /// </summary>
    [Fact]
    public void Replacing_all_reports_how_many_went()
    {
        var vm = Open();
        vm.SelectedNode = vm.Nodes.First(n => n.Path == "Redis");
        vm.EditorText = """{ "a": 1, "b": 1, "c": 1 }""";

        vm.FindOpen = true;
        vm.FindText = "1";
        vm.ReplaceText = "9";

        vm.ReplaceAllInPane();

        Assert.Equal("""{ "a": 9, "b": 9, "c": 9 }""", vm.EditorText);
        Assert.Equal("3 replaced", vm.FindStatus);
    }

    /// <summary>
    /// Ctrl+Z takes back a run of typing as one step, not one keystroke at a time.
    ///
    /// <para>
    /// Coalescing is the whole point: an undo that gives back one character per press is not an undo
    /// anyone uses. A run is consecutive single-character edits in the same direction, so typing
    /// "1234" is one step even though it arrived as four.
    /// </para>
    /// </summary>
    [Fact]
    public void Undoing_the_pane_takes_back_a_run_of_typing_in_one_step()
    {
        var vm = Open();
        vm.SelectedNode = vm.Nodes.First(n => n.Path == "Redis:Database");

        var opened = vm.EditorText;
        Assert.False(vm.CanUndoText);

        // Appended one character at a time, the way it arrives from a text box.
        vm.EditorText = opened + "1";
        vm.EditorText = opened + "12";
        vm.EditorText = opened + "123";

        Assert.True(vm.CanUndoText);
        Assert.True(vm.UndoText());
        Assert.Equal(opened, vm.EditorText);

        // And nothing beyond the beginning: the pane as it was opened is the floor.
        Assert.False(vm.CanUndoText);
        Assert.False(vm.UndoText());
    }

    /// <summary>
    /// Typing over a selection is two steps, not one: replacing the whole value is one action and the
    /// characters typed afterwards are another, so the first Ctrl+Z gives back the typing and the
    /// second gives back the value that was overwritten.
    ///
    /// <para>
    /// Worth pinning because it follows from how a run is detected — a same-length or wholesale change
    /// cannot be a continuation of a typing run, so it always opens a step of its own.
    /// </para>
    /// </summary>
    [Fact]
    public void Replacing_the_value_and_then_typing_are_separate_undo_steps()
    {
        var vm = Open();
        vm.SelectedNode = vm.Nodes.First(n => n.Path == "Redis:Database");

        var opened = vm.EditorText;

        // Select-all and type: the first keystroke replaces everything, the rest extend it.
        vm.EditorText = "1";
        vm.EditorText = "12";
        vm.EditorText = "123";

        Assert.True(vm.UndoText());
        Assert.Equal("1", vm.EditorText);

        Assert.True(vm.UndoText());
        Assert.Equal(opened, vm.EditorText);

        Assert.False(vm.CanUndoText);
    }

    /// <summary>
    /// A paste or a replace is its own step, even mid-run. It is one action, and the run it interrupts
    /// is a different one — merging them would make one Ctrl+Z undo both.
    /// </summary>
    [Fact]
    public void A_wholesale_change_is_its_own_undo_step()
    {
        var vm = Open();
        vm.SelectedNode = vm.Nodes.First(n => n.Path == "Redis:Database");

        vm.EditorText = "1";
        vm.EditorText = "12";

        // Not a single character: a paste over the top.
        vm.EditorText = "98765";

        Assert.True(vm.UndoText());
        Assert.Equal("12", vm.EditorText);

        Assert.True(vm.UndoText());
        Assert.Equal(vm.Editor!.TextAt("Redis:Database"), vm.EditorText);
    }

    /// <summary>
    /// Selecting another node drops the trail. The steps describe the previous node's text, and
    /// replaying one into this pane would paste one node's JSON over another's.
    /// </summary>
    [Fact]
    public void Changing_node_forgets_the_pane_undo_trail()
    {
        var vm = Open();

        vm.SelectedNode = vm.Nodes.First(n => n.Path == "Redis:Database");
        vm.EditorText = "4242";
        Assert.True(vm.CanUndoText);

        vm.SelectedNode = vm.Nodes.First(n => n.Path == "Redis:Enabled");

        Assert.False(vm.CanUndoText);
        Assert.False(vm.UndoText());
    }

    /// <summary>Back from a standing start opens on the last match rather than on nothing.</summary>
    [Fact]
    public void Stepping_backwards_first_opens_on_the_last_match()
    {
        var vm = Open();
        vm.SelectedNode = vm.Nodes.First(n => n.Path == "Redis");
        vm.EditorText = """{ "a": 1, "b": 1 }""";

        vm.FindOpen = true;
        vm.FindText = "1";

        Assert.Equal(vm.Matches[^1], vm.StepMatch(forward: false));
        Assert.Equal("2 of 2", vm.FindStatus);
    }

    /// <summary>
    /// A term with nothing behind it, and the closed bar, both mean nothing to highlight — the pane
    /// renders no layer at all rather than an empty one over every character.
    /// </summary>
    [Fact]
    public void Nothing_is_highlighted_without_a_term_or_an_open_bar()
    {
        var vm = Open();
        vm.SelectedNode = vm.Nodes.First(n => n.Path == "Redis");

        vm.FindOpen = true;
        vm.FindText = "Database";
        Assert.True(vm.HasMatches);

        vm.FindText = string.Empty;
        Assert.False(vm.HasMatches);
        Assert.Empty(vm.FindStatus);

        vm.FindText = "Database";
        vm.FindOpen = false;
        Assert.False(vm.HasMatches);
    }

    /// <summary>
    /// Replace is offered only when it would do something. Both bars ask this rather than each
    /// assembling it, so neither can end up with a lit button that does nothing when pressed.
    /// </summary>
    [Fact]
    public void Replace_is_offered_only_with_something_to_replace()
    {
        var vm = Open();
        vm.SelectedNode = vm.Nodes.First(n => n.Path == "Redis");

        vm.FindOpen = true;
        Assert.False(vm.CanReplace);

        vm.FindText = "Database";
        Assert.True(vm.CanReplace);

        // Nothing found is as much a reason to be off as a pane that cannot be written to.
        vm.FindText = "nothing here matches this";
        Assert.False(vm.CanReplace);
    }

    /// <summary>Case is a choice, and turning it on re-finds rather than filtering what was found.</summary>
    [Fact]
    public void Match_case_re_finds_immediately()
    {
        var vm = Open();
        vm.SelectedNode = vm.Nodes.First(n => n.Path == "Redis");
        vm.EditorText = """{ "Url": 1, "url": 2, "URL": 3 }""";

        vm.FindOpen = true;
        vm.FindText = "url";

        Assert.Equal(3, vm.Matches.Count);

        vm.MatchCase = true;
        Assert.Single(vm.Matches);
        Assert.Equal("1 found", vm.FindStatus);
    }

    /// <summary>
    /// Editing the pane re-finds. The offsets describe a body of text, and a replace can change its
    /// length — a list quietly one character out would highlight the wrong runs.
    /// </summary>
    [Fact]
    public void Editing_the_pane_re_finds_the_matches()
    {
        var vm = Open();
        vm.SelectedNode = vm.Nodes.First(n => n.Path == "Redis");
        vm.EditorText = """{ "a": 1 }""";

        vm.FindOpen = true;
        vm.FindText = "1";
        Assert.Single(vm.Matches);

        vm.EditorText = """{ "a": 1, "b": 1 }""";
        Assert.Equal(2, vm.Matches.Count);

        vm.EditorText = """{ "a": 0 }""";
        Assert.Empty(vm.Matches);
        Assert.Equal("not found", vm.FindStatus);
    }

    /// <summary>
    /// Moving the selection drops the current match: the offsets pointed into the previous node's
    /// text, and carrying them across would highlight whatever happened to be at those positions.
    /// </summary>
    [Fact]
    public void Changing_node_drops_the_previous_panes_matches()
    {
        var vm = Open();
        vm.SelectedNode = vm.Nodes.First(n => n.Path == "Redis");
        vm.FindOpen = true;
        vm.FindText = "Database";

        vm.StepMatch(forward: true);
        Assert.Equal(0, vm.MatchIndex);

        vm.SelectedNode = vm.Nodes.First(n => n.Path == "Redis:ConnectionString");

        Assert.Equal(-1, vm.MatchIndex);
    }

    /// <summary>A click in the pane moves where the next Enter continues from.</summary>
    [Fact]
    public void Syncing_to_the_caret_moves_the_current_match()
    {
        var vm = Open();
        vm.SelectedNode = vm.Nodes.First(n => n.Path == "Redis");
        vm.EditorText = """{ "a": 1, "b": 1, "c": 1 }""";

        vm.FindOpen = true;
        vm.FindText = "1";

        vm.SyncMatchToCaret(vm.Matches[1]);
        Assert.Equal(1, vm.MatchIndex);

        // The next one forward from there is the third, not the second again.
        Assert.Equal(vm.Matches[2], vm.StepMatch(forward: true));
    }

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

    /// <summary>
    /// The tree's own copy, as opposed to the pane's: the row together with its key, so what lands
    /// on the clipboard pastes straight into another object as a member.
    /// </summary>
    [Fact]
    public void Copying_a_row_with_its_key_yields_a_pasteable_member()
    {
        var clipboard = new CapturingClipboard();
        JsonInsight.Platform.Platform.Clipboard = clipboard;

        try
        {
            var vm = Open();
            var row = vm.Nodes.First(n => n.Path == "Redis");

            vm.CopyNodeWithKeyCommand.Execute(row);

            Assert.Null(vm.Error);
            Assert.StartsWith("\"Redis\": {", clipboard.Text, StringComparison.Ordinal);
            Assert.Contains("Copied Redis with its key", vm.Message!, StringComparison.Ordinal);

            // The whole point: wrapped in braces it is a valid object holding exactly this member.
            var pasted = JsonNode.Parse("{" + clipboard.Text + "}");
            Assert.NotNull(pasted!["Redis"]);
        }
        finally
        {
            JsonInsight.Platform.Platform.Reset();
        }
    }

    /// <summary>
    /// The row does not have to be selected — the context menu and the row button hand over whatever
    /// row was clicked, and the value comes from the document rather than from a pane it is not in.
    /// </summary>
    [Fact]
    public void Copying_an_unselected_row_reads_the_document_not_the_pane()
    {
        var clipboard = new CapturingClipboard();
        JsonInsight.Platform.Platform.Clipboard = clipboard;

        try
        {
            var vm = Open();
            vm.SelectedNode = vm.Nodes.First(n => n.Path == "Redis");
            vm.EditorText = """{ "typed": "not applied" }""";

            var other = vm.Nodes.First(n => n.Path == "Redis:ConnectionString");
            vm.CopyNodeValueCommand.Execute(other);

            Assert.Null(vm.Error);
            Assert.DoesNotContain("typed", clipboard.Text, StringComparison.Ordinal);
            Assert.StartsWith("\"", clipboard.Text, StringComparison.Ordinal);
        }
        finally
        {
            JsonInsight.Platform.Platform.Reset();
        }
    }

    /// <summary>
    /// For the selected row the pane is what is on screen, so — like the pane's own Copy — its text,
    /// edits included, is what gets copied, behind the key.
    /// </summary>
    [Fact]
    public void Copying_the_selected_row_takes_the_pane_text_behind_the_key()
    {
        var clipboard = new CapturingClipboard();
        JsonInsight.Platform.Platform.Clipboard = clipboard;

        try
        {
            var vm = Open();
            var row = vm.Nodes.First(n => n.Path == "Redis");
            vm.SelectedNode = row;
            vm.EditorText = """{ "typed": true }""";

            vm.CopyNodeWithKeyCommand.Execute(row);

            Assert.Equal("\"Redis\": " + vm.EditorText, clipboard.Text);
        }
        finally
        {
            JsonInsight.Platform.Platform.Reset();
        }
    }

    private sealed class CapturingClipboard : JsonInsight.Platform.IClipboard
    {
        public string Text { get; private set; } = string.Empty;

        public void SetText(string text) => Text = text;
    }
}
