using System.Windows;
using JsonInsight.Platform;
using Microsoft.Win32;

namespace JsonInsight.Themes;

/// <summary>
/// Swaps the colour dictionary that every style pulls from via DynamicResource.
///
/// Only the colours are swapped - the control templates in Controls.xaml stay loaded and simply
/// repaint. Nothing is rebuilt and no binding is re-evaluated, which is why switching theme cannot
/// disturb an in-progress promote or lose a typed confirmation.
///
/// <para>
/// <see cref="AppTheme"/> now lives in JsonInsight.Presentation, so the view models can name a theme
/// without naming WPF. This class is what <see cref="WpfThemeService"/> registers as the host's
/// implementation; everything below the seam calls that interface instead of this.
/// </para>
/// </summary>
public static class ThemeManager
{
    private const int ThemeDictionaryIndex = 0;

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static void Apply(AppTheme theme)
    {
        Current = theme;

        // The headless mode (--check) and the test harness run without the application dictionaries
        // in place; there is nothing to recolour, so leave it alone.
        var dictionaries = Application.Current?.Resources.MergedDictionaries;
        if (dictionaries is null || dictionaries.Count == 0)
        {
            return;
        }

        // Absolute rather than relative: a relative pack URI resolves against the entry assembly,
        // which is this exe when the app runs but the test host when it does not.
        dictionaries[ThemeDictionaryIndex] = new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/JsonInsight;component/Themes/{theme}.xaml",
                UriKind.Absolute),
        };
    }

    public static void Toggle() => Apply(Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);

    /// <summary>Matches the Windows app theme, defaulting to dark if it cannot be read.</summary>
    public static AppTheme SystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int light)
            {
                return light == 0 ? AppTheme.Dark : AppTheme.Light;
            }
        }
        catch (Exception)
        {
            // Not readable - fall through to the default.
        }

        return AppTheme.Dark;
    }
}
