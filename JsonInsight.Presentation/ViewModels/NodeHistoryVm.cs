using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JsonInsight.History;
using JsonInsight.Model;
using JsonInsight.Vault;
using Change = JsonInsight.History.NodeChange;

namespace JsonInsight.ViewModels;

/// <summary>One row of the history list: a version, and what it did to the node.</summary>
/// <param name="When">The full timestamp, for the tooltip: "2026-11-09 15:17".</param>
/// <param name="Age">The short form the row shows: "19 h ago".</param>
public sealed record RevisionRowVm(
    int Version,
    string When,
    string Age,
    string ChangeText,
    string Preview,
    bool InVaultNow,
    NodeRevision? Revision,
    string? Unreadable)
{
    public string Label => $"v{Version}";

    /// <summary>The row's tooltip: everything the compact row leaves out.</summary>
    public string Details => Unreadable is { } why
        ? $"v{Version} cannot be read: {why}"
        : string.Join("\n", new[]
        {
            $"v{Version}{(InVaultNow ? " — what Vault holds now" : string.Empty)}",
            When,
            $"{ChangeText}: {Preview}",
        }.Where(l => l.Length > 0));

    /// <summary>A deleted or destroyed version is listed so the history has no silent hole, but it has no content.</summary>
    public bool IsEnabled => Revision is not null;

    /// <summary>"changed" / "added" / "removed" / "first kept" / … — also the CSS/brush key.</summary>
    public string ChangeKey => Revision?.Change.ToString().ToLowerInvariant() ?? "unreadable";
}

/// <summary>What the selected version is compared with.</summary>
public enum HistoryCompare
{
    /// <summary>The node as the Tier editor holds it now, unpushed edits included.</summary>
    Current,

    /// <summary>The version read just before the selected one: "what did this version change".</summary>
    Previous,
}

/// <summary>
/// The Tier editor's history panel: every version of the open tier's secret, and what each one did to
/// the selected node (or, with <see cref="WholeDocument"/>, to the whole document).
///
/// <para>
/// Read when the panel opens and held only while it is open — the versions are what lets the panel
/// follow the tree selection and flip between scopes without asking Vault again. Closing it drops
/// them; opening it again reads Vault again. Nothing goes to disk.
/// </para>
///
/// <para>
/// Read-only towards Vault. <see cref="RestoreCommand"/> does not write anything: it hands the old
/// value to the editor as an ordinary edit, which goes out through the push window like any other.
/// </para>
/// </summary>
public sealed partial class NodeHistoryVm : ObservableObject, IDisposable
{
    /// <summary>
    /// How many of the newest versions are read. A mount with <c>max_versions</c> far above the
    /// default 10 would otherwise turn opening the panel into hundreds of requests.
    /// </summary>
    public const int MaxVersionsRead = 50;

    private readonly IHistoryReader _reader;
    private readonly Func<string, string?> _currentText;
    private readonly Func<string, string?, string?> _restore;
    private readonly CancellationTokenSource _work = new();
    private IReadOnlyList<SecretVersion> _versions = [];
    private IReadOnlyList<(int Version, string Reason)> _skipped = [];
    private int _currentInVault;

    [ObservableProperty]
    private string _nodePath;

    [ObservableProperty]
    private bool _wholeDocument;

    [ObservableProperty]
    private bool _showAll;

