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
/// Reviews what the app is holding but has not written, one tier at a time, and pushes it — the
/// review and the push are one window.
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
/// Nothing here writes. The push half is a <see cref="PushVm"/> for the selected tier, rebuilt
/// whenever the tier or its changes move, so every fence the push screen had is still the one in
/// front of the button. It used to be a second window opened from this one, which meant reviewing
/// the keys in one window, confirming the diff in another, and closing back to the first for the
/// next tier; the queued changes and the live diff belong on one screen, read top to bottom.
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

    /// <summary>
    /// The push for the selected tier, or null when there is nothing it may push — no tier, no
    /// changes, or a tier marked read-only. Replaced rather than reset, because what it would send
    /// changes whenever a path is discarded, and a plan built against the previous payload must not
    /// survive into the next.
    /// </summary>
    [ObservableProperty]
    private PushVm? _push;

    /// <summary>
    /// What each push made from this window reported, oldest first: the new version, and anything
    /// that went wrong after the write landed. Kept here because the window moves on to the next
    /// tier as soon as one is pushed, and a read-back failure must not vanish with the screen it was
    /// reported on.
    /// </summary>
    public ObservableCollection<string> PushReport { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    public ObservableCollection<TierDocument> Tiers { get; } = [];

    public string Title => "Pending changes";

    /// <summary>
    /// Whether the window performs the live read itself. Off in tests, the same switch
    /// <see cref="PushVm"/> takes: a rendering check that quietly read production Vault would be worse
    /// than one that failed.
    /// </summary>
    public bool ChecksOnOpen { get; }

    /// <summary>
    /// Set once the host has shown the window. Until then a tier change builds the push but does not
    /// read: the constructor selects the first tier, and a network read started from a constructor is
    /// one nobody can await or see fail.
    /// </summary>
    private bool _opened;

    /// <summary>Whether Check again has anything to check, and is not already checking.</summary>
    public bool CanCheck => Push is { Busy: false };

    /// <summary>Names what it reads, because "Check Vault again" over a file on disk would be wrong.</summary>
    public string CheckLabel => Push is { TierIsFile: true } ? "Check file again" : "Check Vault again";

    /// <summary>Whether the push button may be pressed. Every condition behind it is the push's own.</summary>
    public bool CanPushNow => Push is { CanPush: true };

    /// <summary>
    /// Whether this tier can go to the push screen, which is where it is confirmed against what its
    /// source holds at that moment and where the tier's name is typed out.
    /// </summary>
    public bool CanPush => Changes.Count > 0 && Tier is { Writable: true };

    public ChangesVm(MainVm main, bool checksOnOpen = true)
    {
        _main = main;
        ChecksOnOpen = checksOnOpen;

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

    partial void OnTierChanged(TierDocument? value) => BuildChangeList();

    /// <summary>
    /// Called by the host once the window is on screen, and reads the selected tier's source: the
    /// comparison is half of what this window shows, and it has nothing to show until it has one.
    /// </summary>
    public async Task OpenedAsync()
    {
        _opened = true;

        if (ChecksOnOpen && Push is { } push)
        {
            await push.CheckVaultAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Reads the source again and rebuilds the comparison. A read — nothing is sent.</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckAgainAsync() => Push?.CheckVaultAsync() ?? Task.CompletedTask;

    /// <summary>
    /// Builds the push for what this tier now holds, and reads its source once the window is up.
    ///
    /// <para>
    /// Rebuilt after every discard as well as every tier change: the payload is the tier's live
    /// document, so taking one path back changes what would be sent, and the diff on screen has to be
    /// against that rather than the payload it was checked with a moment ago.
    /// </para>
    /// </summary>
    private void RebuildPush()
    {
        if (Push is { } previous)
        {
            previous.PropertyChanged -= OnPushChanged;
        }

        Push = Tier is { Writable: true } tier && Changes.Count > 0
            ? new PushVm(_main, tier, tier.Live, What, checksOnOpen: false)
            : null;

        if (Push is { } push)
        {
            push.PropertyChanged += OnPushChanged;

            if (_opened && ChecksOnOpen)
            {
                // Not awaited: this runs from a property setter. The check reports its own failure as
                // the push's Problem, so nothing is lost by not waiting for it here.
                _ = push.CheckVaultAsync();
            }
        }

        NotifyPushState();
    }

    private void OnPushChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PushVm.Busy) or nameof(PushVm.CanPush))
        {
            NotifyPushState();
        }
    }

    private void NotifyPushState()
    {
        OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(CheckLabel));
        OnPropertyChanged(nameof(CanPushNow));
        CheckAgainCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Pushes the selected tier and, once it has gone through, moves on to the next tier still holding
    /// changes — a batch across four tiers is four confirmations in one window.
    /// </summary>
    [RelayCommand]
    private async Task PushAsync()
    {
        if (Push is not { CanPush: true } push)
        {
            return;
        }

        await push.PushCommand.ExecuteAsync(null).ConfigureAwait(true);

        if (push.PushedTier is null)
        {
            // Refused or failed. The push says why, and stays on screen to say it.
            return;
        }

        PushReport.Add(push.Message);
        foreach (var note in push.Notes)
        {
            PushReport.Add(note);
        }

        Pushed();
    }

    private void BuildChangeList()
    {
        Changes.Clear();
        Warnings.Clear();

        if (Tier is null || _main.Store.Find(Tier.Id) is not { } editor)
        {
            Message = PushReport.Count > 0 ? "Pushed. Nothing is unsaved now." : "Nothing has been changed.";
            TargetDescription = string.Empty;
            OnPropertyChanged(nameof(CanPush));
            RebuildPush();
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
        OnPropertyChanged(nameof(What));
        RebuildPush();
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
    /// The document this tier would send. It is simply what the tier now says — there is nothing left
    /// to apply, because the changes were applied when they were made.
    /// </summary>
    public JsonNode? BuildUpdated() => CanPush ? Tier?.Live : null;

    /// <summary>One phrase naming this batch, shown beside the destination.</summary>
    public string What => Tier is null
        ? string.Empty
        : $"{Changes.Count} in-memory change(s) on {Tier.Id}";

    /// <summary>
    /// After a push has gone through: that tier is now what its source holds, so it has nothing
    /// unsaved left, and the window moves to the next tier that does.
    ///
    /// <para>
    /// Rebuilt from the documents rather than by removing the tier from the picker: the push adopted
    /// the read-back, which replaced the document set, and a picker still holding the instances from
    /// before would hand the next push a tier object nothing else is looking at.
    /// </para>
    /// </summary>
    private void Pushed()
    {
        if (Tier is { } tier)
        {
            _main.Store.Drop(tier.Id);
        }

        _main.Tiers?.NotifyEditsChanged();
        Refresh();
    }
}
