using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using JsonInsight.Promote;
using JsonInsight.Sources;

namespace JsonInsight.Vault;

/// <summary>
/// One source's configuration, keyed by environment id in <see cref="VaultSettings.Connections"/>.
/// The name predates <see cref="Kind"/>: every one of these used to be a Vault connection and nothing
/// else, and the fields below are still exactly that — <see cref="SecretPath"/>, <see cref="Address"/>,
/// <see cref="Namespace"/>, <see cref="Token"/> and <see cref="AllowInsecureTls"/> are meaningful only
/// when <see cref="Kind"/> is <see cref="SourceKind.Vault"/>.
///
/// <para>
/// Self-contained on purpose. There used to be one shared address, namespace and token that a blank
/// field here fell back to, which meant the answer to "what does this row read, and as whom" was
/// never on the row — it was on the row plus a card above it plus a rule about which won. A row now
/// says all of it, and a project whose four rows share a server is a copy rather than an inheritance.
/// </para>
/// </summary>
public sealed class VaultConnection
{
    /// <summary>
    /// The secret this source <em>is</em> — combined mount and full path, e.g.
    /// <c>kv/app/stage/resources/config/ui.json</c>. Vault kind only.
    ///
    /// <para>
    /// A whole path, not a root. It used to name the environment secret and have a separate app-wide
    /// document appended to it, which is two fields to change to answer one question and a derivation
    /// to explain when the answer was surprising. What a row reads is now written on the row.
    /// </para>
    /// </summary>
    public string SecretPath { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;

    public string Namespace { get; set; } = string.Empty;

    /// <summary>
    /// Never read from or written to appsettings.json. It is populated from user secrets on load and
    /// written back only to secrets.json. See <see cref="VaultSettingsStore"/>.
    /// </summary>
    [JsonIgnore]
    public string Token { get; set; } = string.Empty;

    /// <summary>Opt-in for a Vault behind a self-signed certificate. Off unless deliberately set.</summary>
    public bool AllowInsecureTls { get; set; }

    /// <summary>Which kind of source this is. Defaults to Vault — every row before this field existed was one.</summary>
    public SourceKind Kind { get; set; } = SourceKind.Vault;

    /// <summary>The file this source reads and writes. LocalFile kind only.</summary>
    public string LocalFilePath { get; set; } = string.Empty;

    // ---------------------------------------------------------------------------------------------
    // The restart trigger. Uploading to Vault changes nothing on its own - the config extension
    // materialises the secret into a source that can never reload, and nearly every consumer binds
    // IOptions<T> once, so a restart is the only thing that re-reads Vault. This is a client for an
    // endpoint that already exists, not a feature this app implements.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The endpoint that restarts whatever reads this source, e.g.
    /// <c>https://api.example.com/api/v1/dev/admin/restart?drainSeconds=15</c>. Empty when this source
    /// has no restart configured, which is what makes the button refuse rather than guess.
    ///
    /// <para>
    /// Per source, deliberately. Restarting the thing that reads dev and restarting the thing that
    /// reads prod are different acts against different servers, and one shared endpoint would be a
    /// button whose blast radius depends on which row you were last looking at.
    /// </para>
    /// </summary>
    public string RestartUrl { get; set; } = string.Empty;

    /// <summary>
    /// An optional JSON body to POST. Empty sends none, which is what the documented endpoint wants —
    /// it takes its <c>drainSeconds</c> on the query string.
    /// </summary>
    public string RestartBody { get; set; } = string.Empty;

    /// <summary>
    /// Its own TLS opt-in rather than sharing <see cref="AllowInsecureTls"/>: the restart endpoint is
    /// an application API, not the Vault server, and is routinely on a different host with a
    /// different certificate. Coupling them would mean trusting a bad certificate on one to reach the
    /// other.
    /// </summary>
    public bool RestartAllowInsecureTls { get; set; }

    /// <summary>
    /// Never persisted, anywhere — not to appsettings.json, and unlike <see cref="Token"/>, not to
    /// user secrets either. It is typed afresh for every call.
    ///
    /// <para>
    /// That is the whole confirmation step for this button. A restart drops connections on a live
    /// environment and cannot be taken back, and a stored credential would turn it into a one-click
    /// action sitting next to Test on a row of environments that all look alike.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public string RestartToken { get; set; } = string.Empty;

    /// <summary>
    /// This connection, or - when it has no token of its own - a copy carrying the ambient one.
    /// Returns <c>this</c> unchanged when there is nothing to add, so the common path allocates
    /// nothing and a row that names its own token is never second-guessed.
    /// </summary>
    public VaultConnection WithAmbientToken() => WithAmbientToken(VaultSettingsStore.AmbientToken);

    /// <summary>The same, against a token supplied directly. Pure, so it can be tested as such.</summary>
    public VaultConnection WithAmbientToken(string? ambient)
    {
        if (!string.IsNullOrWhiteSpace(Token) || string.IsNullOrWhiteSpace(ambient))
        {
            return this;
        }

        var copy = Clone();
        copy.Token = ambient.Trim();
        return copy;
    }

    public VaultConnection Clone() => new()
    {
        SecretPath = SecretPath,
        Address = Address,
        Namespace = Namespace,
        Token = Token,
        AllowInsecureTls = AllowInsecureTls,
        Kind = Kind,
        LocalFilePath = LocalFilePath,
        RestartUrl = RestartUrl,
        RestartBody = RestartBody,
        RestartAllowInsecureTls = RestartAllowInsecureTls,
    };
}

/// <summary>
/// The whole <c>Vault</c> section of appsettings.json: every project, and the couple of preferences
/// that are about this app rather than about any one of them.
///
/// <para>
/// <see cref="VaultSettings"/> is the view of this that the rest of the app works with — one project.
/// Nothing outside this file and the projects screen deals in a workspace, which is the point: a
/// differ, a pusher and a tier loader have no business knowing that more than one project exists.
/// </para>
/// </summary>
public sealed class VaultWorkspace
{
    /// <summary>The name the pre-projects configuration is migrated into. See <see cref="Migrate"/>.</summary>
    public const string MigratedProjectName = "appsettings";

