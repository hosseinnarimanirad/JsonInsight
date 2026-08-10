using System.IO;
using System.Text.Json.Nodes;
using JsonInsight.Loading;
using JsonInsight.Model;
using JsonInsight.Promote;
using JsonInsight.Vault;

namespace JsonInsight.Sources;

/// <summary>
/// <see cref="ISourceProvider"/> for <see cref="SourceKind.LocalFile"/>. Ports <c>VaultPusher</c>'s
/// fence sequence to disk rather than reimplementing it from scratch — the same five steps, one
/// substituted at a time:
///
/// <list type="number">
/// <item>Refuse if the tier is marked read-only — <see cref="Blocked"/>, same as <c>VaultPusher.Blocked</c>.</item>
/// <item>Re-parse and re-flatten the payload before it leaves — <see cref="PayloadValidator"/>, the
/// exact code <c>VaultPusher</c> uses, not a copy of it.</item>
/// <item>Compare against what the source holds <em>right now</em>, not what was last read — a live
/// Vault read there, a fresh read of the file's bytes here.</item>
/// <item>A concurrency guard — Vault's is a KV check-and-set version; a file's is a backup taken
/// immediately before the overwrite, plus a temp-file-then-rename so a crash mid-write cannot leave a
/// half-written file behind.</item>
/// <item>Read back what was written and byte-compare it, the same "the write returned rather than
/// verified" distinction <c>VaultPusher</c> draws.</item>
/// </list>
/// </summary>
public sealed class LocalFileSourceProvider : ISourceProvider
{
    private readonly Flattener _flattener;

    public LocalFileSourceProvider(Flattener flattener)
    {
        _flattener = flattener;
    }

    public SourceKind Kind => SourceKind.LocalFile;

    public Task<SourceLoadResult> LoadAsync(
        TierDefinition definition,
        VaultSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(definition.LocalFilePath))
        {
            return Task.FromResult(new SourceLoadResult(definition.Id, null, "no localFilePath in tiers.json")
            {
                NotConfigured = true,
            });
        }

        var path = definition.LocalFilePath;

        if (!File.Exists(path))
        {
            return Task.FromResult(new SourceLoadResult(definition.Id, null, $"{path} does not exist")
            {
                NotConfigured = true,
            });
        }

        try
        {
            var document = Build(definition, path);
            return Task.FromResult(new SourceLoadResult(definition.Id, document, null)
            {
                Detail = document.SourceLine,
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new SourceLoadResult(definition.Id, null, ex.Message));
        }
    }

    /// <summary>Builds the in-memory tier from a file already known to exist.</summary>
    public TierDocument Build(TierDefinition definition, string path)
    {
        var text = OrdinalJsonWriter.DecodeUtf8(File.ReadAllBytes(path));
        var root = OrdinalJsonWriter.Parse(text);
        var fullPath = Path.GetFullPath(path);

        return new TierDocument
        {
            Definition = definition,
            Root = root,
            Flat = _flattener.Flatten(definition.Id, root),
            Origin = TierOrigin.LocalFile,
            FilePath = fullPath,
            FileModifiedUtc = File.GetLastWriteTimeUtc(fullPath),
        };
    }

    public string? Blocked(TierDocument? document, VaultSettings settings)
    {
        if (document is null)
        {
            return "No tier selected.";
        }

        // As in VaultPusher: the only document that arrives read-only is one browsed on the Compare
        // files tab, which was never a source and must not become one by being saved.
        if (!document.Writable)
        {
            return $"{document.Id} is read-only, so this app never writes it.";
        }

        if (string.IsNullOrWhiteSpace(document.FilePath))
        {
            return $"{document.Id} names no file, so there is nowhere to save it.";
        }

        return null;
    }

