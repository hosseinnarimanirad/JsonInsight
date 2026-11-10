using System.Text.Json.Nodes;
using JsonInsight.Classify;
using JsonInsight.Model;
using JsonInsight.Promote;

namespace JsonInsight.History;

/// <summary>What happened to a node in one version, relative to the version read before it.</summary>
public enum NodeChange
{
    /// <summary>
    /// Present in the oldest version Vault still keeps. Not <see cref="Added"/>: with
    /// <c>max_versions</c> in force the node may be older than any version that is left.
    /// </summary>
    FirstKept,

    Added,
    Changed,
    Removed,
    Unchanged,

    /// <summary>Not present, and was not present in the version before either.</summary>
    Absent,
}

/// <summary>
/// One version's view of one node.
///
/// <para>
/// <see cref="Text"/> is the node as canonical JSON — the real value, secrets included, because it is
/// what the diff and Copy value need, exactly as the Tier editor's own pane holds it.
/// <see cref="Preview"/> is the one-line summary a history list shows, and it never reveals a secret.
/// </para>
/// </summary>
public sealed record NodeRevision(
    int Version,
    DateTimeOffset? CreatedTime,
    NodeChange Change,
    string? Text,
    string Preview)
{
    public bool Present => Text is not null;

    /// <summary>The versions a history list shows by default: the ones where the node changed.</summary>
    public bool IsChangePoint => Change is not (NodeChange.Unchanged or NodeChange.Absent);
}

/// <summary>
/// Works out, for one node, which versions of a secret changed it. Pure: it reads only the versions
/// it is handed, and keeps nothing.
/// </summary>
public static class NodeHistory
{
    private const int PreviewLength = 80;

    /// <summary>
    /// One revision per version, newest first. <paramref name="path"/> is a canonical configuration
    /// path (<c>PaymentSettings:Hub</c>, <c>Serilog:WriteTo[Name=Seq]</c>); empty means the whole
    /// document.
    ///
    /// <para>
    /// Each version is compared with the one read just before it. Change is decided on the
    /// flattened leaves — the same rule the All tiers grid uses — so reordering an array that
    /// <c>arrays.json</c> declares a set is not a change, while reformatting is never one either.
    /// </para>
    /// </summary>
    public static IReadOnlyList<NodeRevision> Build(string path, IEnumerable<SecretVersion> versions)
    {
        var ascending = versions.OrderBy(v => v.Version).ToArray();
        var result = new List<NodeRevision>(ascending.Length);

        string? previous = null;
        var first = true;

        foreach (var version in ascending)
        {
            // Holds rather than Find alone: a key set to JSON null is present, and restoring it
            // has to put the null back rather than remove the key.
            var present = Present(version.Root, path);
            var node = present ? JsonNavigator.Find(version.Root, path) : null;
            var signature = present ? Signature(version.Flat, path, node) : null;

            var change = (first, previous is not null, signature is not null) switch
            {
                (true, _, true) => version.Version == 1 ? NodeChange.Added : NodeChange.FirstKept,
                (true, _, false) => NodeChange.Absent,
                (false, false, true) => NodeChange.Added,
                (false, true, false) => NodeChange.Removed,
                (false, false, false) => NodeChange.Absent,
                _ => string.Equals(previous, signature, StringComparison.Ordinal)
                    ? NodeChange.Unchanged
                    : NodeChange.Changed,
            };

            result.Add(new NodeRevision(
                version.Version,
                version.CreatedTime,
                change,
                present ? Text(node) : null,
                present ? Preview(version.Flat, path, node) : "—"));

            previous = signature;
            first = false;
        }

        result.Reverse();
        return result;
    }

    /// <summary>The node at <paramref name="path"/> as canonical JSON, or null when it is absent.</summary>
    public static string? TextAt(JsonNode root, string path) =>
        Present(root, path) ? Text(JsonNavigator.Find(root, path)) : null;

    /// <summary>Whether the version holds the key at all — a key set to JSON null included.</summary>
    private static bool Present(JsonNode root, string path) =>
        path.Length == 0 || Editing.DocumentEditor.Holds(root, path);

    private static string Text(JsonNode? node) =>
        node is null ? "null" : OrdinalJsonWriter.SerializeToText(node);

    /// <summary>
    /// What a node "is" for change detection: its leaves and their comparable values, in path order.
    /// A node with no leaves at all (an empty object or array) falls back to its canonical text, so
    /// going from <c>{}</c> to <c>[]</c> still counts.
    /// </summary>
    internal static string Signature(FlatConfig flat, string path, JsonNode? node)
    {
        var leaves = Leaves(flat, path)
            .OrderBy(l => l.Path, StringComparer.Ordinal)
            .Select(l => $"{l.Path}\u001f{l.Kind}\u001f{l.ComparableValue}")
            .ToArray();

        return leaves.Length > 0
            ? string.Join('\u001e', leaves)
            : "\u001d" + (node is null ? "null" : OrdinalJsonWriter.SerializeCompactToText(node));
    }

    /// <summary>
    /// One line for a history list. A single leaf shows its value — masked when it is a secret — and
    /// a container shows how many keys it holds, since its contents are what the diff is for.
    /// </summary>
    internal static string Preview(FlatConfig flat, string path, JsonNode? node)
    {
        if (path.Length > 0 && flat.Find(path) is { } leaf)
        {
            var shown = SecretMasker.Describe(leaf);
            return shown.Length <= PreviewLength ? shown : string.Concat(shown.AsSpan(0, PreviewLength), "…");
        }

        var count = Leaves(flat, path).Count();
        return count == 1 ? "1 key" : $"{count} keys";
    }

    /// <summary>
    /// The leaves at or beneath <paramref name="path"/>. Not <see cref="FlatConfig.Subtree"/> alone:
    /// an array's elements are flattened as <c>WriteTo[Name=Seq]:…</c> or <c>Items[0]</c>, with no
    /// <c>:</c> after the array's own name, so the array node's leaves need the <c>[</c> form too.
    /// </summary>
    private static IEnumerable<Leaf> Leaves(FlatConfig flat, string path)
    {
        if (path.Length == 0)
        {
            return flat.Leaves.Values;
        }

        var element = path + "[";
        return flat.Subtree(path).Concat(flat.Leaves.Values.Where(l =>
            l.Path.StartsWith(element, StringComparison.Ordinal)));
    }
}
