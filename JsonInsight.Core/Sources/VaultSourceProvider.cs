using System.Text.Json.Nodes;
using JsonInsight.Loading;
using JsonInsight.Model;
using JsonInsight.Vault;

namespace JsonInsight.Sources;

/// <summary>
/// <see cref="ISourceProvider"/> for <see cref="SourceKind.Vault"/>. Wraps <see cref="VaultTierLoader"/>
/// and <see cref="VaultPusher"/> rather than reimplementing either — every fence, every read-back
/// check and every bit of Vault-specific behavior stays exactly where it already lived and already had
/// its own tests. This class only adapts the two of them to one interface.
/// </summary>
public sealed class VaultSourceProvider : ISourceProvider
{
    private readonly VaultTierLoader _loader;
    private readonly VaultPusher _pusher;

    public VaultSourceProvider(Flattener flattener)
    {
        _loader = new VaultTierLoader(flattener);
        _pusher = new VaultPusher(flattener);
    }

    public SourceKind Kind => SourceKind.Vault;

    public async Task<SourceLoadResult> LoadAsync(
        TierDefinition definition,
        VaultSettings settings,
        CancellationToken cancellationToken = default)
    {
        var result = await _loader.LoadAsync(definition, settings, cancellationToken).ConfigureAwait(false);
        return new SourceLoadResult(result.TierId, result.Document, result.Problem)
        {
            NotConfigured = result.NotConfigured,
            Detail = result.Pulled is { } pulled ? $"Vault v{pulled.Version}" : null,
        };
    }

    public string? Blocked(TierDocument? document, VaultSettings settings) =>
        VaultPusher.Blocked(document, settings);

    public Task<PushPreflight> PreflightSaveAsync(
        TierDocument document,
        JsonNode updated,
        string what,
        VaultSettings settings,
        CancellationToken cancellationToken = default) =>
        _pusher.PreflightAsync(document, updated, what, settings, cancellationToken);

    public Task<PushResult> SaveAsync(
        TierDocument document,
        PushPlan plan,
        VaultSettings settings,
        CancellationToken cancellationToken = default) =>
        _pusher.PushAsync(document, plan, settings, cancellationToken);
}
