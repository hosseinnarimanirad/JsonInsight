using JsonInsight.Classify;
using JsonInsight.Loading;
using JsonInsight.Model;
using JsonInsight.Vault;
using JsonInsight.ViewModels;

namespace WebJsonInsight.Tests;

/// <summary>
/// Documents to render against, built from JSON written here rather than read from anywhere.
///
/// <para>
/// The WPF suite renders against real the application payloads out of <c>Fixtures</c>, which
/// is the right choice there: the point of those tests is that <em>those specific documents</em> are
/// handled correctly. These tests ask a narrower question — does the component render, and does it
/// render the states that only appear when the data has a particular shape — so the payloads are
/// written to produce those states on purpose: a key only one tier has, a value two tiers disagree
/// on, a secret, a subtree missing wholesale, an array.
/// </para>
///
/// <para>
/// Inline also means no credential files are needed, so this project runs on a machine that has
/// never seen a the application snapshot — a Linux CI box, for one.
/// </para>
/// </summary>
internal static class Fixtures
{
    private static readonly Flattener Flattener = new(ArrayStrategies.Load(), Classifier.Load());

    /// <summary>
    /// dev, and the tier the others are compared against. Holds
    /// <c>AccountSettings:NightlyApprovalJob</c>, which no other tier has — the rolled-up
    /// missing-subtree row the grid collapses and Promote acts on.
    /// </summary>
    private const string DevJson = """
        {
          "AccountSettings": {
            "NightlyApprovalJob": {
              "Enabled": true,
              "IntervalSeconds": 300,
              "BatchSize": 50
            }
          },
          "ConnectionStrings": {
            "Couchbase": {
              "Url": "couchbase://10.0.0.34",
              "Username": "app",
              "Password": "dev-couchbase-password-value"
            }
          },
          "PaymentSettings": {
            "Hub": { "TerminalId": "12345", "Timeout": 30 }
          },
          "Serilog": {
            "WriteTo": [
              { "Name": "Console" },
              { "Name": "File", "Path": "logs/dev.log" }
            ]
          },
          "configuration": {
            "app": {
              "android": {
                "forceUpdate": {
                  "body": [
                    "مورد اول",
                    "مورد دوم",
                    "مورد سوم",
                    "مورد چهارم",
                    "مورد پنجم"
                  ],
                  "footer": "متن نمونه برای پانویس.",
                  "header": "عنوان نمونه."
                }
              }
            },
            "ports": [8080, 8443],
            "banners": [
              { "code": "bundle-a", "order": 1 },
              { "code": "app", "order": 2 }
            ]
          }
        }
        """;

    /// <summary>stage — no NightlyApprovalJob, a different Couchbase URL, and an extra AdminSettings section.</summary>
    private const string StageJson = """
        {
          "AdminSettings": { "Enabled": false },
          "ConnectionStrings": {
            "Couchbase": {
              "Url": "couchbase://10.0.0.11,10.0.0.12",
              "Username": "app",
              "Password": "stage-couchbase-password-value"
            }
          },
          "PaymentSettings": {
            "Hub": { "TerminalId": "67890", "Timeout": 30 }
          },
          "Serilog": {
            "WriteTo": [
              { "Name": "Console" },
              { "Name": "File", "Path": "logs/stage.log" }
            ]
          }
        }
        """;

    /// <summary>beta — same shape as stage, one more value that differs.</summary>
    private const string BetaJson = """
        {
          "AdminSettings": { "Enabled": true },
          "ConnectionStrings": {
            "Couchbase": {
              "Url": "couchbase://10.1.0.11,10.1.0.12",
              "Username": "app",
              "Password": "beta-couchbase-password-value"
            }
          },
          "PaymentSettings": {
            "Hub": { "TerminalId": "67890", "Timeout": 45 }
          },
          "Serilog": {
            "WriteTo": [
              { "Name": "Console" },
              { "Name": "File", "Path": "logs/beta.log" }
            ]
          }
        }
        """;

    public static TierDocument Dev => AsTier("dev", 12, DevJson);

    public static TierDocument Stage => AsTier("stage", 34, StageJson);

    public static TierDocument Beta => AsTier("beta", 8, BetaJson);

    public static IReadOnlyList<TierDocument> Documents => [Dev, Stage, Beta];

    /// <summary>A payload as a tier, exactly as a Vault read produces one.</summary>
    public static TierDocument AsTier(string tierId, int version, string json, bool writable = true)
    {
        var definition = new TierDefinition
        {
            Id = tierId,
            Label = tierId,
            Writable = writable,
            VaultPath = $"kv/app/{tierId}",
        };

        return new VaultTierLoader(Flattener).Build(
            definition,
            new VaultReadResult(json, version, null, definition.VaultPath!, "https://vault.test"));
    }

    /// <summary>
    /// A source that could not be read, for the column that keeps its place and says so. Takes a
    /// whole <see cref="TierDefinition"/> rather than an id because that is what the grid needs to
    /// keep the column in its configured position.
    /// </summary>
    public static TierUnavailable Unavailable(string tierId, string reason) => new(
        new TierDefinition
        {
            Id = tierId,
            Label = tierId,
            VaultPath = $"kv/app/{tierId}",
        },
        reason);

    /// <summary>
    /// A MainVm with the tabs built and nothing read from anywhere. <c>vaultAtStartup: false</c> is
    /// the switch that keeps the suite off the network — a test that quietly reached production Vault
    /// would be worse than one that failed.
    ///
    /// <para>
    /// Not called <c>Main</c>: a static method by that name in a test assembly is a candidate entry
    /// point, and the test SDK generates one of its own.
    /// </para>
    /// </summary>
    public static MainVm NewMain(
        IReadOnlyList<TierDocument>? documents = null,
        IReadOnlyList<TierUnavailable>? unavailable = null)
    {
        var main = new MainVm(vaultAtStartup: false);
        main.Seed(documents ?? Documents, unavailable);
        return main;
    }
}
