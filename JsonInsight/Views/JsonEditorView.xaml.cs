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
    }

    private JsonEditorVm? Vm => DataContext as JsonEditorVm;

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

    private void Find(bool forward)
    {
        if (Vm is not { } vm || vm.FindText.Length == 0)
        {
            return;
        }

        var text = Editor.Text;

        // Forward starts past the current match so repeated presses advance; backward starts at it,
        // so the search window ends where the current match begins.
        var from = forward ? Editor.SelectionStart + Math.Max(Editor.SelectionLength, 1) : Editor.SelectionStart;

        var hit = forward
            ? TextFinder.Next(text, vm.FindText, from, vm.MatchCase)
            : TextFinder.Previous(text, vm.FindText, from, vm.MatchCase);

        if (hit < 0)
        {
            vm.FindStatus = "not found";
            return;
        }

        Select(hit, vm.FindText.Length);
        Report(vm, hit);
    }

    private void OnReplace(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || vm.FindText.Length == 0 || Editor.IsReadOnly)
        {
            return;
        }

        // Replace acts on the current match only when one is actually selected; otherwise it finds
        // the next one first, so pressing it twice is find-then-replace rather than a silent skip.
        var onMatch = Editor.SelectionLength == vm.FindText.Length &&
                      Editor.SelectedText.Equals(vm.FindText, TextFinder.ComparisonFor(vm.MatchCase));

        if (!onMatch)
        {
            Find(forward: true);
            return;
        }

        var at = Editor.SelectionStart;
        Editor.SelectedText = vm.ReplaceText;
        Editor.Select(at + vm.ReplaceText.Length, 0);

        Find(forward: true);
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

    private void Select(int at, int length)
    {
        Editor.Focus();
        Editor.Select(at, length);

        // Without this the match can be selected off-screen, which reads as nothing having happened.
        Editor.ScrollToLine(Math.Max(0, Editor.GetLineIndexFromCharacterIndex(at) - 2));
    }

    private void Report(JsonEditorVm vm, int at)
    {
        var total = TextFinder.Count(Editor.Text, vm.FindText, vm.MatchCase);
        var ordinal = TextFinder.Ordinal(Editor.Text, vm.FindText, at, vm.MatchCase);

        vm.FindStatus = total == 0 ? "not found" : $"{ordinal} of {total}";
    }
}
