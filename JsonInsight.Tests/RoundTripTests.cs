using JsonInsight.Promote;

namespace JsonInsight.Tests;

/// <summary>
/// The byte format the writer produces.
///
/// <para>
/// It used to be checked by reproducing a snapshot file exactly, which was the strongest proof
/// available while a file was the thing being replaced. Nothing is replaced on disk any more, so
/// what these pin is the property that still matters: the text a push sends is canonical, stable and
/// ordinal-ordered, so a diff against Vault shows content rather than formatting.
/// </para>
/// </summary>
[Collection("sample-files")]
public sealed class RoundTripTests(SampleFiles files)
{
    /// <summary>
    /// Serializing, re-parsing and serializing again must land on the same bytes. This is what makes
    /// the diff against a live secret meaningful — both sides go through here.
    /// </summary>
    [Theory]
    [InlineData("dev")]
    [InlineData("stage")]
    [InlineData("beta")]
    [InlineData("prod")]
    public void Canonical_text_is_a_fixed_point(string tierId)
    {
        var once = OrdinalJsonWriter.SerializeToText(files[tierId].Root);
        var twice = OrdinalJsonWriter.SerializeToText(OrdinalJsonWriter.Parse(once));

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Produced_bytes_carry_no_bom_and_no_trailing_newline()
    {
        var produced = OrdinalJsonWriter.Serialize(files.Beta.Root);

        Assert.False(produced[0] == 0xEF && produced[1] == 0xBB && produced[2] == 0xBF);
        Assert.NotEqual((byte)'\n', produced[^1]);
        Assert.Equal((byte)'}', produced[^1]);
    }

    [Fact]
    public void Line_endings_are_crlf_only()
    {
        var text = OrdinalJsonWriter.SerializeToText(files.Beta.Root);

        var lf = text.Count(c => c == '\n');
        var crlf = text.Split("\r\n").Length - 1;
        Assert.Equal(lf, crlf);
    }

    /// <summary>
    /// Persian text and '&amp;' must survive unescaped. The default JavaScriptEncoder would rewrite
    /// every line containing either, which alone would break byte identity.
    /// </summary>
    [Fact]
    public void Non_ascii_text_is_not_escaped()
    {
        var text = OrdinalJsonWriter.SerializeToText(files.Beta.Root);

        Assert.DoesNotContain("\\u", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Numbers_keep_their_literal_form()
    {
        var node = OrdinalJsonWriter.Parse("""{"a":5000,"b":1.50,"c":0}""");

        var text = OrdinalJsonWriter.SerializeToText(node);

        Assert.Contains("\"a\": 5000", text, StringComparison.Ordinal);
        Assert.DoesNotContain("5000.0", text, StringComparison.Ordinal);
        Assert.Contains("\"b\": 1.50", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The canary for the comparer. Both of these orderings are produced by StringComparer.Ordinal
    /// and by nothing else: OrdinalIgnoreCase puts 'otp' among the capitals, and culture-aware
    /// sorting puts ConnectionString before ConnectTimeoutMs.
    /// </summary>
    [Fact]
    public void Object_keys_sort_with_ordinal_comparison()
    {
        var text = OrdinalJsonWriter.SerializeToText(files.Beta.Root);

        var modules = new[] { "Account", "Auth", "Package", "Profile", "Transaction", "otp" };
        AssertAppearInOrder(text, modules.Select(m => $"\"{m}\":").ToArray());

        var lowercaseLast = text.IndexOf("\"otp\":", StringComparison.Ordinal);
        var transaction = text.IndexOf("\"Transaction\":", StringComparison.Ordinal);
        Assert.True(lowercaseLast > transaction,
            "Lowercase 'otp' must sort after the capitalised module names.");
    }

    [Fact]
    public void Redis_keys_sort_connect_timeout_before_connection_string()
    {
        // 'T' (0x54) precedes 'i' (0x69) under ordinal comparison; a culture-aware sort reverses it.
        var text = OrdinalJsonWriter.SerializeToText(files.Stage.Root);

        var connectTimeout = text.IndexOf("\"ConnectTimeoutMs\":", StringComparison.Ordinal);
        var connectionString = text.IndexOf("\"ConnectionString\":", StringComparison.Ordinal);

        Assert.True(connectTimeout >= 0 && connectionString >= 0);
        Assert.True(connectTimeout < connectionString,
            "ConnectTimeoutMs must sort before ConnectionString (ordinal), not after it.");
    }

    [Fact]
    public void Array_element_order_is_preserved_not_sorted()
    {
        var text = OrdinalJsonWriter.SerializeToText(files.Stage.Root);

        // Serilog sinks are ordered as authored; sorting them would rewrite the file.
        AssertAppearInOrder(text, ["\"Console\"", "\"ElasticsearchManaged\"", "\"Seq\""]);
    }

    private static void AssertAppearInOrder(string text, IReadOnlyList<string> needles)
    {
        var position = 0;
        foreach (var needle in needles)
        {
            var found = text.IndexOf(needle, position, StringComparison.Ordinal);
            Assert.True(found >= 0, $"'{needle}' not found after position {position}.");
            position = found;
        }
    }
}
