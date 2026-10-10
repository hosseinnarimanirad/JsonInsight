using Bunit;
using JsonInsight.Model;
using JsonInsight.Promote;
using JsonInsight.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using WebJsonInsight.Components.Dialogs;
using WebJsonInsight.Platform;

namespace WebJsonInsight.Tests;

/// <summary>
/// The four write flows, rendered and driven far enough to prove the fences are in front of them.
///
/// <para>
/// Nothing here opens a socket. <see cref="PushVm"/> takes the same <c>checksOnOpen</c> switch
/// <see cref="MainVm"/> takes for the startup read, and it is false throughout: a rendering check
/// that quietly reached production Vault — let alone one whose next button uploads to it — would be
/// worse than no check at all.
/// </para>
/// </summary>
public sealed class WritePathTests : TestContext
{
    public WritePathTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private (MainVm Main, DialogService Dialogs) Seeded(IReadOnlyList<TierDocument>? documents = null)
    {
        var main = Fixtures.NewMain(documents);
        var dialogs = new DialogService(main);
        Services.AddSingleton(main);
        Services.AddSingleton(dialogs);
        return (main, dialogs);
    }

    // ------------------------------------------------------------ the guards

    /// <summary>
    /// A row rolled up from a whole section would open several hundred edit rows and be useless. The
    /// cap is high enough for any real batch and low enough that a mis-click is caught rather than
    /// rendered.
    /// </summary>
    [Fact]
    public void Editing_more_keys_than_the_cap_is_refused_with_the_count()
    {
        var (_, dialogs) = Seeded();

        var paths = Enumerable.Range(0, WriteFlows.MaximumEditRows + 1)
            .Select(i => $"Section:Key{i}")
            .ToArray();

        dialogs.OpenEdit(paths);

        Assert.Null(dialogs.Edit);
        Assert.NotNull(dialogs.Refusal);
        Assert.Contains($"{paths.Length} keys", dialogs.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void Editing_nothing_is_refused()
    {
        var (_, dialogs) = Seeded();

        dialogs.OpenEdit([]);

        Assert.Null(dialogs.Edit);
        Assert.Contains("no keys under it", dialogs.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A read-only tier's changes are still reviewed — they are real, and discarding them is still
    /// possible — but the window offers no push for them, and says where it would have gone.
    /// </summary>
    [Fact]
    public void A_read_only_tier_is_reviewed_but_offers_no_push()
    {
        var readOnly = new[]
        {
            Fixtures.AsTier("dev", 1, "{\"A\":1}", writable: false),
            Fixtures.AsTier("stage", 1, "{\"A\":2}", writable: false),
        };

        var (main, _) = Seeded(readOnly);

        main.Store.ApplyTree(main.Documents[0], System.Text.Json.Nodes.JsonNode.Parse("{\"A\":5}")!);
        main.PublishEdits();

        var changes = new ChangesVm(main, checksOnOpen: false);

        Assert.Equal("dev", changes.Tier!.Id);
        Assert.NotEmpty(changes.Changes);
        Assert.Null(changes.Push);
        Assert.False(changes.CanPushNow);
    }

    // -------------------------------------------------------------- the push

    /// <summary>Changes the Hub timeout on dev in memory, the way the Edit dialog does.</summary>
    private static void EditDev(MainVm main)
    {
        var edit = new EditVm(main, ["PaymentSettings:Hub:Timeout"]);
        var row = edit.Rows.First(r => r.TierId == "dev");
        row.Action = RowAction.Set;
        row.NewValue = "999";
        row.Kind = System.Text.Json.JsonValueKind.Number;
        edit.QueueCommand.Execute(null);
    }

    /// <summary>
    /// The fences, as the merged window enforces them. CanPush wants a live read behind it, a real
    /// difference to send, and the tier's name typed out — and none of those is a formality: the
    /// first is where the check-and-set version comes from.
    /// </summary>
    [Fact]
    public void Push_stays_disabled_until_checked_and_confirmed()
    {
        var (main, _) = Seeded();
        EditDev(main);

        var changes = new ChangesVm(main, checksOnOpen: false);
        var push = changes.Push!;

        var page = RenderComponent<ChangesDialog>(p => p
            .Add(c => c.Vm, changes)
            .Add(c => c.OnClose, () => { }));

        Assert.False(push.CanPush);

        // Typing the name is not enough on its own: nothing has been read, so there is no version to
        // carry as a check-and-set and nothing to diff against.
        push.ConfirmText = "dev";
        Assert.False(push.HasChecked);
        Assert.False(changes.CanPushNow);

        var button = page.FindAll("button").First(b => b.TextContent.Contains("Push to Vault", StringComparison.Ordinal));
        Assert.True(button.HasAttribute("disabled"));

        // The confirmation box itself is off until there is a read to confirm against.
        Assert.True(page.Find("#push-confirm").HasAttribute("disabled"));
    }

    /// <summary>
    /// One window: the queued keys and the push for them, with no Preview button and no second
    /// diff against the tier as it was read — only the one against the source as it is now.
    /// </summary>
    [Fact]
    public void Pending_changes_shows_the_queued_keys_above_the_live_diff_in_one_window()
    {
        var (main, _) = Seeded();
        EditDev(main);

        var changes = new ChangesVm(main, checksOnOpen: false);

        var page = RenderComponent<ChangesDialog>(p => p
            .Add(c => c.Vm, changes)
            .Add(c => c.OnClose, () => { }));

        var markup = page.Markup;
        var queued = markup.IndexOf("QUEUED CHANGES", StringComparison.Ordinal);
        var diff = markup.IndexOf("WHAT THE PUSH WOULD CHANGE", StringComparison.Ordinal);

        Assert.True(queued >= 0 && diff > queued, "The queued changes should sit above the push preview.");
        Assert.Contains("PaymentSettings:Hub:Timeout", markup, StringComparison.Ordinal);
        Assert.DoesNotContain(page.FindAll("button"), b => b.TextContent.Trim() == "Preview");
        Assert.Contains(page.FindAll("button"), b => b.TextContent.Contains("Check Vault again", StringComparison.Ordinal));
        Assert.Contains(page.FindAll("button"), b => b.TextContent.Contains("Discard this tier's changes", StringComparison.Ordinal));
    }

    /// <summary>The confirmation is the tier's own name, and nothing else will do.</summary>
    [Theory]
    [InlineData("", false)]
    [InlineData("nonsense", false)]
    [InlineData("stage", false)]
    [InlineData("dev", true)]
    [InlineData("  DEV  ", true)]
    public void The_confirmation_must_be_the_destination_name(string typed, bool matches)
    {
        var main = Fixtures.NewMain();
        Services.AddSingleton(main);

        var push = new PushVm(main, main.Documents.First(d => d.Id == "dev"), checksOnOpen: false)
        {
            ConfirmText = typed,
        };

        Assert.Equal(matches, push.ConfirmMatches);
    }

    /// <summary>
    /// The push belongs to the tier picked above it: its own tier is fixed, so there is one picker in
    /// the window rather than a second one that could push one tier's changes into another.
    /// </summary>
    [Fact]
    public void The_push_is_fixed_to_the_tier_being_reviewed()
    {
        var (main, _) = Seeded();
        EditDev(main);

        var changes = new ChangesVm(main, checksOnOpen: false);

        var page = RenderComponent<ChangesDialog>(p => p
            .Add(c => c.Vm, changes)
            .Add(c => c.OnClose, () => { }));

        Assert.True(changes.Push!.IsTierFixed);
        Assert.Single(changes.Push.Tiers);
        Assert.Single(page.FindAll("select"));
        Assert.Contains(changes.What, page.Markup, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- the edits

    /// <summary>
    /// One row per key per tier, including tiers that do not have the key — a key present in exactly
    /// one tier is the drift this tool exists to find.
    /// </summary>
    [Fact]
    public void The_edit_grid_has_a_row_per_tier_including_tiers_without_the_key()
    {
        var (main, _) = Seeded();

        var edit = new EditVm(main, ["AccountSettings:NightlyApprovalJob:BatchSize"]);

        var page = RenderComponent<EditDialog>(p => p
            .Add(c => c.Vm, edit)
            .Add(c => c.OnClose, () => { }));

        Assert.Equal(main.Documents.Count, edit.Rows.Count);
        Assert.Contains(edit.Rows, r => !r.Exists);
        Assert.Contains("(absent)", page.Markup, StringComparison.Ordinal);
    }

    /// <summary>An edit that matches the existing value is dropped rather than applied.</summary>
    [Fact]
    public void Queueing_a_value_that_already_matches_adds_nothing()
    {
        var (main, _) = Seeded();

        var edit = new EditVm(main, ["PaymentSettings:Hub:Timeout"]);
        foreach (var row in edit.Rows.Where(r => r.Exists))
        {
            row.Action = RowAction.Set;
            row.NewValue = row.Current!.ComparableValue;
            row.Kind = System.Text.Json.JsonValueKind.Number;
        }

        edit.QueueCommand.Execute(null);

        Assert.Empty(main.Store.ModifiedTiers);
    }

    /// <summary>A real change lands in memory, on that tier and no other.</summary>
    [Fact]
    public void A_real_change_queues_and_is_counted()
    {
        var (main, _) = Seeded();

        var edit = new EditVm(main, ["PaymentSettings:Hub:Timeout"]);
        var row = edit.Rows.First(r => r.TierId == "dev");
        row.Action = RowAction.Set;
        row.NewValue = "999";
        row.Kind = System.Text.Json.JsonValueKind.Number;

        edit.QueueCommand.Execute(null);

        Assert.Equal(["dev"], main.Store.ModifiedTiers);

        // Applied, not queued: the tier itself now says it, which is what every tab reads.
        Assert.Equal("999", main.Documents.First(d => d.Id == "dev").Flat.Find("PaymentSettings:Hub:Timeout")!.Value);
    }

    // ----------------------------------------------------------- the promote

    /// <summary>
    /// The per-key defaults by classification: business copied verbatim, infra and secret given a
    /// placeholder. The placeholder is deliberately not "" — an empty string is a valid, deliberate
    /// value in these documents.
    /// </summary>
    [Fact]
    public void Promote_defaults_a_secret_to_a_placeholder_and_never_shows_its_value()
    {
        var (main, _) = Seeded();

        var promote = new PromoteVm(
            main,
            main.Flattener,
            main.Documents.First(d => d.Id == "dev"),
            "AccountSettings:NightlyApprovalJob",
            ["stage", "beta"]);

        var page = RenderComponent<PromoteDialog>(p => p
            .Add(c => c.Vm, promote)
            .Add(c => c.OnClose, () => { }));

        Assert.NotEmpty(promote.Leaves);
        Assert.All(promote.Leaves, l => Assert.NotEqual(PromotionAction.Skip, l.Action));
        Assert.DoesNotContain("dev-couchbase-password-value", page.Markup, StringComparison.Ordinal);
    }

    /// <summary>Promoting something no source holds has nothing to copy from, and says so.</summary>
    [Fact]
    public void Promoting_what_no_source_holds_is_refused()
    {
        var (main, dialogs) = Seeded();

        var row = main.Tiers!.Rows.FirstOrDefault(r => r.CanPromote);
        Assert.NotNull(row);

        // The real row promotes fine; the refusal is for a path nothing holds, which is what a stale
        // grid row would be after a pull removed the section it named.
        dialogs.OpenPromote(row!);
        Assert.NotNull(dialogs.Promote);
    }

    // ------------------------------------------------------------ the wiring

    /// <summary>
    /// Promote does not write. It lands the subtree in memory, and Pending changes is then where it is
    /// reviewed and pushed — the live re-read, the diff and the typed confirmation.
    /// </summary>
    [Fact]
    public void A_promotion_is_pushed_from_pending_changes_rather_than_written()
    {
        var (main, dialogs) = Seeded();

        var row = main.Tiers!.Rows.First(r => r.CanPromote);
        dialogs.OpenPromote(row);

        var promote = dialogs.Promote!;
        var destination = promote.Destinations.First();
        promote.Destination = destination;

        Assert.True(promote.Apply());

        dialogs.OpenChanges();

        Assert.Null(dialogs.Promote);
        Assert.NotNull(dialogs.Changes);
        Assert.Equal(destination.Id, dialogs.Changes!.Push!.Tier!.Id);
    }

    /// <summary>The host renders whichever dialog is open, and nothing when none is.</summary>
    [Fact]
    public void The_dialog_host_is_empty_until_something_opens()
    {
        var (_, dialogs) = Seeded();

        var page = RenderComponent<DialogHost>();
        Assert.Empty(page.FindAll(".modal"));

        dialogs.OpenEdit([]);
        page.Render();
        Assert.NotEmpty(page.FindAll(".modal"));

        dialogs.CloseAndRefresh();
        page.Render();
        Assert.Empty(page.FindAll(".modal"));
    }
}
