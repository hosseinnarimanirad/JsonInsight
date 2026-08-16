using System.Windows;
using JsonInsight.ViewModels;

namespace JsonInsight.Views;

/// <summary>
/// Sets one source's restart endpoint. The DataContext is the row itself, so there is no second
/// copy of these fields to keep in step — and the window that opened this saves the settings when
/// it closes over a change, so nothing typed here waits on the tab's usual pause before writing.
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
