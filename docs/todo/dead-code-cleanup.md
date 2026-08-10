# Dead-code & maintainability cleanup — plan and findings (audit of 2026-08-10)

This is the working plan produced by a full dead-code / duplication audit of both front ends.
It is written to be self-sufficient: a future session should be able to pick up any unchecked
item below without re-running the audit. Read `docs/ORIENTATION.md` first — it is the map of
the codebase and lists the invariants this plan deliberately does not touch. This file is the only copy of the
report; nothing was kept online.

---

## How to resume this in a new session

**State of the tree when the audit ran.** HEAD was `41e2b71` ("fix: source tab") with the
find/highlight feature still uncommitted: modifications across ~29 files plus two untracked
paths, `JsonInsight/Views/FindHighlightAdorner.cs` (live code — `JsonEditorView.xaml.cs:268`
requires it) and `docs/`. Every file:line reference below is against that tree. If the tree
has moved since, spot-check a line number before editing around it; the symbol names are the
stable part.

**What "verified" means here.** Every "zero references" claim was established by grepping the
symbol across all six projects — including `.xaml` string bindings, `.razor` markup,
code-behind string lookups (`FindResource`, `TryFindResource`, `SetResourceReference`),
dynamically-composed CSS classes, and JS interop invocation names — because WPF binds by
string and nothing here is compiler-checked (the solution builds with **0 warnings**; that is
why all of this accumulated invisibly). The §0.1 push bug was additionally re-verified
end-to-end by hand, independently of the sweep that found it.

