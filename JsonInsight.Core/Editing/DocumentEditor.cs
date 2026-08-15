using System.Text.Json.Nodes;
using JsonInsight.Diff;
using JsonInsight.Model;
using JsonInsight.Promote;

namespace JsonInsight.Editing;

/// <summary>One step on the undo stack: the whole tree before a replacement, and what was done.</summary>
public sealed record DocumentEdit(string Path, string Description, JsonNode Before);

/// <summary>
/// What happened to one node since the document was opened.
///
/// <para>
/// The three leaf kinds are the ones an editor has to tell apart on sight — something appeared,
/// something went, something was retyped. <see cref="Mixed"/> is the fourth because a section is not
/// any one of them: it still exists on both sides and merely holds changes, which may be a
/// combination of all three, so it is marked as "there is something under here" rather than being
/// given a kind it does not have.
/// </para>
/// </summary>
public enum NodeChange
{
    None,
    Added,
    Edited,
    Removed,
    Mixed,
}

/// <summary>
/// Whole-subtree editing of one tier's document, held entirely in memory.
///
/// <para>
/// One of these per loaded tier, owned by <see cref="DocumentStore"/>, is what the whole app now
/// edits: <see cref="Working"/> is what every tab displays and what a push sends. There used to be a
/// second model beside it — a queue of per-key changes with a per-key base to detect drift — and the
/// two could hold different answers for the same key, with whichever was pushed first silently
/// winning. Key edits are applied into this tree now (see <c>DocumentStore.Apply</c>), so there is
/// one answer to what a tier says.
/// </para>
///
/// <para>
/// Every step keeps the complete tree rather than a patch. These documents are ~28 KB, a deep clone
/// costs nothing measurable, and it makes undo exact - a patch-based undo has to reason about what a
/// wholesale replacement removed, which is the case most likely to be got wrong.
/// </para>
/// </summary>
public sealed class DocumentEditor
{
    private readonly List<DocumentEdit> _undo = [];
    private readonly List<DocumentEdit> _redo = [];

    public DocumentEditor(TierDocument document)
    {
        Document = document;
        Original = OrdinalJsonWriter.SortedClone(document.Root);
        Working = OrdinalJsonWriter.SortedClone(document.Root);
    }

    public TierDocument Document { get; }

    /// <summary>The tree as loaded. Never mutated - it is what "compare with the original" compares against.</summary>
    public JsonNode Original { get; }

    /// <summary>The tree as edited so far.</summary>
    public JsonNode Working { get; private set; }

    public IReadOnlyList<DocumentEdit> History => _undo;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public bool IsModified => !OriginalText.Equals(WorkingText, StringComparison.Ordinal);

    public string OriginalText => OrdinalJsonWriter.SerializeToText(Original);

    public string WorkingText => OrdinalJsonWriter.SerializeToText(Working);

    /// <summary>The canonical text of one node's subtree - what the editor pane shows for it.</summary>
    public string TextAt(string path) => TextOf(Working, path, "is not in this document");

    public JsonNode? Find(string path) => JsonNavigator.Find(Working, path);

    /// <summary>The node as it stood when this document was opened. Null if it did not exist then.</summary>
    public JsonNode? FindOriginal(string path) => JsonNavigator.Find(Original, path);

    /// <summary>
    /// True when the document holds this key, whatever it holds — a key set to JSON <c>null</c>
    /// included.
    ///
    /// <para>
    /// <see cref="Find"/> cannot answer this: it returns null both for a key that is not there and
    /// for one whose value is null, and every one of these tiers has two of the latter. Asking the
    /// parent whether it has the key is the only way to tell those apart.
    /// </para>
    /// </summary>
    public bool Holds(string path) => path.Length == 0 || Holds(Working, path);

    /// <summary>The same question of the document as it was opened.</summary>
    public bool HoldsOriginal(string path) => path.Length == 0 || Holds(Original, path);

