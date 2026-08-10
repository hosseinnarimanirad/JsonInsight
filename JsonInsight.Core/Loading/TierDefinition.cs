using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using JsonInsight.Model;
using JsonInsight.Sources;

namespace JsonInsight.Loading;

/// <summary>
/// One configured tier: a name and a Vault secret. Nothing about tiers is hardcoded, so adding an
/// environment is a config edit.
///
/// <para>
/// There is deliberately no file here any more. A tier used to name a local snapshot as well as a
/// secret, which meant every question about it had two possible answers and the app had to say which
/// one it was showing. A tier is now the secret.
/// </para>
/// </summary>
public sealed class TierDefinition
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    /// <summary>
    /// False stops this app writing the tier at all — no promote into it, no edit of it, no push.
    ///
    /// <para>
    /// True for every configured source, and not settable from anywhere: a source exists to be read
    /// and written, and a per-row "no writes" tick was a fence in the wrong place — it sat next to
    /// the row rather than next to the write, so the only thing it reliably did was refuse a push
    /// somebody had already decided to make and leave them to find out why.
    /// </para>
    ///
    /// <para>
    /// The one producer of <c>false</c> is <see cref="TierLoader"/>, for a file browsed on the
    /// Compare files tab. That is not a configured source at all — it is an arbitrary JSON someone
    /// pointed at to read — and nothing downstream may mistake it for something this app may write.
    /// </para>
    /// </summary>
    public bool Writable { get; init; } = true;

    /// <summary>
    /// The secret this tier is, e.g. <c>kv/app/stage</c>. A tier without one cannot be
    /// read, which is now the same thing as saying it cannot exist.
    ///
    /// <para>
    /// Meaningful only when <see cref="Kind"/> is <see cref="SourceKind.Vault"/>.
    /// </para>
    /// </summary>
    public string? VaultPath { get; init; }

    /// <summary>
    /// Which kind of source this tier is. Defaults to <see cref="SourceKind.Vault"/>, which is every
    /// tier <c>tiers.json</c> has ever described — a <see cref="SourceKind.LocalFile"/> tier is new,
    /// and names its file in <see cref="LocalFilePath"/> instead of <see cref="VaultPath"/>.
    /// </summary>
    public SourceKind Kind { get; init; } = SourceKind.Vault;

    /// <summary>
    /// The file this tier reads and writes, for a <see cref="SourceKind.LocalFile"/> tier. Meaningful
    /// only when <see cref="Kind"/> is <see cref="SourceKind.LocalFile"/>.
    /// </summary>
    public string? LocalFilePath { get; init; }

    public string? Note { get; init; }

    /// <summary>
    /// Which document this tier holds. Never read from tiers.json — that file describes the root
    /// document only, and every other one is derived from it by <see cref="DocumentTiers"/>.
    /// </summary>
    [JsonIgnore]
    public ConfigDocument Document { get; init; } = ConfigDocument.Root;
}

public sealed class TiersConfig
{
    public required List<TierDefinition> Tiers { get; init; }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static TiersConfig Load(string? file = null)
    {
        file ??= AppPaths.ConfigFile("tiers.json");
        var json = File.ReadAllText(file);
        return JsonSerializer.Deserialize<TiersConfig>(json, SerializerOptions)
               ?? throw new InvalidDataException($"{file} did not deserialize to a tier list.");
    }

    public TierDefinition this[string id] =>
        Tiers.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"No tier named '{id}' in tiers.json.");
}
