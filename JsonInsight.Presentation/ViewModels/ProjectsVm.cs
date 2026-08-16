using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JsonInsight.Model;
using JsonInsight.Sources;
using JsonInsight.Vault;

namespace JsonInsight.ViewModels;

/// <summary>One project on the projects screen.</summary>
public sealed partial class ProjectRowVm : ObservableObject
{
    public string Name { get; }

    /// <summary>
    /// The line under the name: what it compares, across how many sources, and when it was last
    /// opened. One string rather than three <c>Run</c>s, because WPF inserts whitespace between
    /// adjacent runs and the separators end up doubled.
    /// </summary>
    public string Summary { get; }

    /// <summary>What the row asks before the second press of Delete. See <see cref="ProjectsVm.Delete"/>.</summary>
    public string DeletePrompt { get; }

    /// <summary>True for the project that is open behind this screen, so it is marked rather than repeated.</summary>
    public bool IsActive { get; }

    /// <summary>The name box is showing instead of the name, because Rename was pressed.</summary>
    [ObservableProperty]
    private bool _renaming;

    /// <summary>The row is asking whether Delete was meant. See <see cref="ProjectsVm.DeleteCommand"/>.</summary>
    [ObservableProperty]
    private bool _confirmingDelete;

    [ObservableProperty]
    private string _newName = string.Empty;

    public ProjectRowVm(string name, SourceProject project, bool isActive)
    {
        Name = name;
        IsActive = isActive;
        _newName = name;

        var sources = project.ActiveSources.Count switch
        {
            0 when project.Connections.Count == 0 => "no sources yet",
            0 => $"{project.Connections.Count} configured, none chosen — config/tiers.json decides",
            1 => "1 source",
            var count => $"{count} sources",
        };

        Summary = $"{project.Describe()}  ·  {sources}  ·  {ProjectsVm.Ago(project.LastOpenedUtc)}";

        DeletePrompt = $"Delete {name}? Its source paths and its saved tokens go with it, and this app " +
                       "cannot get either back.";
    }
}

/// <summary>
/// The projects screen: what this app opens on, and the way between one piece of work and another.
///
/// <para>
/// A project is a set of sources — one whole path per environment — so switching project changes
/// which secrets get read and on which servers. That is why this is a screen rather than a dropdown,
/// and why the app stops here on launch: the read that used to happen unasked at startup now happens
/// against sources somebody has just pointed at.
/// </para>
/// </summary>
public sealed partial class ProjectsVm : ObservableObject
{
    private readonly MainVm _main;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>The name being typed into the New project box.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private string _newProjectName = string.Empty;

    /// <summary>
    /// Whether there is a name to create a project under, which is the whole of what Create needs.
    ///
    /// <para>
    /// A project <em>is</em> its name here — it is the key its sources are filed under and the key its
    /// tokens are keyed by — so with the box empty there is nothing for Create to make. The button says
    /// that by being unpressable, rather than by being pressed and answering with a status line, which
    /// is the difference between a control that describes the state and one that reports on it
    /// afterwards.
    /// </para>
    /// </summary>
    public bool CanCreate => NewProjectName.Trim().Length > 0;

    /// <summary>
    /// Which project a new one copies its sources from, or null to start empty.
    ///
    /// <para>
    /// Offered because the common second project is the same environments one path lower —
    /// <c>config.json</c> where <c>ui.json</c> was — on the same servers with the same tokens. Since
    /// every row now carries its own address and token, copying is the difference between editing
    /// four paths and filling in four rows from scratch.
    /// </para>
    ///
    /// <para>
    /// A copy takes the whole of every source: its kind, its server, its namespace, its token, its
    /// secret path or file, its TLS opt-ins and its restart endpoint. See
    /// <see cref="SourceProject.Clone"/> — the copy is deep, so the two projects diverge from that
    /// moment rather than sharing a connection object.
    /// </para>
    /// </summary>
    [ObservableProperty]
    private string? _copySourcesFrom;

    [ObservableProperty]
    private bool _alwaysOpenLastProject;

    public ObservableCollection<ProjectRowVm> Items { get; } = [];

    /// <summary>The names a new project can copy from, plus the "start empty" entry at the top.</summary>
    public ObservableCollection<string> CopyChoices { get; } = [];

    public const string StartEmpty = "Start empty";

    public ProjectsVm(MainVm main)
    {
        _main = main;
        Refresh();
    }

    public void Refresh()
    {
        var (workspace, problems) = VaultSettingsStore.LoadWorkspace();
        Populate(workspace);

        if (problems.Count > 0)
        {
            Status = string.Join(" ", problems);
        }
    }

