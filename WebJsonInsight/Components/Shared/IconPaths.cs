namespace WebJsonInsight.Components.Shared;

/// <summary>
/// The icon set, as SVG path data.
///
/// <para>
/// The WPF app draws these from Segoe Fluent Icons by code point — <c></c> for refresh,
/// <c></c> for warning and so on. That font ships with Windows and exists on neither Linux nor
/// macOS, so a glyph-per-code-point approach would render this app as a grid of empty boxes on two
/// of the three platforms it now has to run on. Inline SVG has no such dependency, costs no
/// download, and inherits <c>currentColor</c>, so a themed icon needs no separate light and dark
/// asset.
/// </para>
///
/// <para>
/// A plain C# file rather than the <c>@code</c> block of Icon.razor, because Razor's parser does not
/// accept C# raw string literals and every one of these is one. Each entry names the Fluent glyph it
/// stands in for, so the two apps can be kept visually in step.
/// </para>
/// </summary>
internal static class IconPaths
{
    public static string For(string name) => name switch
    {
        //  — the app mark: a vault handwheel, matching Assets/JsonInsight.ico.
        "vault" => """
            <circle cx="12" cy="12" r="8.4" /><circle cx="12" cy="12" r="2.6" />
            <path d="M12 3.6v3.6M12 16.8v3.6M3.6 12h3.6M16.8 12h3.6" />
            """,

        //  — the projects list.
        "list" => """
            <path d="M8.5 6.5h11M8.5 12h11M8.5 17.5h11" />
            <circle cx="4.6" cy="6.5" r="1.1" fill="currentColor" stroke="none" />
            <circle cx="4.6" cy="12" r="1.1" fill="currentColor" stroke="none" />
            <circle cx="4.6" cy="17.5" r="1.1" fill="currentColor" stroke="none" />
            """,

        //  — back to the project you were in.
        "back" => """<path d="M19 12H5M11 6l-6 6 6 6" />""",

        //  — pull: read every source again.
        "pull" => """<path d="M12 3.5v11M7.5 10l4.5 4.5 4.5-4.5M4.5 19.5h15" />""",

        //  — revert and re-read, and the spinner while a read is in flight.
        "refresh" => """
            <path d="M20 5.5v5h-5" /><path d="M4 18.5v-5h5" />
            <path d="M19.1 9.6A7.6 7.6 0 0 0 5.6 8.2L4 10.5M4.9 14.4a7.6 7.6 0 0 0 13.5 1.4L20 13.5" />
            """,

        //  — the warning banners.
        "warn" => """
            <path d="M12 4.2 2.6 20h18.8L12 4.2Z" /><path d="M12 10v4.4" />
            <circle cx="12" cy="17.2" r="0.9" fill="currentColor" stroke="none" />
            """,

        //  — a read that went well.
        "ok" => """<path d="M20 6.5 9.5 17 4 11.5" />""",

        //  — dismiss.
        "close" => """<path d="M18 6 6 18M6 6l12 12" />""",

        "search" => """<circle cx="10.5" cy="10.5" r="6.5" /><path d="M15.2 15.2 20 20" />""",

        "copy" => """
            <rect x="9" y="9" width="11" height="11" rx="2" />
            <path d="M15 6.5V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v7a2 2 0 0 0 2 2h.5" />
            """,

        "edit" => """
            <path d="M16.5 3.9 20.1 7.5 8.1 19.5 3.5 20.5l1-4.6 12-12Z" />
            <path d="M14.7 5.7l3.6 3.6" />
            """,

        "trash" => """
            <path d="M4.5 6.5h15M9.5 6.5V4.8a1.3 1.3 0 0 1 1.3-1.3h2.4a1.3 1.3 0 0 1 1.3 1.3v1.7" />
            <path d="M6.5 6.5 7.4 20a1.3 1.3 0 0 0 1.3 1.2h6.6a1.3 1.3 0 0 0 1.3-1.2l.9-13.5" />
            """,

        "push" => """<path d="M12 20.5v-11M7.5 14l4.5-4.5 4.5 4.5M4.5 4.5h15" />""",

        //  — the row overflow menu. Vertical, which is the arrangement that reads as "more for
        // this row" rather than "more of this toolbar".
        "more" => """
            <circle cx="12" cy="5" r="1.6" fill="currentColor" stroke="none" />
            <circle cx="12" cy="12" r="1.6" fill="currentColor" stroke="none" />
            <circle cx="12" cy="19" r="1.6" fill="currentColor" stroke="none" />
            """,

        "chevron-right" => """<path d="M9.5 5.5 16 12l-6.5 6.5" />""",

        "chevron-down" => """<path d="M5.5 9.5 12 16l6.5-6.5" />""",

        "sun" => """
            <circle cx="12" cy="12" r="4.2" />
            <path d="M12 2.6v2.4M12 19v2.4M2.6 12h2.4M19 12h2.4M5.3 5.3l1.7 1.7M17 17l1.7 1.7M18.7 5.3 17 7M7 17l-1.7 1.7" />
            """,

        "moon" => """<path d="M20 14.4A8.5 8.5 0 0 1 9.6 4 8.6 8.6 0 1 0 20 14.4Z" />""",

        "folder" => """
            <path d="M3.5 6.8A1.8 1.8 0 0 1 5.3 5h4l2 2.6h7.4a1.8 1.8 0 0 1 1.8 1.8v8.4a1.8 1.8 0 0 1-1.8 1.8H5.3a1.8 1.8 0 0 1-1.8-1.8V6.8Z" />
            """,

        "plus" => """<path d="M12 5v14M5 12h14" />""",

        "undo" => """<path d="M4 9.5h9.5A5.25 5.25 0 0 1 13.5 20H8" /><path d="M7.5 5.5 3.5 9.5l4 4" />""",

        "redo" => """<path d="M20 9.5h-9.5A5.25 5.25 0 0 0 10.5 20H16" /><path d="M16.5 5.5l4 4-4 4" />""",

        "open" => """
            <path d="M14 4.5h5.5V10" /><path d="M19 5 11.5 12.5" />
            <path d="M18 14v4.5a1.5 1.5 0 0 1-1.5 1.5h-11A1.5 1.5 0 0 1 4 18.5v-11A1.5 1.5 0 0 1 5.5 6H10" />
            """,

        // An unknown name draws a circle rather than nothing, so a typo shows up as a wrong icon
        // instead of as a gap that reads like a layout bug.
        _ => """<circle cx="12" cy="12" r="8" />""",
    };
}
