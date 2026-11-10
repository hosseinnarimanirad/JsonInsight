using System.Text.Json.Nodes;
using JsonInsight.Loading;
using JsonInsight.Model;
using JsonInsight.Promote;
using JsonInsight.Vault;

namespace JsonInsight.History;

/// <summary>
/// One earlier version of a secret, read for looking at and nothing else.
///
/// <para>
/// Deliberately not a <see cref="TierDocument"/>. A tier is a source — it is what the grid compares,
/// what an edit lands on and what a push is built from — and an old version is none of those. Giving
/// it its own type means nothing written for pushing can be handed one by mistake.
/// </para>
///
/// <para>
/// It lives only as long as the view that asked for it. Nothing here is cached or written anywhere:
/// opening history again reads Vault again.
/// </para>
/// </summary>
public sealed record SecretVersion(
    int Version,
    DateTimeOffset? CreatedTime,
    JsonNode Root,
    FlatConfig Flat,
    string SecretPath,
    string Address);

/// <summary>What a read of several versions produced: the versions read, and the ones that were not.</summary>
public sealed record SecretVersions(
    IReadOnlyList<SecretVersion> Read,
    IReadOnlyList<(int Version, string Reason)> Skipped);

/// <summary>
/// Reads a Vault tier's version history, on demand and read-only.
///
/// <para>
/// Every method here is a query. There is no write in this class and no path from it to one: getting
/// an old value back is an ordinary edit on the current document, pushed through the usual window
/// with its check-and-set and typed confirmation.
/// </para>
/// </summary>
public sealed class SecretHistory
{
    private readonly Flattener _flattener;

    public SecretHistory(Flattener flattener)
    {
        _flattener = flattener;
    }

    /// <summary>Why history cannot be read for this tier, or null when it can.</summary>
    public static string? Unavailable(TierDocument? tier, VaultSettings settings)
    {
        if (tier is null)
        {
            return "Nothing is loaded.";
        }

        if (tier.Origin != TierOrigin.Vault)
        {
            return "Files have no version history.";
        }

        if (string.IsNullOrWhiteSpace(SecretPathOf(tier)))
        {
            return $"{tier.Label} names no Vault secret.";
        }

        var missing = settings.Unreachable(tier.Id);
        return missing.Count > 0
            ? $"The Vault connection for {tier.Label} is incomplete — missing {string.Join(", ", missing)}."
            : null;
    }

    /// <summary>The version list, newest first.</summary>
    public async Task<VaultSecretMetadata> ListAsync(
        TierDocument tier,
        VaultSettings settings,
        CancellationToken cancellationToken = default)
    {
        using var client = Client(tier, settings);
        return await client.ReadMetadataAsync(SecretPathOf(tier)!, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One version, read now.</summary>
    public async Task<SecretVersion> ReadAsync(
        TierDocument tier,
        VaultSettings settings,
        int version,
        CancellationToken cancellationToken = default)
    {
        using var client = Client(tier, settings);
        return await ReadOneAsync(client, tier, version, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Every readable version in <paramref name="metadata"/>, newest first, over one connection.
    ///
    /// <para>
    /// Deleted and destroyed versions are skipped with their reason rather than requested — Vault
    /// would only answer 404 for them. A version that fails to read is skipped with Vault's answer,
    /// so one bad version does not cost the rest of the history. Cancellation is not swallowed.
    /// </para>
    /// </summary>
    public async Task<SecretVersions> ReadAllAsync(
        TierDocument tier,
        VaultSettings settings,
        VaultSecretMetadata metadata,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.Now;
        var read = new List<SecretVersion>();
        var skipped = new List<(int, string)>();

        var wanted = metadata.Versions.Where(v => v.Readable(now)).ToArray();
        skipped.AddRange(metadata.Versions
            .Where(v => !v.Readable(now))
            .Select(v => (v.Version, v.Unreadable(now)!)));

        using var client = Client(tier, settings);

        for (var i = 0; i < wanted.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report((i, wanted.Length));

            try
            {
                read.Add(await ReadOneAsync(client, tier, wanted[i].Version, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                skipped.Add((wanted[i].Version, ex.Message));
            }
        }

        progress?.Report((wanted.Length, wanted.Length));
        skipped.Sort((a, b) => b.Item1.CompareTo(a.Item1));

        return new SecretVersions(read, skipped);
    }

    private async Task<SecretVersion> ReadOneAsync(
        VaultClient client,
        TierDocument tier,
        int version,
        CancellationToken cancellationToken)
    {
        var pulled = await client
            .ReadVersionAsync(SecretPathOf(tier)!, version, cancellationToken)
            .ConfigureAwait(false);

        var root = OrdinalJsonWriter.Parse(pulled.Json);

        // Flattened through the same rules as the tier itself, so array paths and set comparison mean
        // the same thing in a history as they do on the grid.
        return new SecretVersion(
            pulled.Version,
            pulled.CreatedTime,
            root,
            _flattener.Flatten($"{tier.Id}@v{pulled.Version}", root),
            pulled.SecretPath,
            pulled.Address);
    }

    /// <summary>The secret the tier was read from — the same one its push would write.</summary>
    private static string? SecretPathOf(TierDocument tier) =>
        string.IsNullOrWhiteSpace(tier.VaultSecretPath) ? tier.Definition.VaultPath : tier.VaultSecretPath;

    private static VaultClient Client(TierDocument tier, VaultSettings settings)
    {
        if (Unavailable(tier, settings) is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        return new VaultClient(settings.Resolve(tier.Id));
    }
}