    /// <summary>
    /// Fills the screen from a workspace the caller supplies rather than from the settings files.
    ///
    /// <para>
    /// The app never calls this; the test harness does, for the same reason <see cref="MainVm.Seed"/>
    /// exists. <see cref="AppPaths.AppSettingsFile"/> resolves to the authored appsettings.json even
    /// from a test run, so a test that created a project to look at would create it in the developer's
    /// real settings, next to their live tokens.
    /// </para>
    /// </summary>
    internal void Seed(VaultWorkspace workspace) => Populate(workspace);

    private void Populate(VaultWorkspace workspace)
    {
        Items.Clear();

        // Most recently opened first; a project created but never opened sorts last, since a project
        // nobody has opened is not recent.
        var ordered = workspace.Projects
            .OrderByDescending(p => p.Value.LastOpenedUtc ?? DateTimeOffset.MinValue)
            .ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var (name, project) in ordered)
        {
            Items.Add(new ProjectRowVm(name, project, name.Equals(_main.ActiveProject, StringComparison.OrdinalIgnoreCase)));
        }

        CopyChoices.Clear();
        CopyChoices.Add(StartEmpty);
        foreach (var row in Items)
        {
            CopyChoices.Add(row.Name);
        }

        CopySourcesFrom ??= StartEmpty;

