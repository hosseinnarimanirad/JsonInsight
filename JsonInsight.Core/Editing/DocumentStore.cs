using System.Text.Json.Nodes;
using JsonInsight.Loading;
using JsonInsight.Model;
using JsonInsight.Promote;

namespace JsonInsight.Editing;

/// <summary>
/// The app's in-memory documents: one <see cref="DocumentEditor"/> per tier, held for as long as the
/// tier is loaded.
///
/// <para>
/// It exists because there used to be no single answer to "what does stage currently say". The Tier
/// editor kept its own editor per tier, privately, in a view model that was rebuilt from scratch on
/// every pull and every per-source load — so an edit there was invisible to the All tiers grid, to
/// the Text diff and to promote, and was silently discarded by events that looked like navigation.
/// Ownership moved here so that every tab reads the same tree, and so that an edit outlives a tab
/// being rebuilt.
/// </para>
///
/// <para>
/// Editors are created on demand rather than for every tier at load: an editor deep-clones the
/// document twice, and most sessions edit one tier out of four.
/// </para>
/// </summary>
public sealed class DocumentStore
{
    private readonly Flattener _flattener;
    private readonly Dictionary<string, DocumentEditor> _editors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tiers whose editor has moved since the last <see cref="Materialize"/>.
    ///
    /// <para>
    /// Re-flattening is deferred rather than done per edit because the Tier editor commits a scalar
    /// as it is typed: flattening the whole document and rebuilding the comparison on every keystroke
    /// would be work done for a tab nobody is looking at yet.
    /// </para>
    /// </summary>
    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);

    public DocumentStore(Flattener flattener) => _flattener = flattener;

    /// <summary>The editor for this tier, created on first use.</summary>
    public DocumentEditor For(TierDocument document)
    {
        if (_editors.TryGetValue(document.Id, out var existing))
        {
            return existing;
        }

        var editor = new DocumentEditor(document);
        _editors[document.Id] = editor;
        return editor;
    }

    /// <summary>The editor for this tier if one has been opened, without creating one.</summary>
    public DocumentEditor? Find(string tierId) =>
        _editors.TryGetValue(tierId, out var editor) ? editor : null;

    /// <summary>Whether this tier holds unsaved changes. A tier never opened for editing holds none.</summary>
    public bool IsModified(string tierId) => Find(tierId)?.IsModified == true;

    /// <summary>Every tier holding unsaved changes, in the order the editors were opened.</summary>
    public IReadOnlyList<string> ModifiedTiers =>
        _editors.Where(pair => pair.Value.IsModified).Select(pair => pair.Key).ToList();

    public bool HasUnsavedChanges => _editors.Values.Any(editor => editor.IsModified);

    /// <summary>Records that a tier's editor has moved, so the next materialize re-flattens it.</summary>
    public void MarkEdited(string tierId) => _dirty.Add(tierId);

    /// <summary>
    /// Lands a change set on a tier here and now, rather than queueing it to be written later.
    ///
    /// <para>
    /// Applied onto the tier's working tree rather than onto the document it was read as, so an edit
    /// made on the All tiers tab stacks onto whatever the Tier editor has already changed instead of
    /// silently reverting it. It arrives as one undo step, which is what makes a six-row batch edit
    /// one press of Undo rather than six.
    /// </para>
    /// </summary>
    public void Apply(TierDocument document, IReadOnlyList<PendingEdit> edits)
    {
        if (edits.Count == 0)
        {
            return;
        }

        var editor = For(document);
        ApplyTree(document, EditApplier.Apply(editor.Working, edits));
    }

    /// <summary>
    /// Replaces a tier's whole working tree — what a promote produces, and what anything else that
    /// computes a document rather than a list of keys hands over.
    /// </summary>
    public void ApplyTree(TierDocument document, JsonNode updated)
    {
        var editor = For(document);
        editor.Replace(string.Empty, OrdinalJsonWriter.SerializeToText(updated));
        MarkEdited(document.Id);
    }

    /// <summary>
    /// Every path changed in memory across every tier, for the marks on a grid whose rows are paths
    /// rather than tiers. Empty when nothing has been touched.
    /// </summary>
    public IReadOnlySet<string> ChangedPaths()
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var editor in _editors.Values.Where(e => e.IsModified))
        {
            paths.UnionWith(editor.ChangedPaths());
        }

        return paths;
    }

    /// <summary>The paths one tier has changed, or nothing when it is untouched.</summary>
    public IReadOnlySet<string> ChangedPaths(string tierId) =>
        Find(tierId) is { IsModified: true } editor
            ? editor.ChangedPaths()
            : new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Publishes pending edits into the documents: every dirty tier is re-flattened from its working
    /// tree and returned as a new <see cref="TierDocument"/> carrying both.
    ///
    /// <para>
    /// Returns null when nothing was dirty, so a caller can skip rebuilding a grid that would come
    /// out identical.
    /// </para>
    /// </summary>
    public IReadOnlyList<TierDocument>? Materialize(IReadOnlyList<TierDocument> documents) =>
        Materialize(documents, out _);

    /// <param name="refreshed">
    /// The tiers whose document was replaced, so a tab already showing one can rebuild around it
    /// rather than going on displaying the tree it was holding.
    /// </param>
    public IReadOnlyList<TierDocument>? Materialize(
        IReadOnlyList<TierDocument> documents,
        out IReadOnlyList<string> refreshed)
    {
        refreshed = [];

        if (_dirty.Count == 0)
        {
            return null;
        }

        var updated = new List<TierDocument>(documents.Count);
        var touched = new List<string>();
        var changed = false;

        foreach (var document in documents)
        {
            if (!_dirty.Contains(document.Id) || Find(document.Id) is not { } editor)
            {
                updated.Add(document);
                continue;
            }

            // An editor back at its opened state is not an edit any more — Revert all, and undoing to
            // the start, both land here, and leaving an edited tree on the document would keep
            // claiming otherwise. Either way the flatten comes from the tree being published, never
            // from the document's existing Flat, which may still describe the edit being taken back.
            var live = editor.IsModified ? editor.Working : document.Root;
            var flat = _flattener.Flatten(document.Id, live);

            updated.Add(editor.IsModified
                ? document.WithEdits(live, flat)
                : Unedited(document, flat));

            touched.Add(document.Id);
            changed = true;
        }

        _dirty.Clear();
        refreshed = touched;
        return changed ? updated : null;
    }

    /// <summary>The same tier with no edits on it — what an editor back at its opened state publishes.</summary>
    private static TierDocument Unedited(TierDocument document, FlatConfig flat) => new()
    {
        Definition = document.Definition,
        Root = document.Root,
        EditedRoot = null,
        Flat = flat,
        Origin = document.Origin,
        VaultVersion = document.VaultVersion,
        VaultCreatedTime = document.VaultCreatedTime,
        VaultAddress = document.VaultAddress,
        VaultSecretPath = document.VaultSecretPath,
        FilePath = document.FilePath,
        FileModifiedUtc = document.FileModifiedUtc,
    };

    /// <summary>
    /// Forgets a tier's edits, for when a fresh read has replaced what they were made against.
    /// Keeping them would mean editing one tree while showing another.
    /// </summary>
    public void Drop(string tierId)
    {
        _editors.Remove(tierId);
        _dirty.Remove(tierId);
    }

    public void Clear()
    {
        _editors.Clear();
        _dirty.Clear();
    }
}
