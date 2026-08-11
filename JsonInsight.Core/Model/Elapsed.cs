namespace JsonInsight.Model;

/// <summary>
/// How long ago something happened, in words.
///
/// <para>
/// Every source line in this app already prints an absolute timestamp, and an absolute timestamp is
/// the one thing you cannot read at a glance: "2026-08-10 17:52" beside a value tells you nothing
/// about whether that value is current until you work out what time it is now. The two together are
/// what answers the question — the timestamp for the record, the age for the reflex.
/// </para>
///
/// <para>
/// The scale coarsens as it goes, because precision stops meaning anything: minutes matter inside an
/// hour, hours and minutes inside a day, and past a week nobody is counting. Hours carry their
/// minutes because the difference between "11 h" and "11 h 59 min" is most of a working day, and
/// rounding it away in either direction reads as a lie.
/// </para>
/// </summary>
public static class Elapsed
{
    /// <summary>
    /// <paramref name="moment"/> described as an age — "5 min ago", "11 h 23 min ago", "2 days ago".
    /// Null in, empty out, so a caller with no timestamp renders nothing rather than a placeholder.
    /// </summary>
    /// <param name="now">
    /// Passed in rather than read here so this is a pure function and can be tested at a fixed
    /// instant. Callers pass <see cref="DateTimeOffset.Now"/>.
    /// </param>
    public static string Since(DateTimeOffset? moment, DateTimeOffset now)
    {
        if (moment is not { } then)
        {
            return string.Empty;
        }

        var span = now - then;

        // A source whose timestamp is in the future is a clock disagreeing with Vault's, not a
        // prediction. Saying "just now" is the honest reading of "no measurable age".
        if (span < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (span < TimeSpan.FromHours(1))
        {
            return $"{(int)span.TotalMinutes} min ago";
        }

        if (span < TimeSpan.FromDays(1))
        {
            var hours = (int)span.TotalHours;
            var minutes = span.Minutes;

            return minutes == 0 ? $"{hours} h ago" : $"{hours} h {minutes} min ago";
        }

        if (span < TimeSpan.FromDays(7))
        {
            return Count((int)span.TotalDays, "day");
        }

        if (span < TimeSpan.FromDays(30))
        {
            return Count((int)(span.TotalDays / 7), "week");
        }

        // Calendar months and years vary in length; these are the conventional approximations, which
        // is all a phrase like "2 months ago" ever claims. Anything needing the exact interval has
        // the timestamp printed beside it.
        return span < TimeSpan.FromDays(365)
            ? Count((int)(span.TotalDays / 30), "month")
            : Count((int)(span.TotalDays / 365), "year");
    }

    private static string Count(int value, string unit) =>
        value == 1 ? $"1 {unit} ago" : $"{value} {unit}s ago";
}
