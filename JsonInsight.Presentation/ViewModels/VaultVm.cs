using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JsonInsight.Loading;
using JsonInsight.Promote;
using JsonInsight.Sources;
using JsonInsight.Vault;

namespace JsonInsight.ViewModels;

/// <summary>One kind of source, as offered in a row's picker.</summary>
public sealed record SourceKindChoice(SourceKind Kind, string Label);

/// <summary>
/// One source's row on the Sources tab, and the whole of what that source is: where it lives, how to
/// reach it, whether it may be written, and whether it is one of the columns being compared.
///
/// <para>
/// The name predates <see cref="Kind"/>, exactly as <see cref="VaultConnection"/>'s does and for the
/// same reason: every one of these used to be a Vault connection and nothing else. It is still the
/// view of a <see cref="VaultConnection"/> one-for-one, and renaming one without the other would cost
/// more in confusion than the stale word does.
/// </para>
/// </summary>
public sealed partial class VaultConnectionVm : ObservableObject
{
    public string TierId { get; }

    /// <summary>
    /// Which of the predefined environments this row is, or null for a row that came out of settings
    /// under an id this app does not recognize. An unrecognized row is shown rather than dropped — a
    /// connection that silently vanished from the tab that owns it is a connection nobody can fix —
    /// but it cannot join the comparison, because <see cref="SourceCatalog"/> would skip it anyway.
    /// </summary>
    public SourceEnvironment? Environment { get; }

