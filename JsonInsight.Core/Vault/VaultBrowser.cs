namespace JsonInsight.Vault;

/// <summary>One secret found in Vault, and the address it was found on.</summary>
public sealed record FoundSecret(string Path, string Address);

public sealed record VaultBrowseResult(
    IReadOnlyList<FoundSecret> Secrets,
    IReadOnlyList<string> Problems)
{
    /// <summary>Which tier's connection was read, or null when none could be.</summary>
    public string? Source { get; init; }

    public IReadOnlyList<string> Paths => Secrets
        .Select(s => s.Path)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

/// <summary>
/// Walks one Vault and reports every secret in it, so a path can be picked rather than remembered.
///
/// <para>
/// One environment, not all of them. The tiers hold the same documents under different roots, so
/// walking four servers learns the same layout four times over — three round trips, on three
/// separate connections, to produce a list that was already complete after the first. What a browse
/// finds under the reference environment is therefore offered for every tier, on the same assumption
/// the tiers themselves are built on: a document exists in each of them, at the same relative path.
/// That assumption is exactly what this app exists to check, so where it turns out to be false the
/// grid is the thing that will say so.
/// </para>
///
/// <para>
/// It starts from the mounts the token can see, and falls back to the mounts already named in the
/// configured paths when Vault will not list them — an application token normally cannot read
/// <c>sys/internal/ui/mounts</c>, and that is a permission answer rather than an error worth
/// stopping for.
/// </para>
///
/// <para>
/// Bounded rather than exhaustive, and loud about it. A KV mount can be enormous and this runs while
/// someone waits, so it stops at <see cref="MaxDepth"/> levels and <see cref="MaxListings"/> listings
/// and says so. A silently short list is worse than a slow one: a path missing from the dropdown
/// looks like a path that does not exist.
/// </para>
/// </summary>
public sealed class VaultBrowser
{
    public const int MaxDepth = 8;

    public const int MaxListings = 400;

    private readonly VaultSettings _settings;

    /// <summary>
    /// The tier to read comes from <see cref="VaultSettings.BrowseFrom"/> rather than from a
    /// parameter beside it. Two ways to say the same thing is one too many: the first version of
    /// this took both, and the caller that forgot the parameter silently browsed a different tier
    /// from the one the settings named.
    /// </summary>
    public VaultBrowser(VaultSettings settings) => _settings = settings;

    /// <summary>
    /// Walks one named source's Vault rather than whichever the settings would pick.
    ///
    /// <para>
    /// This is what the per-row Search uses. Each row carries its own address and token now, so "the
    /// Vault" is not one thing to walk — the row that is asking is the only one that can say which
    /// server the answer should come from.
    /// </para>
    /// </summary>
    public Task<VaultBrowseResult> BrowseAsync(
        VaultConnection connection,
        string tierId,
        CancellationToken cancellationToken = default)
    {
        // The row carries its own token; VAULT_TOKEN or ~/.vault-token stands in when it has none,
        // so a Vault reached through `vault login` is searchable without pasting anything into a row.
        connection = connection.WithAmbientToken();

        if (string.IsNullOrWhiteSpace(connection.Address) || string.IsNullOrWhiteSpace(connection.Token))
        {
            return Task.FromResult(new VaultBrowseResult([], [
                $"{tierId} needs an address and a token before it can be searched.",
            ]));
        }

        return BrowseAsync(new Endpoint(connection, tierId), cancellationToken);
    }

    private async Task<VaultBrowseResult> BrowseAsync(Endpoint endpoint, CancellationToken cancellationToken)
    {
        var secrets = new List<FoundSecret>();
        var problems = new List<string>();

        try
        {
            using var client = new VaultClient(endpoint.Connection);
            var mounts = await MountsFor(client, endpoint, problems, cancellationToken).ConfigureAwait(false);

            foreach (var mount in mounts)
            {
                await WalkAsync(client, mount, endpoint, secrets, problems, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            problems.Add($"{endpoint.Connection.Address}: {ex.Message}");
        }

        return new VaultBrowseResult(secrets, problems) { Source = endpoint.TierId };
    }

    private sealed record Endpoint(VaultConnection Connection, string TierId);

    private async Task<IReadOnlyList<string>> MountsFor(
        VaultClient client,
        Endpoint endpoint,
        List<string> problems,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? listed = null;
        try
        {
            listed = await client.ListMountsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            problems.Add($"{endpoint.Connection.Address}: could not list mounts — {ex.Message}");
        }

        if (listed is { Count: > 0 })
        {
            return listed;
        }

        var known = KnownMounts();
        problems.Add(
            $"{endpoint.Connection.Address}: this token cannot list Vault's mounts, so only the ones " +
            $"already in your paths were searched ({string.Join(", ", known)}). A path typed in by hand still works.");

        return known;
    }

    /// <summary>The mounts the configured paths already name — the fallback when Vault will not enumerate them.</summary>
    private IReadOnlyList<string> KnownMounts() =>
        _settings.Connections.Values
            .Select(c => c.SecretPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p =>
            {
                try
                {
                    return VaultClient.ParseMountAndOptionalPath(p).Mount;
                }
                catch
                {
                    return string.Empty;
                }
            })
            .Where(m => m.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static async Task WalkAsync(
        VaultClient client,
        string mount,
        Endpoint endpoint,
        List<FoundSecret> secrets,
        List<string> problems,
        CancellationToken cancellationToken)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((mount, 0));

        var listings = 0;
        var truncated = false;

        while (queue.Count > 0)
        {
            var (path, depth) = queue.Dequeue();

            if (++listings > MaxListings)
            {
                truncated = true;
                break;
            }

            IReadOnlyList<string> keys;
            try
            {
                keys = await client.ListAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A folder this token may not list is not a reason to abandon the rest of the mount.
                problems.Add($"{path}: {ex.Message}");
                continue;
            }

            foreach (var key in keys)
            {
                var isFolder = key.EndsWith('/');
                var child = $"{path}/{(isFolder ? key[..^1] : key)}";

                if (!isFolder)
                {
                    secrets.Add(new FoundSecret(child, endpoint.Connection.Address));
                }

                // A name can be both a secret and a folder, which is exactly how these tiers are
                // laid out - so a secret is queued for listing as well as recorded.
                if (depth + 1 < MaxDepth)
                {
                    queue.Enqueue((child, depth + 1));
                }
                else
                {
                    truncated = true;
                }
            }
        }

        if (truncated)
        {
            problems.Add(
                $"{mount} on {endpoint.Connection.Address}: stopped at {MaxListings} folders or " +
                $"{MaxDepth} levels — the list may be incomplete.");
        }
    }

}
