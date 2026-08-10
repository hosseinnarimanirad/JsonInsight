using System.Windows;
using JsonInsight.ViewModels;

namespace JsonInsight.Views;

public partial class PushDialog : Window
{
    public PushDialog(PushVm vm)
    {
        InitializeComponent();
        DataContext = vm;

        // The comparison is the dialog, so it reads Vault as it opens rather than waiting to be
        // asked. Deferred to Loaded so the window is on screen while the read is in flight - a
        // dialog that appears only once the network has answered looks like a button that did
        // nothing. Nothing is sent by this: it is the same read the Pull button performs.
        if (vm.ChecksOnOpen)
        {
            Loaded += async (_, _) => await vm.CheckVaultAsync();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
