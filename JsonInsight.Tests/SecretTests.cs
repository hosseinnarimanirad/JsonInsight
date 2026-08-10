using JsonInsight.Classify;
using JsonInsight.Diff;
using JsonInsight.Model;

namespace JsonInsight.Tests;

[Collection("sample-files")]
public sealed class SecretTests(SampleFiles files)
{
    /// <summary>Values that must never reach a screen, a log, or a report.</summary>
    private static readonly string[] SecretPaths =
    [
        "Elasticsearch:Password",
        "ConnectionStrings:Couchbase:Password",
        "PaymentSettings:BillProvider:ApiKey",
        "PaymentSettings:InquiryProvider:InquiryToken",
        "Encryption:Profile:Key",
    ];

    [Theory]
    [InlineData("Elasticsearch:Password")]
    [InlineData("PaymentSettings:BillProvider:ApiKey")]
    [InlineData("PaymentSettings:Hub:PrivateKey")]
    [InlineData("Encryption:Profile:Key")]
    public void Credential_paths_classify_as_secret(string path)
    {
        Assert.Equal(ValueClass.Secret, files.Classifier.Classify(path, "whatever"));
    }

    [Theory]
    [InlineData("Elasticsearch:Url")]
    [InlineData("ConnectionStrings:Couchbase:Modules:Auth:Url")]
    [InlineData("AccountSettings:ProxyUrl")]
    public void Deployment_paths_classify_as_infra(string path)
    {
        Assert.Equal(ValueClass.Infra, files.Classifier.Classify(path, "http://example"));
    }

    [Theory]
    [InlineData("PaymentSettings:Hub:CardTransfer:Banks:BANK_A:Terminal")]
    [InlineData("PaymentSettings:BillInquiryProvider")]
    [InlineData("AccountSettings:NightlyApprovalJob:BatchSize")]
    public void Business_constants_stay_business(string path)
    {
        Assert.Equal(ValueClass.Business, files.Classifier.Classify(path, "1234567"));
    }

    /// <summary>
    /// Secret precedence beats infra even when the key also looks like a URL, so a key named
    /// something like ConnectionStrings:...:Password can never be rendered.
    /// </summary>
    [Fact]
    public void Secret_precedence_beats_infra()
    {
        Assert.Equal(ValueClass.Secret,
            files.Classifier.Classify("ConnectionStrings:Couchbase:Password", "http://looks-like-a-url"));
    }

    [Fact]
    public void Secret_leaves_never_expose_their_value_through_the_display_path()
    {
        foreach (var document in files.Documents)
        {
            foreach (var leaf in document.Flat.Leaves.Values.Where(l => l.Class == ValueClass.Secret))
            {
                if (leaf.Value.Length == 0)
                {
                    continue;
                }

                Assert.DoesNotContain(leaf.Value, leaf.DisplayValue, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// The grid binds to cell Display strings. No real secret may appear in any of them, for any
    /// tier, at any time.
    /// </summary>
    [Fact]
    public void No_secret_value_appears_in_any_rendered_cell()
    {
        var secrets = files.Documents
            .SelectMany(d => SecretPaths.Select(p => d.Flat.Find(p)?.Value))
            .Where(v => !string.IsNullOrEmpty(v))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(secrets);

        var rendered = files.Multi.Rows.SelectMany(r => r.Cells).Select(c => c.Display).ToArray();

        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(rendered, cell => cell.Contains(secret!, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Sweeps every tier for keys whose name says "credential" and asserts none is left as a
    /// business value. This is the test that caught PaymentSettings:Encryption:Profile:Key being
    /// rendered in clear because the Encryption rule was anchored at the root.
    /// </summary>
    [Fact]
    public void No_credential_shaped_key_is_classified_as_a_business_value()
    {
        var suspicious = files.Documents
            .SelectMany(d => d.Flat.Leaves.Values)
            .Where(l => System.Text.RegularExpressions.Regex.IsMatch(
                ConfigPath.Last(l.Path),
                "(?i)(password|apikey|token|secret|privatekey|publickey|pem|encryptionkey)$"))
            .Where(l => l.Class == ValueClass.Business)
            .Select(l => l.Path)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(suspicious.Length == 0,
            "These credential-shaped keys would render in clear: " + string.Join(", ", suspicious));
    }

    [Fact]
    public void Encryption_material_is_secret_wherever_it_is_nested()
    {
        Assert.Equal(ValueClass.Secret, files.Classifier.Classify("Encryption:Profile:Key", "x"));
        Assert.Equal(ValueClass.Secret,
            files.Classifier.Classify("PaymentSettings:Encryption:Profile:Key", "x"));
        Assert.Equal(ValueClass.Secret,
            files.Classifier.Classify("PaymentSettings:GatewayB:Iban:RsaKeyPem", "x"));
    }

    /// <summary>
    /// A Couchbase scope set is literally named "token". Masking it as a credential would hide a
    /// collection name and turn it into a placeholder on promote.
    /// </summary>
    [Fact]
    public void A_scope_named_token_is_not_treated_as_a_credential()
    {
        Assert.NotEqual(ValueClass.Secret, files.Classifier.Classify(
            "ConnectionStrings:Couchbase:Modules:Auth:Scopes:token", "tokens"));
    }

    [Fact]
    public void Fingerprints_match_for_identical_secrets_and_differ_otherwise()
    {
        Assert.Equal(SecretMasker.Fingerprint("abc"), SecretMasker.Fingerprint("abc"));
        Assert.NotEqual(SecretMasker.Fingerprint("abc"), SecretMasker.Fingerprint("abd"));
        Assert.Equal(6, SecretMasker.Fingerprint("abc").Length);
    }

    [Fact]
    public void Masked_description_reveals_only_length_and_fingerprint()
    {
        var description = SecretMasker.Describe("super-secret-value");

        Assert.DoesNotContain("super", description, StringComparison.Ordinal);
        Assert.Contains("len 18", description, StringComparison.Ordinal);
    }
}

