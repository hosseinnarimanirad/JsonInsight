using JsonInsight.Model;

namespace JsonInsight.Diff;

/// <summary>Result of comparing exactly two tiers.</summary>
public sealed class TierDiff
{
    public required string LeftTierId { get; init; }

    public required string RightTierId { get; init; }

    public required IReadOnlyList<DiffEntry> Entries { get; init; }

    public required IReadOnlyList<ResolvedAlias> AppliedAliases { get; init; }

    public IEnumerable<DiffEntry> Differences => Entries.Where(e => e.IsDifference);

    public int OnlyInLeft => Entries.Count(e => e.Kind == DiffKind.OnlyInLeft);

    public int OnlyInRight => Entries.Count(e => e.Kind == DiffKind.OnlyInRight);

    public int ValueDifferences => Entries.Count(e =>
        e.Kind is DiffKind.ValueDiffers or DiffKind.TypeDiffers or DiffKind.SetDiffers);

    public int ShapeDifferences => Entries.Count(e => e.Kind == DiffKind.ShapeDiffers);

    public int Meaningful => Entries.Count(e => e.IsMeaningful);

    public int Expected => Entries.Count(e => e.IsExpected);

    public DiffEntry? Find(string path) =>
        Entries.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.Ordinal));
}

public sealed class TierDiffer
{
    private readonly AliasSet _aliases;

    public TierDiffer(AliasSet aliases) => _aliases = aliases;

    public TierDiff Compare(FlatConfig left, FlatConfig right)
    {
        var entries = new List<DiffEntry>();
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        var applied = _aliases.Resolve(left, right);

        foreach (var alias in applied)
        {
            var leftLeaves = left.Subtree(alias.LeftRoot).ToArray();
            var rightLeaves = right.Subtree(alias.RightRoot).ToArray();

            foreach (var leaf in leftLeaves)
            {
                consumed.Add(leaf.Path);
            }

            foreach (var leaf in rightLeaves)
            {
                consumed.Add(leaf.Path);
            }

            entries.Add(new DiffEntry
            {
                Path = alias.DisplayPath,
                Kind = DiffKind.ShapeDiffers,
                Detail = BuildShapeDetail(alias, left, right, leftLeaves, rightLeaves),
            });
        }

        foreach (var path in left.Paths.Concat(right.Paths).Distinct(StringComparer.Ordinal))
        {
            if (consumed.Contains(path))
            {
                continue;
            }

            entries.Add(DiffEntry.Compare(path, left.Find(path), right.Find(path)));
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        return new TierDiff
        {
            LeftTierId = left.TierId,
            RightTierId = right.TierId,
            Entries = entries,
            AppliedAliases = applied,
        };
    }

    private static string BuildShapeDetail(
        ResolvedAlias alias,
        FlatConfig left,
        FlatConfig right,
        IReadOnlyCollection<Leaf> leftLeaves,
        IReadOnlyCollection<Leaf> rightLeaves)
    {
        var detail =
            $"{left.TierId}: {alias.LeftRoot} ({leftLeaves.Count} keys)  |  " +
            $"{right.TierId}: {alias.RightRoot} ({rightLeaves.Count} keys)";

        return alias.Note is null ? detail : detail + "\n" + alias.Note;
    }
}
