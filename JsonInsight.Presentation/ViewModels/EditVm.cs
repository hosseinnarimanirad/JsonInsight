using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JsonInsight.Classify;
using JsonInsight.Editing;
using JsonInsight.Model;

namespace JsonInsight.ViewModels;

/// <summary>
/// A JSON type offered in the editor. Booleans collapse to one entry — True and False are two
/// <see cref="JsonValueKind"/> members for a single JSON type, and offering both would ask which
/// boolean you meant before you have typed the value.
/// </summary>
public sealed record JsonKindOption(JsonValueKind Kind, string Label)
{
    public override string ToString() => Label;
}

/// <summary>What an edit row will do to one key in one tier.</summary>
public enum RowAction
{
    /// <summary>Leave this tier alone. The default for every row.</summary>
    Keep,

    /// <summary>Write the new value. Shown as "Add" in the UI when the key is absent here.</summary>
    Set,

    /// <summary>Remove the key from this tier.</summary>
    Delete,
}

/// <summary>
/// One (path, tier) cell of the edit grid.
///
/// <para>
/// Every tier gets a row, including tiers that do not have the key. That is deliberate: a key that
/// exists in exactly one tier <em>is</em> the drift this tool was built to find, so the shape of the
/// screen should make "set it everywhere" the easy action and "set it in one place" the one you have
/// to choose.
/// </para>
/// </summary>
public sealed partial class EditRowVm : ObservableObject
{
    [ObservableProperty]
    private RowAction _action = RowAction.Keep;

    [ObservableProperty]
    private string _newValue = string.Empty;

    [ObservableProperty]
    private JsonValueKind _kind = JsonValueKind.String;

    public required string Path { get; init; }

    public required string TierId { get; init; }

    public required bool TierWritable { get; init; }

    /// <summary>The leaf as it stands, or null when this tier does not have the key.</summary>
    public Leaf? Current { get; init; }

    public required ValueClass Class { get; init; }

    public bool Exists => Current is not null;

    public bool IsSecret => Class == ValueClass.Secret;

    public string ClassName => Class.ToString().ToLowerInvariant();

    /// <summary>The current value as the user may see it. A secret is described, never revealed.</summary>
    public string CurrentDisplay => Current is null
        ? "(absent)"
        : IsSecret
            ? SecretMasker.Describe(Current.ComparableValue)
            : Current.ComparableValue;

    /// <summary>Set-valued leaves (the Couchbase Scopes arrays) are read here and edited in the file.</summary>
    public bool IsEditable => TierWritable && Current?.IsSet != true;

    public string NotEditableReason => !TierWritable
        ? $"{TierId} is read-only in tiers.json."
        : Current?.IsSet == true
            ? "This key is a set-valued array. Editing arrays is not in this version — change it in the file, " +
              "or give it a strategy in arrays.json first."
            : string.Empty;

    /// <summary>What the row will read as once applied, so the grid shows the outcome rather than the input.</summary>
    public string ResultDisplay => Action switch
    {
        RowAction.Delete => "(removed)",
        RowAction.Set when IsSecret => SecretMasker.Describe(NewValue),
        RowAction.Set => NewValue,
        _ => CurrentDisplay,
    };

    partial void OnActionChanged(RowAction value) => OnPropertyChanged(nameof(ResultDisplay));

    partial void OnNewValueChanged(string value) => OnPropertyChanged(nameof(ResultDisplay));

    /// <summary>Turns the row into a queued edit, or null when it does nothing.</summary>
    public PendingEdit? ToEdit()
    {
        if (Action == RowAction.Keep || !IsEditable)
        {
            return null;
        }

        if (Action == RowAction.Delete)
        {
            return Current is null
                ? null
                : new PendingEdit
                {
                    TierId = TierId,
                    Path = Path,
                    Kind = EditKind.Delete,
                    BaseValue = Current.ComparableValue,
                    Class = Class,
                };
        }

        return new PendingEdit
        {
            TierId = TierId,
            Path = Path,
            Kind = Current is null ? EditKind.Add : EditKind.Update,
            BaseValue = Current?.ComparableValue,
            NewValue = NewValue,
            NewKind = Kind,
            Class = Class,
        };
    }
}