**Ground rules for whoever executes this** (from ORIENTATION.md §7 and the user's own rules):

- Claude never runs `git add` / `git commit` / anything that writes history. Stage and commit
  is the user's move; stop and say what is ready.
- Run both suites after every batch: `dotnet test JsonInsight.Tests/JsonInsight.Tests.csproj`
  (356 tests) and `dotnet test WebJsonInsight.Tests/WebJsonInsight.Tests.csproj` (114 tests).
  If the Photino app is running it locks its own output — build/test the web suite with
  `-p:BaseOutputPath="<scratch>/"`. Same for the WPF app + Visual Studio locking
  `JsonInsight/bin`.
- `dotnet build` at the repo root hits MSB1011 (two solution files, deliberately — see
  ORIENTATION.md); name `JsonInsight.sln` explicitly.
- Use the Grep tool, not `grep` via Bash — an `rtk` hook mangles shell grep output.
- Do not strip or shorten the why-comments; long comments are house style and load-bearing.
- UiSmokeTests renders every WPF view under both themes and turns binding-trace noise into
  failures — it is the safety net for every XAML deletion below; run it after any XAML edit.

**Suggested order:** §0 bugs first (small, high value), then §1 deletions (mechanical), then
§2 consolidations (each one a self-contained refactor), then the §3 decisions. §4 lists what
was checked and must *not* be "cleaned up".

---

## §0 — Bugs found during the audit (fix before/alongside cleanup)

- [ ] **0.1 WPF Tier-editor Push ignores text-pane edits** *(verified end-to-end; the one
  functional bug)*. `MainWindow.xaml.cs:23` wires `Editor.PushRequested` to
  `new PushVm(_vm, tier)` — no `updated` document — so `PushVm.Updated()`
  (`PushVm.cs:230-236`) falls back to the tier plus *queued grid edits*. Text-pane edits live
  only in `DocumentEditor.Working`, a `SortedClone` (`DocumentEditor.cs:57`) that never
  mutates `Tier.Root`, so the dialog diffs the unedited document and reports "already holds
  exactly this". The Blazor host does it right: `TierEditorTab.razor:427` passes
  `Vm.Editor?.Working` with the label "the document as edited on the Tier editor tab".
  Fix: pass the editor's Working document (and the same label) from the WPF host.
- [ ] **0.2 Three VMs label deleted diff lines `Imaginary`**. Of the five copied DiffPlex
  loops (§2.1), `ChangesVm.cs:261`, `PromoteVm.cs:167`, `RawDiffVm.cs:92` resolve row type
  as `newLine?.Type ?? oldLine?.Type`, which classifies every deletion as `Imaginary` — the
  exact failure mode `PushVm.cs:352`'s own comment warns about. In RawDiffVm this also makes
  the "removed" count permanently 0. Fixed automatically by §2.1; fix inline if 2.1 is
  deferred.
- [ ] **0.3 Two Blazor previews drop the `Imaginary` styling arm**. The copied `LineClass`
  switch in `PromoteDialog.razor:164-170` and `ChangesDialog.razor:163-169` omits
  `ChangeType.Imaginary → dl-imaginary`; the other three copies have it. Also fixed by §2.1.
- [ ] **0.4 Web rollup toggle sniffs a display string**. `AllTiersTab.razor:261-271` decides
  expandability with `row.Summary.Contains("only in", Ordinal)` — parsing prose built at
  `TiersVm.cs:443-445` — where WPF (`TiersView.xaml.cs:131-144`) uses `row.CanPromote`.
  Reword the summary and the Web twisty silently breaks. Fix: a `TierRowVm.IsRollup` (or
  `TiersVm.ToggleAny`) both UIs call.
- [ ] **0.5 `.btn-icon` defined twice with conflicting padding**. `app.css:136` (`0`) vs
  `app.css:617` (`4px 7px`). The second block was meant for `.btn-small.btn-icon` on source
  rows (its own comment at 613-614 says so) but applies globally; currently masked by block
  one's fixed width. Fix by *scoping* the 617 block to `.btn-small.btn-icon`, not deleting —
  deletion changes rendered padding on the row-menu button.
- [ ] **0.6 `theme.css` claims a parity test that does not exist**. Lines 13-14 assert that
  WebJsonInsight.Tests checks the light/dark token blocks match — no such test exists. The
  XAML side has one (`UiSmokeTests.Both_themes_define_the_same_brushes`, UiSmokeTests.cs:477).
  62 hex values are hand-synced across four dictionaries (Dark.xaml, Light.xaml, two blocks
  of theme.css) with no guard. Fix: a test that parses both theme sources and asserts
  name+value parity (~30 lines); until then the stale comment should go.
- [ ] **0.7 Refusal wording has drifted between the UIs** — "No *tier* holds {path}"
  (`MainWindow.xaml.cs`) vs "No *source* holds {path}" (`DialogService.cs`); "tiers that are
  marked read-only" vs "sources marked read-only". Symptom of §2.2; consolidating fixes it.
  Related divergences to reconcile while there: Web replace-one has a match-recovery step
  (`TierEditorTab.razor:549-552`) WPF lacks; WPF appends "(read-only)" to grid column headers
  where Web does not; WPF debounces the Tiers filter (`Delay=200`, `TiersView.xaml:87`) while
  the Web SearchBox fires per keystroke.
- [ ] **0.8 `aliases.json` documents an unbuilt feature**. The config note promises an
  `identity` comparison mode ("rewrites a path prefix at load time"), but both resolvers
  filter to `ShapeOnly` (`AliasSet.cs:109,190`); no identity rewrite exists anywhere. Delete
  `AliasComparison.Identity` + the parse branch + the note (§1.1), or implement the promise.
- [ ] **0.9 `PushPlan.Warnings` is permanently empty and both UIs render it**. Both producers
  construct empty lists — the warning became the `Stale` refusal, per their own comments
  (`VaultPusher.cs:252-255`, `LocalFileSourceProvider.cs:163-165`) — yet `PushVm.cs:305` and
  both PushDialogs iterate it. Delete the property and the UI plumbing (~16 LOC total).
- [ ] **0.10 `FindHighlightAdorner.cs` is live but untracked** — the inverse of dead code.
  `git add JsonInsight/Views/FindHighlightAdorner.cs docs/` (user's commit); a fresh clone
  does not build without it.

---

## §1 — Verified dead: zero references anywhere, delete today (~420 LOC)

### 1.1 JsonInsight.Core (~280 LOC, all high confidence)

- [ ] `OrdinalJsonWriter.VerifyRoundTrip` + `RoundTripResult` record + private helpers
  `DescribeFirstDifference` / `SplitLines` / `Truncate` / `CountOccurrences` —
  `OrdinalJsonWriter.cs:175-274` (~95). Served the retired snapshot-file guard;
  RoundTripTests tests `SerializeToText` fixed-points, not this API.
- [ ] `VaultClient.ProbeAsync` + `VaultProbeResult` + `CountRootKeys` —
  `VaultClient.cs:16, 428-457, 495-501` (~40). The Sources-tab Test button uses
  `ReadAsync` directly (`VaultVm.cs:854-855`).
- [ ] `EditApplier.ExpectedPaths` — `EditApplier.cs:154-178` (~25). Doc references
  `SnapshotWriter`, a type that no longer exists in the repo.
- [ ] `ArrayStrategies.Members` (superseded by `ScalarMembers`) and `ArrayStrategies.Empty` —
  `ArrayStrategy.cs:112-130, 77` (~21).
- [ ] `AliasComparison.Identity` + parse branch — `AliasSet.cs:7-14, 87-90` (~10). See §0.8.
- [ ] `AppPaths.ResolveFromRoot` — `AppPaths.cs:94-103` (~10). The relative-tier-file concept
  it served is gone (`TierDefinition` doc: "There is deliberately no file here any more").
- [ ] `PushPlan.Warnings` + UI plumbing (~16). See §0.9.
- [ ] Small orphans (~60 total, each independently verified zero-reference):
  - `TierColumn.From` — `MultiDiff.cs:51-52` (all callers construct directly)
  - `TierDiff.Find` — `TierDiffer.cs:31-32`
  - `Classifier.Permissive` — `Classifier.cs:79`
  - `AliasSet.Definitions` — `AliasSet.cs:60`
  - `OrdinalJsonWriter.ParseFile` — `OrdinalJsonWriter.cs:72`
  - `DocumentEditor.OriginalTextAt` — `DocumentEditor.cs:88-90` (tombstones use
    `OriginalTextOrEmpty`)
  - `VaultConnection.HasRestart` (the **Core** copy) — `VaultSettings.cs:104-106`; every
    caller resolves to `VaultConnectionVm.HasRestart` (`VaultVm.cs:138`)
  - `VaultBrowseResult.Source` — `VaultBrowser.cs:11-12` (set at :111, never read)
  - `FoundSecret.Address` — `VaultBrowser.cs:4` (record could collapse; only `Paths` is
    consumed)
  - `EditSet.StaleIn` — `EditSet.cs:81-83` (ChangesVm calls `IsStaleAgainst` directly)
  - `TierDocument.DisplayName` — `TierDocument.cs:75-78` (pickers bind `PickerLabel`)
  - `VaultWorkspace.Documents` — `VaultSettings.cs:187, 269` (never read by `Migrate` or any
    test)
  - `VaultWorkspace.Token` — `VaultSettings.cs:195-196, 274-276` (`[JsonIgnore]`, so it can
    never load from the file; only ProjectTests.cs:24,99 touch it — adjust those with it)
  - `ConfigDocument.FileSuffix` / `.Slug` — `ConfigDocument.cs:47, 53-73` (~28; snapshot
    file naming for the writer that no longer exists — zero refs even in tests)
- [ ] Doc rot to fix in the same pass: `SnapshotWriter` referenced at `EditApplier.cs:10,150`
  and `PromotionPlanner.cs:9`; `VaultBrowser.cs:56` names `VaultSettings.BrowseFrom` (the
  property is on `VaultWorkspace`); `VaultSettings.cs:441` names `VaultSettings.Token` (means
  `VaultWorkspace.Token`); `ThemeManager.cs:30` names a `--verify-roundtrip` mode (only
  `--check` exists, `App.xaml.cs:13`); `TiersView.xaml.cs:120` says the MultiCell template
  lives in "Theme.xaml" (it is Controls.xaml).
- [ ] Trivial: `TextFinder.cs:131` — `while (at <= text.Length)` can never be false when
  evaluated (loop exits only via `break`); effectively `while (true)`. Redundant
  `?? string.Empty` on non-nullable values at `VaultClient.cs:68,120`,
  `ConfigDocument.cs:37`, `EnvironmentRoots.cs:36`.

### 1.2 JsonInsight.Presentation (~95 LOC, all high confidence)

- [ ] `JsonEditorVm.History` + the refill loop in `NotifyState` —
  `JsonEditorVm.cs:337, 1150-1154` (~7). Never bound by either UI (test hits are Core's
  `DocumentEditor.History`); cleared+refilled on **every** edit — deleting also removes
  per-keystroke work.
- [ ] `MainVm` problems-banner remnants (~30): `DismissProblems` command
  (`MainVm.cs:811-822`), `HasProblems` (`:801`), `ProblemsHeading` (`:803-809`, sole ref is
  `WarningTests.cs:134` — inline that assert against `Problems.Count`), and the
  `Problems.CollectionChanged` subscription (`:205-213`) that exists only to notify them.
  Both UIs bind `Log.HasProblems`, not these.
- [ ] `ChangesVm.PreviewReady` (`ChangesVm.cs:73-74` + 5 assignments) and
  `PromoteVm.PreviewReady` (`PromoteVm.cs:58-59` + 3 assignments) (~12). Both dialogs in
  both UIs render `PreviewLines` directly.
- [ ] `JsonCompareVm.Preselect` — `JsonCompareVm.cs:101-113` (~13). Confirmed by
  `MainVm.cs:754`: "Opens on nothing: there are no snapshot files to preselect any more."
- [ ] Small orphans (~25): `TiersVm.AnyFromVault` (`:184` + notify `:355`),
  `TiersVm.VisibleLeafPaths` (`:524-526`), `TiersVm.Unavailable` public wrapper (`:127` +
  notify `:354`; keep the private field), `TierRowVm.IsExpected` (`:41`; the `r.IsExpected`
  at `:506` is Core's `MultiRow.IsExpected`), `EditRowVm.ActionLabel`
  (`EditVm.cs:98-103,108`), `PromoteLeafVm.SourceDisplay` (`PromoteVm.cs:26`; Core's
  `PromotionLeaf.SourceDisplay` *is* used — this re-export is not),
  `PromoteVm.Placeholders` (`:213-218`), `DiffLineVm.IsUnchanged` (`RawDiffVm.cs:14`).
- [ ] Never-bound notification state (~11): demote `MainVm.Comparing` (`:328-331`) to a plain
  property (only feeds `DocumentCaption`); `TiersVm.Busy` (`:108-113`, doc admits "only a
  re-entry guard") to a plain bool; drop `OnPropertyChanged(nameof(SelectedIsElement))`
  (`JsonEditorVm.cs:603`) and `OnPropertyChanged(nameof(Editor))` (`:1156`); make
  `LogVm.ProblemCount` (`LogVm.cs:72`) private and un-notified.

### 1.3 JsonInsight (WPF) (~13 LOC)

- [ ] Styles `Text.Title` (`Controls.xaml:40-44`) and `Text.Icon` (`:67-73`) — the only 2
  dead keys of 87; all 85 others are referenced, several only via code-behind strings
  (`FindHighlightAdorner.cs:72-73`, `TiersView.xaml.cs:79,97,108`), which is why nothing
  else in the theme files may be deleted on a plain grep.
- [ ] `WpfPlatform.cs:35-36` — `patterns` and `names` compute the identical
  `string.Join(';', extensions.Select(e => $"*.{e}"))` twice; keep one.
- The WPF layer is otherwise clean: all 4 converters used (BoolVis 43 refs, NotEmptyVis 33,
  NotBool 8, Indent 4), zero orphaned handlers or `x:Name`s.

### 1.4 WebJsonInsight (~35 LOC)

- [ ] `select()` — `interop.js:92-101` (~10). Zero invocations; its "Used by Replace" comment
  is stale — `ReplaceOne` calls `jsonInsight.reveal` instead (deliberately, per the
  focus-stealing note at `interop.js:103-109`). Keep `scrollTo`; note `syncHighlights` has
  no C# caller but is used internally by `scrollTo` — not dead.
- [ ] `DialogService.AnyOpen` — `DialogService.cs:54-56` (3).
- [ ] `PhotinoClipboard.LastCopied` — `PhotinoPlatform.cs:43-44,50` (~4). Written, never
  read; its doc claims tests use it — none do. Delete, or actually assert it in a copy test.
- [ ] Icon path entries `"promote"` (`IconPaths.cs:82`) and `"file"` (`:103-106`) (6). No
  static or dynamic `Name=` usage; the promote buttons use the `"push"` icon.
- [ ] Dead CSS (~6): `.check.dense` (`app.css:433`), `.rowmenu-item input` (`:657` — the
  checkbox it styled was replaced per the comment at :666), the three no-op `.difflines`
  re-declarations (`:1223, 1306, 1316` — base rule at :932 already sets `flex:1;
  min-height:0`), and the unconsumed `--tier-count` style attribute
  (`AllTiersTab.razor:110`). All 235 other selectors verified used, including the
  dynamically-composed `mark-*` / `cell-*` / `log-*` / `class-*` families — those are used
  via prefix-matched dynamic strings; do not delete on grep evidence.
- [ ] `_Imports.razor` unused namespaces (~6): `System.Net.Http`, `Components.Forms`,
  `Components.Routing` are certain; `JsonInsight.Classify` / `.Loading` / `.Vault` were
  verified by type-level grep only — confirm with a build before deleting.
- [ ] Mergeable duplicate CSS: `.pathpicker-veil` ≡ `.rowmenu-scrim` (`app.css:537` vs
  `:621`, byte-identical); `.boot` ≡ `.empty` bodies (`:29-34` vs `:265-270`).

---

## §2 — Duplication to consolidate (~800 LOC net)

Ranked by (lines saved × confidence) ÷ effort. Items 1-3 fix §0 bugs as a side effect —
that, not the line count, is the point: three divergences already shipped inside these copies.

- [ ] **2.1 One diff-line pipeline (~280 LOC, medium effort).** Four moves, one theme:
  1. Extract a single builder in Presentation — e.g.
     `DiffLineVm.Build(before, after, includeUnchanged)` returning lines + counts — replacing
     the five copied DiffPlex loops: `JsonEditorVm.RebuildComparison` (1064-1107),
     `PushVm.BuildDiff` (340-404), `ChangesVm.Preview` (256-274), `PromoteVm.Preview`
     (162-180), `RawDiffVm.Rebuild` (81-119). Use the documented old-side-first row type
     (the `JsonEditorVm`/`PushVm` variant) — this fixes §0.2.
  2. Expose the classification (e.g. `DiffLineVm.Kind`: added/removed/modified/imaginary) so
     the five copied Razor `LineClass` switches (`TierEditorTab.razor:440-447`,
     `TextDiffTab.razor:114-121`, `PushDialog.razor:159-166`, `PromoteDialog.razor:164-170`,
     `ChangesDialog.razor:163-169`) collapse — fixes §0.3 — and WPF's parallel trigger
     styles (`Controls.xaml:1108-1136`) read the same source.
  3. A shared `DiffLinesView` Razor component (Lines + headers + empty-message parameters)
     replacing the five copied `dl-row`/`dl-num`/`dl-text` viewers
     (`TierEditorTab.razor:218-233`, `TextDiffTab.razor:70-89`, `PushDialog.razor:89-108`,
     `PromoteDialog.razor:85-100`, `ChangesDialog.razor:101-117`).
  4. Merge WPF's five near-identical diff-row DataTemplates into two shared keyed templates
     in Controls.xaml: two-sided at `JsonEditorView.xaml:546-575`, `PushDialog.xaml:166-194`,
     `RawDiffView.xaml:20-43` (only real difference: number column 40 vs 46); one-sided at
     `ChangesDialog.xaml:179-193` ≡ `PromoteDialog.xaml:143-157` (byte-identical).
- [ ] **2.2 Dialog guards → Presentation (~100 LOC, medium effort).**
  `MainWindow.xaml.cs:40-140` and `DialogService.cs:61-171` hold the same restart / push /
  promote / edit(60-key cap) / changes guards line-for-line — DialogService says so itself
  ("The guards are ported rather than reinvented", :19; `MaximumEditRows = 60` "Matches the
  WPF window's own constant", :27-28). DialogService is already UI-free (references only
  VMs); move the guard predicates + refusal texts down (methods on MainVm or a
  `WriteFlowGuards` class returning VM-or-refusal); each UI keeps only "show dialog / show
  message". Fixes §0.7, and WPF inherits the Web-side guard tests
  (`WritePathTests.cs:42-87`) for free.
- [ ] **2.3 Find/replace orchestration → `JsonEditorVm` (~55 LOC, small-medium).**
  Replace-one (`JsonEditorView.xaml.cs:179-207` vs `TierEditorTab.razor:523-558`),
  replace-all (`:209-229` vs `:560-572`, both hand-format `FindStatus`), and the
  Enter/Shift+Enter/F3/Escape mapping (`:82-113` vs `:455-477`) are duplicated and have
  diverged (Web's recovery step at razor :549-552). VM gains `ReplaceCurrent()`,
  `ReplaceAllInPane()`, `HandleFindKey`. Stepping/matching is already shared
  (`StepMatch`/`SyncMatchToCaret`); highlight *painting* (FindHighlightAdorner vs
  `HighlightRuns` + `.pane-highlights`) is legitimately platform-specific — leave it.
- [ ] **2.4 Core internal dedup (~145 LOC, small pieces):**
  - `AliasSet.ExpandRoots` (313-335) ≡ `ExpandRootsAcross` (271-292) byte-for-byte;
    delegate one to the other (~22).
  - Three token-key parsers in `VaultSettingsStore` — `LegacyTierIdFromTokenKey` (562-576),
    `TierIdFromTokenKey` (711-725), `ParseTokenKey` (727-753) — one helper (~25).
  - `AliasSet.Resolve`+`TryAdd` vs `ResolveMulti`+`TryAddMulti`: same engagement rules
    twice; `Resolve` = `ResolveMulti` over two tiers mapped to `ResolvedAlias` (~45; lean on
    the existing alias tests for parity).
  - `EditValidator.Describe` (173-182) ≈ `DiffEntry.Describe` (152-161): two
    JsonValueKind→word switches differing only in "bool"/"boolean" (~10).
  - `JsonDocumentOptions { Skip, AllowTrailingCommas }` constructed inline 6× while
    `OrdinalJsonWriter.DocumentOptions` already exists (~12).
  - Smaller: keyed-element scan in `DocumentEditor.ElementSlot` (313-324) vs
    `JsonNavigator.Step` (74-85); `LocalFileSourceProvider.Build` (77-92) ≈
    `TierLoader.LoadFile` (31-63); `VaultTierResult` ≈ `SourceLoadResult` shape.
- [ ] **2.5 Blazor VM-swap boilerplate (~60 LOC, small).** The identical
  `_observed`/`OnParametersSet`/`ReferenceEquals`/`StopObserving`/`Observe` block in all 7
  tabs (`TierEditorTab:358-369`, `SourcesTab:269-280`, `AllTiersTab:225-245`,
  `ProjectsScreen:163-177`, `TextDiffTab:99-109`, `CompareFilesTab:121-131`,
  `LogsTab:60-70`). Absorb into `ObservingComponent` (helper or generic base with a `Vm`
  parameter).
- [ ] **2.6 Busy-guard boilerplate (~35 LOC, small).** `if (Busy) return; Busy = true;
  try/catch(report)/finally` ×8: `VaultVm.SearchVaultAsync/LoadRowAsync/TestAsync/
  TestLocalFileAsync`, `TiersVm.PullFromVaultAsync`, `PushVm.CheckVaultAsync/PushAsync`,
  `RestartVm.CallAsync` → one `RunBusyAsync(work, report)` helper; per-site messages stay
  as lambdas.
- [ ] **2.7 Small cross-UI consolidations (~115 LOC, small each):**
  - Rollup predicate → shared VM property (§0.4, ~15).
  - Changes/Promote → Push handoff: the same `Tier/Destination is {} && BuildUpdated() is {}`
    then `new PushVm(main, tier, updated, What)` in `ChangesDialog.xaml.cs:24-38`,
    `PromoteDialog.xaml.cs:22-36`, `ChangesDialog.razor:155-161`,
    `PromoteDialog.razor:156-162` → `ChangesVm.CreatePushVm()` / `PromoteVm.CreatePushVm()`
    (~25).
  - Column header formation + case-insensitive document-by-id lookup → `TiersVm.ColumnHeader`
    record + `MainVm.DocumentById` (sites: `TiersView.xaml.cs:45-114`,
    `AllTiersTab.razor:114-121,300-306`, `TextDiffTab.razor:111-112`,
    `TierEditorTab.razor:40`, plus the guards in 2.2) (~28). Reconcile the "(read-only)"
    suffix divergence here.
  - Cell tooltip → computed `MultiCell.Tooltip` in Core (Web `CellTitle`,
    `AllTiersTab.razor:313-320` vs `Controls.xaml:1082,1096-1100`; texts currently differ)
    (~12).
  - Tree change-mark glyphs (+/✏/−/*) → node VM (`TierEditorTab.razor:431-438` vs
    `JsonEditorView.xaml:184-208` triggers) (~10).
  - Enum `TryParse` change-handler helper (`EditDialog.razor:144-158`,
    `PromoteDialog.razor:140-146`, `SourcesTab.razor:362-368`) (~15).
  - Glob-vs-substring filter dispatch duplicated verbatim — `JsonEditorVm.MatchingPaths`
    (1396-1399) vs `TiersVm.MatchesFilter` (519-522) → one `PathFilter.Matches` (~5).
  - `SourceProviders.For(tier, flattener).Blocked(...)` probe duplicated at
    `JsonEditorVm.OnTierChanged:574-575` and `PushVm.OnTierChanged:215-216` — also note
    `PushVm.Provider` (`PushVm.cs:49`) re-invokes `SourceProviders.For` on every property
    access; cache it (~4).

---

## §3 — Needs a product decision before deleting (~300 LOC)

- [ ] **3.1 The non-root "document" subsystem (~185 LOC, medium confidence).**
  `EnvironmentRoots.cs` (whole file, 66), the non-root branch of `DocumentTiers`
  (`DocumentTiers.cs:37-113` incl. `KnownRoots`, `Derive`), `ConfigDocument.Parse` (80-84) /
  `PathUnder` (35-39), `TierDefinition.Document` (68-73), and `MainVm`'s vestigial field +
  parameter (`MainVm.cs:27-38, 435` — self-documented: "Always the root now, and kept only
  because DocumentTiers still takes one"). Every production caller passes
  `ConfigDocument.Root`; per-connection `SecretPath` now carries the whole path; only
  DocumentTests/SourcesTabTests exercise the non-root path. If multi-document projects are
  not coming back in this form, delete the subsystem and drop the parameter; if they are,
  leave it and note that here.
- [ ] **3.2 Test-only public APIs (~100 LOC).** Only their tests reference these — delete
  with the tests, or keep deliberately as seams (decide per item):
  `TextFinder.Next/Previous/LastIndexBefore/Ordinal/Count` (`TextFinder.cs:19-61,92-107`,
  ~55 — superseded by `All`-based stepping per `All`'s own doc);
  `DocumentEditor.ChangedPaths` (472-473); `OrdinalJsonWriter.ReadText` (74-79);
  `VaultTierLoader.IsVaultBacked` (43-45); `EditSet.Add(PendingEdit)` (48);
  `TiersConfig` indexer (`TierDefinition.cs:95-97`); `VaultConnection.RestartToken`
  (`VaultSettings.cs:91-102` — exists as a structural never-persist guard; its only refs are
  RestartTests asserting it never serializes). **Checked and NOT in this list** (genuine
  documented seams): `MainVm.Seed`, `ProjectsVm.Seed`, `Platform.Reset`,
  `VaultSettingsStore.AmbientTokenLookup`, `VaultClient` internals, `PushVm(checksOnOpen:)`,
  `MainVm(vaultAtStartup:)`.
- [ ] **3.3 `VaultConnectionVm.Namespace`** (`VaultVm.cs:51-52,290`) — round-trips
  ctor→`ToConnection()` but **no UI can view or edit a Vault namespace**. Either add the
  field to both Sources UIs or demote to a plain get-only property (keeps settings
  round-trip).
- [ ] **3.4 `Leaf.ConfigurationKey` (~20 LOC).** Set at `Flattener.cs:250`, threaded through
  every Flattener signature, read by nobody (not serialization — Leaf is never
  JSON-serialized). Documented intent ("trace to runtime behaviour") with no consumer.
- [ ] **3.5 API-surface trims (no LOC, less to misuse):** `VaultVm.BuildSettings` → internal;
  `EditVm.NormalizeKind`, `JsonEditorVm.RefreshMatches` → private; `TiersVm.Edits` alias →
  inline; `DocumentTiers.KnownRoots`, `LocalFileSourceProvider.Build`,
  `OrdinalJsonWriter.DocumentOptions` visibility down. Also: the `sort` parameter on
  `OrdinalJsonWriter.Serialize/SerializeToText/SerializeCompactToText` (130,143,155) is
  never passed `false` anywhere — remove it.
- [ ] **3.6 Icon.razor `StrokeWidth` parameter** — never set by any of ~60 usages; inline
  the 1.7 or keep as API surface.
- [ ] **3.7 Optional: shared `JsonInsight.Presentation.Tests` project.** ~150-250 test LOC
  duplicated or misplaced today: `ScalarArrayTests` duplicates `ArrayNodeTests` cases
  (`An_object_array_still_expands`, string-array-as-leaf); `RenderTests.cs:406-501`
  re-exercises stepping covered by `EditorPaneTests.cs:278-468`; Core's `RestartTrigger` is
  covered **only** via the Web suite (`RestartTests.cs:38-165`), and PushVm's
  `ConfirmMatches`/`CanPush` only via `WritePathTests.cs:96-138`. Consolidating 2.2 moves
  the guard tests naturally.

---

## §4 — Checked, looks like cruft, is not. Do not "clean up".

- **`Dark.xaml` / `Light.xaml` ~100% structural duplication** — the intentional WPF
  dictionary-swap pattern (`ThemeManager.cs:40` swaps index 0); drift is guarded by
  `Both_themes_define_the_same_brushes`. Consolidation machinery would cost more than the
  ~40 lines it saves.
- **Dialogs re-declaring `FontFamily`/`Foreground`/`Background`/`UseLayoutRounding`** —
  required: the implicit `Window` style (`Controls.xaml:1291-1298`) does not apply to
  `Window` subclasses; its only consumer is UiSmokeTests' plain host window.
- **theme.css tokens with equal values** (`--brush-missing` = `--brush-removed` etc.) —
  documented at theme.css:69-74 as independently changeable palettes; do not alias.
- **The write-path fences** in `VaultPusher` / `LocalFileSourceProvider` — each fence exists
  for a specific documented failure (ORIENTATION.md §6); the parallel structure is
  deliberate. At most, the ~12-line read-back-verify block (`VaultPusher.cs:361-378` vs
  `LocalFileSourceProvider.cs:236-249`) could share a helper.
- **Both solution files** (`.sln` + `.slnx`) — deliberate; `AppPaths` treats either as the
  repository marker.
- **The long why-comments** — house style (ORIENTATION.md §9), load-bearing. No count in
  this plan includes comment stripping.
- **VaultVm's single-host members** — `MenuOpen`/`ToggleMenu`/`CloseMenus` are Blazor-only,
  `PushHint`/`StatusLine`/`FilterHint`/`TargetDescription`/`PushedTier`/`ThemeGlyph` are
  WPF-only, `HasMatches`/`ToggleFindCommand`/`SecretsFile`/`HasToken`/`TierIsFile`/`Queued`/
  `SelectAllCommand` are web-only — every one verified used by at least one host; the
  shared-VM design accepts this asymmetry (doc comment on the menu members says so).
- **`VaultSettingsStore.AmbientToken` re-reading on every call** — documented; tests replace
  `AmbientTokenLookup` exactly as designed.
- **The `mark-*` / `cell-*` / `log-*` / `class-*` CSS families** — used via dynamically
  composed class strings; grep for the full name finds nothing. Verified against the actual
  enum values (`NodeChange`, `CellState`, `ValueClass`, `LogVm.LevelKey`).

---

## Numbers at a glance

| Bucket | LOC |
|---|---|
| Verified dead, delete today (§1) | ~420 |
| Duplication → consolidate (§2) | ~800 net |
| Needs a decision (§3) | ~300 |
| **Total reduction potential** | **~1,500 of ~26,000 (≈6%)** |

Plus ~150-250 test LOC (§3.7) and the ~10 §0 fixes, of which 0.1 is the only user-visible
functional bug.

*Audit method: five parallel cross-reference sweeps (Core / Presentation / WPF / Web /
cross-platform), 2026-08-10. If this file and reality disagree, reality wins — re-grep the
symbol before deleting, and update this file in the same pass (ORIENTATION.md's closing rule
applies here too).*
