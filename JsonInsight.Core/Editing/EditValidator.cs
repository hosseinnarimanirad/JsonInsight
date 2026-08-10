using System.Text.Json;
using JsonInsight.Classify;
using JsonInsight.Model;

namespace JsonInsight.Editing;

public enum EditWarningLevel
{
    /// <summary>Worth saying, does not block. Most warnings are this.</summary>
    Warn,

    /// <summary>The edit cannot be written as authored.</summary>
    Blocking,
}

public sealed record EditWarning(EditWarningLevel Level, string Path, string Message)
{
    public bool IsBlocking => Level == EditWarningLevel.Blocking;

    public string Text => $"{Path} — {Message}";
}

/// <summary>
/// Checks a change set against every tier before it is written.
///
/// <para>
/// The rules here exist because adding a key is the one operation with no anchor. Promote and
/// update are both aimed at a path some tier already spells correctly; a brand-new key is
/// indistinguishable from a typo, and because the Vault loader <em>replaces</em> the appsettings
/// layer wholesale rather than merging it, a misspelled key means the intended one is simply absent
/// at runtime — no error, no warning, just missing configuration.
/// </para>
/// </summary>
public sealed class EditValidator
{
    private readonly IReadOnlyList<TierDocument> _documents;
    private readonly Classifier _classifier;

    public EditValidator(IReadOnlyList<TierDocument> documents, Classifier classifier)
    {
        _documents = documents;
        _classifier = classifier;
    }

    public IReadOnlyList<EditWarning> Validate(IReadOnlyList<PendingEdit> edits)
    {
        var warnings = new List<EditWarning>();

        foreach (var edit in edits)
        {
            warnings.AddRange(ValidateOne(edit, edits));
        }

        return warnings;
    }

    private IEnumerable<EditWarning> ValidateOne(PendingEdit edit, IReadOnlyList<PendingEdit> all)
    {
        var known = KnownPaths();

        if (edit.Kind == EditKind.Add)
        {
            foreach (var warning in ValidateNewPath(edit, known, all))
            {
                yield return warning;
            }
        }

        if (edit.Kind == EditKind.Delete)
        {
            var stillHave = _documents
                .Where(d => !d.Id.Equals(edit.TierId, StringComparison.OrdinalIgnoreCase))
                .Where(d => d.Flat.Contains(edit.Path))
                .Select(d => d.Id)
                .ToArray();

            if (stillHave.Length > 0)
            {
                yield return new EditWarning(EditWarningLevel.Warn, edit.Path,
                    $"deleting from {edit.TierId} while {string.Join(", ", stillHave)} still have it — " +
                    "this creates exactly the single-tier drift the tool exists to find.");
            }
        }

        if (edit.Kind is EditKind.Add or EditKind.Update)
        {
            foreach (var warning in ValidateValue(edit))
            {
                yield return warning;
            }
        }
    }

    private IEnumerable<EditWarning> ValidateNewPath(
        PendingEdit edit,
        IReadOnlyDictionary<string, JsonValueKind> known,
        IReadOnlyList<PendingEdit> all)
    {
        var existsSomewhere = known.ContainsKey(edit.Path) ||
                              all.Any(e => e.Kind != EditKind.Add &&
                                           e.Path.Equals(edit.Path, StringComparison.Ordinal));

        if (!existsSomewhere)
        {
            yield return new EditWarning(EditWarningLevel.Warn, edit.Path,
                "this key exists in no tier at all. Check the spelling — a key the runtime never " +
                "looks up produces no error, just missing configuration.");

            if (NearestMatch(edit.Path, known.Keys) is { } suggestion)
            {
                yield return new EditWarning(EditWarningLevel.Warn, edit.Path,
                    $"did you mean {suggestion}?");
            }
        }

        // The ordinal comparer that orders these files treats Url and URL as different keys; the
        // .NET configuration binder does not. A pair that differs only in case is therefore a key
        // that appears twice in the file and resolves to one setting at runtime, arbitrarily.
        var collision = known.Keys.FirstOrDefault(p =>
            !p.Equals(edit.Path, StringComparison.Ordinal) &&
            p.Equals(edit.Path, StringComparison.OrdinalIgnoreCase));

        if (collision is not null)
        {
            yield return new EditWarning(EditWarningLevel.Blocking, edit.Path,
                $"differs from the existing {collision} only in casing. The file's ordinal ordering " +
                "treats these as two keys; the configuration binder treats them as one.");
        }
    }

