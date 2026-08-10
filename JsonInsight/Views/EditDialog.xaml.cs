using System.Windows;
using JsonInsight.ViewModels;

namespace JsonInsight.Views;

public partial class EditDialog : Window
{
    public EditDialog(EditVm vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
