using System.Diagnostics;
using System.Windows;
using DiffPlex.DiffBuilder.Model;
using System.Windows.Controls;
using System.Windows.Threading;
using JsonInsight.Editing;
using JsonInsight.Loading;
using JsonInsight.Platform;
using JsonInsight.Sources;
using JsonInsight.Themes;
using JsonInsight.Vault;
using JsonInsight.ViewModels;
using JsonInsight.Views;

namespace JsonInsight.Tests;

/// <summary>
/// Loads every view against real view models on an STA thread and fails on any binding error.
///
/// XAML faults - a missing StaticResource, a mistyped binding path, a converter that throws - only
/// surface at runtime, so a green compile proves nothing about whether a tab actually renders.
/// This runs the same construction the app does and turns WPF's binding trace into assertions.
/// </summary>
public sealed class UiSmokeTests
{
    [Fact]
    public void Every_view_constructs_and_binds_without_errors()
    {
        var errors = RunOnStaThread(() =>
        {
            var app = EnsureApplication();
            var listener = new BindingErrorListener();

            using (listener.Capture())
            {
                var main = NewMainVm();

                Assert.NotNull(main.Tiers);
                Assert.NotNull(main.RawDiff);
                Assert.NotNull(main.JsonCompare);
                Assert.NotNull(main.JsonEditor);
                Assert.NotNull(main.Vault);
                Assert.NotNull(main.Projects);
                Assert.Empty(main.Problems);

                Render(new TiersView { DataContext = main.Tiers });
                Render(new RawDiffView { DataContext = main.RawDiff });
                Render(new JsonEditorView { DataContext = main.JsonEditor });
                Render(new JsonCompareView { DataContext = main.JsonCompare });
                Render(new VaultView { DataContext = main.Vault });
                Render(new ProjectsView { DataContext = main.Projects });
            }

            return listener.Errors;
        });

        Assert.True(errors.Count == 0,
            "WPF reported binding errors:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    /// <summary>
    /// The projects screen with rows actually realised, and in each of the three states a row has —
    /// resting, renaming, and asking whether Delete was meant.
    ///
    /// <para>
    /// Rendering it against the real settings would show an empty list on most machines, which is the
    /// one arrangement where none of the row template runs: every binding that could be wrong lives
    /// inside it. Seeded for the same reason the tier tabs are.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public void Projects_screen_renders_every_row_state(AppTheme theme)
    {
        var errors = RunOnStaThread(() =>
        {
            EnsureApplication();
            ThemeManager.Apply(theme);

            var listener = new BindingErrorListener();
            using (listener.Capture())
            {
                var main = new MainVm(vaultAtStartup: false);
                var projects = main.Projects!;
                projects.Seed(Workspace());

                Assert.Equal(3, projects.Items.Count);

                projects.Items[1].Renaming = true;
                projects.Items[2].ConfirmingDelete = true;

                Render(new ProjectsView { DataContext = projects });
            }

            return listener.Errors;
        });

        Assert.True(errors.Count == 0,
            "WPF reported binding errors:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    /// <summary>Three projects of the shape the feature is for: one root, two documents beneath it.</summary>
    private static VaultWorkspace Workspace() => new()
    {
        Address = "https://vault.example.com",
        ActiveProject = "appsettings",
        Projects =
        {
            ["appsettings"] = new SourceProject
            {
                ActiveSources = ["dev", "stage", "beta", "prod"],
                Connections =
                {
                    ["dev"] = new VaultConnection { Kind = SourceKind.LocalFile, LocalFilePath = @"C:\snapshots\dev.json" },
                    ["stage"] = new VaultConnection { SecretPath = "kv/app/stage" },
                    ["beta"] = new VaultConnection { SecretPath = "kv/app/beta" },
                    ["prod"] = new VaultConnection { SecretPath = "kv/app/prod" },
                },
                LastOpenedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(3),
            },
            ["config"] = new SourceProject
            {
                ActiveSources = ["stage", "prod"],
                Connections =
                {
                    ["stage"] = new VaultConnection { SecretPath = "kv/app/stage/resources/config/config.json" },
                    ["prod"] = new VaultConnection { SecretPath = "kv/app/prod/resources/config/config.json" },
                },
                LastOpenedUtc = DateTimeOffset.UtcNow - TimeSpan.FromHours(30),
            },
            ["ui"] = new SourceProject
            {
                Connections =
                {
                    ["stage"] = new VaultConnection { SecretPath = "kv/app/stage/resources/config/ui.json" },
                },
            },
        },
    };

    /// <summary>
    /// The edit dialog, rendered against a real multi-tier key, in both themes.
    ///
    /// It is the most template-heavy screen in the app - three combo boxes and a text box inside
    /// DataGrid cell templates - and every one of those only fails once a row is realised, which is
    /// exactly the gap that had been hiding a crash in the promote dialog's Action dropdown.
    /// </summary>
    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public void Edit_dialog_renders_against_a_real_key(AppTheme theme)
    {
        var errors = RunOnStaThread(() =>
        {
            EnsureApplication();
            ThemeManager.Apply(theme);

            var listener = new BindingErrorListener();
            using (listener.Capture())
            {
                var main = NewMainVm();
                var vm = new EditVm(main, ["ConnectionStrings:Couchbase:Modules:Auth:Url"]);

                // One row per tier, whether or not the tier has the key: a key present in exactly
                // one tier is the drift the tool exists to find, so every tier is always offered.
                Assert.Equal(main.Documents.Count, vm.Rows.Count);

                vm.BulkValue = "couchbase://10.0.0.34,10.0.0.35";
                vm.ApplyToAllCommand.Execute(null);
                Assert.True(vm.CanQueue);

                Render(new EditDialog(vm));
            }

            return listener.Errors;
        });

        Assert.True(errors.Count == 0,
            $"WPF reported binding errors in the {theme} theme:" +
            Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    /// <summary>
    /// The JSON editor tab, driven through a real replace-undo-compare cycle before it is rendered.
    ///
    /// Selecting a node is what loads the editor pane, and the pane switches between a text box and
    /// a diff list on the same grid cell — neither of which can misbehave until a node has actually
    /// been selected and the list has rows to realise.
    /// </summary>
    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public void Json_editor_renders_through_a_real_edit_cycle(AppTheme theme)
    {
        var errors = RunOnStaThread(() =>
        {
            EnsureApplication();
            ThemeManager.Apply(theme);

            var listener = new BindingErrorListener();
            using (listener.Capture())
            {
                var main = NewMainVm();
                var vm = main.JsonEditor!;

                // Opens on a writable tier, never on dev, which this app must never write.
                Assert.NotNull(vm.Tier);
                Assert.True(vm.Tier!.Writable);
                Assert.NotEmpty(vm.Nodes);

                // And with nothing selected: the first row is the whole document, and opening onto
                // it would fill the pane before anyone had asked for anything.
                Assert.Null(vm.SelectedNode);
                Assert.Empty(vm.EditorText);
                Assert.False(vm.CanApply);

                // Selecting a node shows that node's subtree and nothing else.
                vm.SelectedNode = vm.Nodes.First(n => n.Path == "AccountSettings");
                Assert.False(vm.IsEditorReadOnly);
                Assert.Contains("ProxyUrl", vm.EditorText, StringComparison.Ordinal);
                Assert.DoesNotContain("ConnectionStrings", vm.EditorText, StringComparison.Ordinal);

                // The pane matches the document, so there is nothing to update yet.
                Assert.False(vm.CanApply);
                Assert.False(vm.CanRevertNode);

                // Reformatting is not an edit: the same JSON on one line still has nothing to apply.
                vm.CompactJson = true;
                Assert.DoesNotContain("\r\n", vm.EditorText, StringComparison.Ordinal);
                Assert.False(vm.CanApply);

                vm.CompactJson = false;

                vm.EditorText = """{ "ProxyUrl": "http://example.invalid:443" }""";
                Assert.True(vm.CanApply);

                vm.ApplyCommand.Execute(null);

                Assert.Null(vm.Error);
                Assert.True(vm.IsModified);
                Assert.True(vm.CanUndo);

                // Applied, so the pane matches again and Update goes quiet until something else is
                // typed. The node now has changes of its own to throw away.
                Assert.False(vm.CanApply);
                Assert.True(vm.CanRevertNode);

                // The edit is discernible in the tree: the node itself and every section above it.
                Assert.True(vm.Nodes.Single(n => n.Path == "AccountSettings").IsChanged);
                Assert.True(vm.Nodes.Single(n => n.Path == string.Empty).IsChanged);
                Assert.False(vm.Nodes.Single(n => n.Path == "ConnectionStrings").IsChanged);

                // And it says which kind of change each one is, because "changed" is three different
                // things: that replacement retyped ProxyUrl and dropped everything beside it, under
                // a section that is now holding both at once.
                Assert.Equal(NodeChange.Edited, vm.Nodes.Single(n => n.Path == "AccountSettings:ProxyUrl").Change);
                Assert.Equal(NodeChange.Removed, vm.Nodes.Single(n => n.Path == "AccountSettings:Transfer").Change);
                Assert.Equal(NodeChange.Mixed, vm.Nodes.Single(n => n.Path == "AccountSettings").Change);
                Assert.Equal(NodeChange.Mixed, vm.Nodes.Single(n => n.Path == string.Empty).Change);
                Assert.Equal(NodeChange.None, vm.Nodes.Single(n => n.Path == "ConnectionStrings").Change);

                // A removal stays in the tree as a tombstone until the file is saved, so the one
                // edit you otherwise could not see is also the one you can still take back.
                vm.SelectedNode = vm.Nodes.First(n => n.Path == "Elasticsearch");
                vm.RemoveNodeCommand.Execute(null);
                Assert.Null(vm.Error);

                var tombstone = vm.Nodes.Single(n => n.Path == "Elasticsearch");
                Assert.True(tombstone.IsRemoved);
                Assert.True(tombstone.IsChanged);

                // Its children are shown too, from the document as opened.
                Assert.Contains(vm.Nodes, n => n.Path == "Elasticsearch:Url" && n.IsRemoved);

                // Selecting it shows what it held, read-only, and offers to put it back rather
                // than to remove it again.
                vm.SelectedNode = tombstone;
                Assert.True(vm.SelectedIsRemoved);
                Assert.True(vm.IsEditorReadOnly);
                Assert.False(vm.CanRemoveNode);
                Assert.False(vm.CanApply);
                Assert.True(vm.CanRevertNode);
                Assert.Equal("Restore node", vm.RevertNodeLabel);
                Assert.Contains("Url", vm.EditorText, StringComparison.Ordinal);

                // The comparison is scoped to the selection, so on a tombstone it is that subtree
                // and nothing else: every line deleted, with real text on the left and nothing on
                // the right. A one-sided view rendered those as uncoloured blank rows.
                Assert.True(vm.CanCompare);
                vm.ToggleComparisonCommand.Execute(null);

                Assert.NotEmpty(vm.ComparisonLines);
                Assert.All(vm.ComparisonLines, l =>
                {
                    Assert.Equal(ChangeType.Deleted, l.Type);
                    Assert.NotEmpty(l.LeftText);
                    Assert.Empty(l.RightText);
                });
                Assert.Contains(vm.ComparisonLines, l => l.LeftText.Contains("Url", StringComparison.Ordinal));

                // Moving the selection moves the comparison with it, rather than leaving the last
                // node's diff on screen. The root row is the whole document, which is where the
                // unrelated AccountSettings edit shows up - old text alongside new.
                vm.SelectedNode = vm.Nodes.Single(n => n.Path == string.Empty);

                Assert.True(vm.ShowingComparison);
                Assert.Contains(vm.ComparisonLines,
                    l => l.Type == ChangeType.Modified && l.LeftText.Length > 0 && l.RightText.Length > 0);
                Assert.Contains(vm.ComparisonLines,
                    l => l.Type == ChangeType.Deleted &&
                         l.LeftText.Contains("Elasticsearch", StringComparison.Ordinal));

                // A node with nothing to compare has nothing to show, so the mode is left rather
                // than parking an empty diff behind a button that is now disabled.
                vm.SelectedNode = vm.Nodes.First(n => n.Path == "ConnectionStrings");

                Assert.False(vm.CanCompare);
                Assert.False(vm.ShowingComparison);
                Assert.Empty(vm.ComparisonLines);

                // Un-removing from *inside* a deleted section restores the whole section, not a
                // fragment of it: recreating Elasticsearch holding nothing but Url would be a
                // partial restore nobody asked for.
                vm.SelectedNode = vm.Nodes.Single(n => n.Path == "Elasticsearch:Url");
                Assert.Equal("Restore node", vm.RevertNodeLabel);
                vm.RevertNodeCommand.Execute(null);

                Assert.Null(vm.Error);
                Assert.False(vm.Nodes.Single(n => n.Path == "Elasticsearch").IsRemoved);
                Assert.False(vm.Nodes.Single(n => n.Path == "Elasticsearch:Url").IsRemoved);
                Assert.Equal(
                    main.Documents.Single(d => d.Id == vm.Tier!.Id).Flat.Paths
                        .Count(p => p.StartsWith("Elasticsearch:", StringComparison.Ordinal)),
                    vm.Nodes.Count(n => n.Path.StartsWith("Elasticsearch:", StringComparison.Ordinal)));

                // And the unrelated AccountSettings edit is still standing, which is what separates
                // this from Undo.
                Assert.True(vm.Nodes.Single(n => n.Path == "AccountSettings").IsChanged);

                // Remove it again so the "changed only" checks below have a tombstone to include.
                vm.SelectedNode = vm.Nodes.Single(n => n.Path == "Elasticsearch");
                Assert.True(vm.CanRemoveNode);
                vm.RemoveNodeCommand.Execute(null);
                Assert.True(vm.Nodes.Single(n => n.Path == "Elasticsearch").IsRemoved);

                // "Changed only" narrows the tree to what has been edited, keeping the sections
                // above it so the tree still navigates.
                var everything = vm.Nodes.Count;
                vm.ShowChangedOnly = true;

                Assert.True(vm.Nodes.Count < everything);
                Assert.All(vm.Nodes, n => Assert.True(n.IsChanged, n.Path));
                Assert.Contains(vm.Nodes, n => n.Path == "AccountSettings");
                Assert.DoesNotContain(vm.Nodes, n => n.Path == "ConnectionStrings");

                // It composes with the search rather than replacing it.
                vm.Filter = "ConnectionStrings";
                Assert.Equal("Nothing matches.", vm.FilterHint);

                vm.Filter = string.Empty;
                vm.ShowChangedOnly = false;
                Assert.Equal(everything, vm.Nodes.Count);

                // Compare is a mode: on, then off, then on again.
                vm.ToggleComparisonCommand.Execute(null);
                Assert.True(vm.ShowingComparison);
                Assert.NotEmpty(vm.ComparisonLines);

                vm.ToggleComparisonCommand.Execute(null);
                Assert.False(vm.ShowingComparison);

                vm.ToggleComparisonCommand.Execute(null);
                Assert.True(vm.ShowingComparison);
                Assert.NotEmpty(vm.ComparisonLines);

                // There is something to push, and the hint says what it would do.
                Assert.True(vm.IsModified);
                Assert.NotEmpty(vm.PushHint);

                Render(new JsonEditorView { DataContext = vm });
            }

            return listener.Errors;
        });

        Assert.True(errors.Count == 0,
            $"WPF reported binding errors in the {theme} theme:" +
            Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    /// <summary>Queues a batch, then renders the review screen that would commit it.</summary>
    [Fact]
    public void Changes_dialog_renders_a_queued_batch()
    {
        var errors = RunOnStaThread(() =>
        {
            EnsureApplication();
            var listener = new BindingErrorListener();

            using (listener.Capture())
            {
                var main = NewMainVm();

                var edit = new EditVm(main, ["ConnectionStrings:Couchbase:Modules:Auth:Url"]);
                edit.BulkValue = "couchbase://10.0.0.34,10.0.0.35";
                edit.ApplyToAllCommand.Execute(null);
                edit.QueueCommand.Execute(null);

                Assert.False(main.Edits.IsEmpty);

                var vm = new ChangesVm(main);
                Assert.NotNull(vm.Tier);
                Assert.NotEmpty(vm.Changes);

                // The batch is ready to hand to the push screen, and the preview is a real diff of
                // the document it would produce.
                Assert.True(vm.CanPush);
                vm.PreviewCommand.Execute(null);
                Assert.NotEmpty(vm.PreviewLines);
                Assert.NotNull(vm.BuildUpdated());

                Render(new ChangesDialog(vm));
            }

            return listener.Errors;
        });

        Assert.True(errors.Count == 0,
            "WPF reported binding errors:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    /// <summary>
    /// Renders every view in both themes, with the grids actually realised.
    ///
    /// Two distinct faults hide here. A colour that a style forgot to reach through
    /// DynamicResource looks fine in whichever theme loaded first and wrong in the other; and a
    /// control template that drops a part - a ComboBox that loses DisplayMemberPath, say - only
    /// misbehaves once a row exists to render, which measuring an empty grid never forces.
    /// </summary>
    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public void Every_view_renders_in_either_theme(AppTheme theme)
    {
        var errors = RunOnStaThread(() =>
        {
            EnsureApplication();
            ThemeManager.Apply(theme);

            var listener = new BindingErrorListener();
            using (listener.Capture())
            {
                var main = NewMainVm();

                Render(new TiersView { DataContext = main.Tiers });
                Render(new RawDiffView { DataContext = main.RawDiff });
                Render(new JsonEditorView { DataContext = main.JsonEditor });
                Render(new JsonCompareView { DataContext = main.JsonCompare });
                Render(new VaultView { DataContext = main.Vault });
                Render(new ProjectsView { DataContext = main.Projects });
            }

            return listener.Errors;
        });

        Assert.True(errors.Count == 0,
            $"WPF reported binding errors in the {theme} theme:" +
            Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    /// <summary>
    /// Every brush key one theme defines, the other must define too. A key present in only one of
    /// them resolves to nothing after a switch, and the control silently loses its colour.
    /// </summary>
    [Fact]
    public void Both_themes_define_the_same_brushes()
    {
        var (dark, light) = RunOnStaThread(() =>
        {
            EnsureApplication();
            return (Keys("Dark"), Keys("Light"));

            static HashSet<string> Keys(string theme)
            {
                var dictionary = new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/JsonInsight;component/Themes/{theme}.xaml"),
                };

                return dictionary.Keys.Cast<object>().Select(k => k.ToString()!).ToHashSet(StringComparer.Ordinal);
            }
        });

        Assert.Empty(dark.Except(light));
        Assert.Empty(light.Except(dark));
        Assert.NotEmpty(dark);
    }

    [Fact]
    public void Promote_dialog_builds_for_a_real_rolled_up_subtree()
    {
        var errors = RunOnStaThread(() =>
        {
            EnsureApplication();
            var listener = new BindingErrorListener();

            using (listener.Capture())
            {
                var main = NewMainVm();
                var dev = main.Documents.Single(d => d.Id == "dev");

                var vm = new PromoteVm(main, main.Flattener, dev,
                    "AccountSettings:NightlyApprovalJob", ["stage", "beta"]);

                Assert.Equal(11, vm.Leaves.Count);
                Assert.Equal(2, vm.Destinations.Count);

                Assert.Contains(vm.Destinations, d => d.Id == "stage");

                // The plan is ready to hand to the push screen, and the preview is a real diff of
                // the document it would produce.
                Assert.True(vm.CanPush);
                vm.PreviewCommand.Execute(null);
                Assert.NotEmpty(vm.PreviewLines);
                Assert.NotNull(vm.BuildUpdated());

                Render(new PromoteDialog(vm));
            }

            return listener.Errors;
        });

        Assert.True(errors.Count == 0,
            "WPF reported binding errors:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    /// <summary>
    /// The push dialog, rendered with real diff rows in it and with the live read switched off.
    ///
    /// <para>
    /// Switched off deliberately: this screen reads Vault as it opens, and a rendering check that
    /// quietly reached production Vault - let alone one whose next button uploads to it - would be
    /// worse than no check at all. The rows are supplied here instead, because a diff template that
    /// has never realised a row has never been tested.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public void Push_dialog_renders_without_contacting_vault(AppTheme theme)
    {
        var errors = RunOnStaThread(() =>
        {
            EnsureApplication();
            ThemeManager.Apply(theme);

            var listener = new BindingErrorListener();
            using (listener.Capture())
            {
                var main = NewMainVm();
                var vm = new PushVm(main, main.Documents.First(d => d.Writable), checksOnOpen: false);

                Assert.False(vm.ChecksOnOpen);
                Assert.NotEmpty(vm.Tiers);
                Assert.DoesNotContain(vm.Tiers, t => !t.Writable);

                // Nothing has been read, so there is nothing to confirm - typing the tier name is
                // not enough on its own, because the check-and-set version comes from that read.
                vm.ConfirmText = vm.Tier!.Id;
                Assert.True(vm.ConfirmMatches);
                Assert.False(vm.CanPush);

                vm.Lines.Add(new DiffLineVm("12", "\"Url\": \"redis://old\"", "12", "\"Url\": \"redis://new\"",
                    ChangeType.Modified));
                vm.Lines.Add(new DiffLineVm("13", "\"Gone\": 1", string.Empty, string.Empty, ChangeType.Deleted));
                vm.Warnings.Add("A warning, so the banner is realised too.");
                vm.Notes.Add("A note, likewise.");

                Render(new PushDialog(vm));
            }

            return listener.Errors;
        });

        Assert.True(errors.Count == 0,
            $"WPF reported binding errors in the {theme} theme:" +
            Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    /// <summary>
    /// The two restart windows, in both themes and in the states that only exist once something has
    /// happened — a body that does not parse, a call that has reported back.
    ///
    /// <para>
    /// Rendered with <c>live: false</c>, so nothing is sent. A smoke test that quietly restarted a
    /// production environment would be the worst one in this repository.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public void Restart_windows_render(AppTheme theme)
    {
        var errors = RunOnStaThread(() =>
        {
            EnsureApplication();
            ThemeManager.Apply(theme);

            var listener = new BindingErrorListener();
            using (listener.Capture())
            {
                var main = NewMainVm();
                var row = main.Vault!.Connections.First(c => c.Kind == SourceKind.Vault);

                // Unconfigured first: the Clear button hides itself and the warning banner does not.
                Assert.False(row.HasRestart);
                Render(new RestartConfigDialog(row));

                row.RestartUrl = "https://api.test/api/v1/dev/admin/restart?drainSeconds=15";
                row.RestartBody = "{ not json";
                row.RestartAllowInsecureTls = true;

                Assert.True(row.HasRestart);
                Assert.True(row.HasRestartBodyProblem);
                Render(new RestartConfigDialog(row));

                row.RestartBody = """{"drain":5}""";
                Assert.False(row.HasRestartBodyProblem);

                var vm = new RestartVm(row, live: false);
                Assert.False(vm.CanCall);

                vm.Token = "admin-token";
                Assert.True(vm.CanCall);

                vm.Notes.Add("POST https://api.test/api/v1/dev/admin/restart?drainSeconds=15");
                vm.Notes.Add("Restart accepted — HTTP 202.");

                Render(new RestartCallDialog(vm));
            }

            return listener.Errors;
        });

        Assert.True(errors.Count == 0,
            $"WPF reported binding errors in the {theme} theme:" +
            Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void Tier_grid_generates_one_column_per_configured_tier()
    {
        var columns = RunOnStaThread(() =>
        {
            EnsureApplication();
            var main = NewMainVm();
            var view = new TiersView { DataContext = main.Tiers };
            Render(view);

            var grid = (DataGrid)view.FindName("Grid")!;
            return grid.Columns.Select(c => c.Header?.ToString() ?? c.Header?.GetType().Name ?? "?").ToList();
        });

        // Path + one per tier + the promote button column. Derived from the config rather than
        // hardcoded, so adding a tier does not fail a test that is not about tier counts.
        Assert.Equal(2 + TiersConfig.Load().Tiers.Count, columns.Count);
    }

    /// <summary>
    /// The app reads every Vault-backed tier live when it opens. A test must not: it would depend on
    /// a network and a token, it would be slow, and a test suite that quietly reached production
    /// Vault would be considerably worse than one that failed. The local snapshots are what these
    /// tests are about anyway.
    /// </summary>
    /// <summary>
    /// A main view model with the startup Vault read switched off and the fixture payloads seeded in
    /// its place. A tier is a Vault secret now, so without the seed there would be nothing to render
    /// — and a rendering test that quietly reached production Vault would be worse than none.
    /// </summary>
    private static MainVm NewMainVm()
    {
        var main = new MainVm(vaultAtStartup: false);
        main.Seed(Fixtures.Documents);
        return main;
    }

    private static readonly SampleFiles Fixtures = new();

    /// <summary>
    /// Shows the element off-screen and lets the dispatcher drain until layout settles.
    ///
    /// Showing rather than only measuring is the point: an unshown window measures its grids but
    /// never realises their rows, and a cell template that throws or a control template that has
    /// dropped a part cannot fail until a row exists to render it. A Window is already the root of
    /// its own tree and cannot be hosted inside another, so it is shown directly.
    /// </summary>
    private static void Render(FrameworkElement element)
    {
        var isWindow = element is Window;
        var root = isWindow
            ? (Window)element
            : new Window
            {
                Content = element,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
            };

        root.Width = 1500;
        root.Height = 900;

        // Off-screen, so the run never puts a window in front of whoever is at the machine.
        root.Left = -20000;
        root.Top = -20000;

        root.Show();

        for (var i = 0; i < 3; i++)
        {
            root.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        }

        if (!isWindow)
        {
            root.Content = null;
        }

        root.Close();
    }

    private static Application EnsureApplication()
    {
        if (Application.Current is { } existing)
        {
            return existing;
        }

        var app = new Application
        {
            // Closing a test's off-screen window would otherwise count as the last window closing
            // and shut the Application down, leaving Application.Current null for the next test -
            // which then cannot create another one, because an AppDomain only gets one.
            ShutdownMode = ShutdownMode.OnExplicitShutdown,

            // The views resolve brushes and templates from here; without it every StaticResource
            // fails. Order matters and mirrors App.xaml: the colour dictionary is index 0 because
            // that is the slot ThemeManager swaps, and Controls.xaml resolves its colours through
            // DynamicResource against whatever sits there.
            Resources = new ResourceDictionary
            {
                MergedDictionaries =
                {
                    new ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/JsonInsight;component/Themes/Dark.xaml"),
                    },
                    new ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/JsonInsight;component/Themes/Controls.xaml"),
                    },
                },
            },
        };

        return app;
    }

    private static T RunOnStaThread<T>(Func<T> action)
    {
        T? result = default;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }

        return result!;
    }

    /// <summary>Turns WPF's data-binding trace output into a list of failures.</summary>
    private sealed class BindingErrorListener : TraceListener
    {
        public List<string> Errors { get; } = [];

        public IDisposable Capture()
        {
            PresentationTraceSources.Refresh();
            var source = PresentationTraceSources.DataBindingSource;
            source.Listeners.Add(this);
            source.Switch.Level = SourceLevels.Error | SourceLevels.Warning;

            return new Scope(() => source.Listeners.Remove(this));
        }

        public override void Write(string? message) => Record(message);

        public override void WriteLine(string? message) => Record(message);

        private void Record(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            // WPF reports a "cannot find governing FrameworkElement" warning for elements measured
            // outside a live visual tree; that is an artefact of the harness, not a real fault.
            if (message.Contains("governing FrameworkElement", StringComparison.Ordinal))
            {
                return;
            }

            if (message.Contains("Error:", StringComparison.Ordinal) ||
                message.Contains("cannot find", StringComparison.OrdinalIgnoreCase))
            {
                Errors.Add(message.Trim());
            }
        }

        private sealed class Scope(Action onDispose) : IDisposable
        {
            public void Dispose() => onDispose();
        }
    }
}
