# Dead-code & maintainability cleanup — what was applied (2026-08-11)

Audit ran 2026-08-10 against `41e2b71` + uncommitted work; re-verified claim-by-claim on 2026-08-11
against `ac8c531`; **applied on 2026-08-11**. This file is now the record of what was done, what was
deliberately not done, and what still needs a decision. Read `docs/ORIENTATION.md` first.

**State at the end of the pass:** `dotnet build JsonInsight.sln` — 8 projects, **0 errors, 0
warnings**. `JsonInsight.Tests` — **364 passed** (was 357). `WebJsonInsight.Tests` — **117 passed**
(was 114). Nothing is staged or committed; the whole change is in the working tree for review.

**Test count arithmetic**, so nobody has to wonder where the numbers went: +3 diff-pipeline
regressions and +2 alias/type-word regressions in `DiffTests`, +2 replace-walk regressions in
`EditorPaneTests`, +1 push-wiring regression in `UiSmokeTests`, −1 `ProjectTests` test deleted with
the property it existed to assert (357 + 8 − 1 = 364). Web: +3 `ThemeTests` (114 + 3 = 117).

---

## §0 — Bugs. All closed.

- [x] **0.1 WPF Tier-editor Push ignored text-pane edits** *(the one user-visible bug)*. The Tier
  editor's `PushRequested` now carries a `PushRequest(Tier, Updated, What)` — the view's own
  `Editor.Working` and the same label the Blazor host uses — and `MainWindow.Push` passes all three
  to `PushVm`. Regression test: `UiSmokeTests.Pushing_from_the_tier_editor_offers_the_edited_pane`
  raises the real button (the button gained an `x:Name` for this) and asserts the payload is the
  editor's working document. Previously the dialog diffed the unedited tier and announced that the
  source "already holds exactly this", silently discarding the tab's work.
- [x] **0.2 Three VMs labelled deleted diff lines `Imaginary`** — fixed at the source by §2.1: there
  is now exactly one row-type resolution in the repo (`DiffLineVm.RowType`, old-side-first). Deletions
  render red on all five screens instead of as blank rows, and the Text diff's "removed" count is no
  longer permanently 0.
- [x] **0.3 Two Blazor previews dropped the `Imaginary` styling arm** — fixed by §2.1: one
  `DiffLinesView` component holds the only `ChangeType`→class switch, and it has the arm.
- [x] **0.4 Web rollup toggle sniffed a display string** — both hosts now call `TiersVm.ToggleAny`.
  Rewording the row summary can no longer break the Web twisty.
- [x] **0.5 `.btn-icon` defined twice with conflicting padding** — the row-menu rule is now scoped
  `.btn-small.btn-icon`, which is what its own comment always claimed it was. Scoped rather than
  deleted, so the row-menu button's padding is unchanged.
- [x] **0.6 `theme.css` claimed a parity test that did not exist** — it exists now:
  `WebJsonInsight.Tests/ThemeTests.cs` asserts the two CSS blocks define identical token sets, that
  every `Brush.*` key in `Dark.xaml` has a CSS token under the translated name
  (`Brush.SurfaceAlt` → `--brush-surface-alt`), and that the web-only extras are exactly the
  documented seven. The theme.css comment now describes what is actually checked.
  **Note for future work:** the token sets are deliberately asymmetric — 31 XAML brush keys against
  38 CSS tokens per block. The seven extras (`added`, `added-soft`, `edited`, `edited-soft`,
  `removed`, `removed-soft`, `holds`) are the editor's change marks and the "holds" accent, which WPF
  renders through Controls.xaml triggers over the findings brushes instead of as named theme keys.
- [x] **0.7 Refusal wording had drifted between the UIs** — settled by §2.2. Both hosts now render
  refusals produced by one `WriteFlows`, on the Sources tab's vocabulary ("source", not "tier").
  Also reconciled: the 60-key cap is one constant. *Still divergent, deliberately:* WPF appends
  "(read-only)" to grid column subtitles where Web does not, and WPF debounces the Tiers filter
  (`Delay=200`) where the Web SearchBox fires per keystroke. Both are cosmetic and neither is a
  duplicated code path any more.