    private static string TextOf(JsonNode root, string path, string complaint)
    {
        if (path.Length > 0 && !Holds(root, path))
        {
            throw new InvalidOperationException($"'{path}' {complaint}.");
        }

        // Text() renders a null value as the literal null rather than tripping over it.
        return Text(JsonNavigator.Find(root, path));
    }

    /// <summary>
    /// One node's canonical text as the document was opened, or the empty string when it was not
    /// there then. The empty string rather than an exception because that is exactly what a diff
    /// wants for a node that has since been added: nothing on the left, the whole block on the right.
    /// </summary>
    public string OriginalTextOrEmpty(string path) => TextOrEmpty(Original, path);

    /// <summary>The same for the edited tree, so a removed node diffs as a whole-block deletion.</summary>
    public string WorkingTextOrEmpty(string path) => TextOrEmpty(Working, path);

    /// <summary>
    /// Asks the parent whether it holds the key rather than whether the node resolves, so a key set
    /// to JSON <c>null</c> reads as present-and-null instead of as absent.
    /// </summary>
    private static string TextOrEmpty(JsonNode root, string path) =>
        path.Length == 0
            ? OrdinalJsonWriter.SerializeToText(root)
            : Holds(root, path) ? Text(JsonNavigator.Find(root, path)) : string.Empty;

    /// <summary>
    /// True when a key was there when the document was opened and is not there now.
    ///
    /// <para>
    /// Asks the parent whether it still holds the key rather than whether the node resolves to
    /// null — a key whose value is JSON <c>null</c> is present, and treating it as removed would
    /// put a tombstone over a perfectly live setting.
    /// </para>
    /// </summary>
    public bool IsRemovedSinceOpened(string path) =>
        path.Length > 0 && Holds(Original, path) && !Holds(Working, path);

