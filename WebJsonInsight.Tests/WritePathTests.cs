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

        var paths = Enumerable.Range(0, DialogService.MaximumEditRows + 1)
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
    /// With every source read-only there is nothing this app may upload, so the push screen does not
    /// open at all rather than opening onto an empty picker.
    /// </summary>
    [Fact]
    public void Pushing_with_no_writable_source_is_refused()
    {
        var readOnly = new[]
        {
            Fixtures.AsTier("dev", 1, "{\"A\":1}", writable: false),
            Fixtures.AsTier("stage", 1, "{\"A\":2}", writable: false),
        };

        var (_, dialogs) = Seeded(readOnly);

        dialogs.OpenPush();

        Assert.Null(dialogs.Push);
        Assert.Contains("nothing this app may upload", dialogs.Refusal!, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------- the push

    /// <summary>
    /// The fences, as the screen enforces them. CanPush wants a live read behind it, a real
    /// difference to send, and the tier's name typed out — and none of those is a formality: the
    /// first is where the check-and-set version comes from.
    /// </summary>
    [Fact]
    public void Push_stays_disabled_until_checked_and_confirmed()
    {
        var main = Fixtures.NewMain();
        Services.AddSingleton(main);

        var push = new PushVm(main, main.Documents[0], checksOnOpen: false);

        var page = RenderComponent<PushDialog>(p => p
            .Add(c => c.Vm, push)
            .Add(c => c.OnClose, () => { }));

        Assert.False(push.CanPush);

        // Typing the name is not enough on its own: nothing has been read, so there is no version to
        // carry as a check-and-set and nothing to diff against.
        push.ConfirmText = main.Documents[0].Id;
        Assert.False(push.HasChecked);
        Assert.False(push.CanPush);

        var button = page.FindAll("button").First(b => b.TextContent.Contains("Push to Vault", StringComparison.Ordinal));
        Assert.True(button.HasAttribute("disabled"));
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
    /// A supplied document fixes the tier: a promote plan or an edited document belongs to the tier it
    /// was built against, and a picker beside it would offer to push one tier's changes into another.
    /// </summary>
    [Fact]
    public void A_supplied_document_fixes_the_destination()
    {
        var main = Fixtures.NewMain();
        Services.AddSingleton(main);

        var dev = main.Documents.First(d => d.Id == "dev");
        var push = new PushVm(main, dev, dev.Root, "a promoted subtree", checksOnOpen: false);

        var page = RenderComponent<PushDialog>(p => p
            .Add(c => c.Vm, push)
            .Add(c => c.OnClose, () => { }));

        Assert.True(push.IsTierFixed);
        Assert.Single(push.Tiers);
        Assert.Empty(page.FindAll("#push-tier"));
        Assert.Contains("a promoted subtree", page.Markup, StringComparison.Ordinal);
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

    /// <summary>An edit that matches the existing value is dropped rather than queued.</summary>
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

        Assert.True(main.Edits.IsEmpty);
    }

    /// <summary>A real change does queue, and the grid says how many are waiting.</summary>
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

        Assert.False(main.Edits.IsEmpty);
        Assert.Single(main.Edits.For("dev"));
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
    /// Promote and Pending changes do not write. They build a document and hand it to the one push
    /// screen, which is where the live re-read, the diff and the typed confirmation live.
    /// </summary>
    [Fact]
    public void Promote_hands_off_to_the_push_screen_rather_than_writing()
    {
        var (main, dialogs) = Seeded();

        var row = main.Tiers!.Rows.First(r => r.CanPromote);
        dialogs.OpenPromote(row);

        var promote = dialogs.Promote!;
        promote.Destination = promote.Destinations.First();

        var updated = promote.BuildUpdated();
        Assert.NotNull(updated);

        dialogs.OpenPush(promote.Destination, updated, promote.What);

        Assert.Null(dialogs.Promote);
        Assert.NotNull(dialogs.Push);
        Assert.True(dialogs.Push!.IsTierFixed);
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
