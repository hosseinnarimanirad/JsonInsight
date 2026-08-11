using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Text.Json.Nodes;
using JsonInsight.Editing;
using JsonInsight.Model;
using JsonInsight.Promote;
using JsonInsight.Sources;
using ValueClass = JsonInsight.Model.ValueClass;

namespace JsonInsight.ViewModels;

/// <summary>One queued change, as shown in the review list.</summary>
public sealed partial class ChangeRowVm : ObservableObject
{
    public required PendingEdit Edit { get; init; }

    /// <summary>True when the document moved after this edit was queued. Blocks the write until resolved.</summary>
    [ObservableProperty]
    private bool _isStale;

    public string Path => Edit.Path;

    public string TierId => Edit.TierId;

    public string KindName => Edit.Kind.ToString().ToLowerInvariant();

    /// <summary>Bound by the Text.ByClass style; the name is what the column shows.</summary>
    public ValueClass Class => Edit.Class;

    public string ClassName => Edit.Class.ToString().ToLowerInvariant();

    public string BaseDisplay => Edit.BaseDisplay;

    public string NewDisplay => Edit.NewDisplay;

    public string StaleReason => IsStale
        ? "The tier changed after this edit was queued — its starting value is no longer what was seen."
        : string.Empty;
}

/// <summary>
/// Reviews the pending change set, one tier at a time, and hands it to the push screen.
///
/// <para>
/// Per tier rather than all at once, because a push is one secret, one version and one typed
/// confirmation, and a confirmation covering four environments would be worth less than the four it
/// replaced. Within a tier the whole batch goes in one version, which is the part that matters: six
/// sequential single-key versions would be six entries in a history that describe one change.
/// </para>
///
/// <para>
/// Nothing here writes. It builds the document the batch would produce and hands it over — the same
/// hand-off Promote makes, into the same screen, with the same fences behind it.
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

    public string Title => "Pending changes";

    public bool HasStale => Changes.Any(c => c.IsStale);

    /// <summary>
    /// Whether this batch can go to the push screen, which is where it is confirmed and where the
    /// tier's name is typed out. A stale edit blocks it here rather than there, because staleness is
    /// about the values these changes were made against and this is the screen showing them.
    /// </summary>
    public bool CanPush => !HasStale && Changes.Count > 0 && Tier is { Writable: true };

    public ChangesVm(MainVm main)
    {
        _main = main;

        foreach (var tierId in main.Edits.TierIds)
        {
            var document = main.Documents.FirstOrDefault(d =>
                d.Id.Equals(tierId, StringComparison.OrdinalIgnoreCase));

            if (document is not null)
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

        if (Tier is null)
        {
            Message = "No pending changes.";
            TargetDescription = string.Empty;
            return;
        }

        var edits = _main.Edits.For(Tier.Id);
        foreach (var edit in edits)
        {
            Changes.Add(new ChangeRowVm
            {
                Edit = edit,
                IsStale = edit.IsStaleAgainst(Tier.Flat),
            });
        }

        TargetDescription = Tier.Definition.Kind == SourceKind.LocalFile
            ? Tier.Definition.LocalFilePath is { Length: > 0 } file
                ? $"{file} — pushing overwrites that file, after backing up what is in it."
                : $"{Tier.Id} names no file, so there is nowhere to push it."
            : Tier.Definition.VaultPath is { Length: > 0 } secret
                ? $"{secret} — pushing creates a new version of that secret. Nothing is written locally."
                : $"{Tier.Id} names neither a Vault secret nor a file, so there is nowhere to push it.";

        var validator = new EditValidator(_main.Documents.ToArray(), _main.Classifier);
        foreach (var warning in validator.Validate(edits))
        {
            Warnings.Add(warning.IsBlocking ? "BLOCKED: " + warning.Text : warning.Text);
        }

        Message = HasStale
            ? $"{Changes.Count} change(s). {Changes.Count(c => c.IsStale)} are stale — the tier moved after they " +
              "were queued. Re-base them against the current values or drop them before writing."
            : $"{Changes.Count} change(s) queued for {Tier.Id}.";

        OnPropertyChanged(nameof(HasStale));
        OnPropertyChanged(nameof(CanPush));
    }

    /// <summary>Re-reads every stale edit's starting value from the current document, keeping the value set.</summary>
    [RelayCommand]
    private void Rebase()
    {
        if (Tier is null)
        {
            return;
        }

        _main.Edits.RebaseOn(Tier);
        PreviewLines.Clear();
        BuildChangeList();
        _main.Tiers?.NotifyEditsChanged();
    }

    [RelayCommand]
    private void Discard(ChangeRowVm? row)
    {
        if (row is null)
        {
            return;
        }

        _main.Edits.Remove(row.Edit);
        PreviewLines.Clear();
        BuildChangeList();
        _main.Tiers?.NotifyEditsChanged();

        if (Changes.Count == 0)
        {
            Tiers.Remove(Tier!);
            Tier = Tiers.FirstOrDefault();
        }
    }

    [RelayCommand]
    private void DiscardTier()
    {
        if (Tier is null)
        {
            return;
        }

        var removed = Tier;
        _main.Edits.RemoveTier(removed.Id);
        Tiers.Remove(removed);
        _main.Tiers?.NotifyEditsChanged();

        Tier = Tiers.FirstOrDefault();
        if (Tier is null)
        {
            BuildChangeList();
        }
    }

    /// <summary>
    /// The change set as a document diff, against the tier as it was read from Vault.
    ///
    /// <para>
    /// A local preview, and it says so: the authoritative comparison is the one the push screen
    /// makes against the version Vault holds at the moment of pushing. This one answers the narrower
    /// question you are on this screen to ask — did my six edits do what I meant them to.
    /// </para>
    /// </summary>
    [RelayCommand]
    private void Preview()
    {
        PreviewLines.Clear();

        if (Tier is null)
        {
            return;
        }

        string before, after;
        try
        {
            before = OrdinalJsonWriter.SerializeToText(Tier.Root);
            after = OrdinalJsonWriter.SerializeToText(EditApplier.Apply(Tier, _main.Edits.For(Tier.Id)));
        }
        catch (Exception ex)
        {
            Message = $"Could not apply these changes: {ex.Message}";
            return;
        }

        // Only the rows that differ: the question this screen is open to answer is "did my six edits
        // do what I meant them to", and the document around them is not part of the answer.
        foreach (var line in DiffLineVm.Build(before, after, includeUnchanged: false).Lines)
        {
            PreviewLines.Add(line);
        }

        Message = PreviewLines.Count == 0
            ? "These changes would not alter the document — every value is already what it is being set to."
            : $"{PreviewLines.Count} changed line(s), against {Tier.Id} as it was read. " +
              "Push compares against what that source holds at that moment.";

        OnPropertyChanged(nameof(CanPush));
    }

    /// <summary>
    /// The document this batch would produce, for the push screen to send. Null when there is
    /// nothing to send or the changes cannot be applied — the caller reports rather than pushes.
    /// </summary>
    public JsonNode? BuildUpdated()
    {
        if (!CanPush || Tier is null)
        {
            return null;
        }

        try
        {
            return EditApplier.Apply(Tier, _main.Edits.For(Tier.Id));
        }
        catch (Exception ex)
        {
            Message = $"Could not apply these changes: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// The push this screen would hand on, or null when there is nothing to hand on. Asked instead of
    /// spelling out "if there is a tier and the document builds" at each of the two hosts' buttons.
    /// </summary>
    public PendingPush? PendingPush() =>
        Tier is { } tier && BuildUpdated() is { } updated
            ? new PendingPush(tier, updated, What)
            : null;

    /// <summary>One phrase naming this batch, carried onto the push screen.</summary>
    public string What => Tier is null
        ? string.Empty
        : $"{_main.Edits.For(Tier.Id).Count} queued key change(s) on {Tier.Id}";

    /// <summary>
    /// Called by the dialog once a push has gone through: the changes are in Vault, so they are no
    /// longer queued anywhere.
    /// </summary>
    public void Pushed()
    {
        if (Tier is not { } tier)
        {
            return;
        }

        _main.Edits.RemoveTier(tier.Id);
        _main.Tiers?.NotifyEditsChanged();

        Tiers.Remove(tier);
        Tier = Tiers.FirstOrDefault();

        if (Tier is null)
        {
            Changes.Clear();
            Message = "Pushed. Nothing is queued now.";
            OnPropertyChanged(nameof(CanPush));
        }
    }
}
