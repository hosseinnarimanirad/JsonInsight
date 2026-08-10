using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using JsonInsight.Diff;

namespace JsonInsight.Loading;

public enum ArrayKind
{
    /// <summary>No strategy declared. Flattened by index and reported as a warning.</summary>
    Unclassified,

    /// <summary>Array of objects matched on an identity field rather than position.</summary>
    KeyedObjects,

    /// <summary>Array of strings treated as an unordered set.</summary>
    StringSet,
}

public sealed class ArrayStrategy
{
    public required string Pattern { get; init; }

    public required ArrayKind Kind { get; init; }

    public string? IdentityField { get; init; }

    public string? Note { get; init; }
}

/// <summary>
/// Declared array handling, loaded from config/arrays.json.
///
/// Two array shapes exist in these files and each needs different treatment. Serilog:WriteTo is
/// an array of sink objects: matched by index, adding the Seq sink to one tier reads as "index 2
/// added and 0,1 rewritten". Couchbase Scopes are unordered sets of collection names, where a
/// reordering is not a change at all. Anything else is flattened by index and flagged, so a new
/// array shape in a future tier can never be silently mis-diffed.
/// </summary>
public sealed class ArrayStrategies
{
    private readonly List<ArrayStrategy> _strategies;

    private ArrayStrategies(List<ArrayStrategy> strategies) => _strategies = strategies;

    public static ArrayStrategies Load(string? file = null)
    {
        file ??= AppPaths.ConfigFile("arrays.json");
        using var document = JsonDocument.Parse(File.ReadAllText(file), OrdinalJsonOptions);

        var strategies = new List<ArrayStrategy>();
        if (document.RootElement.TryGetProperty("strategies", out var map))
        {
            foreach (var entry in map.EnumerateObject())
            {
                var kind = entry.Value.TryGetProperty("kind", out var k) ? k.GetString() : null;
                strategies.Add(new ArrayStrategy
                {
                    Pattern = entry.Name,
                    Kind = kind switch
                    {
                        "keyedObjects" => ArrayKind.KeyedObjects,
                        "stringSet" => ArrayKind.StringSet,
                        _ => ArrayKind.Unclassified,
                    },
                    IdentityField = entry.Value.TryGetProperty("identityField", out var f) ? f.GetString() : null,
                    Note = entry.Value.TryGetProperty("note", out var n) ? n.GetString() : null,
                });
            }
        }

        // Most specific pattern wins, so a precise path can override a broad one.
        strategies.Sort((a, b) => PathGlob.Specificity(b.Pattern).CompareTo(PathGlob.Specificity(a.Pattern)));
        return new ArrayStrategies(strategies);
    }

    public static ArrayStrategies Empty() => new([]);

    public ArrayStrategy? For(string path) =>
        _strategies.FirstOrDefault(s => PathGlob.IsMatch(path, s.Pattern));

    /// <summary>
    /// Whether this array is one value rather than a structure: every element is a scalar, so there is
    /// nothing inside it to navigate to.
    ///
    /// <para>
    /// It is the question "does the flattener produce one leaf for this, or one per element", and it is
    /// asked in two places: <see cref="Flattener"/>, deciding what to emit, and the Tier editor's
    /// hierarchy, deciding whether to draw an expander. They have to agree. When they did not, a
    /// Couchbase <c>Scopes</c> array was a single row on the All tiers tab and four rows in the tree,
    /// so the same array had two spellings depending on which tab you were looking at.
    /// </para>
    ///
    /// <para>
    /// This used to require a declared <see cref="ArrayKind.StringSet"/>, which meant an undeclared
    /// array of five strings — <c>configuration:app:android:forceUpdate:body</c>, a list of release
    /// notes — became five rows called <c>[0]</c> to <c>[4]</c> in the tree and five rows in the grid.
    /// Those index names say nothing: the elements have no identity, nothing hangs below them, and the
    /// only useful way to read or change the thing is as one list. A declaration is now how an array of
    /// <em>objects</em> is given identity, not how a list of strings earns the right to be one value.
    /// </para>
    ///
    /// <para>
    /// <see cref="ArrayKind.KeyedObjects"/> is excluded deliberately: a declaration that says "these
    /// elements have identity" and an array that turns out to hold scalars is a mistake in
    /// <c>arrays.json</c>, and the flattener's existing warning about it is worth keeping.
    /// </para>
    /// </summary>
    public bool IsSingleLeaf(string path, JsonArray array) =>
        For(path)?.Kind != ArrayKind.KeyedObjects && ScalarMembers(array) is not null;

    /// <summary>The elements of <paramref name="array"/> as strings, or null if any element is not one.</summary>
    public static List<string>? Members(JsonArray array)
    {
        var members = new List<string>(array.Count);

        foreach (var element in array)
        {
            if (element is JsonValue value && value.TryGetValue<string>(out var text))
            {
                members.Add(text);
            }
            else
            {
                return null;
            }
        }

        return members;
    }

    /// <summary>
    /// The elements as comparable text, or null if any of them is an object or a nested array — which
    /// is the line between "this array is one value" and "this array is a structure".
    ///
    /// <para>
    /// Numbers, booleans and nulls are read exactly as <see cref="Flattener.ReadValue"/> reads them
    /// anywhere else, so <c>[8080, 8443]</c> is the same pair of tokens here as it would be as two
    /// leaves. A JSON null is a null <see cref="JsonNode"/> rather than a node holding null, so it has
    /// to be matched before anything tries to read a value out of it.
    /// </para>
    /// </summary>
    public static List<string>? ScalarMembers(JsonArray array)
    {
        var members = new List<string>(array.Count);

        foreach (var element in array)
        {
            switch (element)
            {
                case null:
                    members.Add("null");
                    break;

                case JsonObject or JsonArray:
                    return null;

                default:
                    members.Add(Flattener.ReadValue(element).Value);
                    break;
            }
        }

        return members;
    }

    private static readonly JsonDocumentOptions OrdinalJsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