    private static bool Holds(JsonNode root, string path)
    {
        if (IsArrayElement(path))
        {
            // An array holds a position, not a key, so the question is whether the slot resolves.
            // Asking the parent object for "WriteTo[Name=Console]" - which is what the object branch
            // below does - answers no for every element there has ever been.
            try
            {
                return ElementSlot(root, path) is not null;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        return JsonNavigator.Find(root, ConfigPath.Parent(path)) is JsonObject parent &&
               parent.ContainsKey(ConfigPath.Last(path));
    }

    /// <summary>
    /// Replaces the subtree at <paramref name="path"/> with <paramref name="json"/>, wholesale.
    ///
    /// <para>
    /// Any valid JSON is accepted, including one of a different shape from what is there - an object
    /// becoming an array, or a scalar. "Totally replace this node" is the operation, and refusing a
    /// shape change would make it something narrower without making it safer: the byte-exact
    /// round-trip guard and the leaf-set check still stand between this and a file.
    /// </para>
    /// </summary>
    /// <param name="coalesce">
    /// Folds this replacement into the previous one when it is the same node, instead of adding a
    /// step. It is how a value typed one character at a time stays one entry on the undo stack: the
    /// caller sets it only for a continuing run of keystrokes on the node it last edited, so undo
    /// goes back to what the value was before the typing started rather than to a state that existed
    /// only mid-word.
    /// </param>
    public void Replace(string path, string json, bool coalesce = false)
    {
        JsonNode? replacement;
        try
        {
            // Allowing null: a key set to JSON null is a real, distinct configuration state, and the
            // rest of this app treats it as one. Refusing it here made the one value you cannot type
            // also the one the editor reported as invalid JSON, which it is not.
            replacement = OrdinalJsonWriter.ParseAllowingNull(json);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"That is not valid JSON: {ex.Message}", ex);
        }

        var before = OrdinalJsonWriter.SortedClone(Working);

        if (path.Length == 0)
        {
            // The root keeps the kind it was opened as. An object root is bound as the configuration
            // root and cannot become anything else; an array-rooted document - a banner list, a
            // service catalogue - is a list, and turning it into an object is not an edit to that
            // document but a different document in its place.
            var sameKind = (Working, replacement) switch
            {
                (JsonObject, JsonObject) => true,
                (JsonArray, JsonArray) => true,
                _ => false,
            };

            if (replacement is null || !sameKind)
            {
                throw new InvalidOperationException(
                    Working is JsonArray
                        ? "This document's root is a JSON array, and replacing it has to be one too."
                        : "The document root must stay a JSON object - it is bound as the configuration root.");
            }

            Working = replacement;
        }
        else
        {
            ReplaceChild(path, replacement);
        }

        // Same node, continuing run: keep the step already on the stack, which holds the tree from
        // before the run began. Replacing it with the current one would make undo a no-op.
        if (coalesce && _undo.Count > 0 && _undo[^1].Path.Equals(path, StringComparison.Ordinal))
        {
            _redo.Clear();
            return;
        }

        _undo.Add(new DocumentEdit(path, Describe(path), before));
        _redo.Clear();
    }

    private void ReplaceChild(string path, JsonNode? replacement)
    {
        // Assigning a node that already has a parent throws, and a replacement parsed from text
        // never does - but a caller could hand back a node lifted from elsewhere in the tree.
        var value = replacement?.Parent is null ? replacement : replacement.DeepClone();

        if (ElementSlot(Working, path) is var (array, index))
        {
            // In place, so no index shifts and nothing after it moves. That is what makes replacing
            // an element safe where removing one is not - see Remove.
            array[index] = value;
            return;
        }

        var parentPath = ConfigPath.Parent(path);
        var key = ConfigPath.Last(path);

        if (JsonNavigator.Find(Working, parentPath) is not JsonObject parent)
        {
            throw new InvalidOperationException($"'{path}' has no parent object in this document.");
        }

        parent[key] = value;
    }

    /// <summary>
    /// The array and position a path names, when its last segment is an element -
    /// <c>Serilog:WriteTo[Name=Seq]</c> or <c>configuration:banners[3]</c>. Null for an ordinary key.
    ///
    /// <para>
    /// Both forms resolve, because both are produced: the flattener names a keyed array's elements by
    /// identity so an insertion does not shift its siblings, and falls back to the index form for an
    /// array with no declared strategy. A path from anywhere in this app is one of the two.
    /// </para>
    /// </summary>
    private static (JsonArray Array, int Index)? ElementSlot(JsonNode root, string path)
    {
        var last = ConfigPath.Last(path);
        var (name, identityField, identityValue, index) = JsonNavigator.ParseSegment(last);

        if (identityField is null && index is null)
        {
            return null;
        }

        var ownerPath = ConfigPath.Parent(path);
        var owner = ownerPath.Length == 0 ? root : JsonNavigator.Find(root, ownerPath);

        // With no name in front of the brackets the owner *is* the array - "[0]" against a document
        // whose root is an array, or an element of an array of arrays. With a name, the array is the
        // key of that name on the owner.
        var array = name.Length == 0
            ? owner as JsonArray
            : owner is JsonObject holder && holder.TryGetPropertyValue(name, out var child)
                ? child as JsonArray
                : null;

        if (array is null)
        {
            throw new InvalidOperationException($"'{path}' does not name an array in this document.");
        }

        if (index is { } position)
        {
            return position >= 0 && position < array.Count
                ? (array, position)
                : throw new InvalidOperationException($"'{path}' is past the end of an array of {array.Count}.");
        }

        var found = JsonNavigator.IndexOfIdentity(array, identityField!, identityValue);

        return found >= 0
            ? (array, found)
            : throw new InvalidOperationException($"'{path}' is not in this document.");
    }

    /// <summary>True when this path names an array element rather than a key.</summary>
    public static bool IsArrayElement(string path)
    {
        var (_, identityField, _, index) = JsonNavigator.ParseSegment(ConfigPath.Last(path));
        return identityField is not null || index is not null;
    }

    /// <summary>
    /// Puts one node back the way it was when this document was opened, leaving every other edit
    /// alone.
    ///
    /// <para>
    /// Distinct from undo, which walks the history backwards and would take unrelated later edits
    /// with it. This is aimed at a node: it reverses whatever happened to that subtree, whether it
    /// was replaced, emptied, or added since opening — an added node reverts by being removed, since
    /// "the way it was" is not there.
    /// </para>
    /// </summary>
    public void RevertNode(string path)
    {
        var before = OrdinalJsonWriter.SortedClone(Working);
        var original = JsonNavigator.Find(Original, path);

        if (path.Length == 0)
        {
            Working = OrdinalJsonWriter.SortedClone(Original);
        }
        else if (original is null)
        {
            if (IsArrayElement(path))
            {
                // Reverting an element that was not there when the document opened means taking it
                // back out, and that shifts its siblings. Same reason as Remove.
                throw new InvalidOperationException(
                    $"'{path}' was not in this document when it was opened, so undoing it means " +
                    "removing it — which shifts every element after it. Revert the array instead.");
            }

            var parentPath = ConfigPath.Parent(path);
            if (JsonNavigator.Find(Working, parentPath) is JsonObject parent)
            {
                parent.Remove(ConfigPath.Last(path));
            }
        }
        else if (IsArrayElement(path))
        {
            ReplaceChild(path, original.DeepClone());
        }
        else
        {
            // The parent may itself have been replaced or removed since, so it is ensured rather
            // than assumed - reverting a node must not depend on its surroundings being untouched.
            var parentPath = ConfigPath.Parent(path);
            var key = ConfigPath.Last(path);

            JsonNavigator.EnsureObject(Working, parentPath)[key] = original.DeepClone();
        }

        _undo.Add(new DocumentEdit(path, path.Length == 0
            ? "undid every change"
            : $"undid changes to {path}", before));

        _redo.Clear();
    }

    /// <summary>Deletes the node at <paramref name="path"/> outright.</summary>
    public void Remove(string path)
    {
        if (path.Length == 0)
        {
            throw new InvalidOperationException("The document root cannot be removed.");
        }

        if (IsArrayElement(path))
        {
            // Deleting from the middle of an array moves every element after it up one, and this
            // editor shows a deletion as a tombstone measured against the document as opened - so
            // one removal would present as "one gone and all the rest rewritten". Replacing the
            // array wholesale says what actually happened, in one edit, with a diff to match.
            throw new InvalidOperationException(
                $"'{path}' is an array element. Removing one shifts every element after it, which " +
                "this editor cannot show honestly — select the array itself and delete the element " +
                "from its JSON instead.");
        }

        var parentPath = ConfigPath.Parent(path);
        var key = ConfigPath.Last(path);

        if (JsonNavigator.Find(Working, parentPath) is not JsonObject parent || !parent.ContainsKey(key))
        {
            throw new InvalidOperationException($"'{path}' is not in this document.");
        }

        var before = OrdinalJsonWriter.SortedClone(Working);
        parent.Remove(key);

        _undo.Add(new DocumentEdit(path, $"removed {path}", before));
        _redo.Clear();
    }

    public void Undo()
    {
        if (_undo.Count == 0)
        {
            return;
        }

        var step = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(step with { Before = OrdinalJsonWriter.SortedClone(Working) });

        Working = step.Before;
    }

    public void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        var step = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(step with { Before = OrdinalJsonWriter.SortedClone(Working) });

        Working = step.Before;
    }

