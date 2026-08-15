using System.Text.Json.Nodes;
using JsonInsight.Diff;
using JsonInsight.Model;

namespace JsonInsight.Promote;

/// <summary>
/// Works out what promoting a subtree from one tier into another would do, and applies it to a
/// node tree. It never writes anything back - the write path (<c>VaultPusher</c> and
/// <c>LocalFileSourceProvider</c>) owns that.
/// </summary>
public static class PromotionPlanner
{
    public static PromotionPlan Plan(TierDocument source, TierDocument destination, string rootPath)
    {
        if (!destination.Writable)
        {
            throw new InvalidOperationException(
                $"Tier '{destination.Id}' is marked read-only in tiers.json and cannot be written.");
        }

        var leaves = source.Flat.Subtree(rootPath)
            .OrderBy(l => l.Path, StringComparer.Ordinal)
            .Select(leaf =>
            {
                var (action, reason) = Decide(leaf, destination.Id);
                return new PromotionLeaf
                {
                    Source = leaf,
                    DefaultAction = action,
                    Action = action,
                    Reason = reason,
                };
            })
            .ToArray();

        if (leaves.Length == 0)
        {
            throw new InvalidOperationException($"'{rootPath}' has no keys in tier '{source.Id}'.");
        }

        return new PromotionPlan
        {
            SourceTierId = source.Id,
            DestinationTierId = destination.Id,
            RootPath = rootPath,
            Leaves = leaves,
        };
    }

    private static (PromotionAction Action, string Reason) Decide(Leaf leaf, string destinationTierId) =>
        leaf.Class switch
        {
            ValueClass.Secret => (PromotionAction.CopyPlaceholder,
                $"Secret. The value is never copied between tiers; set it in {destinationTierId} directly."),

            ValueClass.Infra => (PromotionAction.CopyPlaceholder,
                $"Deployment-specific. Creating the key makes {destinationTierId} structurally complete, " +
                "but the value belongs to a different environment."),

            _ => (PromotionAction.CopyVerbatim,
                "Business constant - expected to be identical in every tier."),
        };

    /// <summary>
    /// Applies the plan to a copy of the destination tree and returns it. The original tree is not
    /// modified, so a preview can be produced and then discarded without consequence.
    /// </summary>
    public static JsonNode Apply(TierDocument destination, TierDocument source, PromotionPlan plan)
    {
        // Live, so a promote stacks onto whatever the destination has already been edited to rather
        // than being computed against the state it was read in and reverting it.
        var updated = destination.Live.DeepClone();

        foreach (var leaf in plan.Included)
        {
            var parentPath = ConfigPath.Parent(leaf.Path);
            var key = ConfigPath.Last(leaf.Path);

            if (key.Contains('['))
            {
                throw new InvalidOperationException(
                    $"'{leaf.Path}' names an array element. Promote inserts object keys only.");
            }

            var parent = JsonNavigator.EnsureObject(updated, parentPath);

            parent[key] = leaf.Action == PromotionAction.CopyPlaceholder
                ? JsonValue.Create(PromotionPlan.Placeholder(plan.DestinationTierId))
                : CloneSourceValue(source, leaf);
        }

        return updated;
    }

    /// <summary>
    /// Copies the value straight from the source document rather than re-encoding
    /// <see cref="Leaf.Value"/>, so numbers keep their exact literal form and strings keep every
    /// character they had.
    /// </summary>
    private static JsonNode? CloneSourceValue(TierDocument source, PromotionLeaf leaf)
    {
        // Live for the same reason: what is being promoted is what the source says now, which is what
        // the grid showed when the row was picked.
        var node = JsonNavigator.Find(source.Live, leaf.Path);
        return node?.DeepClone();
    }
}
