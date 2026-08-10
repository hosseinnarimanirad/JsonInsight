using System.Text;
using JsonInsight.Editing;
using JsonInsight.Promote;
using JsonInsight.Vault;

namespace JsonInsight.Tests;

[Collection("sample-files")]
public sealed class DocumentEditorTests(SampleFiles files)
{
    private DocumentEditor Editor(string tierId = "beta") => new(files[tierId]);

    [Fact]
    public void A_node_reads_back_as_the_canonical_text_of_its_subtree_and_nothing_more()
    {
        var editor = Editor();

        var text = editor.TextAt("ConnectionStrings:Couchbase:Modules:Auth");

        Assert.StartsWith("{", text, StringComparison.Ordinal);
        Assert.Contains("\"Url\":", text, StringComparison.Ordinal);

        // Only that subtree: not its siblings, not its ancestors, not the rest of the document.
        Assert.DoesNotContain("\"Account\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AccountSettings", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Modules\"", text, StringComparison.Ordinal);
        Assert.True(text.Length < editor.WorkingText.Length / 4);

        // What the pane shows is exactly what the writer would emit for that subtree, so pasting it
        // straight back is a no-op rather than a reformat.
        Assert.Equal(text, OrdinalJsonWriter.SerializeToText(editor.Find("ConnectionStrings:Couchbase:Modules:Auth")!));
        Assert.False(editor.IsModified);
    }