    /// <summary>
    /// The secret this row reads, in full — e.g.
    /// <c>kv/app/stage/resources/config/ui.json</c>. Vault kind only.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTest))]
    private string _secretPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTest))]
    private string _address = string.Empty;

    [ObservableProperty]
    private string _namespace = string.Empty;

    /// <summary>Held only in memory and in the PasswordBox; persisted exclusively to user secrets.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTest))]
    private string _token = string.Empty;

    [ObservableProperty]
    private bool _allowInsecureTls;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>
    /// True when <see cref="Status"/> reports a failure — a test or load that did not reach the
    /// source — so both views can paint it red rather than the neutral grey of "Reading…" and
    /// "Loaded". Reset by every status write and raised explicitly in the failure arms, which keeps
    /// the two from drifting: a new status message is neutral unless the code that wrote it says
    /// otherwise.
    /// </summary>
    [ObservableProperty]
    private bool _statusIsError;

    partial void OnStatusChanged(string value) => StatusIsError = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTest), nameof(CanLoad))]
    private bool _busy;

    /// <summary>True while this row's Vault is being walked. See <see cref="VaultVm.SearchVault"/>.</summary>
    [ObservableProperty]
    private bool _searching;

    /// <summary>Where this source's content lives — which half of the row is the one that matters.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTest))]
    private SourceKind _kind = SourceKind.Vault;

    /// <summary>The file this source reads and writes, when <see cref="Kind"/> is <see cref="SourceKind.LocalFile"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTest))]
    private string _localFilePath = string.Empty;

    /// <summary>
    /// True once Test has read this source successfully and nothing on the row has been edited since.
    /// It is what enables Load.
    ///
    /// <para>
    /// Not persisted, and deliberately not sticky: Load puts this source on the Tier editor, All tiers
    /// and Text diff, so the claim it makes is "this is what that environment holds". A test that
    /// passed against a path since retyped, or a server since repointed, is not evidence for that
    /// claim — so every field saying where this source is or how it is reached clears this again. See
    /// <see cref="Retest"/>.
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoad))]
    private bool _testPassed;

    /// <summary>
    /// Whether a token from <c>vault login</c> or <c>VAULT_TOKEN</c> is standing by for a row that
    /// names none of its own — see <see cref="VaultSettingsStore.AmbientToken"/>.
    ///
    /// <para>
    /// Pushed in by the tab rather than read here. <see cref="VaultSettingsStore.AmbientToken"/> goes
    /// to the environment and then to disk on every call, and <see cref="CanTest"/> is consulted on
    /// every redraw of every row; asking per keystroke would be a file read per keystroke. It is
    /// re-read whenever the tab rebuilds its rows, which is what Revert does and what opening a
    /// project does — so a <c>vault login</c> run while this app is open takes effect on the next of
    /// those.
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTest))]
    private bool _ambientTokenAvailable;

    /// <summary>
    /// Whether this row's overflow menu is showing. View state, not settings: it is here rather than
    /// in the component so the WPF and Blazor rows stay one model, and so opening one row's menu can
    /// close every other.
    /// </summary>
    [ObservableProperty]
    private bool _menuOpen;

    /// <summary>
    /// The endpoint that restarts whatever reads this source. Saved with the rest of the row; the
    /// bearer token that calls it is not, and is typed for each call.
    /// </summary>
    [ObservableProperty]
    private string _restartUrl = string.Empty;

    /// <summary>An optional JSON body. Empty sends none.</summary>
    [ObservableProperty]
    private string _restartBody = string.Empty;

    [ObservableProperty]
    private bool _restartAllowInsecureTls;

    /// <summary>True when this row has somewhere to send a restart, which is what enables the button.</summary>
    public bool HasRestart => !string.IsNullOrWhiteSpace(RestartUrl);

    /// <summary>
    /// Why the configured body is unusable, or empty when it is fine — including when it is blank,
    /// which is the normal case and means no body is sent.
    ///
    /// <para>
    /// On the view model rather than in either config screen, so the WPF window and the Blazor
    /// component report the same thing in the same words. A validation rule that lived in one view
    /// would be a rule the other quietly did not have.
    /// </para>
    /// </summary>
    public string RestartBodyProblem => RestartTrigger.BodyProblem(RestartBody) ?? string.Empty;

    /// <summary>Whether there is anything to say about the body — for a banner that hides itself.</summary>
    public bool HasRestartBodyProblem => RestartBodyProblem.Length > 0;

    /// <summary>What the Call restart button says it would do, in one line, for its tooltip.</summary>
    public string RestartSummary => HasRestart
        ? $"POST {RestartUrl} — restarts whatever reads {Label}. Asks for the bearer token first."
        : "No restart endpoint is configured for this source yet. Press Restart config to set one.";

    partial void OnRestartUrlChanged(string value)
    {
        OnPropertyChanged(nameof(HasRestart));
        OnPropertyChanged(nameof(RestartSummary));
    }

    partial void OnRestartBodyChanged(string value)
    {
        OnPropertyChanged(nameof(RestartBodyProblem));
        OnPropertyChanged(nameof(HasRestartBodyProblem));
    }

    /// <summary>Whether this source is one of the columns being compared. See <see cref="SourceCatalog.MaxActive"/>.</summary>
    [ObservableProperty]
    private bool _active;

    /// <summary>
    /// Every secret this row's last search found, offered in its path picker.
    ///
    /// <para>
    /// Not persisted. It can run to hundreds of entries and it is a property of the Vault rather than
    /// of this project, so it is re-found on demand rather than written into appsettings.json. The one
    /// entry that has to survive a restart — the path this row actually reads — is
    /// <see cref="SecretPath"/>, and it is put back into this list on load so the picker never opens
    /// without the current answer in it.
    /// </para>
    /// </summary>
    public ObservableCollection<string> KnownPaths { get; } = [];

    /// <summary>
    /// <see cref="KnownPaths"/> narrowed by what is typed in the path box, so the dropdown filters
    /// as you type instead of asking you to scan hundreds of entries for the one that contains
    /// "ui.json". The whole list comes back when the box is empty — and when it holds exactly a
    /// known path, because that is what the box looks like right after picking one, and a dropdown
    /// of one entry at that moment would make the list look lost rather than filtered.
    /// </summary>
    public IEnumerable<string> FilteredPaths
    {
        get
        {
            var typed = SecretPath.Trim();

            return typed.Length == 0 || KnownPaths.Contains(typed, StringComparer.Ordinal)
                ? KnownPaths
                : KnownPaths.Where(p => p.Contains(typed, StringComparison.OrdinalIgnoreCase));
        }
    }

    public bool HasToken => !string.IsNullOrWhiteSpace(Token);

    /// <summary>
    /// Whether this row says enough for Test to have something to read — the same three things
    /// <see cref="VaultSettings.Incomplete"/> names for a Vault source, and the file for a local one.
    ///
    /// <para>
    /// Asked of the row rather than of saved settings, because the row is what is on screen: a path
    /// typed a second ago has not been saved and would not be there to find. The ambient token counts,
    /// for the same reason the read itself counts it — a row left blank because <c>vault login</c>
    /// supplied the credential is configured, and a Test button greyed out at it would be wrong.
    /// </para>
    /// </summary>
    public bool CanTest => !Busy && (Kind == SourceKind.LocalFile
        ? !string.IsNullOrWhiteSpace(LocalFilePath)
        : !string.IsNullOrWhiteSpace(Address) &&
          !string.IsNullOrWhiteSpace(SecretPath) &&
          (HasToken || AmbientTokenAvailable));

    /// <summary>
    /// Whether Load has been earned. It waits on a Test that succeeded, and stops waiting the moment
    /// the row is edited again — see <see cref="TestPassed"/>.
    /// </summary>
    public bool CanLoad => TestPassed && !Busy;

    public bool IsVault => Kind == SourceKind.Vault;

    public bool IsLocalFile => Kind == SourceKind.LocalFile;

    /// <summary>Only a recognized environment can be compared; see <see cref="Environment"/>.</summary>
    public bool CanActivate => Environment is not null;

    /// <summary>What to call this row on screen — "test/qa" rather than the "test-qa" it persists as.</summary>
    public string Label => Environment?.Label() ?? TierId;

    /// <summary>
    /// True for a row that has never been filled in — the row is there to be filled in, and writing it
    /// back would put an empty entry in appsettings.json for every environment that has no source.
    /// What counts as "filled in" follows the kind: a Vault source is its secret path, a local one its
    /// file.
    /// </summary>
    public bool IsEmpty => Kind == SourceKind.LocalFile
        ? string.IsNullOrWhiteSpace(LocalFilePath)
        : string.IsNullOrWhiteSpace(SecretPath) && string.IsNullOrWhiteSpace(Token) && !HasRestart;

    public VaultConnectionVm(string tierId, VaultConnection connection, SourceEnvironment? environment = null)
    {
        TierId = tierId;
        Environment = environment ?? SourceEnvironmentExtensions.ParseId(tierId);
        _secretPath = connection.SecretPath;
        _address = connection.Address;
        _namespace = connection.Namespace;
        _token = connection.Token;
        _allowInsecureTls = connection.AllowInsecureTls;
        _kind = connection.Kind;
        _localFilePath = connection.LocalFilePath;
        _restartUrl = connection.RestartUrl;
        _restartBody = connection.RestartBody;
        _restartAllowInsecureTls = connection.RestartAllowInsecureTls;

        if (!string.IsNullOrWhiteSpace(_secretPath))
        {
            KnownPaths.Add(_secretPath);
        }

        // A search replaces the list wholesale, and the filtered view over it has to follow.
        KnownPaths.CollectionChanged += (_, _) => OnPropertyChanged(nameof(FilteredPaths));
    }

    /// <summary>The settings for this row alone, which is all a probe or a search needs.</summary>
    public VaultConnection ToConnection() => new()
    {
        SecretPath = SecretPath.Trim(),
        Address = Address.Trim(),
        Namespace = Namespace.Trim(),
        Token = Token.Trim(),
        AllowInsecureTls = AllowInsecureTls,
        Kind = Kind,
        LocalFilePath = LocalFilePath.Trim(),
        RestartUrl = RestartUrl.Trim(),
        RestartBody = RestartBody.Trim(),
        RestartAllowInsecureTls = RestartAllowInsecureTls,
    };

    partial void OnTokenChanged(string value)
    {
        OnPropertyChanged(nameof(HasToken));
        Retest();
    }

    partial void OnKindChanged(SourceKind value)
    {
        OnPropertyChanged(nameof(IsVault));
        OnPropertyChanged(nameof(IsLocalFile));
        Retest();
    }

    // Every field below says where this source is or how it is reached, so editing any of them means
    // the row no longer describes whatever Test read. Status, Busy, the menu and the ON tick are
    // deliberately not among them: none of them changes what a read would return.
    partial void OnSecretPathChanged(string value)
    {
        OnPropertyChanged(nameof(FilteredPaths));
        Retest();
    }

    partial void OnAddressChanged(string value) => Retest();

    partial void OnNamespaceChanged(string value) => Retest();

    partial void OnAllowInsecureTlsChanged(bool value) => Retest();

    partial void OnLocalFilePathChanged(string value) => Retest();

    /// <summary>
    /// Forgets that Test succeeded. Load is the button that says "this is what that environment
    /// holds", and a verification of a source that has since been edited is not evidence of that —
    /// so it goes back to needing one.
    /// </summary>
    private void Retest() => TestPassed = false;
}