    /// <summary>
    /// Every path that differs from the state this document was opened in, plus every ancestor of
    /// one — so a change buried six levels down marks the sections above it and can be found by
    /// following the marks rather than by remembering where you left off.
    ///
    /// <para>
    /// Computed structurally against the two trees rather than from the undo history. An undo, a
    /// revert, or an edit that puts a value back the way it was all leave the history non-empty
    /// while leaving the document unchanged, and a marker driven by the history would keep claiming
    /// otherwise.
    /// </para>
    ///
    /// <para>
    /// The empty path is in the set whenever anything at all differs, which is what marks the root
    /// row.
    /// </para>
    /// </summary>
    public IReadOnlySet<string> ChangedPaths() =>
        new HashSet<string>(ChangeKinds().Keys, StringComparer.Ordinal);

    /// <summary>
    /// The same walk as <see cref="ChangedPaths"/>, but saying of each path <em>which</em> kind of
    /// change it is, so the tree can render an addition, an edit and a removal as three different
    /// things rather than as one undifferentiated "changed" mark.
    ///
    /// <para>
    /// Only changed paths are present; anything absent is <see cref="NodeChange.None"/>.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, NodeChange> ChangeKinds()
    {
        var kinds = new Dictionary<string, NodeChange>(StringComparer.Ordinal);

        // Compare records the root itself only on the object branch, because that is the branch that
        // has children to attribute the change to. A document whose root is an array is compared
        // whole, so if it differs at all the only true statement is about the root - and without
        // this the one row that could carry the mark would be the one row that never did.
        if (Compare(Original, Working, string.Empty, kinds) && !kinds.ContainsKey(string.Empty))
        {
            kinds[string.Empty] = NodeChange.Edited;
        }

        return kinds;
    }

