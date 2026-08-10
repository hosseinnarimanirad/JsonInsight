using JsonInsight.Loading;
using JsonInsight.Model;
using JsonInsight.Sources;
using JsonInsight.Vault;

namespace JsonInsight.Tests;

/// <summary>
/// Comparing a document other than the appsettings root. The risk worth pinning is where each tier
/// reads from: a document is the same four environments at a path beneath their secrets, worked out
/// from tiers.json rather than restated in it, and a tier whose root cannot be worked out has to be
/// reported rather than pointed at a guess.
/// </summary>
public sealed class DocumentTests
{
    private static readonly ConfigDocument Features = ConfigDocument.Parse("resources/config/features.json");

    [Theory]
    [InlineData("/resources/config/features.json", "resources/config/features.json")]
    [InlineData("resources/config/features.json/", "resources/config/features.json")]
    [InlineData("  ", "")]
    public void Paths_normalize_so_one_document_cannot_look_like_two(string typed, string expected)
    {
        Assert.Equal(expected, ConfigDocument.Parse(typed).RelativePath);
    }

    /// <summary>
    /// The root document is tiers.json as authored. Any other is derived: the same environments read
    /// from a path beneath their roots, with a root that tiers.json does not name coming from the
    /// tier's Vault connection.
    /// </summary>
    [Fact]
    public void A_non_root_document_derives_every_tier_from_its_environment_root()
    {
        var tiers = TiersConfig.Load();
        var settings = new VaultSettings
        {
            Connections =
            {
                ["dev"] = new VaultConnection
                {
                    SecretPath = "kv/app/dev",
                    Address = "https://vault.test",
                    Token = "t",
                },
            },
        };

        var (root, noProblems) = DocumentTiers.For(tiers, settings, ConfigDocument.Root);
        Assert.Same(tiers, root);
        Assert.Empty(noProblems);

        var (derived, problems) = DocumentTiers.For(tiers, settings, Features);
        Assert.Empty(problems);

        Assert.Equal(
            "kv/app/dev/resources/config/features.json",
            derived["dev"].VaultPath);

        Assert.Equal(
            "kv/app/stage/resources/config/features.json",
            derived["stage"].VaultPath);

        Assert.All(derived.Tiers, t => Assert.Equal(Features, t.Document));
    }

    /// <summary>
    /// A tier with no vaultPath and no connection either — but whose siblings all say
    /// <c>kv/app/{tier}</c>, so where dev's secrets live is not a mystery. Asking
    /// someone to type out what the other three rows already say is the version of this that made
    /// picking a document report an error about a tier nobody had touched.
    /// </summary>
    [Fact]
    public void A_missing_root_is_inferred_from_the_scheme_the_others_follow()
    {
        var (derived, problems) = DocumentTiers.For(TiersConfig.Load(), new VaultSettings(), Features);

        Assert.Empty(problems);
        Assert.Equal(
            "kv/app/dev/resources/config/features.json",
            derived["dev"].VaultPath);
    }

    /// <summary>
    /// It infers only from unanimity: a wrong path would send a read somewhere nobody chose, so two
    /// schemes that disagree produce an explanation rather than a guess.
    /// </summary>
    [Fact]
    public void A_root_that_cannot_be_worked_out_is_reported_rather_than_silently_dropped()
    {
        var tiers = new TiersConfig
        {
            Tiers =
            [
                new TierDefinition { Id = "dev", Label = "dev" },
                new TierDefinition { Id = "stage", Label = "stage", VaultPath = "kv/one/stage" },
                new TierDefinition { Id = "beta", Label = "beta", VaultPath = "kv/another/beta" },
            ],
        };

        var (derived, problems) = DocumentTiers.For(tiers, new VaultSettings(), Features);

        Assert.DoesNotContain(derived.Tiers, t => t.Id == "dev");
        Assert.Contains(problems, p => p.StartsWith("dev is not in this comparison", StringComparison.Ordinal));
    }

    /// <summary>
    /// A local-file tier is one file — there is no path beneath it for a non-root document to live at,
    /// so it drops out of a non-root comparison with a reason rather than being asked for a Vault root
    /// it does not have.
    /// </summary>
    [Fact]
    public void A_local_file_tier_is_excluded_from_a_non_root_document_with_a_reason()
    {
        var tiers = new TiersConfig
        {
            Tiers =
            [
                new TierDefinition { Id = "dev", Label = "dev", Kind = SourceKind.LocalFile, LocalFilePath = "dev.json" },
                new TierDefinition { Id = "stage", Label = "stage", VaultPath = "kv/app/stage" },
            ],
        };

        var (derived, problems) = DocumentTiers.For(tiers, new VaultSettings(), Features);

        Assert.DoesNotContain(derived.Tiers, t => t.Id == "dev");
        Assert.Single(derived.Tiers, t => t.Id == "stage");
        Assert.Contains(problems, p => p.StartsWith("dev is not in this comparison", StringComparison.Ordinal) &&
                                        p.Contains("local file", StringComparison.Ordinal));
    }

    [Theory]
    // The ordinary case: every sibling names itself last, under one prefix.
    [InlineData("dev", "kv/app/stage", "kv/app/beta", "kv/app/dev")]
    // A root that does not end in its own tier id says nothing about the scheme, and is ignored.
    [InlineData("dev", "kv/app/stage", "kv/somewhere-else", "kv/app/dev")]
    // Two prefixes disagree, so there is no scheme to follow.
    [InlineData("dev", "kv/one/stage", "kv/two/beta", null)]
    public void Inference_needs_the_known_roots_to_agree(string want, string stage, string beta, string? expected)
    {
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["stage"] = stage,
            ["beta"] = beta,
        };

        Assert.Equal(expected, EnvironmentRoots.Infer(want, known));
    }
}
