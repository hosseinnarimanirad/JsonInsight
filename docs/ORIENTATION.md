# JsonInsight — orientation for a new session

Written for whoever (or whatever) opens this repository cold. `README.md` at the root is the
*product* document: 1000-odd lines describing what the app does and why each screen behaves the way
it does. **This file is the map of the codebase** — where things live, which invariants are
load-bearing, what will bite you, and which decisions have already been argued out.

Read this first, then `README.md` for whichever feature you are about to touch.

---

## 1. What the app is, in four sentences

JsonInsight reads a JSON configuration document out of several environments at once — live from
HashiCorp Vault, or from files on disk — and compares them key by key. It edits values, promotes a
section that exists in one environment into another, and writes the result back where it came from.
Work is organised into **projects**: one per document you compare (the appsettings root in one,
`resources/config/ui.json` in another), each with its own sources.

The rule the whole design hangs off: **there is exactly one answer for what a source holds, and it is
whatever that source holds right now.** Nothing is cached on disk. There used to be a folder of
snapshots and it was deliberately removed — see README *"A source has exactly one answer"*. Do not
reintroduce a local copy of anything.

---

## 2. Repository layout

| Project | TFM | What it is |
|---|---|---|
| `JsonInsight.Core` | `net8.0` | The engine. No UI framework, no view models. Diffing, flattening, editing, promotion, Vault I/O, source providers, path resolution. |
| `JsonInsight.Presentation` | `net8.0` | Every view model, shared by both front ends. CommunityToolkit.Mvvm + DiffPlex. Touches no UI framework — the three places it needed to are behind `Platform`. |
| `JsonInsight` | `net8.0-windows`, `UseWPF` | The WPF desktop app. Views, XAML themes, code-behind. |
| `WebJsonInsight` | `net8.0`, `WinExe` | The Photino + Blazor desktop app (same window, webview instead of WPF). Runs on Windows, Linux, macOS. |
| `JsonInsight.Tests` | `net8.0-windows`, `UseWPF` | Core + view-model + WPF-view tests. **356 tests.** |
| `WebJsonInsight.Tests` | `net8.0` | bUnit component tests for the Blazor front end. **114 tests.** |

Two solution files, deliberately: `JsonInsight.slnx` (modern) and `JsonInsight.sln` (what VS 2022
opens). `AppPaths` treats either as the repository marker, so deleting the one you don't use would
silently move the content root.

### There are TWO front ends and they are peers

This is the single most important structural fact. **A change to behaviour normally has to land in
both.** They share every view model, so most of the work is in `JsonInsight.Presentation` and only
the markup differs:

- WPF: `JsonInsight/Views/*.xaml` + `.xaml.cs`, themes in `JsonInsight/Themes/`.
- Blazor: `WebJsonInsight/Components/**/*.razor`, CSS in `WebJsonInsight/wwwroot/css/`.

Ask the user if you are unsure whether a request covers both — in this project's history the answer
has always been "both", but the WPF side is often the more expensive half (see §7).

---

## 3. The layer map — where to look for what

```
JsonInsight.Core
├── Model/          TierDocument, FlatConfig, Leaf, ConfigDocument
├── Loading/        TierLoader, TierDefinition, Flattener, ArrayStrategy, DocumentTiers
├── Diff/           MultiDiff, TierDiffer, DiffTree, ConfigPath, PathGlob, AliasSet
├── Editing/        DocumentEditor, EditApplier, EditSet, PendingEdit, TextFinder
├── Promote/        PromotionPlanner, JsonNavigator, OrdinalJsonWriter
├── Sources/        SourceCatalog, SourceEnvironment, ISourceProvider, LocalFileSourceProvider
├── Vault/          VaultClient, VaultPusher, VaultSettings, VaultBrowser, TierRefresher
├── Classify/       Classifier (secret / infra / ordinary), SecretMasker
└── config/         tiers.json, aliases.json, arrays.json, classify.json   ← authored, hand-editable

JsonInsight.Presentation/ViewModels
├── MainVm          the shell: projects, documents, loading, problems, status
├── TiersVm         All tiers grid
├── JsonEditorVm    Tier editor (tree + text pane + find/replace)
├── RawDiffVm       Text diff
├── JsonCompareVm   Compare files
├── VaultVm         Sources tab
├── ProjectsVm      the projects screen
├── PushVm/PromoteVm/EditVm/ChangesVm/RestartVm/LogVm   dialogs and the log
```

**Tabs, in order:** Tier editor · All tiers · Text diff · Compare files · Sources · Logs. The
projects screen *replaces* the tabs rather than sitting beside them (`MainVm.ShowingProjectList`).

`OrdinalJsonWriter` is the canonical serializer. Everything that compares, diffs, or pushes goes
through it, so formatting differences are never mistaken for content differences.

---

## 4. Domain vocabulary (get these right or nothing reads correctly)