        // Showing the saved value must not look like someone just ticked it — see the guard below.
        _loadingPreference = true;
        AlwaysOpenLastProject = workspace.AlwaysOpenLastProject;
        _loadingPreference = false;
    }

    [RelayCommand]
    private void Open(ProjectRowVm row) => _main.OpenProject(row.Name);

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void Create()
    {
        var name = NewProjectName.Trim();

        // Kept although CanCreate already refuses this: the Blazor screen commits on Enter as well as
        // on the button, and RelayCommand.Execute does not consult CanExecute. A keystroke that
        // arrives with the box empty gets the sentence rather than silence.
        if (name.Length == 0)
        {
            Status = "Give the project a name first.";
            return;
        }

        var (workspace, _) = VaultSettingsStore.LoadWorkspace();

        if (workspace.Projects.ContainsKey(name))
        {
            Status = $"There is already a project called '{name}'.";
            return;
        }

        // Copied rather than shared: two projects that start identical must be able to diverge, and a
        // shared connection object would mean editing stage in one of them edited it in both.
        //
        // The whole of each source comes across — its server, its token, its secret path and its
        // restart endpoint — because a copy exists to save filling the rows in again, and a half-copy
        // is worse than no copy: the missing half is the one nobody checks for.
        SourceProject? copy = null;
        string? copiedFrom = null;

        if (CopySourcesFrom is { } from && !from.Equals(StartEmpty, StringComparison.Ordinal) &&
            workspace.Projects.TryGetValue(from, out var source))
        {
            copy = source.Clone();
            copiedFrom = from;
        }

        var seed = copy ?? new SourceProject();

        // Whatever it copied, it has not been opened and it is its own document from here on.
        seed.LastOpenedUtc = null;

        workspace.Projects[name] = seed;

        try
        {
            VaultSettingsStore.SaveWorkspace(workspace);
        }
        catch (Exception ex)
        {
            Status = $"Could not create '{name}': {ex.Message}";
            return;
        }

        NewProjectName = string.Empty;
        Refresh();

        Status = copiedFrom is null
            ? $"Created '{name}'. Open it, then set where its sources come from on the Sources tab."
            : $"Created '{name}' from '{copiedFrom}' — {WhatCameAcross(seed)}. Open it and point its " +
              "sources at the new document.";
    }

    /// <summary>
    /// What a copy brought with it, in the terms worth checking before trusting it.
    ///
    /// <para>
    /// The restart endpoints are counted out loud rather than left implied. Everything else a source
    /// carries is visible on the Sources tab the moment the copy is opened; the restart endpoint is
    /// configured behind a dialog and shows up nowhere until you go looking, so it is the one part of
    /// a copy whose absence would be noticed late — after a push, by the thing that did not restart.
    /// </para>
    /// </summary>
    private static string WhatCameAcross(SourceProject project)
    {
        var sources = project.Connections.Count == 1 ? "1 source" : $"{project.Connections.Count} sources";
        var restarts = project.Connections.Values.Count(c => !string.IsNullOrWhiteSpace(c.RestartUrl));

        return restarts switch
        {
            0 => $"{sources} copied, tokens included",
            1 => $"{sources} copied, tokens and 1 restart endpoint included",
            _ => $"{sources} copied, tokens and {restarts} restart endpoints included",
        };
    }

    [RelayCommand]
    private void StartRename(ProjectRowVm row)
    {
        row.NewName = row.Name;
        row.ConfirmingDelete = false;
        row.Renaming = true;
    }

    [RelayCommand]
    private void CancelRename(ProjectRowVm row) => row.Renaming = false;

    [RelayCommand]
    private void CancelDelete(ProjectRowVm row) => row.ConfirmingDelete = false;

    [RelayCommand]
    private void CommitRename(ProjectRowVm row)
    {
        var name = row.NewName.Trim();

        if (name.Length == 0 || name.Equals(row.Name, StringComparison.Ordinal))
        {
            row.Renaming = false;
            return;
        }

        var (workspace, _) = VaultSettingsStore.LoadWorkspace();

        if (workspace.Projects.ContainsKey(name))
        {
            Status = $"There is already a project called '{name}'.";
            return;
        }

        if (!workspace.Projects.Remove(row.Name, out var project))
        {
            Status = $"'{row.Name}' is no longer there.";
            Refresh();
            return;
        }

        workspace.Projects[name] = project;

        // The tokens are keyed by project name, so a rename has to carry them across or every source
        // in the project silently loses its credentials.
        if (workspace.ActiveProject.Equals(row.Name, StringComparison.OrdinalIgnoreCase))
        {
            workspace.ActiveProject = name;
        }

        try
        {
            VaultSettingsStore.SaveWorkspace(workspace);
        }
        catch (Exception ex)
        {
            Status = $"Could not rename '{row.Name}': {ex.Message}";
            return;
        }

        if (_main.ActiveProject.Equals(row.Name, StringComparison.OrdinalIgnoreCase))
        {
            _main.ActiveProject = name;
        }

        Refresh();
        Status = $"'{row.Name}' is now '{name}'.";
    }

    /// <summary>
    /// Two presses, deliberately. A project holds every source path and every token for a piece of
    /// work, none of which this app can get back, and the button sits next to Open on a list of rows
    /// that all look alike.
    /// </summary>
    [RelayCommand]
    private void Delete(ProjectRowVm row)
    {
        if (!row.ConfirmingDelete)
        {
            foreach (var other in Items)
            {
                other.ConfirmingDelete = false;
            }

            row.Renaming = false;
            row.ConfirmingDelete = true;
            return;
        }

        var (workspace, _) = VaultSettingsStore.LoadWorkspace();

        if (!workspace.Projects.Remove(row.Name))
        {
            Refresh();
            return;
        }

        if (workspace.ActiveProject.Equals(row.Name, StringComparison.OrdinalIgnoreCase))
        {
            workspace.ActiveProject = string.Empty;
        }

        try
        {
            // SaveWorkspace prunes the tokens of every project it no longer holds, so this takes the
            // deleted project's credentials out of user secrets with it.
            VaultSettingsStore.SaveWorkspace(workspace);
        }
        catch (Exception ex)
        {
            Status = $"Could not delete '{row.Name}': {ex.Message}";
            return;
        }

        if (_main.ActiveProject.Equals(row.Name, StringComparison.OrdinalIgnoreCase))
        {
            _main.CloseProject();
        }

        Refresh();
        Status = $"Deleted '{row.Name}' and its saved tokens.";
    }

    /// <summary>True while <see cref="Refresh"/> is putting the saved value on screen rather than taking one off it.</summary>
    private bool _loadingPreference;

    /// <summary>
    /// Saved the moment it is ticked. It is one checkbox with no Save beside it, and a preference that
    /// needed a second button to take effect would be a preference that silently did not.
    /// </summary>
    partial void OnAlwaysOpenLastProjectChanged(bool value)
    {
        if (_loadingPreference)
        {
            return;
        }

        var (workspace, _) = VaultSettingsStore.LoadWorkspace();
        if (workspace.AlwaysOpenLastProject == value)
        {
            return;
        }

        workspace.AlwaysOpenLastProject = value;

        try
        {
            VaultSettingsStore.SaveWorkspace(workspace);
        }
        catch (Exception ex)
        {
            Status = $"Could not save that preference: {ex.Message}";
            return;
        }

        Status = value
            ? "This app will reopen the last project instead of stopping here."
            : "This app will stop on this screen when it opens.";
    }

    /// <summary>
    /// How long ago, in the words someone would use. Exact timestamps are worse here: the question
    /// this column answers is "which of these was I in", and "yesterday" answers it faster than a date.
    /// </summary>
    public static string Ago(DateTimeOffset? when)
    {
        if (when is not { } stamp)
        {
            return "never opened";
        }

        var elapsed = DateTimeOffset.UtcNow - stamp;

        return elapsed switch
        {
            { TotalSeconds: < 90 } => "just now",
            { TotalMinutes: < 60 } => $"{(int)elapsed.TotalMinutes} minutes ago",
            { TotalHours: < 2 } => "an hour ago",
            { TotalHours: < 24 } => $"{(int)elapsed.TotalHours} hours ago",
            { TotalDays: < 2 } => "yesterday",
            { TotalDays: < 30 } => $"{(int)elapsed.TotalDays} days ago",
            _ => stamp.ToLocalTime().ToString("d MMM yyyy"),
        };
    }
}
