using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JsonInsight.Classify;
using JsonInsight.Diff;
using JsonInsight.Editing;
using JsonInsight.Loading;
using JsonInsight.Model;
using JsonInsight.Sources;
using JsonInsight.Platform;
using JsonInsight.Vault;

namespace JsonInsight.ViewModels;

public sealed partial class MainVm : ObservableObject
{
    /// <summary>
    /// Set false by the test harness. Startup must not depend on a network, a token, or a Vault that
    /// happens to be up, and a test that quietly reached production Vault would be worse than one
    /// that failed. Null means "whatever appsettings.json says", which is what the app itself passes.
    /// </summary>
    private readonly bool? _vaultAtStartup;

    /// <summary>Problems from the file-load stage, kept so a Vault refresh can rebuild the list without losing them.</summary>
    private readonly List<string> _loadProblems = [];

    /// <summary>
    /// Always the root now, and kept only because <see cref="DocumentTiers"/> still takes one.
    ///
    /// <para>
    /// A tier used to be an environment root plus one app-wide document appended to it, and this was
    /// that document. Each source now names its own whole path — see
    /// <see cref="VaultConnection.SecretPath"/> — so there is nothing left to append and nothing to
    /// switch between. <see cref="DocumentTiers.For"/> returns the catalog untouched for the root,
    /// which is what makes leaving the call in place cost nothing.
    /// </para>
    /// </summary>
    private static readonly ConfigDocument Document = ConfigDocument.Root;

    /// <summary>
    /// Everything the app has said, kept rather than overwritten. See <see cref="LogVm"/> for why
    /// this is a tab and not the banner it replaced.
    /// </summary>
    public LogVm Log { get; } = new();

    [ObservableProperty]
    private string _status = "Loading…";

    /// <summary>
    /// True while any Vault read is in flight — the startup one included.
    ///
    /// <para>
    /// It lives here rather than on the All tiers tab because both reads have to raise it. The
    /// startup read went through this class directly and never touched the tab's own flag, so the
    /// one read nobody asked for was also the only one that gave no sign it was happening.
    /// </para>
    /// </summary>
    [ObservableProperty]
    private bool _vaultBusy;

    /// <summary>
    /// Where the tiers on screen came from, in one line, said whether or not anything went wrong.
    ///
    /// <para>
    /// Only failures used to be reported, which left "it worked" and "nothing happened yet" looking
    /// identical — and made the only visible Vault message a red one, at startup, about a read the
    /// user had not asked for.
    /// </para>
    /// </summary>
    [ObservableProperty]
    private string _vaultNote = string.Empty;

    [ObservableProperty]
    private bool _vaultNoteIsGood = true;

    [ObservableProperty]
    private TiersVm? _tiers;

    [ObservableProperty]
    private RawDiffVm? _rawDiff;

    [ObservableProperty]
    private JsonCompareVm? _jsonCompare;

    [ObservableProperty]
    private JsonEditorVm? _jsonEditor;

    [ObservableProperty]
    private VaultVm? _vault;

    /// <summary>
    /// Which project is open, or empty when the projects screen is showing instead of the tabs.
    ///
    /// <para>
    /// It is a name rather than an object because that is what everything else needs it for: the
    /// Sources tab saves <em>into</em> it, and the workspace is keyed by it. The project's contents
    /// reach the app the same way they always did — through
    /// <see cref="VaultSettingsStore.Load"/>, which now hands back whichever one this names.
    /// </para>
    /// </summary>
    [ObservableProperty]
    private string _activeProject = string.Empty;

    /// <summary>The projects screen. Always present; whether it is what you see is <see cref="HasProjectOpen"/>.</summary>
    [ObservableProperty]
    private ProjectsVm? _projects;

    public bool HasProjectOpen => !string.IsNullOrWhiteSpace(ActiveProject);

    /// <summary>
    /// True while the projects list is being looked at rather than a project worked in. Separate from
    /// <see cref="HasProjectOpen"/> so that going to the list and coming back is free: the tiers, the
    /// queued edits and the tab you were on are all still there, because nothing was closed. Only
    /// opening a <em>different</em> project throws that away, and it has to.
    /// </summary>
    [ObservableProperty]
    private bool _showingProjects;

