using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using JsonInsight.Editing;
using JsonInsight.Model;
using JsonInsight.Promote;
using JsonInsight.Sources;
using JsonInsight.Vault;

namespace JsonInsight.ViewModels;

/// <summary>
/// Writes one tier's document back to its source — a Vault secret or a file on disk — after
/// showing what it would replace.
///
/// <para>
/// Every change this app can make arrives here: a promoted subtree, a batch of key edits, a retyped
/// document, or a tier as it stands. They differ only in which tree they hand over, which is why
/// there is one push screen rather than three write buttons that each grew their own idea of what a
/// confirmation is.
/// </para>
///
/// <para>
/// It opens by reading the secret live, so the diff is against what Vault holds <em>now</em> rather
/// than against what the app was showing — those are the same thing most of the time, and the times
/// they are not are exactly the times this matters. Every fence belongs to <see cref="VaultPusher"/>;
/// what lives here is the review.
/// </para>
/// </summary>
public sealed partial class PushVm : ObservableObject
{
    private readonly MainVm _main;

    /// <summary>The tree to push when one was handed in. Null means "whatever the selected tier is".</summary>
    private readonly JsonNode? _supplied;

    private readonly string? _suppliedWhat;

    private PushPlan? _plan;

    /// <summary>
    /// Whichever provider serves the selected tier's kind. Resolved per tier rather than held for the
    /// dialog's lifetime because the tier picker can move between a Vault secret and a local file, and
    /// the write those two need is not the same write.
    /// </summary>
    private ISourceProvider Provider => SourceProviders.For(Tier, _main.Flattener);

    [ObservableProperty]
    private TierDocument? _tier;

    [ObservableProperty]
    private string _confirmText = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private bool _busy;

    [ObservableProperty]
    private bool _completed;

    /// <summary>False while nothing has been read yet, so a stale diff can never be what is confirmed.</summary>
    [ObservableProperty]
    private bool _hasChecked;

    [ObservableProperty]
    private string _problem = string.Empty;

    /// <summary>
    /// Off by default. The question this screen answers is "what am I about to change", and the
    /// twenty-odd differing lines out of nine hundred are the answer.
    /// </summary>
    [ObservableProperty]
    private bool _showUnchanged;

    /// <summary>Whether opening the dialog performs the live read. See the constructor.</summary>
    public bool ChecksOnOpen { get; }

    public ObservableCollection<TierDocument> Tiers { get; } = [];

