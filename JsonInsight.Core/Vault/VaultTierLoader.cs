using JsonInsight.Loading;
using JsonInsight.Model;
using JsonInsight.Promote;

namespace JsonInsight.Vault;

/// <summary>What a single tier's Vault read produced, or why it did not.</summary>
public sealed record VaultTierResult(
    string TierId,
    TierDocument? Document,
    VaultReadResult? Pulled,
    string? Problem)
{
    public bool Succeeded => Document is not null;

    /// <summary>True when the tier simply has no Vault connection, as opposed to one that failed.</summary>
    public bool NotConfigured { get; init; }
}

/// <summary>
/// Reads a tier out of Vault, which is the only place a tier comes from.
///
/// <para>
/// A tier that cannot be read is reported and left out, never substituted. There is nothing to
/// substitute: the local snapshots this app used to keep are gone, precisely so that a value on
/// screen cannot be some other day's answer wearing the current one's label.
/// </para>
/// </summary>
public sealed class VaultTierLoader
{
    private readonly Flattener _flattener;

    public VaultTierLoader(Flattener flattener)
    {
        _flattener = flattener;
    }

    /// <summary>
    /// True when this tier is configured to be read at all — which is a question about the tier's own
    /// declaration, not about whether a connection row happens to exist for it. A tier that knows its
    /// path and can borrow the default address and token is Vault-backed.
    /// </summary>
    public static bool IsVaultBacked(TierDefinition definition, VaultSettings settings) =>
        !string.IsNullOrWhiteSpace(definition.VaultPath) &&
        settings.Unreachable(definition.Id).Count == 0;

    public async Task<VaultTierResult> LoadAsync(
        TierDefinition definition,
        VaultSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(definition.VaultPath))
        {
            return new VaultTierResult(definition.Id, null, null,
                "no vaultPath in tiers.json") { NotConfigured = true };
        }

        // Reachability only: the path comes from the definition below, so a connection row without
        // one of its own is not a reason to refuse a read whose path was never in question.
        var missing = settings.Unreachable(definition.Id);
        if (missing.Count > 0)
        {
            return new VaultTierResult(definition.Id, null, null,
                $"Vault connection is incomplete — missing {string.Join(", ", missing)}")
            {
                NotConfigured = true,
            };
        }

        var connection = settings.Resolve(definition.Id);

        // tiers.json is the authority on which secret a tier maps to; the Sources tab's SecretPath is
        // the connection detail. They should agree, and when they do not the tier's own declaration
        // wins - otherwise editing a connection could silently repoint a whole column.
        var secretPath = definition.VaultPath!;

        try
        {
            using var client = new VaultClient(connection);
            var pulled = await client.ReadAsync(secretPath, cancellationToken).ConfigureAwait(false);

            return new VaultTierResult(definition.Id, Build(definition, pulled), pulled, null);
        }
        catch (OperationCanceledException)
        {
            return new VaultTierResult(definition.Id, null, null,
                $"timed out after {VaultClient.Timeout.TotalSeconds:0}s");
        }
        catch (Exception ex)
        {
            return new VaultTierResult(definition.Id, null, null, ex.Message);
        }
    }

    /// <summary>Builds the in-memory tier from a payload.</summary>
    public TierDocument Build(TierDefinition definition, VaultReadResult pulled)
    {
        var root = OrdinalJsonWriter.Parse(pulled.Json);

        return new TierDocument
        {
            Definition = definition,
            Root = root,
            Flat = _flattener.Flatten(definition.Id, root),
            Origin = TierOrigin.Vault,
            VaultVersion = pulled.Version,
            VaultCreatedTime = pulled.CreatedTime,
            VaultAddress = pulled.Address,
            VaultSecretPath = pulled.SecretPath,
        };
    }
}