    /// <summary>Returns true when this node differs, having already recorded any differing descendants.</summary>
    private static bool Compare(
        JsonNode? original,
        JsonNode? working,
        string path,
        Dictionary<string, NodeChange> kinds)
    {
        if (original is not JsonObject before || working is not JsonObject after)
        {
            // Arrays and scalars are compared whole. An array's elements are not separately
            // addressable in this tree, so "the array changed" is the finest true statement.
            return !Text(original).Equals(Text(working), StringComparison.Ordinal);
        }

        var any = false;

        foreach (var key in before.Select(p => p.Key).Union(after.Select(p => p.Key), StringComparer.Ordinal))
        {
            var childPath = path.Length == 0 ? key : $"{path}:{key}";

            var hadIt = before.TryGetPropertyValue(key, out var was);
            var hasIt = after.TryGetPropertyValue(key, out var now);

            bool differs;
            if (hadIt != hasIt)
            {
                // Added or removed, and everything under it either way: an added subtree is new
                // throughout, and a removed one is shown as a tombstone throughout, so both need
                // marking or the "changed only" filter would hide most of what it should show.
                // The kind carries down with it - every key inside a new section is itself new.
                differs = true;
                MarkAll(hasIt ? now : was, childPath, hasIt ? NodeChange.Added : NodeChange.Removed, kinds);
            }
            else
            {
                differs = Compare(was, now, childPath, kinds);

                if (differs)
                {
                    // An object still present on both sides did not change in itself - things under
                    // it did, and they may be a mix of all three kinds. Anything else was retyped.
                    kinds[childPath] = was is JsonObject && now is JsonObject
                        ? NodeChange.Mixed
                        : NodeChange.Edited;
                }
            }

            any |= differs;
        }

        if (any)
        {
            kinds[path] = NodeChange.Mixed;
        }

        return any;
    }

    private static void MarkAll(JsonNode? node, string path, NodeChange kind, Dictionary<string, NodeChange> kinds)
    {
        kinds[path] = kind;

        if (node is not JsonObject obj)
        {
            return;
        }

        foreach (var (key, child) in obj)
        {
            MarkAll(child, path.Length == 0 ? key : $"{path}:{key}", kind, kinds);
        }
    }

    private static string Text(JsonNode? node) =>
        node is null ? "null" : OrdinalJsonWriter.SerializeToText(node);

    /// <summary>Back to the state this was opened in, as one undoable step.</summary>
    public void RevertAll()
    {
        if (!IsModified)
        {
            return;
        }

        var before = OrdinalJsonWriter.SortedClone(Working);
        Working = OrdinalJsonWriter.SortedClone(Original);

        _undo.Add(new DocumentEdit(string.Empty, "reverted everything", before));
        _redo.Clear();
    }

    private string Describe(string path)
    {
        var where = path.Length == 0 ? "the whole document" : path;
        return $"replaced {where}";
    }
}
