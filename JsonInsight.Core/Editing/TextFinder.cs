namespace JsonInsight.Editing;

/// <summary>
/// Find and replace over the editor pane's text.
///
/// <para>
/// Plain substring work, kept out of the view so it can be tested: the view's job is the caret and
/// the selection, which is the part a test cannot see anyway. Case sensitivity is a choice rather
/// than a default because these documents distinguish <c>Url</c> from <c>URL</c> — the file's key
/// order treats them as two keys, and a search that silently conflated them would be the one place
/// in this app claiming otherwise.
/// </para>
/// </summary>
public static class TextFinder
{
    public static StringComparison ComparisonFor(bool matchCase) =>
        matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// The next match at or after <paramref name="from"/>, wrapping to the top when there is none
    /// below. Returns -1 when the term does not appear at all.
    /// </summary>
    public static int Next(string text, string term, int from, bool matchCase)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(term))
        {
            return -1;
        }

        var comparison = ComparisonFor(matchCase);
        var start = Math.Clamp(from, 0, text.Length);

        var hit = text.IndexOf(term, start, comparison);
        return hit >= 0 ? hit : text.IndexOf(term, 0, comparison);
    }

    /// <summary>
    /// The previous match strictly before <paramref name="before"/>, wrapping to the bottom. Searching
    /// backwards is worth having rather than making people cycle all the way round a 28 KB document.
    /// </summary>
    public static int Previous(string text, string term, int before, bool matchCase)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(term))
        {
            return -1;
        }

        var comparison = ComparisonFor(matchCase);
        var start = Math.Clamp(before, 0, text.Length);

        var hit = start > 0 ? LastIndexBefore(text, term, start, comparison) : -1;
        return hit >= 0 ? hit : LastIndexBefore(text, term, text.Length, comparison);
    }

    private static int LastIndexBefore(string text, string term, int limit, StringComparison comparison)
    {
        // LastIndexOf's start index is the last position of the match, not of the search window, and
        // getting that wrong silently skips matches near the end. Searching the substring is plainer.
        var window = text[..Math.Clamp(limit, 0, text.Length)];
        return window.LastIndexOf(term, comparison);
    }

    /// <summary>
    /// Where every match starts, in order.
    ///
    /// <para>
    /// What highlighting is built from, and what stepping counts against: with the whole list in hand
    /// "next" is the entry after the current index rather than a fresh search from wherever the caret
    /// happens to be, which is the arrangement that could land on the match it was already showing.
    /// Non-overlapping, so searching <c>aa</c> in <c>aaaa</c> finds two rather than three — the same
    /// rule <see cref="ReplaceAll"/> follows, since a highlight has to be what a replace would take.
    /// </para>
    /// </summary>
    public static IReadOnlyList<int> All(string text, string term, bool matchCase)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(term))
        {
            return [];
        }

        var comparison = ComparisonFor(matchCase);
        var hits = new List<int>();

        for (var i = text.IndexOf(term, comparison); i >= 0; i = text.IndexOf(term, i + term.Length, comparison))
        {
            hits.Add(i);
        }

        return hits;
    }

    public static int Count(string text, string term, bool matchCase) => All(text, term, matchCase).Count;

    /// <summary>
    /// Which match a position sits on, counted from one — for the "3 of 12" reading. Zero when the
    /// position is not on a match.
    /// </summary>
    public static int Ordinal(string text, string term, int at, bool matchCase)
    {
        if (at < 0 || string.IsNullOrEmpty(term) || at + term.Length > text.Length ||
            !text.AsSpan(at, term.Length).Equals(term.AsSpan(), ComparisonFor(matchCase)))
        {
            return 0;
        }

        return Count(text[..(at + term.Length)], term, matchCase);
    }

    /// <summary>
    /// Every occurrence replaced, and how many there were.
    ///
    /// <para>
    /// Walked by hand rather than with <see cref="string.Replace(string, string, StringComparison)"/>
    /// so the count is the number actually replaced rather than a second, separately-computed number
    /// that could disagree with it. Advancing past the inserted text also means replacing <c>a</c>
    /// with <c>aa</c> terminates.
    /// </para>
    /// </summary>
    public static (string Text, int Count) ReplaceAll(string text, string term, string replacement, bool matchCase)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(term))
        {
            return (text, 0);
        }

        var comparison = ComparisonFor(matchCase);
        var built = new System.Text.StringBuilder(text.Length);
        var count = 0;
        var at = 0;

        // Unconditional, deliberately. `at` only ever advances to just past a match, which can never
        // exceed text.Length, so the loop's one real exit is IndexOf finding nothing — and a bound in
        // the condition would have read as a second guard against a runaway that cannot occur.
        while (true)
        {
            var hit = text.IndexOf(term, at, comparison);
            if (hit < 0)
            {
                break;
            }

            built.Append(text, at, hit - at).Append(replacement);
            at = hit + term.Length;
            count++;
        }

        built.Append(text, at, text.Length - at);
        return count == 0 ? (text, 0) : (built.ToString(), count);
    }
}
