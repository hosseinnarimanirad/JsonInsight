namespace JsonInsight.Diff;

/// <summary>
/// Splitting and joining canonical configuration paths.
///
/// Segments are separated by ':', except inside the '[Name=…]' identity suffix a keyed array
/// element carries - splitting naively there would tear 'WriteTo[Name=Seq]' apart if an identity
/// value ever contained a colon.
/// </summary>
public static class ConfigPath
{
    public static string[] Split(string path)
    {
        var segments = new List<string>();
        var start = 0;
        var depth = 0;

        for (var i = 0; i < path.Length; i++)
        {
            switch (path[i])
            {
                case '[':
                    depth++;
                    break;
                case ']':
                    if (depth > 0)
                    {
                        depth--;
                    }

                    break;
                case ':' when depth == 0:
                    segments.Add(path[start..i]);
                    start = i + 1;
                    break;
            }
        }

        segments.Add(path[start..]);
        return segments.ToArray();
    }

    public static string Join(IEnumerable<string> segments) => string.Join(':', segments);

    public static string Parent(string path)
    {
        var segments = Split(path);
        return segments.Length <= 1 ? string.Empty : Join(segments[..^1]);
    }

    public static string Last(string path) => Split(path)[^1];

    /// <summary>
    /// All ancestor paths, outermost first, excluding the path itself.
    ///
    /// <para>
    /// An array element's ancestors include the array. That needs saying because an element carries
    /// its index or identity <em>inside</em> one segment — <c>banners[0]</c>, <c>WriteTo[Name=Seq]</c>
    /// — so a naive prefix walk steps straight from <c>configuration</c> to
    /// <c>configuration:banners[0]</c> and never names <c>configuration:banners</c>, which is the row
    /// the tree actually draws for the array.
    /// </para>
    ///
    /// <para>
    /// Leaving it out was a real fault rather than a tidiness point: the Tier editor's filter keeps a
    /// matching path plus its ancestors, so searching for anything inside an array of objects dropped
    /// the array's own row — and with it every element underneath, because the tree only recurses into
    /// a row it has emitted. The search found the keys and then showed none of them.
    /// </para>
    /// </summary>
    public static IEnumerable<string> Ancestors(string path)
    {
        var segments = Split(path);

        for (var i = 1; i < segments.Length; i++)
        {
            var segment = segments[i - 1];
            var bracket = segment.IndexOf('[', StringComparison.Ordinal);

            // Before the indexed form, so the sequence stays outermost-first - OutermostRemoved takes
            // the first hit and would otherwise report the element as the top of a deleted subtree.
            if (bracket > 0)
            {
                var array = segments[..i];
                array[i - 1] = segment[..bracket];
                yield return Join(array);
            }

            yield return Join(segments[..i]);
        }
    }
}
