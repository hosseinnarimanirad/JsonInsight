using System.Windows;
using JsonInsight.ViewModels;

namespace JsonInsight.Views;

public partial class ChangesDialog : Window
{
    public ChangesDialog(ChangesVm vm)
    {
        InitializeComponent();
        DataContext = vm;

        // The comparison is half of this window, so it reads the source as it opens rather than
        // waiting to be asked. Deferred to Loaded so the window is on screen while the read is in
        // flight - a dialog that appears only once the network has answered looks like a button that
        // did nothing. Nothing is sent by this: it is the same read the Pull button performs.
        Loaded += async (_, _) => await vm.OpenedAsync();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