    public Task<PushPreflight> PreflightSaveAsync(
        TierDocument document,
        JsonNode updated,
        string what,
        VaultSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (Blocked(document, settings) is { } blocked)
        {
            return Task.FromResult(PushPreflight.Failed(blocked));
        }

        var (payload, payloadProblem) = PayloadValidator.Validate(_flattener, document.Id, updated);
        if (payload is null)
        {
            return Task.FromResult(PushPreflight.Failed(payloadProblem!));
        }

        var path = document.FilePath!;

        if (!File.Exists(path))
        {
            return Task.FromResult(PushPreflight.Failed(
                $"{path} no longer exists — it may have been moved or deleted since this tier was loaded. " +
                "Nothing was written."));
        }

        string liveText;
        DateTimeOffset liveModified;
        try
        {
            liveText = OrdinalJsonWriter.SerializeToText(
                OrdinalJsonWriter.Parse(OrdinalJsonWriter.DecodeUtf8(File.ReadAllBytes(path))));
            liveModified = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex)
        {
            return Task.FromResult(PushPreflight.Failed(
                $"Could not read {path} to compare against: {ex.Message} Nothing was written."));
        }

        // The baseline is the tier as it was loaded — Root is never mutated by editing, which
        // happens on a separate tree — compared against the file's live content now, the same role
        // VaultPusher's "read live before pushing" comparison plays.
        var baseline = OrdinalJsonWriter.SerializeToText(document.Root);
        var changedUnderneath = !string.Equals(baseline, liveText, StringComparison.Ordinal);

        // A file that changed underneath is not a warning any more — PushPlan.Stale refuses the write
        // and says what to do instead, exactly as it does for a Vault secret that moved.
        var warnings = new List<string>();

        return Task.FromResult(new PushPreflight(
            new PushPlan(
                document.Id,
                "local disk",
                path,
                changedUnderneath ? 1 : 0,
                liveModified,
                liveText,
                payload,
                0,
                what,
                warnings)
            {
                Kind = SourceKind.LocalFile,
            },
            null));
    }

    public async Task<PushResult> SaveAsync(
        TierDocument document,
        PushPlan plan,
        VaultSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (Blocked(document, settings) is { } blocked)
        {
            return new PushResult(false, null, blocked, []);
        }

        if (plan.Identical)
        {
            return new PushResult(false, null,
                $"{Path.GetFileName(plan.SecretPath)} already holds exactly this. Nothing was written — a " +
                "write that changes nothing is noise in the backup history.", []);
        }

        // The fence VaultPusher grew for a secret that moved, kept in step here: a backup taken
        // immediately before the overwrite makes the change recoverable, not consented to.
        if (plan.Stale is { } stale)
        {
            return new PushResult(false, null, stale, []);
        }

        var path = plan.SecretPath;
        var notes = new List<string>();

        string backupPath;
        try
        {
            backupPath = Backup(path);
            notes.Add($"Backed up the previous content to {Path.GetFileName(backupPath)}.");
        }
        catch (Exception ex)
        {
            return new PushResult(false, null, $"Could not back up {path} before writing: {ex.Message}", notes);
        }

        try
        {
            AtomicWrite(path, plan.PayloadText);
        }
        catch (Exception ex)
        {
            return new PushResult(false, null,
                $"Write failed: {ex.Message} The previous content is still at {backupPath}.", notes);
        }

        // Everything below this line runs after the file has already changed, so none of it may turn
        // the write into a failure — the same rule VaultPusher's read-back follows.
        try
        {
            var readBack = OrdinalJsonWriter.DecodeUtf8(File.ReadAllBytes(path));
            var canonical = OrdinalJsonWriter.SerializeToText(OrdinalJsonWriter.Parse(readBack));

            notes.Add(string.Equals(canonical, plan.PayloadText, StringComparison.Ordinal)
                ? "Read back and verified — the file on disk matches what was sent, byte for byte."
                : "WARNING: the file was read back and does not match what was sent. Reload and compare " +
                  "before trusting it.");
        }
        catch (Exception ex)
        {
            notes.Add($"Written, but it could not be read back to verify: {ex.Message}");
        }

        return new PushResult(true, null, null, notes);
    }

    /// <summary>
    /// Copies the file as it stands into a sibling with a UTC timestamp in its name, refusing to
    /// overwrite one that already exists — a second save in the same second gets a second suffix
    /// rather than clobbering the first backup.
    /// </summary>
    private static string Backup(string path)
    {
        var stamp = File.GetLastWriteTimeUtc(path).ToString("yyyyMMddTHHmmssZ");
        var candidate = $"{path}.bak-{stamp}";
        var suffix = 1;

        while (File.Exists(candidate))
        {
            candidate = $"{path}.bak-{stamp}-{suffix++}";
        }

        File.Copy(path, candidate);
        return candidate;
    }

    /// <summary>
    /// Writes through a temp file in the same directory and then renames it over the target, so a
    /// process killed mid-write leaves either the old file or the new one intact — never a partial one.
    /// </summary>
    private static void AtomicWrite(string path, string text)
    {
        var directory = Path.GetDirectoryName(path)!;
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.tmp-{Guid.NewGuid():N}");

        File.WriteAllText(temp, text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }
}
