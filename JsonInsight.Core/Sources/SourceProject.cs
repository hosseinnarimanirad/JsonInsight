using JsonInsight.Vault;

namespace JsonInsight.Sources;

/// <summary>
/// One named piece of work: a set of sources, and the document they are compared on.
///
/// <para>
/// A project is what makes this app usable against more than one thing at a time. The appsettings
/// root, <c>resources/config/config.json</c> and <c>resources/config/ui.json</c> are three different
/// comparisons — different documents, and often different environments worth comparing them across —
/// and before projects existed, moving between them meant retyping the whole Sources tab. Now each is
/// a project, kept side by side and switched between.
/// </para>
///
/// <para>
/// There is no document here, and that is the simplification the Sources tab is built on. A project
/// used to hold one relative path that was appended to every source's root, so what a row read was
/// two fields and a rule. Each <see cref="VaultConnection.SecretPath"/> is now the whole path to the
/// JSON it reads, and a project is simply the set of them.
/// </para>
/// </summary>
public sealed class SourceProject
{
    /// <summary>Which sources are its columns; see <see cref="SourceCatalog.MaxActive"/>.</summary>
    public List<string> ActiveSources { get; set; } = [];

    /// <summary>Keyed by environment id — see <see cref="SourceEnvironment"/> for the fixed list.</summary>
    public Dictionary<string, VaultConnection> Connections { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When this project was last opened, which is the order the recents list is in. Null for one that
    /// has been created but never opened — it sorts last rather than first, since a project nobody has
    /// opened is not recent.
    /// </summary>
    public DateTimeOffset? LastOpenedUtc { get; set; }

    /// <summary>
    /// What this project compares, said in as few words as it can be: the file name every active
    /// source ends in when they agree, which is the normal case and the one worth naming. When they do
    /// not agree that is worth saying too — a project whose stage row reads ui.json and whose beta row
    /// reads error.json is either deliberate or a mistake, and either way it should not look tidy.
    /// </summary>
    public string Describe()
    {
        var names = (ActiveSources.Count > 0
                ? ActiveSources.Select(id => Connections.GetValueOrDefault(id))
                : Connections.Values)
            .Where(c => c is not null)
            .Select(c => LastSegment(c!))
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return names.Length switch
        {
            0 => "nothing chosen yet",
            1 => names[0],
            _ => $"{string.Join(", ", names.Take(3))}{(names.Length > 3 ? ", …" : string.Empty)} — mixed",
        };
    }

    private static string LastSegment(VaultConnection connection)
    {
        var path = connection.Kind == SourceKind.LocalFile ? connection.LocalFilePath : connection.SecretPath;

        return path.TrimEnd('/', '\\').Split('/', '\\').LastOrDefault() ?? string.Empty;
    }

    public SourceProject Clone() => new()
    {
        ActiveSources = [.. ActiveSources],
        Connections = Connections.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.OrdinalIgnoreCase),
        LastOpenedUtc = LastOpenedUtc,
    };
}
