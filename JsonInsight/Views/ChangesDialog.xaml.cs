using System.Windows;
using JsonInsight.ViewModels;

namespace JsonInsight.Views;

public partial class ChangesDialog : Window
{
    private readonly ChangesVm _vm;

    public ChangesDialog(ChangesVm vm)
    {
        InitializeComponent();
        DataContext = vm;
        _vm = vm;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Hands the batch to the push screen as a whole document. The dialog opens the window rather
    /// than the view model, for the same reason every other one does: a view model that opens
    /// windows cannot be constructed in a test, and these ones are.
    /// </summary>
    private void OnPushClick(object sender, RoutedEventArgs e)
    {
        if (_vm.Tier is not { } tier || _vm.BuildUpdated() is not { } updated)
        {
            return;
        }

        var push = new PushVm(_vm.Main, tier, updated, _vm.What);
        new PushDialog(push) { Owner = this }.ShowDialog();

        if (push.PushedTier is not null)
        {
            _vm.Pushed();
        }
    }
}
