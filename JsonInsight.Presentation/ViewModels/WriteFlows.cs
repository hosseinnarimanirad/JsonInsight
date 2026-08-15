using System.Text.Json.Nodes;
using JsonInsight.Model;

namespace JsonInsight.ViewModels;

/// <summary>
/// The result of asking to open one of the write dialogs: the view model, or the reason there isn't
/// one.
///
/// <para>
/// Both hosts have to render a refusal in their own way — a <c>MessageBox</c> in WPF, a line where the
/// dialog would have been in Blazor — so the guard cannot open anything itself. It answers, and the
/// host decides how to say it.
/// </para>
/// </summary>
/// <param name="Value">The view model to open, or null when there is nothing to open.</param>
/// <param name="Refusal">
/// What to tell the user, or null. Both null means "nothing to do, and nothing worth saying" — which
/// is Review changes with an empty change set: the button is simply inert, and a popup explaining that
/// nothing is queued would be noise.
/// </param>
public readonly record struct Guarded<T>(T? Value, string? Refusal) where T : class
{
    public static Guarded<T> Allow(T value) => new(value, null);

    public static Guarded<T> Refuse(string why) => new(null, why);

    public static Guarded<T> Silently => new(null, null);
}

/// <summary>
/// A push handed on from the Review changes or Promote screen: where it goes, the document those
/// screens built, and the phrase the push dialog uses to describe it.
///
/// <para>
/// Both screens end the same way — "if there is a destination and the document builds, push that" —
/// and both hosts wrote that two-part guard out again at the button. Answering it once means the
/// button only has to ask whether there is a push to make.
/// </para>
/// </summary>
public sealed record PendingPush(TierDocument Tier, JsonNode Updated, string What);

/// <summary>
/// The checks in front of every dialog that can change a source, and the words used to refuse.
///
/// <para>
/// These lived twice — once in <c>MainWindow.xaml.cs</c> as MessageBox calls, once in Blazor's
/// <c>DialogService</c> — and the two copies had already drifted: the same refusal said "No tier holds
/// {path}" in one app and "No source holds {path}" in the other, and "tiers that are marked read-only"
/// against "sources marked read-only". Nobody chose that; it is just what happens to a guard that is
/// written down twice. They are settled here on the vocabulary the Sources tab uses.
/// </para>
///
/// <para>
/// Kept out of the view models themselves because these are decisions about whether to <em>open</em> a
/// screen, which is a host concern; what they return is the view model the host then shows.
/// </para>
/// </summary>
public static class WriteFlows
{
    /// <summary>
    /// A whole section rolled up into one row would open several hundred edit rows and be useless. High
    /// enough for any real batch — the six Couchbase URLs, a whole module — and low enough that a
    /// mis-click is caught rather than rendered.
    /// </summary>
    public const int MaximumEditRows = 60;

    /// <summary>
    /// Everything a push needs is already loaded — the tier, its path and the connection — so what the
    /// dialog adds is the review: the live read, the diff against it, and the tier name typed out.
    /// </summary>
    /// <param name="tier">
    /// Null from the All tiers tab, which is about every source at once and gets the dialog's own
    /// picker. The Tier editor is looking at exactly one and passes it.
    /// </param>
    /// <param name="updated">
    /// The document to push when the caller has one that is not simply the tier as it stands — the Tier
    /// editor's pane. Null means the tier plus whatever change set is queued against it.
    /// </param>
    public static Guarded<PushVm> Push(
        MainVm main,
        TierDocument? tier = null,
        JsonNode? updated = null,
        string? what = null)
    {
        if (main.Documents.All(d => !d.Writable))
        {
            return Guarded<PushVm>.Refuse("No source is writable, so there is nothing this app may upload.");
        }

        return Guarded<PushVm>.Allow(new PushVm(main, tier, updated, what));
    }

    public static Guarded<EditVm> Edit(MainVm main, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return Guarded<EditVm>.Refuse("Nothing to edit — this row has no keys under it.");
        }

        if (paths.Count > MaximumEditRows)
        {
            return Guarded<EditVm>.Refuse(
                $"That row covers {paths.Count} keys. Expand it and edit a smaller part of it.");
        }

        return Guarded<EditVm>.Allow(new EditVm(main, paths));
    }

    /// <summary>
    /// Promoting needs somewhere to copy from and somewhere to copy to, and the two failures read
    /// differently: nothing holds this path at all, against nothing that holds it can be written.
    /// </summary>
    public static Guarded<PromoteVm> Promote(MainVm main, TierRowVm row)
    {
        var source = main.Documents.FirstOrDefault(d =>
            !row.MissingFrom.Contains(d.Id, StringComparer.OrdinalIgnoreCase) &&
            d.Flat.Subtree(row.Path).Any());

        if (source is null)
        {
            return Guarded<PromoteVm>.Refuse($"No source holds {row.Path}, so there is nothing to copy from.");
        }

        var writableTargets = main.Documents
            .Where(d => d.Writable && row.MissingFrom.Contains(d.Id, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        if (writableTargets.Length == 0)
        {
            return Guarded<PromoteVm>.Refuse(
                $"{row.Path} is only missing from sources marked read-only, so there is nowhere to write it.");
        }

        return Guarded<PromoteVm>.Allow(new PromoteVm(main, main.Flattener, source, row.Path, row.MissingFrom));
    }

    /// <summary>
    /// The review of everything held in memory and not yet written. Published first, so a change made
    /// a moment ago on another tab counts — otherwise pressing this straight after an edit would
    /// report nothing to review.
    /// </summary>
    public static Guarded<ChangesVm> Changes(MainVm main)
    {
        main.PublishEdits();

        return main.Store.ModifiedTiers.Count == 0
            ? Guarded<ChangesVm>.Silently
            : Guarded<ChangesVm>.Allow(new ChangesVm(main));
    }

    /// <summary>
    /// Refused before opening when there is nothing configured, so the button says what is missing
    /// rather than opening onto a screen whose only content is a complaint.
    /// </summary>
    public static Guarded<RestartVm> Restart(VaultConnectionVm row) =>
        row.HasRestart
            ? Guarded<RestartVm>.Allow(new RestartVm(row))
            : Guarded<RestartVm>.Refuse(
                $"No restart endpoint is configured for {row.Label}. Press Restart config to set one.");
}
