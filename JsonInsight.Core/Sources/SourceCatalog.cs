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

    /// <summary>
    /// Every source there is to read — one definition per environment that has one configured, ticked
    /// or not, in environment order.
    ///
    /// <para>
    /// Loading and comparing used to be the same list, which made <b>ON</b> mean two things at once:
    /// "compare this" and "read this at all". The second is not a choice worth making — reading a
    /// fifth environment costs one request and makes it available to the Tier editor and the Text
    /// diff without a trip back to the Sources tab to re-tick something. What ON still decides is the
    /// comparison, which is capped because the grid is: see <see cref="Compared"/>.
    /// </para>
    /// </summary>
    public static (TiersConfig Config, IReadOnlyList<string> Problems) Build(VaultSettings settings, TiersConfig seed)
    {
        if (settings.ActiveSources.Count == 0)
        {
            return (seed, []);
        }

        var problems = new List<string>();

        foreach (var raw in settings.ActiveSources.Where(id => SourceEnvironmentExtensions.ParseId(id) is null))
        {
            problems.Add($"'{raw}' in Sources:Active is not one of the predefined environment names and was skipped.");
        }

        // Environment order rather than the order they were ticked, so the columns and the pickers
        // always read dev, test/qa, stage, beta, prod.
        var definitions = new List<TierDefinition>();
        foreach (var environment in SourceEnvironmentExtensions.All)
        {
            if (settings.Connections.TryGetValue(environment.Id(), out var connection) && IsConfigured(connection))
            {
                definitions.Add(ToDefinition(environment, connection));
            }
        }

        // A ticked environment with nothing behind it is the one arrangement that has to be said out
        // loud: it is what stops a Pull entirely, rather than quietly producing a comparison one
        // column short of the one that was asked for. See Incomplete.
        problems.AddRange(Unconfigured(settings).Select(id =>
            $"{id} is ticked ON but has no source configured on the Sources tab."));

        return (new TiersConfig { Tiers = definitions }, problems);
    }

    /// <summary>
    /// Which of the built sources are the columns the All tiers grid compares — the ticked ones, in
    /// environment order, capped at <see cref="MaxActive"/>.
    ///
    /// <para>
    /// Empty when no active set has been saved, meaning "all of them": that is the pre-Sources-tab
    /// arrangement where <c>tiers.json</c> is authoritative and every tier it names is a column.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Compared(VaultSettings settings, TiersConfig built)
    {
        if (settings.ActiveSources.Count == 0)
        {
            return [];
        }

        var ticked = settings.ActiveSources
            .Select(SourceEnvironmentExtensions.ParseId)
            .Where(e => e is not null)
            .Select(e => e!.Value.Id())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return built.Tiers
            .Where(t => ticked.Contains(t.Id))
            .Select(t => t.Id)
            .Take(MaxActive)
            .ToArray();
    }

    /// <summary>
    /// Why nothing may be read yet, or null when it may.
    ///
    /// <para>
    /// One arrangement blocks a read: an environment ticked <b>ON</b> that names no secret and no
    /// file. It used to be skipped with a note, which meant the honest outcome — a comparison missing
    /// the column somebody had just asked for — arrived looking exactly like a successful one three
    /// columns wide. There is no reading around it and no sensible default to substitute, so the
    /// button that would produce that comparison is the thing that goes off.
    /// </para>
    /// </summary>
    public static string? Incomplete(VaultSettings settings)
    {
        var missing = Unconfigured(settings);

        return missing.Count == 0
            ? null
            : $"{string.Join(", ", missing)} {(missing.Count == 1 ? "is" : "are")} ticked ON on the Sources " +
              $"tab but {(missing.Count == 1 ? "names" : "name")} no secret and no file. Configure " +
              $"{(missing.Count == 1 ? "it" : "them")} there, or untick " +
              $"{(missing.Count == 1 ? "it" : "them")} — a comparison one column short of the one you " +
              "asked for is not a comparison worth reading.";
    }

    /// <summary>The ticked environments that have nothing behind them, in environment order.</summary>
    private static IReadOnlyList<string> Unconfigured(VaultSettings settings)
    {
        var ticked = settings.ActiveSources
            .Select(SourceEnvironmentExtensions.ParseId)
            .Where(e => e is not null)
            .Select(e => e!.Value)
            .ToHashSet();

        return SourceEnvironmentExtensions.All
            .Where(ticked.Contains)
            .Where(e => !settings.Connections.TryGetValue(e.Id(), out var connection) || !IsConfigured(connection))
            .Select(e => e.Id())
            .ToArray();
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
