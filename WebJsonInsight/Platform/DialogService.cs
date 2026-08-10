using JsonInsight.Model;
using JsonInsight.ViewModels;

namespace WebJsonInsight.Platform;

/// <summary>
/// Which dialog is open, and the guards in front of each one.
///
/// <para>
/// This is the Blazor counterpart of <c>MainWindow.xaml.cs</c>: in WPF the four write flows are
/// <c>ShowDialog()</c> calls from the window's code-behind, and the checks in front of them —
/// nothing writable, nothing to copy from, a row covering too many keys — are <c>MessageBox</c>
/// calls. There is no modal loop here, so the state of "which dialog is up" has to live somewhere,
/// and putting the guards beside it keeps them in one place rather than spread across the two tabs
/// that trigger them.
/// </para>
///
/// <para>
/// The guards are ported rather than reinvented, including the 60-key cap: a whole section rolled up
/// into one row would open several hundred edit rows and be useless, and the cap is high enough for
/// any real batch — the six Couchbase URLs, a whole module — and low enough that a mis-click is
/// caught rather than rendered.
/// </para>
/// </summary>
public sealed class DialogService
{
    /// <summary>See the note above. Matches the WPF window's own constant.</summary>
    public const int MaximumEditRows = 60;

    private readonly MainVm _main;

    public DialogService(MainVm main) => _main = main;

    /// <summary>Raised whenever the open dialog changes, so the shell can re-render.</summary>
    public event Action? Changed;

    public PushVm? Push { get; private set; }

    public EditVm? Edit { get; private set; }

    public PromoteVm? Promote { get; private set; }

    public ChangesVm? Changes { get; private set; }

    /// <summary>The row whose restart endpoint is being configured, or null.</summary>
    public VaultConnectionVm? RestartConfig { get; private set; }

    /// <summary>The call screen, which exists mainly to be somewhere the bearer token is typed.</summary>
    public RestartVm? Restart { get; private set; }

    /// <summary>A refusal to open something, shown where the dialog would have been.</summary>
    public string? Refusal { get; private set; }

    public bool AnyOpen => Push is not null || Edit is not null || Promote is not null ||
                           Changes is not null || RestartConfig is not null || Restart is not null ||
                           Refusal is not null;

    // ---------------------------------------------------------------- restart

    /// <summary>Where the endpoint is set. Saved with the row; the token is not part of it.</summary>
    public void OpenRestartConfig(VaultConnectionVm row)
    {
        Close();
        RestartConfig = row;
        Raise();
    }

    /// <summary>
    /// The call. Refuses before opening when there is nothing configured, so the button says what is
    /// missing rather than opening onto a screen whose only content is a complaint.
    /// </summary>
    public void OpenRestart(VaultConnectionVm row)
    {
        if (!row.HasRestart)
        {
            Refuse($"No restart endpoint is configured for {row.Label}. Press Restart config to set one.");
            return;
        }

        Close();
        Restart = new RestartVm(row);
        Raise();
    }

    // ------------------------------------------------------------------- push

    /// <summary>
    /// The one place a push starts. Everything it needs is already loaded — the tier, its path and
    /// the connection — so what the dialog adds is the review: the live read, the diff against it,
    /// and the tier name typed out.
    /// </summary>
    /// <param name="tier">
    /// Null from the All tiers tab, which is about every tier at once and gets the dialog's own
    /// picker. The Tier editor is looking at exactly one and passes it.
    /// </param>
    public void OpenPush(TierDocument? tier = null, System.Text.Json.Nodes.JsonNode? updated = null, string? what = null)
    {
        if (_main.Documents.All(d => !d.Writable))
        {
            Refuse("No source is writable, so there is nothing this app may upload.");
            return;
        }

        Close();
        Push = new PushVm(_main, tier, updated, what);
        Raise();
    }

    // ------------------------------------------------------------------- edit

    public void OpenEdit(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            Refuse("Nothing to edit — this row has no keys under it.");
            return;
        }

        if (paths.Count > MaximumEditRows)
        {
            Refuse($"That row covers {paths.Count} keys. Expand it and edit a smaller part of it.");
            return;
        }

        Close();
        Edit = new EditVm(_main, paths);
        Raise();
    }

    // ---------------------------------------------------------------- promote

    public void OpenPromote(TierRowVm row)
    {
        var source = _main.Documents.FirstOrDefault(d =>
            !row.MissingFrom.Contains(d.Id, StringComparer.OrdinalIgnoreCase) &&
            d.Flat.Subtree(row.Path).Any());

        if (source is null)
        {
            Refuse($"No source holds {row.Path}, so there is nothing to copy from.");
            return;
        }

        var writableTargets = _main.Documents
            .Where(d => d.Writable && row.MissingFrom.Contains(d.Id, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        if (writableTargets.Length == 0)
        {
            Refuse($"{row.Path} is only missing from sources marked read-only, so there is nowhere to write it.");
            return;
        }

        Close();
        Promote = new PromoteVm(_main, _main.Flattener, source, row.Path, row.MissingFrom);
        Raise();
    }

    // ---------------------------------------------------------------- changes

    public void OpenChanges()
    {
        if (_main.Edits.IsEmpty)
        {
            return;
        }

        Close();
        Changes = new ChangesVm(_main);
        Raise();
    }

    // ------------------------------------------------------------------ close

    /// <summary>
    /// Shuts whatever is open. The grid is told the change set may have moved, the same way the WPF
    /// window calls NotifyEditsChanged after each dialog returns — the queued-edit marks on the rows
    /// are derived from it and would otherwise keep showing the previous set.
    /// </summary>
    public void CloseAndRefresh()
    {
        Close();
        _main.Tiers?.NotifyEditsChanged();
        Raise();
    }

    private void Close()
    {
        Push = null;
        Edit = null;
        Promote = null;
        Changes = null;
        RestartConfig = null;
        Restart = null;
        Refusal = null;
    }

    private void Refuse(string why)
    {
        Close();
        Refusal = why;
        Raise();
    }

    private void Raise() => Changed?.Invoke();
}
