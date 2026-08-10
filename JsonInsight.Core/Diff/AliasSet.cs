using System.IO;
using System.Text.Json;
using JsonInsight.Model;

namespace JsonInsight.Diff;

public enum AliasComparison
{
    /// <summary>Same shape under a different name: rewrite the path and diff normally.</summary>
    Identity,

    /// <summary>Equivalent in purpose but structurally different: report once, do not pretend to compare.</summary>
    ShapeOnly,
}

public sealed class AliasDefinition
{
    public required string Id { get; init; }

    public required AliasComparison Comparison { get; init; }

    /// <summary>Tier id to the path (or path pattern) that tier uses for this concept.</summary>
    public required IReadOnlyDictionary<string, string> Members { get; init; }

    /// <summary>
    /// When "memberKeysDiffer", the alias only engages if the two tiers hold different child keys
    /// under the root. That keeps a genuinely-added scope visible instead of hiding it behind a
    /// blanket "shapes differ".
    /// </summary>
    public string? When { get; init; }

    public string? Note { get; init; }

    public string? PatternFor(string tierId) =>
        Members.TryGetValue(tierId, out var pattern) ? pattern : null;
}

/// <summary>One alias applied to one concrete pair of tiers.</summary>
public sealed record ResolvedAlias(
    string Id,
    string LeftRoot,
    string RightRoot,
    string DisplayPath,
    string? Note);

/// <summary>One alias applied across every tier in the side-by-side view.</summary>
public sealed record MultiAlias(
    string Id,
    IReadOnlyDictionary<string, string> RootsByTier,
    string DisplayPath,
    string? Note,
    IReadOnlyDictionary<string, int> LeafCountsByTier);

public sealed class AliasSet
{
    private readonly List<AliasDefinition> _aliases;

    private AliasSet(List<AliasDefinition> aliases) => _aliases = aliases;

    public IReadOnlyList<AliasDefinition> Definitions => _aliases;

    public static AliasSet Empty() => new([]);

    public static AliasSet Load(string? file = null)
    {
        file ??= AppPaths.ConfigFile("aliases.json");
        using var document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        var aliases = new List<AliasDefinition>();
        if (document.RootElement.TryGetProperty("aliases", out var list))
        {
            foreach (var element in list.EnumerateArray())
            {
                var members = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var member in element.GetProperty("members").EnumerateObject())
                {
                    members[member.Name] = member.Value.GetString() ?? string.Empty;
                }

                aliases.Add(new AliasDefinition
                {
                    Id = element.GetProperty("id").GetString() ?? "(unnamed)",
                    Comparison = element.TryGetProperty("comparison", out var c) &&
                                 string.Equals(c.GetString(), "identity", StringComparison.OrdinalIgnoreCase)
                        ? AliasComparison.Identity
                        : AliasComparison.ShapeOnly,
                    Members = members,
                    When = element.TryGetProperty("when", out var w) ? w.GetString() : null,
                    Note = element.TryGetProperty("note", out var n) ? n.GetString() : null,
                });
            }
        }