    /// <summary>See <see cref="VaultSettings.LoadTiersAtStartup"/>. Shared: it is a preference about this app, not about a project.</summary>
    public bool LoadTiersAtStartup { get; set; } = true;

    /// <summary>
    /// Skips the projects screen and reopens <see cref="ActiveProject"/> directly. Off by default: the
    /// app opens on the list, and opening on a list costs one click where opening on the wrong project
    /// costs a Vault read against the wrong secrets and a moment of believing the wrong diff.
    /// </summary>
    public bool AlwaysOpenLastProject { get; set; }

    /// <summary>The project that is open, or was open last. Empty before any has been opened.</summary>
    public string ActiveProject { get; set; } = string.Empty;

    /// <summary>Every project, by name. The name is what the user typed; it is also the key.</summary>
    public Dictionary<string, SourceProject> Projects { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------------------------------------------
    // The pre-projects shape, and the shared credentials that went with it. Read so they can be
    // migrated, and nullable so that once they have been — the serializer drops nulls — they stop
    // being written and the file is left in the new shape only.
    // ---------------------------------------------------------------------------------------------

    public Dictionary<string, VaultConnection>? Connections { get; set; }

    public string? Document { get; set; }

    public List<string>? ActiveSources { get; set; }

    public string? BrowseFrom { get; set; }

    public string? Address { get; set; }

    public string? Namespace { get; set; }

    /// <summary>
    /// Brings an older file up to the current shape, in two steps that can each happen alone.
    ///
    /// <list type="number">
    /// <item>A pre-projects configuration becomes a project called <c>appsettings</c>, so an install
    /// that upgrades opens on the work it already had rather than on an empty list.</item>
    /// <item>The shared address, namespace and token are <em>pushed down</em> into every row that did
    /// not override them, then dropped. A row is self-contained now, and a fallback that silently
    /// supplied the address for three rows out of four would take those three rows' credentials with
    /// it the moment it was removed.</item>
    /// </list>
    ///
    /// <para>
    /// In memory only. Nothing is written until something saves, which makes this safe to run on every
    /// load and safe to run in a test: it is idempotent, and a file that is never saved is never
    /// rewritten. Returns whether anything was actually changed.
    /// </para>
    /// </summary>
    public bool Migrate()
    {
        var hadLegacyShape = Projects.Count == 0 &&
                             (Connections is { Count: > 0 } ||
                              !string.IsNullOrWhiteSpace(Document) ||
                              ActiveSources is { Count: > 0 });

        if (hadLegacyShape)
        {
            Projects[MigratedProjectName] = new SourceProject
            {
                ActiveSources = ActiveSources ?? [],
                Connections = Connections ?? new Dictionary<string, VaultConnection>(StringComparer.OrdinalIgnoreCase),
            };

            ActiveProject = MigratedProjectName;

            // A pre-projects row named an environment root and let one app-wide document be appended
            // to it. A row is a whole path now, so the append happens once, here, rather than on every
            // read forever.
            if (!string.IsNullOrWhiteSpace(Document))
            {
                foreach (var connection in Projects[MigratedProjectName].Connections.Values)
                {
                    if (connection.Kind == SourceKind.Vault && !string.IsNullOrWhiteSpace(connection.SecretPath))
                    {
                        connection.SecretPath = $"{connection.SecretPath.TrimEnd('/')}/{Document!.Trim('/')}";
                    }
                }
            }
        }

        var hadSharedCredentials = !string.IsNullOrWhiteSpace(Address) || !string.IsNullOrWhiteSpace(Namespace);

        if (hadSharedCredentials)
        {
            foreach (var connection in Projects.Values.SelectMany(p => p.Connections.Values))
            {
                if (string.IsNullOrWhiteSpace(connection.Address))
                {
                    connection.Address = Address ?? string.Empty;
                }

                if (string.IsNullOrWhiteSpace(connection.Namespace))
                {
                    connection.Namespace = Namespace ?? string.Empty;
                }
            }
        }

        Connections = null;
        Document = null;
        ActiveSources = null;
        BrowseFrom = null;
        Address = null;
        Namespace = null;

        // The pre-projects shared token has nothing to null here. It lives in user secrets, which are
        // read after this runs, and VaultSettingsStore.MergeSecrets pushes it down onto the rows that
        // had none of their own once it has the value. This method only ever sees the half of the old
        // shape that appsettings.json held.
        return hadLegacyShape || hadSharedCredentials;
    }

    /// <summary>
    /// One project as the rest of the app sees it. An unknown name yields an empty project rather than
    /// throwing — the name comes from a settings file, and a typo there should leave the projects
    /// screen usable, not the app dead.
    /// </summary>
    public VaultSettings SettingsFor(string? projectName)
    {
        var project = projectName is not null && Projects.TryGetValue(projectName, out var found)
            ? found
            : new SourceProject();

        return new VaultSettings
        {
            LoadTiersAtStartup = LoadTiersAtStartup,
            ActiveSources = project.ActiveSources,
            Connections = project.Connections,
        };
    }

    /// <summary>
    /// Writes <paramref name="settings"/> back into <paramref name="projectName"/> — the inverse of
    /// <see cref="SettingsFor"/>, and the only way the Sources tab's edits reach a workspace.
    /// </summary>
    public void Apply(string projectName, VaultSettings settings)
    {
        LoadTiersAtStartup = settings.LoadTiersAtStartup;

        if (!Projects.TryGetValue(projectName, out var project))
        {
            project = new SourceProject();
            Projects[projectName] = project;
        }

        project.ActiveSources = settings.ActiveSources;
        project.Connections = settings.Connections;
    }
}

/// <summary>
/// One project's settings — what every part of this app outside the projects screen means by "the
/// settings".
///
/// <para>
/// The name predates projects. It is still exactly the object it always was, minus the shared
/// credentials it used to carry; what changed is that there is now more than one of them, produced by
/// <see cref="VaultWorkspace.SettingsFor"/> rather than read straight off the file.
/// </para>
/// </summary>
public sealed class VaultSettings
{
    /// <summary>
    /// Whether the All tiers tab reads live from Vault when the app opens, rather than showing whatever
    /// the local snapshots happen to hold. On by default: a snapshot is only as fresh as the last
    /// pull, and opening onto a stale one is how a diff gets trusted that should not be.
    ///
    /// <para>
    /// Turning it off is a legitimate choice for working offline or on a slow link — the local files
    /// still load, and the All tiers tab's pull button fetches on demand.
    /// </para>
    /// </summary>
    public bool LoadTiersAtStartup { get; set; } = true;

