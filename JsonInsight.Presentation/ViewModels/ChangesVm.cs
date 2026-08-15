using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Text.Json.Nodes;
using JsonInsight.Classify;
using JsonInsight.Editing;
using JsonInsight.Model;
using JsonInsight.Promote;
using JsonInsight.Sources;
using ValueClass = JsonInsight.Model.ValueClass;

namespace JsonInsight.ViewModels;

/// <summary>One path this tier has changed in memory, as shown in the review list.</summary>
public sealed class ChangeRowVm
{
    public required string Path { get; init; }

    public required string TierId { get; init; }

    public required NodeChange Change { get; init; }

    /// <summary>Bound by the Text.ByClass style; the name is what the column shows.</summary>
    public required ValueClass Class { get; init; }

    public required string Before { get; init; }

    public required string After { get; init; }

    public string KindName => Change switch
    {
        NodeChange.Added => "add",
        NodeChange.Removed => "delete",
        _ => "update",
    };

    public string ClassName => Class.ToString().ToLowerInvariant();

    public string BaseDisplay => Change == NodeChange.Added ? "(absent)" : Describe(Before);

    public string NewDisplay => Change == NodeChange.Removed ? "(removed)" : Describe(After);

    /// <summary>Secrets are described, never shown — the same rule the batch editor follows.</summary>
    private string Describe(string value) =>
        Class == ValueClass.Secret ? SecretMasker.Describe(value) : value;
}

/// <summary>
/// Reviews what the app is holding but has not written, one tier at a time, and hands a tier to the
/// push screen.
///
/// <para>
/// Per tier rather than all at once, because a push is one secret, one version and one typed
/// confirmation, and a confirmation covering four environments would be worth less than the four it
/// replaced. Within a tier the whole document goes in one version, which is the part that matters:
/// six sequential single-key versions would be six entries in a history describing one change.
/// </para>
///
/// <para>
/// This used to review a queue of edits that existed nowhere else until they were pushed. There is no
/// queue now — an edit lands in the tier's in-memory document the moment it is made, wherever it was
/// made — so what this lists is the difference between what a tier says and what its source last
/// handed over. That also retired the staleness machinery: an edit could go stale against a document
/// that moved underneath it, but a document cannot go stale against itself. The question that
/// genuinely remains — did the <em>source</em> move since it was read — was always the push screen's,
/// and still is.
/// </para>
///
/// <para>
/// Nothing here writes. It hands a tier over — the same hand-off Promote used to make, into the same
/// screen, with the same fences behind it.
/// </para>
/// </summary>
public sealed partial class ChangesVm : ObservableObject
{
    private readonly MainVm _main;

    /// <summary>The window needs it to build the push screen; nothing here opens a window itself.</summary>
    public MainVm Main => _main;

    [ObservableProperty]
    private TierDocument? _tier;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private string _targetDescription = string.Empty;

    public ObservableCollection<ChangeRowVm> Changes { get; } = [];