    [ObservableProperty]
    private HistoryCompare _compareWith = HistoryCompare.Current;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCopy), nameof(CanRestore), nameof(RestoreLabel))]
    private RevisionRowVm? _selected;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Progress, the gap in Vault's history, or why it could not be read.</summary>
    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>What the last Copy or Restore did.</summary>
    [ObservableProperty]
    private string? _note;

    [ObservableProperty]
    private string _leftHeader = string.Empty;

    [ObservableProperty]
    private string _rightHeader = string.Empty;

    [ObservableProperty]
    private string _diffSummary = string.Empty;

    public NodeHistoryVm(
        IHistoryReader reader,
        TierDocument tier,
        string path,
        bool canEdit,
        Func<string, string?> currentText,
        Func<string, string?, string?> restore)
    {
        _reader = reader;
        Tier = tier;
        CanEdit = canEdit;
        _nodePath = path;
        _currentText = currentText;
        _restore = restore;
    }

    public TierDocument Tier { get; }

    public bool CanEdit { get; }

    public ObservableCollection<RevisionRowVm> Rows { get; } = [];

    public ObservableCollection<DiffLineVm> Lines { get; } = [];

    /// <summary>The read started by <see cref="Load"/>, for a caller (or a test) to wait on.</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>The path the list is about: the node, or "" for the whole document.</summary>
    public string Path => WholeDocument ? string.Empty : NodePath;

    public string Title => Path.Length == 0 ? $"History · {Tier.Id} · whole document" : $"History · {Path}";

    /// <summary>The rows hidden because nothing changed in them — the toggle's caption.</summary>
    public string ShowAllLabel => $"All versions ({_versions.Count + _skipped.Count})";

    public bool CanCopy => Selected?.Revision is { Present: true };

    /// <summary>
    /// Off for the whole document — restoring that would also undo changes nobody meant to undo — and
    /// off when the version already matches what the editor holds.
    /// </summary>
    public bool CanRestore =>
        CanEdit && !WholeDocument && Path.Length > 0 &&
        Selected?.Revision is { } revision &&
        !string.Equals(revision.Text, _currentText(Path), StringComparison.Ordinal);

    public string RestoreLabel => Selected?.Revision is { Present: false }
        ? $"Remove (absent in v{Selected.Version})"
        : Selected is { } row ? $"Restore v{row.Version}" : "Restore";

    /// <summary>Reads the version list and every readable version, newest first. Called once, when the panel opens.</summary>
    public Task Load() => Loading = LoadAsync();

    private async Task LoadAsync()
    {
        if (_reader.Unavailable(Tier) is { } reason)
        {
            Status = reason;
            return;
        }

        IsBusy = true;
        var token = _work.Token;

        try
        {
            Status = $"Reading the version list of {Tier.Id}…";
            var metadata = await _reader.ListAsync(Tier, token);
            _currentInVault = metadata.CurrentVersion;

            var now = DateTimeOffset.Now;
            var readable = metadata.Versions.Where(v => v.Readable(now)).Take(MaxVersionsRead).ToArray();
            var skipped = metadata.Versions
                .Where(v => !v.Readable(now))
                .Select(v => (v.Version, v.Unreadable(now)!))
                .ToList();

            var read = new List<SecretVersion>();
            for (var i = 0; i < readable.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                Status = $"Reading version {i + 1} of {readable.Length} (v{readable[i].Version})…";

                try
                {
                    read.Add(await _reader.ReadAsync(Tier, readable[i].Version, token));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    skipped.Add((readable[i].Version, ex.Message));
                }
            }

            _versions = read;
            _skipped = skipped;

            var note = DiffSideVm.Describe(metadata);
            var readableCount = metadata.Versions.Count(v => v.Readable(now));
            if (readableCount > MaxVersionsRead)
            {
                note += $" Only the newest {MaxVersionsRead} were read.";
            }

            Status = note;
            OnPropertyChanged(nameof(ShowAllLabel));
            Rebuild();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnNodePathChanged(string value)
    {
        if (!WholeDocument)
        {
            Rebuild();
        }
    }

    partial void OnWholeDocumentChanged(bool value)
    {
        OnPropertyChanged(nameof(NodeScope));
        Rebuild();
    }

    /// <summary>The other half of the Node | Whole document toggle, for a two-button segment.</summary>
    public bool NodeScope
    {
        get => !WholeDocument;
        set => WholeDocument = !value;
    }

    partial void OnShowAllChanged(bool value) => Rebuild();

    partial void OnCompareWithChanged(HistoryCompare value)
    {
        OnPropertyChanged(nameof(ComparePrevious));
        RebuildDiff();
    }

    /// <summary><see cref="CompareWith"/> as the switch both front ends bind to.</summary>
    public bool ComparePrevious
    {
        get => CompareWith == HistoryCompare.Previous;
        set => CompareWith = value ? HistoryCompare.Previous : HistoryCompare.Current;
    }

    /// <summary>The diff's direction, for the line above it: "V31 → CURRENT".</summary>
    public string DiffHeading => LeftHeader.Length == 0 ? string.Empty : $"{LeftHeader}  →  {RightHeader}";

    partial void OnLeftHeaderChanged(string value) => OnPropertyChanged(nameof(DiffHeading));

    partial void OnRightHeaderChanged(string value) => OnPropertyChanged(nameof(DiffHeading));

    partial void OnSelectedChanged(RevisionRowVm? value) => RebuildDiff();

    /// <summary>The editor's document changed — an edit, an undo, a restore — so "current" moved.</summary>
    public void CurrentChanged()
    {
        RebuildDiff();
        OnPropertyChanged(nameof(CanRestore));
    }

    /// <summary>↑/↓ through the enabled rows, the way the list itself moves.</summary>
    [RelayCommand]
    private void Move(int delta)
    {
        var enabled = Rows.Where(r => r.IsEnabled).ToList();
        if (enabled.Count == 0)
        {
            return;
        }

        var at = Selected is null ? -1 : enabled.IndexOf(Selected);
        var next = Math.Clamp(at + delta, 0, enabled.Count - 1);
        Selected = enabled[next];
    }

    [RelayCommand]
    private void Copy()
    {
        if (Selected?.Revision is not { Text: { } text })
        {
            return;
        }

        try
        {
            JsonInsight.Platform.Platform.Clipboard.SetText(text);
            Note = $"Copied {Describe(Path)} as it was in v{Selected.Version} — {text.Length:N0} characters.";
        }
        catch (Exception ex)
        {
            Note = $"Could not copy: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Restore()
    {
        if (!CanRestore || Selected?.Revision is not { } revision)
        {
            return;
        }

        var problem = _restore(Path, revision.Text);
        Note = problem ?? (revision.Present
            ? $"Restored {Path} to v{revision.Version}. Nothing has been written yet — push it from the title bar."
            : $"Removed {Path}, which v{revision.Version} did not have. Nothing has been written yet — push it from the title bar.");

        CurrentChanged();
    }

    private void Rebuild()
    {
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(CanRestore));

        var keep = Selected?.Version;
        Rows.Clear();

        var now = DateTimeOffset.Now;
        var revisions = NodeHistory.Build(Path, _versions);

        var rows = revisions
            .Where(r => ShowAll || r.IsChangePoint)
            .Select(r => new RevisionRowVm(
                r.Version,
                When(r.CreatedTime),
                Age(r.CreatedTime, now),
                ChangeText(r.Change),
                r.Preview,
                r.Version == _currentInVault,
                r,
                null))
            .Concat(_skipped.Select(s => new RevisionRowVm(
                s.Version, string.Empty, string.Empty, s.Reason, string.Empty, s.Version == _currentInVault, null, s.Reason)))
            .OrderByDescending(r => r.Version);

        foreach (var row in rows)
        {
            Rows.Add(row);
        }

        // Keep the selected version across a scope or selection change when it is still listed;
        // otherwise the newest version that changed something, which is the usual first question.
        Selected = Rows.FirstOrDefault(r => r.Version == keep && r.IsEnabled)
                   ?? Rows.FirstOrDefault(r => r.IsEnabled && r.Version != _currentInVault)
                   ?? Rows.FirstOrDefault(r => r.IsEnabled);

        if (Selected is null)
        {
            RebuildDiff();
        }
    }

    private void RebuildDiff()
    {
        Lines.Clear();

        if (Selected?.Revision is not { } revision)
        {
            LeftHeader = RightHeader = string.Empty;
            DiffSummary = _versions.Count == 0 ? string.Empty : "Pick a version.";
            return;
        }

        string? left, right;

        if (CompareWith == HistoryCompare.Previous)
        {
            var previous = NodeHistory.Build(Path, _versions.Where(v => v.Version < revision.Version))
                .FirstOrDefault();

            left = previous?.Text;
            right = revision.Text;
            LeftHeader = previous is null ? "NO EARLIER VERSION" : $"V{previous.Version}";
            RightHeader = $"V{revision.Version}";
        }
        else
        {
            left = revision.Text;
            right = _currentText(Path);
            LeftHeader = $"V{revision.Version}";
            RightHeader = Tier.IsEdited || Tier.VaultVersion != _currentInVault ? "ON SCREEN" : "CURRENT";
        }

        // A node is short, so it is shown whole; the whole document only by its changed lines, or the
        // two that moved are a scroll through hundreds that did not.
        var diff = DiffLineVm.Build(left ?? string.Empty, right ?? string.Empty, includeUnchanged: !WholeDocument);
        foreach (var line in diff.Lines)
        {
            Lines.Add(line);
        }

        DiffSummary = (left, right) switch
        {
            (null, null) => $"{Describe(Path)} is in neither.",
            (null, _) => $"{Describe(Path)} is not in {LeftHeader.ToLowerInvariant()}.",
            (_, null) => $"{Describe(Path)} is not in {RightHeader.ToLowerInvariant()}.",
            _ when diff.Added + diff.Removed + diff.Modified == 0 => "Identical.",
            _ => diff.Counts,
        };
    }

    private static string Describe(string path) => path.Length == 0 ? "the whole document" : path;

    private static string When(DateTimeOffset? created) => created is { } at
        ? $"{at.ToLocalTime():yyyy-MM-dd HH:mm}"
        : string.Empty;

    private static string Age(DateTimeOffset? created, DateTimeOffset now) =>
        created is null ? string.Empty : Elapsed.Since(created, now);

    private static string ChangeText(Change change) => change switch
    {
        Change.FirstKept => "first kept",
        Change.Added => "added",
        Change.Changed => "changed",
        Change.Removed => "removed",
        Change.Unchanged => "unchanged",
        _ => "absent",
    };

    public void Dispose()
    {
        _work.Cancel();
        _work.Dispose();
        _versions = [];
        Rows.Clear();
        Lines.Clear();
    }
}
