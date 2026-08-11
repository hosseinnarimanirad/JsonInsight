using System.Text.Json;
using JsonInsight.Model;

namespace JsonInsight.Diff;

public enum DiffKind
{
    Same,

    /// <summary>Present in the left tier, absent from the right one.</summary>
    OnlyInLeft,

    /// <summary>Present in the right tier, absent from the left one.</summary>
    OnlyInRight,

    ValueDiffers,

    /// <summary>Same path, different JSON type - e.g. 5000 in one tier and "5000" in another.</summary>
    TypeDiffers,

    /// <summary>Set-valued leaf whose members differ.</summary>
    SetDiffers,

    /// <summary>Declared equivalent in purpose but structurally incomparable. See config/aliases.json.</summary>
    ShapeDiffers,
}

public sealed record DiffEntry
{
    public required string Path { get; init; }

    public required DiffKind Kind { get; init; }

    public Leaf? Left { get; init; }

    public Leaf? Right { get; init; }

    /// <summary>Explains a ShapeDiffers or SetDiffers row; null for the simple kinds.</summary>
    public string? Detail { get; init; }

    public ValueClass Class => Left?.Class ?? Right?.Class ?? ValueClass.Business;

    public bool IsDifference => Kind != DiffKind.Same;

    /// <summary>True when the difference is expected because the value is deployment-specific.</summary>
    public bool IsExpected => IsDifference && Class == ValueClass.Infra;

    /// <summary>True when the difference matters: a value that should be identical everywhere is not.</summary>
    public bool IsMeaningful => IsDifference && Class != ValueClass.Infra;

    public static DiffEntry Compare(string path, Leaf? left, Leaf? right)
    {
        if (left is null && right is null)
        {
            throw new ArgumentException($"Nothing to compare at '{path}'.");
        }

        if (right is null)
        {
            return new DiffEntry { Path = path, Kind = DiffKind.OnlyInLeft, Left = left };
        }

        if (left is null)
        {
            return new DiffEntry { Path = path, Kind = DiffKind.OnlyInRight, Right = right };
        }

        if (left.IsSet || right.IsSet)
        {
            return CompareSets(path, left, right);
        }

        if (!SameJsonType(left.Kind, right.Kind))
        {
            return new DiffEntry
            {
                Path = path,
                Kind = DiffKind.TypeDiffers,
                Left = left,
                Right = right,
                Detail = $"{JsonKinds.Describe(left.Kind)} vs {JsonKinds.Describe(right.Kind)}",
            };
        }

        return new DiffEntry
        {
            Path = path,
            Kind = string.Equals(left.Value, right.Value, StringComparison.Ordinal)
                ? DiffKind.Same
                : DiffKind.ValueDiffers,
            Left = left,
            Right = right,
        };
    }

    private static DiffEntry CompareSets(string path, Leaf left, Leaf right)
    {
        if (!left.IsSet || !right.IsSet)
        {
            return new DiffEntry
            {
                Path = path,
                Kind = DiffKind.TypeDiffers,
                Left = left,
                Right = right,
                Detail = $"{(left.IsSet ? "set" : JsonKinds.Describe(left.Kind))} vs " +
                         $"{(right.IsSet ? "set" : JsonKinds.Describe(right.Kind))}",
            };
        }

        var onlyLeft = left.SetMembers!.Except(right.SetMembers!, StringComparer.Ordinal).ToArray();
        var onlyRight = right.SetMembers!.Except(left.SetMembers!, StringComparer.Ordinal).ToArray();

        if (onlyLeft.Length == 0 && onlyRight.Length == 0)
        {
            // Order is not semantic for these arrays, so a reordering is not a change.
            return new DiffEntry { Path = path, Kind = DiffKind.Same, Left = left, Right = right };
        }

        var parts = new List<string>();
        if (onlyLeft.Length > 0)
        {
            parts.Add($"only left: {string.Join(", ", onlyLeft)}");
        }

        if (onlyRight.Length > 0)
        {
            parts.Add($"only right: {string.Join(", ", onlyRight)}");
        }

        return new DiffEntry
        {
            Path = path,
            Kind = DiffKind.SetDiffers,
            Left = left,
            Right = right,
            Detail = string.Join("; ", parts),
        };
    }

    /// <summary>
    /// True and False are separate <see cref="JsonValueKind"/> members but the same JSON type.
    /// Comparing the members directly reported every flipped boolean — <c>true</c> in one tier,
    /// <c>false</c> in another — as a type difference, which reads as a binding fault rather than
    /// what it is: a setting deliberately turned off somewhere.
    /// </summary>
    private static bool SameJsonType(JsonValueKind left, JsonValueKind right) =>
        left == right || (IsBoolean(left) && IsBoolean(right));

    private static bool IsBoolean(JsonValueKind kind) =>
        kind is JsonValueKind.True or JsonValueKind.False;
}
