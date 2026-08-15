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

    /// <summary>
    /// The tree exactly as the source handed it over, and never anything else.
    ///
    /// <para>
    /// This is a baseline, not the working copy: a local-file save compares it byte for byte against
    /// what is on disk right now to decide whether the file moved underneath the app, and a Vault
    /// push pairs it with <see cref="VaultVersion"/> for the same reason. Publishing an in-memory
    /// edit into it would make every save report that the source had changed under it. What the app
    /// is currently holding — edits included — is <see cref="Live"/>.
    /// </para>
    /// </summary>
    public required JsonNode Root { get; init; }

    /// <summary>
    /// The edited tree, when this tier has unsaved changes; null while it still says exactly what was
    /// read. Read through <see cref="Live"/> rather than directly.
    /// </summary>
    public JsonNode? EditedRoot { get; init; }

    /// <summary>
    /// What this tier currently holds: the edits if there are any, otherwise what was read. This is
    /// the tree to display, to compare and to push — <see cref="Root"/> is only the baseline a save
    /// checks the source against.
    /// </summary>
    public JsonNode Live => EditedRoot ?? Root;

    /// <summary>True when there are in-memory changes that no source has been told about yet.</summary>
    public bool IsEdited => EditedRoot is not null;

    /// <summary>Always a flatten of <see cref="Live"/>, so every value on screen is the edited one.</summary>
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

    /// <summary>
    /// The same tier holding an edited tree, with every source-side baseline carried across
    /// untouched — the Vault version and the file mtime describe the read this document came from,
    /// and an in-memory edit does not change what the source was holding when it was read.
    /// </summary>
    public TierDocument WithEdits(JsonNode edited, FlatConfig flat) => new()
    {
        Definition = Definition,
        Root = Root,
        EditedRoot = edited,
        Flat = flat,
        Origin = Origin,
        VaultVersion = VaultVersion,
        VaultCreatedTime = VaultCreatedTime,
        VaultAddress = VaultAddress,
        VaultSecretPath = VaultSecretPath,
        FilePath = FilePath,
        FileModifiedUtc = FileModifiedUtc,
    };

    public string Id => Definition.Id;

    public string Label => Definition.Label;

    public bool Writable => Definition.Writable;

    public IReadOnlyList<string> Warnings => Flat.Warnings;

    public bool IsFromVault => Origin == TierOrigin.Vault;

    /// <summary>
    /// The shortest true thing about where this came from, for somewhere with one line to spare — a
    /// grid column header, a chip beside a tier picker.
    ///
    /// <para>
    /// The version is the point of it. Which KV version a column is showing is the difference between
    /// "stage does not have this key" and "stage did not have it an hour ago", and a header that said
    /// only "Vault" left that unanswerable without opening the push screen. A file has no version, so
    /// it says so rather than printing a number it would have to invent.
    /// </para>
    /// </summary>
    public string SourceBadge => Origin == TierOrigin.Vault
        ? VaultVersion is { } version ? $"Vault v{version:00}" : "Vault"
        : "file";

    /// <summary>
    /// How a tier picker names this: <c>dev v39</c>, <c>prod v11</c>, or <c>dev (file)</c>.
    ///
    /// <para>
    /// The version belongs in the list rather than beside it. A picker is read while choosing which
    /// tier to work on, and which version of it you are about to edit is part of that choice — an
    /// edit is built against a version, and a push is refused when somebody has replaced it since.
    /// </para>
    /// </summary>
    public string PickerLabel => Origin == TierOrigin.Vault
        ? VaultVersion is { } version ? $"{Label} v{version:00}" : Label
        : $"{Label} (file)";

    /// <summary>One line naming the source, for the column header under the tier name.</summary>
    public string SourceLine => Origin == TierOrigin.Vault
        ? $"Vault v{VaultVersion:00}" +
          (VaultCreatedTime is { } created ? $"  {created.LocalDateTime:yyyy-MM-dd HH:mm}" : string.Empty)
        : "Local file" +
          (FileModifiedUtc is { } modified ? $"  {modified.LocalDateTime:yyyy-MM-dd HH:mm}" : string.Empty);

    /// <summary>
    /// When what this tier holds was written: the Vault version's creation time, or the file's last
    /// write time. Not when the app read it — that is a different question, and the freshness banner
    /// is what answers it.
    /// </summary>
    public DateTimeOffset? SourceTime => Origin == TierOrigin.Vault ? VaultCreatedTime : FileModifiedUtc;

    /// <summary>
    /// The age of <see cref="SourceTime"/> in words — "11 h 23 min ago". Empty when the source
    /// carries no timestamp at all, so a caller can append it unconditionally.
    ///
    /// <para>
    /// Shown beside the absolute timestamp rather than instead of it. The timestamp is the record and
    /// the age is what gets read: "2026-08-10 17:52" does not say whether this version landed before
    /// or after the change somebody is asking you about.
    /// </para>
    /// </summary>
    public string SourceAge => Elapsed.Since(SourceTime, DateTimeOffset.Now);

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
