using JsonInsight.Model;

namespace JsonInsight.Editing;

/// <summary>
/// The pending change set: every queued edit, across every tier, until it is committed.
///
/// <para>
/// Batching is the point rather than a convenience. The change that prompted this feature was one
/// shape applied to six sibling keys across two files; six sequential single-key commits — six
/// previews, six backups, six typed confirmations — would have been worse than editing the files by
/// hand. Edits accumulate here and are written a tier at a time, so one commit is one backup and one
/// confirmation no matter how many keys it carries.
/// </para>
///
/// <para>
/// Keyed by tier and path, so editing the same key twice replaces rather than stacks. An edit that
/// sets a value back to what the document already holds is dropped rather than queued: a no-op in
/// the list would make the pending count lie about how much is about to change.
/// </para>
/// </summary>
public sealed class EditSet
{
    private readonly Dictionary<(string TierId, string Path), PendingEdit> _edits = [];

    public IReadOnlyCollection<PendingEdit> All => _edits.Values;

    public int Count => _edits.Count;

    public bool IsEmpty => _edits.Count == 0;

    public IEnumerable<string> TierIds =>
        _edits.Keys.Select(k => k.TierId).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<PendingEdit> For(string tierId) =>
        _edits.Values
            .Where(e => e.TierId.Equals(tierId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .ToArray();

    public PendingEdit? Find(string tierId, string path) =>
        _edits.GetValueOrDefault((tierId, path));

    public bool Contains(string tierId, string path) => _edits.ContainsKey((tierId, path));

    /// <summary>Queues an edit, replacing any previous one for the same key in the same tier.</summary>
    public void Add(PendingEdit edit) => _edits[edit.Key] = edit;

    /// <summary>
    /// Queues an edit unless it would change nothing. Returns false when it was dropped as a no-op,
    /// so the caller can say so rather than showing a pending count that does not move.
    /// </summary>
    public bool AddUnlessNoOp(PendingEdit edit)
    {
        if (edit.Kind == EditKind.Update &&
            string.Equals(edit.BaseValue, edit.NewValue, StringComparison.Ordinal))
        {
            _edits.Remove(edit.Key);
            return false;
        }

        _edits[edit.Key] = edit;
        return true;
    }

    public bool Remove(string tierId, string path) => _edits.Remove((tierId, path));

    public void Remove(PendingEdit edit) => _edits.Remove(edit.Key);

    public void RemoveTier(string tierId)
    {
        foreach (var key in _edits.Keys.Where(k => k.TierId.Equals(tierId, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            _edits.Remove(key);
        }
    }

    public void Clear() => _edits.Clear();

    /// <summary>Re-reads every edit's base from the current document. Values set are left alone.</summary>
    public void RebaseOn(TierDocument document)
    {
        foreach (var edit in For(document.Id))
        {
            _edits[edit.Key] = edit.RebasedOn(document.Flat);
        }
    }

    public string Summary()
    {
        if (IsEmpty)
        {
            return "no pending changes";
        }

        var adds = _edits.Values.Count(e => e.Kind == EditKind.Add);
        var updates = _edits.Values.Count(e => e.Kind == EditKind.Update);
        var deletes = _edits.Values.Count(e => e.Kind == EditKind.Delete);

        var parts = new List<string>();
        if (updates > 0)
        {
            parts.Add($"{updates} changed");
        }

        if (adds > 0)
        {
            parts.Add($"{adds} added");
        }

        if (deletes > 0)
        {
            parts.Add($"{deletes} deleted");
        }

        var tiers = TierIds.ToArray();
        return $"{string.Join(", ", parts)} in {string.Join(", ", tiers)}";
    }
}
