using JsonInsight;
using JsonInsight.Classify;
using JsonInsight.Diff;
using JsonInsight.Loading;
using JsonInsight.Model;
using JsonInsight.Promote;
using JsonInsight.Vault;

namespace JsonInsight.Tests;

/// <summary>
/// The payloads every test runs against, loaded once per run.
///
/// <para>
/// They are derived from four real tier documents, which is what makes them worth
/// having: the point of this suite has always been that <em>documents of this shape</em>
/// are handled correctly, and a payload somebody invented from scratch would not have
/// the awkward corners - a subtree only one tier carries, two tiers holding the same
/// concept in different shapes, a scope set literally named "token" - that the diff,
/// alias and classification machinery exists to survive.
/// </para>
///
/// <para>
/// Every value has been replaced and every name that identified the originating
/// deployment renamed. Structure is untouched: same keys, same nesting, same array
/// lengths, same JSON types, so the counts these tests pin still mean what they meant.
/// Credentials became fixed-alphabet placeholders, hosts became <c>example.com</c>,
/// URLs kept their scheme so the value-shape classifier still reads them as infra, and
/// identifiers kept their digit width. Nothing here is a secret, and
/// <see cref="RepositoryHygieneTests"/> fails the build if that ever stops being true.
/// </para>
///
/// <para>
/// They ship in the build output (see the csproj), so the suite runs from a bare clone
/// with no sibling repository and nothing handed over out of band - a fresh CI box
/// included.
/// </para>
/// </summary>
public sealed class SampleFiles
{
    /// <summary>Where the payloads sit, beside the test binary.</summary>
    public const string FixtureFolder = "Fixtures";

    private static readonly (string Tier, string File, int Version)[] Payloads =
    [
        ("dev", "dev.json", 25),
        ("stage", "stage.json", 34),
        ("beta", "beta.json", 8),
        ("prod", "prod.json", 6),
    ];

    public SampleFiles()
    {
        Tiers = TiersConfig.Load();
        Arrays = ArrayStrategies.Load();
        Classifier = Classifier.Load();
        Aliases = AliasSet.Load();
        Flattener = new Flattener(Arrays, Classifier);

        Documents = Payloads
            .Select(p => AsTier(p.Tier, p.Version, OrdinalJsonWriter.ReadText(PathOf(p.Tier))))
            .ToArray();
    }

    public TiersConfig Tiers { get; }

    public ArrayStrategies Arrays { get; }

    public Classifier Classifier { get; }

    public AliasSet Aliases { get; }

    public Flattener Flattener { get; }

    public IReadOnlyList<TierDocument> Documents { get; }

    public TierDocument Dev => this["dev"];

    public TierDocument Stage => this["stage"];

    public TierDocument Beta => this["beta"];

    public TierDocument Prod => this["prod"];

    public TierDocument this[string id] =>
        Documents.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"No fixture named '{id}'.");

    /// <summary>
    /// Where a fixture payload sits on disk, for the tests that are about reading files - the
    /// Compare files tab is a real feature and browsing is how it works.
    /// </summary>
    public static string PathOf(string tierId)
    {
        var payload = Payloads.First(p => p.Tier.Equals(tierId, StringComparison.OrdinalIgnoreCase));
        var path = Path.Combine(AppContext.BaseDirectory, FixtureFolder, payload.File);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Fixture '{payload.File}' is missing from {Path.GetDirectoryName(path)}. " +
                "It ships in the build output; check the Content item in JsonInsight.Tests.csproj.",
                path);
        }

        return path;
    }

    /// <summary>A payload as a tier, exactly as a Vault read produces one.</summary>
    public TierDocument AsTier(string tierId, int version, string json, bool writable = true)
    {
        var definition = Tiers.Tiers.FirstOrDefault(t => t.Id.Equals(tierId, StringComparison.OrdinalIgnoreCase))
                         ?? new TierDefinition
                         {
                             Id = tierId,
                             Label = tierId,
                             Writable = writable,
                             VaultPath = $"kv/app/{tierId}",
                         };

        return new VaultTierLoader(Flattener).Build(
            writable ? definition : ReadOnly(definition),
            new VaultReadResult(json, version, null, definition.VaultPath ?? $"kv/{tierId}", "https://vault.test"));
    }

    /// <summary>
    /// The same tier, marked read-only.
    ///
    /// <para>
    /// tiers.json no longer ships one. The refusal to write a tier marked read-only is still a real
    /// fence, and one a future tiers.json can still ask for, so the tests that cover it build the
    /// case rather than losing it with the tier that used to supply it.
    /// </para>
    /// </summary>
    public TierDocument ReadOnly(string id = "dev")
    {
        var document = this[id];

        return new TierDocument
        {
            Definition = ReadOnly(document.Definition),
            Root = document.Root,
            Flat = document.Flat,
            Origin = document.Origin,
            VaultVersion = document.VaultVersion,
            VaultAddress = document.VaultAddress,
            VaultSecretPath = document.VaultSecretPath,
        };
    }

    private static TierDefinition ReadOnly(TierDefinition definition) => new()
    {
        Id = definition.Id,
        Label = definition.Label,
        Writable = false,
        VaultPath = definition.VaultPath,
        Document = definition.Document,
    };

    /// <summary>
    /// Settings that name a reachable Vault for every tier, for the checks that run before any read.
    /// A row carries its own address and token now, so "reachable" is a property of each row rather
    /// than of one shared default.
    /// </summary>
    public static VaultSettings Settings()
    {
        var settings = new VaultSettings();

        foreach (var id in new[] { "dev", "stage", "beta", "prod" })
        {
            settings.Connections[id] = new VaultConnection
            {
                SecretPath = $"kv/app/{id}",
                Address = "https://vault.test:8200",
                Token = "test-token",
            };
        }

        return settings;
    }

    public MultiDiff Multi => MultiDiff.Build(Documents.Select(d => d.Flat).ToArray(), Aliases);

    /// <summary>The canonical text of a document, which is what a push would send.</summary>
    public static string Canonical(TierDocument document) => OrdinalJsonWriter.SerializeToText(document.Root);
}

[CollectionDefinition("sample-files")]
public sealed class SampleFilesCollection : ICollectionFixture<SampleFiles>;
