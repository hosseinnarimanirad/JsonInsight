using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using JsonInsight.Classify;
using JsonInsight.Sources;
using JsonInsight.Diff;
using JsonInsight.Editing;
using JsonInsight.Loading;
using JsonInsight.Model;
using JsonInsight.Promote;
using JsonInsight.Vault;

namespace JsonInsight.ViewModels;

/// <summary>One node of the document hierarchy, flattened into a list with an indent.</summary>
public sealed partial class JsonNodeVm : ObservableObject
{
    [ObservableProperty]
    private bool _isExpanded;

    public required string Name { get; init; }

    /// <summary>Canonical path. Empty for the root, which is how "replace the whole document" is expressed.</summary>
    public required string Path { get; init; }

    public required int Depth { get; init; }

    public required bool IsContainer { get; init; }

    /// <summary>
    /// An array of scalars, which the tree shows as a leaf because there is nothing inside it to
    /// navigate to.
    ///
    /// <para>
    /// Separate from <see cref="IsContainer"/> because the two questions have different answers here
    /// and both are load-bearing. The tree asks "is there anything under this to expand into" and the
    /// answer is no. The pane asks "can this be applied as it is typed" and the answer is also no — an
    /// array is invalid JSON for as long as it takes to type one, so applying on every keystroke would
    /// either fail constantly or destroy the node. Collapsing the two would have made
    /// <c>["a", "b"]</c> commit on every character.
    /// </para>
    /// </summary>
    public bool IsScalarArray { get; init; }

    public required int LeafCount { get; init; }

    /// <summary>Masked for a secret, elided for a container. Never the raw value of a secret.</summary>
    [ObservableProperty]
    private string _preview = string.Empty;

    public required bool ContainsSecret { get; init; }

    /// <summary>
    /// What happened to this node since the tier was opened. A section holding changes is
    /// <see cref="NodeChange.Mixed"/> rather than any one kind, so an edit buried deep in a collapsed
    /// subtree is still findable by following the marks down without the ancestors claiming to be
    /// edits themselves.
    ///
    /// <para>
    /// Settable, and observable, because a scalar edited in the pane updates its own row rather than
    /// rebuilding the tree: a rebuild replaces every row, which reselects, which reloads the pane and
    /// throws the caret back to the start of the line being typed.
    /// </para>
    /// </summary>
    [ObservableProperty]
    private NodeChange _change;

    /// <summary>
    /// True when this node, or anything beneath it, differs from the state the tier was opened in.
    /// </summary>
    public bool IsChanged => Change != NodeChange.None;

    /// <summary>
    /// True for a node that has been deleted but not yet saved. It stays in the tree, struck
    /// through, so the deletion is visible and can be taken back.
    /// </summary>
    public bool IsRemoved => Change == NodeChange.Removed;

    public double Indent => Depth * 14.0;

    public string Glyph => IsContainer ? (IsExpanded ? "▾" : "▸") : string.Empty;

    public string Summary => IsContainer
        ? $"{LeafCount} keys"
        : IsScalarArray
            ? LeafCount == 1 ? "1 item" : $"{LeafCount} items"
            : string.Empty;

    public string ChangedTooltip => Change switch
    {
        NodeChange.Removed => "Removed — not yet saved, so it can still be put back",
        NodeChange.Added => IsContainer
            ? "Added — this whole section is new and not yet saved"
            : "Added — not yet saved",
        NodeChange.Edited => "Edited and not yet saved",
        NodeChange.Mixed => "Something under here has been added, edited or removed, and not yet saved",
        _ => string.Empty,
    };

    partial void OnChangeChanged(NodeChange value)
    {
        OnPropertyChanged(nameof(IsChanged));
        OnPropertyChanged(nameof(IsRemoved));
        OnPropertyChanged(nameof(ChangedTooltip));
    }
}