- [x] **0.8 `aliases.json` documented an unbuilt feature** — `AliasComparison.Identity`, its parse
  branch and the note's promise are gone.
- [x] **0.9 `PushPlan.Warnings` was permanently empty and both UIs rendered it** — removed
  end-to-end: the record parameter, both producers, `PushVm.Warnings`, both PushDialogs, and four
  construction sites in `PushTests` (one of which, a target-typed `Plan(...)` helper, the plan
  missed) plus a `UiSmokeTests` usage the plan also missed.
- [x] **0.10 `FindHighlightAdorner.cs` live but untracked** — already resolved before this pass.
- [x] **0.11 `CanReplace` was consolidated on one side only** — the Blazor bar's two buttons now ask
  `Vm.CanReplace` instead of re-assembling `!HasMatches || IsEditorReadOnly`, so the property's doc
  comment ("asked here so the two bars cannot disagree") is true. WPF's bar already used it.
- [x] **0.12 Both ReplaceAll "not found" arms were unreachable** — gone with §2.3; `ReplaceAllInPane`
  documents why the count cannot be zero.
- [x] **0.13 WPF's ported wrap-recovery branch was unreachable and its comment wrong** — fixed by
  making the replace go through the view model, which updates the text synchronously. The branch is
  now live on both hosts, and the "replacing the **last** match" explanation is corrected to "the
  **first**", which is the case that actually produces it.
- [x] **0.14 Replace-one walked in opposite directions on the two hosts** — both now call
  `JsonEditorVm.ReplaceCurrent()`, and the direction is pinned by
  `EditorPaneTests.Replacing_repeatedly_walks_forwards_through_every_match`. `SyncMatchToCaret`'s doc
  no longer promises "or the first one after it", which it never did.
- [x] **0.15 Docs went stale on the adorner → layer rename** — `ORIENTATION.md` §8 and §10 corrected
  (including the mechanism: a layer *behind* a transparent TextBox, not an adorner over it). README's
  "both shortcuts work from anywhere on the tab, in both front ends" now states the truth: Ctrl+F is
  tab-wide on both, F3 is tab-wide in WPF and works in the find box and pane on the web.

---

## §1 — Verified dead. All deleted.

All four packages landed; every symbol was re-grepped immediately before deletion and none had been
resurrected by the post-audit commits. Doc rot listed alongside each item was fixed in the same pass.

- [x] **1.1 JsonInsight.Core** — the `OrdinalJsonWriter` round-trip cluster (incl. `ParseFile`), the
  `VaultClient` probe cluster, `EditApplier.ExpectedPaths`, `ArrayStrategies.Members`/`.Empty`,
  `AliasComparison.Identity`, `AppPaths.ResolveFromRoot`, `PushPlan.Warnings`, and every small orphan
  on the list. Stale `SnapshotWriter` / `BrowseFrom` / `--verify-roundtrip` / "Theme.xaml" doc
  references fixed. `TextFinder`'s always-true loop condition and four redundant `?? string.Empty`
  removed.
- [x] **1.2 JsonInsight.Presentation** — `JsonEditorVm.History` and its per-keystroke refill, the
  `MainVm` problems-banner remnants, both `PreviewReady` properties, `JsonCompareVm.Preselect`, every
  small orphan, and the never-bound notification state (`Comparing` and `Busy` demoted,
  `LogVm.ProblemCount` made private, two dead `OnPropertyChanged` calls dropped).
- [x] **1.3 JsonInsight (WPF)** — dead styles `Text.Title` and `Text.Icon`; `WpfPlatform`'s duplicated
  `string.Join`.
- [x] **1.4 WebJsonInsight** — `interop.js` `select()`, `DialogService.AnyOpen`,
  `PhotinoClipboard.LastCopied`, two dead icon paths, the dead CSS, the two duplicate CSS pairs
  merged, and all six unused `_Imports` namespaces (verified by enumerating every public type in
  those namespaces, not by grepping the namespace name).

**Where the plan was wrong**, recorded so the next audit trusts the right things:

- `VaultWorkspace.Documents` was no longer test-free — new `ProjectTests` fixtures set and asserted
  it, so its deletion took two test edits with it.
