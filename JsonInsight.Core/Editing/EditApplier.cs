using System.Text.Json;
using System.Text.Json.Nodes;
using JsonInsight.Diff;
using JsonInsight.Model;
using JsonInsight.Promote;

namespace JsonInsight.Editing;

/// <summary>
/// Applies a change set to a copy of a tier's node tree. Touches no source — the write path
/// (<c>VaultPusher</c> and <c>LocalFileSourceProvider</c>) owns that, exactly as it does for Promote.
/// </summary>
public static class EditApplier
{
    /// <summary>
    /// Returns a modified deep clone. The original tree is untouched, so a preview can be produced
    /// and thrown away without consequence, and a failed apply cannot leave the loaded document
    /// half-edited.
    /// </summary>
    public static JsonNode Apply(TierDocument destination, IReadOnlyList<PendingEdit> edits)
    {
        var updated = destination.Root.DeepClone();

        // Deletes last. Applying them first could remove a parent that a sibling add still needs,
        // and the empty-parent pruning below has to see the tree in its final shape to decide
        // correctly which containers are genuinely left empty.
        foreach (var edit in edits.Where(e => e.Kind != EditKind.Delete).OrderBy(e => e.Path, StringComparer.Ordinal))
        {
            ApplySet(updated, edit);
        }

        foreach (var edit in edits.Where(e => e.Kind == EditKind.Delete).OrderByDescending(e => e.Path, StringComparer.Ordinal))
        {
            ApplyDelete(updated, edit);
        }

        return updated;
    }

    private static void ApplySet(JsonNode root, PendingEdit edit)
    {
        var parentPath = ConfigPath.Parent(edit.Path);
        var key = ConfigPath.Last(edit.Path);

        if (key.Contains('['))
        {
            throw new InvalidOperationException(
                $"'{edit.Path}' names an array element. Editing writes object keys only — change the " +
                "array in the file directly, or add an arrays.json strategy for it first.");
        }

        // A leaf inside a keyed array element (Serilog:WriteTo[Name=Seq]:Args:serverUrl) has a real
        // parent object that Find can reach, even though EnsureObject refuses to create one. Reaching
        // for it first is what makes those keys editable without teaching the creator about arrays.
        var parent = JsonNavigator.Find(root, parentPath) as JsonObject
                     ?? JsonNavigator.EnsureObject(root, parentPath);

        parent[key] = ToNode(edit);
    }

    private static void ApplyDelete(JsonNode root, PendingEdit edit)
    {
        var parentPath = ConfigPath.Parent(edit.Path);
        var key = ConfigPath.Last(edit.Path);

        if (JsonNavigator.Find(root, parentPath) is not JsonObject parent)
        {
            throw new InvalidOperationException(
                $"Cannot delete '{edit.Path}': its parent is not an object in this tier.");
        }

        parent.Remove(key);
        PruneEmptyAncestors(root, parentPath);
    }

    /// <summary>
    /// Removes containers left empty by a delete, walking up until something is still occupied.
    ///
    /// <para>
    /// Deliberate rather than tidy-up. The flattener treats <c>{}</c> as a real, comparable leaf —
    /// a tier that dropped every child of a section is genuinely not the same as one that never had
    /// the section — so leaving an emptied parent behind would turn "I deleted this key" into "this
    /// tier now declares an empty section", which is a different statement and would show up in the
    /// grid as a new difference rather than as the removal that was asked for.
    /// </para>
    /// </summary>
    private static void PruneEmptyAncestors(JsonNode root, string path)
    {
        while (path.Length > 0)
        {
            if (JsonNavigator.Find(root, path) is not JsonObject node || node.Count > 0)
            {
                return;
            }

            var parentPath = ConfigPath.Parent(path);
            var key = ConfigPath.Last(path);

            // An emptied element inside an array is left alone: removing it would shift its
            // siblings, and array membership is not something an edit to a key should decide.
            if (key.Contains('[') || JsonNavigator.Find(root, parentPath) is not JsonObject parent)
            {
                return;
            }

            parent.Remove(key);
            path = parentPath;
        }
    }

    /// <summary>
    /// Builds the node to write, honouring the declared type. <see cref="JsonNode.Parse"/> is used
    /// for the non-string kinds so a number keeps the literal form it was typed in — 1.50 stays
    /// 1.50 rather than becoming 1.5, which is the same reason the writer parses rather than
    /// re-encodes elsewhere.
    /// </summary>
    private static JsonNode? ToNode(PendingEdit edit)
    {
        var text = edit.NewValue ?? string.Empty;

        switch (edit.NewKind)
        {
            case JsonValueKind.Null:
                return null;

            case JsonValueKind.True:
            case JsonValueKind.False:
                return JsonValue.Create(
                    bool.TryParse(text, out var flag)
                        ? flag
                        : throw new InvalidOperationException($"'{text}' is not a boolean for {edit.Path}."));

            case JsonValueKind.Number:
                if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out _))
                {
                    throw new InvalidOperationException($"'{text}' is not a number for {edit.Path}.");
                }

                return JsonNode.Parse(text)
                       ?? throw new InvalidOperationException($"'{text}' is not a JSON number for {edit.Path}.");

            default:
                return JsonValue.Create(text);
        }
    }

}
