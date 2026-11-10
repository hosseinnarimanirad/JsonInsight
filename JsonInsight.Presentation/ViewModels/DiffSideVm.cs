using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using JsonInsight.History;
using JsonInsight.Loading;
using JsonInsight.Model;
using JsonInsight.Promote;
using JsonInsight.Vault;

namespace JsonInsight.ViewModels;

/// <summary>
/// Reads a tier's version history for a view. An interface so the view models can be tested without
/// a Vault; <see cref="SettingsHistoryReader"/> is the real one.
/// </summary>
public interface IHistoryReader
{
    /// <summary>Why history cannot be read for this tier, or null when it can.</summary>
    string? Unavailable(TierDocument tier);

    Task<VaultSecretMetadata> ListAsync(TierDocument tier, CancellationToken cancellationToken);

    Task<SecretVersion> ReadAsync(TierDocument tier, int version, CancellationToken cancellationToken);
}

/// <summary>
/// The real <see cref="IHistoryReader"/>: the connection settings are loaded at the moment of each
/// query, the same way a pull loads them, so a token corrected on the Sources tab is the one used.
/// </summary>
public sealed class SettingsHistoryReader(Flattener flattener) : IHistoryReader
{
    private readonly SecretHistory _history = new(flattener);

    public string? Unavailable(TierDocument tier) => SecretHistory.Unavailable(tier, Settings());

    public Task<VaultSecretMetadata> ListAsync(TierDocument tier, CancellationToken cancellationToken) =>
        _history.ListAsync(tier, Settings(), cancellationToken);

    public Task<SecretVersion> ReadAsync(TierDocument tier, int version, CancellationToken cancellationToken) =>
        _history.ReadAsync(tier, Settings(), version, cancellationToken);

    private static VaultSettings Settings() => VaultSettingsStore.Load().Settings;
}

/// <summary>One entry in a side's version picker.</summary>
public sealed record VersionChoice(string Label, int? Version, string? Unreadable = null, bool IsLoadRequest = false)
{
    /// <summary>What is on screen: the source as loaded, plus any edits not yet pushed.</summary>
    public bool IsCurrent => Version is null && !IsLoadRequest;

    public bool IsEnabled => Unreadable is null;

    public override string ToString() => Label;

    public static VersionChoice Current(TierDocument tier) => new(
        tier.IsEdited
            ? "On screen (with unpushed edits)"
            : tier.IsFromVault && tier.VaultVersion is { } v ? $"Current (v{v})" : "Current",
        null);

    public static readonly VersionChoice LoadEarlier = new("Earlier versions…", null, IsLoadRequest: true);
}

/// <summary>
/// One side of the Text diff: which source, and which version of it.
///
/// <para>
/// Earlier versions are read only when asked — picking <see cref="VersionChoice.LoadEarlier"/>
/// reads the version list, and picking a version reads that version. Nothing is cached: the one old
/// version held here is the one on screen, it is dropped the moment another is picked, and the whole
/// list goes when the source changes or the tab is rebuilt.
/// </para>
/// </summary>
public sealed partial class DiffSideVm : ObservableObject
{
    private readonly IHistoryReader? _reader;
    private readonly Action _changed;
    private CancellationTokenSource? _work;
    private bool _reverting;

    [ObservableProperty]
    private TierDocument? _source;

    [ObservableProperty]
    private VersionChoice? _selected;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>One line under the picker: progress, a read that failed, or a gap in Vault's history.</summary>
    [ObservableProperty]
    private string? _status;

    /// <summary>The old version on screen, or null while the side shows its current document.</summary>
    [ObservableProperty]
    private SecretVersion? _old;

    public DiffSideVm(IHistoryReader? reader, Action changed)
    {
        _reader = reader;
        _changed = changed;
    }

    public ObservableCollection<VersionChoice> Versions { get; } = [];

    /// <summary>The last read this side started, so a test (or a caller) can wait for it.</summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    /// <summary>"stage" or "stage v31" — what the summary line calls this side.</summary>
    public string Name => Source is null ? "?" : Old is { } old ? $"{Source.Id} v{old.Version}" : Source.Id;

    /// <summary>The column heading: "AS STAGE", or "AS STAGE V31" for an old version.</summary>
    public string Header => "AS " + (Source is null ? "?" : Name.ToUpperInvariant());

    /// <summary>
    /// The canonical text this side contributes to the diff, or null while it has nothing to show —
    /// an old version still being read, or one that failed.
    /// </summary>
    public string? Text => Source is null
        ? null
        : Selected is { IsCurrent: false }
            ? Old is { } old ? OrdinalJsonWriter.SerializeToText(old.Root) : null
            // Live rather than Root: this tab answers "what does this say", and an edit made on the
            // Tier editor is part of what it says.
            : OrdinalJsonWriter.SerializeToText(Source.Live);

