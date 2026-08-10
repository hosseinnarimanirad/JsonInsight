using System.Windows.Controls;

namespace JsonInsight.Views;

/// <summary>
/// The log, bound straight to <see cref="ViewModels.LogVm"/>. No code-behind: entries arrive newest
/// first from the view model, so there is nothing to sort and nothing to scroll to.
/// </summary>
public partial class LogsView : UserControl
{
    public LogsView()
    {
        InitializeComponent();
    }
}
