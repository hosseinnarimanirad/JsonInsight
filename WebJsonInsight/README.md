# WebJsonInsight

JsonInsight as a desktop app that runs on Windows, Linux and macOS. Same engine, same view models, same
Vault secrets — a different window.

It is not a website and not a server. Photino opens a native OS window holding the platform's own
webview, and Blazor renders into it in-process: no Kestrel, no localhost port, no browser. The
"Web" in the name is the UI technology, not the delivery.

## Why it exists

The WPF app is `net8.0-windows`, so it runs on one of the three machines this configuration gets
edited from. Everything below the window was already portable — the split described in *What is
shared* below was a file move, not a port — so the cost of a second front end was the views and
nothing else.

## Running it

```
cd D:\Projects\JsonInsight
dotnet run --project WebJsonInsight
```

Windows needs the WebView2 runtime, which ships with Windows 11 and current Edge. **Linux needs
WebKitGTK**, which is the one dependency that is not already on a stock machine:

```
sudo apt install libwebkit2gtk-4.1-0        # Debian/Ubuntu
sudo dnf install webkit2gtk4.1              # Fedora
```

macOS uses WKWebView and needs nothing installed.

Publishing for the other two platforms works from Windows and produces a self-contained folder:

```
dotnet publish WebJsonInsight -r linux-x64  --self-contained -c Release -o out/linux
dotnet publish WebJsonInsight -r osx-arm64  --self-contained -c Release -o out/macos
dotnet publish WebJsonInsight -r win-x64    --self-contained -c Release -o out/windows
```

Each carries its own native shim — `Photino.Native.so`, `.dylib`, `.dll` — plus `config/` and
`wwwroot/`. The headless check works the same as the WPF app's, and on a machine with no display it
is the only way to run this:

```
WebJsonInsight --check [-v]
```

## What is shared, and what is not

The repository is five projects. The split is the point of the whole exercise:

| Project | Target | What is in it |
|---|---|---|
| `JsonInsight.Core` | `net8.0` | The engine: `Model`, `Loading`, `Diff`, `Classify`, `Editing`, `Promote`, `Sources`, `Vault`, `AppPaths`, `CheckRunner`, `config/` |
| `JsonInsight.Presentation` | `net8.0` | Every view model, and the `Platform` seam |
| `JsonInsight` | `net8.0-windows` | WPF only: `App`, `MainWindow`, `Views/`, `Themes/`, `Assets/` |
| `WebJsonInsight` | `net8.0` | This app: the Photino host, the Razor components, the CSS |
| `JsonInsight.Tests` / `WebJsonInsight.Tests` | | 291 + 35 |

**The view models are shared, not reimplemented.** That is worth stating plainly because it is the
reason this app behaves like the other one rather than nearly like it: `MainVm`, `TiersVm`,
`JsonEditorVm` and the rest are the same objects the WPF app binds to. A fix to the promote planner
or the staleness rule lands in both at once, and the view-model tests cover both.

It was possible because the view-model layer turned out to touch WPF in exactly three places —
clipboard, file-open dialog, theme. Those are now `IClipboard`, `IFilePicker` and `IThemeService`
behind a static `Platform` locator; `WpfPlatform` registers one set, `PhotinoPlatform` the other.

**What is not shared is the views**, and they are a genuine rewrite: ~4,000 lines of XAML became
Razor components and one CSS file. Nothing was translated mechanically.

## How Blazor is bound to the view models

`ObservingComponent` is the whole adapter. WPF binds to `INotifyPropertyChanged` and re-renders the
affected element; Blazor re-renders a component when told to. So a component says what it is
watching and every notification becomes one `StateHasChanged`:

```csharp
protected override void OnParametersSet()
{
    StopObserving();
    Observe(Vm, Vm?.Rows, Vm?.Sections);
}
```

It is coarser than WPF's binding — a changed property re-renders the component rather than one
`TextBlock` — and that is the right trade: Blazor diffs its own output, so the cost is a render-tree
comparison rather than a DOM rewrite.

The one place it has to be finer is the Tier editor's tree, where each row is observed individually.
A scalar applied as you type re-marks its own row *in place* rather than rebuilding the tree — a
rebuild would replace every row, which reselects, which reloads the pane, which throws the caret
back to the start of the value after every keystroke. Watching only the collection would miss that
update; the `@key` on each row is what stops Blazor discarding the `<textarea>` along with it.

## The theme

`Light.xaml` and `Dark.xaml` became `wwwroot/css/theme.css`, token for token and name for name — a
colour can be found in either app by searching for the same string. The two rules that keep the WPF
dark theme honest carry over intact:

