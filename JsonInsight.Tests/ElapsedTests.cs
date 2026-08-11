using JsonInsight.Model;

namespace JsonInsight.Tests;

/// <summary>
/// The age shown beside every source timestamp. Written against a fixed "now" so the thresholds are
/// asserted rather than approximated — the whole value of the phrase is that the boundaries are where
/// a reader expects them.
/// </summary>
public sealed class ElapsedTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    private static string Ago(TimeSpan span) => Elapsed.Since(Now - span, Now);

    [Fact]
    public void No_timestamp_says_nothing_at_all() =>
        Assert.Equal(string.Empty, Elapsed.Since(null, Now));

    /// <summary>Under a minute has no useful number in it, and "0 min ago" reads like a fault.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(59)]
    public void Under_a_minute_is_just_now(int seconds) =>
        Assert.Equal("just now", Ago(TimeSpan.FromSeconds(seconds)));

    /// <summary>
    /// A clock that disagrees with Vault's can put a version in the future. That is a skewed clock,
    /// not a prediction, so it reads as no measurable age rather than as a negative one.
    /// </summary>
    [Fact]
    public void A_timestamp_in_the_future_is_just_now() =>
        Assert.Equal("just now", Ago(TimeSpan.FromMinutes(-30)));

    [Theory]
    [InlineData(1, "1 min ago")]
    [InlineData(5, "5 min ago")]
    [InlineData(59, "59 min ago")]
    public void Inside_an_hour_counts_minutes(int minutes, string expected) =>
        Assert.Equal(expected, Ago(TimeSpan.FromMinutes(minutes)));

    /// <summary>
    /// Hours carry their minutes: the difference between "11 h" and "11 h 59 min" is most of a
    /// working day, and rounding it away in either direction reads as a lie. The exact hour drops the
    /// minutes rather than saying "0 min".
    /// </summary>
    [Theory]
    [InlineData(60, "1 h ago")]
    [InlineData(683, "11 h 23 min ago")]
    [InlineData(1439, "23 h 59 min ago")]
    public void Inside_a_day_counts_hours_and_minutes(int minutes, string expected) =>
        Assert.Equal(expected, Ago(TimeSpan.FromMinutes(minutes)));

    [Theory]
    [InlineData(1, "1 day ago")]
    [InlineData(2, "2 days ago")]
    [InlineData(6, "6 days ago")]
    [InlineData(7, "1 week ago")]
    [InlineData(20, "2 weeks ago")]
    [InlineData(30, "1 month ago")]
    [InlineData(75, "2 months ago")]
    [InlineData(365, "1 year ago")]
    [InlineData(800, "2 years ago")]
    public void Past_a_day_the_scale_coarsens(int days, string expected) =>
        Assert.Equal(expected, Ago(TimeSpan.FromDays(days)));

    /// <summary>One of anything is singular. "1 days ago" is the classic tell of a formatter.</summary>
    [Fact]
    public void One_of_a_unit_is_singular()
    {
        Assert.Equal("1 day ago", Ago(TimeSpan.FromDays(1)));
        Assert.Equal("1 week ago", Ago(TimeSpan.FromDays(7)));
        Assert.Equal("1 month ago", Ago(TimeSpan.FromDays(31)));
        Assert.Equal("1 year ago", Ago(TimeSpan.FromDays(400)));
    }
}
