using JsonInsight.Model;
using JsonInsight.Sources;
using JsonInsight.Vault;

namespace JsonInsight.Loading;

/// <summary>
/// Turns the tier list in tiers.json into the tier list for one <see cref="ConfigDocument"/>.
///
/// <para>
/// tiers.json describes the root document and only that: which file each environment's appsettings
/// live in, and which secret they came from. Every other document is the same four environments read
/// from a path beneath the same roots, so rather than making tiers.json describe a matrix — one
/// entry per environment per document, kept in step by hand — the other documents are derived from
/// it here.
/// </para>
///
/// <para>
/// One thing changes for a non-root document: its secret is <c>{environment root}/{document}</c>,
/// and its environment root comes from the connection in the Sources tab when tiers.json does not
/// name one. Everything else about a tier — its name, whether it may be written — is the tier's own.
/// </para>
/// </summary>
public static class DocumentTiers
{
    /// <param name="settings">Supplies an environment root for a tier that tiers.json gives no <c>vaultPath</c>.</param>
    public static (TiersConfig Config, List<string> Problems) For(
        TiersConfig tiers,
        VaultSettings settings,
        ConfigDocument document)
    {
        if (document.IsRoot)
        {
            return (tiers, []);
        }

        var problems = new List<string>();
        var derived = new List<TierDefinition>();

        var known = KnownRoots(tiers, settings);

        foreach (var tier in tiers.Tiers)
        {
            // A local-file tier is one file, full stop — there is no path beneath it for a different
            // document to live at, so it has nothing to offer once the comparison has moved off the
            // root. It is reported rather than silently dropped, the same courtesy an unreachable
            // Vault tier gets.
            if (tier.Kind == SourceKind.LocalFile)
            {
                problems.Add(
                    $"{tier.Id} is not in this comparison: it is one local file, and {document.Label} is a " +
                    "different document than the one it holds.");
                continue;
            }

            // Declared, or worked out from the scheme the other tiers follow. A tier whose root is
            // only missing because it says the same thing as its siblings with one segment changed
            // should not have to be told what its siblings already say.
            var root = known.GetValueOrDefault(tier.Id) ?? EnvironmentRoots.Infer(tier.Id, known);

            if (root is null)
            {
                problems.Add(
                    $"{tier.Id} is not in this comparison: nothing says where its secrets live, and " +
                    "the other tiers do not follow one naming scheme this could work it out from. " +
                    "Give it a secret path on the Sources tab — the environment secret itself, e.g. " +
                    $"kv/app/{tier.Id}.");
                continue;
            }

            derived.Add(Derive(tier, root, document));
        }

        return (new TiersConfig { Tiers = derived }, problems);
    }

    /// <summary>
    /// Every environment root that is actually declared. tiers.json wins over a connection, for the
    /// same reason the tier's declaration wins elsewhere: editing a connection should not silently
    /// repoint a column.
    /// </summary>
    public static Dictionary<string, string> KnownRoots(TiersConfig tiers, VaultSettings settings)
    {
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tier in tiers.Tiers)
        {
            var declared = !string.IsNullOrWhiteSpace(tier.VaultPath)
                ? tier.VaultPath
                : settings.Connections.GetValueOrDefault(tier.Id)?.SecretPath;

            if (!string.IsNullOrWhiteSpace(declared))
            {
                roots[tier.Id] = declared!.Trim();
            }
        }

        return roots;
    }

    private static TierDefinition Derive(TierDefinition tier, string environmentRoot, ConfigDocument document) =>
        new()
        {
            Id = tier.Id,
            Label = tier.Label,

            // Every document of every tier goes through the same rails, and the fence that decides
            // what may be written is the tier's own writable flag plus Vault's own permissions.
            Writable = tier.Writable,
            VaultPath = document.PathUnder(environmentRoot),
            Document = document,
            Note = tier.Note,
        };
}
