using System.IO;
using System.Text.Json;
using JsonInsight.Model;
using JsonInsight.Promote;

namespace JsonInsight.Diff;

/// <summary>
/// How an alias is compared. One member, deliberately: aliases.json used to document a second,
/// <c>identity</c>, that rewrote a path prefix at load time and diffed normally — it was never
/// built, and a mode that only exists in the note is worse than no mode at all. The enum stays
/// because an alias declaring how it is compared is the shape the config file already has.
/// </summary>
public enum AliasComparison
{
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

    public static AliasSet Empty() => new([]);

    public static AliasSet Load(string? file = null)
    {
        file ??= AppPaths.ConfigFile("aliases.json");
        using var document = JsonDocument.Parse(File.ReadAllText(file), OrdinalJsonWriter.DocumentOptions);

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
                    Comparison = AliasComparison.ShapeOnly,
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
    ///
    /// <para>
    /// The two-tier case of <see cref="ResolveMulti"/> rather than a second copy of the engagement
    /// rules, which is what it was. Whether an alias engages, whether a wildcard may be expanded, and
    /// what <c>memberKeysDiffer</c> means are three questions the Compare-files tab and the All tiers
    /// tab have to answer identically — written twice, one screen eventually hides a difference the
    /// other shows, with nothing failing to say so.
    /// </para>
    ///
    /// <para>
    /// The one thing rebuilt here rather than taken from the <see cref="MultiAlias"/> is the display
    /// path. The N-tier form has no left and right to put in order so it sorts the distinct roots
    /// ordinally; a pair reads in the direction the user chose — <c>Redis / RedisCache</c> comparing
    /// stage to beta, <c>RedisCache / Redis</c> comparing beta to stage.
    /// </para>
    ///
    /// <para>
    /// The two configs are expected to carry different <see cref="FlatConfig.TierId"/>s, as every
    /// caller's do. Everything an alias feeds — <see cref="MultiAlias.RootsByTier"/>, MultiDiff's
    /// cells, the grid's columns — is keyed by tier id, so a pair sharing one is not a comparison
    /// this engine can express in the first place.
    /// </para>
    /// </summary>
    public IReadOnlyList<ResolvedAlias> Resolve(FlatConfig left, FlatConfig right) =>
        ResolveMulti([left, right])
            .Select(alias =>
            {
                var leftRoot = alias.RootsByTier[left.TierId];
                var rightRoot = alias.RootsByTier[right.TierId];

                return new ResolvedAlias(
                    alias.Id,
                    leftRoot,
                    rightRoot,
                    string.Equals(leftRoot, rightRoot, StringComparison.Ordinal)
                        ? leftRoot
                        : $"{leftRoot} / {rightRoot}",
                    alias.Note);
            })
            .ToArray();

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
                    // Aligning two *different* wildcard patterns would be guesswork about which
                    // instantiation pairs with which. Refuse rather than guess.
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
                // A tier with nothing at all under the root is genuinely missing the concept. That
                // is an ordinary "only in" finding and must stay visible, not be softened into
                // "shapes differ" — so the alias is abandoned for every tier, not just this one.
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

    /// <summary>Every concrete path in any of the tiers that a wildcard root pattern matches.</summary>
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
}
