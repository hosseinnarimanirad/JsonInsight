using System.Text.RegularExpressions;

namespace JsonInsight.Tests;

/// <summary>
/// This repository is public. Nothing in it may name the deployment it grew up against, and
/// nothing in it may carry credential-shaped text.
///
/// <para>
/// The tool reads live configuration - bank gateway keys, database passwords, signing material -
/// so the failure being guarded against is not hypothetical: a snapshot pulled for debugging, a
/// fixture refreshed from a real tier, a hostname pasted into a doc. All three are one careless
/// commit away, and all three are invisible in review. A test is the only thing that catches them
/// every time.
/// </para>
///
/// <para>
/// If this fails, do not add an exclusion. Replace the value.
/// </para>
/// </summary>
public sealed class RepositoryHygieneTests
{
    /// <summary>
    /// Names that identify the deployment this tool was built for. Assembled at runtime so this
    /// file does not itself contain the words it bans.
    /// </summary>
    private static readonly string[] ForbiddenNames =
    [
        "se" + "keh", "ro" + "yanteam", "kv_" + "royan", "bank" + "mellat", "ry" + "nt.ir",
    ];

    /// <summary>
    /// The institutions the original deployment integrated with. Whole words only, and assembled
    /// the same way as above: several of these are ordinary substrings — "dey" sits inside "ready",
    /// "sina" inside "Sinatra" — so matching them the way the names above are matched would fail on
    /// prose rather than on a leak.
    ///
    /// <para>
    /// This list exists because one survived the first sweep: a rename map that carried
    /// <c>Mellat</c> and <c>MELLAT</c> did not carry <c>mellat</c>, and a lowercase bank name sat in
    /// a test fixture until the whole tree was swept a second time. Names are not always spelled the
    /// way the config spells them.
    /// </para>
    /// </summary>
    private static readonly Regex ForbiddenInstitutions = new(
        @"\b(" + "mel" + "lat|" + "mel" + "li|sad" + "erat|tej" + "arat|pasar" + "gad|par" + "sian|" +
        "ayan" + "deh|kesha" + "varzi|re" + "fah|resa" + "lat|se" + "pah|sar" + "maye|iran" + "zamin|" +
        "en" + "bank|kha" + "var|sha" + "hr|si" + "na|me" + "lal|meh" + "ri|de" + "y|bor" + "na|" +
        "ghab" + "zino|finno" + "tech|shah" + "kar|takh" + "fifan|kah" + "roba)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CredentialShaped = new(
        // A JWT, or a long unbroken base64 run - what a key, token or certificate looks like.
        @"eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}|[A-Za-z0-9+/]{60,}={0,2}",
        RegexOptions.Compiled);

    private static readonly string[] SkipDirectories =
    [
        ".git", ".vs", "bin", "obj", "node_modules", "snapshots", "packages",
    ];

    private static readonly string[] ScannedExtensions =
    [
        ".cs", ".json", ".xaml", ".razor", ".md", ".txt", ".csproj", ".sln", ".slnx",
        ".js", ".css", ".html", ".yml", ".yaml", ".ps1", ".gitignore",
    ];

