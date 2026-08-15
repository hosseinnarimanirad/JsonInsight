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
    /// <summary>
    /// One line-by-line diff of two canonical serializations, for every screen in the app that shows
    /// one: the editor's comparison, the push screen, promote, pending changes, and the Text diff
    /// tab.
    ///
    /// <para>
    /// One method rather than the five copied loops it replaces. The copies were not merely
    /// repetitive — three of them had drifted onto the wrong row-type resolution (see
    /// <see cref="RowType"/>), so a deleted line rendered as an uncoloured blank row on those three
    /// screens and the Text diff tab's "removed" count sat permanently at zero. Nothing about
    /// diffing two strings is per-screen; what is per-screen is the sentence written about the
    /// result, which is why this returns the counts rather than a message.
    /// </para>
    ///
    /// <para>
    /// <paramref name="includeUnchanged"/> keeps the rows that carry no change — the push screen's
    /// "show unchanged" toggle and the Text diff tab's "hide unchanged" box are the two switches on
    /// it. The counts are of the whole diff either way, so flipping it changes what is on screen
    /// without changing what the summary above it says.
    /// </para>
    /// </summary>
    public static DiffLines Build(string before, string after, bool includeUnchanged)
    {
        var model = SideBySideDiffBuilder.Instance.BuildDiffModel(before, after, ignoreWhitespace: false);

        var lines = new List<DiffLineVm>();
        var added = 0;
        var removed = 0;
        var modified = 0;

        for (var i = 0; i < Math.Max(model.OldText.Lines.Count, model.NewText.Lines.Count); i++)
        {
            var oldLine = i < model.OldText.Lines.Count ? model.OldText.Lines[i] : null;
            var newLine = i < model.NewText.Lines.Count ? model.NewText.Lines[i] : null;

            var type = RowType(oldLine?.Type, newLine?.Type);

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

            // An Imaginary row is padding rather than a change - the blank half of a pair whose real
            // line is on the other side - so it is dropped alongside the unchanged rows rather than
            // shown as a change nobody made. It survives when the caller asked for everything,
            // because there it is what keeps the two sides lined up.
            if (!includeUnchanged && type is ChangeType.Unchanged or ChangeType.Imaginary)
            {
                continue;
            }

            lines.Add(new DiffLineVm(
                oldLine?.Position?.ToString() ?? string.Empty,
                oldLine?.Text ?? string.Empty,
                newLine?.Position?.ToString() ?? string.Empty,
                newLine?.Text ?? string.Empty,
                type));
        }

        return new DiffLines(lines, added, removed, modified);
    }

    /// <summary>
    /// The kind of change a row represents, given what each side says about its own line.
    ///
    /// <para>
    /// A deleted line pairs a real old line with an <c>Imaginary</c> placeholder on the new side, so
    /// reading the new side first — the obvious way round — labels every deletion "imaginary" and
    /// renders it as an uncoloured blank row. The old side is what carries the answer there.
    /// </para>
    /// </summary>
    private static ChangeType RowType(ChangeType? oldType, ChangeType? newType)
    {
        var right = newType ?? ChangeType.Imaginary;
        var left = oldType ?? ChangeType.Imaginary;

        return right is ChangeType.Imaginary or ChangeType.Unchanged ? left : right;
    }
}

/// <summary>
/// What <see cref="DiffLineVm.Build"/> produces: the rows to render, and how many rows each kind of
/// change accounts for.
///
/// <para>
/// The counts describe the whole diff rather than <see cref="Lines"/>, so a caller that asked for
/// only the changed rows and one that asked for all of them are told the same three numbers.
/// </para>
/// </summary>
/// <param name="Lines">The rows to render, in document order.</param>
/// <param name="Added">Lines the right-hand side has and the left-hand side does not.</param>
/// <param name="Removed">Lines the left-hand side has and the right-hand side does not.</param>
/// <param name="Modified">Lines both sides have and disagree about.</param>
public sealed record DiffLines(IReadOnlyList<DiffLineVm> Lines, int Added, int Removed, int Modified)
{
    /// <summary>
    /// The phrase the editor's comparison and the push screen both end their message with. Shared
    /// because the two were already character-for-character identical, and a sentence formatted in
    /// two places is a sentence that reads two ways after the next edit.
    ///
    /// <para>
    /// The Text diff tab deliberately does not use it. That tab compares two *sources* rather than
    /// describing a change somebody made, so its summary names both sides and orders the numbers to
    /// match the direction it reads in.
    /// </para>
    /// </summary>
    public string Counts => $"{Modified} changed, {Added} added, {Removed} removed.";
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
            // Live rather than Root: this tab answers "what do these two say", and an edit made on
            // the Tier editor is part of what they say. Root is only the baseline a push checks the
            // source against.
            leftText = OrdinalJsonWriter.SerializeToText(Left.Live);
            rightText = OrdinalJsonWriter.SerializeToText(Right.Live);
        }
        catch (Exception ex)
        {
            Summary = $"Could not serialize: {ex.Message}";
            return;
        }

        var diff = DiffLineVm.Build(leftText, rightText, includeUnchanged: !HideUnchanged);

        foreach (var line in diff.Lines)
        {
            Lines.Add(line);
        }

        // Not DiffLines.Counts: this tab is comparing two sources rather than describing an edit
        // somebody made, so the summary names both sides and reads in the direction the two columns
        // are laid out in.
        Summary = $"{Left.Id} → {Right.Id}:  {diff.Added} added, {diff.Removed} removed, {diff.Modified} modified";
    }
}
