using System.Reflection;

namespace JsonInsight.Platform;

/// <summary>
/// What build this is, as one string both front ends show in the same place.
///
/// <para>
/// The number is not written down anywhere in the tree. MinVer derives it at build time from the
/// nearest <c>v*</c> tag plus the number of commits since it — see the note in Directory.Build.props
/// — and stamps it onto every assembly in the solution as
/// <see cref="AssemblyInformationalVersionAttribute"/>. This class is the only thing that reads it
/// back, so "what version is this" has one answer rather than one per window.
/// </para>
///
/// <para>
/// It reads the version off <em>this</em> assembly rather than off <c>GetEntryAssembly()</c>, for two
/// reasons. The entry assembly differs between the front ends (JsonInsight.exe and
/// WebJsonInsight.exe) and is null outright under some hosts and test runners, so the obvious call is
/// the one that returns nothing exactly when a screenshot of the version would be most useful. And
/// every project in the solution is versioned together by Directory.Build.props, so this assembly's
/// number is the same number the executable in front of it carries — by construction, not by luck.
/// </para>
/// </summary>
public static class AppVersion
{
    /// <summary>
    /// The full informational version, e.g. <c>1.2.0</c> at a tag, or <c>1.2.1-alpha.0.7</c> seven
    /// commits past one. Falls back to <c>0.0.0-unknown</c> if the attribute is somehow missing, which
    /// is a build that was assembled by something other than this solution's build.
    /// </summary>
    public static string Full { get; } = Read();

    /// <summary>
    /// What goes in the header: <see cref="Full"/> with a <c>v</c> in front and any <c>+metadata</c>
    /// suffix dropped. The SDK appends the commit hash there when SourceLink is in the build, which is
    /// worth keeping in <see cref="Full"/> and is forty characters of noise beside a title.
    /// </summary>
    public static string Display { get; } = "v" + Trim(Full);

    /// <summary>
    /// The tooltip behind <see cref="Display"/>. Says what the number means, because
    /// <c>1.2.1-alpha.0.7</c> read cold looks like a broken release rather than a build seven commits
    /// past v1.2.0.
    /// </summary>
    public static string Tooltip { get; } =
        $"JsonInsight {Full}. Derived at build time from the nearest release tag and the number of " +
        "commits since it, so a build with a -alpha suffix is a development build past that release " +
        "rather than a pre-release of it.";

    private static string Read() =>
        typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        is { Length: > 0 } version
            ? version
            : "0.0.0-unknown";

    /// <summary>Everything up to the first <c>+</c>, which is where SemVer build metadata starts.</summary>
    private static string Trim(string version) =>
        version.IndexOf('+') is var plus && plus >= 0 ? version[..plus] : version;
}
