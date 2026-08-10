using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using JsonInsight.Model;
using JsonInsight.Promote;

namespace JsonInsight.ViewModels;

public sealed record DiffLineVm(string LeftNumber, string LeftText, string RightNumber, string RightText, ChangeType Type)
{
    public bool IsUnchanged => Type == ChangeType.Unchanged;
}

/// <summary>
/// The plain-text escape hatch: any two sources, canonically serialized and diffed line by line.
/// Both sides go through the same writer, so what shows up is a real content difference rather
/// than a formatting artefact.
/// </summary>
public sealed partial class RawDiffVm : ObservableObject
{
    private readonly IReadOnlyList<TierDocument> _documents;

    [ObservableProperty]
    private TierDocument? _left;

    [ObservableProperty]
    private TierDocument? _right;

    [ObservableProperty]
    private bool _hideUnchanged = true;

    [ObservableProperty]
    private string _summary = string.Empty;

    public ObservableCollection<DiffLineVm> Lines { get; } = [];

    public IReadOnlyList<TierDocument> Documents => _documents;

    public RawDiffVm(IReadOnlyList<TierDocument> documents)
    {
        _documents = documents;
        _left = documents.FirstOrDefault();
        _right = documents.Skip(1).FirstOrDefault() ?? documents.FirstOrDefault();
        Rebuild();
    }

    partial void OnLeftChanged(TierDocument? value) => Rebuild();

    partial void OnRightChanged(TierDocument? value) => Rebuild();

    partial void OnHideUnchangedChanged(bool value) => Rebuild();

    [RelayCommand]
    private void Swap() => (Left, Right) = (Right, Left);

    private void Rebuild()
    {
        Lines.Clear();

        if (Left is null || Right is null)
        {
            Summary = "Pick two sources.";
            return;
        }

        string leftText, rightText;
        try
        {
            leftText = OrdinalJsonWriter.SerializeToText(Left.Root);
            rightText = OrdinalJsonWriter.SerializeToText(Right.Root);
        }
        catch (Exception ex)
        {
            Summary = $"Could not serialize: {ex.Message}";
            return;
        }

        var model = SideBySideDiffBuilder.Instance.BuildDiffModel(leftText, rightText, ignoreWhitespace: false);

        var added = 0;
        var removed = 0;
        var modified = 0;

        for (var i = 0; i < Math.Max(model.OldText.Lines.Count, model.NewText.Lines.Count); i++)
        {
            var oldLine = i < model.OldText.Lines.Count ? model.OldText.Lines[i] : null;
            var newLine = i < model.NewText.Lines.Count ? model.NewText.Lines[i] : null;

            var type = newLine?.Type ?? oldLine?.Type ?? ChangeType.Unchanged;
            switch (type)
            {
                case ChangeType.Inserted:
                    added++;
                    break;
                case ChangeType.Deleted:
                    removed++;
                    break;
                case ChangeType.Modified:
                    modified++;
                    break;
            }

            if (HideUnchanged && type == ChangeType.Unchanged)
            {
                continue;
            }

            Lines.Add(new DiffLineVm(
                oldLine?.Position?.ToString() ?? string.Empty,
                oldLine?.Text ?? string.Empty,
                newLine?.Position?.ToString() ?? string.Empty,
                newLine?.Text ?? string.Empty,
                type));
        }

        Summary = $"{Left.Id} → {Right.Id}:  {added} added, {removed} removed, {modified} modified";
    }
}
