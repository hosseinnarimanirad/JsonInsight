using System.Text.RegularExpressions;

namespace WebJsonInsight.Tests;

/// <summary>
/// The guard theme.css always claimed to have and never did.
///
/// Colours live in four hand-maintained dictionaries — Dark.xaml, Light.xaml, and the two blocks of
/// theme.css — and nothing in the build checks them against each other. A token defined in only one
/// block resolves to nothing after a theme switch and the control silently loses its colour, which
/// is the one class of bug neither test suite can see: bUnit renders markup, not computed styles,
/// and the WPF smoke tests only ever look at the XAML pair (UiSmokeTests.Both_themes_define_the_same_brushes).
/// </summary>
public class ThemeTests
{
    /// <summary>
    /// Light and dark must name exactly the same tokens. Without this, adding a colour to one block
    /// and forgetting the other ships a theme that looks right until someone flips it.
    /// </summary>
    [Fact]
    public void Both_theme_blocks_define_the_same_tokens()
    {
        var css = ThemeCss();

        var light = BrushTokens(Block(css, ":root,\n[data-theme=\"light\"]"));
        var dark = BrushTokens(Block(css, "[data-theme=\"dark\"]"));

        Assert.NotEmpty(light);
        Assert.Empty(light.Except(dark));
        Assert.Empty(dark.Except(light));
    }

    /// <summary>
    /// Every brush the WPF themes define must exist in the CSS themes under the translated name, so a
    /// colour added to the desktop app cannot quietly skip the web one. The reverse does not hold —
    /// see <see cref="The_web_only_tokens_are_the_documented_ones"/> — because the web theme carries a
    /// few tokens WPF expresses through triggers instead.
    /// </summary>
    [Fact]
    public void Every_xaml_brush_has_a_css_token()
    {
        var css = BrushTokens(Block(ThemeCss(), "[data-theme=\"dark\"]"));

        var expected = XamlBrushKeys().Select(CssName).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(expected);
        Assert.Empty(expected.Except(css));
    }

    /// <summary>
    /// The asymmetry above, pinned so it stays deliberate. These seven are the editor's change marks
    /// and the "holds" accent, which WPF renders through Controls.xaml triggers over the findings
    /// brushes rather than through named theme keys. Listing them here means a genuinely forgotten
    /// XAML brush shows up as a new name in this set instead of hiding inside a size comparison.
    /// </summary>
    [Fact]
    public void The_web_only_tokens_are_the_documented_ones()
    {
        var css = BrushTokens(Block(ThemeCss(), "[data-theme=\"dark\"]"));
        var fromXaml = XamlBrushKeys().Select(CssName).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            new[]
            {
                "--brush-added",
                "--brush-added-soft",
                "--brush-edited",
                "--brush-edited-soft",
                "--brush-holds",
                "--brush-removed",
                "--brush-removed-soft",
            },
            css.Except(fromXaml).OrderBy(t => t, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// <c>Brush.SurfaceAlt</c> becomes <c>--brush-surface-alt</c>: the same word in the same order, so
    /// a colour can be found in either app by searching for one spelling and guessing the other.
    /// </summary>
    private static string CssName(string xamlKey)
    {
        var name = xamlKey["Brush.".Length..];
        var kebab = Regex.Replace(name, "(?<!^)([A-Z])", "-$1");

        return "--brush-" + kebab.ToLowerInvariant();
    }

    private static HashSet<string> BrushTokens(string block) =>
        Regex.Matches(block, @"--brush-[a-z-]+(?=\s*:)")
            .Select(m => m.Value)
            .ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<string> XamlBrushKeys() =>
        Regex.Matches(File.ReadAllText(RepositoryFile("JsonInsight", "Themes", "Dark.xaml")),
                @"x:Key=""(Brush\.[A-Za-z]+)""")
            .Select(m => m.Groups[1].Value);

    /// <summary>
    /// The declarations of one rule, found by its selector and read to the closing brace. Comments are
    /// stripped first so prose that happens to spell a token name cannot be mistaken for one.
    /// </summary>
    private static string Block(string css, string selector)
    {
        var start = css.IndexOf(selector + " {", StringComparison.Ordinal);
        Assert.True(start >= 0, $"theme.css no longer has a `{selector}` rule");

        var open = css.IndexOf('{', start);
        var close = css.IndexOf('}', open);
        Assert.True(close > open, $"the `{selector}` rule in theme.css is unterminated");

        return css[(open + 1)..close];
    }

    private static string ThemeCss() =>
        Regex.Replace(
            File.ReadAllText(RepositoryFile("WebJsonInsight", "wwwroot", "css", "theme.css")),
            @"/\*.*?\*/", string.Empty, RegexOptions.Singleline)
            .ReplaceLineEndings("\n");

    /// <summary>
    /// Walks up to the folder holding the solution, the same way JsonInsight.Tests' RepositoryHygieneTests
    /// does — not <see cref="JsonInsight.AppPaths.ContentRoot"/>, which is its parent. The two copies of
    /// this walk cannot share a home until there is a test project both suites reference.
    /// </summary>
    private static string RepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "JsonInsight.slnx")) ||
                File.Exists(Path.Combine(directory.FullName, "JsonInsight.sln")))
            {
                return Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"no JsonInsight.sln/.slnx above {AppContext.BaseDirectory}; the theme files cannot be found");
    }
}