/// <summary>
/// The edit screen: one or many paths, every tier, in one grid.
///
/// <para>
/// One dialog rather than three. Updating a value, adding a key that exists nowhere and deleting one
/// are the same operation seen from different starting states, and the change that prompted this
/// feature — one shape applied to six sibling keys across two files — is only bearable if all of it
/// fits on one screen with a single "set them all to this" box.
/// </para>
///
/// <para>
/// Nothing here writes. The dialog queues into <see cref="MainVm.Edits"/>; the commit dialog is what
/// runs the preview, the guard, the backup and the typed confirmation.
/// </para>
/// </summary>
public sealed partial class EditVm : ObservableObject
{
    private readonly MainVm _main;

    [ObservableProperty]
    private string _bulkValue = string.Empty;

    [ObservableProperty]
    private JsonValueKind _bulkKind = JsonValueKind.String;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _queued;

    public ObservableCollection<EditRowVm> Rows { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    /// <summary>The JSON types the editor writes. Deliberately short — these files hold no others.</summary>
    public static IReadOnlyList<JsonKindOption> KindOptions { get; } =
    [
        new(JsonValueKind.String, "string"),
        new(JsonValueKind.Number, "number"),
        new(JsonValueKind.True, "boolean"),
        new(JsonValueKind.Null, "null"),
    ];

    /// <summary>Maps a leaf's kind onto one the combo offers, so False selects the boolean entry.</summary>
    public static JsonValueKind NormalizeKind(JsonValueKind kind) => kind switch
    {
        JsonValueKind.False => JsonValueKind.True,
        JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Undefined => JsonValueKind.String,
        _ => kind,
    };

    public IReadOnlyList<string> Paths { get; }

    public string Title => Paths.Count == 1 ? $"Edit {Paths[0]}" : $"Edit {Paths.Count} keys";

    public string Subtitle =>
        $"{Rows.Count} rows across {Rows.Select(r => r.TierId).Distinct(StringComparer.OrdinalIgnoreCase).Count()} tiers. " +
        "Nothing is written here — changes queue up and are committed together.";

    /// <summary>
    /// Enabled when there is something to queue — or something to withdraw. Setting a row back to
    /// Keep is how an already-queued edit is taken out again, so the button has to stay live for
    /// that case; otherwise reopening a key to reconsider it would be a dead end.
    /// </summary>
    public bool CanQueue =>
        (Rows.Any(r => r.Action != RowAction.Keep && r.IsEditable) || HasSomethingToWithdraw) &&
        !Warnings.Any(w => w.StartsWith(BlockingPrefix, StringComparison.Ordinal));

    private bool HasSomethingToWithdraw =>
        Rows.Any(r => r.Action == RowAction.Keep && _main.Edits.Contains(r.TierId, r.Path));

    private const string BlockingPrefix = "BLOCKED: ";

    /// <summary>
    /// Edits one or more paths across every tier.
    ///
    /// <para>
    /// A path that exists in no tier at all is still a valid thing to hand this: every tier gets a
    /// row either way, and a row whose key is absent queues as an add. What is gone is the toolbar
    /// button that let one be typed from nothing, not the ability to create a key.
    /// </para>
    /// </summary>
    public EditVm(MainVm main, IReadOnlyList<string> paths)
    {
        _main = main;
        Paths = paths;
        BuildRows();
    }

    private void BuildRows()
    {
        Rows.Clear();

        foreach (var path in Paths)
        {
            var classification = _main.Classifier.Classify(path, ValueSomewhere(path));

            foreach (var document in _main.Documents)
            {
                var leaf = document.Flat.Find(path);
                var existing = _main.Edits.Find(document.Id, path);

                var row = new EditRowVm
                {
                    Path = path,
                    TierId = document.Id,
                    TierWritable = document.Writable,
                    Current = leaf,
                    Class = leaf?.Class ?? classification,
                    NewValue = existing?.NewValue ?? (leaf is { IsSet: false } ? leaf.Value : string.Empty),
                    Kind = NormalizeKind(existing?.NewKind ?? leaf?.Kind ?? InferKindFromOtherTiers(path)),
                };

                // An edit already queued for this key reopens in the state it was left in, so
                // revisiting a row to check it does not silently drop it from the change set.
                if (existing is not null)
                {
                    row.Action = existing.Kind == EditKind.Delete ? RowAction.Delete : RowAction.Set;
                }

                row.PropertyChanged += (_, _) => Revalidate();
                Rows.Add(row);
            }
        }

        Revalidate();
    }

    /// <summary>Any tier's value for the path, used only to classify it before anything is typed.</summary>
    private string ValueSomewhere(string path) =>
        _main.Documents.Select(d => d.Flat.Find(path)?.Value).FirstOrDefault(v => v is not null) ?? string.Empty;

    /// <summary>
    /// A new key defaults to the JSON type its namesake already has elsewhere. Writing "10" where the
    /// siblings hold 10 produces a file inconsistent with itself even where the binder coerces it.
    /// </summary>
    private JsonValueKind InferKindFromOtherTiers(string path)
    {
        foreach (var document in _main.Documents)
        {
            if (document.Flat.Find(path) is { } leaf)
            {
                return NormalizeKind(leaf.Kind);
            }
        }

        return JsonValueKind.String;
    }

    /// <summary>
    /// Applies one value to every editable row. This is the whole point of the batch case: six
    /// Couchbase URLs widened from one seed host to four is one string typed once, not six.
    /// </summary>
    [RelayCommand]
    private void ApplyToAll()
    {
        foreach (var row in Rows.Where(r => r.IsEditable))
        {
            row.NewValue = BulkValue;
            row.Kind = BulkKind;
            row.Action = RowAction.Set;
        }

        Revalidate();
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var row in Rows.Where(r => r.IsEditable))
        {
            row.Action = RowAction.Set;
        }

        Revalidate();
    }

