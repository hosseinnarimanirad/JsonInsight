using JsonInsight.Editing;
using JsonInsight.Loading;
using JsonInsight.Model;
using JsonInsight.Promote;
using JsonInsight.Sources;
using JsonInsight.Vault;

namespace JsonInsight.Tests;

/// <summary>
/// The local-file write path, exercised without touching a real config file: every test works
/// against its own scratch copy in the temp folder, cleaned up whether the test passes or not. These
/// pin the same shape of fence <c>PushTests</c> pins for Vault — refuse a bad payload, notice the
/// source changed underneath the app, back up before writing, write atomically, verify by reading
/// back — substituting a file read/write for a Vault read/check-and-set at each step.
/// </summary>
[Collection("sample-files")]
public sealed class LocalFileProviderTests(SampleFiles files) : IDisposable
{
    private const string EditPath = "ConnectionStrings:Couchbase:Modules:Auth:Url";

    private readonly List<string> _scratchFiles = [];

    private static PendingEdit Widen(TierDocument tier, string value)
    {
        var leaf = tier.Flat.Find(EditPath)!;
        return new PendingEdit
        {
            TierId = tier.Id,
            Path = EditPath,
            Kind = EditKind.Update,
            BaseValue = leaf.ComparableValue,
            NewValue = value,
            NewKind = leaf.Kind,
            Class = leaf.Class,
        };
    }

    private string NewScratchFile(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"jsoninsight-local-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        _scratchFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _scratchFiles)
        {
            File.Delete(path);

            // Backups are named off the target path, so they are siblings of it rather than of the
            // Guid-named scratch file itself — sweep anything this test session left behind.
            foreach (var backup in Directory.GetFiles(Path.GetDirectoryName(path)!, $"{Path.GetFileName(path)}.bak-*"))
            {
                File.Delete(backup);
            }
        }
    }

    private TierDefinition Definition(string path, bool writable = true) => new()
    {
        Id = "local",
        Label = "local",
        Kind = SourceKind.LocalFile,
        Writable = writable,
        LocalFilePath = path,
    };

    [Fact]
    public async Task Loading_a_file_produces_a_tier_with_the_same_shape_a_vault_read_would()
    {
        var path = NewScratchFile(SampleFiles.Canonical(files.Beta));
        var provider = new LocalFileSourceProvider(files.Flattener);

        var result = await provider.LoadAsync(Definition(path), SampleFiles.Settings());

        Assert.True(result.Succeeded);
        Assert.Equal(TierOrigin.LocalFile, result.Document!.Origin);
        Assert.Equal(files.Beta.Flat.Count, result.Document.Flat.Count);
        Assert.Equal(SampleFiles.Canonical(files.Beta), SampleFiles.Canonical(result.Document));
    }

    [Fact]
    public async Task A_missing_file_is_reported_as_not_configured_rather_than_throwing()
    {
        var provider = new LocalFileSourceProvider(files.Flattener);
        var missing = Path.Combine(Path.GetTempPath(), $"jsoninsight-does-not-exist-{Guid.NewGuid():N}.json");

        var result = await provider.LoadAsync(Definition(missing), SampleFiles.Settings());

        Assert.False(result.Succeeded);
        Assert.True(result.NotConfigured);
    }

    [Fact]
    public async Task A_read_only_tier_is_blocked_before_anything_is_touched()
    {
        var path = NewScratchFile(SampleFiles.Canonical(files.Beta));
        var provider = new LocalFileSourceProvider(files.Flattener);
        var loaded = (await provider.LoadAsync(Definition(path, writable: false), SampleFiles.Settings())).Document!;

        var preflight = await provider.PreflightSaveAsync(loaded, loaded.Root, "test edit", SampleFiles.Settings());

        Assert.False(preflight.Ok);
        Assert.Contains("read-only", preflight.Problem, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SampleFiles.Canonical(files.Beta), File.ReadAllText(path).Trim());
    }

    [Fact]
    public async Task Saving_writes_the_file_backs_up_the_previous_content_and_verifies_by_reading_back()
    {
        var path = NewScratchFile(SampleFiles.Canonical(files.Beta));
        var provider = new LocalFileSourceProvider(files.Flattener);
        var settings = SampleFiles.Settings();

        var loaded = (await provider.LoadAsync(Definition(path), settings)).Document!;
        const string widened = "couchbase://10.0.0.1,10.0.0.2,10.0.0.3,10.0.0.4";
        var edited = EditApplier.Apply(loaded, [Widen(loaded, widened)]);

        var preflight = await provider.PreflightSaveAsync(loaded, edited, "test edit", settings);
        Assert.True(preflight.Ok);

        var result = await provider.SaveAsync(loaded, preflight.Plan!, settings);

        Assert.True(result.Succeeded);
        Assert.Contains(result.Notes, n => n.Contains("Backed up", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Contains("verified", StringComparison.Ordinal));

        var backups = Directory.GetFiles(Path.GetDirectoryName(path)!, $"{Path.GetFileName(path)}.bak-*");
        var backup = Assert.Single(backups);
        Assert.Equal(SampleFiles.Canonical(files.Beta), File.ReadAllText(backup).Trim());

        var reloaded = await provider.LoadAsync(Definition(path), settings);
        Assert.Equal(widened, reloaded.Document!.Flat.Find(EditPath)!.Value);
    }

    [Fact]
    public async Task A_file_changed_on_disk_since_loading_is_flagged_rather_than_silently_overwritten()
    {
        var path = NewScratchFile(SampleFiles.Canonical(files.Beta));
        var provider = new LocalFileSourceProvider(files.Flattener);
        var settings = SampleFiles.Settings();

        var loaded = (await provider.LoadAsync(Definition(path), settings)).Document!;

        // Something else — another tool, a manual edit — touches the file after this tier was loaded.
        File.WriteAllText(path, SampleFiles.Canonical(files.Prod));

        var preflight = await provider.PreflightSaveAsync(loaded, loaded.Root, "test edit", settings);

        Assert.True(preflight.Ok);
        Assert.False(preflight.Plan!.BaseMatchesLive);
        Assert.Contains(preflight.Plan.Warnings, w => w.Contains("changed on disk", StringComparison.Ordinal));

        // What the diff is built from is what's actually on disk now, not the stale in-memory copy.
        Assert.Equal(SampleFiles.Canonical(files.Prod), preflight.Plan.LiveText);
    }

    /// <summary>
    /// The payload check is the exact code <c>VaultPusher.Payload</c> runs — <see cref="PayloadValidator"/>
    /// — so its refusal behavior is already pinned by the existing Vault-side tests. What is new here
    /// is the fence around it: a bad payload must be caught before this provider touches the file at
    /// all, which a file that vanishes between load and save is a real, constructible way to prove
    /// without needing to fabricate a payload that fails re-parsing.
    /// </summary>
    [Fact]
    public async Task A_file_removed_after_loading_is_refused_rather_than_recreated()
    {
        var path = NewScratchFile(SampleFiles.Canonical(files.Beta));
        var provider = new LocalFileSourceProvider(files.Flattener);
        var settings = SampleFiles.Settings();

        var loaded = (await provider.LoadAsync(Definition(path), settings)).Document!;
        File.Delete(path);

        var preflight = await provider.PreflightSaveAsync(loaded, loaded.Root, "test edit", settings);

        Assert.False(preflight.Ok);
        Assert.Contains("no longer exists", preflight.Problem, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(path));
    }
}
