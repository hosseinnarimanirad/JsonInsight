using JsonInsight.Diff;

namespace JsonInsight.Tests;

/// <summary>
/// Splitting and walking canonical paths, and in particular the one place an array element differs
/// from every other segment: it carries its index or identity <em>inside</em> the segment.
/// </summary>
public sealed class ConfigPathTests
{
    /// <summary>The identity suffix is one segment, colons inside it included.</summary>
    [Theory]
    [InlineData("A:B:C", new[] { "A", "B", "C" })]
    [InlineData("Serilog:WriteTo[Name=Seq]:serverUrl", new[] { "Serilog", "WriteTo[Name=Seq]", "serverUrl" })]
    [InlineData("A:B[Name=x:y]:C", new[] { "A", "B[Name=x:y]", "C" })]
    public void Split_keeps_an_identity_suffix_whole(string path, string[] expected) =>
        Assert.Equal(expected, ConfigPath.Split(path));

    [Fact]
    public void Ancestors_of_a_plain_path_are_its_prefixes()
    {
        Assert.Equal(
            ["A", "A:B"],
            ConfigPath.Ancestors("A:B:C"));
    }

    /// <summary>
    /// The array itself is an ancestor of its element. Without this the Tier editor's filter kept
    /// <c>configuration:banners[0]</c> and dropped <c>configuration:banners</c> — so searching for
    /// something inside an array of objects hid the array's row, and the tree, which only recurses
    /// into a row it has emitted, then showed none of the matches it had found.
    /// </summary>
    [Theory]
    [InlineData("configuration:banners[0]:code",
        new[] { "configuration", "configuration:banners", "configuration:banners[0]" })]
    [InlineData("Serilog:WriteTo[Name=Seq]:serverUrl",
        new[] { "Serilog", "Serilog:WriteTo", "Serilog:WriteTo[Name=Seq]" })]
    public void Ancestors_of_an_array_element_include_the_array(string path, string[] expected) =>
        Assert.Equal(expected, ConfigPath.Ancestors(path));

    /// <summary>
    /// Outermost first, and the array before the element inside it. <c>OutermostRemoved</c> takes the
    /// first hit, so a reversed pair would report the element as the top of a deleted subtree.
    /// </summary>
    [Fact]
    public void Ancestors_stay_outermost_first()
    {
        var ancestors = ConfigPath.Ancestors("A:B[0]:C:D").ToArray();

        Assert.Equal(["A", "A:B", "A:B[0]", "A:B[0]:C"], ancestors);
        Assert.True(
            Array.IndexOf(ancestors, "A:B") < Array.IndexOf(ancestors, "A:B[0]"),
            "the array must come before the element inside it");
    }

    /// <summary>
    /// A root-level element has no array name to yield. The bracket has to be preceded by something
    /// for there to be an array path at all.
    /// </summary>
    [Fact]
    public void An_element_of_a_root_array_yields_no_extra_ancestor() =>
        Assert.Equal(["[0]"], ConfigPath.Ancestors("[0]:code"));

    [Fact]
    public void A_single_segment_has_no_ancestors() => Assert.Empty(ConfigPath.Ancestors("A"));

    /// <summary>
    /// <see cref="ConfigPath.Parent"/> drops one whole segment, so an element's parent is the array's
    /// parent rather than the array — <c>Serilog</c>, not <c>Serilog:WriteTo</c>.
    ///
    /// <para>
    /// That is deliberately <em>not</em> the same rule <see cref="ConfigPath.Ancestors"/> follows, and
    /// the difference is worth pinning rather than leaving to be discovered. Ancestors answers "what
    /// rows must exist above this one", where the array is one of them. Parent answers "what should be
    /// selected once this is gone" and "what object might now be empty", and for both of those the
    /// array is the wrong answer: an array element cannot be removed in the first place, and pruning
    /// an emptied array is not what the empty-object rule is for.
    /// </para>
    /// </summary>
    [Fact]
    public void Parent_drops_a_whole_segment_where_Ancestors_names_the_array()
    {
        Assert.Equal("Serilog", ConfigPath.Parent("Serilog:WriteTo[Name=Seq]"));
        Assert.Equal("WriteTo[Name=Seq]", ConfigPath.Last("Serilog:WriteTo[Name=Seq]"));

        Assert.Contains("Serilog:WriteTo", ConfigPath.Ancestors("Serilog:WriteTo[Name=Seq]:serverUrl"));
    }
}
