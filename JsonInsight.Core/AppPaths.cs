using System.IO;

namespace JsonInsight;

/// <summary>
/// Resolves where the config files and the tier snapshots live.
///
/// <para>
/// Tier paths in config/tiers.json are relative to the <em>content root</em>: the folder this app's
/// own repository sits in. That is the only anchor it has, and deliberately the only one - naming a
/// neighbouring project here would make the app depend on a repository it does not own, and a tier
/// pointing anywhere else is a config edit rather than a code change. Detecting it by walking up
/// means the app works from bin\Debug, from a published folder, or from a test run with nothing
/// hardcoded.
/// </para>
/// </summary>
public static class AppPaths
{
    public const string RootOverrideVariable = "JSONINSIGHT_ROOT";

    /// <summary>Points the app at a config folder elsewhere; see <see cref="ConfigDirectory"/>.</summary>
    public const string ConfigOverrideVariable = "JSONINSIGHT_CONFIG";

    /// <summary>
    /// Points the app at a settings file elsewhere. The escape hatch for an installed layout the
    /// fallback below guesses wrong, and how a second front end runs against its own settings without
    /// a code change.
    /// </summary>
    public const string SettingsOverrideVariable = "JSONINSIGHT_SETTINGS";

    /// <summary>
    /// The application-data folder this app owns, per platform. Only ever the fallback: on a
    /// developer machine the authored file is found first, which is what keeps both front ends
    /// reading the same projects and connections.
    ///
    /// <para>
    /// macOS is named explicitly because .NET maps <see cref="Environment.SpecialFolder.ApplicationData"/>
    /// to <c>~/.config</c> there rather than to the Application Support folder every other Mac app
    /// uses, and a config tool that hides its settings somewhere unidiomatic is one whose settings
    /// nobody can find.
    /// </para>
    /// </summary>
    public static string UserDataDirectory
    {
        get
        {
            if (OperatingSystem.IsMacOS())
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library",
                    "Application Support",
                    "JsonInsight");
            }

