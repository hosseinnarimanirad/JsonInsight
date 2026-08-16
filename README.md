# JsonInsight

A WPF desktop app for managing the application's .NET configuration tiers: reading them live from Vault or from
JSON files on disk, comparing tiers against each other, editing values, promoting a missing section
from one tier into another, and writing the result back where it came from.

Work is organised into **projects** — one per thing you compare, each with its own document and its
own sources. It opens on the list of them. See *Projects* below.

Within a project it works on one JSON at a time, compared across up to four environments chosen from
`dev`, `test/qa`, `stage`, `beta` and `prod`. The Sources tab is where each of those is
pointed at the whole path to one Vault secret, or at one file on disk. See *Where a source lives* and
*Finding the JSON* below.

## A source has exactly one answer

That is the whole of it, and everything else follows from it.

| | Vault source | Local-file source |
|---|---|---|
| Where a tier comes from | `kv/app/{env}`, read live | one JSON file, read live |
| Where an edit goes | back into that secret, as a new version | back into that file, backed up first |
| What is cached on disk | **nothing** | **nothing** |

There used to be a folder of snapshot files behind this — one per tier per pull, plus a
`…pending.json` for every local edit waiting to be uploaded. It is gone, deliberately. Every one of
those files was a claim about what an environment held that was only true on the day it was written,
and the app had to spend its explanations saying which of the two answers you were looking at: the
column header said whether a value was live or from a file, an edit landed in a file whose name
meant "not uploaded yet", and a diff could have some other day's answer on either side of it.

**A local-file source is not that coming back.** A snapshot was a *copy* of an answer that lived
somewhere else, which is what made it able to disagree with the thing it copied. A local-file source
is the answer — the file is the environment, read live on every load and written in place, with no
second location for it to drift from. What was removed was the second copy, not the disk.

So there is still one answer per source, it is whatever that source holds right now, and a change to
it is a new version of the secret or a rewritten file, or it did not happen.

**Vault is read when the app opens**, and the window opens empty until it answers. Turn that off
with `"LoadTiersAtStartup": false` in the `Vault` section of `appsettings.json` and nothing loads
until you press **Pull from Vault** in the title bar.

**A tier Vault cannot serve keeps its column and says `UNAVAILABLE`.** Its cells read `?` rather
than `—`: "I could not ask" is the absence of a finding, and rendering it like a gap would fill the
grid with hundreds of differences nobody has established. It takes no part in any comparison — no
row calls it missing, no rollup offers to promote into it — and the reason goes in the problems
banner. There is nothing to fall back to and, by design, nothing to fall back on.

**The read says it is happening, and says how it went.** The Pull button disables itself and its
icon turns for the duration — the startup read included, which is the one that most needs it: a
button that still looks pressable during a network call is a button that gets pressed again. A
banner under the header then reports the outcome whether or not anything went wrong. Reporting only
failures left "it worked" indistinguishable from "nothing has happened yet", and made the only Vault
message anyone ever saw a red one, at startup, about a read they had not asked for.

The problems banner is capped and scrolls. One document whose arrays are not declared in
`arrays.json` can produce forty warnings, and a banner that grows without limit takes the whole
window for a list whose first two lines were the point; the count above it says how much is below
the fold.

## Why it exists

The .NET config lives in four environments that drift silently, and every one of them is a Vault
secret this app reads and writes:

| Tier | Secret | Read | Pushed |
|---|---|---|---|
| dev | `kv/app/dev` | yes | yes |
| stage | `kv/app/stage` | yes | yes |
| beta | `kv/app/beta` | yes | yes |
| prod | `kv/app/prod` | yes | yes |

⚠️ The drift figures below were counted in early August 2026 against a set of snapshots, and have not
been recounted since. `JsonInsight.exe --check` reprints them against what Vault holds now.

Because the Vault loader *replaces* the appsettings layer wholesale rather than merging it, a key
missing from a tier is simply absent at runtime. There is no fallback. As of 2026-08-03 the four
tiers span **444 distinct paths, 47 of which are missing from at least one tier**, rolling up into
21 promotable subtrees. The largest:

- `AccountSettings:NightlyApprovalJob` — 11 keys in dev, absent from every vault tier
- `AdminSettings` — 8 keys in all three vault tiers, absent from dev
- `AuthSettings:GatewayA` (5 keys) and the Serilog `Seq` sink (3) — in the vaults, missing from dev
- `PaymentSettings:Hub:Payment` — 3 keys in dev, absent from every vault tier
- `PaymentSettings:BillWalletLock` — in dev and stage, missing from beta and prod

Those numbers are a reading, not a constant. `JsonInsight.exe --check` reprints the whole list
against whatever the files hold today.

## Running it

Windows plus the .NET 8 SDK — it is WPF, so `net8.0-windows` is not portable.

**There are two solution files, holding the same two projects.** `JsonInsight.sln` is the classic
format, which Visual Studio 2022 opens; `JsonInsight.slnx` is the modern one, for Visual Studio 2026.
Adding or removing a project means editing both, and the cost of that is why they are worth
mentioning here rather than leaving to be discovered. It also means `dotnet` commands that take a
solution have to be told which one — with both present, a bare `dotnet build` cannot choose.

```
cd D:\Projects\JsonInsight
dotnet run --project JsonInsight
dotnet build JsonInsight.sln
```

A headless mode, useful for a quick check or a pre-commit hook:

```
JsonInsight.exe --check [-v]     # prints every comparison the app performs; exit 1 if a tier was unavailable
```

It reads Vault, because that is where a tier is. There is nothing on disk to check against, and a
headless mode that quietly checked something else would be the one thing worse than not having one.

The **content root** — the folder holding this repository, found by walking up for `JsonInsight.slnx`
*or* `JsonInsight.sln` and taking its parent — is still how the app finds its own `config/` folder.
Either solution file counts, so deleting whichever one you do not use cannot quietly move it.
Override with the `JSONINSIGHT_ROOT` environment variable.

`Ctrl+D` switches between the light and dark themes. The theme it opens in follows
the Windows app theme and is not remembered per app, which is deliberate: the only file this app
could persist it to is the one the Sources tab writes, and a colour preference has no business in a
file that holds connection settings.

## Projects

**The app opens on a list of projects, not on a diff.** A project is one piece of work: a document,
and the sources to compare it across. The appsettings root is one, `resources/config/config.json` is
another, `resources/config/ui.json` is a third — different documents, often worth comparing across
different environments, and each with its own idea of where `dev` comes from.

Each row says what it compares, across how many sources, and when it was last opened, most recent
first. **Open** loads it. **Projects** in the title bar goes back to the list without closing
anything — the tiers, the queued edits and the tab you were on are all still there when you press
**Back to …**. Opening a *different* project is the destructive one, and it says so by simply doing
what it must: different secrets, so nothing from the old project survives the move.

**New project** takes a name and, optionally, another project to copy the sources from. That option
is there because the usual second project is the same environments looking at a different document —
the same four Vault roots, one path lower. The copy is its own from that moment: editing `stage` in
one does not touch the other.

**Create is off until the box says something.** A project *is* its name here — it is the key its
sources are filed under and the key its tokens are keyed by, which is why renaming one has to carry
them across — so an unnamed project is not a thing that can exist. The button says that by being
unpressable rather than by being pressed and answering afterwards with a line of status text.
Whitespace does not count.

**What a project owns, and what it does not:**

| Per project | Shared by every project |
|---|---|
| Each environment's source — the whole path to one Vault secret, or one file | `LoadTiersAtStartup` |
| That source's own address, token, TLS setting (and namespace, if ever needed) | `AlwaysOpenLastProject` |
| Which sources are active, and in what order | The `config/` folder's rules |
| That source's restart endpoint, if it has one | |

**A row is self-contained.** There used to be one shared address, namespace and token that a blank
field on a row fell back to, so the answer to "what does stage read, and as whom" was the row plus a
card above it plus a rule about which won. That is gone: every row says all of it. Two environments
on different Vault servers is now an ordinary thing to configure rather than an override, and the
copy-sources option on **New project** is what keeps it from being four times the typing.

**Tokens are filed under the project**, at `Vault:Projects:{project}:Connections:{env}:Token` in user
secrets. Renaming a project carries them across; deleting one takes them with it.