    /// <summary>Keyed by environment id — see <see cref="SourceEnvironment"/> for the fixed list.</summary>
    public Dictionary<string, VaultConnection> Connections { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which configured sources are currently being compared — at most <see cref="SourceCatalog.MaxActive"/>
    /// of them, in the order they should appear as columns.
    ///
    /// <para>
    /// Empty means "nobody has chosen here yet", and <see cref="SourceCatalog"/> answers that by
    /// standing aside entirely: <c>config/tiers.json</c> stays authoritative, exactly as it was before
    /// this field existed. That is what lets the Sources tab ship without changing what any existing
    /// installation sees — a chosen set only takes over once it has actually been chosen and saved.
    /// </para>
    /// </summary>
    public List<string> ActiveSources { get; set; } = [];

    /// <summary>
    /// The connection for one source, or an empty one when there is no row for it.
    ///
    /// <para>
    /// A copy rather than the stored object, so a caller that fills in a blank field for a probe does
    /// not edit the settings by doing so. There is nothing to merge any more: a row carries its own
    /// address, namespace and token, and what is missing from it is named by <see cref="Incomplete"/>
    /// rather than quietly supplied from somewhere off screen.
    /// </para>
    ///
    /// <para>
    /// The one thing still supplied from elsewhere is the ambient token — see
    /// <see cref="VaultSettingsStore.AmbientToken"/> — and only onto a row that has none of its own.
    /// It lands on the copy and never on the stored object, which is what keeps it out of
    /// secrets.json: an ambient credential is owned by <c>vault login</c>, and writing it here would
    /// fork it into a stale duplicate this app would then keep using after the real one was renewed.
    /// </para>
    /// </summary>
    public VaultConnection Resolve(string tierId) =>
        (Connections.TryGetValue(tierId, out var connection)
            ? connection.Clone()
            : new VaultConnection()).WithAmbientToken();

    /// <summary>
    /// What is missing before <paramref name="tierId"/> could be <em>reached</em> — the server and
    /// the credentials, not what to ask it for.
    ///
    /// <para>
    /// Separate from <see cref="Incomplete"/> because the secret path does not always come from the
    /// connection: a tier reading a document knows its path from its own definition, and demanding
    /// one here too refused reads whose path was never in question.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Unreachable(string tierId)
    {
        var resolved = Resolve(tierId);
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(resolved.Address))
        {
            missing.Add("address");
        }

        if (string.IsNullOrWhiteSpace(resolved.Token))
        {
            missing.Add("token");
        }

        return missing;
    }