- **Nothing outside `theme.css` names a colour.** Every rule uses `var(--brush-*)`, which is the CSS
  equivalent of `DynamicResource`: flipping `data-theme` repaints everything with no component
  re-rendering, so switching theme mid-promote cannot lose a preview or a typed confirmation.
- **Both themes define the same token set.** A token in only one resolves to nothing after a switch
  and silently drops a control's colour.

Two things genuinely differ rather than translating:

- **Fonts.** Segoe UI Variable, Segoe Fluent Icons and Cascadia Mono ship with Windows and exist on
  neither Linux nor macOS. Each stack names the platform-native equivalents ahead of a generic
  fallback, so the app looks native on all three rather than looking like Windows on one.
- **Icons.** The WPF app draws from Segoe Fluent Icons by code point, which renders as a grid of
  empty boxes off Windows. `IconPaths.cs` replaces them with inline SVG — no download, inherits
  `currentColor`, and each entry names the Fluent glyph it stands in for.

The OS theme is read through `prefers-color-scheme`, which the webview answers the same way on all
three platforms. The WPF app reads a Windows registry key and has nothing to read anywhere else.

## Safety

Every fence is the same code, because it is literally the same code — `VaultPusher`,
`PayloadValidator` and the providers are in `JsonInsight.Core`. This app adds a window in front of
them, not a second implementation:

- A source marked `writable: false` is refused before anything is read.
- The payload is re-parsed and re-flattened before it leaves, and refused unless it holds precisely
  the keys the document holds.
- The comparison is against a read taken moments earlier, not against what was on screen.
- The write carries that version as a KV v2 **check-and-set**.
- The result is read back and compared.
- The destination's name has to be typed out.

`DialogService` holds the guards that live in `MainWindow.xaml.cs` in the WPF app — nothing writable,
nothing to copy from, the 60-key cap on a rolled-up edit — so they are in one place rather than
spread across the two tabs that trigger them.

> ⚠ **There is no undo.** A push is a new version of a live secret. What can put the old one back is
> Vault's own version history.

⚠ Everything on screen is live production configuration, credentials included. The Tier editor's
text pane renders values in clear — it has to, since a subtree cannot be retyped without being read —
and it is the only screen that does. Never paste a value into a report or a commit.

## Tests

```
dotnet test JsonInsight.sln
```

35 tests in `WebJsonInsight.Tests`, using bUnit. They are the Blazor half of what `UiSmokeTests` does
for WPF, and exist for the same reason: a green compile proves nothing about whether a component
renders.

Two things about this project are deliberate. It targets plain `net8.0`, so it runs on a Linux CI
box. And its fixtures are inline JSON rather than the real the application snapshots the WPF suite uses, so it
needs no credential files — the payloads are shaped on purpose to produce each state worth checking:
a dev-only subtree, a value two tiers disagree on, a secret, a keyed array.

The load-bearing ones:

- **An unavailable source keeps its column and reads as unknown.** `UNAVAILABLE` in the header, `?`
  cells, and nothing counting it as a gap. `?` is "I could not ask"; `—` is a finding.
- **A subtree missing from every other tier collapses to one row**, not eleven saying the same thing.
- **Secrets are masked**, in the grid and in the editor's tree — asserted as the absence of the
  fixture's plaintext from the rendered markup, so a new binding that leaked one would fail here.
- **An edit marks its node and its ancestors differently** — `Edited` on the node, `Mixed` above it.
  A parent is not an edit.
- **A removed node stays in the tree as a tombstone**, and the button that says *Undo node changes*
  elsewhere says *Restore node* on one.
- **The pane says how it commits** — applied as you type for a value, Update node for a section.
- **Push stays disabled until checked and confirmed**, and the confirmation must be the destination's
  own name.
- **A supplied document fixes the destination**, so a promote plan cannot be pushed into the wrong
  tier.
- **Queueing a value that already matches adds nothing.**
- **Editing more keys than the cap is refused with the count.**

Nothing in the suite touches the network. `PushVm` takes the same `checksOnOpen` switch `MainVm`
takes for the startup read, and it is false throughout.

## Not in this version

**No installer** — `dotnet publish` produces a folder. **No app icon on Linux/macOS**; the WPF app's
`.ico` is Windows-only and a `.icns` and a `.desktop` file have not been made. **No auto-update.**
**No second window**: one window, one project, exactly as the WPF app works.

The shell is deliberately swappable. The Razor components do not know they are in Photino, so
running them under Electron.NET or as a localhost Blazor Server app is `Program.cs` and packaging —
about 200 lines — rather than a port. That matters because Photino is a small project: its own docs
call it "still in early development", and in March 2026 its team announced a shift to AI-assisted
maintenance citing time constraints. It works, it is Apache-2.0, and the exit is cheap.
