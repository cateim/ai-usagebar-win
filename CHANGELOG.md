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

### ✨ Added

- `scripts/sync-upstream.ps1`, which detects a newer `ai-usagebar` on crates.io,
  compares it against the version the last release bundled, verifies the JSON
  contract against that new CLI, and publishes the catch-up release. A failed
  contract check stops it before anything is committed.

### 🔧 Changed

- The build block in the README was repaired. Its Windows paths had been mangled
  by backslash escapes into commands that could not run.
- The popup and settings screenshots sit side by side, sized so both render at
  the same height, and a second row shows the tray tooltip next to the published
  executable's icon in Explorer.

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

[Unreleased]: https://github.com/cateim/ai-usagebar-win/compare/v2026.8.6...HEAD
[2026.8.6]: https://github.com/cateim/ai-usagebar-win/compare/v2026.8.5...v2026.8.6
[2026.8.5]: https://github.com/cateim/ai-usagebar-win/compare/v2026.8.4...v2026.8.5
[2026.8.4]: https://github.com/cateim/ai-usagebar-win/compare/v2026.8.3...v2026.8.4
[2026.8.3]: https://github.com/cateim/ai-usagebar-win/compare/v2026.8.2...v2026.8.3
[2026.8.2]: https://github.com/cateim/ai-usagebar-win/compare/v2026.8.1...v2026.8.2
[2026.8.1]: https://github.com/cateim/ai-usagebar-win/releases/tag/v2026.8.1
[0.3.0]: https://github.com/FranzoiDev/ai-usagebar-win/releases