    /// <summary>Everything still missing before <paramref name="tierId"/> could be read from here.</summary>
    public IReadOnlyList<string> Incomplete(string tierId)
    {
        var resolved = Resolve(tierId);
        var missing = Unreachable(tierId).ToList();

        if (string.IsNullOrWhiteSpace(resolved.SecretPath))
        {
            missing.Add("secret path");
        }

        return missing;
    }
}

/// <summary>
/// Loads and saves the <see cref="VaultWorkspace"/> across two files, and is the only place that
/// knows which half goes where.
///
/// <para>
/// The non-secret half - addresses, namespaces, secret paths, projects - lives in
/// <c>JsonInsight/appsettings.json</c>, where it can be read and reviewed. Tokens live in user
/// secrets, at the standard <c>%APPDATA%\Microsoft\UserSecrets\{id}\secrets.json</c> location and in
/// the standard flat colon-delimited key format, so
/// <c>dotnet user-secrets set "Vault:Projects:appsettings:Connections:stage:Token" …</c> and this app
/// edit the same file and see each other's changes.
/// </para>
///
/// <para>
/// A token must never reach appsettings.json. That is enforced structurally rather than by care:
/// <see cref="VaultConnection.Token"/> is <see cref="JsonIgnoreAttribute"/>, so the serializer that
/// produces appsettings.json cannot emit it even if this class is changed later.
/// </para>
/// </summary>
public static class VaultSettingsStore
{
    /// <summary>Matches the UserSecretsId in JsonInsight.csproj; changing one without the other orphans the tokens.</summary>
    public const string UserSecretsId = "jsoninsight-9f3c1d20";

