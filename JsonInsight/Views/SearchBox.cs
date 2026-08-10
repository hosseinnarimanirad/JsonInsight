using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JsonInsight.Views;

/// <summary>
/// The two things the search field's template needs that a <see cref="TextBox"/> does not carry:
/// a way to clear itself, and which glyph to wear.
///
/// <para>
/// The clear button is a routed command with a class-level binding rather than a click handler,
/// because the button lives inside a <see cref="ControlTemplate"/> shared by every search field in
/// the app — there is no code-behind for it to call. Sending the command to the templated parent
/// means one handler serves all of them and no view has to know it has a clear button.
/// </para>
/// </summary>
public static class SearchBox
{
    public static readonly RoutedUICommand ClearCommand =
        new("Clear", nameof(ClearCommand), typeof(SearchBox));

    static SearchBox()
    {
        CommandManager.RegisterClassCommandBinding(
            typeof(TextBox),
            new CommandBinding(
                ClearCommand,
                (sender, e) =>
                {
                    var box = (TextBox)sender;
                    box.Clear();

                    // Clearing in order to type the next search is the whole point; leaving focus on
                    // a button that has just disappeared would take the keyboard nowhere.
                    box.Focus();
                    e.Handled = true;
                },
                (sender, e) => e.CanExecute = ((TextBox)sender).Text.Length > 0));
    }

    /// <summary>
    /// The icon at the head of the field. A magnifier by default, since most of these are searches;
    /// the replace field sets its own, because a magnifier in front of a replacement would say the
    /// wrong thing about what that field does. Segoe Fluent Icons, written as an escape so this file
    /// stays plain ASCII.
    /// </summary>
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph",
        typeof(string),
        typeof(SearchBox),
        new PropertyMetadata("\uE721"));

    public static string GetGlyph(DependencyObject element) => (string)element.GetValue(GlyphProperty);

    public static void SetGlyph(DependencyObject element, string value) => element.SetValue(GlyphProperty, value);
}
