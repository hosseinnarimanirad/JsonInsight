using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using JsonInsight.ViewModels;

namespace JsonInsight.Views;

public partial class TiersView : UserControl
{
    private TiersVm? _boundTo;

    public TiersView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Grid.MouseDoubleClick += OnGridDoubleClick;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_boundTo is not null)
        {
            _boundTo.DocumentsChanged -= OnDocumentsChanged;
        }

        _boundTo = null;
        BuildTierColumns();

        if (DataContext is TiersVm vm)
        {
            // A Vault pull replaces the documents without replacing the view model, and the headers
            // carry the provenance - "Vault v8" against "app.beta.v08.json (Vault unavailable)".
            // Rebuilding only on DataContextChanged would leave them describing the previous source.
            vm.DocumentsChanged += OnDocumentsChanged;
        }
    }

    private void OnDocumentsChanged(object? sender, EventArgs e) => BuildTierColumns(force: true);

    /// <summary>
    /// One column per configured tier, generated at runtime. The alternative - hardcoding dev,
    /// stage and beta - would mean editing XAML to see prod, which is exactly the kind of coupling
    /// tiers.json exists to avoid.
    /// </summary>
    private void BuildTierColumns(bool force = false)
    {
        if (DataContext is not TiersVm vm || (!force && ReferenceEquals(vm, _boundTo)))
        {
            return;
        }

        _boundTo = vm;

        // Keep the Path column and the actions column; replace everything in between.
        while (Grid.Columns.Count > 2)
        {
            Grid.Columns.RemoveAt(1);
        }

        for (var i = 0; i < vm.Diff.TierIds.Count; i++)
        {
            var document = vm.Documents.FirstOrDefault(d =>
                d.Id.Equals(vm.Diff.TierIds[i], StringComparison.OrdinalIgnoreCase));

            Grid.Columns.Insert(1 + i, new DataGridTemplateColumn
            {
                Header = BuildHeader(vm.Diff.TierIds[i], document, vm.UnavailableReason(vm.Diff.TierIds[i])),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                CellTemplate = BuildCellTemplate(i),
            });
        }
    }

    private static object BuildHeader(string tierId, Model.TierDocument? document, string? unavailable)
    {
        var panel = new StackPanel();

        var title = new TextBlock { Text = tierId.ToUpperInvariant(), FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        panel.Children.Add(title);

        // Which version this column is, or why it has none. A column with no values under it has to
        // say so in its own header: the cells below can only show that they are unknown, not why.
        var subtitle = new TextBlock
        {
            FontSize = 10,
            FontWeight = FontWeights.Normal,
        };

        if (document is not null)
        {
            subtitle.Text = document.Writable ? document.SourceLine : document.SourceLine + "  (read-only)";
            subtitle.ToolTip = document.SourceDetail;

            // A resource reference rather than a resolved brush: this header is built in code, and
            // a brush captured here would keep the old theme's colour after a switch.
            subtitle.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextTertiary");
        }
        else
        {
            subtitle.Text = "UNAVAILABLE";
            subtitle.ToolTip = unavailable is null
                ? "Vault could not be read for this tier."
                : $"Vault could not be read for this tier:\r\n\r\n{unavailable}\r\n\r\nThere is no local copy to " +
                  "fall back on — nothing is kept on disk — so this column has no values and takes no part " +
                  "in the comparison.";

            subtitle.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Missing");
        }

        panel.Children.Add(subtitle);

        // A third line rather than a longer second one. The subtitle is already a version and a
        // timestamp, and the age is the part that is read at a glance while scanning four columns —
        // appending it would bury it at the end of the line most likely to be trimmed.
        if (document?.SourceAge is { Length: > 0 } age)
        {
            var elapsed = new TextBlock
            {
                Text = age,
                FontSize = 10,
                FontWeight = FontWeights.Normal,
            };

            elapsed.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextTertiary");
            panel.Children.Add(elapsed);
        }

        return panel;
    }

    /// <summary>
    /// A ContentPresenter bound to one cell of the row. The implicit DataTemplate for MultiCell in
    /// Controls.xaml then supplies the colouring, so the styling is not duplicated per column.
    /// </summary>
    private static DataTemplate BuildCellTemplate(int index)
    {
        const string ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var xaml =
            $"<DataTemplate xmlns=\"{ns}\">" +
            $"<ContentPresenter Content=\"{{Binding Cells[{index}]}}\" />" +
            "</DataTemplate>";

        return (DataTemplate)XamlReader.Parse(xaml);
    }

    private void OnGridDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is TiersVm vm && vm.SelectedRow is { IsGroup: true } row)
        {
            vm.ToggleAny(row);
        }
    }

    private void OnPromoteClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TierRowVm row })
        {
            PromoteRequested?.Invoke(this, row);
        }
    }

    private void OnEditRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TierRowVm row })
        {
            EditRequested?.Invoke(this, row.LeafPaths);
        }
    }

    private void OnReviewChangesClick(object sender, RoutedEventArgs e) =>
        ReviewChangesRequested?.Invoke(this, EventArgs.Empty);

    public event EventHandler<TierRowVm>? PromoteRequested;

    public event EventHandler<IReadOnlyList<string>>? EditRequested;

    public event EventHandler? ReviewChangesRequested;
}
