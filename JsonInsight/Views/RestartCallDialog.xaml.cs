using System.Windows;
using JsonInsight.ViewModels;

namespace JsonInsight.Views;

/// <summary>
/// Calls one source's restart endpoint.
///
/// <para>
/// Code-behind exists only to sync the PasswordBox, which by design exposes no bindable password
/// property — the same reason the Sources tab has one. The token goes straight into
/// <see cref="RestartVm.Token"/> and nowhere else: it is never persisted, and this window is the
/// only place it exists.
/// </para>
/// </summary>
public partial class RestartCallDialog : Window
{
    public RestartCallDialog(RestartVm vm)
    {
        InitializeComponent();
        DataContext = vm;

        // The token is the only thing this screen is waiting for, so it starts focused.
        Loaded += (_, _) => TokenBox.Focus();
    }

    private void OnTokenChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is RestartVm vm)
        {
            vm.Token = TokenBox.Password;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
