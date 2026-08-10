using System.Text.Json;
using System.Text.Json.Nodes;
using JsonInsight.Classify;
using JsonInsight.Model;

namespace JsonInsight.Loading;

/// <summary>
/// Turns a JsonNode tree into canonical <c>Section:Sub:Key</c> leaves.
///
/// Two paths are produced for every leaf. The canonical <see cref="Leaf.Path"/> is what the app
/// diffs and promotes on; for keyed arrays it names the element by identity
/// (<c>Serilog:WriteTo[Name=Seq]:serverUrl</c>) so inserting a sink does not shift its siblings.
/// The <see cref="Leaf.ConfigurationKey"/> is the index form the ASP.NET binder actually sees
/// (<c>Serilog:WriteTo:2:serverUrl</c>), kept so any finding can be traced to runtime behaviour.
/// </summary>
public sealed class Flattener
{
    private readonly ArrayStrategies _arrays;
    private readonly Classifier _classifier;

    public Flattener(ArrayStrategies arrays, Classifier classifier)
    {
        _arrays = arrays;
        _classifier = classifier;
    }

    /// <summary>How many undeclared array paths the summary names before it stops and counts.</summary>
    private const int ExamplesInSummary = 3;

    public FlatConfig Flatten(string tierId, JsonNode root)
    {
        var leaves = new List<Leaf>();
        var warnings = new List<string>();
        var undeclared = new List<string>();

        Walk(root, string.Empty, string.Empty, tierId, leaves, warnings, undeclared);

        if (undeclared.Count > 0)
        {
            warnings.Add(UndeclaredArraysWarning(undeclared));
        }

        return new FlatConfig(tierId, leaves, warnings);
    }

    /// <summary>
    /// One line for every array in this document that <c>arrays.json</c> does not describe.
    ///
    /// <para>
    /// It used to be one line each, which is the right shape for a document with two of them and the
    /// wrong shape for the real ones: <c>config.json</c> has 68, so four tiers produced 272 identical
    /// sentences differing only in a path. A banner that long is not a warning, it is a wall — the
    /// count is the finding, the paths are the detail, and burying the first behind 272 copies of the
    /// second means nobody reads either.
    /// </para>
    ///
    /// <para>
    /// The paths are not dropped. A few are named outright and the rest are counted, which is what
    /// makes the line actionable — <c>arrays.json</c> is edited by pattern, so the first few are
    /// usually enough to write the rule that covers all of them.
    /// </para>
    /// </summary>
    private static string UndeclaredArraysWarning(List<string> paths)
    {
        paths.Sort(StringComparer.Ordinal);

        var examples = string.Join(", ", paths.Take(ExamplesInSummary));
        var rest = paths.Count - Math.Min(ExamplesInSummary, paths.Count);

        var which = paths.Count == 1
            ? $"array {examples} has"
            : $"{paths.Count} arrays have";

        var listed = paths.Count == 1
            ? string.Empty
            : rest == 0
                ? $" They are: {examples}."
                : $" The first {ExamplesInSummary} are: {examples} (+{rest} more).";

        return $"{which} no declared strategy in arrays.json, so they are compared by index — which " +
               $"reports spurious changes if an element is inserted or reordered.{listed}";
    }

    private void Walk(
        JsonNode? node,
        string path,
        string configurationKey,
        string tierId,
        List<Leaf> leaves,
        List<string> warnings,
        List<string> undeclared)
    {
        switch (node)
        {
            case null:
                leaves.Add(MakeLeaf(path, configurationKey, "null", JsonValueKind.Null, tierId));
                return;

            case JsonObject obj:
            {
                if (obj.Count == 0 && path.Length > 0)
                {
                    // An empty object is a real, comparable state - a tier that dropped every child
                    // of a section is not the same as a tier that never had the section.
                    leaves.Add(MakeLeaf(path, configurationKey, "{}", JsonValueKind.Object, tierId));
                    return;
                }

                foreach (var (key, child) in obj)
                {
                    Walk(child, Join(path, key), Join(configurationKey, key), tierId, leaves, warnings, undeclared);
                }

                return;
            }

            case JsonArray array:
                WalkArray(array, path, configurationKey, tierId, leaves, warnings, undeclared);
                return;

            default:
                var (value, kind) = ReadValue(node);
                leaves.Add(MakeLeaf(path, configurationKey, value, kind, tierId));
                return;
        }
    }

