using JsonInsight.Model;

namespace JsonInsight.Diff;

public enum CellState
{
    Present,
    Missing,

    /// <summary>The tier holds this concept in an incomparable shape. See config/aliases.json.</summary>
    Shape,

    /// <summary>
    /// Vault could not be read for this tier, so nothing is known about this key in it.
    ///
    /// <para>
    /// Emphatically not <see cref="Missing"/>. Missing is a finding — this tier does not have the
    /// key — and it is the finding this whole app exists to surface. "I could not ask" is the
    /// absence of a finding, and rendering the two the same way would fill the grid with hundreds of
    /// gaps that are not there.
    /// </para>
    /// </summary>
    Unavailable,
}

public sealed record MultiCell(string TierId, CellState State, Leaf? Leaf, string? Detail = null)
{
    /// <summary>
    /// Falls back to <see cref="Detail"/> when there is no leaf, which is how a rolled-up group row
    /// shows "11 keys" in the tiers that do have the subtree.
    /// </summary>
    public string Display => State switch
    {
        CellState.Missing => "—",
        CellState.Shape => "~shape",
        CellState.Unavailable => "?",
        _ => Leaf?.DisplayValue ?? Detail ?? string.Empty,
    };

    public bool IsKnown => State != CellState.Unavailable;
}

/// <summary>
/// One column of the grid: a tier, and what is known about it. A null <see cref="Flat"/> is a tier
/// Vault could not serve — it keeps its column and every cell in it reads as unknown.
/// </summary>
public sealed record TierColumn(string TierId, FlatConfig? Flat)
{
    public bool IsAvailable => Flat is not null;

    public static IReadOnlyList<TierColumn> From(IEnumerable<FlatConfig> tiers) =>
        tiers.Select(t => new TierColumn(t.TierId, t)).ToArray();
}

/// <summary>One canonical path shown across every selected tier.</summary>
public sealed class MultiRow
{
    public required string Path { get; init; }

    public required IReadOnlyList<MultiCell> Cells { get; init; }

    public required ValueClass Class { get; init; }

    public string? Detail { get; init; }

    public bool AnyMissing => Cells.Any(c => c.State == CellState.Missing);

    public bool AnyShape => Cells.Any(c => c.State == CellState.Shape);

    /// <summary>
    /// Every cell that could be asked. A tier Vault could not serve is excluded from every judgement
    /// this row makes — it has not been found to differ, and it has not been found to agree.
    /// </summary>
    private IEnumerable<MultiCell> Known => Cells.Where(c => c.IsKnown);

    public bool AllPresent => Known.All(c => c.State == CellState.Present);

    /// <summary>True when every tier that has the value agrees on it and none are missing.</summary>
    public bool Identical
    {
        get
        {
            if (!AllPresent)
            {
                return false;
            }

            var known = Known.ToArray();
            if (known.Length == 0)
            {
                // Nothing could be asked, so nothing differs. A row of unknowns is not a finding.
                return true;
            }

            var first = known[0].Leaf!.ComparableValue;
            var firstKind = known[0].Leaf!.Kind;
            return known.All(c =>
                string.Equals(c.Leaf!.ComparableValue, first, StringComparison.Ordinal) &&
                c.Leaf!.Kind == firstKind);
        }
    }

    public bool IsDifference => !Identical;

    public bool IsExpected => IsDifference && Class == ValueClass.Infra;

    public bool IsMeaningful => IsDifference && Class != ValueClass.Infra;

    public MultiCell Cell(string tierId) =>
        Cells.First(c => string.Equals(c.TierId, tierId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Compares any number of tiers into one row-per-path table.</summary>
public sealed class MultiDiff
{
    public required IReadOnlyList<string> TierIds { get; init; }

    public required IReadOnlyList<MultiRow> Rows { get; init; }

    public required IReadOnlyList<MultiAlias> AppliedAliases { get; init; }

    public int MissingCount => Rows.Count(r => r.AnyMissing);

    public int DifferingCount => Rows.Count(r => r is { IsDifference: true, AnyMissing: false, AnyShape: false });

    public int ExpectedCount => Rows.Count(r => r.IsExpected);

    public int MeaningfulCount => Rows.Count(r => r.IsMeaningful);

    /// <summary>Compares the tiers that could be read; a tier that could not keeps its column.</summary>
    public static MultiDiff Build(IReadOnlyList<TierColumn> columns, AliasSet aliases)
    {
        var tiers = columns.Where(c => c.IsAvailable).Select(c => c.Flat!).ToArray();
        var built = Build(tiers, aliases);

        if (tiers.Length == columns.Count)
        {
            return built;
        }

        // Re-spread every row across the full column list, so the unreadable tiers sit in their
        // configured position rather than being appended after the ones that answered.
        var rows = built.Rows
            .Select(row => new MultiRow
            {
                Path = row.Path,
                Class = row.Class,
                Detail = row.Detail,
                Cells = columns
                    .Select(c => c.IsAvailable
                        ? row.Cell(c.TierId)
                        : new MultiCell(c.TierId, CellState.Unavailable, null))
                    .ToArray(),
            })
            .ToArray();

        return new MultiDiff
        {
            TierIds = columns.Select(c => c.TierId).ToArray(),
            Rows = rows,
            AppliedAliases = built.AppliedAliases,
        };
    }

    public static MultiDiff Build(IReadOnlyList<FlatConfig> tiers, AliasSet aliases)
    {
        var appliedAliases = aliases.ResolveMulti(tiers);
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<MultiRow>();

        foreach (var alias in appliedAliases)
        {
            foreach (var tier in tiers)
            {
                foreach (var leaf in tier.Subtree(alias.RootsByTier[tier.TierId]))
                {
                    consumed.Add(leaf.Path);
                }
            }

            var counts = string.Join("  |  ", tiers.Select(t =>
                $"{t.TierId}: {alias.RootsByTier[t.TierId]} ({alias.LeafCountsByTier[t.TierId]} keys)"));

            rows.Add(new MultiRow
            {
                Path = alias.DisplayPath,
                Class = ValueClass.Business,
                Detail = alias.Note is null ? counts : counts + "\n" + alias.Note,
                Cells = tiers
                    .Select(t => new MultiCell(t.TierId, CellState.Shape, null,
                        alias.RootsByTier[t.TierId]))
                    .ToArray(),
            });
        }

        var allPaths = tiers
            .SelectMany(t => t.Paths)
            .Where(p => !consumed.Contains(p))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        foreach (var path in allPaths)
        {
            var cells = new List<MultiCell>(tiers.Count);
            ValueClass? valueClass = null;

            foreach (var tier in tiers)
            {
                var leaf = tier.Find(path);
                cells.Add(leaf is null
                    ? new MultiCell(tier.TierId, CellState.Missing, null)
                    : new MultiCell(tier.TierId, CellState.Present, leaf));

                // Classification comes from the path, so any tier holding it gives the same answer.
                valueClass ??= leaf?.Class;
            }

            rows.Add(new MultiRow
            {
                Path = path,
                Cells = cells,
                Class = valueClass ?? ValueClass.Business,
            });
        }

        rows.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        return new MultiDiff
        {
            TierIds = tiers.Select(t => t.TierId).ToArray(),
            Rows = rows,
            AppliedAliases = appliedAliases,
        };
    }
}