    [RelayCommand]
    private void KeepAll()
    {
        foreach (var row in Rows)
        {
            row.Action = RowAction.Keep;
        }

        Revalidate();
    }

    [RelayCommand]
    private void DeleteAll()
    {
        foreach (var row in Rows.Where(r => r is { IsEditable: true, Exists: true }))
        {
            row.Action = RowAction.Delete;
        }

        Revalidate();
    }

    private void Revalidate()
    {
        Warnings.Clear();

        var edits = Rows.Select(r => r.ToEdit()).OfType<PendingEdit>().ToArray();
        if (edits.Length == 0)
        {
            Message = HasSomethingToWithdraw
                ? "Every row is set to Keep. Queueing now withdraws the change(s) already pending for these keys."
                : "Nothing selected. Set an action on at least one row.";

            OnPropertyChanged(nameof(CanQueue));
            return;
        }

        var validator = new EditValidator(_main.Documents.ToArray(), _main.Classifier);
        foreach (var warning in validator.Validate(edits))
        {
            Warnings.Add(warning.IsBlocking ? BlockingPrefix + warning.Text : warning.Text);
        }

        var byKind = edits.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count());
        Message = string.Join(", ",
            new[]
                {
                    byKind.TryGetValue(EditKind.Update, out var u) ? $"{u} update" : null,
                    byKind.TryGetValue(EditKind.Add, out var a) ? $"{a} add" : null,
                    byKind.TryGetValue(EditKind.Delete, out var d) ? $"{d} delete" : null,
                }
                .Where(x => x is not null));

        OnPropertyChanged(nameof(CanQueue));
    }

    /// <summary>Queues every selected row into the app-wide change set. Still writes nothing.</summary>
    [RelayCommand]
    private void Queue()
    {
        if (!CanQueue)
        {
            return;
        }

        var queued = 0;
        var noOps = 0;
        var withdrawn = 0;

        foreach (var row in Rows)
        {
            if (row.ToEdit() is not { } edit)
            {
                // A row set back to Keep withdraws an edit queued for it earlier.
                if (row.Action == RowAction.Keep && _main.Edits.Remove(row.TierId, row.Path))
                {
                    withdrawn++;
                }

                continue;
            }

            if (_main.Edits.AddUnlessNoOp(edit))
            {
                queued++;
            }
            else
            {
                noOps++;
            }
        }

        Queued = true;

        var parts = new List<string> { $"{queued} change(s) queued" };
        if (noOps > 0)
        {
            parts.Add($"{noOps} matched the existing value and were dropped");
        }

        if (withdrawn > 0)
        {
            parts.Add($"{withdrawn} withdrawn");
        }

        Message = string.Join("; ", parts) +
                  $". {_main.Edits.Count} pending in total — review and write them from the All tiers tab.";

        _main.Tiers?.NotifyEditsChanged();
    }
}
