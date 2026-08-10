using JsonInsight.Loading;
using JsonInsight.Vault;

namespace JsonInsight.Sources;

/// <summary>
/// Turns the Sources tab's settings into the <see cref="TiersConfig"/> the rest of the app compares —
/// the dynamic replacement for hand-editing <c>config/tiers.json</c>.
///
/// <para>
/// Gated on <see cref="VaultSettings.ActiveSources"/> being non-empty, deliberately: that field did
/// not exist before this feature, so it is empty for every installation that has not opened the
/// Sources tab and picked an active set. Until then, <paramref name="seed"/> — <c>tiers.json</c>,
/// loaded exactly as before — stays authoritative, so upgrading this app changes nothing about what
/// is on screen until the new tab is actually used.
/// </para>
/// </summary>
public static class SourceCatalog
{
    /// <summary>How many sources this app compares side by side at once.</summary>
    public const int MaxActive = 4;

    public static (TiersConfig Config, IReadOnlyList<string> Problems) Build(VaultSettings settings, TiersConfig seed)
    {
        if (settings.ActiveSources.Count == 0)
        {
            return (seed, []);
        }

        var problems = new List<string>();

        var requested = settings.ActiveSources
            .Select(id => (Raw: id, Environment: SourceEnvironmentExtensions.ParseId(id)))
            .ToArray();

        foreach (var (raw, environment) in requested.Where(r => r.Environment is null))
        {
            problems.Add($"'{raw}' in Sources:Active is not one of the predefined environment names and was skipped.");
        }

        var resolved = requested
            .Select(r => r.Environment)
            .Where(e => e is not null)
            .Select(e => e!.Value)
            .Distinct()
            .ToList();

        if (resolved.Count > MaxActive)
        {
            problems.Add(
                $"{resolved.Count} sources are active, more than the {MaxActive} this app compares at once — " +
                $"only the first {MaxActive} are shown: {string.Join(", ", resolved.Take(MaxActive).Select(e => e.Id()))}.");
            resolved = resolved.Take(MaxActive).ToList();
        }

        var definitions = new List<TierDefinition>();
        foreach (var environment in resolved)
        {
            if (!settings.Connections.TryGetValue(environment.Id(), out var connection) || !IsConfigured(connection))
            {
                problems.Add($"{environment.Id()} is active but has no source configured on the Sources tab — skipped.");
                continue;
            }

            definitions.Add(ToDefinition(environment, connection));
        }

        return (new TiersConfig { Tiers = definitions }, problems);
    }

    private static bool IsConfigured(VaultConnection connection) => connection.Kind == SourceKind.LocalFile
        ? !string.IsNullOrWhiteSpace(connection.LocalFilePath)
        : !string.IsNullOrWhiteSpace(connection.SecretPath);

    private static TierDefinition ToDefinition(SourceEnvironment environment, VaultConnection connection) => new()
    {
        Id = environment.Id(),
        Label = environment.Label(),

        // Writable is left at its default of true. A configured source is one somebody named on the
        // Sources tab in order to work with it; there is no longer a per-row tick that says otherwise.
        Kind = connection.Kind,
        VaultPath = connection.Kind == SourceKind.Vault ? connection.SecretPath : null,
        LocalFilePath = connection.Kind == SourceKind.LocalFile ? connection.LocalFilePath : null,
    };
}
