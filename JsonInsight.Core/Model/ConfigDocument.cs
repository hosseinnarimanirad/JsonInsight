namespace JsonInsight.Model;

/// <summary>
/// Which JSON document the whole app is looking at, as a path relative to each environment's Vault
/// root.
///
/// <para>
/// Vault holds more than one document per environment. The appsettings root is at
/// <c>kv/app/{env}</c>, and there are others beneath it —
/// <c>kv/app/{env}/resources/config/features.json</c>. They are the same shape of
/// problem: four environments that drift, with no fallback between them. So the document is a
/// selection rather than a second app: pick one in the Sources tab and every tab compares that one
/// across dev, stage, beta and prod.
/// </para>
///
/// <para>
/// The empty path is the root document, and it is deliberately not a special case anywhere except
/// here: it is the only document tiers.json names local files for, and the only one dev reads from a
/// local snapshot rather than from Vault.
/// </para>
/// </summary>
public sealed record ConfigDocument(string RelativePath)
{
    /// <summary>The appsettings document — the environment secret itself, with nothing appended.</summary>
    public static readonly ConfigDocument Root = new(string.Empty);

    public bool IsRoot => RelativePath.Length == 0;

    public string Label => IsRoot ? "appsettings (root)" : RelativePath;

    /// <summary>
    /// The secret path for one environment: the root document is the environment secret itself, any
    /// other is a path beneath it.
    /// </summary>
    public string PathUnder(string environmentRoot)
    {
        var trimmed = environmentRoot.Trim().TrimEnd('/');
        return IsRoot ? trimmed : $"{trimmed}/{RelativePath}";
    }

    /// <summary>
    /// Normalizes anything typed or read from settings. Leading and trailing slashes go, so
    /// <c>/resources/config/features.json</c> and <c>resources/config/features.json</c> are one
    /// document rather than two identical-looking ones.
    /// </summary>
    public static ConfigDocument Parse(string? raw)
    {
        var path = (raw ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        return path.Length == 0 ? Root : new ConfigDocument(path);
    }

    public bool Equals(ConfigDocument? other) =>
        other is not null && string.Equals(RelativePath, other.RelativePath, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(RelativePath);

    public override string ToString() => Label;
}
