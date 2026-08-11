namespace JsonInsight.ViewModels;

/// <summary>
/// Runs one asynchronous command with a busy flag held for the duration, and turns a failure into
/// something the screen says rather than an exception nobody sees.
///
/// <para>
/// Seven command bodies across four view models had written this out by hand: raise the flag, await,
/// report in a <c>catch</c>, lower the flag in a <c>finally</c>. The shape is dull; the
/// <c>finally</c> is not. A command that forgets it leaves its button greyed and its row saying
/// "Loading…" forever, with nothing on screen to explain why — and no test notices, because the
/// happy path lowers the flag either way. That failure is only ever found by using the app, which is
/// the argument for writing it once.
/// </para>
///
/// <para>
/// The flag arrives as a setter rather than being inferred, because these are not one flag: some are
/// per-view-model (<c>Busy</c>), two are per-row (<c>row.Busy</c>, <c>row.Searching</c> — a row can
/// be searching its Vault while another row is loading), and two have to raise a notification for a
/// computed <c>CanCall</c>/<c>CanPush</c> in the same breath, on the failure path as much as on the
/// happy one. A base class was the obvious alternative and does not fit: the view models here already
/// derive from <c>ObservableObject</c>, and the flag four of these commands set does not even live
/// on the view model that owns the command — it lives on the row they were pressed against.
/// </para>
///
/// <para>
/// The <em>re-entry</em> guard — <c>if (Busy) return;</c> — deliberately stayed at the call sites and
/// is not part of this. It looks like the same shape but is not: four of the sites run configuration
/// checks between the guard and the flag which must not run a second time on a second press (they
/// write refusals into the same status line the first press is using), and their guards ask more
/// than "is it busy" — one also refuses a non-Vault row, one a missing tier, one asks a computed
/// <c>CanCall</c>. Folding them in here would mean either checking the same flag twice or moving
/// those checks under the flag, where a refusal would flicker the button on and off.
/// </para>
/// </summary>
internal static class BusyGuard
{
    /// <param name="setBusy">
    /// Called with true before the work and false after it, whatever happened. Anything the flag's
    /// own change notification cannot carry — a computed <c>CanPush</c>, a whole <c>NotifyState()</c>
    /// — belongs in here rather than after the await, so that it is equally unmissable when the work
    /// threw.
    /// </param>
    /// <param name="work">
    /// The command itself, including whatever it wants to put on screen on the way in: it runs
    /// synchronously up to its first await, so a status line set at the top of it lands in the same
    /// breath as the flag, exactly as it did when this was written out in full.
    /// </param>
    /// <param name="report">
    /// What to say when the work throws. Per-site, because these do not report alike: some write a
    /// row's status, one also logs, and two also put <c>TestPassed</c> back to false — a read that
    /// failed must not leave Load switched on behind it.
    /// </param>
    public static async Task RunAsync(Action<bool> setBusy, Func<Task> work, Action<Exception> report)
    {
        setBusy(true);

        try
        {
            // ConfigureAwait(true) written out, as every await in this layer is: the finally below
            // lowers a flag that both hosts have bound, so it has to come back to the thread that may
            // raise a change notification. This is the one await in the chain that is not visible at a
            // call site, which is the reason to say it here rather than rely on the default.
            await work().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Every one of these commands reaches a network or a disk, where the exception set is
            // open-ended and none of it is a bug in this app. A narrower catch would turn a proxy's
            // odd socket error into a crashed app instead of a line in a row's status.
            report(ex);
        }
        finally
        {
            setBusy(false);
        }
    }
}
