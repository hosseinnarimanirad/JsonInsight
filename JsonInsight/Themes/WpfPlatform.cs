using JsonInsight.Platform;
using Microsoft.Win32;

namespace JsonInsight.Themes;

/// <summary>
/// What this app is, as far as the shared view models are concerned: a clipboard, a file picker and a
/// theme. Registered once in <c>App.OnStartup</c>; WebJsonInsight registers its own three against the
/// same interfaces, which is the whole of what makes one view-model layer serve both windows.
/// </summary>
public static class WpfPlatform
{
    public static void Register()
    {
        JsonInsight.Platform.Platform.Clipboard = new WpfClipboard();
        JsonInsight.Platform.Platform.FilePicker = new WpfFilePicker();
        JsonInsight.Platform.Platform.Theme = new WpfThemeService();
    }
}

internal sealed class WpfClipboard : IClipboard
{
    /// <summary>
    /// The copy flag leaves the text on the clipboard after this process exits, which is what anyone
    /// pasting into an editor afterwards expects.
    /// </summary>
    public void SetText(string text) => System.Windows.Clipboard.SetDataObject(text, copy: true);
}

internal sealed class WpfFilePicker : IFilePicker
{
    public string? OpenFile(string title, IReadOnlyList<string> filter, string? startingDirectory = null)
    {
        var extensions = filter.Count == 0 ? ["json"] : filter;
        // One list serves both halves of a filter entry: the label the type dropdown shows and the
        // pattern the dialog actually matches on. They were built twice, identically; keeping it to
        // one is also what stops the label from ever describing something the filter does not do.
        var patterns = string.Join(';', extensions.Select(e => $"*.{e}"));

        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = $"JSON files ({patterns})|{patterns}|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (!string.IsNullOrWhiteSpace(startingDirectory))
        {
            dialog.InitialDirectory = startingDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}

internal sealed class WpfThemeService : IThemeService
{
    public AppTheme Current => ThemeManager.Current;

    public void Apply(AppTheme theme) => ThemeManager.Apply(theme);

    public void Toggle() => ThemeManager.Toggle();
}
