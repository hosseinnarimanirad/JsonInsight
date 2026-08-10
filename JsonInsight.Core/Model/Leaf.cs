using System.Text.Json;

namespace JsonInsight.Model;

/// <summary>
/// How a value is expected to behave across environments. Drives the promote defaults and what the
/// grid counts as drift: without it, every URL and credential separating two deployments would be
/// reported as a finding.
/// </summary>
public enum ValueClass
{
    /// <summary>Expected to be identical everywhere. A mismatch is a real finding.</summary>
    Business,

    /// <summary>Deployment-specific by nature (urls, hosts, ports). A mismatch is expected.</summary>
    Infra,

    /// <summary>Never rendered, never logged, never copied verbatim on promote.</summary>
    Secret,
}

/// <summary>A single scalar (or set) leaf of a configuration tree, keyed by its canonical path.</summary>
public sealed record Leaf
{
    /// <summary>Canonical path, e.g. <c>Serilog:WriteTo[Name=Seq]:serverUrl</c>.</summary>
    public required string Path { get; init; }

    /// <summary>
    /// The form the ASP.NET configuration binder sees, e.g. <c>Serilog:WriteTo:2:serverUrl</c>.
    /// Kept so a canonical path can always be correlated back to runtime behaviour.
    /// </summary>
    public required string ConfigurationKey { get; init; }

    /// <summary>Raw value. For strings this is the unescaped text; for numbers/bools the literal token.</summary>
    public required string Value { get; init; }

    public required JsonValueKind Kind { get; init; }

    public required string TierId { get; init; }

    public ValueClass Class { get; init; } = ValueClass.Business;

    /// <summary>
    /// Set for leaves produced by the <c>stringSet</c> array strategy. Ordinal-sorted for comparison
    /// only — the underlying array is never re-sorted when the document is written back.
    /// </summary>
    public IReadOnlyList<string>? SetMembers { get; init; }

    public bool IsSet => SetMembers is not null;

    /// <summary>Value rendered for comparison. Sets collapse to a canonical ordered form.</summary>
    public string ComparableValue =>
        SetMembers is null ? Value : "{" + string.Join(", ", SetMembers) + "}";

    /// <summary>Value rendered for display. Secrets never reveal their content.</summary>
    public string DisplayValue => Class == ValueClass.Secret
        ? SecretPlaceholder
        : ComparableValue;

    public const string SecretPlaceholder = "••••••";
}
