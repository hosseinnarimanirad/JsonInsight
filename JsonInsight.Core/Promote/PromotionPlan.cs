using System.Text.Json;
using System.Text.Json.Nodes;
using JsonInsight.Classify;
using JsonInsight.Model;

namespace JsonInsight.Promote;

public enum PromotionAction
{
    /// <summary>Copy the source value as-is. The default for business constants.</summary>
    CopyVerbatim,

    /// <summary>Create the key with a loud sentinel value. The default for infra and secrets.</summary>
    CopyPlaceholder,

    /// <summary>Leave the destination without this key.</summary>
    Skip,
}

/// <summary>One key being promoted, and what will happen to it.</summary>
public sealed class PromotionLeaf
{
    public required Leaf Source { get; init; }

    public required PromotionAction DefaultAction { get; init; }

    public PromotionAction Action { get; set; }

    public required string Reason { get; init; }

    public string Path => Source.Path;

    public ValueClass Class => Source.Class;

    /// <summary>What the user is allowed to see. Secrets are described, never revealed.</summary>
    public string SourceDisplay => Source.Class == ValueClass.Secret
        ? SecretMasker.Describe(Source.ComparableValue)
        : Source.ComparableValue;

    public string ResultDisplay(string destinationTierId) => Action switch
    {
        PromotionAction.CopyVerbatim => SourceDisplay,
        PromotionAction.CopyPlaceholder => PromotionPlan.Placeholder(destinationTierId),
        _ => "(not created)",
    };
}

public sealed class PromotionPlan
{
    public required string SourceTierId { get; init; }

    public required string DestinationTierId { get; init; }

    /// <summary>The subtree root being promoted, e.g. AccountSettings:NightlyApprovalJob.</summary>
    public required string RootPath { get; init; }

    public required IReadOnlyList<PromotionLeaf> Leaves { get; init; }

    public IEnumerable<PromotionLeaf> Included => Leaves.Where(l => l.Action != PromotionAction.Skip);

    public int PlaceholderCount => Leaves.Count(l => l.Action == PromotionAction.CopyPlaceholder);

    public int VerbatimCount => Leaves.Count(l => l.Action == PromotionAction.CopyVerbatim);

    public int SkippedCount => Leaves.Count(l => l.Action == PromotionAction.Skip);

    /// <summary>
    /// A distinctive, greppable sentinel. Deliberately not "" - an empty string is a valid,
    /// deliberate value in these files, so blanking would be indistinguishable from a forgotten key.
    /// This fails loudly at startup instead.
    /// </summary>
    public static string Placeholder(string destinationTierId) => $"<<SET-FOR-{destinationTierId}>>";
}