        return new AliasSet(aliases);
    }

    /// <summary>
    /// Works out which aliases actually apply to a given tier pair, expanding any wildcard in the
    /// member patterns against the paths those tiers really contain.
    /// </summary>
    public IReadOnlyList<ResolvedAlias> Resolve(FlatConfig left, FlatConfig right)
    {
        var resolved = new List<ResolvedAlias>();

        foreach (var alias in _aliases.Where(a => a.Comparison == AliasComparison.ShapeOnly))
        {
            var leftPattern = alias.PatternFor(left.TierId);
            var rightPattern = alias.PatternFor(right.TierId);
            if (leftPattern is null || rightPattern is null)
            {
                continue;
            }

            var wildcarded = leftPattern.Contains('*') || rightPattern.Contains('*');
            if (!wildcarded)
            {
                TryAdd(alias, leftPattern, rightPattern, left, right, resolved);
                continue;
            }

            if (!string.Equals(leftPattern, rightPattern, StringComparison.Ordinal))
            {
                // Aligning two *different* wildcard patterns would be guesswork about which
                // instantiation pairs with which. Refuse rather than guess.
                continue;
            }

            foreach (var root in ExpandRoots(leftPattern, left, right))
            {
                TryAdd(alias, root, root, left, right, resolved);
            }
        }

        return resolved;
    }

    private static void TryAdd(
        AliasDefinition alias,
        string leftRoot,
        string rightRoot,
        FlatConfig left,
        FlatConfig right,
        List<ResolvedAlias> resolved)
    {
        var leftLeaves = left.Subtree(leftRoot).ToArray();
        var rightLeaves = right.Subtree(rightRoot).ToArray();

        // If one side has nothing at all, the concept is genuinely missing from that tier. That is
        // an ordinary "only in" finding and must stay visible, not be softened into "shapes differ".
        if (leftLeaves.Length == 0 || rightLeaves.Length == 0)
        {
            return;
        }

        var engages = alias.When switch
        {
            "memberKeysDiffer" => !ChildKeys(leftLeaves, leftRoot).SetEquals(ChildKeys(rightLeaves, rightRoot)),
            _ => !string.Equals(leftRoot, rightRoot, StringComparison.Ordinal),
        };

        if (!engages)
        {
            return;
        }

        var display = string.Equals(leftRoot, rightRoot, StringComparison.Ordinal)
            ? leftRoot
            : $"{leftRoot} / {rightRoot}";

        resolved.Add(new ResolvedAlias(alias.Id, leftRoot, rightRoot, display, alias.Note));
    }

    /// <summary>
    /// The N-tier form used by the side-by-side view. An alias engages only when every tier
    /// actually holds the concept - if one tier lacks it entirely that is a plain "missing" finding
    /// and must not be softened into "shapes differ".
    /// </summary>
    public IReadOnlyList<MultiAlias> ResolveMulti(IReadOnlyList<FlatConfig> tiers)
    {
        var resolved = new List<MultiAlias>();
        if (tiers.Count < 2)
        {
            return resolved;
        }

        foreach (var alias in _aliases.Where(a => a.Comparison == AliasComparison.ShapeOnly))
        {
            var patterns = new Dictionary<string, string>(StringComparer.Ordinal);
            var coversAll = true;
            foreach (var tier in tiers)
            {
                var pattern = alias.PatternFor(tier.TierId);
                if (pattern is null)
                {
                    coversAll = false;
                    break;
                }

                patterns[tier.TierId] = pattern;
            }

            if (!coversAll)
            {
                continue;
            }

            if (patterns.Values.Any(p => p.Contains('*')))
            {
                if (patterns.Values.Distinct(StringComparer.Ordinal).Count() != 1)
                {
                    continue;
                }

                foreach (var root in ExpandRootsAcross(patterns.Values.First(), tiers))
                {
                    TryAddMulti(alias, tiers.ToDictionary(t => t.TierId, _ => root, StringComparer.Ordinal), tiers, resolved);
                }
            }
            else
            {
                TryAddMulti(alias, patterns, tiers, resolved);
            }
        }

        return resolved;
    }

    private static void TryAddMulti(
        AliasDefinition alias,
        IReadOnlyDictionary<string, string> roots,
        IReadOnlyList<FlatConfig> tiers,
        List<MultiAlias> resolved)
    {
        var leavesByTier = new Dictionary<string, Leaf[]>(StringComparer.Ordinal);
        foreach (var tier in tiers)
        {
            var leaves = tier.Subtree(roots[tier.TierId]).ToArray();
            if (leaves.Length == 0)
            {
                return;
            }

            leavesByTier[tier.TierId] = leaves;
        }

        var engages = alias.When switch
        {
            "memberKeysDiffer" => tiers
                .Select(t => ChildKeys(leavesByTier[t.TierId], roots[t.TierId]))
                .Distinct(HashSet<string>.CreateSetComparer())
                .Count() > 1,
            _ => roots.Values.Distinct(StringComparer.Ordinal).Count() > 1,
        };

        if (!engages)
        {
            return;
        }

        var distinctRoots = roots.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var display = distinctRoots.Length == 1 ? distinctRoots[0] : string.Join(" / ", distinctRoots);

        resolved.Add(new MultiAlias(alias.Id, roots, display, alias.Note,
            tiers.ToDictionary(t => t.TierId, t => leavesByTier[t.TierId].Length, StringComparer.Ordinal)));
    }

    private static IEnumerable<string> ExpandRootsAcross(string pattern, IReadOnlyList<FlatConfig> tiers)
    {
        var depth = pattern.Split(':').Length;
        var roots = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in tiers.SelectMany(t => t.Paths))
        {
            var segments = ConfigPath.Split(path);
            if (segments.Length < depth)
            {
                continue;
            }

            var candidate = string.Join(':', segments.Take(depth));
            if (PathGlob.IsMatch(candidate, pattern))
            {
                roots.Add(candidate);
            }
        }

        return roots.Order(StringComparer.Ordinal);
    }

    private static HashSet<string> ChildKeys(IEnumerable<Leaf> leaves, string root)
    {
        var prefix = root + ":";
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var leaf in leaves)
        {
            if (!leaf.Path.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = leaf.Path[prefix.Length..];
            var end = rest.IndexOf(':');
            keys.Add(end < 0 ? rest : rest[..end]);
        }

        return keys;
    }

    /// <summary>Every concrete path in either tier that a wildcard root pattern matches.</summary>
    private static IEnumerable<string> ExpandRoots(string pattern, FlatConfig left, FlatConfig right)
    {
        var depth = pattern.Split(':').Length;
        var roots = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in left.Paths.Concat(right.Paths))
        {
            var segments = ConfigPath.Split(path);
            if (segments.Length < depth)
            {
                continue;
            }

            var candidate = string.Join(':', segments.Take(depth));
            if (PathGlob.IsMatch(candidate, pattern))
            {
                roots.Add(candidate);
            }
        }

        return roots.Order(StringComparer.Ordinal);
    }
}