    public ObservableCollection<DiffLineVm> PreviewLines { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    public ObservableCollection<TierDocument> Tiers { get; } = [];

    public string Title => "Unsaved changes";

    /// <summary>
    /// Whether this tier can go to the push screen, which is where it is confirmed against what its
    /// source holds at that moment and where the tier's name is typed out.
    /// </summary>
    public bool CanPush => Changes.Count > 0 && Tier is { Writable: true };

    public ChangesVm(MainVm main)
    {
        _main = main;

        // Published first: an edit made a moment ago on another tab may not have been flattened into
        // the documents yet, and a review screen that opened saying "nothing to write" would be
        // wrong in the one situation it exists for.
        main.PublishEdits();

        foreach (var tierId in main.Store.ModifiedTiers)
        {
            if (main.Documents.FirstOrDefault(d =>
                    d.Id.Equals(tierId, StringComparison.OrdinalIgnoreCase)) is { } document)
            {
                Tiers.Add(document);
            }
        }

        Tier = Tiers.FirstOrDefault();
    }

    partial void OnTierChanged(TierDocument? value)
    {
        PreviewLines.Clear();
        BuildChangeList();
    }

    private void BuildChangeList()
    {
        Changes.Clear();
        Warnings.Clear();

        if (Tier is null || _main.Store.Find(Tier.Id) is not { } editor)
        {
            Message = "Nothing has been changed.";
            TargetDescription = string.Empty;
            OnPropertyChanged(nameof(CanPush));
            return;
        }

        // Only the paths that actually changed. ChangeKinds also marks every ancestor of a change as
        // Mixed so a collapsed tree can be followed down to it; on a flat list those are not changes,
        // they are the route to one.
        foreach (var (path, change) in editor.ChangeKinds()
                     .Where(pair => pair.Value is NodeChange.Added or NodeChange.Edited or NodeChange.Removed)
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Changes.Add(new ChangeRowVm
            {
                Path = path,
                TierId = Tier.Id,
                Change = change,
                Class = ClassOf(path),
                Before = editor.OriginalTextOrEmpty(path),
                After = editor.WorkingTextOrEmpty(path),
            });
        }

        TargetDescription = Tier.Definition.Kind == SourceKind.LocalFile
            ? Tier.Definition.LocalFilePath is { Length: > 0 } file
                ? $"{file} — pushing overwrites that file, after backing up what is in it."
                : $"{Tier.Id} names no file, so there is nowhere to push it."
            : Tier.Definition.VaultPath is { Length: > 0 } secret
                ? $"{secret} — pushing creates a new version of that secret. Nothing is written locally."
                : $"{Tier.Id} names neither a Vault secret nor a file, so there is nowhere to push it.";

        Message = Changes.Count == 0
            ? $"{Tier.Id} matches what its source last handed over."
            : $"{Changes.Count} change(s) held in memory for {Tier.Id}, written to no source yet.";

        OnPropertyChanged(nameof(CanPush));
    }

    /// <summary>
    /// How the value at a path is classified, so a secret is described rather than printed. Read from
    /// the live flatten, which is the one that knows about a key that only exists because it was just
    /// added.
    /// </summary>
    private ValueClass ClassOf(string path) =>
        Tier?.Flat.Find(path)?.Class
        ?? _main.Classifier.Classify(path, string.Empty);

    /// <summary>Takes back one path, leaving every other change on this tier alone.</summary>
    [RelayCommand]
    private void Discard(ChangeRowVm? row)
    {
        if (row is null || Tier is null || _main.Store.Find(Tier.Id) is not { } editor)
        {
            return;
        }

        try
        {
            editor.RevertNode(row.Path);
        }
        catch (Exception ex)
        {
            Message = $"Could not take back {row.Path}: {ex.Message}";
            return;
        }

        AfterDiscard();
    }

    /// <summary>Takes this tier back to what its source last handed over.</summary>
    [RelayCommand]
    private void DiscardTier()
    {
        if (Tier is null || _main.Store.Find(Tier.Id) is not { } editor)
        {
            return;
        }

        editor.RevertAll();
        AfterDiscard();
    }

    private void AfterDiscard()
    {
        if (Tier is { } tier)
        {
            _main.Store.MarkEdited(tier.Id);
        }

        _main.PublishEdits();
        _main.Tiers?.NotifyEditsChanged();

        PreviewLines.Clear();
        Refresh();
    }

    /// <summary>
    /// Rebuilds the tier list around what is still unsaved, keeping the tier being looked at if it
    /// still has changes.
    /// </summary>
    private void Refresh()
    {
        var current = Tier?.Id;

        var remaining = _main.Store.ModifiedTiers
            .Select(id => _main.Documents.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            .Where(d => d is not null)
            .Select(d => d!)
            .ToList();

        Tiers.Clear();
        foreach (var document in remaining)
        {
            Tiers.Add(document);
        }

        var next = Tiers.FirstOrDefault(d => d.Id.Equals(current, StringComparison.OrdinalIgnoreCase))
                   ?? Tiers.FirstOrDefault();

        if (ReferenceEquals(next, Tier))
        {
            // Same instance, so assigning raises nothing and the list has to be rebuilt by hand.
            BuildChangeList();
            return;
        }

        Tier = next;
        if (Tier is null)
        {
            BuildChangeList();
        }
    }

    /// <summary>
    /// What this tier has changed, as a document diff against the state it was read in.
    ///
    /// <para>
    /// A local preview, and it says so: the authoritative comparison is the one the push screen makes
    /// against what the source holds at the moment of pushing. This one answers the narrower question
    /// you are on this screen to ask — did my six edits do what I meant them to.
    /// </para>
    /// </summary>
    [RelayCommand]
    private void Preview()
    {
        PreviewLines.Clear();

        if (Tier is null || _main.Store.Find(Tier.Id) is not { } editor)
        {
            return;
        }

        string before, after;
        try
        {
            before = editor.OriginalText;
            after = editor.WorkingText;
        }
        catch (Exception ex)
        {
            Message = $"Could not build the comparison: {ex.Message}";
            return;
        }

        // Only the rows that differ: the question this screen is open to answer is "did my six edits
        // do what I meant them to", and the document around them is not part of the answer.
        foreach (var line in DiffLineVm.Build(before, after, includeUnchanged: false).Lines)
        {
            PreviewLines.Add(line);
        }

        Message = PreviewLines.Count == 0
            ? "Nothing differs from the state this tier was read in."
            : $"{PreviewLines.Count} changed line(s), against {Tier.Id} as it was read. " +
              "Push compares against what that source holds at that moment.";

        OnPropertyChanged(nameof(CanPush));
    }

    /// <summary>
    /// The document this tier would send. It is simply what the tier now says — there is nothing left
    /// to apply, because the changes were applied when they were made.
    /// </summary>
    public JsonNode? BuildUpdated() => CanPush ? Tier?.Live : null;

    /// <summary>
    /// The push this screen would hand on, or null when there is nothing to hand on. Asked instead of
    /// spelling out "if there is a tier and it has changes" at each of the two hosts' buttons.
    /// </summary>
    public PendingPush? PendingPush() =>
        Tier is { } tier && BuildUpdated() is { } updated
            ? new PendingPush(tier, updated, What)
            : null;

    /// <summary>One phrase naming this batch, carried onto the push screen.</summary>
    public string What => Tier is null
        ? string.Empty
        : $"{Changes.Count} in-memory change(s) on {Tier.Id}";

    /// <summary>
    /// Called by the dialog once a push has gone through: that tier is now what its source holds, so
    /// it has nothing unsaved left.
    /// </summary>
    public void Pushed()
    {
        if (Tier is not { } tier)
        {
            return;
        }

        _main.Store.Drop(tier.Id);
        _main.Tiers?.NotifyEditsChanged();

        Tiers.Remove(tier);
        Tier = Tiers.FirstOrDefault();

        if (Tier is null)
        {
            Changes.Clear();
            Message = "Pushed. Nothing is unsaved now.";
            OnPropertyChanged(nameof(CanPush));
        }
    }
}