    [Fact]
    public void No_file_names_the_deployment_this_tool_was_built_for()
    {
        var offenders = new List<string>();

        foreach (var file in RepositoryFiles())
        {
            var text = File.ReadAllText(file);

            // This file holds both lists, so it is the one file allowed to contain them.
            if (Path.GetFileName(file) == "RepositoryHygieneTests.cs")
            {
                continue;
            }

            foreach (var name in ForbiddenNames)
            {
                var index = text.IndexOf(name, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    offenders.Add($"{Relative(file)}: line {LineOf(text, index)} contains \"{name}\"");
                }
            }

            if (ForbiddenInstitutions.Match(text) is { Success: true } institution)
            {
                offenders.Add(
                    $"{Relative(file)}: line {LineOf(text, institution.Index)} names the institution " +
                    $"\"{institution.Value}\"");
            }
        }

        Assert.True(offenders.Count == 0,
            "These files name the originating deployment:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void No_file_carries_credential_shaped_text()
    {
        var offenders = new List<string>();

        foreach (var file in RepositoryFiles())
        {
            var text = File.ReadAllText(file);

            // This file's own pattern, and nothing else, is allowed to look like one.
            if (Path.GetFileName(file) == "RepositoryHygieneTests.cs")
            {
                continue;
            }

            if (CredentialShaped.Match(text) is { Success: true } match)
            {
                offenders.Add(
                    $"{Relative(file)}: line {LineOf(text, match.Index)} holds a " +
                    $"{match.Length}-character credential-shaped string");
            }
        }

        Assert.True(offenders.Count == 0,
            "These files carry credential-shaped text:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The fixtures are the file most likely to be refreshed from a real tier by someone in a
    /// hurry, so they get their own check rather than relying on the sweep above.
    /// </summary>
    [Theory]
    [InlineData("dev")]
    [InlineData("stage")]
    [InlineData("beta")]
    [InlineData("prod")]
    public void Fixture_hosts_are_all_example_domains(string tier)
    {
        var text = File.ReadAllText(SampleFiles.PathOf(tier));

        var hosts = Regex.Matches(text, @"[a-zA-Z][a-zA-Z0-9+.\-]*://([^""/:\s]+)")
            .Select(m => m.Groups[1].Value)
            .Where(host => !host.EndsWith("example.com", StringComparison.OrdinalIgnoreCase))
            .Where(host => !IsPrivateAddress(host))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.True(hosts.Length == 0,
            $"{tier}.json points at hosts that are not example.com: {string.Join(", ", hosts)}");
    }

    private static bool IsPrivateAddress(string host) =>
        host is "localhost" || host.StartsWith("10.", StringComparison.Ordinal) ||
        host.StartsWith("127.", StringComparison.Ordinal);

    /// <summary>
    /// Everything that would end up on the remote: tracked files plus untracked ones that are not
    /// ignored. Asked of git rather than derived from a directory walk, because the question here is
    /// precisely "what would a push publish" - a developer's own <c>appsettings.json</c> names their
    /// real Vault and is gitignored for exactly that reason, and a walk would fail on it forever.
    /// </summary>
    private static IEnumerable<string> RepositoryFiles() =>
        (GitTrackedFiles() ?? WalkedFiles())
        .Where(file => ScannedExtensions.Contains(
                           Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) ||
                       Path.GetFileName(file).Equals(".gitignore", StringComparison.OrdinalIgnoreCase))
        .Where(File.Exists);

    private static IReadOnlyList<string>? GitTrackedFiles()
    {
        try
        {
            using var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                // -z: NUL-separated, so a path with a space or non-ASCII arrives intact.
                ArgumentList = { "ls-files", "--cached", "--others", "--exclude-standard", "-z" },
                WorkingDirectory = RepositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (git is null)
            {
                return null;
            }

            var output = git.StandardOutput.ReadToEnd();
            git.WaitForExit(30_000);

            return git.ExitCode != 0
                ? null
                : output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                    .Select(relative => Path.Combine(RepositoryRoot, relative.Replace('/', Path.DirectorySeparatorChar)))
                    .ToArray();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            // No git on PATH, or not a checkout. Fall back to the walk.
            return null;
        }
    }

    private static IEnumerable<string> WalkedFiles() =>
        Directory.EnumerateFiles(RepositoryRoot, "*", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(RepositoryRoot, file)
                .Split(Path.DirectorySeparatorChar)
                .Any(segment => SkipDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase)));

    private static string Relative(string file) => Path.GetRelativePath(RepositoryRoot, file);

    private static int LineOf(string text, int index) =>
        text.Take(index).Count(c => c == '\n') + 1;

    /// <summary>
    /// The folder holding the solution - not <see cref="AppPaths.ContentRoot"/>, which is its
    /// parent and would drag in whatever else happens to sit beside this checkout.
    /// </summary>
    private static string RepositoryRoot { get; } = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "JsonInsight.slnx")) ||
                File.Exists(Path.Combine(directory.FullName, "JsonInsight.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find the repository root above {AppContext.BaseDirectory}.");
    }
}
