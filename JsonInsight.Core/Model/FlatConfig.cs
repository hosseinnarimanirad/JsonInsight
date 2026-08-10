namespace JsonInsight.Model;

/// <summary>
/// A tier flattened to canonical <c>Section:Sub:Key</c> leaves. This is the form everything
/// diffs against; the <see cref="TierDocument"/> JsonNode tree is only used for writing.
/// </summary>
public sealed class FlatConfig
{
    private readonly Dictionary<string, Leaf> _leaves;

    public FlatConfig(string tierId, IEnumerable<Leaf> leaves, IEnumerable<string>? warnings = null)
    {
        TierId = tierId;
        // Ordinal: config paths are compared exactly. A case-only collision is a defect, not a match.
        _leaves = new Dictionary<string, Leaf>(StringComparer.Ordinal);
        foreach (var leaf in leaves)
        {
            _leaves[leaf.Path] = leaf;
        }

        Warnings = warnings?.ToArray() ?? [];
    }

    public string TierId { get; }

    public IReadOnlyList<string> Warnings { get; }

    public IReadOnlyDictionary<string, Leaf> Leaves => _leaves;

    public int Count => _leaves.Count;

    public IEnumerable<string> Paths => _leaves.Keys;

    public Leaf? Find(string path) => _leaves.GetValueOrDefault(path);

    public bool Contains(string path) => _leaves.ContainsKey(path);

    /// <summary>All leaves at or beneath <paramref name="prefix"/> (which may itself be a leaf).</summary>
    public IEnumerable<Leaf> Subtree(string prefix)
    {
        var withSeparator = prefix + ":";
        foreach (var (path, leaf) in _leaves)
        {
            if (path.Equals(prefix, StringComparison.Ordinal) ||
                path.StartsWith(withSeparator, StringComparison.Ordinal))
            {
                yield return leaf;
            }
        }
    }

    /// <summary>Top-level section names, ordinal-sorted.</summary>
    public IReadOnlyList<string> Sections =>
        _leaves.Keys
            .Select(p => p.Split(':', 2)[0])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
}