    /// <summary>Whether the projects screen is what fills the window, rather than the tabs.</summary>
    public bool ShowingProjectList => ShowingProjects || !HasProjectOpen;

    partial void OnActiveProjectChanged(string value)
    {
        OnPropertyChanged(nameof(HasProjectOpen));
        OnPropertyChanged(nameof(ShowingProjectList));
        OnPropertyChanged(nameof(ProjectChip));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(DocumentCaption));
    }

    partial void OnShowingProjectsChanged(bool value) => OnPropertyChanged(nameof(ShowingProjectList));

    public ObservableCollection<string> Problems { get; } = [];

    public ObservableCollection<TierDocument> Documents { get; } = [];

    /// <summary>
    /// Which of the loaded sources the All tiers grid compares — the ones ticked <b>ON</b>, capped at
    /// four. Empty means "all of them", which is the case before an active set has ever been saved
    /// and the case a seeded test runs in.
    ///
    /// <para>
    /// <see cref="Documents"/> is wider than this on purpose. Everything configured is read, so the
    /// Tier editor and the Text diff can reach a fifth environment without a trip back to the Sources
    /// tab; the cap belongs to the grid, which is four columns wide, not to the read.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Compared { get; private set; } = [];

    /// <summary>
    /// Why Pull is off, or null when it may be pressed. See <see cref="SourceCatalog.Incomplete"/> —
    /// the one arrangement that blocks it is an environment ticked ON with nothing behind it.
    /// </summary>
    [ObservableProperty]
    private string? _pullBlocked;

    /// <summary>Whether a read may be attempted at all — what the Pull button binds to.</summary>
    public bool CanPull => !VaultBusy && PullBlocked is null && HasProjectOpen;

    partial void OnPullBlockedChanged(string? value) => OnPropertyChanged(nameof(CanPull));

    partial void OnVaultBusyChanged(bool value) => OnPropertyChanged(nameof(CanPull));

    /// <summary>
    /// Re-reads the settings to work out whether a read may be attempted. Called on load and again
    /// whenever the Sources tab saves, so ticking the last unconfigured environment off turns Pull
    /// back on without a full reload.
    /// </summary>
    public void RefreshPullState()
    {
        if (!HasProjectOpen)
        {
            PullBlocked = null;
            return;
        }

        var (settings, _) = VaultSettingsStore.Load();
        PullBlocked = SourceCatalog.Incomplete(settings);
    }

    /// <summary>
    /// The tiers Vault could not serve. They keep their column and say so rather than dropping out
    /// of the comparison: "I could not read beta" and "there is no beta" are different sentences,
    /// and the second one is a lie told at exactly the moment it matters.
    /// </summary>
    public ObservableCollection<TierUnavailable> Unavailable { get; } = [];

    /// <summary>
    /// The batched change set, held here rather than on the All tiers tab so it survives a Vault refresh
    /// and a tab switch. Edits queued against a document that then moves are re-checked at commit
    /// time rather than discarded — see <see cref="PendingEdit.IsStaleAgainst"/>.
    /// </summary>
    public EditSet Edits { get; } = new();

    public string ContentRoot { get; private set; } = string.Empty;

    private Flattener? _flattener;
    private AliasSet? _aliases;
    private Classifier? _classifier;
    private TiersConfig? _tiersConfig;
    private TierLoader? _loader;

    public MainVm(bool? vaultAtStartup = null)
    {
        _vaultAtStartup = vaultAtStartup;

        var (workspace, _) = VaultSettingsStore.LoadWorkspace();
        Projects = new ProjectsVm(this);

        // Opening on the list is the default, and it is the reason nothing is read from Vault until a
        // project is picked: the app used to fire a read on launch that nobody asked for, and with
        // more than one project that read would also have been against whichever secrets happened to
        // be last. "Always open the last project" is the opt-out for people who only have one.
        if (workspace.AlwaysOpenLastProject && workspace.Projects.ContainsKey(workspace.ActiveProject))
        {
            OpenProject(workspace.ActiveProject);
            return;
        }

        // The engine and the empty tabs exist either way, so the window is whole the moment it opens
        // and picking a project is a load rather than a construction.
        InitializeEngine();
        BuildTabs([]);
        Status = workspace.Projects.Count == 0
            ? "No projects yet. Create one to choose where its dev, stage, beta and prod come from."
            : "Choose a project.";
    }

    /// <summary>
    /// Opens a project: it becomes the active one, its document becomes the one on screen, and every
    /// tab is rebuilt around its sources. Stamped as recently opened, which is the order the projects
    /// screen lists them in.
    /// </summary>
    public void OpenProject(string name)
    {
        var (workspace, _) = VaultSettingsStore.LoadWorkspace();

        if (!workspace.Projects.TryGetValue(name, out var project))
        {
            Status = $"There is no project called '{name}'.";
            return;
        }

        // Already in it and merely looking at the list: go back to it rather than reloading. A reload
        // would drop the queued edits and the tab you were on to arrive exactly where you started.
        if (ShowingProjects && name.Equals(ActiveProject, StringComparison.OrdinalIgnoreCase))
        {
            ShowingProjects = false;
            return;
        }

        // A different project is a different set of secrets, so nothing that belongs to this one may
        // survive the move — least of all edits queued against tiers that are about to be replaced.
        Edits.Clear();

        workspace.ActiveProject = name;
        project.LastOpenedUtc = DateTimeOffset.UtcNow;

        try
        {
            VaultSettingsStore.SaveWorkspace(workspace);
        }
        catch (Exception ex)
        {
            // Not fatal: what this write records is which project was last open and when. Refusing to
            // open it because that could not be noted down would be the wrong way round.
            _loadProblems.Add($"Could not record '{name}' as the open project: {ex.Message}");
        }

        ActiveProject = name;
        ShowingProjects = false;
        Comparing = project.Describe();
        Reload();
    }

    /// <summary>
    /// Shows the projects list over the top of whatever is open. Nothing is closed and nothing is
    /// discarded — see <see cref="ShowingProjects"/> — so this is safe to press to go and look.
    /// </summary>
    [RelayCommand]
    public void ShowProjectList()
    {
        Projects?.Refresh();
        ShowingProjects = true;
    }

    /// <summary>Back to the project that is still open behind the list.</summary>
    [RelayCommand]
    public void BackToProject() => ShowingProjects = false;

    /// <summary>
    /// Closes the open project without opening another — what happens when the one you were in is
    /// deleted, so the window is not left showing tiers that no longer belong to anything.
    /// </summary>
    public void CloseProject()
    {
        ActiveProject = string.Empty;
        ShowingProjects = false;
        Edits.Clear();
        Compared = [];
        PullBlocked = null;

        Documents.Clear();
        Unavailable.Clear();
        _loadProblems.Clear();
        Problems.Clear();

        BuildTabs([]);
        Projects?.Refresh();

        VaultNote = string.Empty;
        Status = "Choose a project.";
    }

    private string _comparing = string.Empty;

    /// <summary>
    /// What the open project compares, in as few words as it can be said — usually the file name every
    /// active source ends in. Derived from the sources rather than stored, because with each row
    /// naming its own whole path there is no single document to store.
    ///
    /// <para>
    /// Nothing binds this: what the views show is <see cref="DocumentCaption"/>, which reads it. So it
    /// is a plain property rather than an observable one, and the setter raises the caption's
    /// notification itself — that is the notification that has to survive, not one for this.
    /// </para>
    /// </summary>
    public string Comparing
    {
        get => _comparing;
        set
        {
            if (string.Equals(_comparing, value, StringComparison.Ordinal))
            {
                return;
            }

            _comparing = value;
            OnPropertyChanged(nameof(DocumentCaption));
        }
    }

    /// <summary>
    /// The open project's name, shown as a chip <em>beside</em> the app's name rather than in place of
    /// it.
    ///
    /// <para>
    /// Both belong there. Which project is open decides which secrets every tab reads — and on the
    /// Sources tab, where the rows look much the same from one project to the next, it is the only
    /// thing that says which set of them you are editing. But a project here is often named after a
    /// file (<c>config.json</c>, <c>content.json</c>), so putting it where the title goes reads as the
    /// app having been renamed to a file. The app is JsonInsight; the chip says what it is open on.
    /// </para>
    /// </summary>
    public string ProjectChip => ActiveProject;

    /// <summary>What the window is called, so the project is legible in the taskbar too.</summary>
    public string WindowTitle => HasProjectOpen ? $"{ActiveProject} — JsonInsight" : "JsonInsight";

    /// <summary>Under it: what this project compares, and how many tiers of it loaded.</summary>
    public string DocumentCaption
    {
        get
        {
            if (!HasProjectOpen)
            {
                return "no project open";
            }

            var what = Comparing.Length == 0 ? "JsonInsight" : Comparing;

            return Documents.Count switch
            {
                0 => $"{what} — nothing loaded yet",
                1 => $"{what} — 1 tier",
                var count => $"{what} — {count} tiers compared",
            };
        }
    }

    public Flattener Flattener => _flattener ??= new Flattener(ArrayStrategies.Load(), Classifier.Load());

    public Classifier Classifier => _classifier ??= Classifier.Load();

    /// <summary>
    /// Reads every tier from disk and builds the tabs, then — unless it is switched off — replaces
    /// the Vault-backed tiers with what Vault holds right now.
    ///
    /// <para>
    /// The local read stays synchronous and happens first on purpose. The window is usable the
    /// instant it opens, works with no network at all, and never blocks on a Vault that is slow to
    /// answer; the live values arrive a moment later and swap themselves in.
    /// </para>
    /// </summary>
    [RelayCommand]
    public void Reload() => Load(resetVaultTab: true);

    /// <param name="resetVaultTab">
    /// False when the load was triggered from the Sources tab itself. Rebuilding that tab under the
    /// hands of whoever just pressed a button on it would discard the address or token they had
    /// typed but not saved.
    /// </param>
    private void Load(bool resetVaultTab)
    {
        _loadProblems.Clear();
        Problems.Clear();
        Documents.Clear();
        Unavailable.Clear();

        // A reload is a deliberate "start again", so the tabs that hold their own state are dropped
        // and rebuilt. A Vault refresh, by contrast, keeps them — see BuildTabs.
        Tiers = null;
        JsonCompare = null;

        if (resetVaultTab)
        {
            Vault = null;
        }

        try
        {
            InitializeEngine();

            // No project, nothing to build a comparison out of. Stopping here rather than after the
            // catalog matters: the workspace still names whichever project was open last, so going on
            // would put that project's problems in the banner over the list of every project — a
            // complaint about something nobody has opened.
            if (!HasProjectOpen)
            {
                Compared = [];
                PullBlocked = null;
                BuildTabs([]);
                return;
            }

            // tiers.json describes the root document; every other one is the same environments read
            // from a path beneath the same secrets. Saved settings rather than the Sources tab's
            // current fields: what is compared should not depend on an edit nobody has committed.
            //
            // SourceCatalog.Build only overrides tiers.json once the Sources tab's active-set picker
            // has actually been used and saved — until then this is exactly TiersConfig.Load(), as it
            // has always been.
            var (settings, _) = VaultSettingsStore.Load();
            var (catalog, catalogProblems) = SourceCatalog.Build(settings, TiersConfig.Load());
            var (tiers, documentProblems) = DocumentTiers.For(catalog, settings, Document);
            _tiersConfig = tiers;
            Compared = SourceCatalog.Compared(settings, tiers);
            _loadProblems.AddRange(catalogProblems);
            _loadProblems.AddRange(documentProblems);

            PullBlocked = SourceCatalog.Incomplete(settings);

            // The tabs are built empty first so the window is usable immediately and the read has
            // somewhere to land. There is nothing on disk to fill them with: a tier is a Vault
            // secret, and until Vault answers, the honest thing to show is nothing.
            BuildTabs([]);

            // Nothing is read while a ticked environment names no source. The startup read follows
            // the same rule the Pull button does — one of them going ahead while the other refused
            // would mean the app quietly produced, unasked, exactly the comparison it will not
            // produce when asked.
            if (PullBlocked is { } blocked)
            {
                Status = "Nothing was read — a ticked environment has no source.";
                SetNote(false, blocked);
                return;
            }

            if (!ShouldReadVault())
            {
                Status = "Reading at startup is switched off, so no tier has been loaded.";
                SetNote(false,
                    "\"LoadTiersAtStartup\" is false, so nothing has been read. Press Pull from Vault or " +
                    "disk — nothing is cached, by design.");
                return;
            }

            Status = $"{ActiveProject} — reading from Vault or disk…";
            _ = RefreshFromVaultAsync(announce: true, atStartup: true);
        }
        catch (Exception ex)
        {
            Status = $"Load failed: {ex.Message}";
            _loadProblems.Add(ex.ToString());
            RepublishProblems();
        }
    }

    /// <summary>
    /// The parts that do not depend on a project: the config folder's rules and the objects built from
    /// them. Cheap, local, and needed before any project is open, since the tabs exist — empty —
    /// while the projects screen is showing.
    /// </summary>
    private void InitializeEngine()
    {
        ContentRoot = AppPaths.ContentRoot;
        OnPropertyChanged(nameof(ContentRoot));

        var arrays = ArrayStrategies.Load();
        _classifier = Classifier.Load();
        _aliases = AliasSet.Load();
        _loader = new TierLoader(arrays, _classifier);
        _flattener = new Flattener(arrays, _classifier);
    }

    /// <summary>
    /// Fills the tabs with documents that did not come from Vault.
    ///
    /// <para>
    /// The app never calls this. The test harness does, because a tier is a Vault secret and a test
    /// must not reach one — the same reason the constructor takes a switch for the startup read. It
    /// is the only way to get documents into this object without a network, and it is deliberately
    /// the only one.
    /// </para>
    /// </summary>
    /// <param name="compared">
    /// Which of them the All tiers grid compares, or null for all of them — the arrangement every
    /// test that does not care about the distinction runs in.
    /// </param>
    internal void Seed(
        IReadOnlyList<TierDocument> documents,
        IReadOnlyList<TierUnavailable>? unavailable = null,
        IReadOnlyList<string>? compared = null)
    {
        Compared = compared ?? [];
        BuildTabs(documents, unavailable);
        Status = $"{TierCount(documents.Count)} seeded for testing.";
    }

    private bool ShouldReadVault()
    {
        if (_vaultAtStartup is { } explicitChoice)
        {
            return explicitChoice;
        }

        var (settings, _) = VaultSettingsStore.Load();
        return settings.LoadTiersAtStartup;
    }

    /// <summary>
    /// Reads every tier again. Behind both the read the app performs when it opens and the pull
    /// button — they are the same operation, and the only difference is who asked for it.
    /// </summary>
    /// <param name="atStartup">
    /// Whether this read is the one the app performs when it opens rather than one someone asked
    /// for. It changes nothing about the read and everything about how a failure reads: "cannot
    /// reach Vault" is alarming when you did not press anything, and the answer to "why is it
    /// telling me this?" has to be in the message rather than in this README.
    /// </param>
    public async Task<TierRefreshReport?> RefreshFromVaultAsync(
        bool announce = false,
        bool atStartup = false)
    {
        if (_tiersConfig is null || _flattener is null)
        {
            return null;
        }

        var (settings, settingsProblems) = VaultSettingsStore.Load();

        // Re-asked here rather than trusted from load time: the Sources tab can have been saved since,
        // and this is the last point before anything is read.
        PullBlocked = SourceCatalog.Incomplete(settings);
        if (PullBlocked is { } blocked)
        {
            SetNote(false, blocked);
            return null;
        }

        VaultBusy = true;
        VaultNote = atStartup
            ? "Reading every source, as it does when it opens…"
            : "Reading every source…";
        VaultNoteIsGood = true;

        TierRefreshReport report;
        try
        {
            report = await new TierRefresher(_flattener)
                .RefreshAsync(_tiersConfig, settings)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status = $"Refresh failed: {ex.Message}";
            _loadProblems.Add($"{Preamble(atStartup)}{ex.Message}");
            RepublishProblems();
            SetNote(false, $"The sources could not be read: {ex.Message}");
            return null;
        }
        finally
        {
            VaultBusy = false;
        }

        BuildTabs(report.Documents, report.Unavailable);

        foreach (var problem in settingsProblems)
        {
            Problems.Add($"vault settings: {problem}");
        }

        // A tier that could not be read has no values at all — there is nothing to fall back on and
        // nothing to compare. That is a finding rather than a log line, so it goes in the banner.
        foreach (var failure in report.Failures)
        {
            Problems.Add($"{Preamble(atStartup)}{failure.TierId} could not be read, so it is not being " +
                         $"compared — {failure.Message}");
        }

        if (announce)
        {
            Status = $"{TierCount(report.Documents.Count)} — {report.Summary}";
        }

        // "Nothing was configured to read" is not a success, so it does not get the green tick — it
        // is the state where the answer to "is this live?" is no.
        SetNote(report.UnavailableCount == 0 && report.Lines.Count > 0, Describe(report, atStartup));

        return report;
    }

    /// <summary>
    /// What a failure line opens with. At startup it has to answer the question nobody asked out
    /// loud: why is this app telling me it could not reach a server I never asked it to contact.
    /// </summary>
    private static string Preamble(bool atStartup) => atStartup
        ? "JsonInsight reads every source when it opens, and could not this time — set \"LoadTiersAtStartup\": false in appsettings.json to stop it trying. "
        : string.Empty;

    private string Describe(TierRefreshReport report, bool atStartup)
    {
        var when = atStartup ? "on opening" : "just now";

        if (report.Lines.Count == 0)
        {
            return "No tier has a source configured, so nothing was read.";
        }

        if (report.UnavailableCount == 0)
        {
            return $"All {report.RefreshedCount} tiers read {when}. These values are live — they are what " +
                   "their sources hold right now, and nothing here is a copy.";
        }

        return $"{report.RefreshedCount} of {report.Lines.Count} tiers read {when}. " +
               $"{report.UnavailableCount} could not be read and {(report.UnavailableCount == 1 ? "is" : "are")} " +
               "not being compared — see below.";
    }

    private void SetNote(bool good, string note)
    {
        VaultNoteIsGood = good;
        VaultNote = note;
        Log.Add(good ? LogLevel.Info : LogLevel.Warning, note);
    }

    /// <summary>
    /// Every status line also lands in the log. The status bar holds one line and is overwritten by
    /// the next thing that happens, which is fine while you are watching it and useless afterwards —
    /// "what did it say before I pressed that?" is exactly the question the Logs tab exists to answer.
    /// </summary>
    partial void OnStatusChanged(string value) => Log.Info(value);

    private static string TierCount(int count) => count == 1 ? "1 tier" : $"{count} tiers";

    /// <summary>
    /// Rebuilds the tabs around a document set. The All tiers tab is refreshed in place rather than
    /// replaced, so a filter, a chosen section and the expand/collapse state all survive a Vault
    /// refresh — losing them on every pull would make the pull button annoying enough not to press.
    /// </summary>
    /// <summary>
    /// Puts one freshly-read source into the comparison and leaves every other document as it was.
    ///
    /// <para>
    /// What the per-row <b>Load</b> on the Sources tab calls. Pull reads everything, which is the
    /// wrong size of act when one row's path was just corrected and the other three are fine — and on
    /// four Vault servers it is also three network round trips nobody asked for.
    /// </para>
    ///
    /// <para>
    /// The document is merged in the catalog's order rather than appended, so loading dev after prod
    /// does not put dev in the last column. Like Pull, this rebuilds the Tier editor around the new
    /// document set — an in-progress edit does not survive either of them.
    /// </para>
    /// </summary>
    public void AdoptDocument(TierDocument document)
    {
        var merged = Documents.ToList();
        var at = merged.FindIndex(d => d.Id.Equals(document.Id, StringComparison.OrdinalIgnoreCase));

        if (at >= 0)
        {
            merged[at] = document;
        }
        else
        {
            merged.Add(document);
        }

        if (_tiersConfig is not null)
        {
            var order = _tiersConfig.Tiers
                .Select((tier, index) => (tier.Id, index))
                .ToDictionary(pair => pair.Id, pair => pair.index, StringComparer.OrdinalIgnoreCase);

            merged = merged
                .OrderBy(d => order.TryGetValue(d.Id, out var index) ? index : int.MaxValue)
                .ToList();
        }

        // It loaded, so whatever it was previously unavailable for no longer applies.
        var unavailable = Unavailable
            .Where(u => !u.Id.Equals(document.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        BuildTabs(merged, unavailable);
    }

    private void BuildTabs(
        IReadOnlyList<TierDocument> documents,
        IReadOnlyList<TierUnavailable>? unavailable = null)
    {
        Documents.Clear();
        foreach (var document in documents)
        {
            Documents.Add(document);
        }

        Unavailable.Clear();
        foreach (var tier in unavailable ?? [])
        {
            Unavailable.Add(tier);
        }

        OnPropertyChanged(nameof(DocumentCaption));
        RepublishProblems();

        if (Tiers is null)
        {
            Tiers = new TiersVm(this, documents, Unavailable, _aliases!, _flattener!);
        }
        else
        {
            Tiers.Refresh(documents, Unavailable);
        }

        RawDiff = new RawDiffVm(documents);

        // Rebuilt rather than refreshed: its whole state is an in-memory edit of a specific document,
        // and carrying that across a reload would mean editing one tree while showing another.
        JsonEditor = new JsonEditorVm(this);

        if (Vault is null)
        {
            Vault = new VaultVm(this, _flattener!);
            foreach (var problem in Vault.Problems)
            {
                Problems.Add($"vault settings: {problem}");
            }
        }

        // Opens on nothing: there are no snapshot files to preselect any more, and this tab is for
        // whatever two JSON files you have — a payload someone sent you, an export, a backup.
        JsonCompare ??= new JsonCompareVm(_loader!, _aliases!);
    }

    /// <summary>
    /// Recomputes the current findings: the load-stage problems plus whatever the documents warn
    /// about. Anything that was not in the previous set is logged.
    ///
    /// <para>
    /// Only the difference is logged, not the whole set. This runs on every pull, every reload and
    /// every single-source load, and a warning that is still true is not a new event — re-logging it
    /// each time would bury the one line that did change under four copies of the three that did not.
    /// </para>
    /// </summary>
    private void RepublishProblems()
    {
        var previous = Problems.ToHashSet(StringComparer.Ordinal);

        Problems.Clear();

        foreach (var problem in _loadProblems)
        {
            Problems.Add(problem);
        }

        // Grouped by the warning itself rather than listed per tier. These documents are four copies
        // of one shape, so a warning about it is almost always true of all four — and "dev: …",
        // "stage: …", "beta: …", "prod: …" is the same sentence four times with the interesting word
        // at the front. Naming the tiers once puts the finding first and still says where it applies.
        foreach (var group in Documents
                     .SelectMany(d => d.Warnings.Select(w => (Tier: d.Id, Warning: w)))
                     .GroupBy(p => p.Warning, StringComparer.Ordinal))
        {
            var tiers = group.Select(p => p.Tier).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            Problems.Add(tiers.Length == Documents.Count && Documents.Count > 1
                ? $"every source: {group.Key}"
                : $"{string.Join(", ", tiers)}: {group.Key}");
        }

        foreach (var problem in Problems.Where(p => !previous.Contains(p)))
        {
            Log.Warn(problem);
        }
    }

    /// <summary>
    /// Recolours the whole window in place. Nothing is reloaded, so a half-filled promote dialog or
    /// an unsaved Vault address survives the switch.
    /// </summary>
    [RelayCommand]
    private void ToggleTheme()
    {
        JsonInsight.Platform.Platform.Theme.Toggle();
        OnPropertyChanged(nameof(ThemeGlyph));
        OnPropertyChanged(nameof(ThemeTooltip));
    }

    /// <summary>Shows the theme you would get by clicking, not the one you are in.</summary>
    public string ThemeGlyph => JsonInsight.Platform.Platform.Theme.Current == AppTheme.Dark
        ? BrightnessGlyph
        : QuietHoursGlyph;

    // Segoe Fluent Icons code points, written as escapes so the source stays plain ASCII.
    private const string BrightnessGlyph = "\uE706";
    private const string QuietHoursGlyph = "\uE708";

    public string ThemeTooltip => JsonInsight.Platform.Platform.Theme.Current == AppTheme.Dark
        ? "Switch to the light theme (Ctrl+D)"
        : "Switch to the dark theme (Ctrl+D)";
}