- `VaultSettings.cs:75` was not a stale `<see cref>`; the only genuinely stale one was at `:440`.
- `AliasSet`'s parse branch could not be deleted at the two lines given — the condition above it was
  part of the same ternary.
- `VaultBrowser.cs:56`'s doc was *false*, not merely mis-targeted, so it was rewritten rather than
  re-pointed.
- One test (`The_shared_token_survives_migration_for_the_secrets_merge_to_use`) existed only to assert
  a deleted property and was removed with it; its reasoning already lived on a neighbouring test.

---

## §2 — Duplication. Consolidated.

- [x] **2.1 One diff-line pipeline.** `DiffLineVm.Build(before, after, includeUnchanged)` returns a
  `DiffLines` record (lines + counts); all five DiffPlex loops call it, and exactly one
  `SideBySideDiffBuilder` call site remains. One `DiffLinesView.razor` replaces the five copied Razor
  viewers *and* their five class switches. WPF's five diff-row templates became one shared column
  block plus three thin keyed templates (`DiffRow`, `DiffRow.Edit`, `DiffRow.Preview`) — two rather
  than one two-sided key because a `DataTemplate` cannot take a parameter and the editor's amber
  Modified wash is a real distinction. Number column unified on 46 (the editor's was 40).
- [x] **2.2 Dialog guards → Presentation.** `WriteFlows` returns `Guarded<T>` (a view model, a
  refusal, or neither — the last being Review changes with an empty change set, which is deliberately
  silent). Both hosts keep only "show dialog / show refusal". WPF inherits the Web-side guard tests.
- [x] **2.3 Find/replace orchestration → `JsonEditorVm`.** Gained `ReplaceCurrent()`,
  `ReplaceAllInPane()`, and the key map as `FindBoxKey` / `PaneKey`. The map is split in two on
  purpose: the find field answers Enter, the pane deliberately does not, because in a JSON pane Enter
  types a newline. Closes 0.11–0.14. Highlight *painting* stays per-host, as intended.
- [x] **2.4 Core internal dedup.** Five of seven merged (~150 LOC): `ExpandRoots` deleted as
  redundant, the token-key parsers reduced to two shared helpers, `Resolve` reimplemented over
  `ResolveMulti`, the two `JsonValueKind`→word switches merged, the six inline `JsonDocumentOptions`
  pointed at `OrdinalJsonWriter.DocumentOptions`, the keyed-element scan shared, and
  `VaultBrowser.Endpoint` collapsed. **Two deliberately not merged** — see "Judged and left alone".
- [x] **2.5 Blazor VM-swap boilerplate.** Seven tabs now inherit `ObservingComponent<TVm>`, which owns
  the `Vm` parameter and the swap. **This fixed a real leak:** `AllTiersTab` never detached its
  `DocumentsChanged` handler when the view model was replaced, so an old `TiersVm` stayed alive
  re-rendering the tab; the base now has a paired `StopObservingAlso` hook.
- [x] **2.6 Busy-guard boilerplate — 7 of 9 sites.** `BusyGuard.RunAsync(setBusy, work, report)` owns
  the one invariant worth centralising: the flag goes up, a failure is reported, and the flag comes
  down in a `finally` nobody can forget. The re-entry guard deliberately stayed at each call site —
  four sites run configuration pre-checks *between* the guard and the flag, and folding the guard in
  would have let a second press write "Not configured yet" over a read still in flight. Three distinct
  flags are in play (`Busy`, `row.Busy`, `row.Searching`), two of them per-row, which is also why this
  is a static helper rather than a base class.
  **Two sites refused, correctly:** `PushVm.PushAsync` has no `finally` at all — it clears `Busy`
  mid-method and runs ~40 lines unguarded afterwards, so a `finally`-based helper would hold the flag
  across `_main.Reload()` and newly swallow its exceptions. `MainVm.RefreshFromVaultAsync` has no
  re-entry guard to share (the plan was wrong that it guards on `VaultBusy` — it only ever sets it),
  and its `catch` produces the method's return value.
  **Honest note on the payoff:** this one is near break-even on lines. The win is uniformity and the
  un-forgettable `finally`, not size — worth knowing before someone measures it.
- [x] **2.7 Small cross-UI consolidations.** Done: the rollup predicate (0.4), the Changes/Promote →
  Push handoff (a shared `PendingPush` record and a `PendingPush()` method on both view models,
  replacing the two-part guard at four sites and the anonymous tuple in the Blazor callback), the
  enum-from-`<select>` helper (`Choice.Set`, five sites), and the cell tooltip
  (`MultiCell.Tooltip` in Core). The tooltip merge also fixed a real gap: WPF bound the tooltip to
  `Detail`, which is null for a leaf cell, so the desktop grid showed **no** tooltip on exactly the
  cells whose values are trimmed to fit the column. `PushVm.Provider` is now cached and invalidated in
  `OnTierChanged`, and that method's own `SourceProviders.For(...).Blocked(...)` probe refills the
  cache instead of building a second registry. Not done: the tree change-mark glyphs, the
  column-header/document-by-id lookup, the glob-vs-substring filter dispatch
  (`JsonEditorVm.MatchingPaths` vs `TiersVm.MatchesFilter` → one `PathFilter.Matches`), and the
  `JsonEditorVm.OnTierChanged` half of the provider-probe bullet — see below.

---

## Judged and left alone (with reasons, so this is not re-opened blindly)

- **`LocalFileSourceProvider.Build` ≈ `TierLoader.LoadFile`** — three material differences, not one:
  one throws where the other assumes existence, one synthesises a `Writable = false` definition (the
  read-only fence of ORIENTATION §6.1) where the other takes the configured writable one, and only
  one records `FileModifiedUtc` (the save-time staleness baseline). Merging would thread the
  read-only fence through a shared function as a boolean. ~4 LOC of genuine overlap; not worth it.
- **`VaultTierResult` ≈ `SourceLoadResult`** — the extra members are the point of the boundary:
  `VaultSourceProvider` is precisely where a Vault-specific result becomes the provider-agnostic
  `Detail` string. Two honest records beat one leaky one.
- **Tree change-mark glyphs (§2.7)** — cannot move to the node view model without making things
  worse. WPF's triggers exist mainly to set `Foreground`, and house style forbids naming a colour
  outside the theme files, so the triggers stay regardless; and WPF's Edited mark deliberately borrows
  the icon font (`&#xE70F;`) because the pencil has no legible ASCII form, which the browser cannot
  use. Only the Blazor switch would collapse, and it has nowhere shared to collapse *to*.
- **Column header + document-by-id lookup (§2.7)** — not attempted this pass; it spans `TiersVm`,
  `MainVm` and four views, and `TiersVm`/`MainVm` were held by the in-flight §2.6 work. Still worth
  doing, and it would settle the "(read-only)" suffix divergence and one case-sensitive id comparison
  (`TierEditorTab.razor` uses `==` where every other site uses OrdinalIgnoreCase).
- Everything in the original **§4** ("checked, looks like cruft, is not") was left alone as
  instructed: the theme dictionary pattern, the dialog font/brush re-declarations, equal-valued theme
  tokens, the write-path fences, both solution files, the long why-comments, VaultVm's single-host
  members, `AmbientToken` re-reading, and the dynamically-composed CSS families.

---

## Deliberate behaviour changes — worth a look before committing

1. **`DiffEntry.Detail` now reads "number vs boolean" rather than "number vs bool".** Merging the two
   `JsonValueKind`→word switches forced a choice; "boolean" won because `EditVm.KindOptions` already
   labels the Edit dialog's picker `string / number / boolean / null`, so the warning and the picker
   beside it now agree. Visible in the Compare-files detail column and the All-tiers detail strip.
   Pinned by a new test.
2. **Five settings/rule files now parse with `MaxDepth = 128` instead of the default 64.** A
   consequence of pointing the six inline `JsonDocumentOptions` at `OrdinalJsonWriter.DocumentOptions`,
   which carries that setting. It only widens what parses, on files that are a handful of levels deep
   by construction. Trivial to back out by giving the shared options a 64 sibling if you would rather
   not widen it.
3. **The All-tiers cell tooltip changed on both hosts** — WPF gains one where it had none on leaf
   cells; the Blazor wording is now the shared sentence. The Blazor tab still shows the *specific*
   unavailable reason, which only the view model knows.
4. **Non-`btn-small` icon buttons lose a `4px 7px` padding** they were only receiving because the
   row-menu rule was unscoped. Their fixed width and centred content make this visually inert, but it
   is a rendering change.

---

## Found while working — not fixed, needs a decision

**`MainVm.RefreshFromVaultAsync` has no re-entry guard.** It sets `VaultBusy` but never reads it, and
nothing else guards it either — so two overlapping pulls are possible in a way the other seven async
commands are not. Left exactly as found, because adding a guard is a behaviour change, but it is the
odd one out now that the rest are uniform.

Two pre-existing bugs in `VaultSettingsStore`'s token-key parsing, preserved exactly as they were
because fixing them changes behaviour:

1. **A malformed secrets key can crash workspace loading.** A key of `"Vault:Connections:Token"`
   satisfies both the prefix and the `":Token"` suffix with overlap, so the substring range has start
   > end and throws `ArgumentOutOfRangeException`. `MergeSecrets` iterates every key in secrets.json
   and does not catch. A one-line length guard fixes it.
2. **A project name containing `:` is handled inconsistently** — such a project's tokens are surfaced
   on load but never pruned on save, because the two parsers disagree about whether the key is
   recognisable. This is why the three parsers were reduced to two shared helpers rather than one.

---

## §3 — Still needs a product decision (nothing here was applied)

- [ ] **3.1 The non-root "document" subsystem (~185 LOC).** `EnvironmentRoots.cs`, the non-root branch
  of `DocumentTiers`, `ConfigDocument.Parse`/`PathUnder`, `TierDefinition.Document`, and `MainVm`'s
  vestigial field and parameter. Every production caller passes `ConfigDocument.Root`; only
  `DocumentTests`/`SourcesTabTests` exercise the non-root path. **Delete it, or keep it and say here
  that multi-document projects are coming back.**
- [ ] **3.2 Test-only public APIs (~100 LOC).** `TextFinder.Next/Previous/LastIndexBefore/Count/
  Ordinal`, `DocumentEditor.ChangedPaths`, `OrdinalJsonWriter.ReadText`, `VaultTierLoader.IsVaultBacked`,
  `EditSet.Add(PendingEdit)`, the `TiersConfig` indexer, `VaultConnection.RestartToken`. Delete with
  their tests, or keep deliberately as seams — decide per item. (`ReadText` is now *purely* test-only:
  §1.1 deleted `ParseFile`, its only other caller.)
- [ ] **3.3 `VaultConnectionVm.Namespace`** — round-trips through settings but no UI can view or edit
  it. Add the field to both Sources UIs, or demote to a get-only property.
- [ ] **3.4 `Leaf.ConfigurationKey`** — set by the Flattener, threaded through every signature, read by
  nobody.
- [ ] **3.5 API-surface trims.** `VaultVm.BuildSettings` → internal (needs `InternalsVisibleTo` or a
  test change — its external callers are tests), `EditVm.NormalizeKind` and
  `JsonEditorVm.RefreshMatches` → private, `TiersVm.Edits` inlined, three visibility reductions, and
  the `sort` parameter on the three `OrdinalJsonWriter.Serialize*` overloads that is never passed
  `false`.
- [ ] **3.6 `Icon.razor` `StrokeWidth`** — never set by any of ~60 usages. Inline the 1.7 or keep it.
- [ ] **3.7 A shared `JsonInsight.Presentation.Tests` project.** Still worth considering: Core's
  `RestartTrigger` is covered only via the Web suite, `PushVm.ConfirmMatches`/`CanPush` only via
  `WritePathTests`, and the repository-root walk is now written out in two test projects
  (`RepositoryHygieneTests` and the new `ThemeTests`) because there is nowhere for both to share it.

---

*Applied 2026-08-11 by a single pass over the whole plan: §0 and §1 in full, §2.1–2.7 except the two
items recorded above as judged-and-left-alone, §3 untouched pending decisions. If this file and
reality disagree, reality wins — re-grep before acting, and update this file in the same pass.*
