using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JsonInsight.ViewModels;

namespace JsonInsight.Views;

public partial class ProjectsView : UserControl
{
    public ProjectsView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Double-clicking a row opens it, matching the double-click-to-act rows on the Tiers grid and
    /// the Tier editor tree. Read off ClickCount because the row is a Border, which has no
    /// MouseDoubleClick of its own. Not while the row is renaming or confirming a delete: both put
    /// other controls under the pointer, and two quick clicks on those bubble up here as a
    /// double-click — opening the project out from under a half-typed name would be the surprise.
    /// </summary>
    private void OnRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2
            && sender is FrameworkElement { DataContext: ProjectRowVm { Renaming: false, ConfirmingDelete: false } row }
            && DataContext is ProjectsVm vm)
        {
            vm.OpenCommand.Execute(row);
        }
    }
}