**An upgrade keeps working.** The shared address, namespace and token are pushed down into every row
that relied on them before they are dropped, and the old app-wide document is appended once to each
row's root so a row that read `{root}` + `resources/config/ui.json` now reads that path outright.
Removing the shared values without either step would have taken three rows in four offline.

**Nothing is read until a project is opened.** The startup Vault read used to happen unasked; with
more than one project it would also have been against whichever secrets happened to be last. Tick
**Always open the last project** to skip the list — the right setting if you only have one.

**An install that predates projects opens on its own work.** The old single configuration is folded
into a project called `appsettings`, in memory on load and on disk the first time anything saves, so
upgrading changes nothing except that the thing you were comparing now has a name.

## The six tabs

Each is named for what you go there to do rather than for the format involved: three of them read
JSON, so *JSON compare* beside *Raw diff* said nothing about which one answers the question you
actually have.

**Tier editor** — one tier's whole document, as a searchable hierarchy on the left and replaceable
JSON text on the right. The escape hatch for changes the key-by-key editor cannot express:
restructuring a section, pasting in a block someone sent you, retyping a subtree wholesale. See
*Replacing JSON wholesale* below. **Push to Vault** is the only way an edit leaves this window;
there is no Save, because there is nowhere to save to. The tier picker names each tier's version in
the list — `dev v39`, `prod v11`, or `dev (file)` — because that is the version a push is built on,
and the one a push is refused over if somebody replaces it first. It offers every source that was
read, not only the four being compared.

**All tiers** — one row per configuration path, one column per tier, each column headed by where its
values came from and, for a Vault source, which version: `Vault v34`, not merely `Vault`. Which KV
version a column holds is the difference between "stage does not have this key" and "stage did not
have it an hour ago". A local-file column says `file`; it has no version to name. A subtree missing wholesale from the same tiers collapses to a single row
(`AccountSettings:NightlyApprovalJob — 11 keys, only in dev`), and that rolled-up node is exactly what
the Promote button acts on. The grid opens showing every key, with disagreements carrying their own
colour: a value present everywhere that should match and does not is washed orange, a
deployment-specific one (a URL, a host — expected to differ) gets a muted shade of the same hue, and
a missing key keeps its red. **Only changed values** narrows the grid to the rows where the sources
disagree; **Show expected** controls whether the deployment-specific ones count. A key with an
unwritten change against it stays on the grid whatever the filters say, until the change is written:
setting a key to the same value in every tier is the ordinary edit here, and it is also precisely
what makes the row identical — hiding it would mean the grid answered a successful edit by removing
the row that showed its result.

This is also where configuration is changed: a row's own **Edit** button changes or removes that key
across the tiers. Editing starts from the row rather than from a toolbar button, because a button
acting on "whatever is selected" is the one that eventually gets pressed against the wrong row.
Nothing on this tab writes directly — see *Editing* below. The other direction is the title bar's
**Push to Vault or disk**, which is the only place a push starts. See *Pushing to Vault*.

**Text diff** — any two *configured tiers*, both serialized through the same writer, diffed line by
line, laid out **as left | as right**. Long lines wrap rather than scrolling sideways: a PEM block or
a 900-character token used to carry the right-hand column off the screen with it, so a two-column
diff read as a one-column one until you scrolled to find the other side.

**Compare files** — any two JSON files on disk, browsed for. The one tab that reads a file at all,
and it never writes one. Compared by key path, not by line, so a reordered key is not a change; one
checkbox switches between every key and only the ones that differ. Use it for the JSON that is not a
tier: a payload someone sent you, an export taken before a change, the gateway's KV blob. Browsed
files go through the same flattener, array strategies and secret classification as a tier, so the
comparison means the same thing, and are marked read-only on the way in.

**Sources** — one row per environment, saying where that environment's JSON comes from and whether
it is one of the columns the other tabs compare. One list, and nothing above it.

A row is either a **Vault** secret — the whole path to one JSON, with its own address and token — or
a **local file**, one JSON on disk. Everything about a row is on that one line, with the occasional
settings behind the **⋮** at its end.

The line reads left to right in the order a row gets filled in: **ON**, **environment**, **kind**,
**address**, **token**, then **Search** and the **JSON path** it fills, then **Test** and **Load**.
Search walks that row's own Vault and fills its picker with the JSONs actually there; see *Finding
the JSON* below. A local-file row swaps Search for **Browse** in the same place.

`Namespace` is the one setting not shown. It sets the `X-Vault-Namespace` header, which only means
anything on Vault Enterprise, and it is empty on every row in these deployments — so it stays in
`appsettings.json`, still read and still sent when present, rather than taking a column on the row
from the JSON path, which is the longest thing here and the one most worth reading in full.

Tick **ON** to make a row one of the compared columns; four at a time, and a fifth is refused here
with a reason rather than accepted and quietly dropped later. Until something is ticked and saved,
`config/tiers.json` decides that instead and nothing on this tab changes what is on screen — so an
existing install upgrades to this tab without moving.

**ON decides what is compared, not what is read.** A pull reads *every* environment with a source
configured, ticked or not, so a fifth one is available on the **Tier editor** and **Text diff**
without a trip back here to untick something first. Only the **All tiers** grid is capped, because
only the grid is four columns wide — it says so above itself when more was read than it is showing.

**A row ticked ON with nothing behind it turns Pull off.** It used to be skipped with a note, which
meant the honest outcome — a comparison missing the column somebody had just asked for — arrived
looking exactly like a successful one, three columns wide. There is no reading around it and no
default to substitute, so the button that would produce that comparison is the thing that goes off,
and its tooltip names the environments in the way. The read the app performs when it opens follows
the same rule: one of them going ahead while the other refused would mean the app quietly produced,
unasked, exactly the comparison it will not produce when asked.

**Test** reads what the row points at and reports what is there — a Vault secret's version and key
count, a file's key count and details — keeping nothing and changing nothing on the other tabs. It is
off until the row says where to read from: a server, a token and a path for a Vault source, a file
for a local one. A token from `vault login` counts as the token, since it is the one the read would
use.

**Load** reads that one source and puts it on screen: after it, the Tier editor, All tiers and Text
diff are all showing that source without anyone having pressed Pull. Pull reads all four, which is
the wrong size of act when one row's path was just corrected and the other three are fine — and on
four Vault servers it is three network round trips nobody asked for. Loading a source that is already
on screen replaces it in place, so it keeps its column.

**Load is off until Test has passed**, and off again after any edit to the row. Load is the button
that says *this is what that environment holds* — it puts what it reads onto three other tabs — so it
waits for something that says the row points where you think it does. A test that passed against a
path since retyped, or a server since repointed, is not evidence about the source now described, so
editing the kind, address, namespace, token, path or file takes Load back off. Ticking **ON**,
opening the menu and the row's own status line do not: none of them changes what a read would return.

**⋮** now holds only what is both occasional and a Vault concern: **Insecure TLS**,
**Restart config…** and **Call restart…**. Test used to live here too, while it was optional; a
button you have to press before the next one works is not an occasional option. A local-file row has
no certificate to trust and nothing running behind it to restart, so it has no ⋮ at all rather than
an empty one.

