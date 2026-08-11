using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JsonInsight.Classify;
using JsonInsight.Diff;
using JsonInsight.Loading;
using JsonInsight.Model;

namespace JsonInsight.ViewModels;

/// <summary>One key path, side by side. Secrets render as a fingerprint, never as their value.</summary>
public sealed record JsonCompareRowVm(DiffEntry Entry)
{
    public string Path => Entry.Path;

    public DiffKind Kind => Entry.Kind;

    public ValueClass Class => Entry.Class;

    public bool IsSecret => Class == ValueClass.Secret;

    public string LeftValue => Render(Entry.Left);

    public string RightValue => Render(Entry.Right);

    public string? Detail => Entry.Detail;

    public string Status => Entry.Kind switch
    {
        DiffKind.Same => "same",
        DiffKind.OnlyInLeft => "only left",
        DiffKind.OnlyInRight => "only right",
        DiffKind.ValueDiffers => "differs",
        DiffKind.TypeDiffers => "type",
        DiffKind.SetDiffers => "set",
        DiffKind.ShapeDiffers => "shape",
        _ => Entry.Kind.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// An em dash for a key the side does not have, so "absent" never reads as "present and empty" —
    /// the two are different findings and the empty-string case is common in these files.
    /// </summary>
    private static string Render(Leaf? leaf) =>
        leaf is null ? "—" : SecretMasker.Describe(leaf);
}

/// <summary>
/// Compares any two JSON files on disk, not just the configured tiers.
///
/// <para>
/// Loading goes through <see cref="TierLoader"/> with a synthetic non-writable
/// <see cref="TierDefinition"/>, so a browsed file gets the same decoding, comment handling, array
/// strategies and secret classification as a tier — and cannot be written to by anything in the app.
/// Comparison is <see cref="TierDiffer"/>, the same engine behind the All tiers tab, so a row here and a
/// row there mean the same thing rather than nearly the same thing.
/// </para>
/// </summary>
public sealed partial class JsonCompareVm : ObservableObject
{
    private readonly TierLoader _loader;
    private readonly AliasSet _aliases;

    private TierDocument? _leftDocument;
    private TierDocument? _rightDocument;

    [ObservableProperty]
    private string _leftPath = string.Empty;

    [ObservableProperty]
    private string _rightPath = string.Empty;

    /// <summary>
    /// Off by default. The question that brings anyone to this tab is "what is different", and on two
    /// vault snapshots the identical rows outnumber the rest by roughly fifty to one.
    /// </summary>
    [ObservableProperty]
    private bool _showIdentical;

    [ObservableProperty]
    private string _filter = string.Empty;

    [ObservableProperty]
    private string _summary = "Pick two JSON files.";

    [ObservableProperty]
    private string _error = string.Empty;

    public ObservableCollection<JsonCompareRowVm> Rows { get; } = [];

    /// <summary>Array-strategy warnings from either file: an undeclared array is compared by index.</summary>
    public ObservableCollection<string> Warnings { get; } = [];

    public JsonCompareVm(TierLoader loader, AliasSet aliases)
    {
        _loader = loader;
        _aliases = aliases;
    }

    partial void OnLeftPathChanged(string value) => Reload(left: true);

    partial void OnRightPathChanged(string value) => Reload(left: false);

    partial void OnShowIdenticalChanged(bool value) => Rebuild();

    partial void OnFilterChanged(string value) => Rebuild();

    [RelayCommand]
    private void BrowseLeft()
    {
        if (Pick("Left file") is { } picked)
        {
            LeftPath = picked;
        }
    }

    [RelayCommand]
    private void BrowseRight()
    {
        if (Pick("Right file") is { } picked)
        {
            RightPath = picked;
        }
    }

    [RelayCommand]
    private void Swap()
    {
        (LeftPath, RightPath) = (RightPath, LeftPath);
    }

    /// <summary>Re-reads both files from disk. Useful after editing one outside the app.</summary>
    [RelayCommand]
    private void Reload()
    {
        Reload(left: true);
        Reload(left: false);
    }

    private static string? Pick(string title) =>
        JsonInsight.Platform.Platform.FilePicker.OpenFile(title, ["json", "jsonc"]);

    private void Reload(bool left)
    {
        var path = left ? LeftPath : RightPath;
        var document = Load(path, left ? "left" : "right", out var failure);

        if (left)
        {
            _leftDocument = document;
        }
        else
        {
            _rightDocument = document;
        }

        Error = failure ?? string.Empty;
        Rebuild();
    }

    private TierDocument? Load(string path, string id, out string? failure)
    {
        failure = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            // Read-only by construction: the loader marks a browsed file as such, because nothing
            // on this tab writes and a file on disk is not a tier.
            return _loader.LoadFile(path, id, Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            failure = $"{Path.GetFileName(path)}: {ex.Message}";
            return null;
        }
    }

    private void Rebuild()
    {
        Rows.Clear();
        Warnings.Clear();

        if (_leftDocument is null || _rightDocument is null)
        {
            Summary = Error.Length > 0 ? "Could not load." : "Pick two JSON files.";
            return;
        }

        foreach (var warning in _leftDocument.Warnings.Concat(_rightDocument.Warnings).Distinct(StringComparer.Ordinal))
        {
            Warnings.Add(warning);
        }

        var diff = new TierDiffer(_aliases).Compare(_leftDocument.Flat, _rightDocument.Flat);

        foreach (var entry in diff.Entries)
        {
            if (!ShowIdentical && !entry.IsDifference)
            {
                continue;
            }

            if (Filter.Length > 0 && !entry.Path.Contains(Filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Rows.Add(new JsonCompareRowVm(entry));
        }

        var differences = diff.Entries.Count(e => e.IsDifference);

        Summary =
            $"{_leftDocument.Label} ({_leftDocument.Flat.Count} keys)  →  " +
            $"{_rightDocument.Label} ({_rightDocument.Flat.Count} keys):  " +
            $"{diff.OnlyInLeft} only left, {diff.OnlyInRight} only right, " +
            $"{diff.ValueDifferences} value, {diff.ShapeDifferences} shape  —  " +
            $"{differences} of {diff.Entries.Count} keys differ, {Rows.Count} shown";
    }
}
