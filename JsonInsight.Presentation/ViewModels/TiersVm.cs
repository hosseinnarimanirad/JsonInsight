using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JsonInsight.Diff;
using JsonInsight.Editing;
using JsonInsight.Loading;
using JsonInsight.Model;
using JsonInsight.Vault;

namespace JsonInsight.ViewModels;

/// <summary>One line in the tier grid: either a rolled-up subtree or a single key.</summary>
public sealed partial class TierRowVm : ObservableObject
{
    [ObservableProperty]
    private bool _isExpanded;

    public required DiffNode Node { get; init; }

    public required int Depth { get; init; }

    public required IReadOnlyList<MultiCell> Cells { get; init; }

    public required bool IsGroup { get; init; }

    public string Path => Node.Path;

    public string Label => Node.Segment;

    public double Indent => Depth * 14.0;

    /// <summary>"11 keys — only in dev" for a rolled-up subtree; empty for a single key.</summary>
    public string Summary { get; init; } = string.Empty;

    public string? Detail => Node.Row?.Detail;

    public ValueClass Class => Node.Row?.Class ?? ValueClass.Business;

    public bool IsSecret => Class == ValueClass.Secret;

    public bool HasShape => Node.Row?.AnyShape ?? Node.HasShapeDifference;

    /// <summary>
    /// A row can be promoted when its whole subtree is missing from at least one writable tier.
    /// This is precisely the rolled-up node, which is why the rollup and the promote unit are the
    /// same concept rather than two similar ones.
    /// </summary>
    public bool CanPromote => Node.IsUniformlyMissing;

    /// <summary>
    /// Anything with real leaves under it can be edited: a single key directly, a subtree as a
    /// delete. The alias shape rows are the exception — they describe two structurally different
    /// things being the same concept, and there is no single path an edit could act on.
    /// </summary>
    public bool CanEdit => Node.LeafCount > 0 && !HasShape;

    public IReadOnlyList<string> MissingFrom => Node.UniformlyMissingFrom ?? [];

    /// <summary>Every real leaf path under this row, which is what an edit or a delete acts on.</summary>
    public IReadOnlyList<string> LeafPaths =>
        Node.DescendantsAndSelf()
            .Where(n => n.Row is not null && n.Children.Count == 0)
            .Select(n => n.Path)
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>Set by the view model when the pending change set touches this row.</summary>
    [ObservableProperty]
    private bool _hasPendingEdit;

    public string Glyph => IsGroup ? (IsExpanded ? "▾" : "▸") : string.Empty;
}

public sealed partial class TiersVm : ObservableObject
{
    private readonly MainVm _main;
    private readonly AliasSet _aliases;
    private readonly Flattener _flattener;
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expandedRollups = new(StringComparer.Ordinal);

    private IReadOnlyList<TierDocument> _documents;
    private IReadOnlyList<TierUnavailable> _unavailable;
    private DiffNode _root;

    [ObservableProperty]
    private string _filter = string.Empty;

    /// <summary>Off by default: the ~350 rows that agree everywhere are not what anyone opened this for.</summary>
    [ObservableProperty]
    private bool _showIdentical;

    /// <summary>
    /// On by default, but counted separately. Hiding deployment-specific differences entirely would
    /// mean a misclassified key could disappear forever.
    /// </summary>
    [ObservableProperty]
    private bool _showExpected = true;

    [ObservableProperty]
    private string? _sectionFilter;

    [ObservableProperty]
    private TierRowVm? _selectedRow;

    /// <summary>
    /// True while this tab's pull is in flight, and only a re-entry guard — nothing binds it, so it
    /// is a plain bool rather than an observable one. What the button disables itself on is
    /// <see cref="MainVm.VaultBusy"/>, which the startup read raises too.
    /// </summary>
    private bool Busy { get; set; }

    /// <summary>The result of the last pull, one line per tier. Empty until one has run.</summary>
    public ObservableCollection<string> PullReport { get; } = [];

    [ObservableProperty]
    private string _pullStatus = string.Empty;

    public ObservableCollection<TierRowVm> Rows { get; } = [];

    public ObservableCollection<SectionVm> Sections { get; } = [];

    public IReadOnlyList<TierDocument> Documents => _documents;

    /// <summary>
    /// One column per configured tier, in the order tiers.json gives them, whether or not Vault
    /// answered for it. A tier that could not be read is a column of unknowns rather than an absent
    /// column: dropping it would quietly turn a four-way comparison into a three-way one.
    /// </summary>
    private IReadOnlyList<TierColumn> Columns()
    {
        var columns = Compared(_documents).Select(d => new TierColumn(d.Id, d.Flat)).ToList();
        columns.AddRange(Compared(_unavailable).Select(u => new TierColumn(u.Id, null)));
        return columns;
    }