**Insecure TLS** says its state twice — a tick and the word **ON**/**OFF** — and a row with it on
also carries a **TLS off** pill beside its address. It reads as a double until you have had it on
without noticing: certificate checking being off is a property of the connection, and a setting you
can only see by opening a menu is one you forget you turned on.

There is no per-row write switch. Every configured source is writable: the tick used to sit next to
the row rather than next to the write, so the only thing it reliably did was refuse a push somebody
had already decided to make. What is still refused is a file opened on the **Compare files** tab —
that was never a source, and the push and save paths both check rather than assume.

Paths, addresses and the active set persist to `JsonInsight/appsettings.json` under the open project;
tokens go only to .NET user secrets (`%APPDATA%\Microsoft\UserSecrets\jsoninsight-9f3c1d20\secrets.json`,
the same file `dotnet user-secrets` edits), enforced structurally — the token properties are
`[JsonIgnore]`, so the serializer that produces appsettings.json cannot emit them.

**Logs** — everything the app has said, newest first: every warning, every read failure and every
status line, each with the time it happened. **Clear** empties it.

This replaced a banner above the tabs, and the reason is worth stating because the banner looked like
the more urgent design. A banner is the right shape for one urgent sentence. These are not one
sentence: a single undeclared array produces a line per source, so the thing meant to draw the eye
instead took the top of the window and pushed the grid — the reason the app is open — below the fold.
Its only control was *Dismiss*, so the choice on offer was between losing the window and losing the
findings. A tab keeps both, and the count on the tab is what stops a failed read being silent.

Nothing is lost by clearing it: what is in there is a record of what already happened, not the app's
state. A reload or a pull says all of it again.

## Where a source lives

A source is a **Vault secret** or a **local file**, and the rest of the app cannot tell which. Every
source loads *and saves* through an `ISourceProvider` keyed by its kind, resolved from one registry
(`SourceProviders`), and what comes out is a `TierDocument` — so the diff, the editor, the promote
planner and the grid have never had to know where a column came from, and still do not.

A local file earns its place on the cases Vault cannot answer: a `Vaults\*.json` snapshot of a tier
that is not deployed yet, an export taken before a change, a payload someone sent you, an
environment that is a file on a share and not in Vault at all. Before this, those went through the
Compare files tab, which reads two files, read-only, outside the tier model entirely — fine for
"how do these two differ", useless for "promote this section from stage into it".

**A local file is writable, with the same fence a Vault push gets.** Not a weaker one — the same five
steps, one substituted at a time:

| | Vault | Local file |
|---|---|---|
| Refuse a document opened for comparison | the same flag | the same flag |
| Re-parse the payload before it leaves | `PayloadValidator` | the same code, not a copy |
| Compare against what is there *now* | a live re-read | a fresh read of the file's bytes |
| Concurrency guard | KV check-and-set version | backup, then temp-file-then-rename |
| Verify after writing | read back and compare | read back and byte-compare |

The backup is a sibling named for the file's previous modification time, taken immediately before the
overwrite, and a second save in the same second gets its own suffix rather than clobbering the first.
The write itself goes to a temp file in the same directory and is renamed over the target, so a
process killed mid-write leaves either the old file or the new one — never half of one.

**Both kinds name one whole thing.** A Vault row is the full path to one secret; a local row is one
file. Neither has anything appended to it, which is what removed the old asymmetry — a Vault source
used to be a root that a shared document was bolted onto, and a file, having no root, had to be
explained as an exception.

### `tiers.json` and the active set

`config/tiers.json` still describes the tier list, and on an install where nothing has been ticked on
the Sources tab it is still the only thing that does. Ticking sources and saving replaces it: the
active set becomes the columns, built from the source rows rather than from that file, and
`tiers.json` stops being consulted for anything but the fallback.

Both routes produce the same `TiersConfig`, so nothing downstream changes. The difference is only
whether adding an environment is a text edit to a file in the config folder or four clicks on a tab —
and the tab is the one that can add a `test/qa` that Vault has no secret for.

## Finding the JSON

Vault holds more than one JSON per environment. The appsettings root is the environment secret
itself, `kv/app/{env}`, and there are others beneath it —
`kv/app/{env}/resources/config/ui.json`. They are the same shape of problem: four
environments that drift, with no fallback between them.

**A source row names one of them in full.** Not a root with a document appended — the whole path. A
project is a comparison, so `ui.json` across four environments is a project whose four rows each end
in `ui.json`, and `content.json` is a different project. What a row reads is written on the row.

**Search**, on each Vault row, is how the path gets filled in. It asks that row's Vault for the
mounts its token can see — the endpoint the Vault web UI itself uses — walks each one, and fills that
row's picker with every secret found. It reads metadata only; no value is fetched and nothing is
written.

Three things it deliberately does rather than the obvious:

- **Each row searches its own server.** There is no "the" Vault to walk: every row carries its own
  address, and in these deployments beta and prod are not on the same host as stage. A single button
  walking "the" Vault would have had to pick one of them and offer its answer for the rest.
- **A token that cannot list mounts is not an error.** Most application tokens cannot read
  `sys/internal/ui/mounts`, so it falls back to the mounts already named in your paths and says which
  ones it searched.
- **The path field stays typeable.** It is an editable combobox: what a search found is a
  convenience, and a secret the token cannot list must still be reachable by typing it. The path the
  row already reads is kept in the list whatever the search returned — a picker that dropped the
  current answer would make a working row look misconfigured.

It is bounded and loud about it — 400 listings and 8 levels, and it says when it stopped early. A
silently short list is worse than a slow one: a path missing from the dropdown looks like a path that
does not exist.

The found list is **not** persisted. It can run to hundreds of entries, `appsettings.json` is a file
people read, and it is a property of the Vault rather than of the project — so it is re-found on
demand. The one entry that has to survive a restart is the path the row actually reads, and that is
the row.

## Safety

There is exactly one thing this app can change — a Vault secret — and one code path that changes it.
That is the first fence, and the one the rest lean on: the promote flow, the batch change set and the
document editor all end in the same place, so a fence added there is added to all three and a fence
skipped there is skipped by none of them.

- A document that arrived read-only is refused by the pusher, the promote planner and the editor
  alike. Every configured source is writable; the one thing that reaches them read-only is a file
  browsed on the **Compare files** tab, which was never a source and must not become one by being
  saved.
- **The payload is re-parsed before it leaves** and refused unless it holds exactly the keys the
  document holds. A serializer that dropped or invented a key is caught on the way out rather than
  discovered in tomorrow's diff.
- **What Vault holds is read immediately beforehand**, so the comparison being confirmed is against
  the current version rather than against whatever was on screen.
- **A push built on a version that is no longer live is refused outright.** If the secret was read at
  v34 and Vault now holds v36, nothing is sent: pull again, redo the change against v36, and push.
  The check-and-set below cannot catch this one — the version it carries is the *current* one, so the
  write lands cleanly on top of the other person's upload and reports success.
- **The write carries that version as a check-and-set**, so a secret somebody else changed in between
  is refused by Vault itself rather than clobbered.
- **The result is read back and compared.** "The POST returned 200" and "Vault holds what I sent" are
  different claims, and only one of them is the reason to press the button.
- The final step requires typing the destination tier's name.
- A queued edit that the tier has moved out from under blocks its own push until it is reviewed.
- Secrets are never rendered — not in the grid, not in an edit row, not in the pending-changes list,
  not in the Tier editor's hierarchy. All of them show `•••••• len 64 a3f1c9`, and the short hash
  still lets you see whether two tiers hold the same secret. The one deliberate exception is the
  Tier editor's text pane, which renders values in clear because a subtree cannot be retyped without
  being read.

> ⚠️ **There is no undo.** A push is a new version of a live secret, and this app keeps nothing that
> could put the old one back. What can: Vault's own version history — the previous version is still
> there, and rolling back is reading it and pushing it again. Nothing here removes a Vault version.

⚠️ Everything on screen is live production configuration, credentials included. Never paste a value
into a report or a commit. The upside of keeping nothing on disk is that there is no folder of
credentials to leak; the corresponding cost is that closing the window loses anything not pushed.

## Editing

Changes queue up and go in as a batch. That is not a convenience: the change that prompted the
feature was one shape applied to six sibling Couchbase URLs across two environments, and six
sequential single-key pushes would be six versions of a secret describing one change.

1. **Select.** A row's **Edit** button opens that key; **Edit all shown** opens everything the filter
   currently matches. The filter box takes a glob as soon as it contains a `*`, so
   `ConnectionStrings:Couchbase:Modules:*:Url` selects exactly those six.
2. **Set.** One grid, one row per key *per tier* — including tiers that do not have the key, because
   a key present in exactly one tier is the drift this tool exists to find. A **Set every row to**
   box applies one value to all of them at once. Values are typed: string, number, boolean or null,
   defaulted to whatever the key already is elsewhere.
3. **Queue.** Changes join the pending set and the toolbar says how many are waiting. An edit that
   matches the existing value is dropped rather than queued.
4. **Review and push.** One tier at a time — one secret, one version, one typed confirmation — but
   every key for that tier in a single version. *Review and write* opens the batch; **Push to Vault…**
   hands the document it produces to the push screen, which diffs it against the version Vault holds
   at that moment. See *Pushing to Vault*.

Three things worth knowing:

- **A queued edit records the value it was made against.** If a Vault read moves the tier underneath
  it, the edit is marked stale and the push is blocked until it is re-based or dropped, so a batch can
  never silently overwrite something that changed while it sat there.
- **Deleting the last key in an object removes the emptied parent too.** The flattener treats `{}`
  as a real, comparable state, so leaving a husk behind would turn "I removed this key" into "this
  tier now declares an empty section" — a different statement, and a new grid row rather than the
  removal that was asked for.
- **A brand-new key is checked harder than an edit**, because it is the one operation with no
  anchor and is indistinguishable from a typo. A path that exists in no tier is flagged, the closest
  known path is offered as a suggestion, a casing-only collision with an existing key is *refused*
  (the file's ordinal ordering treats `Url` and `URL` as two keys; the configuration binder treats
  them as one), and a type that disagrees with the other tiers is called out.

## Replacing JSON wholesale

The **Tier editor** tab edits one tier as a document rather than as a set of keys.

Pick a tier, search the hierarchy — a **Changed** switch in front of the box, then a path substring
or a glob once it contains a `*` — and click any node. Its subtree appears as canonical JSON, exactly
what the writer would emit for it, so pasting it straight back is a no-op rather than a reformat.

### The pane commits two different ways

Which one you are in depends on what is selected, and the pane says so on its bottom strip rather
than leaving it to be inferred from whether a button greyed itself out.

**A single value — a string, number, boolean or `null` — is applied as you type.** Changing
`Redis:Database` from `0` to `2` is a keystroke, not a keystroke and a button: there is no half-typed
state worth protecting anyone from, and the tree's mark and preview update as it lands. Text that
does not parse yet is not an error — it is a value on its way in, so nothing commits, the strip says
so quietly with the reader's own reason, and no red banner appears on the way through `"unterminate`.

**A section keeps the button.** An object or an array is invalid JSON for as long as it takes to
type one, so applying as you type would either fail on every keystroke or destroy the node. Those
wait for **Update node**.

Two things follow from applying as you type, and both are load-bearing:

- **The tree is re-marked in place rather than rebuilt.** A rebuild replaces every row, which
  reselects, which reloads the pane — and throws the caret back to the start of the value after every
  keystroke.
- **A run of keystrokes on one value is one undo step.** Undo reaches what the value was before you
  started typing, not a state that existed only mid-word. Moving the selection away ends the run, so
  coming back later is a second edit rather than a continuation of the first.

- **Update node** applies the pane to that node, and is still how a section is committed. Any valid
  JSON is accepted, including a different shape: an object can become an array or a scalar. It is
  disabled while the pane matches the document, so it lights up when there is something to apply and
  goes quiet once applied — and a reformat alone never lights it, because both sides are parsed and
  re-serialized before comparing. It stays available for a value as the way to turn one into a
  section. The root row is the whole document, so "replace everything" is reachable from the same
  tree.

  **It is also disabled while the pane does not parse**, and the strip underneath says why —
  the reader's own message, naming the character and the position. It used to stay lit on
  unparseable text so that pressing it produced that error; that made the button the only way to
  find out, and made an unpressable state look exactly like a pressable one — the same button,
  offering to replace a node with something that cannot be read. The answer is on screen now, and
  the button is offered only when it would work.
- **Find** — a switch on the toolbar, or `Ctrl+F`. It was a shortcut alone, which is not a control:
  nothing on screen said the bar existed. The bar is one row with replace beside it rather than under
  it, because it is open most of the time it is in use and a second row pushes the text down by a
  whole field. `F3` and `Enter` step forward, `Shift+F3` and `Shift+Enter` back, both wrapping; `Aa`
  is match case, worth having because this file's ordinal key order treats `Url` and `URL` as two
  keys, and it lights up while it is on rather than being a tick box you have to look twice at. The
  counter reads `3 of 12` in a fixed-width slot, so a count changing as you type does not shove the
  row sideways. Replace and Replace all edit the *pane*, not the document — for a section, Update
  node still has to follow, which is what keeps a bulk replace reviewable before it lands.

  `Ctrl+F` works from anywhere on the tab in both front ends. It was a key handler on the WPF pane
  alone, so it only opened the bar once the caret was already in the JSON — which is exactly where you
  do not need a shortcut to get to — and `F3` did nothing from inside the find box, which is where it
  is most likely to be pressed. `F3` is tab-wide in the WPF app; in the web app it works in the find
  box and the pane, which is where it is pressed, but not from the tree. Stepping and replacing are offered only when there is
  something to step to or replace, rather than staying lit and doing nothing; and clicking in the
  pane moves the search's idea of *here*, so the next `Enter` continues from where you are looking
  rather than from wherever the last step stopped.

  **Every match is highlighted**, softly, and the one you are standing on more strongly. Neither pane
  can colour part of its own content — a `<textarea>` cannot, and nor can a WPF `TextBox` — so both
  put the marks *behind* a transparent editor: the Photino app in a layer holding the same string at
  the same metrics, the WPF app in a `FindHighlightLayer` sharing the editor's grid cell. Both read
  their marks from one list on the view model, so a match is the same thing in both. WPF gets the
  easier half of it: `GetRectFromCharacterIndex` already answers in scrolled coordinates, so there is
  nothing there matching the browser side's scroll-syncing or its rule that every metric affecting
  where a character lands must be identical on both elements.

  Four things this fixed rather than added. Stepping used to search from the caret — and the caret
  sits *at* the current match after a step, so ↓ found the same one again; it walks an index into the
  match list now, which cannot land on the entry it is already on. Revealing a match used to focus
  the pane, which took the caret out of the find box and turned the second `Enter` into a newline in
  the JSON; the highlight is what shows the match now, so nothing needs focus to be visible and the
  box keeps it.

  The other two were the WPF highlights, which were drawn by an *adorner* and had both of the faults
  that arrangement guarantees. An adorner renders above what it adorns, and these brushes are opaque,
  so every mark erased the word it was marking — pale yellow on white in the light theme, which read
  as the searched term having simply gone missing from the pane. And an adorner lives in the window's
  adorner layer rather than in the tree it decorates: selecting another tab disconnects this pane,
  the layer drops the adorner, and the code that attached it only ever ran while its field was null —
  so after the first switch away from the Tier editor, nothing was ever drawn again for the life of
  the window. An ordinary child of the grid has neither fault, because it paints under the glyphs and
  travels with the pane.
- **Compact** and **Wrap** are switches rather than buttons, matching **Changed** on the tree. Both
  are states you leave set, and a button labelled with its own current state has to be read before it
  can be pressed — and reads as an action whichever way round it is labelled. Compact is display only
  (nothing compact is ever written) and switching mid-edit reformats what is in the pane rather than
  reloading the node, so an edit in progress survives it. Wrap is on by default: some of these values
  are tokens and PEM blocks hundreds of characters long, and reading one of those a screen-width at a
  time is not reading it. Turning it off is for the opposite need — seeing the indentation of a deep
  structure without every long value folding the shape out of it.
- **Every search box in the app clears itself.** The `×` appears once there is something to clear
  and is part of the field's own template, so the filter on All tiers, the path search here and the
  find box all have it without any of them knowing they do.
- **Undo node changes** puts one node back the way it was when the tier was opened, leaving every
  other edit alone. It appears only for a node that has changes of its own. That is the difference
  from Undo, which walks the history backwards and would take unrelated later edits with it.
- **Copy** puts the pane's JSON on the clipboard — exactly what is on screen, edits included, rather
  than what the document holds. The pane is the thing being looked at, and copying something subtly
  different from it would be the surprise.
- **Remove node** deletes one.
- **Reload node** and **Undo node changes** are easy to read as the same thing and are not. Reload
  node throws away the *text* in the pane and shows the node again as the document currently has it —
  it changes nothing. Undo node changes throws away the *changes already applied* to that node,
  putting it back the way it was when the tier was opened, and is itself an undoable edit. One is
  "forget what I typed", the other is "forget what I applied". With a value now applying as you type
  there is rarely anything un-applied to reload, so Reload node is mostly for sections.

**Changed nodes are marked in the tree**, on the node itself *and* on every section above it, so an
edit buried inside a collapsed subtree is found by following the marks down rather than by
remembering where you left off. The mark says *which kind* of change it is, because "changed" is
three different things and a tree that renders them identically makes you open every marked node to
find out which:

| mark | colour | means |
|---|---|---|
| `+` | green | added — this node was not in the document when the tier was opened |
| ✏ | amber | edited — it was there, and it was retyped |
| `−` | red, struck through | removed — it was there, and it is a tombstone now |
| `*` | blue | a section still present on both sides that *holds* changes, possibly all three kinds at once |

The distinction at the bottom row is the point of having four: a parent is not an edit, and labelling
it as one would be a lie told at exactly the level you are scanning. An added or removed subtree
carries its kind all the way down — every key inside a new section is itself new.

The mark is computed against the two trees rather than from the undo history: an undo, a revert, or
retyping a value back the way it was all leave history behind while leaving the document unchanged,
and a marker driven by history would keep insisting otherwise.

**A removed node stays in the tree until you save**, rendered from the document as it was opened —
its children included. Dropping it out of the tree the moment it was deleted would make the one edit
you cannot see also the one you cannot take back. Selecting a tombstone shows what it held,
read-only, and the button that would say *Undo node changes* elsewhere says **Restore node** instead,
because putting a deleted node back is not the same sentence — it is the same button, though, so it
is not dressed as a different one. Restoring from *inside* a deleted section restores the whole
section: recreating a parent holding nothing but the one key you clicked would be a partial restore
nobody asked for. Saving is what makes a removal final — after that the tombstone is gone, because
the document it was measured against is the one you just wrote.

### Arrays in the hierarchy

**An array's elements are rows like anything else.** The tree used to stop at an array and show only
`[12]`, which meant the only way to see what was in one was to select it and read the whole thing as
text — for the arrays in these documents, several hundred lines of it. Everything else in the app
already addressed those elements: the flattener produces a leaf path for each, the All tiers grid has
a row for each, and the navigator resolves both path forms. The hierarchy was the one place that did
not.

The paths are the flattener's, so a row here and a row on the All tiers tab are the same path rather
than two spellings of one: `Serilog:WriteTo[Name=Console]` where `arrays.json` declares an identity
field, `configuration:banners[3]` where it does not. Each element carries a one-line hint from its
first short string field — `code: bundle-a` — because `[0]` on its own says nothing about which one it
is.

Two things follow from arrays being ordered, and both are enforced rather than hoped for:

- **An element can be replaced but not removed.** Replacing is in place, so nothing after it moves.
  Deleting one shifts every element after it, and this editor shows a deletion as a tombstone
  measured against the document as opened — so one removal would present as "one gone and all the
  rest rewritten". Remove node is therefore not offered for an element, and the pane says to delete
  it from the array's own JSON instead. Undoing an element that was *added* since opening is refused
  for the same reason.
- **Marks are computed per element**, which `ChangeKinds` cannot do — it compares an array whole,
  because an array's elements are not separately addressable there. Under an index-named array an
  insertion still marks everything after it; that is the same thing the grid reports, and what a
  strategy in `arrays.json` exists to fix.

**The search box searches both documents**, the edited one and the one as opened. A removed key is
by definition not in the edited document, so searching that alone made a filter hide every tombstone
— which turned the one edit you cannot see into the one you cannot take back. Typing in the box
narrows what is shown; it never decides that a deletion did not happen.

**Changed** narrows the tree to exactly those nodes — the review view for a batch of edits before
saving them. Ancestors come along, so it stays a tree you can navigate rather than a flat list of
paths that tells you what moved without telling you where it lives. It sits in front of the search
box and composes with it rather than replacing it, and when it finds nothing it says which of the two
reasons applies: *nothing has been changed yet*, or *nothing matches*. It is a switch rather than a
tick box because it is a mode you leave on, and one left on is why the tree looks half empty an hour
later.

**A filtered tree opens expanded**, because a match hidden inside a collapsed parent is a filter that
lied about what it found — but it can still be collapsed. Those used to be the same statement: the
filtered view ignored collapse state entirely, which made the expander dead for as long as anything
was in the search box, and a search that finds two hundred rows is exactly when closing a section is
worth doing. The filtered view now has its own collapse state instead: empty by default, so it still
opens expanded, and emptied again whenever the filter changes, because it describes a tree that no
longer exists. Nothing leaks either way — a section closed while searching is not closed when you
come back out of the search.

Nothing leaves this window until Push. In between:

- **Undo / Redo** walk the replacements one at a time. Each step keeps the whole tree rather than a
  patch — these documents are ~28 KB, so a clone costs nothing measurable, and a patch-based undo
  would have to reason about what a wholesale replacement removed, which is the case most likely to
  be got wrong.
- **Revert all** returns to the state the tier was opened in, and is itself undoable. A revert
  button with an undo arrow next to it that did nothing would be worse than no revert button.
- **Compare with original** is a toggle rather than an action: it swaps the right-hand pane for a
  diff against that same opened state and stays lit while it is on. It is scoped to the selection —
  the selected node's subtree, or the whole document when the selection is the root row — because
  this screen is navigated one node at a time and a diff of 28 KB does not answer *what did I just
  do to this*. The header names the scope, so a diff is never mistaken for a leftover of the last
  selection. Moving the selection moves the comparison with it; any edit made meanwhile refreshes it
  rather than leaving a stale diff up; and it is disabled for a node with nothing to compare, so
  landing on one drops back to the editor rather than parking an empty diff behind a button that can
  no longer be pressed to escape it. Both sides go through the same writer, so what shows up is
  content rather than formatting. It is laid out **as opened | as edited**, so a removed line has
  somewhere to appear and a changed one shows what it changed from — a one-sided view rendered every
  deletion as an uncoloured blank row. Its three colours are the tree's: green added, red removed,
  amber edited, rather than the blue that elsewhere in this app means *these two tiers differ*.

⚠️ **This pane renders values in clear, credentials included.** It is the one screen in the app that
does, and it has to be: a subtree cannot be retyped without being read. The hierarchy beside it still
shows secrets as `•••••• len 64 a3f1c9`, which is the form worth scanning. Nothing else changes — the
All tiers grid and the promote and change dialogs all keep their masking.

The tab opens with **nothing selected**, because the first row is the whole document and landing on
it would fill the pane with 28 KB of JSON before anyone had asked for anything. The point of the
hierarchy is to get to one part of it.

### Where an edit goes

There is one answer, and it is the same one for the All tiers tab's batch and for this pane: **a new
version of the tier's Vault secret**, or nowhere.

That is a deliberate narrowing. There used to be a Save here, which wrote a
`app.<tier>.v<NN>.pending.json` — a file whose name meant "edited, not uploaded" and which then
had to supersede the snapshot it came from everywhere in the app, so that the grid, the diff tabs
and this pane all agreed with the app's own last write. It worked, and it cost a concept: a third
state for a tier, in between what Vault holds and what anyone had decided to deploy, which every
screen had to be able to explain.

**The cost of removing it is real and worth stating: closing the window loses an edit you have not
pushed.** There is nowhere to park work overnight. In exchange, a tier has one state, every screen
shows the same one, and the question "is what I am looking at what is deployed?" has stopped being a
question.

## Pushing to Vault

The other half of Pull, and the only way anything leaves this app. A push starts in exactly one
place — the title bar's **Push to Vault or disk** — and everything else hands on to the same screen:

| From | What it pushes |
|---|---|
| Title bar → **Push to Vault or disk** | opens the review of everything unwritten; you pick the tier |
| Pending changes → **Push to Vault…** | that tier, as the app is holding it |
| Promote → **Push to Vault…** | the destination with the promoted subtree added |

The tabs have no push buttons of their own. Every edit lands in the tier's shared in-memory document
the moment it is applied, wherever it was made, so the title bar's button already sends exactly what
any tab is showing — and a second way in is a second thing to keep in step with the first.

A push is one tier: one secret, one version, one typed confirmation. When several tiers are unwritten
the review stays open between them, advancing to the next tier each time one goes out, so a batch
spanning four tiers is four presses rather than four trips back through a tab.

The screen **reads the secret live as it opens**, and lays the two sides out **as Vault | as
pushed**:

```
VAULT v34  ·  2026-08-03 19:49        WHAT WOULD BE PUSHED — 6 queued key change(s) on stage
```

and names the destination above it in full, because the address is the one thing a confirmation
dialog must not leave to be assumed:

```
https://vault.example.com:8200  ·  kv/app/stage  ·  v34 → v35
```

The comparison is against what Vault holds **now**, not against what the app was showing. Those are
the same thing most of the time, and the times they are not are exactly the times this matters — so
if the version on screen is not the one this was built from, **the push is refused**. Push, and the
confirmation box beside it, both go dead, and the screen says why:

> Nothing was sent. This was built from v34 and kv/app/stage now holds v36 — somebody uploaded in
> between, and pushing would replace their version rather than merge with it. Save your work outside
> this app first — pulling replaces what is in memory, and these changes are only here — then pull
> again, redo them against v36, and push.

This used to be a warning that let the push through, on the reasoning that the diff showed what was
really being replaced. That is true and it is not enough: what the diff shows is *their* version
against *your whole document*, and the whole document is this app's unit of change — so confirming it
does not merge their change, it deletes it, through a check-and-set that reports success. A
concurrency guard that cannot refuse anything is a label, not a guard.

The way out is deliberately manual. This app cannot merge two documents and must not pretend to, and
the pull that gets you a fresh base **replaces what is in memory** — which is why the sentence says
to save your work before you do it rather than leaving that to be discovered. The same fence stands
in front of a local-file source whose file changed on disk since it was loaded, worded for a file:
there is no version number to name, and the backup taken before an overwrite makes such a write
recoverable, not consented to.

The diff still appears on a refused push. It is what says how much of somebody else's work the push
would have taken out — it just stops promising a version number for a write that is not going to
happen.

Otherwise the tier's name is typed out, and **Push to Vault** sends it.

### What stops it going wrong

| | |
|---|---|
| A document opened for comparison rather than as a source | refused before anything is read |
| The payload | re-parsed and re-flattened before it leaves, and refused unless it holds precisely the keys the document holds |
| The comparison | made against a read taken moments earlier, not against what was on screen |
| A base version that is no longer live | refused outright — nothing is sent, and the screen says to pull again and redo the change against what is there now |
| The write itself | carries that version as a KV v2 **check-and-set**, so a secret that moved in between is refused by Vault rather than clobbered |
| Afterwards | read back and compared — "the POST returned 200" and "Vault holds what I sent" are different claims, and only one of them is the reason to press the button |

A payload byte-identical to what Vault already holds is refused rather than uploaded: a version that
changes nothing is noise in a history whose whole value is that every entry means something.

It is also the only screen that can fail *after* doing something, so it reports each step rather than
one verdict — the new version number, the read-back check, and anything that went wrong after the
write landed. A read-back that cannot be performed is reported and is **not** treated as a failed
push: saying otherwise would send somebody to push again over a version that is already theirs.

Afterwards everything reloads onto what Vault now holds, because the tier's current state has changed
and every tab showing the previous one is showing history.

### What it deliberately does not do

**It pushes one tier per press.** A push is one secret, one version and one typed confirmation, and a
confirmation covering four environments at once would be worth less than the four it replaced.

**It keeps no copy of what it replaced.** The previous version is in Vault's own history, which is
the one place that cannot go stale; a local copy would be a second answer to a question that already
has one — which is the arrangement this app spent its first version explaining.

**Reading and writing are separate capabilities in Vault.** A token that pulls a secret is not
necessarily allowed to update it, and a 403 says exactly that rather than being folded into a generic
failure.

## Promoting a section

Pick a row that is missing from a tier, choose a destination, review the per-key actions, preview,
then **Push to Vault…** — which hands the destination-with-the-subtree-added to the push screen,
where it is diffed against the live secret and confirmed. Defaults per key:

| Class | Default | Why |
|---|---|---|
| business (bank codes, terminal ids, TTLs, flags) | copy verbatim | genuinely identical across tiers |
| infra (urls, hosts, ports) | placeholder `<<SET-FOR-beta>>` | completes the tier's shape, fails loudly at startup |
| secret | placeholder, value never shown | credentials are not copied between tiers |

The placeholder is deliberately not `""`: an empty string is a valid, deliberate value in these
documents, so blanking a key would be indistinguishable from someone forgetting to set it. It is
also why a promote is worth reviewing before it becomes a version — a placeholder that reaches an
environment fails loudly at startup, which is what it is for, but it fails *there*.

## Configuration

Everything the app knows lives in `JsonInsight/config/`, and the authored copy is preferred over
the one in `bin\`, so edits take effect the next time a project is opened, with no rebuild.

| File | Purpose |
|---|---|
| `tiers.json` | The tier list, and the fallback the Sources tab replaces once an active set is saved: an id, a label, `kind`, and the Vault secret or local file the tier *is*. Editing it is still a 5-line way to add an environment, and the UI grows a column with no code change — but it must also be added to every `members` block in `aliases.json`, or that alias silently disengages for *all* tiers. Append rather than insert: the Text diff tab preselects its two sides positionally. |
| `arrays.json` | How each array is compared. `Serilog:WriteTo` is matched on its `Name` field; Couchbase `Scopes` are unordered sets. An array matching neither is flagged rather than silently mis-diffed. |
| `aliases.json` | Concepts that are equivalent but structurally different, e.g. stage's 6-key `Redis` object versus beta's single packed `RedisCache:Configuration`. Reported once, not pretended to be a rename. |
| `classify.json` | secret / infra / business. Drives the promote defaults, the new-key warnings, and what the grid counts as drift. |

A Vault tier's `vaultPath` — or a local-file tier's `localFilePath` — is the tier. Without one there
is nothing to read and nothing to push to, and the tier is reported as not configured rather than
quietly shown as empty. For a document other than the root the path is derived from it; see *Choosing
a document*.

Source settings are the one exception to the config folder. They live in `JsonInsight/appsettings.json`,
written by the Sources tab and the projects screen, and safe to hand-edit while the app runs:

```jsonc
"Vault": {
  "LoadTiersAtStartup": true,
  "AlwaysOpenLastProject": false,
  "ActiveProject": "ui",
  "Projects": {
    "ui": {
      "ActiveSources": ["dev", "stage", "beta", "prod"],
      "Connections": {
        "dev": {
          "Kind": "LocalFile",
          "LocalFilePath": "D:\\snapshots\\dev.json"
        },
        "stage": {
          "SecretPath": "kv/app/stage/resources/config/ui.json",
          "Address": "https://vault.example.com:8200"
          // "Namespace": "team-b"   <- Vault Enterprise only; no UI, still honoured
        },
        "beta": {
          "SecretPath": "kv/app/beta/resources/config/ui.json",
          "Address": "https://beta-vault.example.com:8200",
          "AllowInsecureTls": true
        }
      },
      "LastOpenedUtc": "2026-08-08T09:41:12+00:00"
    },
    "content": { "...": "the same four environments, one path lower" }
  }
}
```

Tokens are in none of it — see *Projects* and the Sources tab above.

`appsettings.json` is **gitignored**: the addresses and secret paths in it describe a real
deployment. `JsonInsight/appsettings.example.json` is the starting point — copy it and edit.

### Where your Vault tokens are stored

A token never reaches `appsettings.json`. That is enforced structurally rather than by care:
`VaultConnection.Token` is `[JsonIgnore]` and the file is produced by serializing the model, so the
serializer *cannot* emit one even if the class changes later.

They go to .NET user secrets instead, under the id `jsoninsight-9f3c1d20`:

| Platform | File |
|---|---|
| Windows | `%APPDATA%\Microsoft\UserSecrets\jsoninsight-9f3c1d20\secrets.json` |
| Linux, macOS | `~/.microsoft/usersecrets/jsoninsight-9f3c1d20/secrets.json` |

Standard location, standard flat key format, so the CLI and this app edit the same file and see each
other's changes:

```
dotnet user-secrets set "Vault:Projects:appsettings:Connections:stage:Token" "hvs.…" --id jsoninsight-9f3c1d20
```

Three things worth knowing:

- **User secrets are not encrypted.** Microsoft is explicit that the Secret Manager is a development
  convenience, not a trusted store; the file is plain JSON protected by filesystem permissions alone.
  Anything running as you can read it. Prefer a short-lived token over a long-lived one, and see
  below for how to store nothing at all.
- **They outlive the app.** Uninstalling removes nothing. Delete the folder above to clear them.
- **The restart token is never stored anywhere**, unlike the Vault token — it is typed afresh for
  every call. Restarting a live environment cannot be undone, so it does not get a saved credential
  sitting next to a Test button on a row of environments that all look alike.

#### Storing nothing at all

If `VAULT_TOKEN` is set, or `~/.vault-token` exists — which is where `vault login` puts it — that is
used for any source with no token of its own, and nothing is written to disk by this app. A row's own
token still wins, so this is a fallback rather than an override:

```
vault login -method=oidc          # or ldap, approle, whatever your Vault uses
```

This is the recommended setup for production Vaults: the token is short-lived, it is refreshed by
the tool that owns it, and this app never holds a credential of its own.

## Code layout

Everything under `JsonInsight/`. The pipeline runs left to right: load → flatten → diff →
classify → (promote or edit).

| Folder | What lives there |
|---|---|
| `Model/` | The flat representation everything else works on: `TierDocument` per tier — carrying where it came from, since that changes what a write means — `Leaf` per key path, `FlatConfig` over one tier's leaves, and `ConfigDocument`, which is *which* JSON all of them are. |
| `Loading/` | Turning JSON into the flat model. `Flattener` (nested objects → `A:B:C` paths), `ArrayStrategy` (the `arrays.json` rules), `TiersConfig`, `DocumentTiers` (the tier list for a document other than the root), and `TierLoader` — which reads a *file*, and is used by the Compare files tab alone. |
| `Diff/` | `TierDiffer` for a pair, `MultiDiff` for the N-column grid, `DiffNode` for the roll-up into promote units, plus `AliasSet` and `PathGlob`. |
| `Classify/` | `Classifier` (secret / infra / business) and `SecretMasker`, which produces the `•••••• len 64 a3f1c9` rendering. |
| `Editing/` | Both editing models, neither of which writes. `PendingEdit` / `EditSet` / `EditApplier` / `EditValidator` are the key-by-key change set; `DocumentEditor` is the whole-subtree editor with the undo stack behind the Tier editor tab. |
| `Promote/` | `PromotionPlanner` builds the promote plan and applies it to a copy of the destination; `OrdinalJsonWriter` is the byte-exact canonical serializer; `JsonNavigator` resolves a configuration path against a tree, array elements included. None of it writes anything — a mutation produces a document, and `VaultPusher` is what does anything with one. |
| `Sources/` | What a source *is*, and the seam the rest of the app sees instead of Vault. `SourceKind` and `ISourceProvider` (load, blocked, preflight, save); `VaultSourceProvider` and `LocalFileSourceProvider`, which carry the same write fence — `PayloadValidator` is the shared middle of it rather than a copy; `SourceEnvironment`, the closed list of environment names a source is chosen from; `SourceProject`, one project's set of sources; `SourceProviders`, the registry both the read and the write path resolve through; and `SourceCatalog`, which turns the open project's settings into the `TiersConfig` everything downstream compares, or stands aside for `tiers.json` when nothing has been chosen. |
| `Vault/` | `VaultClient` (KV v2 read, list, mount enumeration, and one check-and-set write), `VaultPusher` (the fences, the preflight and the read-back for a Vault write), `VaultTierLoader` (a payload → a `TierDocument`), `TierRefresher` (the whole-fleet read behind both startup and the pull button, dispatching each tier to its provider), `VaultBrowser` (the bounded per-row metadata walk behind Search), and the settings pair: `VaultWorkspace` (every project, and the migration off the pre-projects shape — folding the app-wide document into each row's path and pushing the shared credentials down into the rows that relied on them) with `VaultSettings` (one project as everything else sees it) and `VaultSettingsStore`, the only code that knows which half goes to appsettings.json and which to user secrets. |
| `ViewModels/`, `Views/` | One VM and one view per tab, plus `ProjectsVm`/`ProjectsView` for the screen the app opens on, `PromoteDialog`, `EditDialog`, `ChangesDialog`, `PushDialog` and the layout converters. `JsonEditorVm` is the largest, because the hierarchy, the pane, the change markers and the undo state all belong to one screen. |
| `Themes/` | The look. `Light.xaml` and `Dark.xaml` hold colours and nothing else; `Controls.xaml` holds every style and template; `ThemeManager.cs` swaps the colour dictionary at runtime. |
| `Assets/` | `JsonInsight.ico` — a vault handwheel in the app's navy and accent blue, ten frames from 16 to 256px (the 16 and 20px frames drop the spokes, which turn to mush at that scale). `make-icon.ps1` draws and repacks it; run it only to change the icon. |
| `config/` | The four JSON files above. |

`AppPaths.cs` resolves the content root and the config directory; `CheckRunner.cs` is the whole
headless `--check` path, which is why that mode can never diverge from what the UI shows — they call
the same loader against the same server.

### The theme

The visual language is shared with the sibling DiskLens app — same token names (`Brush.Surface`,
`Brush.Accent`, `Text.Heading`, `Button.Primary`), same 12px cards, 7px controls and Segoe UI
Variable / Fluent Icons pairing — so the two read as one family.

**Every interactive control is one of two heights**, `Size.Control` (28) and `Size.Control.Dense`
(23), set on `Button.Base` and `Field.Base` rather than spelled into each style. Padding sets
horizontal breathing room only. The alternative is what this app had: a 34px button beside a 32px
field beside a 19px search box, each arrived at by its own padding, with no way to change the
density of the app without finding all of them. A button and the field next to it now line up by
construction rather than by coincidence.

There are **six button styles and three text-field styles**, and the rule for picking one is what
the control *is*, never how big it should look:

| Button | For |
|---|---|
| `Button.Primary` | the one action a screen is for — accent-filled |
| `Button.Secondary` | everything else, and the implicit default for an unstyled `Button` |
| `Button.Danger` | the writes, and only those. Soft until hover |
| `Button.Small` | an action inside a dense row — a grid row, a filter bar |
| `Button.Icon` | a bare glyph with no chrome, square at the control height |
| `Button.Ghost.Tiny` | the clear affordance *inside* a field, sized to the text rather than to a button |

| Field | For |
|---|---|
| (implicit `TextBox`) | a plain field, UI font |
| `TextBox.Mono` | monospaced, for paths and values compared literally. Stretches — the JSON pane is one |
| `TextBox.Inline` | the same on one line of a row: it does not stretch, so it cannot take the height of the tallest thing beside it |
| `TextBox.Search` | a filter field — icon, placeholder from `Tag`, and a clear button |

`Button.Base`, `Button.Ghost` and `Field.Base` are bases rather than choices; nothing picks them
directly. Two styles were deleted rather than kept "in case": `Button.Chip` had no usages at all,
and `TextBox.Cell` differed from `TextBox.Inline` by one pixel of padding.

Two rules keep the dark theme honest, and both are load-bearing rather than stylistic:

- **`Controls.xaml` never names a colour.** Every brush it uses is a `DynamicResource` pointing at
  a key that `Light.xaml` and `Dark.xaml` both define. `ThemeManager` replaces the colour
  dictionary at index 0 and every control repaints itself; nothing is reloaded, so switching theme
  mid-promote cannot lose a preview or a typed confirmation.
- **No converter returns a brush.** A converter runs once per binding and hands back a frozen
  brush, which would survive a theme switch and leave half the window in the previous theme. The
  findings colours — red for a missing key, amber for an incomparable shape, blue for a value that
  differs, green for agreement — are `Style` and `DataTemplate` triggers keyed on the row's own
  enum, so `Converters.cs` is down to visibility and indentation.

`JsonInsight.Tests` renders every view in *both* themes and asserts that the two colour
dictionaries define exactly the same key set — a key present in only one of them resolves to
nothing after a switch and silently drops a control's colour.

## Tests

```
dotnet test JsonInsight.sln
```

280 tests across twenty files (`RoundTripTests`, `PromoteTests`, `EditTests`,
`DocumentEditorTests`, `EditorPaneTests`, `ArrayNodeTests`, `TreeTests`, `DiffTests`, `SecretTests`,
`JsonCompareTests`, `VaultTests`, `VaultTierTests`, `PushTests`, `DocumentTests`,
`SourceProviderTests`, `LocalFileProviderTests`, `SourceCatalogTests`, `SourcesTabTests`,
`ProjectTests`, `UiSmokeTests`).

They run against **real the application payloads** — the last snapshots pulled before this app stopped keeping
any, read from `Fixtures` and wrapped as tiers exactly as a Vault read produces
them. Those files are fixtures now rather than a source: the app reads a tier from Vault and from
nowhere else. They are not copied into this repository, because they hold live credentials and
duplicating those to make a test tidier would be a poor trade.

**Nothing in the suite writes a settings file either**, which needs saying because it is not obvious:
`AppPaths.AppSettingsFile` resolves to the *authored* `JsonInsight/appsettings.json` even from a test
run, so a test that saved a project would create it in the developer's real settings, beside their
live tokens. The workspace tests therefore work on `VaultWorkspace` in memory, `SourcesTabTests`
drives `BuildSettings()` and stops short of `Save`, and the projects screen has a `Seed` for the same
reason the main view model does.

**Nothing in the suite touches the network**, and everything about the design points that way: the
Vault tests hand a payload straight to the loader, which is the same object a live read hands it to;
the push tests build the request body and read the response envelope through static methods that
never open a socket; and every view model that would read Vault takes a switch the harness sets to
false. A test that quietly reached production Vault would be worse than one that failed — and one
whose next button uploads to it, worse still.

The load-bearing ones:

- **Canonical text is a fixed point.** Serializing a document, re-parsing it and serializing again
  lands on the same bytes, for every tier. That is what makes a diff against a live secret mean
  content rather than formatting — both sides go through it.
- **Ordinal canary.** Lowercase `otp` must sort last in `Modules`, and `ConnectTimeoutMs` before
  `ConnectionString`. Both orderings fail under `OrdinalIgnoreCase` and under culture-aware
  sorting, so this test catches the wrong comparer.
- **Golden promote.** Promotes `AccountSettings:NightlyApprovalJob` into beta and asserts 11 new keys,
  correct sorted position, no reformatted lines, a source tier left untouched, and a document the
  pusher accepts.
- **Golden edit.** Widens all six `Modules:*:Url` values in one batch against beta, and asserts that
  the set of paths whose value changed is *exactly* those six — not a count, which would pass by luck
  once the URLs already held the target value.
- **Delete prunes.** Removing every key under `PaymentSettings:BillWalletLock` must leave
  no `{}` husk behind, because the flattener treats an empty object as a real comparable state.
- **An unavailable tier.** A tier Vault could not serve keeps its column in its configured position,
  its cells read as unknown rather than as missing, no row counts it as a gap, and no rollup offers
  to promote a subtree into it. The refresher reports it rather than substituting anything, because
  there is nothing to substitute.
- **Document editing.** Undo and redo walk the history exactly, a change after an undo drops the redo
  branch, revert-all is itself undoable, the original tree is never mutated (it is what *compare*
  compares against), and invalid JSON is refused without disturbing anything. What comes out is a
  document the pusher accepts, holding exactly the keys the edit said it would.
- **Node revert.** Reverting one node leaves a later unrelated edit standing — which is the whole
  reason it exists next to Undo — restores a removed subtree, removes a node that was added since
  opening, and is itself undoable. Compact text carries the same document as the indented form and
  parses back to it exactly, which is what makes the display toggle safe mid-edit.
- **Pending removals.** A removed node reports as *removed* rather than merely absent, and so does
  everything under it; a key never present reports as absent; and a key holding JSON `null` reports
  as present, because it is — reading it as removed would put a tombstone over a live setting.
- **Change markers.** An edit marks its own node and every ancestor and *nothing else* — a marker
  that lit up siblings would mean nothing. Markers clear when the document matches again however it
  got there: by undo, by revert, or by retyping the old value with two entries still in the history.
- **Arrays in the tree.** A keyed array's elements are named by identity and an unkeyed array's by
  position — the same paths the flattener produced, asserted against them rather than restated. An
  element replaces in place without disturbing its siblings and reverts the same way; removing one is
  refused with the reason; an edited element marks itself and its array while its siblings stay
  unmarked.
- **The editor pane.** A value applies as it is typed and a section does not; a run of keystrokes is
  one undo step and returning to the node later is a second; the tree is re-marked rather than
  rebuilt, because a rebuild moves the caret. A key removed by retyping its parent reads as a
  tombstone, and a search filter does not hide it. A key holding JSON `null` opens — `Find` and
  `Holds` disagree about those and `Holds` is the one that is right, which every tier has two of.
  Find wraps in both directions, match case actually distinguishes `Url` from `URL`, and replace-all
  terminates when the replacement contains the term.
- **Root arrays.** A document whose root is a JSON array shows its elements in the hierarchy, at the
  same paths the flattener produces; an element opens in the pane and replaces in place; the root
  keeps the kind it was opened as; and an edit to it marks the one row that can carry the mark. The
  tree used to walk such a document as an object, find nothing, and render a single row.
- **Collapsing while filtering.** A section can be collapsed while a search is on — it used to be
  impossible — and the collapse state does not leak in either direction between the filtered tree and
  the unfiltered one, because they are not the same tree.
- **The migration off the pre-projects shape.** The app-wide document is folded into each row's path
  exactly once however many times it runs, the shared address and token land on every row that had
  none without overwriting the rows that had their own, and a token whose row existed only in user
  secrets is carried across rather than pruned as a leftover.
- **Alias acceptance.** Raw stage→beta is 4/6; with aliases and `Name`-keyed arrays it collapses
  to 3/1 plus 4 explicit shape rows. If that stops differing, the alias machinery has stopped
  working.
- **Credential sweep.** Every key whose name says "credential" must not be classified as a business
  value. This test caught `PaymentSettings:Encryption:Profile:Key` rendering in clear, because the
  original `Encryption:**` rule was anchored at the root.
- **The push request, without a network.** The body carries the payload verbatim under a
  check-and-set version; a payload that is not a JSON object is refused before anything is sent; a
  read-shaped response is not accepted as proof that a write landed; and a check-and-set refusal is
  told apart from every other 400, because it is the one failure that is not a fault. Nothing in
  this file opens a socket — the body and the envelope are built and read by static methods for
  exactly that reason.
- **Push gates.** A read-only tier, a tier with no `vaultPath` and an unusable connection are each
  refused by name; the payload is the canonical document and holds exactly the keys the tree holds;
  and the secret pushed to is the tier's own path, so a document under the root can never be uploaded
  over the root.
- **UI smoke.** Every view and dialog is constructed against real view models on an STA thread with
  WPF's binding trace turned into assertions — a green compile proves nothing about whether a tab
  renders. The views are *shown* off-screen rather than only measured, which is what makes the test
  worth having: an unshown window never realises its DataGrid rows, so a cell template that throws
  cannot fail. That gap had been hiding a real crash in the promote dialog's Action dropdown, and it
  caught a wrong style binding in the edit dialog the first time that screen was rendered. The push
  dialog is rendered with its live read switched off and its rows supplied by the test: a rendering
  check that quietly reached production Vault — let alone one whose next button uploads to it —
  would be worse than no check at all.

## Not in this version

**No bulk push**: one tier per press, because a push is one secret, one version and one typed
confirmation. **No offline anything**: with no tier on disk there is nothing to read, compare or edit
without a reachable Vault, and an edit you have not pushed does not survive closing the window. **No
version browser**: this app reads the current version of a secret and nothing else, so rolling back
means reading the previous version somewhere that can show you one — Vault's own UI — and pushing it
again. **One document at a time**: switching is a reload rather than a second set of columns, so
there is no cross-document diff — that is what the Compare files tab is for. **No value translation
between incomparable shapes** (`Redis` to `RedisCache` is declared incomparable, with the reason
shown). On the All tiers tab, **no editing of array-valued keys** — the Couchbase `Scopes` sets are
read there and replaced on the Tier editor tab instead, which handles arrays because it replaces
whole nodes.
