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

/// <summary>
/// What this app calls a JSON type when it says so in prose — "number vs boolean" on a type-difference
/// row, "written as boolean while other tiers hold it as …" in an edit warning.
///
/// <para>
/// One list because it was two, and the two had drifted: the differ said <c>bool</c> and the edit
/// validator said <c>boolean</c>, so the same value was named differently depending on which screen
/// reported it. <c>boolean</c> is the survivor because the Edit dialog's own kind picker already
/// offers that word — <c>EditVm.KindOptions</c> reads string / number / boolean / null — and a dialog
/// whose picker and whose warning disagree about what to call a value is the worse of the two
/// inconsistencies to leave standing.
/// </para>
///
/// <para>
/// True and False share one word deliberately. They are two <see cref="JsonValueKind"/> members for a
/// single JSON type, and naming them apart would describe a flag turned off somewhere as a change of
/// type — the same mistake <c>DiffEntry.SameJsonType</c> exists to avoid.
/// </para>
/// </summary>
public static class JsonKinds
{
    public static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        _ => kind.ToString().ToLowerInvariant(),
    };
}