/// <summary>
/// The Sources tab: one row per environment, saying where that environment's JSON comes from and
/// whether it is one of the columns the other tabs compare.
///
/// <para>
/// One list and nothing above it, on purpose. There used to be two more cards — shared Vault
/// credentials, and an app-wide document appended to every row's root — so answering "what does stage
/// actually read, and as whom" meant reading three places and knowing which of them won. A row now
/// carries its own address, token and whole secret path, and <b>Search</b> walks that row's own Vault
/// to fill its path picker with the JSONs that are really there.
/// </para>
///
/// <para>
/// Until an active set is ticked and saved, this tab is a settings editor and nothing more:
/// <see cref="SourceCatalog"/> leaves <c>tiers.json</c> authoritative, so rows can be filled in and
/// tested without changing what is on screen anywhere else.
/// </para>
/// </summary>
public sealed partial class VaultVm : ObservableObject
{
    private readonly MainVm _main;
    private readonly Flattener _flattener;

    /// <summary>Preserved across a save: it is a setting this tab does not show.</summary>
    private bool _loadTiersAtStartup = true;

    /// <summary>
    /// Whether <c>vault login</c> or <c>VAULT_TOKEN</c> has left a token behind, read once per rebuild
    /// and handed to each row. See <see cref="VaultConnectionVm.AmbientTokenAvailable"/> for why it is
    /// not asked per row.
    /// </summary>
    private bool _ambientToken;

    [ObservableProperty]
    private string _status = string.Empty;

    public ObservableCollection<VaultConnectionVm> Connections { get; } = [];

    public ObservableCollection<string> Problems { get; } = [];

    /// <summary>The two kinds a row can be, offered on every row's picker.</summary>
    public IReadOnlyList<SourceKindChoice> Kinds { get; } =
    [
        new(SourceKind.Vault, "Vault"),
        new(SourceKind.LocalFile, "Local file"),
    ];

