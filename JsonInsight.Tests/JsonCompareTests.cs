using JsonInsight.Diff;
using JsonInsight.Loading;
using JsonInsight.Model;
using JsonInsight.ViewModels;

namespace JsonInsight.Tests;

/// <summary>
/// The free-form compare tab. Paths come from tiers.json rather than being spelled out, so these
/// stay valid when the snapshots are re-pulled from Vault under a new version number.
/// </summary>
[Collection("sample-files")]
public sealed class JsonCompareTests(SampleFiles files)
{
    private JsonCompareVm Vm() => new(new TierLoader(files.Arrays, files.Classifier), files.Aliases);

    private JsonCompareVm Loaded(string leftId, string rightId)
    {
        var vm = Vm();
        vm.LeftPath = SampleFiles.PathOf(leftId);
        vm.RightPath = SampleFiles.PathOf(rightId);
        return vm;
    }

    [Fact]
    public void Compares_by_key_path_and_finds_the_deliberate_stage_beta_difference()
    {
        var vm = Loaded("stage", "beta");

        // The one intended divergence: stage sits behind a gateway that verifies the admin token and
        // injects admin_* headers; beta's gateway does not have that yet, so it verifies the bearer
        // itself. If this ever reads Same, one of the two tiers has been changed without the other.
        var trust = vm.Rows.Single(r => r.Path == "AdminSettings:TrustGatewayHeaders");
        Assert.Equal(DiffKind.ValueDiffers, trust.Kind);

        // Both tiers sign with the same key pair, so the PEM must not show up as a difference.
        var pem = new JsonCompareVm(new TierLoader(files.Arrays, files.Classifier), files.Aliases);
        pem.LeftPath = SampleFiles.PathOf("stage");
        pem.RightPath = SampleFiles.PathOf("beta");
        pem.ShowIdentical = true;
        Assert.Equal(DiffKind.Same, pem.Rows.Single(r => r.Path == "AdminSettings:Jwt:PrivateKeyPem").Kind);
    }

    [Fact]
    public void A_file_compared_with_itself_reports_nothing()
    {
        var vm = Vm();
        vm.LeftPath = SampleFiles.PathOf("beta");
        vm.RightPath = SampleFiles.PathOf("beta");

        Assert.Empty(vm.Rows);

        vm.ShowIdentical = true;
        Assert.Equal(files.Beta.Flat.Count, vm.Rows.Count);
        Assert.All(vm.Rows, r => Assert.Equal(DiffKind.Same, r.Kind));
    }

    [Fact]
    public void Show_identical_switches_between_all_values_and_only_different_ones()
    {
        var vm = Loaded("stage", "beta");

        var differencesOnly = vm.Rows.Count;
        Assert.All(vm.Rows, r => Assert.NotEqual(DiffKind.Same, r.Kind));

        vm.ShowIdentical = true;
        var everything = vm.Rows.Count;

        Assert.True(everything > differencesOnly,
            $"Showing all values should add rows, but went from {differencesOnly} to {everything}.");
        Assert.Contains(vm.Rows, r => r.Kind == DiffKind.Same);
    }

    /// <summary>
    /// The tab can open any file, including one full of live credentials. Nothing may render a
    /// secret's content - the admin signing key in the vault snapshots is the worst case.
    /// </summary>
    [Fact]
    public void Secret_values_are_shown_as_a_fingerprint_never_as_their_content()
    {
        var vm = Loaded("stage", "beta");
        vm.ShowIdentical = true;

        var pem = vm.Rows.Single(r => r.Path == "AdminSettings:Jwt:PrivateKeyPem");
        Assert.True(pem.IsSecret, "An inline PEM must classify as a secret.");
        Assert.Contains(Leaf.SecretPlaceholder, pem.LeftValue, StringComparison.Ordinal);

        foreach (var row in vm.Rows)
        {
            Assert.DoesNotContain("BEGIN PRIVATE KEY", row.LeftValue, StringComparison.Ordinal);
            Assert.DoesNotContain("BEGIN PRIVATE KEY", row.RightValue, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Absent_on_one_side_renders_as_a_dash_not_as_an_empty_value()
    {
        var vm = Loaded("dev", "stage");
        var onlyOnOneSide = vm.Rows.First(r => r.Kind == DiffKind.OnlyInLeft);

        Assert.Equal("—", onlyOnOneSide.RightValue);
        Assert.NotEqual("—", onlyOnOneSide.LeftValue);
    }

    [Fact]
    public void Filter_narrows_to_matching_paths_and_is_case_insensitive()
    {
        var vm = Loaded("stage", "beta");
        vm.ShowIdentical = true;

        vm.Filter = "adminsettings";

        Assert.NotEmpty(vm.Rows);
        Assert.All(vm.Rows, r => Assert.StartsWith("AdminSettings", r.Path, StringComparison.Ordinal));
    }

    [Fact]
    public void Swapping_exchanges_the_two_sides()
    {
        var vm = Loaded("stage", "beta");
        var (left, right) = (vm.LeftPath, vm.RightPath);

        vm.SwapCommand.Execute(null);

        Assert.Equal(right, vm.LeftPath);
        Assert.Equal(left, vm.RightPath);
    }

    [Fact]
    public void A_file_that_cannot_be_read_reports_an_error_rather_than_throwing()
    {
        var vm = Vm();
        vm.LeftPath = SampleFiles.PathOf("beta");
        vm.RightPath = Path.Combine(Path.GetTempPath(), "jsoninsight-does-not-exist.json");

        Assert.NotEqual(string.Empty, vm.Error);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public void A_file_that_is_not_json_reports_an_error_rather_than_throwing()
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"jsoninsight-not-json-{Guid.NewGuid():N}.json");
        File.WriteAllText(scratch, "this is not json at all");

        try
        {
            var vm = Vm();
            vm.LeftPath = SampleFiles.PathOf("beta");
            vm.RightPath = scratch;

            Assert.NotEqual(string.Empty, vm.Error);
            Assert.Empty(vm.Rows);
        }
        finally
        {
            File.Delete(scratch);
        }
    }
}
