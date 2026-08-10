using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace JsonInsight.Views;

/*
 * Colour is not converted here.
 *
 * A converter runs once when a binding is produced and returns a frozen brush, so a value-to-brush
 * converter would survive a theme switch unchanged and leave half the window in the other theme.
 * Every findings colour is therefore a Style or DataTemplate trigger in Themes/Controls.xaml
 * setting a DynamicResource, which repaints on its own. What is left below is pure layout logic.
 */

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (parameter as string == "invert")
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var present = value switch
        {
            string s => !string.IsNullOrWhiteSpace(s),
            int n => n > 0,
            null => false,
            _ => true,
        };

        // "invert" is what shows the empty-state message: the thing that says "there is nothing here"
        // is visible exactly when the list it stands in for is not.
        if (parameter as string == "invert")
        {
            present = !present;
        }

        return present ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>
/// Negates a bool, for the "enabled while not busy" case. Distinct from the visibility converter's
/// invert parameter because IsEnabled wants a bool, not a Visibility.
/// </summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => value is not true;
}

/// <summary>Turns a tree depth into a left margin, so nesting shows without a TreeView.</summary>
public sealed class IndentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new Thickness(value is double d ? d : 0, 0, 0, 0);

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}
