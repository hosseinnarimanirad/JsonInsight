using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
        // different object each time. Watched here rather than bound, because what the adorner needs
        // is a call, not a value.
        DataContextChanged += OnViewModelReplaced;
        Loaded += (_, _) => RefreshHighlights();
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
    /// Raised with the tier to upload. The dialog is opened by the window rather than from here, for
    /// the same reason the promote and edit dialogs are: a view model that opens windows cannot be
    /// constructed in a test, and these ones are.
    /// </summary>
    public event EventHandler<Model.TierDocument>? PushRequested;

    private void OnPushClick(object sender, RoutedEventArgs e)
    {
        if (Vm is { Tier: { } tier })
        {
            PushRequested?.Invoke(this, tier);
        }
    }

    /// <summary>
    /// Find and replace lives in the code-behind because it is entirely about the text box: where the
    /// caret is, what is selected, and what to scroll to. The matching itself is
    /// <see cref="TextFinder"/>, which is testable; none of what is left here is.
    /// </summary>
    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OpenFind();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F3)
        {
            Find(forward: Keyboard.Modifiers != ModifierKeys.Shift);
            e.Handled = true;
        }
    }

    private void OnFindBoxKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Find(forward: Keyboard.Modifiers != ModifierKeys.Shift);
                e.Handled = true;
                break;

            case Key.Escape:
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

    private void OnReplace(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || Editor.IsReadOnly)
        {
            return;
        }

        // Whichever match is current, or the first one when nothing has been stepped to yet — so
        // pressing Replace without having pressed Next first still replaces something.
        var at = vm.MatchAt >= 0 ? vm.MatchAt : vm.StepMatch(forward: true);
        if (at < 0)
        {
            return;
        }

        Editor.Select(at, vm.FindText.Length);
        Editor.SelectedText = vm.ReplaceText;
        Editor.Select(at + vm.ReplaceText.Length, 0);

        // The list was re-found against the changed text, so the entry that was current has gone and
        // the one that took its place is the next match. Landing there is what makes pressing Replace
        // repeatedly walk the document.
        vm.SyncMatchToCaret(at + vm.ReplaceText.Length);

        if (vm.MatchAt >= 0)
        {
            Reveal(vm.MatchAt, vm.FindText.Length);
        }
    }

    private void OnReplaceAll(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || vm.FindText.Length == 0 || Editor.IsReadOnly)
        {
            return;
        }

        var (replaced, count) = TextFinder.ReplaceAll(Editor.Text, vm.FindText, vm.ReplaceText, vm.MatchCase);

        if (count == 0)
        {
            vm.FindStatus = "not found";
            return;
        }

        var caret = Editor.SelectionStart;
        Editor.Text = replaced;
        Editor.Select(Math.Min(caret, replaced.Length), 0);

        vm.FindStatus = $"{count} replaced";
    }

    /// <summary>
    /// Puts a match on screen without taking the caret.
    ///
    /// <para>
    /// It used to focus the editor, which moved the caret out of the find box — so the second Enter
    /// went into the JSON as a newline instead of finding the next match. The adorner is what shows
    /// the match now, so there is nothing left that needs focus to be visible, and the box keeps it.
    /// </para>
    /// </summary>
    private void Reveal(int at, int length)
    {
        Editor.Select(at, length);
        Editor.ScrollToLine(Math.Max(0, Editor.GetLineIndexFromCharacterIndex(at) - 2));
    }

    // ------------------------------------------------------------------ highlighting

    private FindHighlightAdorner? _highlights;

    /// <summary>
    /// Attaches the adorner the first time it is needed and keeps it fed. Late rather than in the
    /// constructor: an adorner layer only exists once the control is in a visual tree.
    /// </summary>
    private void RefreshHighlights()
    {
        if (Vm is not { } vm)
        {
            return;
        }

        if (_highlights is null)
        {
            if (AdornerLayer.GetAdornerLayer(Editor) is not { } layer)
            {
                return;
            }

            _highlights = new FindHighlightAdorner(Editor);
            layer.Add(_highlights);
        }

        _highlights.Show(vm.Matches, vm.FindText.Length, vm.MatchIndex);
    }
}