- **Environment** — one of five, closed enum, `SourceEnvironment`: `dev`, `test-qa`, `stage`,
  `beta`, `prod`. `Id()` is the persisted key; `Label()` is what's shown (`test/qa`, not `test-qa`).
- **Source** — where one environment's JSON comes from: a whole Vault secret path, or one file on
  disk. Configured on the **Sources** tab; persisted in `appsettings.json` (paths, addresses) and
  .NET user secrets (tokens, never in appsettings — enforced by `[JsonIgnore]`).
- **Tier / TierDocument** — a source that has actually been read. Carries `VaultVersion`,
  `VaultAddress`, `Flat` (the flattened leaves) and `Root` (the mutable tree).
- **Project** — a named set of sources. Switching projects changes which secrets get read, which is
  why the app opens on a list of them and reads nothing until one is chosen.
- **Loaded vs compared** — *(changed 2026-08-10, see §8)* every configured environment is **read**;
  only the ticked ones (`MaxActive = 4`) are **compared** in the All tiers grid. `MainVm.Documents`
  is the loaded set; `MainVm.Compared` is the ids the grid narrows to (empty = all).

---

## 5. Configuration and settings — three separate things

Easy to conflate; they are not the same.

1. **`JsonInsight.Core/config/*.json`** — the *rules*. `tiers.json` (fallback tier list),
   `aliases.json`, `arrays.json` (array keying strategies), `classify.json` (what counts as a
   secret). Hand-edited, shared by both front ends, resolved by `AppPaths.ConfigDirectory` which
   prefers the **authored** folder over the `bin\` copy so edits take effect without a rebuild.
2. **`JsonInsight/appsettings.json`** — the workspace: projects, their sources, addresses, active
   sets. Written by the Sources tab. Resolved by `AppPaths.AppSettingsFile`; both front ends land on
   the same file on a dev machine, deliberately.
3. **.NET user secrets** — tokens only, keyed `Vault:Projects:{project}:Connections:{env}:Token`.

Override any of them with `JSONINSIGHT_ROOT`, `JSONINSIGHT_CONFIG`, `JSONINSIGHT_SETTINGS`.

There is also an **ambient token** (`VAULT_TOKEN`, or `~/.vault-token` from `vault login`), used only
for a row that names no token of its own. It is never written back — `VaultSettingsStore.AmbientToken`
re-reads on every call, and `VaultSettingsStore.AmbientTokenLookup` is replaceable so tests aren't at
the mercy of the developer's machine.

---

## 6. Load-bearing invariants — do not "simplify" these away

**The write path.** `VaultPusher` and `LocalFileSourceProvider` are the only code that changes
anything anywhere, and each fence exists because of a specific failure:

1. A read-only document is refused (only a file browsed on **Compare files** arrives read-only).
2. The payload is re-parsed and re-flattened before it leaves, and refused unless it holds exactly
   the keys the tree holds — catches a serializer that dropped or invented a key.
3. What the source holds is read immediately beforehand, so the diff being confirmed is current.
4. **A push whose base version is no longer live is refused outright** (`PushPlan.Stale`). The
   check-and-set below cannot catch this: it carries the *current* version, so the write lands
   cleanly on the other person's upload and reports success.
5. The write carries that version as a KV check-and-set.
6. The result is read back and byte-compared — "the POST returned 200" and "Vault holds what I sent"
   are different claims.
7. Nothing after step 5 may turn a landed write into a reported failure.

**Secrets are never rendered** anywhere except the Tier editor's text pane, which has to show them
because a subtree cannot be retyped without being read. Everything else shows
`•••••• len 64 a3f1c9`.

**`RepositoryHygieneTests`** fails the build if any file names the deployment this tool grew up
against, or carries credential-shaped text. If it fires, **replace the value — do not add an
exclusion.**

---

## 7. Working practicalities (this is the section that saves you an hour)

### The running Photino app locks its own output
If `WebJsonInsight.exe` is running, `dotnet build` on the solution fails with MSB3021/MSB3027 on
`JsonInsight.Presentation.dll`. Either ask the user to close it, or build and test to a scratch path:

```bash
dotnet build WebJsonInsight.Tests/WebJsonInsight.Tests.csproj -p:BaseOutputPath="<scratch>/"
dotnet test  WebJsonInsight.Tests/WebJsonInsight.Tests.csproj -p:BaseOutputPath="<scratch>/"
```

The WPF suite is unaffected: `dotnet test JsonInsight.Tests/JsonInsight.Tests.csproj`.

### `grep` in Bash is proxied and mangles output
An `rtk` hook rewrites shell `grep`, which strips whitespace, truncates lines and sometimes prints
its own help text instead of results. **Use the `Grep` tool, not `grep` via Bash.** `sed -n` and
`ls` are fine.

### Test suites and what each is for
- `JsonInsight.Tests` — core logic, view models, and `UiSmokeTests`, which renders every WPF view on
  an STA thread against real view models and **turns WPF's binding trace into assertions**. A typo in
  a `{Binding}` path or a missing `StaticResource` fails here rather than at runtime. Very cheap
  insurance; run it after any XAML edit.
- `WebJsonInsight.Tests` — bUnit renders the Blazor components for real, so every row of every grid
  is actually built. `JSRuntimeMode.Loose` because the components call `jsonInsight.*` for things only
  a browser can do.

Neither suite may touch the network. `new MainVm(vaultAtStartup: false)` and `PushVm(checksOnOpen:
false)` are the switches; `MainVm.Seed(...)` is the only way to get documents in without a read.

---

## 8. Decisions already made (don't re-litigate; extend deliberately)

Recent, and each was a deliberate reversal of something that looked reasonable:

- **Create project is disabled without a name.** A project *is* its name — the key its sources are
  filed under and its tokens are keyed by.
- **Sources row order** is ON · environment · kind · address · token · Search · JSON path · Test ·
  Load, with Search/Browse *leading* the path cell. Test moved out of the ⋮ menu; a local-file row
  has no ⋮ at all (its only non-Vault item was Test), with the width reserved so buttons stay in one
  column.
- **Test is enabled only when the row is complete**; **Load only after Test passes**, and any edit to
  the row (kind, address, namespace, token, path, file) clears that again. An ambient token counts as
  the token.
- **Update node is disabled while the pane does not parse**, and the reason — the JSON reader's own
  message — shows under the pane in `JsonEditorVm.EditorProblem`. It used to stay lit so pressing it
  produced the error, which made the button the only way to find out.
- **A push built on a superseded version is refused**, not warned about. See §6.4.
- **Every configured environment is read; ON only picks the four compared.** Loading and comparing
  used to be one list, so ON meant both "compare this" and "read this at all".
- **A ticked environment with no source disables Pull** — and the startup read refuses on the same
  condition, so the app never quietly produces unasked the comparison it will not produce when asked.
- **Find highlights every match**, current one stronger. Neither pane can colour its own content:
  Blazor draws a layer behind a transparent `<textarea>` at identical metrics (`.pane-highlights`,
  scroll-synced in `interop.js`); WPF uses `FindHighlightAdorner` over the `TextBox`. Both read one
  `Matches`/`MatchIndex` list on `JsonEditorVm`, so stepping walks an **index**, never a fresh search
  from the caret (which used to re-find the same match), and revealing never steals focus (which used
  to turn the second Enter into a newline).
- **Tier pickers read `dev v39` / `prod v11` / `dev (file)`** — `TierDocument.PickerLabel`.

---

## 9. House style

The prose in this codebase is unusual and worth matching. Comments explain **why**, in full
sentences, and very often name the alternative that was rejected and the failure that rejected it.
They are long where the reasoning is long. Match that density — do not strip it, and do not add
comments that merely restate the code.

Concrete conventions:

- **Nothing outside the theme files names a colour.** WPF: `DynamicResource Brush.*` from
  `Themes/Light.xaml` / `Dark.xaml`. Blazor: `var(--brush-*)` from `wwwroot/css/theme.css`. Both
  themes must define the *same token set*, and a new token goes in all four dictionaries.
- View models are shared; if you find yourself writing the same logic in a `.razor` and a `.xaml.cs`,
  it belongs in `JsonInsight.Presentation`.
- WPF tooltips that explain *why a control is disabled* need
  `ToolTipService.ShowOnDisabled="True"` — the default hides them exactly when they're wanted.
- A WPF `Button`/`TextBlock` cannot have both a `Style="{StaticResource X}"` attribute and a
  `<Button.Style>` element; use `BasedOn` inside the element. A local `Text=` attribute also beats a
  style `Setter`, so text that a trigger must replace has to come from the style.
- Tests are named as sentences and carry a `<summary>` explaining what would break without them.

---

## 10. Where to start, by task

| If you're asked to… | Start at |
|---|---|
| change what loads / what is compared | `SourceCatalog`, `MainVm.Load` / `RefreshFromVaultAsync` |
| change the Sources tab | `VaultVm`, `SourcesTab.razor`, `Views/VaultView.xaml` |
| change the grid | `TiersVm`, `MultiDiff`, `AllTiersTab.razor`, `Views/TiersView.xaml(.cs)` |
| change the editor / find | `JsonEditorVm`, `TextFinder`, `TierEditorTab.razor`, `Views/JsonEditorView.xaml(.cs)`, `FindHighlightAdorner` |
| change writing | `VaultPusher`, `LocalFileSourceProvider`, `PushVm`, both `PushDialog`s |
| change how JSON is flattened/diffed | `Flattener`, `MultiDiff`, `config/arrays.json`, `config/aliases.json` |
| change what counts as a secret | `config/classify.json`, `Classifier` |
| add a colour | all four theme dictionaries, then use the token |

---

*Last updated 2026-08-10. If you make a structural change or reverse a decision in §8, update this
file in the same pass — it is only worth having if it is true.*