            // Windows -> %APPDATA%; Linux -> $XDG_CONFIG_HOME or ~/.config.
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "JsonInsight");
        }
    }

    /// <summary>
    /// The repository folder, identified by a file that can only be at its top. Two names because
    /// there are two solution files - the modern one, and the classic one Visual Studio 2022 opens.
    /// Either counts, so deleting whichever you do not use cannot quietly move the content root and
    /// with it every relative path in tiers.json.
    /// </summary>
    private static readonly string[] RepositoryMarkers = ["JsonInsight.slnx", "JsonInsight.sln"];

    private static string? _contentRoot;
    private static string? _configDirectory;
    private static string? _appSettingsFile;

    /// <summary>
    /// What relative tier paths resolve against: the folder holding this app's repository. Override
    /// it with <see cref="RootOverrideVariable"/> to point the app at snapshots kept elsewhere.
    /// </summary>
    public static string ContentRoot => _contentRoot ??= ResolveContentRoot();

    /// <summary>The folder containing tiers.json and friends.</summary>
    public static string ConfigDirectory => _configDirectory ??= ResolveConfigDirectory();

    public static string ConfigFile(string name) => Path.Combine(ConfigDirectory, name);

    /// <summary>
    /// appsettings.json, holding the non-secret half of the Vault connection settings. It sits beside
    /// JsonInsight.csproj rather than in config\ because it is the file the Sources tab writes back to,
    /// and the same authored-over-bin preference applies: writing the build output would be undone by
    /// the next build.
    /// </summary>
    public static string AppSettingsFile => _appSettingsFile ??= ResolveAppSettingsFile();

    /// <summary>Resolves a tiers.json-style path (relative to the content root) to a full path.</summary>
    public static string ResolveFromRoot(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            return Path.GetFullPath(relativePath);
        }

        return Path.GetFullPath(Path.Combine(ContentRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>
    /// The parent of the folder holding a solution file. The parent rather than the repository
    /// itself so that <c>JsonInsight/…</c> in tiers.json reads the same way as any sibling folder
    /// someone chooses to keep snapshots in.
    /// </summary>
    private static string ResolveContentRoot()
    {
        var overridden = Environment.GetEnvironmentVariable(RootOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return Path.GetFullPath(overridden);
        }

        foreach (var start in CandidateStarts())
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (RepositoryMarkers.Any(marker => File.Exists(Path.Combine(directory.FullName, marker))) &&
                    directory.Parent is { } parent)
                {
                    return parent.FullName;
                }

                directory = directory.Parent;
            }
        }

        // Published layout: nothing above the executable identifies a repository, so the folder the
        // app was deployed into is what relative paths hang off.
        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// Prefers the authored config folder over the copy in bin\. These files are meant to be
    /// hand-edited while the app runs; reading the build output would silently ignore those edits
    /// until the next rebuild. Falls back to the output copy for a published app.
    ///
    /// <para>
    /// The authored folder moved to JsonInsight.Core when the engine was split out of the WPF project,
    /// so the folder holding <c>JsonInsight.Core.csproj</c> is what is looked for now. The pre-split
    /// location is still checked after it: an older working copy, or a bin\ layout from before the
    /// move, should keep resolving rather than fall through to the build output and quietly stop
    /// honouring edits. Both front ends walk the same list and land on the same folder.
    /// </para>
    /// </summary>
    private static string ResolveConfigDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable(ConfigOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden) &&
            File.Exists(Path.Combine(overridden, "tiers.json")))
        {
            return Path.GetFullPath(overridden);
        }

        // The authored folder, by the project that owns it and by the two layouts a sibling project
        // sees it from. Ordered current-first; the JsonInsight\ entries are the pre-split location.
        string[] relativeCandidates =
        [
            Path.Combine("JsonInsight.Core", "config"),
            "config",
            Path.Combine("JsonInsight", "config"),
        ];

        foreach (var start in CandidateStarts())
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                var owner = File.Exists(Path.Combine(directory.FullName, "JsonInsight.Core.csproj")) ||
                            File.Exists(Path.Combine(directory.FullName, "JsonInsight.csproj"));

                foreach (var relative in relativeCandidates)
                {
                    // A bare "config" only counts inside the project folder that owns it - otherwise
                    // any ancestor happening to hold a config\ folder would capture the lookup.
                    if (relative == "config" && !owner)
                    {
                        continue;
                    }

                    var candidate = Path.Combine(directory.FullName, relative);
                    if (File.Exists(Path.Combine(candidate, "tiers.json")))
                    {
                        return candidate;
                    }
                }

                directory = directory.Parent;
            }
        }

        // Published layout: config sits beside the executable.
        var deployed = Path.Combine(AppContext.BaseDirectory, "config");
        if (File.Exists(Path.Combine(deployed, "tiers.json")))
        {
            return deployed;
        }

        throw new FileNotFoundException(
            $"Could not locate config/tiers.json. Looked beside the executable ({AppContext.BaseDirectory}) " +
            $"and up the tree from it; set {ConfigOverrideVariable} to point at the folder holding it.");
    }

    /// <summary>
    /// Mirrors <see cref="ResolveConfigDirectory"/>: authored file first, build output as the fallback.
    /// Unlike the config files this one may legitimately not exist yet - a fresh clone has no Vault
    /// settings - so the authored project folder is returned as the place to create it.
    /// </summary>
    private static string ResolveAppSettingsFile()
    {
        var overridden = Environment.GetEnvironmentVariable(SettingsOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return Path.GetFullPath(overridden);
        }

        foreach (var start in CandidateStarts())
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "JsonInsight.csproj")))
                {
                    return Path.Combine(directory.FullName, "appsettings.json");
                }

                // Source layout seen from a sibling project (e.g. the test project, or WebJsonInsight).
                // Both front ends resolving here is deliberate: a developer machine running the WPF
                // app and the web one is configuring one set of projects, not two.
                var sibling = Path.Combine(directory.FullName, "JsonInsight", "JsonInsight.csproj");
                if (File.Exists(sibling))
                {
                    return Path.Combine(Path.GetDirectoryName(sibling)!, "appsettings.json");
                }

                directory = directory.Parent;
            }
        }

        // Installed layout: the per-user application-data folder, not the folder holding the binary.
        // Writing beside the executable works from a self-contained folder and fails everywhere the
        // binary lands somewhere read-only - Program Files, /usr/lib, a macOS .app bundle - which is
        // every real install on the three platforms this now has to run on.
        return Path.Combine(UserDataDirectory, "appsettings.json");
    }

    private static IEnumerable<string> CandidateStarts()
    {
        yield return AppContext.BaseDirectory;
        yield return Directory.GetCurrentDirectory();
    }
}