    public const string SectionName = "Vault";

    private const string SharedTokenKey = "Vault:Token";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The file <c>dotnet user-secrets</c> writes:
    /// <c>%APPDATA%\Microsoft\UserSecrets\{id}\secrets.json</c> on Windows,
    /// <c>~/.microsoft/usersecrets/{id}/secrets.json</c> on Linux and macOS.
    ///
    /// <para>
    /// The two are spelled out rather than derived from
    /// <see cref="Environment.SpecialFolder.ApplicationData"/>, which was how this read while the app
    /// was Windows-only. That mapping gives the right answer on Windows and the wrong one everywhere
    /// else — <c>~/.config/Microsoft/UserSecrets</c>, which the tooling does not use — so this app and
    /// <c>dotnet user-secrets</c> would have been editing two different files off Windows while the
    /// README promised they were the same one. A token written by one and not seen by the other reads
    /// as a Vault permission problem, which is the wrong thing to go and investigate.
    /// </para>
    /// </summary>
    public static string SecretsFile =>
        OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft",
                "UserSecrets",
                UserSecretsId,
                "secrets.json")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".microsoft",
                "usersecrets",
                UserSecretsId,
                "secrets.json");

    /// <summary>The environment variable the Vault CLI and every Vault SDK read.</summary>
    public const string TokenVariable = "VAULT_TOKEN";

    /// <summary>
    /// A token this app has not been given and does not store: <c>VAULT_TOKEN</c> if it is set,
    /// otherwise whatever <c>vault login</c> left in <c>~/.vault-token</c>. Null when there is
    /// neither.
    ///
    /// <para>
    /// This is the preferred way to run against a production Vault. The credential stays owned by
    /// the tool that issued it — short-lived, renewed and revoked by <c>vault</c> — while this app
    /// holds nothing on disk of its own. User secrets are unencrypted, so the best answer to "where
    /// should a production token be kept" is that this app should not be keeping one.
    /// </para>
    ///
    /// <para>
    /// Deliberately re-read rather than cached: running <c>vault login</c> while the app is open has
    /// to take effect, and a cached copy would go on presenting a token that has since been renewed
    /// as though the server were rejecting it.
    /// </para>
    /// </summary>
    public static string? AmbientToken => AmbientTokenLookup();

    /// <summary>
    /// How <see cref="AmbientToken"/> is obtained. Replaceable so that a host with its own auth -
    /// an OIDC flow, an AppRole exchange - can supply a token without this app learning about it,
    /// and so the tests that assert "this row has no token" are not at the mercy of whether the
    /// machine running them happens to have had <c>vault login</c> run on it.
    /// </summary>
    public static Func<string?> AmbientTokenLookup { get; set; } = ReadAmbientToken;

