using JsonInsight.Platform;
using Photino.NET;

namespace WebJsonInsight.Platform;

/// <summary>
/// What this host can do, as far as the shared view models are concerned — the Photino answers to the
/// same three questions <c>WpfPlatform</c> answers for the desktop app.
///
/// <para>
/// Registered once at startup. Everything above this file is Razor and CSS; everything below it is
/// the same view models and the same engine the WPF app runs, which is why a behaviour here is a
/// behaviour there rather than a second implementation that has to be kept in step.
/// </para>
/// </summary>
public static class PhotinoPlatform
{
    public static PhotinoWebviewTheme Theme { get; } = new();

    public static void Register(PhotinoWindow window)
    {
        JsonInsight.Platform.Platform.Clipboard = new PhotinoClipboard();
        JsonInsight.Platform.Platform.FilePicker = new PhotinoFilePicker(window);
        JsonInsight.Platform.Platform.Theme = Theme;
    }
}

/// <summary>
/// The webview's clipboard, reached from C# by handing the text to the page.
///
/// <para>
/// Set by <c>ClipboardInterop</c> rather than written here: the browser clipboard API is
/// asynchronous and permission-gated, and <see cref="IClipboard.SetText"/> is neither. The interop
/// object is assigned once the Blazor runtime is up; before that — and in a test — there is no
/// writer attached and the text goes nowhere.
/// </para>
/// </summary>
public sealed class PhotinoClipboard : IClipboard
{
    /// <summary>Set by the layout once JS interop is available. Null until then.</summary>
    public static Func<string, Task>? Writer { get; set; }

    public void SetText(string text)
    {
        // Fire-and-forget: the view model's Copy command is synchronous, and there is nothing useful
        // to do with a failure the user has not been shown anyway - the pane still holds the text.
        _ = Writer?.Invoke(text);
    }
}

/// <summary>
/// Photino's native open-file dialog — the platform's own, so it is the Explorer dialog on Windows,
/// GTK's on Linux and the Finder panel on macOS.
/// </summary>
public sealed class PhotinoFilePicker : IFilePicker
{
    private readonly PhotinoWindow _window;

    public PhotinoFilePicker(PhotinoWindow window) => _window = window;

    public string? OpenFile(string title, IReadOnlyList<string> filter, string? startingDirectory = null)
    {
        var extensions = filter.Count == 0 ? ["json"] : filter.ToArray();

        // Photino takes (name, extensions) pairs and wants the extensions without dots, which is the
        // shape IFilePicker already uses - that is why the interface does not carry a WPF filter
        // string for this side to parse back apart.
        var results = _window.ShowOpenFile(
            title: title,
            defaultPath: startingDirectory,
            multiSelect: false,
            filters: [("JSON", extensions), ("All files", ["*"])]);

        return results is { Length: > 0 } ? results[0] : null;
    }
}

/// <summary>
/// The theme, which in a webview is a CSS attribute rather than a resource dictionary.
///
/// <para>
/// It holds the value and announces changes; the layout component listens and stamps
/// <c>data-theme</c> on the page. Same reason WPF swaps only the colour dictionary and leaves the
/// templates loaded: nothing is rebuilt, so switching theme mid-promote cannot lose a preview or a
/// typed confirmation.
/// </para>
/// </summary>
public sealed class PhotinoWebviewTheme : IThemeService
{
    public AppTheme Current { get; private set; } = AppTheme.Dark;

    /// <summary>Raised after <see cref="Current"/> changes, so the layout can re-render.</summary>
    public event Action<AppTheme>? Changed;

    public void Apply(AppTheme theme)
    {
        if (Current == theme)
        {
            return;
        }

        Current = theme;
        Changed?.Invoke(theme);
    }

    public void Toggle() => Apply(Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);

    /// <summary>The CSS attribute value the page is stamped with.</summary>
    public string Attribute => Current == AppTheme.Dark ? "dark" : "light";
}
