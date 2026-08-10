using System.Windows;
using JsonInsight.ViewModels;

namespace JsonInsight.Views;

/// <summary>
/// Sets one source's restart endpoint. The DataContext is the row itself, so everything typed here
/// is saved by the Sources tab's own Save settings along with the rest of that row — there is no
/// separate persistence step and no second copy of these fields to keep in step.
/// </summary>
public partial class RestartConfigDialog : Window
{
    public RestartConfigDialog(VaultConnectionVm row)
    {
        InitializeComponent();
        DataContext = row;
    }

    private void OnDoneClick(object sender, RoutedEventArgs e) => Close();

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not VaultConnectionVm row)
        {
            return;
        }

        row.RestartUrl = string.Empty;
        row.RestartBody = string.Empty;
        row.RestartAllowInsecureTls = false;
    }
}
