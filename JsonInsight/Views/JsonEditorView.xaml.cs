using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JsonInsight.Editing;
using JsonInsight.ViewModels;

namespace JsonInsight.Views;

public partial class JsonEditorView : UserControl
{
    public JsonEditorView()
    {
        InitializeComponent();

        // Double-click expands or collapses, matching the Tiers grid. Single-click stays selection,
        // because selecting is what loads the node into the editor and it must not also move the tree.
        Tree.MouseDoubleClick += (_, _) =>
        {
            if (DataContext is JsonEditorVm { SelectedNode: { IsContainer: true } node } vm)
            {
                vm.ToggleNodeCommand.Execute(node);
            }
        };

        // The view model is replaced on every reload and every pull, so the match list to paint is a
        // different object each time. Watched here rather than bound, because what the layer needs
        // is a call, not a value.
        DataContextChanged += OnViewModelReplaced;
    }

    private JsonEditorVm? Vm => DataContext as JsonEditorVm;

    private JsonEditorVm? _watched;

    private void OnViewModelReplaced(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_watched is not null)
        {
            _watched.PropertyChanged -= OnViewModelChanged;
        }

        _watched = Vm;

        if (_watched is not null)
        {
            _watched.PropertyChanged += OnViewModelChanged;
        }

        RefreshHighlights();
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(JsonEditorVm.Matches) or nameof(JsonEditorVm.MatchIndex))
        {
            RefreshHighlights();
        }
    }

    /// <summary>
    /// Raised with everything the push screen needs to review this tab's work. The dialog is opened by
    /// the window rather than from here, for the same reason the promote and edit dialogs are: a view
    /// model that opens windows cannot be constructed in a test, and these ones are.
    /// </summary>
    public event EventHandler<PushRequest>? PushRequested;

    private void OnPushClick(object sender, RoutedEventArgs e)
    {
        if (Vm is { Tier: { } tier } vm)
        {
            PushRequested?.Invoke(this, new PushRequest(
                tier, vm.Editor?.Working, "the document as edited on the Tier editor tab"));
        }
    }

    private void OnCopyRowWithKey(object sender, RoutedEventArgs e) => CopyRow(sender, withKey: true);

    private void OnCopyRowValue(object sender, RoutedEventArgs e) => CopyRow(sender, withKey: false);

    /// <summary>
    /// The row comes through the menu's placement target rather than the MenuItem's DataContext: a
    /// ContextMenu lives in its own visual tree (the same gotcha VaultView's row menu documents), and
    /// the placement target is the one reliable link back to the ListBoxItem that was right-clicked —
    /// which need not be the selected row, so the command takes the node rather than assuming one.
    /// </summary>
    private void CopyRow(object sender, bool withKey)
    {
        if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { DataContext: JsonNodeVm node } } }
            && Vm is { } vm)
        {
            (withKey ? vm.CopyNodeWithKeyCommand : vm.CopyNodeValueCommand).Execute(node);
        }
    }

    /// <summary>
    /// Find and replace lives in the code-behind because it is entirely about the text box: where the
    /// caret is, what is selected, and what to scroll to. The matching itself is
    /// <see cref="TextFinder"/>, which is testable; none of what is left here is.
    ///
    /// <para>
    /// Handled for the whole tab rather than for the pane alone. Ctrl+F used to be a
    /// <c>KeyDown</c> on the editor, so it only worked once the caret was already in the JSON — which
    /// is precisely where you do not need a shortcut to get to. F3 was the same, and did nothing from
    /// inside the find box, where it is most likely to be pressed.
    /// </para>
    /// </summary>
    private void OnPaneKeyDown(object sender, KeyEventArgs e)
    {
        // Nothing to search while the diff is up, which is also when the Find switch is off.
        if (Vm is not { ShowingComparison: false })
        {
            return;
        }

        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        // Ctrl+Z takes back what was typed into the pane, and only while the pane has the caret: the
        // find and replace boxes are ordinary text boxes and keep their own undo. Handled here rather
        // than left to the TextBox because its native stack is flattened every time the view model
        // writes the text — which Replace, Replace all and a scalar committing as you type all do.
        if (control && !shift && e.Key == Key.Z && Editor.IsKeyboardFocused)
        {
            if (Vm?.UndoText() == true)
            {
                Editor.CaretIndex = Math.Min(Editor.CaretIndex, Editor.Text.Length);
                e.Handled = true;
            }

            return;
        }

        switch (JsonEditorVm.PaneKey(e.Key.ToString(), shift, control))
        {
            case JsonEditorVm.FindKey.Open:
                OpenFind();
                e.Handled = true;
                break;

            case JsonEditorVm.FindKey.Next:
                Find(forward: true);
                e.Handled = true;
                break;

            case JsonEditorVm.FindKey.Previous:
                Find(forward: false);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// A click in the pane moves the search's idea of "here", so the next Enter continues from where
    /// you are looking rather than from wherever the last step stopped — the same thing a click does
    /// on the Blazor pane.
    ///
    /// <para>
    /// Read after the event rather than during it: the caret has not moved yet while the click is
    /// still being previewed. Handling <c>SelectionChanged</c> instead would have been simpler and
    /// wrong — <see cref="Reveal"/> selects the match it just stepped to, so stepping would re-enter
    /// this and argue with itself about which match is current.
    /// </para>
    /// </summary>
    private void OnEditorClicked(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not { FindOpen: true, HasMatches: true } vm)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            () => vm.SyncMatchToCaret(Editor.CaretIndex),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnFindBoxKeyDown(object sender, KeyEventArgs e)
    {
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        switch (JsonEditorVm.FindBoxKey(e.Key.ToString(), shift))
        {
            case JsonEditorVm.FindKey.Next:
                Find(forward: true);
                e.Handled = true;
                break;

            case JsonEditorVm.FindKey.Previous:
                Find(forward: false);
                e.Handled = true;
                break;

            case JsonEditorVm.FindKey.Close:
                Vm?.CloseFindCommand.Execute(null);
                Editor.Focus();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Fires when the Find switch goes on, and directly when Ctrl+F opens the bar.</summary>
    private void OnFindToggled(object sender, RoutedEventArgs e)
    {
        if (Vm is not { FindOpen: true })
        {
            return;
        }

        // Selected text is almost always what you were about to search for.
        if (Editor.SelectionLength is > 0 and < 200 && !Editor.SelectedText.Contains('\n'))
        {
            Vm.FindText = Editor.SelectedText;
        }

        // The bar is collapsed until this switch flips, so the box does not exist to be focused yet
        // on the frame that opens it.
        Dispatcher.BeginInvoke(() =>
        {
            FindBox.Focus();
            FindBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OpenFind()
    {
        if (Vm is not { } vm)
        {
            return;
        }

        vm.FindOpen = true;
        OnFindToggled(this, new RoutedEventArgs());
    }

    private void OnFindNext(object sender, RoutedEventArgs e) => Find(forward: true);

    private void OnFindPrevious(object sender, RoutedEventArgs e) => Find(forward: false);

    /// <summary>
    /// The next or previous match, revealed in the pane.
    ///
    /// <para>
    /// Which match that is comes from <see cref="JsonEditorVm.StepMatch"/> — an index into the shared
    /// match list — rather than from a fresh search starting at the caret. The caret sits <em>at</em>
    /// the current match after a step, and a forward search from there had to be nudged past it by
    /// hand; an index cannot land on the entry it is already on.
    /// </para>
    /// </summary>
    private void Find(bool forward)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        var hit = vm.StepMatch(forward);
        if (hit < 0)
        {
            return;
        }

        Reveal(hit, vm.FindText.Length);
    }

    /// <summary>
    /// Replaces the current match. Which match, and what becomes current afterwards, is
    /// <see cref="JsonEditorVm.ReplaceCurrent"/> — shared with the Blazor pane, and written through the
    /// view model rather than into the text box because the box's binding is delayed (see below).
    /// </summary>
    private void OnReplace(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        var caret = vm.ReplaceCurrent();
        if (caret < 0)
        {
            return;
        }

        Editor.Select(Math.Min(caret, Editor.Text.Length), 0);

        if (vm.MatchAt >= 0)
        {
            Reveal(vm.MatchAt, vm.FindText.Length);
        }
    }

    /// <summary>
    /// Every match in the pane at once. Still only in the pane — a section needs Update node
    /// afterwards, which is what keeps a bulk replace reviewable before it lands.
    ///
    /// <para>
    /// Written through the view model rather than straight into the text box. The box's binding is
    /// delayed by 250&#160;ms, so assigning <c>Editor.Text</c> left the view model — and with it the
    /// match list the highlights are drawn from and the count in the bar — a quarter second behind
    /// what was on screen.
    /// </para>
    /// </summary>
    private void OnReplaceAll(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        var caret = Editor.SelectionStart;

        vm.ReplaceAllInPane();

        Editor.Select(Math.Min(caret, Editor.Text.Length), 0);
    }

    /// <summary>
    /// Puts a match on screen without taking the caret.
    ///
    /// <para>
    /// It used to focus the editor, which moved the caret out of the find box — so the second Enter
    /// went into the JSON as a newline instead of finding the next match. The highlight layer is what
    /// shows the match now, so there is nothing left that needs focus to be visible, and the box
    /// keeps it.
    /// </para>
    /// </summary>
    private void Reveal(int at, int length)
    {
        Editor.Select(at, length);
        Editor.ScrollToLine(Math.Max(0, Editor.GetLineIndexFromCharacterIndex(at) - 2));
    }

    // ------------------------------------------------------------------ highlighting

    /// <summary>
    /// Hands the layer the current match list.
    ///
    /// <para>
    /// The layer is declared in the XAML beside the editor, so there is nothing to attach and nothing
    /// to retry. This used to create an adorner on first use and guard on a null field — which meant
    /// that once the adorner layer had dropped it, on the first switch away from this tab, the guard
    /// saw a non-null field and never put it back.
    /// </para>
    /// </summary>
    private void RefreshHighlights()
    {
        if (Vm is { } vm)
        {
            Highlights.Show(vm.Matches, vm.FindText.Length, vm.MatchIndex);
        }
    }
}

/// <summary>
/// What this tab asks the window to push.
/// </summary>
/// <param name="Updated">
/// The document as edited in the pane, which is the whole point of pushing from this tab. Without it
/// the dialog falls back to the tier plus whatever edits are queued against it — and since the pane
/// edits a <c>SortedClone</c> that never touches <c>Tier.Root</c>, that fallback diffs the unedited
/// document and reports that the source already holds exactly this.
/// </param>
public sealed record PushRequest(Model.TierDocument Tier, JsonNode? Updated, string What);
