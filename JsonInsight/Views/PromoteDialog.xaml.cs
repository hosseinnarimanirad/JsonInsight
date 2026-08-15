using System.Windows;
using JsonInsight.ViewModels;

namespace JsonInsight.Views;

public partial class PromoteDialog : Window
{
    private readonly PromoteVm _vm;

    public PromoteDialog(PromoteVm vm)
    {
        InitializeComponent();
        DataContext = vm;
        _vm = vm;
    }

    /// <summary>
    /// Lands the promotion on the destination tier in memory and closes. Nothing is written here:
    /// the promoted keys join whatever else is unsaved, to be reviewed and pushed from the top bar
    /// or from the Tier editor — which is what makes it possible to promote a subtree, look at it
    /// beside everything else, and only then decide.
    /// </summary>
    private void OnPushClick(object sender, RoutedEventArgs e)
    {
        if (_vm.Apply())
        {
            Close();
        }
    }
}
