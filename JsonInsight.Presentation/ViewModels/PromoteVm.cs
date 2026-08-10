using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
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

    public string SourceDisplay => Leaf.SourceDisplay;

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

    [ObservableProperty]
    private bool _previewReady;

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
        PreviewReady = false;
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
        PreviewReady = false;

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

        var model = SideBySideDiffBuilder.Instance.BuildDiffModel(before, after, false);
        for (var i = 0; i < Math.Max(model.OldText.Lines.Count, model.NewText.Lines.Count); i++)
        {
            var oldLine = i < model.OldText.Lines.Count ? model.OldText.Lines[i] : null;
            var newLine = i < model.NewText.Lines.Count ? model.NewText.Lines[i] : null;
            var type = newLine?.Type ?? oldLine?.Type ?? ChangeType.Unchanged;

            if (type == ChangeType.Unchanged)
            {
                continue;
            }

            PreviewLines.Add(new DiffLineVm(
                oldLine?.Position?.ToString() ?? string.Empty,
                oldLine?.Text ?? string.Empty,
                newLine?.Position?.ToString() ?? string.Empty,
                newLine?.Text ?? string.Empty,
                type));
        }

        PreviewReady = true;
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

    /// <summary>One phrase naming this promote, carried onto the push screen.</summary>
    public string What => $"{RootPath} promoted from {Source.Id} ({Leaves.Count} key(s))";

    /// <summary>
    /// The keys that would go in carrying a placeholder rather than a real value, listed after a
    /// push because they are the ones that will fail loudly at startup until somebody sets them.
    /// </summary>
    public IReadOnlyList<string> Placeholders =>
        Leaves.Where(l => l.Action == PromotionAction.CopyPlaceholder).Select(l => l.Path).ToArray();

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