    /// <summary>How many columns are spoken for, said where the tick boxes are.</summary>
    public string ActiveSummary
    {
        get
        {
            var active = Connections.Count(c => c.Active);
            return active == 0
                ? "No sources chosen — config/tiers.json is still in charge of what every tab compares."
                : $"{active} of {SourceCatalog.MaxActive} comparison slots used.";
        }
    }

    public string SecretsFile => VaultSettingsStore.SecretsFile;

    public VaultVm(MainVm main, Flattener flattener)
    {
        _main = main;
        _flattener = flattener;
        ReloadSettings();
    }

    /// <summary>
    /// Opens one row's overflow menu and closes every other, which is what makes a second click
    /// somewhere else dismiss the first rather than leaving two menus open down the list.
    /// </summary>
    [RelayCommand]
    private void ToggleMenu(VaultConnectionVm? row)
    {
        if (row is null)
        {
            return;
        }

        var opening = !row.MenuOpen;

        foreach (var other in Connections)
        {
            other.MenuOpen = false;
        }

        row.MenuOpen = opening;
    }

    /// <summary>Closes any open row menu — for a click landing anywhere else on the tab.</summary>
    [RelayCommand]
    private void CloseMenus()
    {
        foreach (var row in Connections)
        {
            row.MenuOpen = false;
        }
    }

    [RelayCommand]
    private void ReloadSettings()
    {
        foreach (var row in Connections)
        {
            row.PropertyChanged -= RowChanged;
        }

        Connections.Clear();
        Problems.Clear();

        _ambientToken = !string.IsNullOrWhiteSpace(VaultSettingsStore.AmbientToken);

        // Nothing is read until a project is open, and this is where that has to be enforced: the
        // workspace still names whichever project was open last, so loading here would fill the tab
        // with one project's servers and tokens while the projects list is the thing on screen — and
        // would go to user secrets at startup to do it. The rows exist, empty, so the tab has its
        // shape; what goes in them arrives with a project.
        if (!_main.HasProjectOpen)
        {
            foreach (var environment in SourceEnvironmentExtensions.All)
            {
                AddRow(new VaultConnectionVm(environment.Id(), new VaultConnection(), environment));
            }

            Status = "No project is open. Open one and its sources appear here.";
            OnPropertyChanged(nameof(ActiveSummary));
            RefreshDuplicateError();
            return;
        }

        var (settings, problems) = VaultSettingsStore.Load();
        foreach (var problem in problems)
        {
            Problems.Add(problem);
        }

        _loadTiersAtStartup = settings.LoadTiersAtStartup;

        // One row per predefined environment, whether or not it has a source yet — the list is the
        // fixed list rather than whatever tiers.json names, because this tab is where an environment
        // that has no source gets one, and a row that only appears once it is already configured is a
        // row nobody can use to configure it.
        foreach (var environment in SourceEnvironmentExtensions.All)
        {
            settings.Connections.TryGetValue(environment.Id(), out var connection);
            AddRow(new VaultConnectionVm(environment.Id(), connection ?? new VaultConnection(), environment));
        }

        // Anything settings hold under an id this app does not recognize. It cannot be compared — see
        // VaultConnectionVm.Environment — but it is shown so it can be seen and edited rather than
        // silently carried around in a file nobody opens.
        foreach (var (tierId, connection) in settings.Connections)
        {
            if (Connections.All(c => !c.TierId.Equals(tierId, StringComparison.OrdinalIgnoreCase)))
            {
                AddRow(new VaultConnectionVm(tierId, connection));
            }
        }

        MarkActive(settings);

        // The no-project case returned above, so this is always a project's own count.
        Status = $"{Connections.Count(c => !c.IsEmpty)} of {Connections.Count} environments have a source configured.";

        OnPropertyChanged(nameof(ActiveSummary));

        // The rows are new objects, so whatever the previous set's clash was is either gone or
        // about to be found again on this one.
        RefreshDuplicateError();
    }

    private void AddRow(VaultConnectionVm row)
    {
        // Before the subscription, so the row's own notification for it is not one the tab has to
        // ignore: this is the tab telling the row what it already decided, not a change to react to.
        row.AmbientTokenAvailable = _ambientToken;

        row.PropertyChanged += RowChanged;
        Connections.Add(row);
    }

    /// <summary>
    /// Ticks the rows that are actually being compared right now — the saved active set if there is
    /// one, and otherwise the tiers <c>tiers.json</c> supplies, since that is what
    /// <see cref="SourceCatalog"/> falls back to and therefore what is genuinely on screen. Opening
    /// this tab onto six empty tick boxes while four tiers were plainly being compared would make the
    /// column that says "on" the one thing here that is not true.
    /// </summary>
    private void MarkActive(VaultSettings settings)
    {
        var active = settings.ActiveSources.Count > 0 ? settings.ActiveSources : TierIds();

        _syncingActive = true;
        foreach (var id in active.Take(SourceCatalog.MaxActive))
        {
            var row = Connections.FirstOrDefault(c => c.TierId.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (row is { CanActivate: true })
            {
                row.Active = true;
            }
        }

        _syncingActive = false;
    }

    private static IReadOnlyList<string> TierIds()
    {
        try
        {
            return TiersConfig.Load().Tiers.Select(t => t.Id).ToArray();
        }
        catch
        {
            // The tab must still open with a broken tiers.json — this is where a source gets fixed,
            // and the sources themselves do not depend on that file.
            return [];
        }
    }

    /// <summary>
    /// Holds the cap while the tab itself is the one setting <see cref="VaultConnectionVm.Active"/> —
    /// both the initial tick pass and the revert below would otherwise re-enter the check they are the
    /// answer to.
    /// </summary>
    private bool _syncingActive;

    private void RowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not VaultConnectionVm row)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(VaultConnectionVm.Active):
                ActiveToggled(row);
                break;