    /// <summary>
    /// Narrows a loaded set to the columns this grid compares.
    ///
    /// <para>
    /// Everything configured is read — see <see cref="MainVm.Compared"/> — so the Tier editor and the
    /// Text diff can reach a fifth environment without one being re-ticked. This grid is four columns
    /// wide, so it shows what was ticked and nothing else; a document that is loaded but not compared
    /// is not missing, it is simply not one of the four being asked about.
    /// </para>
    /// </summary>
    private IEnumerable<T> Compared<T>(IEnumerable<T> loaded) where T : notnull
    {
        var compared = _main.Compared;

        return compared.Count == 0
            ? loaded
            : loaded.Where(item => compared.Contains(Identify(item), StringComparer.OrdinalIgnoreCase));
    }

    private static string Identify<T>(T item) => item switch
    {
        TierDocument document => document.Id,
        TierUnavailable unavailable => unavailable.Id,
        _ => string.Empty,
    };

    /// <summary>Why a tier has no values, for its column header. Empty for one that was read.</summary>
    public string? UnavailableReason(string tierId) =>
        _unavailable.FirstOrDefault(u => u.Id.Equals(tierId, StringComparison.OrdinalIgnoreCase))?.Reason;

    public MultiDiff Diff { get; private set; }

    public string ShownLabel => $"{Rows.Count} rows shown";

    /// <summary>Whether anything is held in memory that no source has been told about yet.</summary>
    public bool HasPendingEdits => _main.Store.ModifiedTiers.Count > 0;

    public string PendingLabel
    {
        get
        {
            var tiers = _main.Store.ModifiedTiers;
            if (tiers.Count == 0)
            {
                return "no unsaved changes";
            }

            var keys = _main.Store.ChangedPaths().Count(p => p.Length > 0);
            return $"{keys} unsaved change(s) on {string.Join(", ", tiers)}";
        }
    }

    public string SourceLabel
    {
        get
        {
            var read = _documents.Count;
            var missing = _unavailable.Count;

            if (read == 0)
            {
                return missing == 0 ? "nothing loaded yet" : $"no tier could be read ({missing} unavailable)";
            }

            var head = missing == 0
                ? $"{read} tier(s) live from Vault or disk"
                : $"{read} live from Vault or disk, {missing} unavailable";

            // More is read than is compared once a fifth environment is configured. Saying only the
            // read count beside a four-column grid would leave the fifth looking like it failed.
            var compared = Diff.TierIds.Count;

            return compared < read + missing
                ? $"{head} — {compared} compared here, the rest on the Tier editor and Text diff"
                : head;
        }
    }

    public TiersVm(
        MainVm main,
        IReadOnlyList<TierDocument> documents,
        IReadOnlyList<TierUnavailable> unavailable,
        AliasSet aliases,
        Flattener flattener)
    {
        _main = main;
        _documents = documents;
        _unavailable = unavailable;
        _aliases = aliases;
        _flattener = flattener;

        Diff = MultiDiff.Build(Columns(), aliases);
        _root = DiffNode.Build(Diff);

        BuildSections();
        Rebuild();
    }

    partial void OnFilterChanged(string value) => Rebuild();

    partial void OnShowIdenticalChanged(bool value) => Rebuild();

    partial void OnShowExpectedChanged(bool value) => Rebuild();

    partial void OnSectionFilterChanged(string? value) => Rebuild();

    [RelayCommand]
    private void ToggleRow(TierRowVm? row)
    {
        if (row is null || !row.IsGroup)
        {
            return;
        }

        if (!_collapsed.Remove(row.Path))
        {
            _collapsed.Add(row.Path);
        }

        Rebuild();
    }

