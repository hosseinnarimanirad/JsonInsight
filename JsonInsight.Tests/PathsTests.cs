using JsonInsight.Vault;

namespace JsonInsight.Tests;

/// <summary>
/// Where the app reads its rules from and where it keeps its settings.
///
/// <para>
/// These exist because splitting the engine into JsonInsight.Core broke the first one silently. The
/// authored config folder moved with the engine, the lookup was still asking for the folder holding
/// <c>JsonInsight.csproj</c>, and every path fell through to the copy in bin\ — which is byte-identical
/// on a fresh build, so the whole suite stayed green while the documented "edit a rule file and it
/// takes effect with no rebuild" behaviour had stopped working. A test that only reads values cannot
/// catch that; this one asserts <em>which file</em> was read.
/// </para>
/// </summary>
public sealed class PathsTests
{
    /// <summary>
    /// The rules come from the authored folder, not from a build output. bin\ and obj\ hold copies
    /// that a rebuild overwrites, so resolving to one turns a hand-edit into a change that appears to
    /// do nothing until the next build.
    /// </summary>
    [Fact]
    public void ConfigDirectory_is_the_authored_folder_rather_than_a_build_output()
    {
        var resolved = AppPaths.ConfigDirectory;
        var segments = resolved.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        Assert.True(File.Exists(Path.Combine(resolved, "tiers.json")), $"no tiers.json in {resolved}");
        Assert.DoesNotContain("bin", segments, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("obj", segments, StringComparer.OrdinalIgnoreCase);

        // The folder that owns it: JsonInsight.Core, since the engine moved there.
        var owner = Directory.GetParent(resolved);
        Assert.NotNull(owner);
        Assert.True(
            File.Exists(Path.Combine(owner!.FullName, "JsonInsight.Core.csproj")),
            $"config resolved to {resolved}, whose parent does not hold JsonInsight.Core.csproj");
    }

    /// <summary>All four rule files, not just the one the lookup probes for.</summary>
    [Theory]
    [InlineData("tiers.json")]
    [InlineData("arrays.json")]
    [InlineData("aliases.json")]
    [InlineData("classify.json")]
    public void Every_rule_file_is_present(string name) =>
        Assert.True(File.Exists(AppPaths.ConfigFile(name)), $"{name} missing from {AppPaths.ConfigDirectory}");

    /// <summary>
    /// A test run must resolve to the authored settings file, which is the same one the WPF app
    /// writes. Stated as an assertion because the alternative is worse than it looks: the suite would
    /// be reading — and a careless test writing — a file in the developer's real application-data
    /// folder, beside their live tokens.
    /// </summary>
    [Fact]
    public void AppSettingsFile_is_the_authored_file_rather_than_the_user_data_folder()
    {
        var resolved = AppPaths.AppSettingsFile;

        Assert.Equal("appsettings.json", Path.GetFileName(resolved));
        Assert.StartsWith(AppPaths.ContentRoot, resolved, StringComparison.OrdinalIgnoreCase);
        Assert.False(
            resolved.StartsWith(AppPaths.UserDataDirectory, StringComparison.OrdinalIgnoreCase),
            $"a test run resolved settings to the real user-data folder ({resolved})");
    }

    /// <summary>
    /// The user-secrets file is the one <c>dotnet user-secrets</c> writes, whichever platform this is
    /// on. The two conventions genuinely differ, and deriving both from ApplicationData — as this did
    /// while the app was Windows-only — silently points at nothing on Linux and macOS.
    /// </summary>
    [Fact]
    public void SecretsFile_follows_the_platform_user_secrets_convention()
    {
        var resolved = VaultSettingsStore.SecretsFile;

        Assert.Equal("secrets.json", Path.GetFileName(resolved));
        Assert.Contains(VaultSettingsStore.UserSecretsId, resolved);

        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(Path.Combine("Microsoft", "UserSecrets"), resolved);
        }
        else
        {
            Assert.Contains(Path.Combine(".microsoft", "usersecrets"), resolved);
        }
    }

    /// <summary>
    /// The per-user folder is this app's own, and on macOS it is the one Mac apps actually use —
    /// .NET maps ApplicationData to ~/.config there, which is idiomatic on Linux and nowhere else.
    /// </summary>
    [Fact]
    public void UserDataDirectory_is_platform_idiomatic()
    {
        var resolved = AppPaths.UserDataDirectory;

        Assert.Equal("JsonInsight", Path.GetFileName(resolved));

        if (OperatingSystem.IsMacOS())
        {
            Assert.Contains(Path.Combine("Library", "Application Support"), resolved);
        }
    }
}