            // All three change what "configured" means for a row, and the count says so.
            case nameof(VaultConnectionVm.Kind):
            case nameof(VaultConnectionVm.SecretPath):
            case nameof(VaultConnectionVm.LocalFilePath):
                OnPropertyChanged(nameof(ActiveSummary));
                RefreshDuplicateError();
                break;

            // These two do not change "configured", but they are half of a Vault source's identity —
            // repointing an address can make two rows the same source as surely as retyping a path.
            case nameof(VaultConnectionVm.Address):
            case nameof(VaultConnectionVm.Namespace):
                RefreshDuplicateError();
                break;
        }
    }

    /// <summary>
    /// The duplicate-source complaint, live: set the moment two rows come to read the same place,
    /// cleared the moment they stop. Its own property rather than a write into <see cref="Status"/>,
    /// because it has two jobs the status line cannot do — it is what disables Save settings, and it
    /// stays on screen in red for as long as the clash exists instead of scrolling away under the
    /// next report.
    /// </summary>
    [ObservableProperty]
    private string _duplicateError = string.Empty;

    /// <summary>What gates Save settings: nothing else does, so the button reads as always-on until two rows clash.</summary>
    public bool CanSaveSettings => DuplicateError.Length == 0;

    partial void OnDuplicateErrorChanged(string value)
    {
        OnPropertyChanged(nameof(CanSaveSettings));
        SaveSettingsCommand.NotifyCanExecuteChanged();
    }

    private void RefreshDuplicateError() => DuplicateError = DuplicateSourceProblem() ?? string.Empty;

    /// <summary>
    /// Enforces the cap where it is understood rather than where it is discovered: a fifth tick is
    /// refused here, with a reason, instead of being accepted and then quietly dropped by
    /// <see cref="SourceCatalog"/> a reload later.
    /// </summary>
    private void ActiveToggled(VaultConnectionVm row)
    {
        if (_syncingActive)
        {
            return;
        }

        if (row.Active && !row.CanActivate)
        {
            Revert(row);
            Status = $"'{row.TierId}' is not one of the predefined environments, so it cannot be compared. " +
                     "Give it one of those names in appsettings.json, or configure that environment's row instead.";
            return;
        }

        if (row.Active && Connections.Count(c => c.Active) > SourceCatalog.MaxActive)
        {
            Revert(row);
            Status = $"{SourceCatalog.MaxActive} sources are already active — untick one before adding " +
                     $"{row.Label}. This app compares {SourceCatalog.MaxActive} columns at a time.";
            return;
        }

        Status = row.Active
            ? $"{row.Label} joins the comparison when you press Save settings."
            : $"{row.Label} leaves the comparison when you press Save settings.";

        OnPropertyChanged(nameof(ActiveSummary));
    }

    private void Revert(VaultConnectionVm row)
    {
        _syncingActive = true;
        row.Active = false;
        _syncingActive = false;
    }

    /// <summary>
    /// Whether the next Save press is the one that goes ahead and discards. See
    /// <see cref="MainVm.AdoptSavedSourcesAsync"/>; cleared by anything that resolves the question.
    /// </summary>
    private bool _confirmingDiscard;

    [RelayCommand(CanExecute = nameof(CanSaveSettings))]
    private async Task SaveSettingsAsync()
    {
        if (!_main.HasProjectOpen)
        {
            Status = "No project is open, so there is nothing to save these into. Open one first.";
            return;
        }

        // Refused rather than saved-and-warned: two rows reading the same place is one source
        // counted twice on every tab that compares them, and a save is the moment it becomes real.
        if (DuplicateSourceProblem() is { } duplicate)
        {
            Status = duplicate;
            return;
        }

        string saved;
        try
        {
            var settings = BuildSettings();
            var (appSettingsPath, secretsPath) = VaultSettingsStore.Save(settings, _main.ActiveProject);

            // Pull is off while a ticked environment names no source, and this is the screen where
            // that gets fixed. Re-asked here so unticking the last empty row turns the button back on
            // without waiting for a full reload.
            _main.RefreshPullState();

            saved = $"Saved into the '{_main.ActiveProject}' project — paths and addresses in " +
                    $"{Path.GetFileName(appSettingsPath)}, tokens in user secrets ({secretsPath}).";
        }
        catch (Exception ex)
        {
            Status = $"Save failed: {ex.Message}";
            return;
        }

        // The tabs follow the save. They used to be left describing the set they were built from, so
        // ticking a source changed a file and nothing else and the only ways to catch up were to
        // restart the app or open another project and come back. Only what actually moved is read —
        // see AdoptSavedSourcesAsync — so unticking a source costs no request at all.
        Status = $"{saved} Rebuilding the other tabs…";

        var adopted = await _main.AdoptSavedSourcesAsync(_confirmingDiscard).ConfigureAwait(true);
        _confirmingDiscard = !adopted.Adopted && adopted.AtRisk.Count > 0;

        Status = adopted.Message.Length == 0 ? saved : $"{saved} {adopted.Message}";
    }

    /// <summary>Saving from anywhere else — the restart dialog — goes through the same command.</summary>
    private void SaveSettings() => SaveSettingsCommand.Execute(null);

    /// <summary>
    /// Walks this row's Vault and fills its picker with every secret found there, so the JSON can be
    /// chosen rather than remembered.
    ///
    /// <para>
    /// This row's server, using this row's token — not "the" Vault, because there is no longer one:
    /// each row carries its own address, and yours already differ between beta, prod and the rest.
    /// Metadata only. No value is read and nothing is written.
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task SearchVaultAsync(VaultConnectionVm row)
    {
        if (row.Searching || row.Kind != SourceKind.Vault)
        {
            return;
        }

        // Searching, not Busy: a search is metadata only and reads nothing, so it must not block — or
        // be blocked by — the Test and Load on the same row, which are the pair that touch values.
        await BusyGuard.RunAsync(
            searching => row.Searching = searching,
            async () =>
            {
                row.Status = "Searching this Vault…";

                var settings = BuildSettings();
                var result = await new VaultBrowser(settings)
                    .BrowseAsync(row.ToConnection(), row.TierId)
                    .ConfigureAwait(true);

                var found = result.Paths;

                // The path this row already reads stays in the list whatever the search returned. A token
                // that cannot list the mount still reads the secret perfectly well, and a picker that
                // dropped the current answer would make a working row look misconfigured.
                var chosen = row.SecretPath;

                row.KnownPaths.Clear();
                foreach (var path in found)
                {
                    row.KnownPaths.Add(path);
                }

                if (!string.IsNullOrWhiteSpace(chosen) && !row.KnownPaths.Contains(chosen, StringComparer.OrdinalIgnoreCase))
                {
                    row.KnownPaths.Insert(0, chosen);
                }

                row.SecretPath = chosen;

                Problems.Clear();
                foreach (var problem in result.Problems)
                {
                    Problems.Add(problem);
                }

                var jsons = found.Count(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase));

                row.Status = found.Count == 0
                    ? "Nothing found. The token may not have list permission here — a path typed in by hand still works."
                    : $"{found.Count} secret(s) found, {jsons} of them .json. The list is not saved; it is " +
                      "re-found whenever you press Search.";
            },
            ex => row.Status = $"Search failed: {ex.Message}");
    }

    /// <summary>Picks the file a local-file source reads, so a path can be chosen rather than typed.</summary>
    [RelayCommand]
    private void BrowseLocalFile(VaultConnectionVm row)
    {
        string? startingDirectory = null;
        if (!string.IsNullOrWhiteSpace(row.LocalFilePath))
        {
            try
            {
                startingDirectory = Path.GetDirectoryName(Path.GetFullPath(row.LocalFilePath));
            }
            catch
            {
                // An unusable path in the box is not a reason to refuse to open the picker — the
                // picker is how it gets replaced.
            }
        }

        var picked = JsonInsight.Platform.Platform.FilePicker.OpenFile(
            $"The JSON file {row.Label} reads",
            ["json", "jsonc"],
            startingDirectory);

        if (picked is not null)
        {
            row.LocalFilePath = picked;
            row.Kind = SourceKind.LocalFile;
            row.Status = string.Empty;
        }
    }

    /// <summary>
    /// Reads this one source and puts it on screen — the same read Pull performs, for one row.
    ///
    /// <para>
    /// Pull reads everything, which is the wrong size of act when one row's path was just corrected
    /// and the other three are fine; on four Vault servers it is also three network round trips
    /// nobody asked for. After this the Tier editor, All tiers and Text diff are all showing this
    /// source without anyone having pressed Pull.
    /// </para>
    ///
    /// <para>
    /// It reads through the same <see cref="ISourceProvider"/> registry Pull does rather than a
    /// second code path of its own, so a Vault row and a local-file row load here exactly as they
    /// would there — including the flattening, the array strategies and the secret classification.
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task LoadRowAsync(VaultConnectionVm row)
    {
        if (row.Busy)
        {
            return;
        }

        // Both views disable the button for this, so it is only reachable by a caller that went round
        // them. Said rather than ignored: a Load that quietly did nothing would look like a failed one.
        if (!row.TestPassed)
        {
            row.Status = "Test this source first — Load turns on once a test has read it. " +
                         "Load puts what it reads on the other tabs, so it waits for something that " +
                         "says the row points where you think it does.";
            return;
        }

        var definition = ToDefinition(row);
        var settings = BuildSettings();

        var missing = definition.Kind == SourceKind.LocalFile
            ? (string.IsNullOrWhiteSpace(row.LocalFilePath) ? ["file"] : Array.Empty<string>())
            : settings.Incomplete(row.TierId);

        if (missing.Count > 0)
        {
            row.Status = $"Not configured yet — missing: {string.Join(", ", missing)}.";
            return;
        }

        if (!SourceProviders.Default(_flattener).TryGetValue(definition.Kind, out var provider))
        {
            row.Status = $"No provider registered for {definition.Kind}.";
            row.StatusIsError = true;
            return;
        }

        await BusyGuard.RunAsync(
            busy => row.Busy = busy,
            async () =>
            {
                row.Status = "Loading…";

                var result = await provider.LoadAsync(definition, settings).ConfigureAwait(true);

                if (result.Document is not { } document)
                {
                    row.Status = result.Problem ?? "Could not be read.";
                    row.StatusIsError = true;
                    _main.Log.Warn($"{row.Label} could not be loaded — {row.Status}");
                    return;
                }

                _main.AdoptDocument(document);

                row.Status = $"Loaded — {document.Flat.Count} keys. It is on the other tabs now; " +
                             "no need to pull.";
                _main.Log.Info($"{row.Label} loaded on its own — {document.Flat.Count} keys.");
            },
            // A source that could not be read is a finding rather than a status line that scrolls
            // past, so this one logs as well — the row says what happened, the Logs tab keeps it.
            ex =>
            {
                row.Status = ex.Message;
                row.StatusIsError = true;
                _main.Log.Error($"{row.Label} could not be loaded — {ex.Message}");
            });
    }

    /// <summary>This row as the catalog would describe it, for a read that goes through a provider.</summary>
    private static TierDefinition ToDefinition(VaultConnectionVm row) => new()
    {
        Id = row.TierId,
        Label = row.Label,
        Kind = row.Kind,
        VaultPath = row.Kind == SourceKind.Vault ? row.SecretPath.Trim() : null,
        LocalFilePath = row.Kind == SourceKind.LocalFile ? row.LocalFilePath.Trim() : null,
    };

    /// <summary>
    /// Reads what this row points at and says what is there — a Vault secret's version and key count,
    /// or a file's key count and modification time. Keeps nothing.
    /// </summary>
    [RelayCommand]
    private async Task TestAsync(VaultConnectionVm row)
    {
        if (row.Busy)
        {
            return;
        }

        if (row.Kind == SourceKind.LocalFile)
        {
            await TestLocalFileAsync(row);
            return;
        }

        var settings = BuildSettings();
        var missing = settings.Incomplete(row.TierId);
        if (missing.Count > 0)
        {
            row.TestPassed = false;
            row.Status = $"Not configured yet — missing: {string.Join(", ", missing)}.";
            return;
        }

        var connection = row.ToConnection();

        await BusyGuard.RunAsync(
            busy => row.Busy = busy,
            async () =>
            {
                row.Status = $"Reading {connection.SecretPath}…";

                using var client = new VaultClient(connection);
                var pulled = await client.ReadAsync(connection.SecretPath).ConfigureAwait(true);

                var keys = _flattener.Flatten(row.TierId, OrdinalJsonWriter.Parse(pulled.Json)).Count;

                // This is what turns Load on, and the only thing that does. Set after the read rather
                // than around it, so a token that authenticated but a path that is not there does not
                // count as a source having been reached.
                row.TestPassed = true;

                row.Status = $"{connection.SecretPath} — Vault version {pulled.Version}, {keys} keys. " +
                             "Nothing was kept; Load is on now, and is what puts it on screen.";
            },
            // The failure arm puts TestPassed back down as well: a read that threw is a source that was
            // not reached, and leaving Load switched on from an earlier successful test would offer to
            // put a document on the other tabs that this row can no longer fetch.
            ex =>
            {
                row.TestPassed = false;
                row.Status = ex.Message;
                row.StatusIsError = true;
            });
    }

    /// <summary>
    /// Test for a local-file source: does the file exist, does it parse, and how much is in it — the
    /// same question asked of a Vault secret, answered from disk.
    /// </summary>
    private async Task TestLocalFileAsync(VaultConnectionVm row)
    {
        if (string.IsNullOrWhiteSpace(row.LocalFilePath))
        {
            row.TestPassed = false;
            row.Status = "Not configured yet — missing: file. Press Browse to pick one.";
            return;
        }

        await BusyGuard.RunAsync(
            busy => row.Busy = busy,
            async () =>
            {
                row.Status = $"Reading {row.LocalFilePath}…";

                var definition = new TierDefinition
                {
                    Id = row.TierId,
                    Label = row.Label,
                    Kind = SourceKind.LocalFile,
                    LocalFilePath = row.LocalFilePath.Trim(),
                };

                var result = await new LocalFileSourceProvider(_flattener)
                    .LoadAsync(definition, BuildSettings())
                    .ConfigureAwait(true);

                // A file that parsed is a source that was reached, which is the same bar the Vault half
                // clears above — so it turns Load on the same way.
                row.TestPassed = result.Document is not null;

                row.Status = result.Document is { } document
                    ? $"{document.FilePath} — {document.Flat.Count} keys, {result.Detail}. Load is on now."
                    : result.Problem ?? "Could not be read.";
                row.StatusIsError = result.Document is null;
            },
            // Same failure arm as the Vault half, for the same reason: a file that could not be read is
            // not a source that was reached, whatever an earlier test decided.
            ex =>
            {
                row.TestPassed = false;
                row.Status = ex.Message;
                row.StatusIsError = true;
            });
    }

    /// <summary>
    /// The sentence that refuses a save when two rows read the same place, or null when every source
    /// is its own. Two sources pointing at one secret or one file is one source counted twice — the
    /// comparison columns would agree with each other by construction and call it agreement between
    /// environments.
    ///
    /// <para>
    /// Identity follows the kind: a local-file source is its file (case-insensitively — this is a
    /// Windows path), a Vault source is its address, namespace and secret path together. The secret
    /// path is compared case-sensitively, because Vault paths are. Rows without a path yet are not
    /// duplicates of anything — a blank points nowhere, not at the same nowhere.
    /// </para>
    /// </summary>
    public string? DuplicateSourceProblem()
    {
        var rows = Connections.Where(r => !r.IsEmpty).ToList();

        for (var i = 0; i < rows.Count; i++)
        {
            for (var j = i + 1; j < rows.Count; j++)
            {
                if (!SameSource(rows[i], rows[j]))
                {
                    continue;
                }

                var what = rows[i].Kind == SourceKind.LocalFile
                    ? $"the same file — {rows[i].LocalFilePath.Trim()}"
                    : $"the same Vault secret — {rows[i].SecretPath.Trim()} at {rows[i].Address.Trim()}";

                return $"'{rows[i].Label}' and '{rows[j].Label}' read {what}. Two sources reading " +
                       "the same place is one source counted twice, so change one of them before saving.";
            }
        }

        return null;
    }

    private static bool SameSource(VaultConnectionVm a, VaultConnectionVm b)
    {
        if (a.Kind != b.Kind)
        {
            return false;
        }

        if (a.Kind == SourceKind.LocalFile)
        {
            return !string.IsNullOrWhiteSpace(a.LocalFilePath) &&
                   string.Equals(a.LocalFilePath.Trim(), b.LocalFilePath.Trim(),
                       StringComparison.OrdinalIgnoreCase);
        }

        // A Vault row can be non-empty on a token or a restart endpoint alone; without a path it
        // does not read anywhere, so it cannot read the same place as anything.
        return !string.IsNullOrWhiteSpace(a.SecretPath) &&
               string.Equals(a.SecretPath.Trim(), b.SecretPath.Trim(), StringComparison.Ordinal) &&
               string.Equals(SameAddressForm(a.Address), SameAddressForm(b.Address), StringComparison.OrdinalIgnoreCase) &&
               string.Equals(a.Namespace.Trim(), b.Namespace.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>https://vault:8200</c> and <c>https://vault:8200/</c> are the same server, and a duplicate
    /// check that let a trailing slash tell them apart would be one that misses the most likely way
    /// of typing the same address twice.
    /// </summary>
    private static string SameAddressForm(string address) => address.Trim().TrimEnd('/');

    /// <summary>
    /// The save both hosts call when the restart-config dialog closes over a change, so the endpoint
    /// lands in the project the moment it is set rather than waiting on a separate trip to Save
    /// settings. It is the ordinary save — the endpoint is part of the row, and there is no narrower
    /// write than the row's settings.
    /// </summary>
    public void SaveRestartConfig() => SaveSettings();

    /// <summary>The settings exactly as the tab currently shows them, ready to test with or save.</summary>
    public VaultSettings BuildSettings()
    {
        var settings = new VaultSettings
        {
            // Not shown on this tab, and a save that silently reset it would be a save that discards a
            // setting.
            LoadTiersAtStartup = _loadTiersAtStartup,
        };

        foreach (var row in Connections)
        {
            // A row that was never filled in is not a source. Writing it back would put an empty
            // entry in appsettings.json for every environment that has none.
            if (row.IsEmpty)
            {
                continue;
            }

            settings.Connections[row.TierId] = row.ToConnection();
        }

        // In row order, which is SourceEnvironment order, which is the order the columns appear in.
        //
        // An active row that is not configured is still written down. SourceCatalog names it as a
        // problem on the next load, which is the honest outcome: the tick was deliberate, and dropping
        // it here would make it vanish on save with nothing said about why.
        settings.ActiveSources = Connections
            .Where(c => c.Active && c.CanActivate)
            .Select(c => c.TierId)
            .ToList();

        return settings;
    }
}