    /// <summary>A scalar node reads back as its bare value, so a single key can be retyped on its own.</summary>
    [Fact]
    public void A_leaf_node_reads_back_as_just_its_value()
    {
        var editor = Editor();
        const string path = "ConnectionStrings:Couchbase:KvTimeoutSeconds";

        var text = editor.TextAt(path);

        // A bare JSON scalar: no braces, no key, no surrounding object.
        Assert.Equal(files.Beta.Flat.Find(path)!.Value, text);
        Assert.DoesNotContain("{", text, StringComparison.Ordinal);
        Assert.DoesNotContain(":", text, StringComparison.Ordinal);

        editor.Replace(path, "42");
        Assert.Equal("42", files.Flattener.Flatten("beta", editor.Working).Find(path)!.Value);

        // A string leaf comes back quoted, which is what makes it valid JSON to paste over.
        Assert.StartsWith("\"", editor.TextAt("ConnectionStrings:Couchbase:Modules:Auth:Bucket"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Replacing_a_node_swaps_the_whole_subtree()
    {
        var editor = Editor();
        const string path = "ConnectionStrings:Couchbase:Modules:Auth";

        editor.Replace(path, """{ "Bucket": "replaced", "Url": "couchbase://10.0.0.1" }""");

        Assert.True(editor.IsModified);

        var after = files.Flattener.Flatten("beta", editor.Working);
        Assert.Equal("replaced", after.Find($"{path}:Bucket")!.Value);
        Assert.Equal("couchbase://10.0.0.1", after.Find($"{path}:Url")!.Value);

        // Wholesale means wholesale: the keys the replacement does not mention are gone.
        Assert.False(after.Contains($"{path}:Username"));
        Assert.False(after.Contains($"{path}:Password"));

        // And nothing outside the subtree moved.
        Assert.True(after.Contains("ConnectionStrings:Couchbase:Modules:Account:Url"));
    }

    /// <summary>
    /// A replacement may change shape. "Totally replace this node" is the operation, and refusing an
    /// object-to-array change would narrow it without making it safer - the round-trip guard and the
    /// key-set check still stand between this and a file.
    /// </summary>
    [Fact]
    public void A_replacement_may_change_the_shape_of_a_node()
    {
        var editor = Editor();

        editor.Replace("AccountSettings:Transfer", """[1, 2, 3]""");
        Assert.Contains("\"Transfer\": [", editor.WorkingText, StringComparison.Ordinal);

        editor.Replace("AccountSettings:Transfer", "\"now a string\"");
        Assert.Contains("\"Transfer\": \"now a string\"", editor.WorkingText, StringComparison.Ordinal);
    }

    [Fact]
    public void The_root_can_be_replaced_but_must_stay_an_object()
    {
        var editor = Editor();

        editor.Replace(string.Empty, """{ "OnlyThing": 1 }""");
        Assert.Equal(1, files.Flattener.Flatten("beta", editor.Working).Count);

        var ex = Assert.Throws<InvalidOperationException>(() => editor.Replace(string.Empty, "[]"));
        Assert.Contains("root must stay a JSON object", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_json_is_refused_and_changes_nothing()
    {
        var editor = Editor();
        var before = editor.WorkingText;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            editor.Replace("AccountSettings:Transfer", "{ not json"));

        Assert.Contains("not valid JSON", ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, editor.WorkingText);
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public void Undo_and_redo_walk_the_history_exactly()
    {
        var editor = Editor();
        var original = editor.WorkingText;

        editor.Replace("AccountSettings:Transfer", """{ "MinAmount": 1 }""");
        var afterFirst = editor.WorkingText;

        editor.Replace("AccountSettings:Transfer", """{ "MinAmount": 2 }""");
        var afterSecond = editor.WorkingText;

        Assert.Equal(2, editor.History.Count);

        editor.Undo();
        Assert.Equal(afterFirst, editor.WorkingText);

        editor.Undo();
        Assert.Equal(original, editor.WorkingText);
        Assert.False(editor.CanUndo);
        Assert.False(editor.IsModified);

        editor.Redo();
        Assert.Equal(afterFirst, editor.WorkingText);

        editor.Redo();
        Assert.Equal(afterSecond, editor.WorkingText);
        Assert.False(editor.CanRedo);
    }

    [Fact]
    public void A_new_change_after_an_undo_drops_the_redo_branch()
    {
        var editor = Editor();

        editor.Replace("AccountSettings:Transfer", """{ "MinAmount": 1 }""");
        editor.Undo();
        Assert.True(editor.CanRedo);

        editor.Replace("AccountSettings:Transfer", """{ "MinAmount": 9 }""");
        Assert.False(editor.CanRedo);
    }

    [Fact]
    public void Revert_all_returns_to_the_opened_state_and_is_itself_undoable()
    {
        var editor = Editor();
        var original = editor.WorkingText;

        editor.Replace("AccountSettings:Transfer", """{ "MinAmount": 1 }""");
        editor.Remove("Elasticsearch");
        Assert.True(editor.IsModified);

        editor.RevertAll();
        Assert.Equal(original, editor.WorkingText);
        Assert.False(editor.IsModified);

        // Undoing a revert has to bring the work back, or "revert" is a destructive button with an
        // undo arrow next to it that does nothing.
        editor.Undo();
        Assert.True(editor.IsModified);
        Assert.DoesNotContain(files.Flattener.Flatten("beta", editor.Working).Paths,
            p => p.StartsWith("Elasticsearch", StringComparison.Ordinal));
    }

    /// <summary>The original is what "compare with the original" compares against, so editing must not touch it.</summary>
    [Fact]
    public void The_original_tree_is_never_mutated()
    {
        var editor = Editor();
        var original = editor.OriginalText;

        editor.Replace(string.Empty, """{ "OnlyThing": 1 }""");

        Assert.Equal(original, editor.OriginalText);
        Assert.NotEqual(original, editor.WorkingText);
    }

    [Fact]
    public void Removing_a_node_takes_its_whole_subtree()
    {
        var editor = Editor();

        editor.Remove("ConnectionStrings:Couchbase:Modules:Auth");

        var after = files.Flattener.Flatten("beta", editor.Working);
        Assert.DoesNotContain(after.Paths, p =>
            p.StartsWith("ConnectionStrings:Couchbase:Modules:Auth", StringComparison.Ordinal));

        // Its siblings are untouched - this is a removal, not a restructure.
        Assert.True(after.Contains("ConnectionStrings:Couchbase:Modules:Account:Url"));
    }

    // ------------------------------------------------------------ node revert

    /// <summary>
    /// The difference from Undo, which is the whole reason this exists: undo walks the history
    /// backwards and would take unrelated later edits with it. This is aimed at one node.
    /// </summary>
    [Fact]
    public void Reverting_a_node_leaves_every_other_edit_alone()
    {
        var editor = Editor();
        const string transfer = "AccountSettings:Transfer";
        const string auth = "ConnectionStrings:Couchbase:Modules:Auth:Url";

        var originalTransfer = editor.TextAt(transfer);

        editor.Replace(transfer, """{ "MinAmount": 999 }""");
        editor.Replace(auth, "\"couchbase://10.0.0.1\"");

        editor.RevertNode(transfer);

        // The reverted node is back...
        Assert.Equal(originalTransfer, editor.TextAt(transfer));
        Assert.DoesNotContain(transfer, editor.ChangedPaths());

        // ...and the later, unrelated edit survived, which an Undo would have taken.
        Assert.Equal("couchbase://10.0.0.1",
            files.Flattener.Flatten("beta", editor.Working).Find(auth)!.Value);
        Assert.Contains(auth, editor.ChangedPaths());
        Assert.True(editor.IsModified);
    }

    [Fact]
    public void Reverting_a_node_added_since_opening_removes_it()
    {
        var editor = Editor();

        editor.Replace("AccountSettings:Transfer", """{ "MinAmount": 1, "Invented": true }""");
        Assert.Contains("AccountSettings:Transfer:Invented", editor.ChangedPaths());

        editor.RevertNode("AccountSettings:Transfer:Invented");

        Assert.DoesNotContain("AccountSettings:Transfer:Invented",
            files.Flattener.Flatten("beta", editor.Working).Paths);
    }

    [Fact]
    public void Reverting_a_removed_node_brings_it_and_its_subtree_back()
    {
        var editor = Editor();
        var before = editor.TextAt("ConnectionStrings:Couchbase:Modules:Auth");

        editor.Remove("ConnectionStrings:Couchbase:Modules:Auth");
        editor.RevertNode("ConnectionStrings:Couchbase:Modules:Auth");

        Assert.Equal(before, editor.TextAt("ConnectionStrings:Couchbase:Modules:Auth"));
        Assert.Empty(editor.ChangedPaths());
    }

    /// <summary>
    /// A removal is pending, not final, so the document has to be able to describe it: the key was
    /// there when this was opened and is not there now. That is what the tree renders as a tombstone.
    /// </summary>
    [Fact]
    public void A_removed_node_is_reported_as_removed_rather_than_simply_absent()
    {
        var editor = Editor();
        const string path = "ConnectionStrings:Couchbase:Modules:Auth";

        Assert.False(editor.IsRemovedSinceOpened(path));

        editor.Remove(path);

        Assert.True(editor.IsRemovedSinceOpened(path));
        Assert.Null(editor.Find(path));
        Assert.NotNull(editor.FindOriginal(path));

        // Its descendants are removed too, and the marker set covers all of them so the
        // "changed only" filter shows the whole tombstone rather than just its top.
        Assert.True(editor.IsRemovedSinceOpened($"{path}:Url"));
        Assert.Contains($"{path}:Url", editor.ChangedPaths());

        // A key that never existed is absent, not removed - the distinction the tree renders.
        Assert.False(editor.IsRemovedSinceOpened("ConnectionStrings:Couchbase:Modules:NeverExisted"));
    }

    /// <summary>
    /// A JSON null is a present key holding null, not a missing one. Reading it as removed would put
    /// a tombstone over a live setting.
    /// </summary>
    [Fact]
    public void A_key_holding_json_null_is_present_not_removed()
    {
        var editor = Editor();
        editor.Replace("AccountSettings:Transfer", """{ "MinAmount": null }""");

        Assert.False(editor.IsRemovedSinceOpened("AccountSettings:Transfer:MinAmount"));
    }

    [Fact]
    public void A_node_revert_is_itself_undoable()
    {
        var editor = Editor();
        editor.Replace("AccountSettings:Transfer", """{ "MinAmount": 999 }""");

        editor.RevertNode("AccountSettings:Transfer");
        Assert.Empty(editor.ChangedPaths());

        editor.Undo();
        Assert.Contains("AccountSettings:Transfer", editor.ChangedPaths());
        Assert.Equal("999", files.Flattener.Flatten("beta", editor.Working)
            .Find("AccountSettings:Transfer:MinAmount")!.Value);
    }

    [Fact]
    public void Reverting_the_root_undoes_everything()
    {
        var editor = Editor();
        editor.Replace("AccountSettings:Transfer", """{ "MinAmount": 999 }""");
        editor.Remove("Elasticsearch");

        editor.RevertNode(string.Empty);

        Assert.False(editor.IsModified);
        Assert.Empty(editor.ChangedPaths());
    }

    // ------------------------------------------------------------ compact form

    /// <summary>
    /// Compact is a display format. It must carry exactly the same content - the pane switches to it
    /// and back while an edit is in progress, and a round trip that lost or escaped anything would
    /// corrupt whatever was on screen.
    /// </summary>
    [Fact]
    public void Compact_text_is_the_same_document_on_one_line()
    {
        var pretty = OrdinalJsonWriter.SerializeToText(files.Beta.Root);
        var compact = OrdinalJsonWriter.SerializeCompactToText(files.Beta.Root);

        Assert.DoesNotContain("\r\n", compact, StringComparison.Ordinal);
        Assert.True(compact.Length < pretty.Length);

        // Persian text and '&' stay unescaped in both, same as the byte-exact writer.
        Assert.DoesNotContain("\\u", compact, StringComparison.Ordinal);

        // And it parses back to precisely the document it came from.
        Assert.Equal(pretty, OrdinalJsonWriter.SerializeToText(OrdinalJsonWriter.Parse(compact)));
    }

    // -------------------------------------------------------- change markers

    [Fact]
    public void Nothing_is_marked_changed_until_something_changes()
    {
        Assert.Empty(Editor().ChangedPaths());
    }

    /// <summary>
    /// A change marks itself and every section above it, so an edit buried in a collapsed subtree
    /// can be found by following the marks down rather than by remembering where it was made.
    /// </summary>
    [Fact]
    public void A_change_marks_itself_and_all_its_ancestors_and_nothing_else()
    {
        var editor = Editor();
        editor.Replace("ConnectionStrings:Couchbase:Modules:Auth:Url", "\"couchbase://10.0.0.1\"");

        var changed = editor.ChangedPaths();

        Assert.Contains("ConnectionStrings:Couchbase:Modules:Auth:Url", changed);
        Assert.Contains("ConnectionStrings:Couchbase:Modules:Auth", changed);
        Assert.Contains("ConnectionStrings:Couchbase:Modules", changed);
        Assert.Contains("ConnectionStrings:Couchbase", changed);
        Assert.Contains("ConnectionStrings", changed);

        // The empty path is the root row, marked whenever anything at all differs.
        Assert.Contains(string.Empty, changed);

        // Siblings and unrelated sections stay unmarked, or the marker means nothing.
        Assert.DoesNotContain("ConnectionStrings:Couchbase:Modules:Auth:Bucket", changed);
        Assert.DoesNotContain("ConnectionStrings:Couchbase:Modules:Account", changed);
        Assert.DoesNotContain("AccountSettings", changed);
    }

    [Fact]
    public void An_added_subtree_is_marked_all_the_way_down()
    {
        var editor = Editor();
        editor.Replace("AccountSettings:Transfer", """{ "MinAmount": 1, "Nested": { "Deep": true } }""");

        var changed = editor.ChangedPaths();

        Assert.Contains("AccountSettings:Transfer", changed);
        Assert.Contains("AccountSettings:Transfer:Nested", changed);
        Assert.Contains("AccountSettings:Transfer:Nested:Deep", changed);

        // DailyLimit and MaxAmount were dropped by the replacement; the parent carries that.
        Assert.Contains("AccountSettings:Transfer:DailyLimit", changed);
    }

    [Fact]
    public void A_removed_node_marks_its_parent()
    {
        var editor = Editor();
        editor.Remove("ConnectionStrings:Couchbase:Modules:Auth");

        var changed = editor.ChangedPaths();

        Assert.Contains("ConnectionStrings:Couchbase:Modules:Auth", changed);
        Assert.Contains("ConnectionStrings:Couchbase:Modules", changed);
        Assert.DoesNotContain("ConnectionStrings:Couchbase:Modules:Account", changed);
    }

    /// <summary>
    /// The marker is computed against the two trees, not from the undo history. An undo, a revert,
    /// or retyping a value back the way it was all leave history behind while leaving the document
    /// unchanged - and a marker driven by history would keep insisting otherwise.
    /// </summary>
    [Fact]
    public void Marks_clear_when_the_document_matches_again_however_it_got_there()
    {
        var editor = Editor();
        var originalUrl = editor.TextAt("ConnectionStrings:Couchbase:Modules:Auth:Url");

        editor.Replace("ConnectionStrings:Couchbase:Modules:Auth:Url", "\"couchbase://10.0.0.1\"");
        Assert.NotEmpty(editor.ChangedPaths());

        // Put it back by hand rather than by undoing: the history still has two entries.
        editor.Replace("ConnectionStrings:Couchbase:Modules:Auth:Url", originalUrl);

        Assert.Equal(2, editor.History.Count);
        Assert.Empty(editor.ChangedPaths());
        Assert.False(editor.IsModified);

        editor.Undo();
        Assert.NotEmpty(editor.ChangedPaths());

        editor.RevertAll();
        Assert.Empty(editor.ChangedPaths());
    }

    // -------------------------------------------------------- kinds of change

    /// <summary>
    /// The marker says which kind of change it is, because "changed" is three different things and
    /// an editor that renders them identically makes you open every marked node to find out which.
    /// </summary>
    [Fact]
    public void A_retyped_value_is_an_edit_and_the_sections_above_it_only_hold_one()
    {
        var editor = Editor();
        editor.Replace("ConnectionStrings:Couchbase:Modules:Auth:Url", "\"couchbase://10.0.0.1\"");

        var kinds = editor.ChangeKinds();

        Assert.Equal(NodeChange.Edited, kinds["ConnectionStrings:Couchbase:Modules:Auth:Url"]);

        // An object that still exists on both sides did not change in itself - things under it did.
        Assert.Equal(NodeChange.Mixed, kinds["ConnectionStrings:Couchbase:Modules:Auth"]);
        Assert.Equal(NodeChange.Mixed, kinds["ConnectionStrings"]);
        Assert.Equal(NodeChange.Mixed, kinds[string.Empty]);

        Assert.DoesNotContain("ConnectionStrings:Couchbase:Modules:Account", kinds.Keys);
    }

    [Fact]
    public void An_added_subtree_is_new_all_the_way_down_and_a_removed_one_is_gone_all_the_way_down()
    {
        var editor = Editor();
        editor.Replace("AccountSettings:Invented", """{ "Nested": { "Deep": true } }""");
        editor.Remove("ConnectionStrings:Couchbase:Modules:Auth");

        var kinds = editor.ChangeKinds();

        Assert.Equal(NodeChange.Added, kinds["AccountSettings:Invented"]);
        Assert.Equal(NodeChange.Added, kinds["AccountSettings:Invented:Nested"]);
        Assert.Equal(NodeChange.Added, kinds["AccountSettings:Invented:Nested:Deep"]);

        Assert.Equal(NodeChange.Removed, kinds["ConnectionStrings:Couchbase:Modules:Auth"]);
        Assert.Equal(NodeChange.Removed, kinds["ConnectionStrings:Couchbase:Modules:Auth:Url"]);
        Assert.Equal(NodeChange.Removed, kinds["ConnectionStrings:Couchbase:Modules:Auth:Bucket"]);

        // Their parents hold the change without claiming to be it.
        Assert.Equal(NodeChange.Mixed, kinds["AccountSettings"]);
        Assert.Equal(NodeChange.Mixed, kinds["ConnectionStrings:Couchbase:Modules"]);
    }

    /// <summary>
    /// One wholesale replacement is routinely all three kinds at once, which is the case the parent
    /// marker exists for: it says "look under here" rather than picking one of the three to be wrong.
    /// </summary>
    [Fact]
    public void A_section_holding_all_three_kinds_is_marked_as_holding_them_not_as_being_one()
    {
        var editor = Editor();
        editor.Replace("AccountSettings:Transfer", """{ "MinAmount": 1, "Nested": { "Deep": true } }""");

        var kinds = editor.ChangeKinds();

        Assert.Equal(NodeChange.Edited, kinds["AccountSettings:Transfer:MinAmount"]);
        Assert.Equal(NodeChange.Added, kinds["AccountSettings:Transfer:Nested"]);
        Assert.Equal(NodeChange.Removed, kinds["AccountSettings:Transfer:DailyLimit"]);
        Assert.Equal(NodeChange.Removed, kinds["AccountSettings:Transfer:MaxAmount"]);

        Assert.Equal(NodeChange.Mixed, kinds["AccountSettings:Transfer"]);
    }

    /// <summary>A node whose type changed is a retype, not a container of changes.</summary>
    [Fact]
    public void An_object_replaced_by_a_scalar_is_an_edit()
    {
        var editor = Editor();
        editor.Replace("AccountSettings:Transfer", "\"gone\"");

        Assert.Equal(NodeChange.Edited, editor.ChangeKinds()["AccountSettings:Transfer"]);
    }

    /// <summary>
    /// A node-scoped diff needs a side for a node that only exists on one of them. The empty string
    /// is what renders that as a whole-block insertion or deletion rather than throwing.
    /// </summary>
    [Fact]
    public void A_node_missing_from_one_side_reads_back_as_empty_rather_than_throwing()
    {
        var editor = Editor();
        const string path = "ConnectionStrings:Couchbase:Modules:Auth";

        editor.Remove(path);

        Assert.NotEmpty(editor.OriginalTextOrEmpty(path));
        Assert.Empty(editor.WorkingTextOrEmpty(path));

        editor.Replace("AccountSettings:Invented", "42");

        Assert.Empty(editor.OriginalTextOrEmpty("AccountSettings:Invented"));
        Assert.Equal("42", editor.WorkingTextOrEmpty("AccountSettings:Invented"));

        // The empty path is the whole document on both sides, which is what the root row compares.
        Assert.Equal(editor.OriginalText, editor.OriginalTextOrEmpty(string.Empty));
        Assert.Equal(editor.WorkingText, editor.WorkingTextOrEmpty(string.Empty));
    }

    // ------------------------------------------------------------------ push

    /// <summary>
    /// What leaves the editor is a whole document, and it has to be one the pusher will accept:
    /// canonical, and holding exactly the keys the edited tree holds.
    /// </summary>
    [Fact]
    public void An_edited_document_is_something_the_pusher_accepts()
    {
        var editor = Editor();
        editor.Replace("AccountSettings:Transfer",
            """{ "DailyLimit": 1, "MaxAmount": 2, "MinAmount": 3, "NewKnob": true }""");

        var (payload, problem) = new VaultPusher(files.Flattener).Payload("beta", editor.Working);

        Assert.Null(problem);
        Assert.Equal(editor.WorkingText, payload);
        Assert.Contains("\"NewKnob\": true", payload!, StringComparison.Ordinal);

        // One key added and none lost, which is what the edit said.
        var before = files.Beta.Flat.Paths.ToHashSet(StringComparer.Ordinal);
        var after = files.Flattener.Flatten("beta", editor.Working).Paths.ToHashSet(StringComparer.Ordinal);
        Assert.Single(after.Except(before, StringComparer.Ordinal));
        Assert.Empty(before.Except(after, StringComparer.Ordinal));
    }

    [Fact]
    public void Removing_a_section_removes_every_key_under_it()
    {
        var editor = Editor();

        var elasticsearchKeys = files.Beta.Flat.Paths
            .Count(p => p.StartsWith("Elasticsearch:", StringComparison.Ordinal));

        Assert.True(elasticsearchKeys > 0);
        editor.Remove("Elasticsearch");

        var after = files.Flattener.Flatten("beta", editor.Working).Paths;

        Assert.DoesNotContain(after, p => p.StartsWith("Elasticsearch", StringComparison.Ordinal));
        Assert.Equal(files.Beta.Flat.Count - elasticsearchKeys, after.Count());
    }

    [Fact]
    public void Pushing_a_read_only_tier_is_refused()
    {
        var readOnly = files.ReadOnly();
        var editor = new DocumentEditor(readOnly);
        editor.Replace("AccountSettings:Transfer", """{ "MinAmount": 1 }""");

        var blocked = VaultPusher.Blocked(readOnly, SampleFiles.Settings());

        Assert.NotNull(blocked);
        Assert.Contains("read-only", blocked, StringComparison.OrdinalIgnoreCase);
    }
}
