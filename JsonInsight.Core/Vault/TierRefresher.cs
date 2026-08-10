using JsonInsight.Loading;
using JsonInsight.Model;
using JsonInsight.Sources;

namespace JsonInsight.Vault;

/// <summary>One tier's line in a read report. Every tier gets one, including the ones that failed.</summary>
public sealed record TierRefreshLine(string TierId, TierRefreshOutcome Outcome, string Message)
{
    public string Text => $"{TierId,-6} {Message}";
}

public enum TierRefreshOutcome
{
    /// <summary>Read from Vault; the grid is showing live values.</summary>
    Refreshed,

    /// <summary>Vault could not be read. The tier has no values at all — there is no local copy.</summary>
    Unavailable,

    /// <summary>The tier names no secret, or has no connection that could reach one.</summary>
    NotConfigured,
}

/// <summary>
/// A tier that is configured but could not be read, kept so its column can say so.
///
/// <para>
/// It carries the definition rather than just the id because the grid still shows the tier: dropping
/// it out of the comparison entirely would turn "I could not read beta" into "there is no beta",
/// which is the wrong sentence at exactly the moment it matters.
/// </para>
/// </summary>
public sealed record TierUnavailable(TierDefinition Definition, string Reason)
{
    public string Id => Definition.Id;
}

public sealed record TierRefreshReport(
    IReadOnlyList<TierDocument> Documents,
    IReadOnlyList<TierUnavailable> Unavailable,
    IReadOnlyList<TierRefreshLine> Lines)
{
    public int RefreshedCount => Lines.Count(l => l.Outcome == TierRefreshOutcome.Refreshed);

    public int UnavailableCount => Unavailable.Count;

    public IEnumerable<TierRefreshLine> Failures =>
        Lines.Where(l => l.Outcome is TierRefreshOutcome.Unavailable or TierRefreshOutcome.NotConfigured);

    /// <summary>The one-line status: what happened, in the order that matters if you only read the first clause.</summary>
    public string Summary
    {
        get
        {
            if (Lines.Count == 0)
            {
                return "No tiers are configured.";
            }

            var parts = new List<string>();
            if (UnavailableCount > 0)
            {
                parts.Add($"{UnavailableCount} unavailable");
            }

            parts.Add($"{RefreshedCount} read from Vault");
            return string.Join(", ", parts) + ".";
        }
    }
}

/// <summary>
/// Reads every configured tier in one pass, dispatching each to the <see cref="ISourceProvider"/> its
/// <see cref="TierDefinition.Kind"/> names — the registry in <see cref="SourceProviders"/>, the same
/// one the push flow resolves through. A tier that names a kind with no provider is reported the way
/// an unreachable Vault tier is, rather than throwing, so a partially-configured catalog still opens.
/// </summary>
public sealed class TierRefresher
{
    private readonly IReadOnlyDictionary<SourceKind, ISourceProvider> _providers;

    public TierRefresher(Flattener flattener)
        : this(SourceProviders.Default(flattener).Values)
    {
    }

    public TierRefresher(IEnumerable<ISourceProvider> providers)
    {
        _providers = providers.ToDictionary(p => p.Kind);
    }

    public async Task<TierRefreshReport> RefreshAsync(
        TiersConfig tiers,
        VaultSettings settings,
        CancellationToken cancellationToken = default)
    {
        var documents = new List<TierDocument>();
        var unavailable = new List<TierUnavailable>();
        var lines = new List<TierRefreshLine>();

        foreach (var definition in tiers.Tiers)
        {
            if (!_providers.TryGetValue(definition.Kind, out var provider))
            {
                unavailable.Add(new TierUnavailable(definition, $"no provider registered for {definition.Kind}"));
                lines.Add(new TierRefreshLine(definition.Id, TierRefreshOutcome.NotConfigured,
                    $"not configured — no provider registered for {definition.Kind}."));
                continue;
            }

            var result = await provider.LoadAsync(definition, settings, cancellationToken).ConfigureAwait(false);

            if (result.Succeeded)
            {
                documents.Add(result.Document!);
                lines.Add(new TierRefreshLine(definition.Id, TierRefreshOutcome.Refreshed,
                    $"{result.Detail ?? Describe(definition.Kind)}, {result.Document!.Flat.Count} keys."));
                continue;
            }

            unavailable.Add(new TierUnavailable(definition, result.Problem!));
            lines.Add(new TierRefreshLine(
                definition.Id,
                result.NotConfigured ? TierRefreshOutcome.NotConfigured : TierRefreshOutcome.Unavailable,
                result.NotConfigured
                    ? $"not configured — {result.Problem}."
                    : $"unavailable — {result.Problem}"));
        }

        return new TierRefreshReport(documents, unavailable, lines);
    }

    private static string Describe(SourceKind kind) => kind switch
    {
        SourceKind.Vault => "Vault",
        SourceKind.LocalFile => "local file",
        _ => kind.ToString(),
    };
}
