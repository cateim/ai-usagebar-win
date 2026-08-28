# CLAUDE.md

Single source of truth for this repository. Read this before changing anything.

## Overview

`ai-usagebar-win` is a Windows system-tray app that shows AI plan usage at a
glance. It is a **thin wrapper** around the Rust CLI
[`ai-usagebar`](https://github.com/akitaonrails/ai-usagebar): the C# side runs
`ai-usagebar usage --json`, parses stdout, and renders it. It never calls
provider APIs and never handles credentials.

The project is a structural evolution of
[FranzoiDev/ai-usagebar-win](https://github.com/FranzoiDev/ai-usagebar-win).
The WPF UI was preserved; the internals were rewritten as the wrapper described
above.

## Tech stack

| Piece         | Version / notes                                          |
| ------------- | -------------------------------------------------------- |
| .NET          | 8 (`net8.0-windows10.0.19041.0`)                         |
| UI            | WPF, styled with WPF-UI 4.3.0 (Fluent, Mica, dark theme) |
| Tray icon     | H.NotifyIcon.Wpf 2.2.0                                   |
| Icon drawing  | System.Drawing.Common 9.0.0                              |
| Config format | TOML via Tomlyn 0.17.0                                   |
| Platforms     | `x64` and `arm64` (there is no `AnyCPU`)                 |
| Versioning    | CalVer, see below                                        |

## Architecture

```
Poller  ──runs──>  ai-usagebar usage --json  ──stdout──>  UsageJsonRoot
   │                                                          │
   └── raises Updated(Config, UsageJsonRoot) on the UI thread ─┘
                              │
                          Renderer
                              ├─> Rendered(Severity, Tooltip)  ─> TrayService
                              ├─> PopupModel                   ─> PopupWindow
                              └─> SettingsModel                ─> SettingsWindow
```

`Poller` owns all process execution and never throws at the caller: every
failure path (binary missing, non-zero exit, timeout, invalid JSON) is turned
into a synthetic entry with id `UsageJsonEntry.SystemId` and status `error`, so
problems surface in the UI instead of disappearing.

## Directory structure

```
AiUsageBar/
  App.xaml.cs              tray-first wiring, single instance, first-run shortcut
  Converters.cs            XAML value converters (severity to brush, etc.)
  Models/
    Interop.cs             JSON contract for `ai-usagebar usage --json`
    ViewModels.cs          popup and settings view-models bound by XAML
  Services/
    CliBinary.cs           resolves the bundled CLI, extracting it on first use
    CliSettings.cs         `settings show` / `settings apply` (primary, API keys)
    Config.cs              TOML load/save for poll_seconds, this app's own file
    Poller.cs              background polling loop, runs the Rust CLI
    Renderer.cs            JSON to tooltip / popup / settings models
    TrayIconFactory.cs     severity-tinted tray icon drawn in code
    TrayService.cs         H.NotifyIcon wrapper
    StartupService.cs      "Start with Windows" via the HKCU Run key
    ShortcutService.cs     Start Menu shortcut so Windows Search finds the app
    NativeMethods.cs       Win32 interop (cursor position, DPI)
  Views/
    PopupWindow.xaml       frameless popup anchored above the taskbar
    SettingsWindow.xaml    settings form
.github/workflows/
  ci.yml                   restore + build on push to master and on PRs
  release.yml              publish single-file .exe to a GitHub Release
scripts/
  sync-upstream.ps1        detect a newer ai-usagebar and release the catch-up
  release.ps1              bump, commit, tag and push a release in one step
  check-cli-contract.ps1   verifies the installed CLI still matches Interop.cs
  generate-icon.py         draws Assets/app.ico (sparkle in a severity-banded ring)
  capture-screenshots.ps1  captures the windows, redraws the tray tooltip
  demo-usage.json          five-provider sample for AIUSAGEBAR_WIN_FIXTURE
```

## Build and run

Always pass `-p:Platform=x64` (or `arm64`). The project declares those two
platforms only, and every command in CI and in the README specifies one.

```powershell
# Close a running instance first: it locks the .exe and the build fails with MSB3027.
Stop-Process -Name ai-usagebar-win -Force -ErrorAction SilentlyContinue

dotnet restore AiUsageBar.sln
dotnet build AiUsageBar/AiUsageBar.csproj -c Debug -p:Platform=x64

.\AiUsageBarind\Debug
et8.0-windows10.0.19041.0i-usagebar-win.exe
```

`dotnet run` does not work here. It looks for the output under `bin\Debug\`,
while `Platform` puts it in `bind\Debug\`, so it fails with "cannot find the
file". Run the produced `.exe` directly instead.

If the app then reports a missing .NET Desktop Runtime, the SDK was installed
privately (via `dotnet-install.ps1`) rather than by the official installer, and
the launcher cannot find it. Point it at the install once:

```powershell
[Environment]::SetEnvironmentVariable('DOTNET_ROOT', "$env:LOCALAPPDATA\Microsoft\dotnet", 'User')
```

This affects local builds only. Releases are published `--self-contained`, so
users never need a runtime installed.

Or open `AiUsageBar.sln` in Visual Studio, set the platform to x64, press F5.

For a local build you need `ai-usagebar` on `PATH` (`cargo install ai-usagebar`).
Local builds do not bundle the CLI, so `CliBinary` falls back to PATH. Without
it the app still starts and shows a "System Error" card explaining what failed.
Released builds need none of this: see "Bundled CLI" below.

`dotnet-install.ps1` at the root is the stock Microsoft SDK installer kept for
convenience. CI does not use it (it uses `actions/setup-dotnet`).

## Development helpers

Two opt-in environment variables exist only to drive the UI into states that are
awkward to reach on demand. Neither has any effect unless set.

| Variable | Effect |
|---|---|
| `AIUSAGEBAR_WIN_FIXTURE` | Path to a JSON file rendered instead of running the CLI. `scripts/demo-usage.json` holds the five-provider sample used for the README screenshots. |
| `AIUSAGEBAR_WIN_PIN_POPUP` | Set to `1` to stop the popup hiding on focus loss. Without it the popup cannot be screenshotted, since focusing a terminal dismisses it. |

`scripts/capture-screenshots.ps1` captures the popup and settings windows and
redraws the tray tooltip (the shell owns that one, so it cannot be captured).

## Tests

There are none, deliberately. An empty `AiUsageBar.Tests` project was inherited
from the fork and never contained a single test, so it was removed along with
its solution entries and the CI `Test` step. If you reintroduce tests, note that
the app under test is a WPF assembly, so the test project needs `UseWPF`.

## Configuration

Two files, on purpose. A missing file or parse error falls back to defaults,
never an exception.

| Setting                                                 | File                                       | Owner        |
| ------------------------------------------------------- | ------------------------------------------ | ------------ |
| `poll_seconds` (refresh interval, default 60, floor 15) | `%APPDATA%\ai-usagebar-win\settings.toml`  | this app     |
| `[ui] primary` (vendor leading the tooltip and popup)   | `%APPDATA%\ai-usagebar\config\config.toml` | the Rust CLI |

Everything else in the CLI's file (providers, API keys, credentials) belongs to
the CLI. See `config.example.toml`.

## Bundled CLI

A released build embeds `ai-usagebar.exe`, so a downloaded executable works on a
clean machine with no Rust toolchain. The pieces:

- `release.yml` runs `cargo install ai-usagebar --locked` on the runner, copies
  the result to `AiUsageBar/Assets/ai-usagebar.exe`, and **runs the contract
  check against it**. A CLI that no longer matches `Interop.cs` fails the release
  instead of shipping a build that cannot read its own backend.
- The `.csproj` embeds that file *conditionally* (`Exists(...)`), so local builds
  without it still compile.
- `CliBinary` extracts it to `%LOCALAPPDATA%\ai-usagebar-win\bin` on first use,
  re-extracting when the app version changes, and returns that path. **The
  bundled copy wins over PATH** so every user runs the CLI the release was tested
  with. With no resource embedded, it falls back to the `ai-usagebar` command.
- The binary is git-ignored. Never commit it.
- Redistribution is covered by `THIRD-PARTY-NOTICES.md` (the CLI is MIT).

The upstream version is **not pinned**: each release picks up whatever is current
on crates.io. That is a deliberate trade, chosen so upstream fixes arrive without
manual migration. The contract check is what makes it safe, and the release notes
record which CLI version each build shipped.

## Tracking the upstream CLI

Validated against **`ai-usagebar` 1.4.0**, with 1.7.0 current on crates.io at the
time of writing. Upstream moves fast (ten releases in three weeks around 1.0.0),
so treat schema drift as expected rather than exceptional. Note the contract
check covers `usage --json` only: `CliSettings` also depends on the shape of
`settings show` and on `settings apply` accepting `schema_version`, `primary` and
`keys`, where each key entry is `{action: "set"|"clear", value}`. Re-check those
by opening the settings window after an upstream jump.

Check whether the published app is behind upstream:

```powershell
pwsh -File scripts/sync-upstream.ps1
```

It compares three numbers: what crates.io publishes, what this machine has, and
what the last GitHub Release bundled (read back from the release notes, the only
record of it, since nothing pins the version). Add `-Release` to install the new
CLI, verify the contract against it, write the changelog entry and publish. A
failed contract check stops it before anything is committed.

The steps it automates, if you need them by hand:

```powershell
cargo install ai-usagebar --force --locked
ai-usagebar --version
pwsh ./scripts/check-cli-contract.ps1
```

The check compares the live JSON against the fields in `Interop.cs`, the
severity strings in `SeverityRules.Parse`, and the section types `Renderer.cs`
handles. It exits non-zero when something the app depends on is gone.

Failure modes ranked by how they show up:

- **Loud and safe**: the command or its flags change. `Poller` catches it and
  renders a "System Error" card. Nothing to design around.
- **Silent**: a field is renamed. `System.Text.Json` ignores unknown fields and
  defaults missing ones, so the vendor renders as `ready` with no bars. This is
  what the contract script exists to catch.
- **Cosmetic**: a new vendor id appears. The tooltip tag falls back to the first
  three letters of the id. `Renderer.cs` has short tags for 7 of the 16 vendors
  the CLI supports; extending that list is optional polish.

`usage --json` carries no `schema_version` (only `settings show` does), so there
is no version field to branch on. The script is the substitute.

## Versioning and release

CalVer `YEAR.MONTH.REVISION`, month without a leading zero, revision restarting
at 1 whenever the year or month changes.

`<Version>` in `AiUsageBar/AiUsageBar.csproj` is the **single source of truth**.
`AssemblyVersion` and `FileVersion` derive from it, and `release.yml` reads it
to name the artifact and tag the release.

### Release procedure

Use the script. It exists because every step below has been forgotten at least
once by hand:

```powershell
pwsh -File scripts/release.ps1 -Message "feat(ui): what changed"
pwsh -File scripts/release.ps1 -Message "..." -DryRun   # print, change nothing
```

It refuses to run outside master, refuses to publish a version `CHANGELOG.md`
does not document, reuses a hand-made bump instead of skipping ahead, and asks
for the tag to be typed back before pushing. If you stop at that prompt, the
commit and tag stay local and it tells you how to push or undo them.

The manual sequence it replaces, for reference:

**A version bump is not done until the tag is pushed.** Bumping `<Version>` on
its own produces nothing anyone can download: the artifact exists only after the
tag triggers the workflow. Treat the four steps below as one unit.

1. Bump `<Version>` in `AiUsageBar/AiUsageBar.csproj` to the next CalVer value.
2. Add the matching section at the top of `CHANGELOG.md`.
3. Commit both.
4. Tag and push, which triggers the `release` workflow:

   ```powershell
   git tag v<version>
   git push origin v<version>
   ```

Both lines in step 4 are required. `git tag` only creates the tag locally, and
`git push origin v<version>` is what publishes it. Pushing a tag that was never
created fails with `src refspec ... does not match any`.

**Standing order for agents:** whenever you bump the version, deliver the exact
`git tag` and `git push` commands in the same message as the bump, and state
plainly that the release is incomplete until they run. Never leave a bump in the
working tree without them, and never assume the tag step happened.

Alternative: run the `release` workflow manually from the Actions tab. It reads
`<Version>` and creates the tag itself. Use it only when no tag exists yet for
that version, since re-running it for an existing tag updates the current
release instead of creating a new one.

`release.yml` fails the run when a pushed tag disagrees with `<Version>`, so
never tag before the bump is committed.

To replace an already published version (same number, new binary), delete the
release and tag first, then recreate:

```powershell
gh release delete v<version> --yes --cleanup-tag
git tag -d v<version>            # only if it still exists locally
git tag v<version>
git push origin v<version>
```

## Conventions

- Default branch is `master`.
- Conventional Commits in English, lowercase title, body as a hyphen list with
  past-tense verbs.
- **No em dashes or en dashes anywhere**, including code comments and docs. Use
  a comma, colon, parentheses, or a new sentence.
- Comments explain _why_, not _what_. The existing comments carry the reasoning
  behind non-obvious decisions; preserve that when editing around them.
- Nullable reference types and implicit usings are enabled.

## Gotchas

**The CLI reports vendors the user never configured.** `usage --json` returns
every candidate vendor, and unconfigured ones come back with status `error`
("no API key", missing credential file). Since `GetWorstSeverity` maps any
non-`ready` status to `Critical`, rendering them all would pin the tray icon to
red permanently. `Renderer.ShouldShow` exists exactly to prevent that: it keeps
entries that are `ready`, the primary vendor (so a real outage still surfaces),
and the synthetic system entry. **Do not delete this filter.** It was removed
once by accident and had to be restored.

**Never write an app-only key into the CLI's config.** The CLI parses that file
with unknown top-level fields denied, so one stray key makes _every_ invocation
fail with a TOML parse error and the app shows nothing but a System Error. This
already happened once: `poll_seconds` was written there, so pressing Save in the
settings window bricked the CLI. Anything the CLI does not declare goes to
`Config.AppConfigPath()` instead. `Config.Save()` also removes the legacy
`poll_seconds` from the CLI file, repairing configs broken by older builds.

**An unrecognized severity must never read as healthy.** `SeverityRules.Parse`
maps an unknown string to `Severity.Unknown` (grey), not `Severity.Low` (green),
and both `GetWorstSeverity` and `Render` propagate a single `Unknown` instead of
letting a healthy sibling metric mask it. The reason: `Unknown` sorts below
`Low` in the enum, so a naive `Max()` would hide it. If the CLI renames a
severity level, the tray must say "no idea", never "all good" over a maxed-out
quota.

**Publish flags live in the workflow, not the `.csproj`.** `SelfContained`,
`PublishSingleFile` and the RID are passed only on the `dotnet publish` command
line. Putting them in the `.csproj` would force a RID onto every `dotnet build`
and break it.

**`Config.Save()` must preserve unknown keys.** The TOML file is shared with the
Rust CLI, which stores credentials there. Saving through a plain model would
delete the user's API keys, so saving goes through a `TomlTable` that keeps
properties the C# model does not know about.

**Provider settings go through the CLI, not through Tomlyn.** `CliSettings`
wraps `ai-usagebar settings show` (non-secret metadata: `primary`,
`primary_choices`, and a `configured` flag per vendor) and `settings apply`
(a JSON patch on stdin). Two reasons it must stay that way: the CLI writes with
`toml_edit`, preserving comments and keys we know nothing about, and it validates
field names, so the app cannot brick the config the way a stray `poll_seconds`
once did. `Config.cs` still owns `poll_seconds`, which is ours alone.

**Never log or pass an API key as an argument.** Keys travel to the CLI only on
stdin, so they stay out of the process list. `settings show` never returns a key
value, only whether one exists, so the settings window cannot display existing
keys: a blank field means "unchanged", and clearing is an explicit action.

**Windows only.** WPF requires Windows 10 2004 (19041) or later. There is no
cross-platform build.
