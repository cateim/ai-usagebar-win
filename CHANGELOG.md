# Changelog

All notable changes to ai-usagebar-win are documented in this file.

The format is based on [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/).

Versions follow **CalVer** in the `YEAR.MONTH.REVISION` format (`YYYY.M.R`, month
without a leading zero, revision restarting every month), so this project does
**not** follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html). The
`0.x` entry below uses the SemVer scheme this project shipped with before the
rewrite.

## [Unreleased]

<!--
Every notable change lands here first, under one of the six types, in this order:
Added, Changed, Deprecated, Removed, Fixed, Security.
Releasing renames this heading to `## [X.Y.Z] - YYYY-MM-DD`, opens a fresh empty
Unreleased above it, and updates the link references at the bottom of the file.
-->

## [2026.9.1] - 2026-09-09

An audit release. Five parallel reviews went through the process handling, the
JSON contract, the WPF layer, the config and secret paths, and startup cost,
cross-checked against the `ai-usagebar` 1.14.0 source. Most of what follows was
found there.

### ✨ Added

- **The settings window shows both version numbers**, opposite the buttons:
  `App <CalVer>  ·  CLI <semver>`, with the resolved CLI path in the tooltip.
  The two move independently, since no release pins the CLI, so a bug report
  naming only one of them cannot be reproduced. The tooltip settles the third
  variable, whether the CLI came from the bundled copy or from `PATH`. The CLI's
  version is read off the UI thread and cached for the life of the process.
- **A last line of defence for the tray.** An unhandled exception on the
  dispatcher used to end the process, and a tray app that vanishes leaves the
  user with no window, no message and nothing to report. It now survives, with
  the icon turned grey and the error in the tooltip.
- `scripts/sync-upstream.ps1`, which detects a newer `ai-usagebar` on crates.io,
  compares it against the version the last release bundled, verifies the JSON
  contract against that new CLI, and publishes the catch-up release. A failed
  contract check stops it before anything is committed.

### 🔧 Changed

- **A failed poll now retries in 5 seconds**, then 10, 20, 40, up to the
  configured interval, instead of waiting the full minute. The app is usually
  launched from the Run key, so its first poll races the network stack coming up
  at logon: the old behaviour showed a wrong tray icon for a minute, every time.
- **Releases are published ReadyToRun.** The startup path (WPF, the WPF-UI
  dictionaries, BAML parsing, JSON, TOML) is precompiled rather than JITted on
  first launch, at the cost of roughly a third more file size. Trimming stays
  off: it is unsupported for WPF.
- **The settings window opens before it reads anything.** Both the provider list
  and Save shell out to the CLI, and both did so synchronously on the UI thread,
  so clicking Settings produced nothing on screen until the CLI exited, with the
  tray frozen alongside it. Both are off-thread now, and Save is disabled while
  it runs so a second click cannot race the first.
- **The popup follows the taskbar.** It resolves the monitor under the cursor
  and that monitor's own DPI, and anchors against whichever edge the taskbar is
  docked to. It also repositions after a refresh, since content that grew while
  it was open used to slide behind the taskbar.
- **The tooltip uses the CLI's own three-letter tags.** The hardcoded table
  disagreed with the CLI for most vendors and gave every custom provider the
  same "cus".
- **`Config.Save()` no longer rewrites the CLI's config.** `[ui] primary` was
  already being written by the CLI itself, and the second write through Tomlyn
  stripped every comment in the file (the model API has no representation for
  one). The only thing the app still writes there is the removal of the legacy
  `poll_seconds`, and only when it is actually present.
- **The contract check validates each section type separately** and asserts that
  `percent` is an integer, and it now runs the same assertions over
  `scripts/demo-usage.json`. On a machine with no credentials, which is the
  release runner every time, errored entries carry no metrics and no sections,
  so most of the script was being skipped exactly where it is trusted most.
- `scripts/README.md` documents the maintainer tooling: when to run
  `sync-upstream.ps1` versus `release.ps1`, every flag, the prerequisites, what
  to do when the contract check fails, and the gotchas: the confirmation prompt
  takes the tag and nothing else, a local tag publishes nothing, and a Rust build
  failing on a locked file is a retry rather than a bug. `CLAUDE.md` records the
  one that cost a discarded release: since no release pins the CLI, publishing
  anything is already a CLI catch-up.
