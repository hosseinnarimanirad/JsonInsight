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

    /// <summary>Where the endpoint is set. Saved with the row; the token is not part of it.</summary>
    public void OpenRestartConfig(VaultConnectionVm row)
    {
        Close();
        RestartConfig = row;
        Raise();
    }

    /// <summary>The call. See <see cref="WriteFlows.Restart"/> for why it can be refused.</summary>
    public void OpenRestart(VaultConnectionVm row) =>
        Open(WriteFlows.Restart(row), vm => Restart = vm);

    // ------------------------------------------------------------------- push

    /// <summary>The one place a push starts. See <see cref="WriteFlows.Push"/>.</summary>
    public void OpenPush(TierDocument? tier = null, JsonNode? updated = null, string? what = null) =>
        Open(WriteFlows.Push(_main, tier, updated, what), vm => Push = vm);

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
        Close();
        _main.Tiers?.NotifyEditsChanged();
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
