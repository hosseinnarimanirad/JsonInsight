namespace JsonInsight.Platform;

/// <summary>
/// The three things a view model needs that only a host can do: put text on the clipboard, ask for a
/// file, and change the theme.
///
/// <para>
/// This is the whole of what separated the view-model layer from running anywhere other than WPF.
/// Not a design that was planned for — it is what was found when the layer was measured: 4,500 lines
/// of view models, three call sites reaching a UI framework. Naming those three is what let the
/// layer move to a project WebJsonInsight can reference from Linux, rather than be rewritten there.
/// </para>
///
/// <para>
/// A static locator rather than constructor injection, deliberately. There is one window and one set
/// of these per process, the alternative is threading three parameters through every view model
/// constructor in a working application, and <c>ThemeManager</c> — the thing being replaced — was
/// already a static. The defaults do nothing rather than throw, so a test or a headless
/// <c>--check</c> run that never sets them behaves as it did before this existed.
/// </para>
/// </summary>
public static class Platform
{
    public static IClipboard Clipboard { get; set; } = new NoClipboard();

    public static IFilePicker FilePicker { get; set; } = new NoFilePicker();

    public static IThemeService Theme { get; set; } = new NoThemeService();

    /// <summary>
    /// Puts every seam back to its do-nothing default. For tests: a host registered in one test would
    /// otherwise still be registered in the next, since these are static for the life of the process.
    /// </summary>
    public static void Reset()
    {
        Clipboard = new NoClipboard();
        FilePicker = new NoFilePicker();
        Theme = new NoThemeService();
    }
}

public interface IClipboard
{
    void SetText(string text);
}

/// <summary>
/// Asking the host for a file. Returns a path or null, which is the shape both a WPF
/// <c>OpenFileDialog</c> and Photino's <c>ShowOpenFile</c> already have.
/// </summary>
public interface IFilePicker
{
    /// <param name="filter">
    /// Extensions without the dot, e.g. <c>["json"]</c>. Spelled this way rather than as a WPF filter
    /// string because that format — <c>"JSON|*.json|All|*.*"</c> — is a WPF convention, and putting it
    /// in the interface would make every other host parse it back apart.
    /// </param>
    string? OpenFile(string title, IReadOnlyList<string> filter, string? startingDirectory = null);
}

public enum AppTheme
{
    Light,
    Dark,
}

public interface IThemeService
{
    AppTheme Current { get; }

    void Apply(AppTheme theme);

    void Toggle();
}

internal sealed class NoClipboard : IClipboard
{
    public void SetText(string text)
    {
    }
}

internal sealed class NoFilePicker : IFilePicker
{
    public string? OpenFile(string title, IReadOnlyList<string> filter, string? startingDirectory = null) => null;
}

/// <summary>
/// Remembers what it was told and repaints nothing. A headless run has no colours to change, and a
/// view model asking which theme it is in should still get a consistent answer.
/// </summary>
internal sealed class NoThemeService : IThemeService
{
    public AppTheme Current { get; private set; } = AppTheme.Dark;

    public void Apply(AppTheme theme) => Current = theme;

    public void Toggle() => Apply(Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);
}
