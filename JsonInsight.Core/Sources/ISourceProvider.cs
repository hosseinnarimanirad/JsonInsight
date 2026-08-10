using System.Text.Json.Nodes;
using JsonInsight.Loading;
using JsonInsight.Model;
using JsonInsight.Vault;

namespace JsonInsight.Sources;

/// <summary>What a single source's read produced, or why it did not — provider-agnostic.</summary>
public sealed record SourceLoadResult(string TierId, TierDocument? Document, string? Problem)
{
    public bool Succeeded => Document is not null;

    /// <summary>True when the source simply is not configured, as opposed to one that failed to read.</summary>
    public bool NotConfigured { get; init; }

    /// <summary>
    /// One kind-specific clause for the refresh line — "Vault v34" for a Vault read, "modified
    /// 2026-08-06 14:02" for a local file. Null falls back to <see cref="SourceKind"/>'s own name.
    /// </summary>
    public string? Detail { get; init; }
}

/// <summary>
/// Reads and writes one <see cref="SourceKind"/> of source. Every tier reads through whichever
/// provider its <see cref="TierDefinition.Kind"/> names, so the diff, edit and promote code — which
/// only ever sees a <see cref="TierDocument"/> — never has to know whether it came from Vault or a
/// local file.
///
/// <para>
/// <see cref="Blocked"/>, <see cref="PreflightSaveAsync"/> and <see cref="SaveAsync"/> mirror
/// <c>VaultPusher</c>'s three-step write: decide up front whether a write may even be attempted,
/// build a plan against the source's current state, then send it. Every implementation is expected to
/// carry the same shape of fence <c>VaultPusher</c> does — re-parse and re-flatten the payload before
/// it leaves, compare against what the source holds right now rather than what was last read, and
/// verify after writing — even though what "compare against what it holds now" and "verify" mean is
/// different for a Vault check-and-set than for a file on disk.
/// </para>
/// </summary>
public interface ISourceProvider
{
    SourceKind Kind { get; }

    Task<SourceLoadResult> LoadAsync(
        TierDefinition definition,
        VaultSettings settings,
        CancellationToken cancellationToken = default);

    /// <summary>Why this tier could not be saved at all, or null when a save could be attempted.</summary>
    string? Blocked(TierDocument? document, VaultSettings settings);

    Task<PushPreflight> PreflightSaveAsync(
        TierDocument document,
        JsonNode updated,
        string what,
        VaultSettings settings,
        CancellationToken cancellationToken = default);

    Task<PushResult> SaveAsync(
        TierDocument document,
        PushPlan plan,
        VaultSettings settings,
        CancellationToken cancellationToken = default);
}
