using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace JsonInsight.Diff;

/// <summary>
/// Matches configuration paths against colon-separated glob patterns.
///
/// <c>*</c> matches any text within one segment; <c>**</c> matches zero or more whole segments.
/// So <c>**:*Password</c> matches any key ending in Password at any depth, and
/// <c>ConnectionStrings:Couchbase:Modules:*:Scopes:*</c> matches one module's scopes.
///
/// Matching is case-insensitive because the ASP.NET configuration binder is; leaf *identity*
/// elsewhere stays ordinal so a case-only collision surfaces as a defect rather than a match.
/// </summary>
public static class PathGlob
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    public static bool IsMatch(string path, string pattern) =>
        Cache.GetOrAdd(pattern, Compile).IsMatch(path);

    /// <summary>Number of literal (non-wildcard) characters, used to break ties between rules.</summary>
    public static int Specificity(string pattern) =>
        pattern.Count(c => c is not ('*' or ':'));

    private static Regex Compile(string pattern)
    {
        var segments = pattern.Split(':');
        var builder = new StringBuilder("^");

        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            var isLast = i == segments.Length - 1;

            if (segment == "**")
            {
                // Zero or more segments. Consume the following separator too, so "A:**:B" matches "A:B".
                builder.Append(isLast ? "(?:.*)?" : "(?:[^:]*(?::[^:]*)*:)?");
                continue;
            }

            builder.Append(TranslateSegment(segment));
            if (!isLast)
            {
                builder.Append(':');
            }
        }

        builder.Append('$');
        return new Regex(builder.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    private static string TranslateSegment(string segment)
    {
        var builder = new StringBuilder();
        foreach (var c in segment)
        {
            if (c == '*')
            {
                builder.Append("[^:]*");
            }
            else
            {
                builder.Append(Regex.Escape(c.ToString()));
            }
        }

        return builder.ToString();
    }
}
