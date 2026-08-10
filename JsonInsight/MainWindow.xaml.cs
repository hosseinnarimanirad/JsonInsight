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
        Editor.PushRequested += (_, tier) => Push(tier);

        Sources.RestartConfigRequested += OnRestartConfigRequested;
        Sources.RestartRequested += OnRestartRequested;
    }

    /// <summary>
    /// Where a source's restart endpoint is set. The dialog edits the row directly, so the Sources
    /// tab's own Save settings persists it with everything else on that row.
    /// </summary>
    private void OnRestartConfigRequested(object? sender, VaultConnectionVm row) =>
        new RestartConfigDialog(row) { Owner = this }.ShowDialog();

    /// <summary>
    /// Calling it. Refused before the window opens when there is nothing configured, so the button
    /// says what is missing rather than opening onto a screen whose only content is a complaint.
    /// </summary>
    private void OnRestartRequested(object? sender, VaultConnectionVm row)
    {
        if (!row.HasRestart)
        {
            MessageBox.Show(this,
                $"No restart endpoint is configured for {row.Label}. Press Restart config to set one.",
                "Restart", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        new RestartCallDialog(new RestartVm(row)) { Owner = this }.ShowDialog();
    }

    /// <summary>
    /// The one place a push starts. Everything it needs is already loaded - the tier, its Vault path
    /// and the connection - so what the dialog adds is the review: the live read, the diff against
    /// it, and the tier name typed out.
    /// </summary>
    private void Push(Model.TierDocument? tier)
    {
        if (_vm.Documents.All(d => !d.Writable))
        {
            MessageBox.Show(this,
                "No tier is writable, so there is nothing this app may upload.",
                "Push to Vault", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        new PushDialog(new PushVm(_vm, tier)) { Owner = this }.ShowDialog();
    }

    private void OnPromoteRequested(object? sender, TierRowVm row)
    {
        var source = _vm.Documents.FirstOrDefault(d =>
            !row.MissingFrom.Contains(d.Id, StringComparer.OrdinalIgnoreCase) &&
            d.Flat.Subtree(row.Path).Any());

        if (source is null)
        {
            MessageBox.Show(this,
                $"No tier holds {row.Path}, so there is nothing to copy from.",
                "Promote", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var writableTargets = _vm.Documents
            .Where(d => d.Writable && row.MissingFrom.Contains(d.Id, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        if (writableTargets.Length == 0)
        {
            MessageBox.Show(this,
                $"{row.Path} is only missing from tiers that are marked read-only, so there is nowhere to write it.",
                "Promote", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new PromoteDialog(new PromoteVm(_vm, _vm.Flattener, source, row.Path, row.MissingFrom))
        {
            Owner = this,
        };

        dialog.ShowDialog();
    }

    private void OnEditRequested(object? sender, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            MessageBox.Show(this,
                "Nothing to edit — this row has no keys under it.",
                "Edit", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // A whole section rolled up into one row would open several hundred rows and be useless.
        // The cap is high enough for any real batch (the six Couchbase URLs, a whole module) and
        // low enough that a mis-click is caught rather than rendered.
        const int maximum = 60;
        if (paths.Count > maximum)
        {
            MessageBox.Show(this,
                $"That row covers {paths.Count} keys. Expand it and edit a smaller part of it.",
                "Edit", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        new EditDialog(new EditVm(_vm, paths)) { Owner = this }.ShowDialog();
        _vm.Tiers?.NotifyEditsChanged();
    }

    private void OnReviewChangesRequested(object? sender, EventArgs e)
    {
        if (_vm.Edits.IsEmpty)
        {
            return;
        }

        new ChangesDialog(new ChangesVm(_vm)) { Owner = this }.ShowDialog();
        _vm.Tiers?.NotifyEditsChanged();
    }
}
