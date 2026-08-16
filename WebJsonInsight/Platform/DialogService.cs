using System.Text.Json.Nodes;
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
/// The guards themselves are not here: they are <see cref="WriteFlows"/>, shared with the WPF window,
/// because two copies of the same refusal had already drifted apart in wording. What is left here is
/// this host's half — which dialog is up, and how a refusal is shown.
/// </para>
/// </summary>
public sealed class DialogService
{
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

    // ---------------------------------------------------------------- restart

    /// <summary>
    /// What the restart config said when its dialog opened, so closing it can tell an edit from a
    /// look. The token is not part of it — it is typed per call and never saved.
    /// </summary>
    private (string Url, string Body, bool InsecureTls) _restartConfigOpened;

    /// <summary>Where the endpoint is set. Saved with the row; the token is not part of it.</summary>
    public void OpenRestartConfig(VaultConnectionVm row)
    {
        Close();
        RestartConfig = row;
        _restartConfigOpened = (row.RestartUrl, row.RestartBody, row.RestartAllowInsecureTls);
        Raise();
    }

    /// <summary>The call. See <see cref="WriteFlows.Restart"/> for why it can be refused.</summary>
    public void OpenRestart(VaultConnectionVm row) =>
        Open(WriteFlows.Restart(row), vm => Restart = vm);

    // ------------------------------------------------------------------- push

    /// <summary>The one place a push starts. See <see cref="WriteFlows.Push"/>.</summary>
    public void OpenPush(TierDocument? tier = null, JsonNode? updated = null, string? what = null) =>
        Open(WriteFlows.Push(_main, tier, updated, what), vm => Push = vm);

    /// <summary>
    /// True while the open push was handed off from the review screen, so closing it can land back
    /// there. Cleared by <see cref="Close"/>, which every other opening passes through.
    /// </summary>
    private bool _pushCameFromChanges;

    /// <summary>
    /// The review screen's hand-off. Separate from <see cref="OpenPush"/> because of what happens on
    /// close: in WPF the push dialog is modal over the review, so finishing one tier lands you back
    /// on the review advanced to the next tier still holding changes. Here there is no modal stack —
    /// opening the push closed the review — so the way back has to be remembered.
    /// </summary>
    public void OpenPushFromChanges(PendingPush pending)
    {
        Open(WriteFlows.Push(_main, pending.Tier, pending.Updated, pending.What), vm => Push = vm);
        _pushCameFromChanges = Push is not null;
    }

    // ------------------------------------------------------------------- edit

    public void OpenEdit(IReadOnlyList<string> paths) =>
        Open(WriteFlows.Edit(_main, paths), vm => Edit = vm);

    // ---------------------------------------------------------------- promote

    public void OpenPromote(TierRowVm row) =>
        Open(WriteFlows.Promote(_main, row), vm => Promote = vm);

    // ---------------------------------------------------------------- changes

    public void OpenChanges() =>
        Open(WriteFlows.Changes(_main), vm => Changes = vm);

    // ------------------------------------------------------------------ close

    /// <summary>
    /// Shuts whatever is open. The grid is told the change set may have moved, the same way the WPF
    /// window calls NotifyEditsChanged after each dialog returns — the queued-edit marks on the rows
    /// are derived from it and would otherwise keep showing the previous set.
    /// </summary>
    public void CloseAndRefresh()
    {
        var reopenChanges = _pushCameFromChanges && Push is not null;

        Close();
        _main.Tiers?.NotifyEditsChanged();

        // Back to the review, advanced to the next tier still holding changes — the queue that lets
        // a batch spanning four tiers be pushed in four presses without reopening anything. Whether
        // the push went through or was backed out of, the review is where you were; when the last
        // tier was just pushed, Changes has nothing to show and everything simply closes.
        if (reopenChanges && WriteFlows.Changes(_main) is { Value: { } next })
        {
            Changes = next;
        }

        Raise();
    }

    /// <summary>
    /// Shows whatever the guard allowed, or the reason it did not. A guard that answered neither — an
    /// empty change set — closes nothing and says nothing, so the press is simply inert.
    /// </summary>
    private void Open<T>(Guarded<T> guarded, Action<T> show) where T : class
    {
        if (guarded.Refusal is { } why)
        {
            Refuse(why);
            return;
        }

        if (guarded.Value is not { } vm)
        {
            return;
        }

        Close();
        show(vm);
        Raise();
    }

    private void Close()
    {
        // The restart-config dialog edits the row directly, so closing it — however it closes, the
        // Done button or another dialog opening over it — is the moment an edit is real, and the
        // settings save then and there rather than waiting on a Save settings press that was easy
        // to forget. Mirrors the WPF window, which saves when its modal ShowDialog returns changed.
        if (RestartConfig is { } edited &&
            (edited.RestartUrl, edited.RestartBody, edited.RestartAllowInsecureTls) != _restartConfigOpened)
        {
            _main.Vault?.SaveRestartConfig();
        }

        Push = null;
        Edit = null;
        Promote = null;
        Changes = null;
        RestartConfig = null;
        Restart = null;
        Refusal = null;
        _pushCameFromChanges = false;
    }

    private void Refuse(string why)
    {
        Close();
        Refusal = why;
        Raise();
    }

    private void Raise() => Changed?.Invoke();
}
