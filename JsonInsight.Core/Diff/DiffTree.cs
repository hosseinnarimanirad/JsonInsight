namespace JsonInsight.Diff;

/// <summary>
/// A node in the rolled-up view of a <see cref="MultiDiff"/>.
///
/// The rollup is not cosmetic. When every leaf beneath a node is missing from the same tiers, the
/// node is the thing that is actually missing - AccountSettings:NightlyApprovalJob is one absent
/// feature, not eleven unrelated absent keys - and that node is exactly the unit the promote
/// operation copies. Collapsing the display and defining the promote unit are the same decision.
/// </summary>
public sealed class DiffNode
{
    private readonly List<DiffNode> _children = [];

    public required string Segment { get; init; }

    public required string Path { get; init; }

    public required int Depth { get; init; }

    /// <summary>Set only on nodes that correspond to an actual row (a leaf, or an alias shape row).</summary>
    public MultiRow? Row { get; set; }

    public DiffNode? Parent { get; set; }

    public IReadOnlyList<DiffNode> Children => _children;

    public bool IsLeaf => Row is not null && _children.Count == 0;

    public void Add(DiffNode child)
    {
        child.Parent = this;
        _children.Add(child);
    }

    public IEnumerable<DiffNode> DescendantsAndSelf()
    {
        yield return this;
        foreach (var descendant in _children.SelectMany(c => c.DescendantsAndSelf()))
        {
            yield return descendant;
        }
    }

    public IEnumerable<MultiRow> LeafRows =>
        DescendantsAndSelf().Where(n => n.Row is not null).Select(n => n.Row!);

    public int LeafCount => LeafRows.Count();

    /// <summary>
    /// The tiers that hold nothing at all beneath this node, when that is true of every leaf below
    /// it. Null when the subtree is mixed - which is what stops a rollup from hiding a partial gap.
    /// </summary>
    public IReadOnlyList<string>? UniformlyMissingFrom { get; private set; }

    public bool IsUniformlyMissing => UniformlyMissingFrom is { Count: > 0 };

    /// <summary>True when nothing beneath this node differs in any way.</summary>
    public bool AllIdentical { get; private set; }

    public bool HasMeaningfulDifference { get; private set; }

    public bool HasShapeDifference { get; private set; }

    /// <summary>Computes the rollup flags bottom-up. Call once on the root after building.</summary>
    public void Summarize()
    {
        foreach (var child in _children)
        {
            child.Summarize();
        }

        var rows = LeafRows.ToArray();
        AllIdentical = rows.Length > 0 && rows.All(r => !r.IsDifference);
        HasMeaningfulDifference = rows.Any(r => r.IsMeaningful);
        HasShapeDifference = rows.Any(r => r.AnyShape);

        if (rows.Length == 0)
        {
            UniformlyMissingFrom = null;
            return;
        }

        // A tier qualifies only if it is missing EVERY leaf beneath this node. One present leaf and
        // the subtree is a partial gap, which must stay expanded so the gap is visible.
        var missingEverywhere = rows[0].Cells
            .Where(c => c.State == CellState.Missing)
            .Select(c => c.TierId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var row in rows.Skip(1))
        {
            missingEverywhere.IntersectWith(row.Cells
                .Where(c => c.State == CellState.Missing)
                .Select(c => c.TierId));

            if (missingEverywhere.Count == 0)
            {
                break;
            }
        }

        UniformlyMissingFrom = missingEverywhere.Count > 0
            ? missingEverywhere.Order(StringComparer.Ordinal).ToArray()
            : null;
    }

    /// <summary>
    /// The highest ancestor (including this node) whose whole subtree is missing from exactly the
    /// same tiers. This is the node a Promote button should act on.
    /// </summary>
    public DiffNode PromotionRoot()
    {
        var node = this;
        while (node.Parent is { IsUniformlyMissing: true } parent &&
               parent.Depth > 0 &&
               SameTiers(parent.UniformlyMissingFrom!, node.UniformlyMissingFrom))
        {
            node = parent;
        }

        return node;
    }

    private static bool SameTiers(IReadOnlyList<string> a, IReadOnlyList<string>? b) =>
        b is not null && a.Count == b.Count && a.SequenceEqual(b, StringComparer.Ordinal);

    public static DiffNode Build(MultiDiff diff)
    {
        var root = new DiffNode { Segment = string.Empty, Path = string.Empty, Depth = 0 };
        var index = new Dictionary<string, DiffNode>(StringComparer.Ordinal) { [string.Empty] = root };

        foreach (var row in diff.Rows)
        {
            var segments = ConfigPath.Split(row.Path);
            var current = root;
            var path = string.Empty;

            for (var i = 0; i < segments.Length; i++)
            {
                path = path.Length == 0 ? segments[i] : path + ":" + segments[i];

                if (!index.TryGetValue(path, out var node))
                {
                    node = new DiffNode { Segment = segments[i], Path = path, Depth = i + 1 };
                    current.Add(node);
                    index[path] = node;
                }

                current = node;
            }

            current.Row = row;
        }

        root.Summarize();
        return root;
    }
}