    private void WalkArray(
        JsonArray array,
        string path,
        string configurationKey,
        string tierId,
        List<Leaf> leaves,
        List<string> warnings,
        List<string> undeclared)
    {
        var strategy = _arrays.For(path);

        switch (strategy?.Kind)
        {
            case ArrayKind.KeyedObjects:
            {
                var field = strategy!.IdentityField
                            ?? throw new InvalidOperationException($"keyedObjects strategy for '{strategy.Pattern}' has no identityField.");

                var seen = new HashSet<string>(StringComparer.Ordinal);
                var usable = true;
                var index = 0;

                foreach (var element in array)
                {
                    if (element is not JsonObject item ||
                        item[field] is not JsonValue idValue ||
                        !idValue.TryGetValue<string>(out var id))
                    {
                        warnings.Add($"{path}[{index}]: missing identity field '{field}'; fell back to index paths.");
                        usable = false;
                        break;
                    }

                    if (!seen.Add(id))
                    {
                        warnings.Add($"{path}: duplicate identity '{field}={id}'; fell back to index paths.");
                        usable = false;
                        break;
                    }

                    index++;
                }

                if (usable)
                {
                    index = 0;
                    foreach (var element in array)
                    {
                        var item = (JsonObject)element!;
                        var id = item[field]!.GetValue<string>();
                        Walk(element, $"{path}[{field}={id}]", Join(configurationKey, index.ToString()), tierId, leaves, warnings, undeclared);
                        index++;
                    }

                    return;
                }

                break;
            }

            default:
            {
                // An array of scalars is one value, declared or not. ArrayStrategies decides rather
                // than a loop here, because the Tier editor's hierarchy asks the same question to
                // decide whether to draw an expander — two implementations of "is this array one
                // value" is how the tree and the grid came to disagree about it before.
                //
                // Undeclared is no longer the deciding factor. A list of five release-note strings has
                // no identity to key on and nothing hanging below its elements, so [0] to [4] were five
                // names that said nothing and five rows that could only ever be read together.
                if (_arrays.IsSingleLeaf(path, array))
                {
                    var members = ArrayStrategies.ScalarMembers(array)!;

                    // Ordinal-sorted for comparison only. The document's own order is untouched, and
                    // OrdinalJsonWriter still writes the array exactly as it was read.
                    members.Sort(StringComparer.Ordinal);
                    leaves.Add(MakeLeaf(path, configurationKey, string.Empty, JsonValueKind.Array, tierId, members));
                    return;
                }

                // What is left holds objects or nested arrays, which is the case index comparison is
                // genuinely dangerous for: insert one element and every element after it reports as
                // changed. That is what a keyedObjects strategy exists to fix, so this is worth saying.
                //
                // Recorded rather than written out: one sentence per array turned a document with 68
                // of them into 68 warnings. Flatten() emits a single summary at the end.
                if (strategy?.Kind == ArrayKind.StringSet)
                {
                    warnings.Add($"{path}: declared stringSet but contains objects or nested arrays; fell back to index paths.");
                }
                else
                {
                    undeclared.Add(path);
                }

                break;
            }
        }

        // Fallback for every failure above: positional paths, already warned about.
        var i = 0;
        foreach (var element in array)
        {
            Walk(element, $"{path}[{i}]", Join(configurationKey, i.ToString()), tierId, leaves, warnings, undeclared);
            i++;
        }
    }

    private Leaf MakeLeaf(
        string path,
        string configurationKey,
        string value,
        JsonValueKind kind,
        string tierId,
        IReadOnlyList<string>? setMembers = null)
    {
        var forClassification = setMembers is null ? value : string.Join(",", setMembers);
        return new Leaf
        {
            Path = path,
            ConfigurationKey = configurationKey,
            Value = value,
            Kind = kind,
            TierId = tierId,
            SetMembers = setMembers,
            Class = _classifier.Classify(path, forClassification),
        };
    }

    private static string Join(string prefix, string segment) =>
        prefix.Length == 0 ? segment : prefix + ":" + segment;

    /// <summary>
    /// Reads a scalar. Strings yield their decoded text; everything else yields the raw JSON token,
    /// so a numeric 5000 and a string "5000" remain distinguishable (that difference is a real
    /// binding bug, not a formatting detail).
    /// </summary>
    public static (string Value, JsonValueKind Kind) ReadValue(JsonNode node)
    {
        if (node is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<JsonElement>(out var element))
            {
                return element.ValueKind switch
                {
                    JsonValueKind.String => (element.GetString() ?? string.Empty, JsonValueKind.String),
                    _ => (element.GetRawText(), element.ValueKind),
                };
            }

            // Nodes built in memory (during a promote) have no backing JsonElement.
            if (jsonValue.TryGetValue<string>(out var text))
            {
                return (text, JsonValueKind.String);
            }

            var raw = jsonValue.ToJsonString();
            return (raw, raw switch
            {
                "true" => JsonValueKind.True,
                "false" => JsonValueKind.False,
                "null" => JsonValueKind.Null,
                _ => JsonValueKind.Number,
            });
        }

        return (node.ToJsonString(), JsonValueKind.Undefined);
    }
}