- `dotnet-install.ps1` moved from the repository root into `scripts/`, where the
  rest of the tooling lives. `CLAUDE.md` follows it.
- The build block in the README was repaired, and so was the same block in
  `CLAUDE.md`, which still held the mangled version: `\b` and `\a` had been
  written as a literal backspace and bell, leaving two control characters in the
  file and a run command nobody could copy. Both now point at `bin\x64\Debug\`,
  where `-p:Platform=x64` actually puts the executable.
- The popup and settings screenshots sit side by side, sized so both render at
  the same height, and a second row shows the tray tooltip next to the published
  executable's icon in Explorer. The settings shot was retaken for this release,
  showing the version footer and the eleven providers the current CLI reports.

### 🐛 Fixed

- **A fresh install showed an empty error instead of what to do next.** The CLI
  exits 1 when every vendor failed, which is exactly the machine where nothing is
  signed in yet, and it still prints a complete report naming the problem with
  each vendor. The app checked the exit code before parsing, threw all of that
  away, and showed "Process exited with code 1:" followed by nothing. The
  first-run guidance written for this case had never once been reachable. Output
  is parsed first now and the exit code judged second.
- **Everything that was not a quota bar was invisible.** The app handled section
  types `metric` and `fact`, and `fact` has never existed in the CLI: the real
  types are `metric`, `text`, `block` and `spacer`. Vendors that report no bars
  at all, Grok, DeepSeek and Kilo among them, rendered as a name over an empty
  card, hiding the prepaid balance that was the only number they had. OpenRouter
  lost three of its four rows. The fixture behind every screenshot was written in
  the same phantom shape, which is why it looked right.
- **Opening or saving settings could hang the entire app, with no way out but
  Task Manager.** `CliSettings` drained the CLI's stdout to the end before
  reading a byte of stderr, so anything over the 4 KB pipe buffer on stderr (a
  TOML parse error with source context, a Rust backtrace) deadlocked the two
  processes against each other. The 10-second timeout could not fire, because it
  ran after both reads. Both pipes are drained together now.
- **A save could silently delete every stored API key.** Writing `[ui] primary`
  read the CLI's config first and fell back to an empty table when the read
  failed for any reason, including a hand-edited typo or an antivirus holding the
  file open for a moment, then wrote that empty table over a file holding the
  user's credentials. Reading and writing are now told apart: an unreadable file
  is never written over.
- **Config writes are atomic.** They truncated the target first, so losing power
  or being killed mid-write left the CLI's config, API keys and all, empty or
  half written, with no backup.
- **Alt+F4 on the popup killed the app.** The window was closed for real while
  the app kept its reference, so the next tray click called `Show()` on a
  destroyed window and the process died. It hides instead, like the settings
  window already did.
- **One unlucky moment at logon left the bundled CLI unused for the whole
  session.** A failed extraction, typically the virus scanner holding the file it
  had just written, was cached as "fall back to PATH", and on a clean machine
  there is nothing on PATH: every poll failed until the app was restarted, with
  the correct binary sitting on disk. Failures are no longer cached, the move is
  retried briefly, the temp file carries the process id so two instances cannot
  collide, and the version stamp records the size so a truncated binary is
  detected instead of trusted.
- **"Start with Windows" broke silently when the executable moved.** The path was
  stamped in when the box was ticked and never revalidated, so moving the .exe
  left an entry Windows fails silently, while the checkbox still read as ticked.
  It is repaired on every launch, the way the Start Menu shortcut already was.
- **The popup painted an unrecognized severity green.** The tray already refuses
  to read an unknown level as healthy; the popup, which is where the numbers are
  actually read, did the opposite. It is grey now, on both surfaces.
- **A named account hid an outage on the primary vendor.** Entry ids are not bare
  slugs (`anthropic@work`, `custom:<id>`) while the primary always is, so a user
  with named accounts had no primary as far as the filter was concerned, and an
  expired login on the vendor they care about stopped surfacing entirely.
- **Quitting mid-poll could orphan the CLI process** or push an error into an
  app that was already tearing down. Cancellation is now told apart from the
  timeout, the child is killed in both cases, and the poller is disposed before
  the tray icon it reports to.
- Error cards no longer render unbounded CLI output verbatim: a captive portal
  answering with an HTML page turned the popup into a wall of markup.
- A render that throws degrades to an error card instead of taking the process
  down, and the three unguarded list accesses that could throw are null-safe.
- `sync-upstream.ps1 -Release` could never install anything. It stored the
  crates.io response in `$crate`, which PowerShell treats as the same variable as
  the `$Crate` holding the crate name, so `cargo install` was handed the whole
  JSON object and refused it with "missing crate name before '@'".

## [2026.8.6] - 2026-08-27

### ✨ Added

- Settings can now set the **API key** for every provider that uses one, eleven
  of them, instead of telling you to edit `config.toml` by hand. Each field says
  whether a key is already saved, or whether it is coming from an environment
  variable, which takes precedence. Existing keys are never shown back, so a
  blank field means "keep it"; removing one is an explicit Clear. The writing is
  done by the CLI, so comments and unrelated settings in the file are preserved.
- The provider list in Settings now comes from the CLI, so providers added
  upstream show up without a change here.

### 🔧 Changed

- **The tray icon is now a gauge.** Its ring fills clockwise with how much of the
  quota is gone, so a glance at the notification area answers "how much is left?"
  and not only "is it bad?". Colour still tracks severity.
- New application icon: an AI sparkle inside a ring split into the same severity
  bands the app uses (green below 50%, amber to 75%, orange to 90%, red above).
  The previous bar chart said nothing about what the app measures.
- The settings window shows the app icon in its title bar, which the custom
  title bar did not inherit from the executable.
- The interface is no longer tinted blue. Every grey was pulling toward blue,
  some by a wide margin; they are neutral now. The usage bars keep their green,
  amber and red.
- API keys sit in a collapsed section, so the settings window opens on the three
  options most people actually change instead of eleven key fields.
- Screenshots in the README were redone from the current build.

## [2026.8.5] - 2026-08-16

### 🔧 Changed

- First run on a machine with nothing signed in now explains what to do instead
  of showing raw credential errors: the popup asks you to sign in once with the
  Claude or Codex CLI, or to add an API key, and keeps the technical detail
  underneath in smaller text.
- The tray icon is grey, not red, when no provider returns data at all. Red is
  reserved for a quota that is actually in trouble, so a fresh install and a
  network outage no longer look like an emergency.

## [2026.8.4] - 2026-08-16

### ✨ Added

- The app now ships with the `ai-usagebar` CLI inside it. Installing Rust and
  running `cargo install` is no longer required: download the `.exe` and it
  works. The bundled copy is extracted on first use and takes precedence over
  any copy already on `PATH`, so everyone runs the version the release was
  tested against. Each release records which CLI version it shipped, and the
  redistribution terms are in `THIRD-PARTY-NOTICES.md`.

## [2026.8.3] - 2026-08-16

### 🔧 Changed

- Settings are now split by owner: the refresh interval belongs to this app, and
  only `[ui] primary` is written into the CLI's config.

### 🐛 Fixed

- Saving in the settings window no longer breaks the app. The refresh interval
  was written into the CLI's `config.toml` as `poll_seconds`, a key the CLI does
  not accept, and it then refused to parse the file at all, so every reading
  turned into "System Error". The interval now lives in
  `%APPDATA%\ai-usagebar-win\settings.toml`, and saving also removes the stray
  key from the CLI's file, repairing configs broken by earlier versions.

## [2026.8.2] - 2026-08-16

### ✨ Added

- The executable finally has an icon, three rising bars, instead of the generic
  Windows placeholder in Explorer, the taskbar and the installer.
- The tray icon uses that same three-bar shape rather than a plain square, while
  still being tinted by severity.

### 🐛 Fixed

- The popup no longer refuses to reopen. Closing it left an internal flag set, so
  every later tray click hid an already hidden window and the only way out was
  Task Manager.
- Output from the CLI is now decoded as UTF-8. Separators and accented text came
  through mangled (`Â·` instead of `·`) because the pipe was being read with the
  console code page.
- Usage bars no longer overlap their own text. The CLI's detail text grew into a
  full sentence and collided with the metric label; it now sits on its own line
  under each bar.

## [2026.8.1] - 2026-08-16

### ✨ Added

- 10-second timeout on the CLI process, preventing an indefinite hang.
- Captured `stderr` and surfaced it as a synthetic error entry, so a missing or
  failing binary is reported in the UI instead of failing silently.
- `Severity.Unknown` (grey icon) for the uninitialized state, distinguishing
  "not loaded yet" from "healthy".
- A script that checks the installed CLI against the JSON contract the app
  expects, so upstream schema changes are caught instead of failing silently.

### 🔧 Changed

- Architecture rewrite: the app is now a C# WPF wrapper around the native Rust
  `ai-usagebar` binary.
- Delegated all network requests, API key management, and OAuth to the Rust CLI.
- Config handling now preserves unknown keys on save, so CLI-owned settings
  survive a round trip.
- Replaced em-dashes with hyphens across user-facing strings.
- Migrated versioning from SemVer to CalVer.

### 🗑️ Removed

- Obsolete OAuth and API key logic from config and view models.
- The empty `AiUsageBar.Tests` project, which never contained a test.

### 🐛 Fixed

- Restored the build: the renderer still called `Config.IsConfiguredId`, removed
  during the legacy cleanup.
- Vendors you never configured are no longer listed. The CLI reports every
  candidate vendor, and the unconfigured ones came back as errors, which kept the
  tray icon permanently red.
- An unrecognized severity from the CLI now shows grey instead of green, so a
  future upstream rename cannot make a maxed-out quota look healthy.

## [0.3.0] - 2026-08-15

Inherited from the upstream project this repository was forked from. The date is
the fork point, since the release itself was published before this repository
existed. Earlier releases:
<https://github.com/FranzoiDev/ai-usagebar-win/releases>

### ✨ Added

- **Optional OAuth token refresh** for Claude/Codex (off by default): refreshes
  a near-expiry token and writes the rotated tokens back to the CLI credential
  files. The setting warns that it may sign out a CLI session.
- **Start with Windows** toggle (per-user `Run` registry key).
- **Start Menu shortcut** created on first run, so the app is findable in
  Windows Search.
- **Single-instance launch**: re-launching surfaces the existing popup instead
  of adding a second tray icon.

### 🔧 Changed

- Rewrote the app in **C# + WPF** (from the original Rust + Win32), styled with
  [WPF-UI](https://github.com/lepoco/wpfui) for a Fluent look (Mica backdrop,
  dark theme, modern controls). It ships as a single self-contained `.exe` built
  by GitHub Actions, with no Windows App SDK runtime needed.

### 🐛 Fixed

- The popup now anchors just above the taskbar instead of at the cursor height.

## Legend

| Type          | Meaning                                 |
| ------------- | --------------------------------------- |
| ✨ Added      | new features                            |
| 🔧 Changed    | changes in existing functionality       |
| ⚠️ Deprecated | features that will be removed soon      |
| 🗑️ Removed    | features removed in this release        |
| 🐛 Fixed      | bug fixes                               |
| 🔐 Security   | vulnerabilities addressed and hardening |

[Unreleased]: https://github.com/cateim/ai-usagebar-win/compare/v2026.9.1...HEAD
[2026.9.1]: https://github.com/cateim/ai-usagebar-win/compare/v2026.8.6...v2026.9.1
[2026.8.6]: https://github.com/cateim/ai-usagebar-win/compare/v2026.8.5...v2026.8.6
[2026.8.5]: https://github.com/cateim/ai-usagebar-win/compare/v2026.8.4...v2026.8.5
[2026.8.4]: https://github.com/cateim/ai-usagebar-win/compare/v2026.8.3...v2026.8.4
[2026.8.3]: https://github.com/cateim/ai-usagebar-win/compare/v2026.8.2...v2026.8.3
[2026.8.2]: https://github.com/cateim/ai-usagebar-win/compare/v2026.8.1...v2026.8.2
[2026.8.1]: https://github.com/cateim/ai-usagebar-win/releases/tag/v2026.8.1
[0.3.0]: https://github.com/FranzoiDev/ai-usagebar-win/releases
