using System.Windows;
using System.Windows.Controls;
using JsonInsight.ViewModels;

namespace JsonInsight.Views;

/// <summary>
/// Code-behind exists only to sync the PasswordBoxes, which by design expose no bindable password
/// property. Sync runs on Loaded and on DataContextChanged (Reload replaces the view models without
/// recreating the controls), and pushes edits back on PasswordChanged.
/// </summary>
public partial class VaultView : UserControl
{
    public VaultView()
    {
        InitializeComponent();
    }

    private void TokenBox_Sync(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box && box.DataContext is VaultConnectionVm row && box.Password != row.Token)
        {
            box.Password = row.Token;
        }
    }

    private void TokenBox_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is PasswordBox box && box.DataContext is VaultConnectionVm row && box.Password != row.Token)
        {
            box.Password = row.Token;
        }
    }

    private void TokenBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box && box.DataContext is VaultConnectionVm row)
        {
            row.Token = box.Password;
        }
    }

    /// <summary>
    /// Opens a row's overflow menu under its button.
    ///
    /// <para>
    /// The DataContext is assigned here rather than left to inherit. A ContextMenu is rooted in its
    /// own visual tree, so it never picks up the row the button sits on, and every binding inside it
    /// would resolve against nothing — the TLS tick would read as unchecked on every row and writing
    /// it back would go nowhere.
    /// </para>
    /// </summary>
    private void OnRowMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button)
        {
            return;
        }

        menu.DataContext = button.DataContext;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ---------------------------------------------------------------------------------------------
    // The restart pair. Raised rather than shown here, the same way TiersView raises its promote and
    // edit requests: a UserControl has no owner window to be modal to, and MainWindow is the one
    // place in this app that opens a dialog.
    // ---------------------------------------------------------------------------------------------

    // Test used to need a handler here for the same reason the two below still do - it lived in the
    // ContextMenu, whose visual tree cannot see the tab. It is a button on the row now, so it binds
    // to TestCommand directly and the handler is gone.

    private void OnRestartConfigClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: VaultConnectionVm row })
        {
            RestartConfigRequested?.Invoke(this, row);
        }
    }

    private void OnRestartCallClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: VaultConnectionVm row })
        {
            RestartRequested?.Invoke(this, row);
        }
    }

    public event EventHandler<VaultConnectionVm>? RestartConfigRequested;

    public event EventHandler<VaultConnectionVm>? RestartRequested;
}
