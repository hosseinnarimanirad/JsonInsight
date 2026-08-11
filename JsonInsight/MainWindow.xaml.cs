using System.Text.Json.Nodes;
using System.Windows;
using JsonInsight.ViewModels;
using JsonInsight.Views;

namespace JsonInsight;

public partial class MainWindow : Window
{
    private readonly MainVm _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        Tiers.PromoteRequested += OnPromoteRequested;
        Tiers.EditRequested += OnEditRequested;
        Tiers.ReviewChangesRequested += OnReviewChangesRequested;

        // Both push entry points open the same dialog. The All tiers tab names no tier and gets the
        // dialog's own picker; the Tier editor is looking at exactly one and passes it.
        Tiers.PushRequested += (_, _) => Push(null);
        Editor.PushRequested += (_, request) => Push(request.Tier, request.Updated, request.What);

        Sources.RestartConfigRequested += OnRestartConfigRequested;
        Sources.RestartRequested += OnRestartRequested;
    }

    /// <summary>
    /// Where a source's restart endpoint is set. The dialog edits the row directly, so the Sources
    /// tab's own Save settings persists it with everything else on that row.
    /// </summary>
    private void OnRestartConfigRequested(object? sender, VaultConnectionVm row) =>
        new RestartConfigDialog(row) { Owner = this }.ShowDialog();

    /// <summary>Calling it. See <see cref="WriteFlows.Restart"/> for why it can be refused.</summary>
    private void OnRestartRequested(object? sender, VaultConnectionVm row) =>
        Open(WriteFlows.Restart(row), "Restart", vm => new RestartCallDialog(vm));

    /// <summary>
    /// The one place a push starts. Everything it needs is already loaded - the tier, its Vault path
    /// and the connection - so what the dialog adds is the review: the live read, the diff against
    /// it, and the tier name typed out.
    /// </summary>
    /// <param name="updated">
    /// The document to push, when the caller has one that is not simply the tier as it stands. The
    /// Tier editor passes its edited pane; the All tiers tab passes nothing and means the tier plus
    /// whatever change set is queued against it.
    /// </param>
    private void Push(Model.TierDocument? tier, JsonNode? updated = null, string? what = null) =>
        Open(WriteFlows.Push(_vm, tier, updated, what), "Push to Vault", vm => new PushDialog(vm));

    private void OnPromoteRequested(object? sender, TierRowVm row) =>
        Open(WriteFlows.Promote(_vm, row), "Promote", vm => new PromoteDialog(vm));

    /// <summary>
    /// The queued-edit marks on the grid rows are derived from the change set, so the grid is told
    /// after each of these two returns — but only when something actually opened, since a refusal
    /// cannot have changed anything.
    /// </summary>
    private void OnEditRequested(object? sender, IReadOnlyList<string> paths)
    {
        if (Open(WriteFlows.Edit(_vm, paths), "Edit", vm => new EditDialog(vm)))
        {
            _vm.Tiers?.NotifyEditsChanged();
        }
    }

    private void OnReviewChangesRequested(object? sender, EventArgs e)
    {
        if (Open(WriteFlows.Changes(_vm), "Review changes", vm => new ChangesDialog(vm)))
        {
            _vm.Tiers?.NotifyEditsChanged();
        }
    }

    /// <summary>
    /// Shows the dialog the guard allowed, or says why there isn't one. The guards themselves are
    /// <see cref="WriteFlows"/>, shared with the Blazor host — this half is only how a refusal looks
    /// here, which is the one part of it that is genuinely per-app.
    /// </summary>
    /// <returns>True when a dialog was shown, so the caller can tell a refusal from a return.</returns>
    private bool Open<T>(Guarded<T> guarded, string caption, Func<T, Window> dialog) where T : class
    {
        if (guarded.Refusal is { } why)
        {
            MessageBox.Show(this, why, caption, MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        if (guarded.Value is not { } vm)
        {
            return false;
        }

        var window = dialog(vm);
        window.Owner = this;
        window.ShowDialog();
        return true;
    }
}
