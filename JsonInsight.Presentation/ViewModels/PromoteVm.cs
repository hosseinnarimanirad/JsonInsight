using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JsonInsight.Loading;
using JsonInsight.Model;
using JsonInsight.Promote;

namespace JsonInsight.ViewModels;

public sealed partial class PromoteLeafVm : ObservableObject
{
    [ObservableProperty]
    private PromotionAction _action;

    public required PromotionLeaf Leaf { get; init; }

    public required string DestinationTierId { get; init; }

    public string Path => Leaf.Path;

    public string ClassName => Leaf.Class.ToString().ToLowerInvariant();

    public string Reason => Leaf.Reason;

    public string ResultDisplay => Action switch
    {
        PromotionAction.CopyVerbatim => Leaf.SourceDisplay,
        PromotionAction.CopyPlaceholder => PromotionPlan.Placeholder(DestinationTierId),
        _ => "(not created)",
    };

    partial void OnActionChanged(PromotionAction value)
    {
        Leaf.Action = value;
        OnPropertyChanged(nameof(ResultDisplay));
    }
}

public sealed partial class PromoteVm : ObservableObject
{
    private readonly MainVm _main;
    private readonly Flattener _flattener;

    /// <summary>The window needs it to build the push screen; nothing here opens a window itself.</summary>
    public MainVm Main => _main;

    [ObservableProperty]
    private TierDocument? _destination;

    [ObservableProperty]
    private string? _message;

    public ObservableCollection<PromoteLeafVm> Leaves { get; } = [];

    public ObservableCollection<DiffLineVm> PreviewLines { get; } = [];

    public ObservableCollection<TierDocument> Destinations { get; } = [];

    public TierDocument Source { get; }

    public string RootPath { get; }

    public string Title => $"Promote {RootPath}";

    public string Subtitle => $"from {Source.Id} into a tier that does not have it";

    /// <summary>
    /// Whether this plan can go to the push screen, which is where the destination tier's name is
    /// typed out and where the comparison against the live secret is made.
    /// </summary>
    public bool CanPush => Leaves.Count > 0 && Destination is { Writable: true };

    public PromoteVm(MainVm main, Flattener flattener, TierDocument source, string rootPath, IEnumerable<string> missingFrom)
    {
        _main = main;
        _flattener = flattener;
        Source = source;
        RootPath = rootPath;

        // Only writable tiers that actually lack this subtree are offered. dev never appears here:
        // it is marked read-only because writing it would destroy its 119 comment lines.
        foreach (var document in main.Documents.Where(d =>
                     d.Writable && missingFrom.Contains(d.Id, StringComparer.OrdinalIgnoreCase)))
        {
            Destinations.Add(document);
        }

        Destination = Destinations.FirstOrDefault();
    }

    partial void OnDestinationChanged(TierDocument? value)
    {
        PreviewLines.Clear();
        BuildPlan();
    }

    private void BuildPlan()
    {
        Leaves.Clear();
        if (Destination is null)
        {
            Message = "No writable tier is missing this subtree.";
            return;
        }

        try
        {
            var plan = PromotionPlanner.Plan(Source, Destination, RootPath);
            foreach (var leaf in plan.Leaves)
            {
                Leaves.Add(new PromoteLeafVm
                {
                    Leaf = leaf,
                    DestinationTierId = Destination.Id,
                    Action = leaf.DefaultAction,
                });
            }

            Message = $"{plan.Leaves.Count} keys. Defaults: {plan.VerbatimCount} copied, " +
                      $"{plan.PlaceholderCount} created with a placeholder value.";
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }

        OnPropertyChanged(nameof(CanPush));
    }

    [RelayCommand]
    private void Preview()
    {
        PreviewLines.Clear();

        if (Destination is null)
        {
            return;
        }

        string before, after;
        try
        {
            before = OrdinalJsonWriter.SerializeToText(Destination.Root);
            after = OrdinalJsonWriter.SerializeToText(BuildUpdated()!);
        }
        catch (Exception ex)
        {
            Message = $"Could not build the promoted document: {ex.Message}";
            return;
        }

        // Only the rows that differ: what is being reviewed here is the subtree being copied in, and
        // the destination's untouched keys are not it.
        foreach (var line in DiffLineVm.Build(before, after, includeUnchanged: false).Lines)
        {
            PreviewLines.Add(line);
        }

        Message = PreviewLines.Count == 0
            ? "The destination would not change."
            : $"{PreviewLines.Count} changed line(s), against {Destination.Id} as it was read. " +
              "Push compares against what that source holds at that moment.";

        OnPropertyChanged(nameof(CanPush));
    }

    /// <summary>The destination document with this plan applied, for the push screen to send.</summary>
    public JsonNode? BuildUpdated()
    {
        if (Destination is null)
        {
            return null;
        }

        try
        {
            return PromotionPlanner.Apply(Destination, Source, CurrentPlan());
        }
        catch (Exception ex)
        {
            Message = $"Could not build the promoted document: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// The push this screen would hand on, or null when there is nothing to hand on. Asked instead of
    /// spelling out "if there is a destination and the document builds" at each of the two hosts'
    /// buttons.
    /// </summary>
    public PendingPush? PendingPush() =>
        Destination is { } destination && BuildUpdated() is { } updated
            ? new PendingPush(destination, updated, What)
            : null;

    /// <summary>One phrase naming this promote, carried onto the push screen.</summary>
    public string What => $"{RootPath} promoted from {Source.Id} ({Leaves.Count} key(s))";

    private PromotionPlan CurrentPlan()
    {
        var plan = PromotionPlanner.Plan(Source, Destination!, RootPath);
        foreach (var leaf in plan.Leaves)
        {
            var chosen = Leaves.FirstOrDefault(l =>
                string.Equals(l.Path, leaf.Path, StringComparison.Ordinal));
            if (chosen is not null)
            {
                leaf.Action = chosen.Action;
            }
        }

        return plan;
    }
}
