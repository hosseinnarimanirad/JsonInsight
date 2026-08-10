namespace JsonInsight.Sources;

/// <summary>
/// The predefined environment names a source is picked from. Closed on purpose: it
/// is what makes a source's id something you choose from a list rather than type, and what makes "one
/// source per environment" a real constraint rather than a convention nobody enforces.
///
/// <para>
/// Extending this list is a one-line, deliberate change here — not a per-deployment config edit —
/// because every place that persists a source keys it by <see cref="SourceEnvironmentExtensions.Id"/>,
/// and a typo'd id would silently create a second, orphaned row rather than editing the one you meant.
/// </para>
/// </summary>
public enum SourceEnvironment
{
    Dev,
    TestQa,
    Stage,
    Beta,
    Prod,
}

public static class SourceEnvironmentExtensions
{
    /// <summary>Canonical order — also the order tiers.json historically listed dev/stage/beta/prod in.</summary>
    public static readonly IReadOnlyList<SourceEnvironment> All =
    [
        SourceEnvironment.Dev,
        SourceEnvironment.TestQa,
        SourceEnvironment.Stage,
        SourceEnvironment.Beta,
        SourceEnvironment.Prod,
    ];

    /// <summary>The stable, lowercase key this environment persists and compares under.</summary>
    public static string Id(this SourceEnvironment environment) => environment switch
    {
        SourceEnvironment.Dev => "dev",
        SourceEnvironment.TestQa => "test-qa",
        SourceEnvironment.Stage => "stage",
        SourceEnvironment.Beta => "beta",
        SourceEnvironment.Prod => "prod",
        _ => throw new ArgumentOutOfRangeException(nameof(environment)),
    };

    /// <summary>What to show on screen — "test/qa" rather than "test-qa", which is an id, not a label.</summary>
    public static string Label(this SourceEnvironment environment) => environment switch
    {
        SourceEnvironment.TestQa => "test/qa",
        _ => environment.Id(),
    };

    /// <summary>Parses an id back to its environment, tolerant of the id/label spelling difference.</summary>
    public static SourceEnvironment? ParseId(string? id) => id?.Trim().ToLowerInvariant() switch
    {
        "dev" => SourceEnvironment.Dev,
        "test-qa" or "test/qa" or "testqa" => SourceEnvironment.TestQa,
        "stage" => SourceEnvironment.Stage,
        "beta" => SourceEnvironment.Beta,
        "prod" => SourceEnvironment.Prod,
        _ => null,
    };
}