    [RelayCommand]
    private void ExpandAll()
    {
        _collapsed.Clear();
        Rebuild();
    }

    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var node in _root.DescendantsAndSelf().Where(n => n.Children.Count > 0 && n.Depth > 0))
        {
            _collapsed.Add(node.Path);
        }

        Rebuild();
    }

    [RelayCommand]
    private void ClearSection() => SectionFilter = null;

    /// <summary>
    /// Reads every tier again and rebuilds the grid. Nothing is written: a pull replaces what is in
    /// memory, and what is in memory is all there is.
    /// </summary>
    /// <summary>
    /// Whether the next Pull press is the one that goes ahead and discards. See
    /// <see cref="PullFromVaultAsync"/>; reset by anything that resolves the question.
    /// </summary>
    private bool _confirmingDiscard;

    [RelayCommand]
    private async Task PullFromVaultAsync()
    {
        if (Busy)
        {
            return;
        }

        // A pull re-reads every source and replaces what is in memory, so unsaved edits go with it.
        // Asked rather than assumed, and asked the way the Delete button on the projects screen asks
        // — a second press rather than a dialog, so the same code serves both front ends.
        if (!_confirmingDiscard && _main.Store.ModifiedTiers is { Count: > 0 } modified)
        {
            _confirmingDiscard = true;
            Report($"{string.Join(", ", modified)} " +
                   $"{(modified.Count == 1 ? "has" : "have")} unsaved changes that a pull would discard. " +
                   "Push first to keep them, or press Pull again to re-read and throw them away.");
            return;
        }

        _confirmingDiscard = false;

        await BusyGuard.RunAsync(
            busy => Busy = busy,
            async () =>
            {
                PullReport.Clear();
                Report("Reading every source…");

                var report = await _main.RefreshFromVaultAsync();

                if (report is null)
                {
                    Report("Nothing to pull — no tier names a vaultPath in tiers.json.");
                    return;
                }

                foreach (var line in report.Lines)
                {
                    PullReport.Add(line.Text);
                }

                Report(report.Summary);
            },
            ex => Report($"Pull failed: {ex.Message}"));
    }

    /// <summary>
    /// The pull button lives in the title bar, so it can be pressed from any tab — but LAST PULL is
    /// only visible on this one. The outcome goes to the status bar as well, so pressing it from the
    /// Sources tab does not look like nothing happened.
    /// </summary>
    private void Report(string text)
    {
        PullStatus = text;
        _main.Status = text;
    }

    public void Rebuild()
    {
        Rows.Clear();
        Emit(_root, 0);

        MarkPendingRows();

        OnPropertyChanged(nameof(ShownLabel));
        OnPropertyChanged(nameof(PendingLabel));
        OnPropertyChanged(nameof(HasPendingEdits));
    }

    /// <summary>Rebuilds from the given documents after a push or a Vault read.</summary>
    public void Refresh(IReadOnlyList<TierDocument> documents, IReadOnlyList<TierUnavailable> unavailable)
    {
        _documents = documents;
        _unavailable = unavailable;
        Diff = MultiDiff.Build(Columns(), _aliases);
        _root = DiffNode.Build(Diff);
        BuildSections();
        Rebuild();

        OnPropertyChanged(nameof(Documents));
        OnPropertyChanged(nameof(SourceLabel));
        DocumentsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Raised when the document set has been replaced. The view rebuilds its per-tier columns from
    /// this rather than from DataContextChanged, which fires once and would leave the headers
    /// claiming a local file after a Vault pull swapped the values underneath them.
    /// </summary>
    public event EventHandler? DocumentsChanged;

    /// <summary>Call after anything is edited in memory, so the grid and the toolbar agree with it.</summary>
    public void NotifyEditsChanged()
    {
        MarkPendingRows();
        OnPropertyChanged(nameof(PendingLabel));
        OnPropertyChanged(nameof(HasPendingEdits));
    }

    /// <summary>
    /// Marks the rows holding a value that has been changed in memory and not yet written.
    ///
    /// <para>
    /// The tier is deliberately not part of the question. A row here is one path across every tier,
    /// so "some tier has an unwritten change at this path" is exactly what the mark can honestly say
    /// — which of them it is, is what opening the row shows.
    /// </para>
    /// </summary>
    private void MarkPendingRows()
    {
        var touched = _main.Store.ChangedPaths();

        if (touched.Count == 0)
        {
            foreach (var row in Rows)
            {
                row.HasPendingEdit = false;
            }

            return;
        }

        foreach (var row in Rows)
        {
            row.HasPendingEdit = row.LeafPaths.Any(touched.Contains);
        }
    }

    private void BuildSections()
    {
        Sections.Clear();
        foreach (var node in _root.Children.OrderBy(n => n.Segment, StringComparer.Ordinal))
        {
            var rows = node.LeafRows.ToArray();
            Sections.Add(new SectionVm(
                node.Segment,
                rows.Length,
                rows.Count(r => r.AnyMissing),
                rows.Count(r => r.IsMeaningful)));
        }
    }

    private void Emit(DiffNode parent, int depth)
    {
        foreach (var node in parent.Children.OrderBy(n => n.Segment, StringComparer.Ordinal))
        {
            if (!Include(node))
            {
                continue;
            }

            var isLeafRow = node.Row is not null && node.Children.Count == 0;

            // A subtree missing wholesale from the same tiers is one finding, so it becomes one row.
            var rollup = node.IsUniformlyMissing && node.LeafCount > 1;

            if (isLeafRow && !rollup)
            {
                Rows.Add(new TierRowVm
                {
                    Node = node,
                    Depth = depth,
                    Cells = node.Row!.Cells,
                    IsGroup = false,
                });
                continue;
            }

            var expanded = !_collapsed.Contains(node.Path) && !rollup;

            Rows.Add(new TierRowVm
            {
                Node = node,
                Depth = depth,
                Cells = SummaryCells(node),
                IsGroup = true,
                IsExpanded = expanded,
                Summary = rollup
                    ? $"{node.LeafCount} keys — only in {string.Join(", ", PresentTiers(node))}"
                    : $"{node.LeafCount} keys",
            });

            if (expanded || (rollup && !_collapsed.Contains(node.Path) && _expandedRollups.Contains(node.Path)))
            {
                Emit(node, depth + 1);
            }
        }
    }

    public void ToggleRollup(TierRowVm row)
    {
        if (!_expandedRollups.Remove(row.Path))
        {
            _expandedRollups.Add(row.Path);
        }

        Rebuild();
    }

    /// <summary>
    /// Opening a group row, whichever of the two kinds it is: a rolled-up subtree expands into its own
    /// children, an ordinary group collapses.
    ///
    /// <para>
    /// Asked here rather than decided in each view, because the two views did not decide it the same
    /// way. WPF read <see cref="TierRowVm.CanPromote"/>, which is the actual property; the Blazor tab
    /// searched the row's <see cref="TierRowVm.Summary"/> for the words "only in" — so rewording a
    /// display string would have silently turned every rollup into an ordinary group there and nowhere
    /// else.
    /// </para>
    /// </summary>
    public void ToggleAny(TierRowVm row)
    {
        if (row.CanPromote)
        {
            ToggleRollup(row);
        }
        else
        {
            ToggleRowCommand.Execute(row);
        }
    }

    private IEnumerable<string> PresentTiers(DiffNode node) =>
        Diff.TierIds.Except(node.UniformlyMissingFrom ?? [], StringComparer.Ordinal);

    /// <summary>Cells for a group row: the key count where present, an em dash where absent.</summary>
    private IReadOnlyList<MultiCell> SummaryCells(DiffNode node)
    {
        var missing = node.UniformlyMissingFrom ?? [];
        return Diff.TierIds
            .Select(id => missing.Contains(id, StringComparer.Ordinal)
                ? new MultiCell(id, CellState.Missing, null)
                : new MultiCell(id, CellState.Present, null, $"{node.LeafCount} keys"))
            .ToArray();
    }

    private bool Include(DiffNode node)
    {
        if (SectionFilter is not null && node.Depth >= 1)
        {
            var section = ConfigPath.Split(node.Path)[0];
            if (!section.Equals(SectionFilter, StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (Filter.Length > 0 && !MatchesFilter(node))
        {
            return false;
        }

        var rows = node.LeafRows.ToArray();
        if (rows.Length == 0)
        {
            return false;
        }

        if (!ShowIdentical && rows.All(r => !r.IsDifference))
        {
            return false;
        }

        if (!ShowExpected && rows.All(r => !r.IsDifference || r.IsExpected))
        {
            return ShowIdentical && rows.All(r => !r.IsDifference);
        }

        return true;
    }

    /// <summary>
    /// Substring by default; a glob as soon as the box contains a <c>*</c>. That is how a filter like
    /// <c>ConnectionStrings:Couchbase:Modules:*:Url</c> narrows the grid to exactly the keys a batch
    /// edit is about to act on — the same string then selects them for editing.
    /// </summary>
    private bool MatchesFilter(DiffNode node) =>
        Filter.Contains('*', StringComparison.Ordinal)
            ? node.LeafRows.Any(r => PathGlob.IsMatch(r.Path, Filter))
            : node.LeafRows.Any(r => r.Path.Contains(Filter, StringComparison.OrdinalIgnoreCase));
}

public sealed record SectionVm(string Name, int Keys, int Missing, int Meaningful)
{
    public string Counts => Missing > 0 || Meaningful > 0
        ? $"{Keys}  ({Missing} missing, {Meaningful} differ)"
        : $"{Keys}";

    public bool HasFindings => Missing > 0 || Meaningful > 0;
}
