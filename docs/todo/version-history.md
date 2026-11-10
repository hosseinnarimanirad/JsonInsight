# Version history — node and whole-document, read-only from Vault

Planned 2026-11-10. Read `docs/ORIENTATION.md` first. Lands in **both** front ends (WPF and Blazor).

## Resume here

| Phase | What | State |
|---|---|---|
| 1 | Core: `VaultClient.ReadMetadataAsync` + `ReadVersionAsync`, `History/SecretHistory` (reads), `History/NodeHistory` (which versions changed a node), tests | **done 2026-11-10**, committed `c80209e` — 17 new tests in `JsonInsight.Tests/HistoryTests.cs`; suite 431/432 (the 1 failure is the existing `RepositoryHygieneTests` deployment-name check on `README.md` + `Samples/`), Web 119/119. See *Phase 1 notes* |
| 2 | Whole-document compare: version choices in the Text diff tab's side pickers; **Copy version JSON** | **done 2026-11-10**, uncommitted — `ViewModels/DiffSideVm.cs` (`IHistoryReader`, `SettingsHistoryReader`, `VersionChoice`, `DiffSideVm`), `RawDiffVm` reworked onto two sides + `CopyCommand`, `MainVm` passes the reader, `RawDiffView.xaml` + `TextDiffTab.razor`. Tests: 9 in `JsonInsight.Tests/TextDiffHistoryTests.cs`, 1 bUnit in `WebJsonInsight.Tests/TextDiffHistoryTests.cs`. Not yet tried against a real Vault |
| 3 | Node history panel in the Tier editor (WPF `JsonEditorView` + Blazor `TierEditorTab.razor`), **Copy value**, **Restore this node** | **done 2026-11-10**, uncommitted — `ViewModels/NodeHistoryVm.cs`; `JsonEditorVm` gains `History`, `ShowHistory`/`CloseHistory`, `HistoryReader`, restore-as-edit; WPF panel docked right + tree menu *History…* + pane-head *History*; Blazor `Tabs/HistoryPanel.razor` + `.with-history` grid column + row/pane buttons. Core: JSON null now counts as present (`DocumentEditor.Holds` made internal). Tests: 12 in `NodeHistoryPanelTests.cs`, 1 null test, 2 WPF smoke (both themes), 1 bUnit. Not yet tried against a real Vault |
| 4 | Entry from an All tiers cell ("History of this key in prod") | not started |
| 5 | Docs: rewrite README *Not in this version* → "a read-only version browser"; add a *History* section; ORIENTATION §3 map | not started |

Before phase 3, check per tier: `vault kv metadata get kv/app/<env>` — how many versions Vault keeps
(`max_versions`, default 10), and whether the token may **read** `kv/metadata/...` (Search only needs
**list**).

## Decisions (user, 2026-11-10)

- **Nothing stored on disk, and no copy kept.** No session cache. History is a read-only query that
  runs only when the user asks, for one node or for the whole JSON. Old versions live only while the
  panel / diff that asked for them is open, and are dropped when it closes; opening it again reads
  Vault again.
- **Both node and whole-document history show a diff.** Default: selected old version (left) →
  current (right). Toggle: *Compare with: Current | Previous version*.
- **Getting an old value back:**
  - **Copy value** (node) / **Copy version JSON** (whole document) to the clipboard — the same
    clipboard path the Tier editor's own copy uses.
  - **Restore this node** — replaces the node in the current document as an ordinary **pending
    edit**; it then goes out through *Push to Vault or disk* with the CAS check and typed
    confirmation. The result is a new version holding the old value, never a rollback.
  - No "restore whole document" (it would also undo changes nobody meant to undo).
- **Vault sources only.** A local-file source says *"Files have no version history"*; the `.bak-*`
  files the writer leaves are not treated as history.

## Vault calls (all reads)

| Call | Returns |
|---|---|
| `GET v1/{mount}/metadata/{path}` | `current_version`, `oldest_version`, `max_versions`, and per version `created_time`, `deletion_time`, `destroyed` |
| `GET v1/{mount}/data/{path}?version=N` | that version's `data.data` (404 when deleted or destroyed) |

Vault records **when** a version was written, not **who** — "who" is only in the audit log.

## UX

- **Node history** opens from the Tier editor tree (context menu **History…** + a toolbar button) as
  a **side panel**, not a modal or a tab — the JetBrains *Show History for Selection* / VS Code
  Timeline pattern. Lists only the versions where the node changed (*Show all versions* toggle), each
  row: `vN`, date + relative age, change kind, short preview (secrets masked by fingerprint).
  ↑/↓ moves through versions and updates the diff; Esc closes. *Scope: Node | Whole document*.
- **States:** loading ("Reading 10 versions…", cancellable); one version → "No earlier versions";
  deleted/destroyed rows greyed with the reason; a gap note when Vault no longer keeps older versions;
  403 on metadata names the missing capability; unpushed edits → "Current" means what Vault holds,
  with a separate *Your unsaved edits* choice.

## Phase 1 notes

- `History/SecretVersion` is deliberately **not** a `TierDocument`: an old version is not a source,
  so nothing built for pushing can be handed one by mistake.
- Change detection compares flattened leaves (`FlatConfig.Subtree`, `ComparableValue`), so an array
  declared a set in `arrays.json` does not show reordering as a change — the same rule as the grid.
- The oldest version that holds a node is reported as `FirstKept`, not `Added`: with `max_versions`
  in force, the node may well be older than any version Vault still keeps.
- A JSON `null` value is present, not absent (fixed in phase 3 via `DocumentEditor.Holds`), so
  restoring it puts the null back rather than removing the key.

## Phase 3 notes

- The panel reads the list plus every readable version (newest 50 at most) when it opens, holds them
  while open, and drops them on close / tier change. Moving the tree selection or flipping *Whole
  document* recomputes from those — no new Vault reads.
- *Restore vN* = `DocumentEditor.Replace` (or `Remove` when vN did not have the node); then the normal
  push. Off for the whole document and when vN already matches the editor.
- Layout (reworked 2026-11-10 after the first look): the panel is a narrow (330px) version list —
  scope segment, two switches, the list filling the height, Copy value / Restore at the foot. The
  **diff takes the editor pane** (like *Compare with original*), with its scope and counts in the pane
  header; opening the comparison closes the history. Whole-document diffs show changed lines only.
- "Current" in the diff is what the editor holds (unpushed edits included); the header says
  `ON SCREEN` when that may differ from what Vault holds.