    public ObservableCollection<DiffLineVm> Lines { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    public ObservableCollection<string> Notes { get; } = [];

    /// <summary>
    /// True when the content was handed in rather than derived from the tier, which fixes the tier:
    /// a promote plan or an edited document belongs to the tier it was built against, and offering a
    /// picker beside it would offer to push one tier's changes into another.
    /// </summary>
    public bool IsTierFixed => _supplied is not null;

    /// <param name="updated">
    /// The document to push. Null means the tier as it stands, with any change set queued against it
    /// applied — which is what the All tiers tab's button means.
    /// </param>
    /// <param name="checksOnOpen">
    /// Set false by the test harness, the same way <see cref="MainVm"/> takes the startup read. This
    /// screen's whole content is a live comparison, so it reads Vault as it opens — and a test that
    /// quietly reached production Vault from a rendering check would be worse than one that failed.
    /// </param>
    public PushVm(
        MainVm main,
        TierDocument? tier = null,
        JsonNode? updated = null,
        string? what = null,
        bool checksOnOpen = true)
    {
        _main = main;
        _supplied = updated;
        _suppliedWhat = what;
        ChecksOnOpen = checksOnOpen;

        if (updated is not null && tier is not null)
        {
            Tiers.Add(tier);
        }
        else
        {
            foreach (var document in main.Documents.Where(d => d.Writable))
            {
                Tiers.Add(document);
            }
        }

        Tier = Tiers.FirstOrDefault(d => d.Id.Equals(tier?.Id, StringComparison.OrdinalIgnoreCase))
               ?? Tiers.FirstOrDefault();
    }

    /// <summary>True when the selected tier is a file on disk rather than a Vault secret.</summary>
    public bool TierIsFile => Tier?.Definition.Kind == SourceKind.LocalFile;

    /// <summary>Where this tier's content actually lives, whichever kind of source it is.</summary>
    private string? TierPath => TierIsFile ? Tier?.Definition.LocalFilePath : Tier?.Definition.VaultPath;

    /// <summary>What is being uploaded, and to where — said before anything is typed or pressed.</summary>
    public string Destination => _plan?.Destination
                                 ?? (TierPath is { Length: > 0 } path
                                     ? $"{path} — press Check to read what this would replace."
                                     : "This tier names no secret and no file, so there is nowhere to push it.");

    /// <summary>The change in one phrase, so the dialog says what it is pushing and not only where.</summary>
    public string What => _suppliedWhat ?? DescribeQueuedEdits();

    public string SourceLine => Tier is null ? string.Empty : $"{Tier.Id}: {Tier.SourceLine}";

    /// <summary>
    /// The two sides of the diff, named on the pane so neither is mistaken for the other. The left one
    /// names the kind it read, because "VAULT v03" over the contents of a file on disk would be a
    /// wrong answer to the only question this pane exists to settle.
    /// </summary>
    public string LeftHeader
    {
        get
        {
            var what = TierIsFile ? "FILE" : "VAULT";

            if (_plan is null)
            {
                return $"{what} — NOT READ YET";
            }

            var when = _plan.LiveCreated is { } created
                ? $"  ·  {created.LocalDateTime:yyyy-MM-dd HH:mm}"
                : string.Empty;

            return _plan.Kind == SourceKind.LocalFile
                ? $"FILE AS IT IS NOW{when}"
                : $"VAULT v{_plan.LiveVersion:00}{when}";
        }
    }

    public string RightHeader => _plan is null
        ? "WHAT WOULD BE PUSHED"
        : $"WHAT WOULD BE PUSHED  —  {_plan.What}";

    public bool ConfirmMatches =>
        Tier is not null && ConfirmText.Trim().Equals(Tier.Id, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A push needs a live read behind it, a real difference to send, a source that has not moved
    /// since this was built, and the tier's name typed out. Nothing here is a formality: the first is
    /// where the check-and-set version comes from, and the third is the one the check-and-set cannot
    /// catch — see <see cref="PushPlan.Stale"/>.
    /// </summary>
    public bool CanPush =>
        !Busy && !Completed && HasChecked &&
        _plan is { Identical: false, Stale: null } && ConfirmMatches;

    /// <summary>
    /// Why this may not be pushed because the source moved, or null. The providers refuse it too —
    /// this is what stops the button being pressable in the first place, and what the dialog shows
    /// instead of a diff nobody may act on.
    /// </summary>
    public string? Stale => _plan?.Stale;

    public bool IsStale => Stale is not null;

    partial void OnTierChanged(TierDocument? value)
    {
        _plan = null;
        HasChecked = false;
        Completed = false;
        ConfirmText = string.Empty;
        Lines.Clear();
        Warnings.Clear();
        Notes.Clear();
        Message = string.Empty;

        Problem = SourceProviders.For(value, _main.Flattener)
            .Blocked(value, VaultSettingsStore.Load().Settings) ?? string.Empty;

        NotifyState();
    }

    partial void OnConfirmTextChanged(string value) => OnPropertyChanged(nameof(CanPush));

    partial void OnShowUnchangedChanged(bool value) => BuildDiff();

    /// <summary>
    /// The tree this would send: the one handed in, or the selected tier with its queued key changes
    /// applied. A tier with nothing queued produces itself, which the preflight then reports as
    /// nothing to push rather than pretending there is.
    /// </summary>
    private JsonNode? Updated()
    {
        if (_supplied is not null)
        {
            return _supplied;
        }

        if (Tier is null)
        {
            return null;
        }

        var edits = _main.Edits.For(Tier.Id);
        return edits.Count == 0 ? Tier.Root : EditApplier.Apply(Tier, edits);
    }

    private string DescribeQueuedEdits()
    {
        if (Tier is null)
        {
            return string.Empty;
        }

        var edits = _main.Edits.For(Tier.Id);
        return edits.Count == 0
            ? $"{Tier.Id} as it stands"
            : $"{edits.Count} queued key change(s)";
    }

    /// <summary>
    /// Reads the secret live and builds the diff against it. Sends nothing — and is run on opening,
    /// because a dialog whose whole content is a comparison has nothing to show until it has one.
    /// </summary>
    [RelayCommand]
    public async Task CheckVaultAsync()
    {
        if (Busy || Tier is null)
        {
            return;
        }

        if (Updated() is not { } updated)
        {
            Problem = "There is nothing to push.";
            return;
        }

        Busy = true;
        HasChecked = false;
        _plan = null;
        Lines.Clear();
        Warnings.Clear();
        Message = $"Reading {TierPath} …";
        NotifyState();

        try
        {
            var preflight = await Provider
                .PreflightSaveAsync(Tier, updated, What, VaultSettingsStore.Load().Settings)
                .ConfigureAwait(true);

            if (!preflight.Ok)
            {
                Problem = preflight.Problem!;
                Message = string.Empty;
                return;
            }

            _plan = preflight.Plan;
            HasChecked = true;

            // A source that moved is reported where a refusal belongs rather than among the warnings:
            // it is not something to read on the way past, it is the reason the button below is off.
            Problem = _plan!.Stale ?? string.Empty;

            foreach (var warning in _plan.Warnings)
            {
                Warnings.Add(warning);
            }

            BuildDiff();
        }
        catch (Exception ex)
        {
            Problem = $"Check failed: {ex.Message}";
        }
        finally
        {
            Busy = false;
            NotifyState();
        }
    }

    private void BuildDiff()
    {
        Lines.Clear();

        if (_plan is null)
        {
            return;
        }

        if (_plan.Identical)
        {
            Message = _plan.Kind == SourceKind.LocalFile
                ? "That file already holds exactly this. There is nothing to push."
                : $"Vault v{_plan.LiveVersion:00} already holds exactly this. There is nothing to push.";
            return;
        }

        var model = SideBySideDiffBuilder.Instance.BuildDiffModel(
            _plan.LiveText, _plan.PayloadText, ignoreWhitespace: false);

        var added = 0;
        var removed = 0;
        var modified = 0;

        for (var i = 0; i < Math.Max(model.OldText.Lines.Count, model.NewText.Lines.Count); i++)
        {
            var oldLine = i < model.OldText.Lines.Count ? model.OldText.Lines[i] : null;
            var newLine = i < model.NewText.Lines.Count ? model.NewText.Lines[i] : null;

            // The old side carries the answer for a deletion: DiffPlex pairs a real removed line
            // with an Imaginary placeholder, so reading the new side first labels every deletion
            // "imaginary" and renders it as an uncoloured blank row.
            var right = newLine?.Type ?? ChangeType.Imaginary;
            var left = oldLine?.Type ?? ChangeType.Imaginary;
            var type = right is ChangeType.Imaginary or ChangeType.Unchanged ? left : right;

            switch (type)
            {
                case ChangeType.Inserted:
                    added++;
                    break;
                case ChangeType.Deleted:
                    removed++;
                    break;
                case ChangeType.Modified:
                    modified++;
                    break;
            }

            if (!ShowUnchanged && type is ChangeType.Unchanged or ChangeType.Imaginary)
            {
                continue;
            }

            Lines.Add(new DiffLineVm(
                oldLine?.Position?.ToString() ?? string.Empty,
                oldLine?.Text ?? string.Empty,
                newLine?.Position?.ToString() ?? string.Empty,
                newLine?.Text ?? string.Empty,
                type));
        }

        var counts = $"{modified} changed, {added} added, {removed} removed.";

        // The diff still earns its place on a refused push — it is what says how much of somebody
        // else's work this would have taken out — but it must not go on promising the next version
        // number of a write that is not going to happen.
        if (_plan.Stale is not null)
        {
            Message = $"What is on the left is what the source holds now, and it is not what this was " +
                      $"built from: {counts} Shown so the size of the collision is visible; nothing " +
                      "will be sent.";
            return;
        }

        // A file has no version history to promise the next number of — see PushPlan.Kind.
        Message = _plan.Kind == SourceKind.LocalFile
            ? $"The file as it is now → what is on the right: {counts} " +
              "Pushing overwrites it, after backing up what is there."
            : $"Vault v{_plan.LiveVersion:00} → what is on the right: {counts} " +
              $"Pushing creates v{_plan.LiveVersion + 1:00}.";
    }

    /// <summary>
    /// Uploads it. The check-and-set version is the one the check above read, so a secret that moved
    /// in the meantime is refused by Vault rather than replaced.
    /// </summary>
    [RelayCommand]
    private async Task PushAsync()
    {
        if (!CanPush || Tier is null || _plan is null)
        {
            return;
        }

        Busy = true;
        Message = $"Pushing to {_plan.SecretPath} …";
        NotifyState();

        PushResult result;
        try
        {
            result = await Provider
                .SaveAsync(Tier, _plan, VaultSettingsStore.Load().Settings)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Problem = ex.Message;
            Busy = false;
            NotifyState();
            return;
        }

        foreach (var note in result.Notes)
        {
            Notes.Add(note);
        }

        Busy = false;

        if (!result.Succeeded)
        {
            Problem = result.Error!;

            // The one failure worth re-reading for: the secret moved, so the diff on screen is
            // against a version that is no longer current and must not be confirmable.
            HasChecked = false;
            _plan = null;
            NotifyState();
            return;
        }

        Completed = true;
        Problem = string.Empty;

        // A local file write returns no version — there is none to return. Reporting "version " with
        // nothing after it would read as a write that half worked.
        if (result.Version is { } version)
        {
            Message = $"Pushed. {Tier.Id} is now version {version} in Vault.";
            _main.Status = $"Pushed {Tier.Id} to Vault — version {version}.";
        }
        else
        {
            Message = $"Written. {Tier.Id} now holds this on disk, and the previous content was backed up beside it.";
            _main.Status = $"Wrote {Tier.Id} to disk.";
        }

        // The changes are at their source now, so they are no longer pending anywhere.
        if (_supplied is null)
        {
            _main.Edits.RemoveTier(Tier.Id);
            _main.Tiers?.NotifyEditsChanged();
        }

        PushedTier = Tier.Id;
        NotifyState();

        // Everything reloads onto what Vault now holds, because the tier's current state has changed
        // and every tab showing the previous one is showing history.
        _main.Reload();
    }

    /// <summary>
    /// Which tier was pushed, once one has been. The dialog that opened this reads it to clear its
    /// own state - a promote or a change set that has been uploaded is not still pending.
    /// </summary>
    public string? PushedTier { get; private set; }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(CanPush));
        OnPropertyChanged(nameof(Stale));
        OnPropertyChanged(nameof(IsStale));
        OnPropertyChanged(nameof(Destination));
        OnPropertyChanged(nameof(What));
        OnPropertyChanged(nameof(SourceLine));
        OnPropertyChanged(nameof(LeftHeader));
        OnPropertyChanged(nameof(RightHeader));
        OnPropertyChanged(nameof(ConfirmMatches));
    }
}
