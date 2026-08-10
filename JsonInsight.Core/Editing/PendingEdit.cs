using System.Text.Json;
using JsonInsight.Classify;
using JsonInsight.Model;

namespace JsonInsight.Editing;

public enum EditKind
{
    /// <summary>The key exists in this tier and its value changes.</summary>
    Update,

    /// <summary>The key does not exist in this tier and is created.</summary>
    Add,

    /// <summary>The key exists in this tier and is removed.</summary>
    Delete,
}

/// <summary>
/// One queued change to one key in one tier.
///
/// <para>
/// An edit records the value it was made against as well as the value it sets. That base is what
/// makes a batched change set safe to hold across a Vault refresh or an F5: when the underlying
/// document moves, an edit whose base no longer matches is stale, and committing it would silently
/// discard whatever the other change was. <see cref="IsStaleAgainst"/> is the check, and the commit
/// dialog refuses to write until every stale edit has been re-based or dropped.
/// </para>
/// </summary>
public sealed record PendingEdit
{
    public required string TierId { get; init; }

    /// <summary>Canonical configuration path, e.g. <c>ConnectionStrings:Couchbase:Modules:Auth:Url</c>.</summary>
    public required string Path { get; init; }

    public required EditKind Kind { get; init; }

    /// <summary>
    /// The value this edit was authored against. Null for <see cref="EditKind.Add"/>, where the
    /// precondition is that the key is absent rather than that it holds a particular value.
    /// </summary>
    public string? BaseValue { get; init; }

    /// <summary>The literal text to write. Null for <see cref="EditKind.Delete"/>.</summary>
    public string? NewValue { get; init; }

    /// <summary>
    /// The JSON type to write it as. These files carry ints (KvTimeoutSeconds: 10) and bools
    /// (UseNetworkResolutionExternal: false) alongside strings, and writing "10" where the siblings
    /// hold 10 produces a file that is inconsistent even where the binder happens to coerce it.
    /// </summary>
    public JsonValueKind NewKind { get; init; } = JsonValueKind.String;

    public ValueClass Class { get; init; } = ValueClass.Business;

    /// <summary>Identity of the change: one edit per key per tier, last one wins.</summary>
    public (string TierId, string Path) Key => (TierId, Path);

    public bool IsSecret => Class == ValueClass.Secret;

    /// <summary>What the user is allowed to see of the outgoing value. Secrets are described, never shown.</summary>
    public string NewDisplay => Kind switch
    {
        EditKind.Delete => "(removed)",
        _ when IsSecret => SecretMasker.Describe(NewValue),
        _ => NewValue ?? string.Empty,
    };

    public string BaseDisplay => Kind switch
    {
        EditKind.Add => "(absent)",
        _ when IsSecret => SecretMasker.Describe(BaseValue),
        _ => BaseValue ?? string.Empty,
    };

    /// <summary>
    /// True when the document has moved underneath this edit — the key vanished, appeared, or now
    /// holds a value other than the one the edit was authored against.
    /// </summary>
    public bool IsStaleAgainst(FlatConfig current)
    {
        var leaf = current.Find(Path);

        return Kind switch
        {
            EditKind.Add => leaf is not null,
            _ => leaf is null || !string.Equals(leaf.ComparableValue, BaseValue, StringComparison.Ordinal),
        };
    }

    /// <summary>Re-reads the base from the current document, so a reviewed stale edit can proceed.</summary>
    public PendingEdit RebasedOn(FlatConfig current)
    {
        var leaf = current.Find(Path);

        return Kind switch
        {
            EditKind.Add when leaf is not null => this with { Kind = EditKind.Update, BaseValue = leaf.ComparableValue },
            EditKind.Add => this,
            _ when leaf is null => this with { Kind = EditKind.Add, BaseValue = null },
            _ => this with { BaseValue = leaf.ComparableValue },
        };
    }
}