    private IEnumerable<EditWarning> ValidateValue(PendingEdit edit)
    {
        // The classifier drives both what is masked on screen and what a promote refuses to copy, so
        // a new key landing in the wrong class is a leak waiting to happen. Surfacing the verdict in
        // the preview is what catches it before the write rather than after.
        var classified = _classifier.Classify(edit.Path, edit.NewValue ?? string.Empty);
        if (classified != edit.Class)
        {
            yield return new EditWarning(EditWarningLevel.Warn, edit.Path,
                $"classified as {classified.ToString().ToLowerInvariant()} by classify.json, " +
                $"but queued as {edit.Class.ToString().ToLowerInvariant()}.");
        }

        var others = _documents
            .Where(d => !d.Id.Equals(edit.TierId, StringComparison.OrdinalIgnoreCase))
            .Select(d => (d.Id, Leaf: d.Flat.Find(edit.Path)))
            .Where(x => x.Leaf is not null)
            .ToArray();

        var mismatched = others
            .Where(x => x.Leaf!.Kind != edit.NewKind && !BothNumericish(x.Leaf!.Kind, edit.NewKind))
            .ToArray();

        if (mismatched.Length > 0)
        {
            var describe = string.Join(", ", mismatched.Select(x => $"{x.Id}: {Describe(x.Leaf!.Kind)}"));
            yield return new EditWarning(EditWarningLevel.Warn, edit.Path,
                $"written as {Describe(edit.NewKind)} while other tiers hold it as {describe}.");
        }

        if (edit.NewValue is { } value && value.StartsWith("<<SET-FOR-", StringComparison.Ordinal))
        {
            yield return new EditWarning(EditWarningLevel.Warn, edit.Path,
                "the value is still a promote placeholder. It will fail loudly at startup, which is " +
                "the intent — but do not upload this snapshot expecting it to run.");
        }
    }

    /// <summary>True and False are two JsonValueKind members for one JSON type, so they never mismatch.</summary>
    private static bool BothNumericish(JsonValueKind a, JsonValueKind b) =>
        a is JsonValueKind.True or JsonValueKind.False && b is JsonValueKind.True or JsonValueKind.False;

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Number => "number",
        JsonValueKind.String => "string",
        JsonValueKind.Null => "null",
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private Dictionary<string, JsonValueKind> KnownPaths()
    {
        var known = new Dictionary<string, JsonValueKind>(StringComparer.Ordinal);
        foreach (var document in _documents)
        {
            foreach (var (path, leaf) in document.Flat.Leaves)
            {
                known.TryAdd(path, leaf.Kind);
            }
        }

        return known;
    }

    /// <summary>
    /// The closest known path by edit distance, or null when nothing is close enough to be worth
    /// suggesting. The threshold scales with length so a short key does not attract a suggestion
    /// that shares almost nothing with it.
    /// </summary>
    internal static string? NearestMatch(string path, IEnumerable<string> candidates)
    {
        var budget = Math.Max(2, path.Length / 6);
        string? best = null;
        var bestDistance = int.MaxValue;

        foreach (var candidate in candidates)
        {
            // Cheap rejection first: two paths whose lengths differ by more than the budget cannot
            // be within it, and this runs against every key in every tier.
            if (Math.Abs(candidate.Length - path.Length) > budget)
            {
                continue;
            }

            var distance = Distance(path, candidate, budget);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return bestDistance <= budget ? best : null;
    }

    /// <summary>Levenshtein distance, abandoned early once every cell in a row exceeds the budget.</summary>
    private static int Distance(string a, string b, int budget)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowBest = current[0];

            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                rowBest = Math.Min(rowBest, current[j]);
            }

            if (rowBest > budget)
            {
                return int.MaxValue;
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