/// <summary>
/// The Tier editor tab: one tier's document, as a hierarchy on the left and replaceable text on the
/// right.
///
/// <para>
/// This is the escape hatch for changes the key-by-key editor cannot express - restructuring a
/// section, pasting a block someone sent you, retyping a subtree wholesale. It edits entirely in
/// memory with a real undo stack, and only the Save button touches disk, through the same writer,
/// guard, backup and typed confirmation as everything else.
/// </para>
/// </summary>
public sealed partial class JsonEditorVm : ObservableObject
{
    private readonly MainVm _main;
    private readonly Dictionary<string, DocumentEditor> _editors = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);

    /// <summary>Names array elements the way the flattener does, so a tree path and a grid path agree.</summary>
    private readonly ArrayStrategies _arrays = ArrayStrategies.Load();

    [ObservableProperty]
    private TierDocument? _tier;

    [ObservableProperty]
    private JsonNodeVm? _selectedNode;

    [ObservableProperty]
    private string _filter = string.Empty;

    /// <summary>
    /// Narrows the tree to what has been edited. Ancestors come with it — a bare list of changed
    /// leaves would tell you what moved without telling you where it lives, and the tree is how this
    /// screen navigates.
    /// </summary>
    [ObservableProperty]
    private bool _showChangedOnly;

    /// <summary>The text of the selected node, as edited. Applying it replaces that node.</summary>
    [ObservableProperty]
    private string _editorText = string.Empty;

    /// <summary>
    /// Renders the pane on one line instead of indented. Display only — nothing compact is ever
    /// written to disk, and what Update parses does not care either way.
    /// </summary>
    [ObservableProperty]
    private bool _compactJson;

    /// <summary>
    /// Wraps long lines in the pane instead of scrolling sideways. On by default: some of these
    /// values are tokens and PEM blocks hundreds of characters long, and reading one of those a
    /// screen-width at a time is not reading it. Off is for the case wrapping is bad at — seeing the
    /// indentation of a deep structure without every long value folding the shape out of it.
    /// </summary>
    [ObservableProperty]
    private bool _wordWrap = true;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private string? _error;

    // ------------------------------------------------------------ find/replace

    /// <summary>
    /// Whether the find bar is showing. A bar rather than a dialog because what it searches is one
    /// pane of a screen you are still navigating: a modal would cover the tree the search results are
    /// meant to be read against.
    /// </summary>
    [ObservableProperty]
    private bool _findOpen;

    [ObservableProperty]
    private string _findText = string.Empty;

    [ObservableProperty]
    private string _replaceText = string.Empty;

    /// <summary>
    /// Off by default. These documents distinguish <c>Url</c> from <c>URL</c> — the writer's ordinal
    /// key order treats them as two keys — so it is worth being able to turn on, and worth not
    /// forcing on someone looking for a value.
    /// </summary>
    [ObservableProperty]
    private bool _matchCase;

    /// <summary>"3 of 12", or why there is nothing to show.</summary>
    [ObservableProperty]
    private string _findStatus = string.Empty;

    partial void OnFindTextChanged(string value) => FindStatus = string.Empty;

    partial void OnMatchCaseChanged(bool value) => FindStatus = string.Empty;

    partial void OnFindOpenChanged(bool value) => FindStatus = string.Empty;

    [RelayCommand]
    private void CloseFind() => FindOpen = false;

    [ObservableProperty]
    private bool _showingComparison;

    public ObservableCollection<TierDocument> Tiers { get; } = [];

    public ObservableCollection<JsonNodeVm> Nodes { get; } = [];

    public ObservableCollection<DiffLineVm> ComparisonLines { get; } = [];

    public ObservableCollection<string> History { get; } = [];

    public JsonEditorVm(MainVm main)
    {
        _main = main;

        foreach (var document in main.Documents)
        {
            Tiers.Add(document);
        }

        // Opens on a writable tier: a tier marked read-only in tiers.json would present a screen
        // whose main action is disabled for reasons not yet on show.
        Tier = Tiers.FirstOrDefault(d => d.Writable) ?? Tiers.FirstOrDefault();
    }

    public DocumentEditor? Editor =>
        Tier is null ? null : _editors.TryGetValue(Tier.Id, out var editor) ? editor : null;

    // ------------------------------------------------------------------ state

    public bool CanEdit => Tier is { Writable: true };

    public string ReadOnlyReason => Tier is null || Tier.Writable
        ? string.Empty
        : $"{Tier.Id} is marked read-only in tiers.json, so this app never writes it. You can read and " +
          "search it here, and compare it against another tier.";

    public bool IsModified => Editor?.IsModified ?? false;

    public bool CanUndo => Editor?.CanUndo ?? false;

    public bool CanRedo => Editor?.CanRedo ?? false;

    /// <summary>
    /// The pane is read-only only for a tier tiers.json marks as such.
    ///
    /// <para>
    /// This screen shows values in clear, secrets included. It has to: a subtree cannot be retyped
    /// without being read, and the tree beside it already carries the fingerprint rendering for
    /// scanning. It is the one place in the app that renders a credential, and it is deliberate.
    /// </para>
    /// </summary>
    public bool IsEditorReadOnly => !CanEdit || SelectedIsRemoved;

    /// <summary>The selected node is a pending removal, shown as it was rather than as it is.</summary>
    public bool SelectedIsRemoved => SelectedNode?.IsRemoved ?? false;

    /// <summary>
    /// True for a single value — a string, number or boolean — as opposed to an object or an array.
    ///
    /// <para>
    /// This is the line between the two ways this pane commits. A scalar is applied as it is typed:
    /// there is no half-typed state worth protecting anyone from, and pressing a button to commit
    /// <c>2</c> is ceremony. A container is not, because a half-typed object is invalid JSON for as
    /// long as it takes to type it, and applying that would either fail constantly or destroy the
    /// node. Those keep the button.
    /// </para>
    /// </summary>
    public bool SelectedIsScalar =>
        SelectedNode is { IsContainer: false, IsScalarArray: false, IsRemoved: false, Path.Length: > 0 };

    /// <summary>How the pane commits, said once where the pane is, so neither behaviour is a surprise.</summary>
    public string CommitHint => (CanEdit, SelectedNode) switch
    {
        (false, _) => string.Empty,
        (_, null) => string.Empty,
        (_, { IsRemoved: true }) => string.Empty,

        (_, { IsContainer: true }) when SelectedIsElement =>
            "Press Update node to apply. This is one element of an array: it can be replaced in place, " +
            "but not removed — deleting one shifts every element after it, so remove it from the array's own JSON.",

        (_, { IsContainer: true }) =>
            "Press Update node to apply — a section is not applied as you type, because it is invalid JSON for as long as it takes to type one.",

        (_, { IsScalarArray: true }) =>
            "Press Update node to apply. This is one list rather than a set of rows: its elements have " +
            "no identity to name them by, so it is read and changed whole.",

        _ => "Applied as you type. Update node is the fallback for a shape change — a section typed " +
             "into a value's pane.",
    };

    /// <summary>
    /// Removing something twice is not a thing, so the button goes away for a tombstone — and it is
    /// not offered for an array element, which cannot be deleted without shifting its siblings.
    /// </summary>
    public bool CanRemoveNode =>
        CanEdit &&
        SelectedNode is { IsRemoved: false, Path.Length: > 0 } node &&
        !DocumentEditor.IsArrayElement(node.Path);

    /// <summary>True when the selection is one element of an array rather than a key.</summary>
    public bool SelectedIsElement =>
        SelectedNode is { Path.Length: > 0 } node && DocumentEditor.IsArrayElement(node.Path);

    /// <summary>
    /// Whether the pane holds something the document does not already say. Compared after parsing
    /// and re-serializing both sides, so switching the pane between pretty and compact — or
    /// reindenting by hand — is not mistaken for an edit.
    /// </summary>
    private bool _textDiffers;

    /// <summary>
    /// Why the pane does not parse, or empty when it does. This is the whole of what stands between
    /// the pane and Update node.
    ///
    /// <para>
    /// Update used to stay lit on text that does not parse, so that pressing it produced the real
    /// error. That made the button the only way to find out, and made an unpressable state look
    /// exactly like a pressable one: the same button, offering to replace a node with something that
    /// cannot be read. Saying it here instead means the answer is on screen without pressing
    /// anything, and Update is offered only when it would work.
    /// </para>
    ///
    /// <para>
    /// It is not <see cref="Error"/>. Half-typed JSON is a value on its way in, not a failure — the
    /// pane reports it in its own strip, quietly, rather than raising the banner that means something
    /// went wrong.
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditorProblem))]
    private string _editorProblem = string.Empty;

    /// <summary>Whether there is anything to say about the pane's text — for a line that hides itself.</summary>
    public bool HasEditorProblem => EditorProblem.Length > 0;

    public bool CanApply =>
        CanEdit && SelectedNode is { IsRemoved: false } && _textDiffers && !HasEditorProblem;

    /// <summary>Offered only for a node that actually has changes of its own to throw away.</summary>
    public bool CanRevertNode => CanEdit && SelectedNode is { IsChanged: true };

    /// <summary>
    /// The same action reads differently on a tombstone, so the button says which one it is:
    /// putting a deleted node back is not "undoing changes", it is restoring it.
    /// </summary>
    public string RevertNodeLabel => SelectedIsRemoved ? "Restore node" : "Undo node changes";

    /// <summary>
    /// Whether there is anything at the selected node to compare. With nothing selected the scope is
    /// the whole document, which is also what the root row means.
    /// </summary>
    public bool CanCompare => SelectedNode is { } node ? node.IsChanged : IsModified;

    /// <summary>What the comparison is scoped to — the selected node, or the whole document.</summary>
    private string ComparisonPath => SelectedNode?.Path ?? string.Empty;

    /// <summary>
    /// The right-hand pane's own label. It names the scope while comparing, because a node-scoped
    /// diff that does not say which node it is showing is indistinguishable from a stale one.
    /// </summary>
    public string PaneHeader
    {
        get
        {
            if (!ShowingComparison)
            {
                // Kept short: the toolbar beside it grew a button, and a header that ellipsises
                // mid-warning warns nobody. The full sentence is on the label's tooltip.
                return "SELECTED NODE — REPLACE IT WHOLESALE";
            }

            // The path keeps its own casing while the label around it is upper-cased: this file
            // treats Url and URL as two keys, and a header that flattened the difference would be
            // the one place in the app quietly claiming otherwise.
            var where = ComparisonPath.Length == 0 ? "WHOLE DOCUMENT" : ComparisonPath;
            return $"AS OPENED  |  AS EDITED  —  {where}";
        }
    }

    /// <summary>
    /// Why this tier could not be uploaded, worked out once per tier rather than per binding —
    /// answering it means reading two settings files, and this is bound from a button's tooltip.
    /// </summary>
    private string? _pushBlocked;

    /// <summary>
    /// Push is the only way an edit here leaves this window.
    ///
    /// <para>
    /// There is no Save, because there is nowhere to save to: a tier is a Vault secret and nothing
    /// else. What that removes is the state this app used to be able to get into — an edited file on
    /// disk that was neither what Vault held nor what anyone had decided to upload, sitting there
    /// looking like a tier.
    /// </para>
    /// </summary>
    public bool CanPush => _pushBlocked is null && IsModified;

    public string PushHint => _pushBlocked
                              ?? (IsModified
                                  ? $"Write this document to {PushDestination}. You will see it diffed against " +
                                    "what that source holds now, and confirm, before anything is sent."
                                  : "Nothing has been changed yet.");

    /// <summary>Where the push would land — the tier's secret, or its file.</summary>
    private string PushDestination => Tier?.Definition.Kind == SourceKind.LocalFile
        ? Tier.Definition.LocalFilePath ?? "its file"
        : Tier?.Definition.VaultPath ?? "its secret";

    public string StatusLine
    {
        get
        {
            if (Tier is null)
            {
                return "No tier selected.";
            }

            var head = $"{Tier.Id}: {Tier.SourceLine}";
            var edits = _main.Edits.For(Tier.Id).Count;

            // The two editing models are separate on purpose, so a tier with queued key edits and an
            // open document edit is a state worth naming rather than silently merging.
            return edits == 0
                ? head
                : head + $"  ·  {edits} key change(s) also queued on the All tiers tab — those are separate from this.";
        }
    }

    // ----------------------------------------------------------------- events

    partial void OnTierChanged(TierDocument? value)
    {
        Error = null;
        Message = null;
        ShowingComparison = false;
        _collapsed.Clear();
        _collapsedWhileFiltering.Clear();

        if (value is not null && !_editors.ContainsKey(value.Id))
        {
            _editors[value.Id] = new DocumentEditor(value);
        }

        _pushBlocked = SourceProviders.For(value, _main.Flattener)
            .Blocked(value, VaultSettingsStore.Load().Settings);

        RebuildTree();

        // Deliberately nothing selected. The first row is the whole document, and opening onto it
        // would fill the pane with 28 KB of JSON before anyone had asked for anything - the point of
        // the hierarchy is to get to one part of it.
        SelectedNode = null;
        EditorText = string.Empty;
        Message = "Pick a node on the left to see its JSON. The top row is the whole document.";

        NotifyState();
    }

    partial void OnSelectedNodeChanged(JsonNodeVm? value)
    {
        Error = null;

        // Moving the selection ends the run of keystrokes the undo stack was folding into one step.
        // Coming back to the same node later is a second edit, not a continuation of the first.
        _instantPath = null;

        LoadEditorText();

        OnPropertyChanged(nameof(CanRevertNode));
        OnPropertyChanged(nameof(CanRemoveNode));
        OnPropertyChanged(nameof(SelectedIsRemoved));
        OnPropertyChanged(nameof(SelectedIsScalar));
        OnPropertyChanged(nameof(SelectedIsElement));
        OnPropertyChanged(nameof(CommitHint));
        OnPropertyChanged(nameof(RevertNodeLabel));
        OnPropertyChanged(nameof(IsEditorReadOnly));
        OnPropertyChanged(nameof(CanCompare));
        OnPropertyChanged(nameof(PaneHeader));

        if (!ShowingComparison)
        {
            return;
        }

        // The comparison is scoped to the selection, so moving the selection moves it. Landing on a
        // node with nothing to show drops back to the editor rather than leaving an empty diff up
        // beside a button that is now disabled and could not be pressed to get out of it.
        if (CanCompare)
        {
            RebuildComparison();
        }
        else
        {
            ShowingComparison = false;
        }
    }

    partial void OnEditorTextChanged(string value)
    {
        // Loading a node into the pane is not someone typing into it.
        if (_loadingText)
        {
            return;
        }

        RecomputeTextDiffers();

        if (_textDiffers && SelectedIsScalar)
        {
            ApplyAsTyped();
        }

        OnPropertyChanged(nameof(CanApply));
    }

    /// <summary>True while the pane is being filled from the document, so the write-back is not re-entered.</summary>
    private bool _loadingText;

    /// <summary>
    /// The node the current run of keystrokes is editing, or null when there is no run in progress.
    /// It is what lets a value typed one character at a time land on the undo stack as one step
    /// rather than as one step per pause in typing.
    /// </summary>
    private string? _instantPath;

    /// <summary>
    /// Commits a scalar the moment it parses, so changing Redis:Database from 0 to 2 is one
    /// keystroke rather than a keystroke and a button.
    ///
    /// <para>
    /// Text that does not parse is not an error here — it is a value halfway through being typed, and
    /// a red banner on every intermediate keystroke would make typing feel broken. It simply does not
    /// commit, and the reason is said quietly under the pane by <see cref="EditorProblem"/>, which is
    /// also what holds Update node off until it clears.
    /// </para>
    ///
    /// <para>
    /// A shape change is deliberately excluded. Typing an object into a scalar's pane is a real
    /// operation, but it is invalid JSON on the way in and it is not what "edit this value" means —
    /// it keeps the button.
    /// </para>
    /// </summary>
    private void ApplyAsTyped()
    {
        if (Editor is null || !CanEdit || SelectedNode is not { } node)
        {
            return;
        }

        JsonNode? parsed;
        try
        {
            parsed = OrdinalJsonWriter.ParseAllowingNull(EditorText);
        }
        catch
        {
            // Said under the pane by EditorProblem, with the reader's own reason for it. Cleared
            // rather than added to: "Redis:Database updated" from the previous keystroke would
            // otherwise sit there claiming this one landed as well.
            Message = null;
            return;
        }

        if (parsed is JsonObject or JsonArray)
        {
            Message = "That turns this value into a section — press Update node to apply a shape change.";
            return;
        }

        try
        {
            Editor.Replace(node.Path, EditorText, coalesce: _instantPath == node.Path);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return;
        }

        _instantPath = node.Path;
        Error = null;
        Message = $"{node.Path} updated. Nothing has been written yet.";

        // The marks are refreshed on the rows that already exist rather than by rebuilding the tree.
        // A rebuild replaces every row, which reselects, which reloads the pane — and throws the
        // caret back to the start of the value being typed.
        RefreshMarksInPlace(node);

        RecomputeTextDiffers();
        NotifyState();
    }

    /// <summary>
    /// Re-marks the rows on screen against the document as it now stands, leaving the rows themselves
    /// in place. Every row is re-asked rather than just the edited one, because an edit changes its
    /// ancestors' marks too — and un-marks them again when a value is typed back to what it was.
    /// </summary>
    private void RefreshMarksInPlace(JsonNodeVm edited)
    {
        if (Editor is null)
        {
            return;
        }

        _kinds = Editor.ChangeKinds();

        foreach (var node in Nodes)
        {
            node.Change = KindOf(node.Path);
        }

        edited.Preview = PreviewOf(edited.Path, Editor.Find(edited.Path));
    }

    /// <summary>
    /// Re-renders whatever is in the pane in the other format, rather than reloading the node.
    /// Switching how JSON is displayed must not throw away an edit in progress.
    /// </summary>
    partial void OnCompactJsonChanged(bool value)
    {
        if (string.IsNullOrWhiteSpace(EditorText))
        {
            return;
        }

        try
        {
            var node = OrdinalJsonWriter.Parse(EditorText);
            EditorText = value
                ? OrdinalJsonWriter.SerializeCompactToText(node)
                : OrdinalJsonWriter.SerializeToText(node);
        }
        catch
        {
            // Half-typed JSON cannot be reformatted, and losing it to a display toggle would be
            // worse than leaving it as it is until it parses again.
        }
    }

    /// <summary>
    /// A changed filter is a different tree, so what was collapsed in the previous one is dropped
    /// rather than carried over. Keeping it would mean a section you closed while searching for one
    /// thing is closed again — and closed for no visible reason — while searching for another.
    /// </summary>
    partial void OnFilterChanged(string value)
    {
        _collapsedWhileFiltering.Clear();
        RebuildTree();
    }

    partial void OnShowChangedOnlyChanged(bool value)
    {
        _collapsedWhileFiltering.Clear();
        RebuildTree();
    }

    /// <summary>
    /// The comparison is a mode rather than an action, so it is driven by the flag the button binds
    /// to: flipping it on is what builds the diff, and everything that changes the document while it
    /// is on refreshes it rather than leaving a stale one on screen.
    /// </summary>
    partial void OnShowingComparisonChanged(bool value)
    {
        if (value)
        {
            RebuildComparison();
        }
        else
        {
            // The message on screen is the comparison's own summary, and it names a scope. Leaving
            // it up beside the editor would have it describing a node nobody is looking at.
            ComparisonLines.Clear();
            Message = null;
        }

        OnPropertyChanged(nameof(PaneHeader));
    }

    // --------------------------------------------------------------- commands

    [RelayCommand]
    private void ToggleNode(JsonNodeVm? node)
    {
        if (node is null || !node.IsContainer)
        {
            return;
        }

        // Whichever set the current view is reading. A section collapsed while searching stays
        // collapsed for that search and does not follow you back out of it, because the two trees
        // are not the same tree.
        if (!Collapsed.Remove(node.Path))
        {
            Collapsed.Add(node.Path);
        }

        RebuildTree();
    }

    [RelayCommand]
    private void ExpandAll()
    {
        Collapsed.Clear();
        RebuildTree();
    }

    [RelayCommand]
    private void CollapseAll()
    {
        if (Editor is null)
        {
            return;
        }

        var collapsed = Collapsed;
        foreach (var node in Nodes.Where(n => n is { IsContainer: true, Depth: > 0 }))
        {
            collapsed.Add(node.Path);
        }

        RebuildTree();
    }

    /// <summary>
    /// Copies the pane to the clipboard — what is on screen, edits included, rather than what the
    /// document holds. The pane is the thing being looked at, and copying something subtly different
    /// from it would be the surprise.
    /// </summary>
    [RelayCommand]
    private void CopyNode()
    {
        if (EditorText.Length == 0)
        {
            return;
        }

        try
        {
            // The host's clipboard. On WPF this still sets the copy flag, so the text outlives this
            // process — which is what anyone pasting into an editor afterwards expects.
            JsonInsight.Platform.Platform.Clipboard.SetText(EditorText);
        }
        catch (Exception ex)
        {
            // Another process can hold the clipboard open. That is not this app's fault and not
            // worth an exception dialog, but it does mean nothing was copied, so it has to say so.
            Error = $"Could not copy: {ex.Message}";
            return;
        }

        Error = null;

        var where = SelectedNode is { Path.Length: > 0 } node ? node.Path : "the whole document";
        Message = $"Copied {where} — {EditorText.Length:N0} characters.";
    }

    /// <summary>
    /// Puts the node's current text back in the pane, discarding whatever was typed.
    ///
    /// <para>
    /// This is not <see cref="RevertNode"/>, and the difference is which of the two things in front
    /// of you it acts on. This one throws away the <em>text</em> and leaves the document exactly as
    /// it is; that one throws away the applied <em>changes</em> and puts the node back the way it was
    /// when the tier was opened. One is "forget what I typed", the other is "forget what I applied".
    /// </para>
    /// </summary>
    [RelayCommand]
    private void ReloadNode()
    {
        LoadEditorText();
        Error = null;
        Message = "Reloaded this node from the document.";
    }

    [RelayCommand]
    private void Apply()
    {
        if (Editor is null || SelectedNode is null)
        {
            return;
        }

        var path = SelectedNode.Path;
        try
        {
            Editor.Replace(path, EditorText);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return;
        }

        Error = null;
        Message = $"Replaced {(path.Length == 0 ? "the whole document" : path)}. " +
                  "Nothing has been written yet.";

        AfterChange(reselect: path);
    }

    [RelayCommand]
    private void RemoveNode()
    {
        if (Editor is null || SelectedNode is not { Path.Length: > 0 } node)
        {
            return;
        }

        try
        {
            Editor.Remove(node.Path);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return;
        }

        Error = null;
        Message = $"Removed {node.Path}. Nothing has been written yet.";
        AfterChange(reselect: ConfigPath.Parent(node.Path));
    }

    /// <summary>
    /// Throws away the changes made to the selected node, leaving every other edit alone. Distinct
    /// from Undo, which walks the history backwards and would take unrelated later edits with it.
    /// </summary>
    [RelayCommand]
    private void RevertNode()
    {
        if (Editor is null || SelectedNode is not { IsChanged: true } node)
        {
            return;
        }

        var wasRemoved = node.IsRemoved;
        var path = wasRemoved ? OutermostRemoved(node.Path) : node.Path;

        try
        {
            Editor.RevertNode(path);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return;
        }

        Error = null;
        Message = (wasRemoved, path.Length) switch
        {
            (true, _) when !path.Equals(node.Path, StringComparison.Ordinal) =>
                $"Put {path} back — {node.Path} was inside it, so the whole removed section was restored.",

            (true, _) => $"Put {path} back. Other edits are untouched.",

            (false, 0) => "Undid every change to this document.",

            _ => $"Undid the changes to {path}. Other edits are untouched.",
        };

        AfterChange(reselect: path);
    }

    [RelayCommand]
    private void Undo()
    {
        if (Editor is not { CanUndo: true } editor)
        {
            return;
        }

        var step = editor.History[^1];
        editor.Undo();

        Error = null;
        Message = $"Undid: {step.Description}.";
        AfterChange(reselect: step.Path);
    }

    [RelayCommand]
    private void Redo()
    {
        if (Editor is not { CanRedo: true } editor)
        {
            return;
        }

        editor.Redo();
        Error = null;
        Message = "Redid the last undone change.";
        AfterChange(reselect: SelectedNode?.Path ?? string.Empty);
    }

    [RelayCommand]
    private void RevertAll()
    {
        if (Editor is not { IsModified: true } editor)
        {
            return;
        }

        editor.RevertAll();
        Error = null;
        Message = "Reverted every change back to the state this tier was opened in.";
        AfterChange(reselect: string.Empty);
    }

    /// <summary>
    /// Flips the right-hand pane between the editor and a line diff against the state this tier was
    /// opened in. A mode, not an action: the button stays lit while it is on, and pressing it again
    /// returns to the editor.
    /// </summary>
    [RelayCommand]
    private void ToggleComparison() => ShowingComparison = !ShowingComparison;

    /// <summary>
    /// Builds the diff for whatever is selected — that node's subtree, or the whole document when the
    /// selection is the root row. Scoped rather than always whole-document because the editor is
    /// navigated one node at a time, and a diff of 28 KB does not answer "what did I just do to
    /// this".
    ///
    /// <para>
    /// Both sides go through the same writer, so what shows up is a real content difference rather
    /// than a formatting artefact. A node that only exists on one side contributes the empty string
    /// on the other, which renders it as a whole-block insertion or deletion.
    /// </para>
    /// </summary>
    private void RebuildComparison()
    {
        ComparisonLines.Clear();

        if (Editor is not { } editor)
        {
            return;
        }

        var path = ComparisonPath;

        var model = SideBySideDiffBuilder.Instance.BuildDiffModel(
            editor.OriginalTextOrEmpty(path), editor.WorkingTextOrEmpty(path), ignoreWhitespace: false);

        for (var i = 0; i < Math.Max(model.OldText.Lines.Count, model.NewText.Lines.Count); i++)
        {
            var oldLine = i < model.OldText.Lines.Count ? model.OldText.Lines[i] : null;
            var newLine = i < model.NewText.Lines.Count ? model.NewText.Lines[i] : null;

            var type = RowType(oldLine?.Type, newLine?.Type);
            if (type is ChangeType.Unchanged or ChangeType.Imaginary)
            {
                continue;
            }

            ComparisonLines.Add(new DiffLineVm(
                oldLine?.Position?.ToString() ?? string.Empty,
                oldLine?.Text ?? string.Empty,
                newLine?.Position?.ToString() ?? string.Empty,
                newLine?.Text ?? string.Empty,
                type));
        }

        var removed = ComparisonLines.Count(l => l.Type == ChangeType.Deleted);
        var added = ComparisonLines.Count(l => l.Type == ChangeType.Inserted);
        var modified = ComparisonLines.Count(l => l.Type == ChangeType.Modified);

        var where = path.Length == 0 ? "this document" : path;

        Message = ComparisonLines.Count == 0
            ? $"No differences — {where} matches the state it was opened in."
            : $"{where}, against the state this tier was opened in: " +
              $"{modified} changed, {added} added, {removed} removed.";
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

    // ---------------------------------------------------------------- helpers

    private void AfterChange(string reselect)
    {
        RebuildTree();

        // Assigning the same-valued node does not raise a change notification, and the rebuilt
        // instance carries a fresh IsChanged - so the reload is forced rather than assumed.
        SelectedNode = null;
        SelectedNode = Nodes.FirstOrDefault(n => n.Path.Equals(reselect, StringComparison.Ordinal))
                       ?? Nodes.FirstOrDefault();

        if (ShowingComparison)
        {
            // Reselecting already refreshed it, but a reselect that lands on the same path it left
            // is not a change this can rely on - and a stale diff is worse than a redundant rebuild.
            RebuildComparison();
        }

        NotifyState();
    }

    private void NotifyState()
    {
        History.Clear();
        foreach (var step in Editor?.History.AsEnumerable().Reverse() ?? [])
        {
            History.Add(step.Description);
        }

        OnPropertyChanged(nameof(Editor));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(ReadOnlyReason));
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(CanPush));
        OnPropertyChanged(nameof(PushHint));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanRevertNode));
        OnPropertyChanged(nameof(CanRemoveNode));
        OnPropertyChanged(nameof(CanCompare));
        OnPropertyChanged(nameof(SelectedIsRemoved));
        OnPropertyChanged(nameof(RevertNodeLabel));
        OnPropertyChanged(nameof(PaneHeader));
        OnPropertyChanged(nameof(IsEditorReadOnly));
        OnPropertyChanged(nameof(StatusLine));
    }

    private void LoadEditorText()
    {
        _loadingText = true;
        try
        {
            LoadEditorTextCore();
        }
        finally
        {
            _loadingText = false;
            RecomputeTextDiffers();
            OnPropertyChanged(nameof(CanApply));
        }
    }

    private void LoadEditorTextCore()
    {
        if (Editor is null || SelectedNode is null)
        {
            EditorText = string.Empty;
            return;
        }

        var path = SelectedNode.Path;
        var removed = SelectedNode.IsRemoved;

        try
        {
            // Exactly the selected node's subtree, in the writer's canonical form - so pasting it
            // straight back is a no-op rather than a reformat. A tombstone has nothing in the edited
            // document, so it shows what it held when the tier was opened, read-only.
            if (!(removed ? Editor.HoldsOriginal(path) : Editor.Holds(path)))
            {
                throw new InvalidOperationException($"'{path}' is not in this document.");
            }

            var node = removed ? Editor.FindOriginal(path) : Editor.Find(path);

            // A key whose value is JSON null is present and holds null; the node for it is a null
            // reference, so the literal has to be written rather than serialized.
            EditorText = node is null
                ? "null"
                : CompactJson
                    ? OrdinalJsonWriter.SerializeCompactToText(node)
                    : OrdinalJsonWriter.SerializeToText(node);
        }
        catch (Exception ex)
        {
            EditorText = string.Empty;
            Error = ex.Message;
        }
    }

    /// <summary>
    /// Decides both of the things Update depends on: whether the pane says anything the document
    /// does not, and whether what it says can be read at all.
    ///
    /// <para>
    /// Both sides are parsed and re-serialized before comparing, so a reformat is not an edit. Text
    /// that does not parse is still a difference — the pane no longer says what the document says —
    /// but it is not one Update can act on, so it lands in <see cref="EditorProblem"/> and the button
    /// waits for it to clear.
    /// </para>
    /// </summary>
    private void RecomputeTextDiffers()
    {
        if (Editor is null || SelectedNode is null || SelectedNode.IsRemoved)
        {
            _textDiffers = false;
            EditorProblem = string.Empty;
            return;
        }

        try
        {
            var current = Editor.TextAt(SelectedNode.Path);
            var parsed = OrdinalJsonWriter.ParseAllowingNull(EditorText);
            var typed = parsed is null ? "null" : OrdinalJsonWriter.SerializeToText(parsed);

            _textDiffers = !typed.Equals(current, StringComparison.Ordinal);
            EditorProblem = string.Empty;
        }
        catch (Exception ex)
        {
            // Still a difference — the pane no longer says what the document says — but not one
            // Update can act on. Both facts are recorded, and CanApply needs both.
            _textDiffers = true;
            EditorProblem = Unparseable(ex);
        }
    }

    /// <summary>
    /// What to put under the pane when it does not parse. The reader's own message names the
    /// character and the position, which is the useful half; the sentence in front of it says what
    /// that means for the button, which is the half a JSON reader has no idea about.
    /// </summary>
    private static string Unparseable(Exception ex) =>
        $"Not valid JSON, so there is nothing to apply — {ex.Message}";

    private void RebuildTree()
    {
        Nodes.Clear();

        if (Editor is null)
        {
            return;
        }

        _kinds = Editor.ChangeKinds();
        _removedPaths.Clear();

        Emit(Editor.Working, string.Empty, Tier!.Id, 0, VisiblePaths());

        OnPropertyChanged(nameof(FilterHint));
    }

    private IReadOnlyDictionary<string, NodeChange> _kinds =
        new Dictionary<string, NodeChange>(StringComparer.Ordinal);

    /// <summary>What happened to a path since the tier was opened; <c>None</c> for anything untouched.</summary>
    private NodeChange KindOf(string path) =>
        _kinds.TryGetValue(path, out var kind) ? kind : NodeChange.None;

    /// <summary>Paths currently shown as tombstones, filled while the tree is built.</summary>
    private readonly HashSet<string> _removedPaths = new(StringComparer.Ordinal);

    /// <summary>
    /// The outermost removed node at or above a path.
    ///
    /// <para>
    /// Un-removing a key from deep inside a deleted section would otherwise recreate its parents
    /// holding nothing but that one key — a partial restore nobody asked for. Restoring the whole
    /// thing that was deleted is the only answer that is not surprising.
    /// </para>
    /// </summary>
    private string OutermostRemoved(string path)
    {
        // Ancestors yields outermost first, so the first hit is the top of the deleted subtree.
        foreach (var ancestor in ConfigPath.Ancestors(path))
        {
            if (_removedPaths.Contains(ancestor))
            {
                return ancestor;
            }
        }

        return path;
    }

    /// <summary>
    /// The paths the tree may show, or null for "everything". The two filters compose: with both on,
    /// you get the changed nodes whose paths also match the search.
    /// </summary>
    private HashSet<string>? VisiblePaths()
    {
        var searched = MatchingPaths();

        if (!ShowChangedOnly)
        {
            return searched;
        }

        // ChangeKinds already carries every ancestor of every change, so this stays a tree.
        var changed = new HashSet<string>(_kinds.Keys, StringComparer.Ordinal);
        if (searched is not null)
        {
            changed.IntersectWith(searched);
        }

        return changed;
    }

    /// <summary>Says why the tree is empty, which is otherwise indistinguishable from a bug.</summary>
    public string FilterHint
    {
        get
        {
            if (ShowChangedOnly && _kinds.Count == 0)
            {
                return "Nothing has been changed yet.";
            }

            return Nodes.Count <= 1 && (Filter.Length > 0 || ShowChangedOnly)
                ? "Nothing matches."
                : string.Empty;
        }
    }

    /// <summary>
    /// The paths the filter admits, plus every ancestor of each. Keeping ancestors is what makes a
    /// filtered tree still a tree - a hit six levels down with its parents stripped away tells you a
    /// key exists but not which section it belongs to, which is the question you were asking.
    ///
    /// <para>
    /// Both trees are searched, not just the edited one. A removed key is not in the edited document
    /// at all, so searching that alone made a filter hide exactly the rows this screen works hardest
    /// to keep visible: a deletion would vanish from the tree instead of showing as a tombstone, and
    /// the one edit you cannot see would also be the one you cannot take back.
    /// </para>
    /// </summary>
    private HashSet<string>? MatchingPaths()
    {
        if (Filter.Length == 0 || Editor is null || Tier is null)
        {
            return null;
        }

        var keep = new HashSet<string>(StringComparer.Ordinal);
        var isGlob = Filter.Contains('*', StringComparison.Ordinal);

        var paths = _main.Flattener.Flatten(Tier.Id, Editor.Working).Paths
            .Union(_main.Flattener.Flatten(Tier.Id, Editor.Original).Paths, StringComparer.Ordinal);

        foreach (var path in paths)
        {
            var hit = isGlob
                ? PathGlob.IsMatch(path, Filter)
                : path.Contains(Filter, StringComparison.OrdinalIgnoreCase);

            if (!hit)
            {
                continue;
            }

            keep.Add(path);
            foreach (var ancestor in ConfigPath.Ancestors(path))
            {
                keep.Add(ancestor);
            }
        }

        return keep;
    }

    private void Emit(JsonNode? node, string path, string tierId, int depth, HashSet<string>? matches)
    {
        // The root row, so "replace the whole document" is reachable from the tree rather than
        // being a separate mode.
        Nodes.Add(new JsonNodeVm
        {
            Name = tierId,
            Path = string.Empty,
            Depth = 0,
            IsContainer = true,
            LeafCount = Tier?.Flat.Count ?? 0,
            Preview = "(whole document)",
            ContainsSecret = true,
            Change = KindOf(string.Empty),
            IsExpanded = !Collapsed.Contains(string.Empty),
        });

        if (Collapsed.Contains(string.Empty))
        {
            return;
        }

        // A document is not always an object. A banner list or a service catalogue is a JSON array
        // at the root, and this used to walk it as an object, find nothing, and render a hierarchy
        // with one row in it - the one shape of document where the tree said nothing at all.
        if (node is JsonArray rootArray)
        {
            EmitElements(rootArray, path, depth + 1, matches, insideRemoved: false);
            return;
        }

        EmitChildren(node, Editor!.Original, path, depth + 1, matches, insideRemoved: false);
    }

    /// <summary>
    /// Which nodes are collapsed, which is a different question while a filter is on.
    ///
    /// <para>
    /// A filtered tree opens expanded, because a match hidden inside a collapsed parent is a filter
    /// that lied about what it found. That used to be done by ignoring collapse state entirely,
    /// which made the expander dead for as long as a search was in the box - and a search that finds
    /// two hundred rows is exactly when collapsing a section is worth doing. So the filtered view
    /// gets its own set instead: empty by default, so it still opens expanded, and emptied again
    /// whenever the filter changes, because it describes a tree that no longer exists.
    /// </para>
    /// </summary>
    private HashSet<string> Collapsed => IsFiltering ? _collapsedWhileFiltering : _collapsed;

    private bool IsFiltering => Filter.Length > 0 || ShowChangedOnly;

    private readonly HashSet<string> _collapsedWhileFiltering = new(StringComparer.Ordinal);

    /// <summary>
    /// Walks the edited tree and the opened one together.
    ///
    /// <para>
    /// A key the opened document has and the edited one does not is not simply gone: it is a pending
    /// removal, and until the file is saved it is still undoable. Dropping it out of the tree would
    /// make the one edit you cannot see also the one you cannot take back, so it stays as a
    /// tombstone — rendered from the opened document, struck through, and restorable.
    /// </para>
    /// </summary>
    private void EmitChildren(
        JsonNode? working,
        JsonNode? original,
        string path,
        int depth,
        HashSet<string>? matches,
        bool insideRemoved)
    {
        var live = working as JsonObject;
        var was = original as JsonObject;

        if (live is null && was is null)
        {
            return;
        }

        var keys = (live?.Select(p => p.Key) ?? [])
            .Union(was?.Select(p => p.Key) ?? [], StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        foreach (var key in keys)
        {
            var childPath = path.Length == 0 ? key : $"{path}:{key}";

            if (matches is not null && !matches.Contains(childPath))
            {
                continue;
            }

            // TryGetPropertyValue rather than the indexer: a key holding JSON null is present, and
            // reading it as absent would put a tombstone over a live setting.
            var inLive = live is not null && live.TryGetPropertyValue(key, out var liveChild);
            var inWas = was is not null && was.TryGetPropertyValue(key, out var wasChild);

            liveChild = inLive ? live![key] : null;
            wasChild = inWas ? was![key] : null;

            var removed = insideRemoved || (!inLive && inWas);
            var shown = removed ? wasChild : liveChild;

            var isContainer = IsContainer(childPath, shown);
            var expanded = isContainer && !Collapsed.Contains(childPath);

            if (removed)
            {
                _removedPaths.Add(childPath);
            }

            Nodes.Add(new JsonNodeVm
            {
                Name = key,
                Path = childPath,
                Depth = depth,
                IsContainer = isContainer,
                IsScalarArray = !isContainer && shown is JsonArray,
                LeafCount = isContainer ? CountLeaves(shown) : (shown as JsonArray)?.Count ?? 0,
                Preview = removed ? "(removed)" : PreviewOf(childPath, shown),
                ContainsSecret = ContainsSecret(childPath, shown),
                Change = KindOf(childPath),
                IsExpanded = expanded,
            });

            if (expanded && shown is JsonObject)
            {
                EmitChildren(
                    removed ? null : liveChild,
                    wasChild,
                    childPath,
                    depth + 1,
                    matches,
                    removed);
            }
            else if (expanded && shown is JsonArray array)
            {
                EmitElements(array, childPath, depth + 1, matches, removed);
            }
        }
    }

    /// <summary>
    /// Whether a value gets an expander, and children, in the hierarchy.
    ///
    /// <para>
    /// An object always does. An array does only when the flattener splits it into elements — which is
    /// the same question <see cref="ArrayStrategies.IsSingleLeaf"/> answers for the flattener itself,
    /// asked here so the two cannot drift apart. A declared <c>stringSet</c> such as a Couchbase
    /// <c>Scopes</c> list is one leaf everywhere else in the app, so it is one row here too, showing
    /// its members as text rather than expanding into a row per string that no other tab has.
    /// </para>
    ///
    /// <para>
    /// Arrays of objects are untouched: <c>Serilog:WriteTo</c> still expands, because the flattener
    /// still produces a leaf path per sink and the tree still has somewhere to send you.
    /// </para>
    /// </summary>
    private bool IsContainer(string path, JsonNode? value) => value switch
    {
        JsonObject => true,
        JsonArray array => !_arrays.IsSingleLeaf(path, array),
        _ => false,
    };

    /// <summary>
    /// One row per array element.
    ///
    /// <para>
    /// The tree used to stop at an array and show only <c>[12]</c>, which meant the only way to see
    /// what was in one was to select it and read the whole thing as text — for the arrays in these
    /// documents, several hundred lines of it. Everything else in the app already addresses these
    /// elements: the flattener produces a leaf path for each, the grid has a row for each, and
    /// <see cref="JsonNavigator"/> resolves both path forms. The hierarchy was the one place that
    /// did not.
    /// </para>
    ///
    /// <para>
    /// The paths are the flattener's, identity form included, so a row here and a row on the All
    /// tiers tab are the same path rather than two spellings of one.
    /// </para>
    /// </summary>
    private void EmitElements(JsonArray array, string path, int depth, HashSet<string>? matches, bool insideRemoved)
    {
        // Only a changed array can hold a changed element, so an untouched one skips the comparison
        // rather than serializing every element to prove nothing happened.
        var arrayChanged = !insideRemoved && KindOf(path) != NodeChange.None;
        var identityField = _arrays.For(path) is { Kind: ArrayKind.KeyedObjects } keyed ? keyed.IdentityField : null;

        for (var i = 0; i < array.Count; i++)
        {
            var element = array[i];
            var childPath = ElementPath(path, element, i, identityField);

            if (matches is not null && !matches.Contains(childPath))
            {
                continue;
            }

            var isContainer = IsContainer(childPath, element);
            var expanded = isContainer && !Collapsed.Contains(childPath);

            if (insideRemoved)
            {
                _removedPaths.Add(childPath);
            }

            Nodes.Add(new JsonNodeVm
            {
                Name = childPath[path.Length..],
                Path = childPath,
                Depth = depth,
                IsContainer = isContainer,
                IsScalarArray = !isContainer && element is JsonArray,
                LeafCount = isContainer ? CountLeaves(element) : (element as JsonArray)?.Count ?? 0,
                Preview = insideRemoved ? "(removed)" : ElementPreview(childPath, element),
                ContainsSecret = ContainsSecret(childPath, element),
                Change = insideRemoved ? NodeChange.Removed : ElementChange(childPath, arrayChanged, isContainer),
                IsExpanded = expanded,
            });

            if (!expanded)
            {
                continue;
            }

            if (element is JsonObject)
            {
                EmitChildren(element, Editor!.FindOriginal(childPath), childPath, depth + 1, matches, insideRemoved);
            }
            else if (element is JsonArray nested)
            {
                EmitElements(nested, childPath, depth + 1, matches, insideRemoved);
            }
        }
    }

    /// <summary>
    /// The flattener's name for an element: by identity where the array declares one, by position
    /// otherwise. The identity form is what makes a mark survive an insertion above it.
    /// </summary>
    private static string ElementPath(string path, JsonNode? element, int index, string? identityField)
    {
        if (identityField is not null &&
            element is JsonObject item &&
            item[identityField] is JsonValue value &&
            value.TryGetValue<string>(out var id))
        {
            return $"{path}[{identityField}={id}]";
        }

        return $"{path}[{index}]";
    }

    /// <summary>
    /// What happened to one element, compared against the document as opened.
    ///
    /// <para>
    /// <see cref="DocumentEditor.ChangeKinds"/> compares an array whole — an array's elements are not
    /// separately addressable there, so "the array changed" is the finest true statement it makes.
    /// The tree can be finer because it has resolved the element already, so this asks the narrower
    /// question directly. Under an index-named array an insertion still marks everything after it,
    /// which is the same thing the grid reports and what a strategy in arrays.json exists to fix.
    /// </para>
    /// </summary>
    private NodeChange ElementChange(string path, bool arrayChanged, bool isContainer)
    {
        if (!arrayChanged || Editor is null)
        {
            return NodeChange.None;
        }

        var was = Editor.FindOriginal(path);
        var now = Editor.Find(path);

        if (was is null)
        {
            return now is null ? NodeChange.None : NodeChange.Added;
        }

        var before = OrdinalJsonWriter.SerializeToText(was);
        var after = now is null ? string.Empty : OrdinalJsonWriter.SerializeToText(now);

        if (before.Equals(after, StringComparison.Ordinal))
        {
            return NodeChange.None;
        }

        // A container that still exists on both sides holds the change rather than being it, which
        // is the same distinction the object walk draws.
        return isContainer ? NodeChange.Mixed : NodeChange.Edited;
    }

    /// <summary>
    /// A one-line hint for an element, since <c>[0]</c> on its own says nothing about which one it
    /// is. The first short string field usually identifies it — <c>code</c>, <c>name</c>, <c>title</c>
    /// — and a scalar element is simply its own value.
    /// </summary>
    private string ElementPreview(string path, JsonNode? element)
    {
        if (element is JsonArray nested)
        {
            return $"[{nested.Count}]";
        }

        if (element is not JsonObject item)
        {
            return PreviewOf(path, element);
        }

        foreach (var (key, value) in item)
        {
            if (value is JsonValue scalar &&
                scalar.TryGetValue<string>(out var text) &&
                text.Length is > 0 and <= 40)
            {
                return _main.Classifier.Classify($"{path}:{key}", text) == ValueClass.Secret
                    ? $"{key}: {SecretMasker.Describe(text)}"
                    : $"{key}: {text}";
            }
        }

        return $"{item.Count} keys";
    }

    private static int CountLeaves(JsonNode? node) => node switch
    {
        JsonObject o => o.Sum(p => Math.Max(1, CountLeaves(p.Value))),
        JsonArray a => Math.Max(1, a.Sum(CountLeaves)),
        _ => 1,
    };

    private string PreviewOf(string path, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject:
                return string.Empty;

            // An array of objects is still a structure: its rows are below it, so the preview only
            // has to say how many there are.
            case JsonArray objects when !_arrays.IsSingleLeaf(path, objects):
                return $"[{objects.Count}]";

            case JsonArray scalars:
                return ScalarArrayPreview(path, scalars);
        }

        var value = ScalarText(node);

        return _main.Classifier.Classify(path, value) == ValueClass.Secret
            ? SecretMasker.Describe(value)
            : value;
    }

    /// <summary>
    /// What a collapsed array shows on its row: enough of the list to recognise it, and how much is
    /// left. "[5]" said only that there were five of something, which is the one thing the row's own
    /// item count already says.
    /// </summary>
    private string ScalarArrayPreview(string path, JsonArray array)
    {
        var members = ArrayStrategies.ScalarMembers(array);

        if (members is null || members.Count == 0)
        {
            return "[]";
        }

        // A list of secrets is described, never shown - the same rule a secret scalar gets, and the
        // reason this asks the classifier rather than just joining the members.
        var joined = string.Join(",", members);
        if (_main.Classifier.Classify(path, joined) == ValueClass.Secret)
        {
            return SecretMasker.Describe(joined);
        }

        const int shown = 2;
        const int width = 26;

        var head = members.Take(shown).Select(m => m.Length > width ? m[..width] + "…" : m);
        var rest = members.Count - Math.Min(shown, members.Count);

        return rest == 0
            ? $"[{string.Join(", ", head)}]"
            : $"[{string.Join(", ", head)}, +{rest} more]";
    }

    private bool ContainsSecret(string path, JsonNode? node)
    {
        if (node is JsonObject or JsonArray)
        {
            return Tier is not null &&
                   Tier.Flat.Subtree(path).Any(l => l.Class == ValueClass.Secret);
        }

        return _main.Classifier.Classify(path, ScalarText(node)) == ValueClass.Secret;
    }

    /// <summary>
    /// A scalar's text, the same way the flattener reads it. A JSON null is a genuine null
    /// <see cref="JsonNode"/> rather than a node holding null, so it has to be handled before
    /// anything tries to read a value out of it.
    /// </summary>
    private static string ScalarText(JsonNode? node) =>
        node is null ? "null" : Loading.Flattener.ReadValue(node).Value;
}