    private static string? ReadAmbientToken()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(TokenVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment.Trim();
        }

        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".vault-token");

            if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } token)
            {
                return token;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Unreadable is the same as absent: the row reports a missing token and says so.
        }

        return null;
    }

    /// <summary>Where a project's per-source token lives, e.g. <c>Vault:Projects:config:Connections:stage:Token</c>.</summary>
    public static string ConnectionTokenKey(string projectName, string tierId) =>
        $"Vault:Projects:{projectName}:Connections:{tierId}:Token";

    /// <summary>The pre-projects key for the same thing, read once so an upgrade does not lose a token.</summary>
    private static string LegacyConnectionTokenKey(string tierId) => $"Vault:Connections:{tierId}:Token";

    /// <summary>The suffix every token key ends in, whichever shape the rest of it takes.</summary>
    private const string TokenKeySuffix = ":Token";

    /// <summary>
    /// What lies between <paramref name="prefix"/> and <c>:Token</c>, or null when the key is not
    /// that shape at all.
    ///
    /// <para>
    /// The shared half of the three parsers below — one project-scoped, one pre-projects, one that
    /// works out <em>which</em> project a key belongs to. All three answer "is this a connection
    /// token key" the same way and only disagree about what the middle of it means, which is exactly
    /// the split this makes: recognise the shape here, interpret the body there.
    /// </para>
    ///
    /// <para>
    /// Case-insensitive on both ends, because these keys are compared against a dictionary that is
    /// itself <see cref="StringComparer.OrdinalIgnoreCase"/> — a hand-edited secrets.json writes
    /// <c>vault:projects:…</c> as readily as <c>Vault:Projects:…</c> and the configuration binder
    /// treats them as one key.
    /// </para>
    /// </summary>
    private static string? TokenKeyBody(string key, string prefix) =>
        key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        key.EndsWith(TokenKeySuffix, StringComparison.OrdinalIgnoreCase)
            ? key[prefix.Length..^TokenKeySuffix.Length]
            : null;

    /// <summary>
    /// A body that is exactly one source id: non-empty, and holding no further separator. A body with
    /// a colon in it is some deeper key that merely ends in <c>:Token</c> and is none of this app's
    /// business.
    /// </summary>
    private static string? AsTierId(string? body) =>
        body is { Length: > 0 } && !body.Contains(':') ? body : null;

    /// <summary>The source id in a pre-projects connection token key, or null if it is not one.</summary>
    private static string? LegacyTierIdFromTokenKey(string key) =>
        AsTierId(TokenKeyBody(key, "Vault:Connections:"));

    /// <summary>
    /// The whole workspace: every project, and the credentials they share. A missing or malformed file
    /// yields an empty workspace plus a problem string rather than an exception — the projects screen
    /// and the Sources tab must still open, since they are where a broken setting gets fixed.
    /// </summary>
    public static (VaultWorkspace Workspace, IReadOnlyList<string> Problems) LoadWorkspace()
    {
        var problems = new List<string>();
        var workspace = ReadAppSettings(problems);

        var migrated = workspace.Migrate();
        MergeSecrets(workspace, migrated, problems);

        return (workspace, problems);
    }

    /// <summary>
    /// The active project's settings — what every part of the app outside the projects screen means by
    /// "the settings", and the same shape this returned before projects existed.
    /// </summary>
    public static (VaultSettings Settings, IReadOnlyList<string> Problems) Load()
    {
        var (workspace, problems) = LoadWorkspace();
        return (workspace.SettingsFor(workspace.ActiveProject), problems);
    }

    private static VaultWorkspace ReadAppSettings(List<string> problems)
    {
        var path = AppPaths.AppSettingsFile;
        if (!File.Exists(path))
        {
            return new VaultWorkspace();
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path), nodeOptions: null, OrdinalJsonWriter.DocumentOptions);

            if (root?[SectionName] is not JsonNode section)
            {
                return new VaultWorkspace();
            }

            return section.Deserialize<VaultWorkspace>(ReadOptions) ?? new VaultWorkspace();
        }
        catch (Exception ex)
        {
            problems.Add($"appsettings.json: {ex.Message}");
            return new VaultWorkspace();
        }
    }

    /// <param name="migrated">
    /// True when <see cref="VaultWorkspace.Migrate"/> has just folded a pre-projects file into one
    /// project. Its tokens are still under the old keys, so they are read from there this once and
    /// land under the new ones on the next save.
    /// </param>
    private static void MergeSecrets(VaultWorkspace workspace, bool migrated, List<string> problems)
    {
        var secrets = ReadSecrets(problems);
        if (secrets.Count == 0)
        {
            return;
        }

        // The pre-projects shared token, if there is still one. It is pushed into every row that has
        // none of its own — the other half of VaultWorkspace.Migrate, done here because this is the
        // first point at which its value is known. Without it, removing the shared token would take
        // every row that never overrode it offline.
        secrets.TryGetValue(SharedTokenKey, out var shared);

        foreach (var (projectName, project) in workspace.Projects)
        {
            foreach (var (tierId, connection) in project.Connections)
            {
                if (secrets.TryGetValue(ConnectionTokenKey(projectName, tierId), out var token))
                {
                    connection.Token = token;
                }
                else if (migrated && secrets.TryGetValue(LegacyConnectionTokenKey(tierId), out var legacy))
                {
                    connection.Token = legacy;
                }
            }

            // A token set for a source that appsettings.json does not know about would otherwise be
            // invisible. Surface it as a connection so the tab can show it and let you complete it.
            foreach (var (key, value) in secrets)
            {
                var tierId = TierIdFromTokenKey(key, projectName);
                if (tierId is null || project.Connections.ContainsKey(tierId))
                {
                    continue;
                }

                project.Connections[tierId] = new VaultConnection { Token = value };
            }
        }

        // The same courtesy for the pre-projects keys, and the same reason it matters more here: a
        // token for a source that only ever existed in user secrets has no connection to be found
        // through, and SaveSecrets prunes the old keys once the migrated shape is written. Not
        // carrying it across now would delete it a moment later.
        if (migrated && workspace.Projects.TryGetValue(VaultWorkspace.MigratedProjectName, out var carried))
        {
            foreach (var (key, value) in secrets)
            {
                var tierId = LegacyTierIdFromTokenKey(key);
                if (tierId is null || carried.Connections.ContainsKey(tierId))
                {
                    continue;
                }

                carried.Connections[tierId] = new VaultConnection { Token = value };
            }
        }

        // Last, so a row's own token always wins over the shared one it is replacing.
        if (!string.IsNullOrWhiteSpace(shared))
        {
            foreach (var connection in workspace.Projects.Values.SelectMany(p => p.Connections.Values))
            {
                if (string.IsNullOrWhiteSpace(connection.Token))
                {
                    connection.Token = shared;
                }
            }
        }
    }

    /// <summary>
    /// The source id in a token key belonging to <paramref name="projectName"/>, or null if it is not
    /// one. Asks with the project's name already built into the prefix rather than going through
    /// <see cref="ParseTokenKey"/> and comparing: a project name is whatever was typed on the projects
    /// screen, so it is the caller — who has the name — that can say where it ends, not a parser
    /// hunting for the next <c>:Connections:</c>.
    /// </summary>
    private static string? TierIdFromTokenKey(string key, string projectName) =>
        AsTierId(TokenKeyBody(key, $"Vault:Projects:{projectName}:Connections:"));

    /// <summary>Whether <paramref name="key"/> is any project's connection token, and which project's.</summary>
    private static (string Project, string Tier)? ParseTokenKey(string key)
    {
        const string middle = ":Connections:";

        var body = TokenKeyBody(key, "Vault:Projects:");
        if (body is null)
        {
            return null;
        }

        var split = body.IndexOf(middle, StringComparison.OrdinalIgnoreCase);
        if (split <= 0)
        {
            return null;
        }

        var project = body[..split];
        var tier = AsTierId(body[(split + middle.Length)..]);

        if (tier is null || project.Contains(':'))
        {
            return null;
        }

        return (project, tier);
    }

    /// <summary>
    /// Reads secrets.json as the flat colon-delimited map `dotnet user-secrets` produces. Nested
    /// objects are flattened too, since hand-edits often nest them.
    /// </summary>
    public static Dictionary<string, string> ReadSecrets(List<string>? problems = null)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = SecretsFile;

        if (!File.Exists(path))
        {
            return result;
        }

        try
        {
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
            {
                return result;
            }

            var root = JsonNode.Parse(text, nodeOptions: null, OrdinalJsonWriter.DocumentOptions);

            if (root is JsonObject o)
            {
                Flatten(o, string.Empty, result);
            }
        }
        catch (Exception ex)
        {
            problems?.Add($"secrets.json: {ex.Message}");
        }

        return result;
    }

    private static void Flatten(JsonObject o, string prefix, Dictionary<string, string> into)
    {
        foreach (var (key, value) in o)
        {
            var path = prefix.Length == 0 ? key : $"{prefix}:{key}";

            if (value is JsonObject nested)
            {
                Flatten(nested, path, into);
            }
            else if (value is not null)
            {
                into[path] = value.ToString();
            }
        }
    }

    /// <summary>
    /// Writes one project's settings back, leaving every other project alone. The Sources tab edits a
    /// project rather than a file, so this reads the workspace, replaces that project's half of it and
    /// saves the whole thing — writing <paramref name="settings"/> straight out would delete every
    /// project that is not open.
    /// </summary>
    public static (string AppSettingsPath, string SecretsPath) Save(VaultSettings settings, string projectName)
    {
        var (workspace, _) = LoadWorkspace();
        workspace.Apply(projectName, settings);
        return SaveWorkspace(workspace);
    }

    /// <summary>
    /// Writes the non-secret half to appsettings.json and the tokens to secrets.json, preserving every
    /// unrelated key in both files. Returns the two paths written.
    /// </summary>
    public static (string AppSettingsPath, string SecretsPath) SaveWorkspace(VaultWorkspace workspace)
    {
        SaveAppSettings(workspace);
        SaveSecrets(workspace);
        return (AppPaths.AppSettingsFile, SecretsFile);
    }

    private static void SaveAppSettings(VaultWorkspace workspace)
    {
        var path = AppPaths.AppSettingsFile;

        JsonObject root;
        if (File.Exists(path))
        {
            root = JsonNode.Parse(File.ReadAllText(path), nodeOptions: null, OrdinalJsonWriter.DocumentOptions)
                   as JsonObject ?? [];
        }
        else
        {
            root = [];
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }

        // Serializing the model - whose token properties are [JsonIgnore] - is what keeps tokens out
        // of this file. Do not hand-build the node here.
        root[SectionName] = JsonSerializer.SerializeToNode(workspace, WriteOptions);

        File.WriteAllText(path, root.ToJsonString(WriteOptions));
    }

    private static void SaveSecrets(VaultWorkspace workspace)
    {
        var path = SecretsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var existing = ReadSecrets();

        // The shared token is gone as a concept: its value was pushed onto every row that had none on
        // load, so what is left under this key is a duplicate of live credentials. Removing it is the
        // last step of the migration, not a loss.
        existing.Remove(SharedTokenKey);

        foreach (var (projectName, project) in workspace.Projects)
        {
            foreach (var (tierId, connection) in project.Connections)
            {
                var key = ConnectionTokenKey(projectName, tierId);
                if (string.IsNullOrWhiteSpace(connection.Token))
                {
                    existing.Remove(key);
                }
                else
                {
                    existing[key] = connection.Token;
                }
            }
        }

        // A connection — or a whole project — removed in the UI must not leave its token behind. The
        // pre-projects keys go the same way: their value was carried into a project on migration, so
        // what is left under the old key is a duplicate of a live secret, which is the last thing to
        // leave lying around.
        foreach (var key in existing.Keys.ToArray())
        {
            if (ParseTokenKey(key) is { } owner)
            {
                if (!workspace.Projects.TryGetValue(owner.Project, out var project) ||
                    !project.Connections.ContainsKey(owner.Tier))
                {
                    existing.Remove(key);
                }
            }
            else if (LegacyTierIdFromTokenKey(key) is not null)
            {
                existing.Remove(key);
            }
        }

        var o = new JsonObject();
        foreach (var (key, value) in existing.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            o[key] = value;
        }

        File.WriteAllText(path, o.ToJsonString(WriteOptions));
    }
}