    /// <summary>Points the side at a source, back on its current document, with no history read.</summary>
    public void Reset(TierDocument? source)
    {
        Cancel();
        Old = null;
        Status = null;
        Source = source;

        Versions.Clear();
        if (source is not null)
        {
            Versions.Add(VersionChoice.Current(source));
            if (_reader is not null && source.IsFromVault)
            {
                Versions.Add(VersionChoice.LoadEarlier);
            }
        }

        SetSelected(Versions.FirstOrDefault());
        Notify();
    }

    partial void OnSelectedChanged(VersionChoice? value)
    {
        if (_reverting || value is null)
        {
            return;
        }

        if (value.IsLoadRequest)
        {
            // The request is an action, not a place to stay: the picker goes back to where it was and
            // the list fills in underneath it.
            SetSelected(Versions.FirstOrDefault(v => v.IsCurrent));
            Pending = LoadVersionsAsync();
            return;
        }

        if (!value.IsEnabled)
        {
            SetSelected(Versions.FirstOrDefault(v => v.IsCurrent));
            Status = $"v{value.Version} cannot be read: {value.Unreadable}.";
            return;
        }

        Cancel();
        Old = null;

        if (value.Version is { } version)
        {
            Pending = ReadVersionAsync(version);
        }
        else
        {
            Status = null;
        }

        Notify();
    }

    private async Task LoadVersionsAsync()
    {
        if (_reader is null || Source is not { } source)
        {
            return;
        }

        if (_reader.Unavailable(source) is { } reason)
        {
            Status = reason;
            return;
        }

        var token = Begin();
        Status = $"Reading the version list of {source.Id}…";

        try
        {
            var metadata = await _reader.ListAsync(source, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            var now = DateTimeOffset.Now;
            Versions.Clear();
            Versions.Add(VersionChoice.Current(source));

            foreach (var info in metadata.Versions)
            {
                Versions.Add(Choice(info, metadata.CurrentVersion, now));
            }

            SetSelected(Versions[0]);
            Status = Describe(metadata);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            End(token);
        }
    }

    private async Task ReadVersionAsync(int version)
    {
        if (_reader is null || Source is not { } source)
        {
            return;
        }

        var token = Begin();
        Status = $"Reading v{version} of {source.Id}…";

        try
        {
            var old = await _reader.ReadAsync(source, version, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            Old = old;
            Status = null;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            End(token);
            Notify();
        }
    }

    internal static VersionChoice Choice(VaultVersionInfo info, int currentVersion, DateTimeOffset now)
    {
        var when = info.CreatedTime is { } created
            ? $"{created.ToLocalTime():yyyy-MM-dd HH:mm} ({Elapsed.Since(created, now)})"
            : "no date";

        var unreadable = info.Unreadable(now);
        var label = unreadable is not null
            ? $"v{info.Version} · {when} · {unreadable}"
            : info.Version == currentVersion
                ? $"v{info.Version} · {when} · in Vault now"
                : $"v{info.Version} · {when}";

        return new VersionChoice(label, info.Version, unreadable);
    }

    /// <summary>
    /// What the list does not show. Vault drops the oldest versions past <c>max_versions</c>, and a
    /// list that silently started at v25 would read as "this secret was created at v25".
    /// </summary>
    internal static string Describe(VaultSecretMetadata metadata)
    {
        var count = metadata.Versions.Count;
        var oldest = metadata.Versions.Count > 0 ? metadata.Versions.Min(v => v.Version) : 0;
        var listed = count == 1 ? "1 version" : $"{count} versions";

        return oldest > 1
            ? $"{listed} listed. Vault no longer keeps v1–v{oldest - 1}."
            : $"{listed} listed.";
    }

    private CancellationToken Begin()
    {
        Cancel();
        _work = new CancellationTokenSource();
        IsBusy = true;
        return _work.Token;
    }

    private void End(CancellationToken token)
    {
        if (_work is { } work && work.Token == token)
        {
            _work = null;
            work.Dispose();
            IsBusy = false;
        }
    }

    private void Cancel()
    {
        if (_work is { } work)
        {
            _work = null;
            work.Cancel();
            work.Dispose();
            IsBusy = false;
        }
    }

    private void SetSelected(VersionChoice? choice)
    {
        _reverting = true;
        try
        {
            Selected = choice;
        }
        finally
        {
            _reverting = false;
        }
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(Text));
        _changed();
    }
}
