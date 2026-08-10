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
    /// Hands the promoted document to the push screen. The dialog opens the window rather than the
    /// view model, for the same reason every other one does: a view model that opens windows cannot
    /// be constructed in a test, and these ones are.
    /// </summary>
    private void OnPushClick(object sender, RoutedEventArgs e)
    {
        if (_vm.Destination is not { } destination || _vm.BuildUpdated() is not { } updated)
        {
            return;
        }

        var push = new PushVm(_vm.Main, destination, updated, _vm.What);
        new PushDialog(push) { Owner = this }.ShowDialog();

        if (push.PushedTier is not null)
        {
            Close();
        }
    }
}
