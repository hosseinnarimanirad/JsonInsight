using System.Text.Json.Nodes;
using JsonInsight.Loading;

namespace JsonInsight.Model;

/// <summary>Where a loaded document's content came from.</summary>
public enum TierOrigin
{
    /// <summary>Read live from Vault.</summary>
    Vault,

    /// <summary>
    /// Read from a JSON file on disk. Two things use this: a configured source whose
    /// <see cref="TierDefinition.Kind"/> is <see cref="Sources.SourceKind.LocalFile"/> — writable
    /// exactly like a Vault source — and a file someone browsed for on the Compare files tab, which
    /// is always marked read-only on the way in. <see cref="TierDocument.Writable"/> is what
    /// distinguishes the two; <see cref="Origin"/> alone does not, and it is the only thing that
    /// still tells them apart now that a source carries no write flag of its own.
    /// </summary>
    LocalFile,
}

/// <summary>
/// A loaded tier: the mutable node tree used for editing, and the flattened leaves used for diffing.
///
/// <para>
/// A Vault tier is what Vault holds, and nothing else — no local copy behind it, no file it was read
/// from — so an edit to one becomes a new Vault version or it does not happen. A local-file tier is
/// the opposite: it <em>is</em> a file, and an edit to it becomes a new write to that file, guarded by
/// the same shape of fence a Vault push has (see <see cref="Sources.ISourceProvider"/>). Either way
/// there is exactly one answer for what a tier currently holds — never a cache that might be stale.
/// </para>
/// </summary>
public sealed class TierDocument
{
    public required TierDefinition Definition { get; init; }

    public required JsonNode Root { get; init; }

    public required FlatConfig Flat { get; init; }

    public TierOrigin Origin { get; init; } = TierOrigin.Vault;

    /// <summary>The Vault KV version this content was read at. Null for a local file.</summary>
    public int? VaultVersion { get; init; }

    public DateTimeOffset? VaultCreatedTime { get; init; }

    /// <summary>The Vault address it came from, kept so the grid can say which server was read.</summary>
    public string? VaultAddress { get; init; }

    /// <summary>The secret it was read from — the tier's own path, not the connection's.</summary>
    public string? VaultSecretPath { get; init; }

    /// <summary>Set only for a local-file tier or a browsed file. A Vault tier never has one.</summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// The file's last-write time when this was read, for a local-file tier. This is the baseline a
    /// save compares the file's current mtime and content against to notice it changed underneath the
    /// app — the same role <see cref="VaultVersion"/> plays for a Vault tier's check-and-set.
    /// </summary>
    public DateTimeOffset? FileModifiedUtc { get; init; }

    public string Id => Definition.Id;

    public string Label => Definition.Label;

    public bool Writable => Definition.Writable;

    public IReadOnlyList<string> Warnings => Flat.Warnings;

    public bool IsFromVault => Origin == TierOrigin.Vault;

    /// <summary>What to call this on screen: the version for a Vault tier, the file name for a local one.</summary>
    public string DisplayName => Origin == TierOrigin.Vault
        ? $"v{VaultVersion:00}"
        : System.IO.Path.GetFileName(FilePath ?? "(no file)");

    /// <summary>One line naming the source, for the column header under the tier name.</summary>
    public string SourceLine => Origin == TierOrigin.Vault
        ? $"Vault v{VaultVersion:00}" +
          (VaultCreatedTime is { } created ? $"  {created.LocalDateTime:yyyy-MM-dd HH:mm}" : string.Empty)
        : "Local file" +
          (FileModifiedUtc is { } modified ? $"  {modified.LocalDateTime:yyyy-MM-dd HH:mm}" : string.Empty);

    /// <summary>The longer form, for a tooltip.</summary>
    public string SourceDetail => Origin == TierOrigin.Vault
        ? $"Read live from {VaultAddress}{(VaultSecretPath is null ? string.Empty : " · " + VaultSecretPath)} " +
          $"at version {VaultVersion}.\r\n\r\nNothing is kept on disk: this is what Vault holds, and an edit " +
          "to it becomes a new Vault version."
        : Writable
            ? $"Read from {FilePath}.\r\n\r\nAn edit to it becomes a new write to that file — backed up first, " +
              "and refused unless it can reproduce the file byte-for-byte."
            : $"Read from {FilePath}. A browsed file is never written.";
}
