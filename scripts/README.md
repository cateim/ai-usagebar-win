# scripts/

Maintainer tooling. Everything here is run by hand from the repository root with
PowerShell 7 (`pwsh`), never by the app and never by CI, with one exception:
`check-cli-contract.ps1`, which the release workflow also runs on the runner.

| Script                    | What it is for                                                                    |
| ------------------------- | --------------------------------------------------------------------------------- |
| `sync-upstream.ps1`       | Check whether the published app is behind the upstream CLI, and ship the catch-up |
| `release.ps1`             | Bump, commit, tag and push a release in one step                                  |
| `check-cli-contract.ps1`  | Verify the installed `ai-usagebar` still matches `Interop.cs`                     |
| `capture-screenshots.ps1` | Recapture the README screenshots                                                  |
| `generate-icon.py`        | Redraw `Assets/app.ico`                                                           |
| `demo-usage.json`         | Five-provider sample for `AIUSAGEBAR_WIN_FIXTURE`, not a script                   |
| `dotnet-install.ps1`      | Stock Microsoft SDK installer, vendored for convenience, not written here         |

## Which one do I run?

Two different releases, two different entry points. Picking the wrong one is the
only real trap here.

- **The app itself changed** (UI, services, docs, workflows): close the changelog
  by hand, then run `release.ps1`.
- **Only the bundled CLI is behind upstream**: run `sync-upstream.ps1 -Release`.
  It writes its own changelog entry and calls `release.ps1` for you.

They are not interchangeable. `sync-upstream.ps1 -Release` publishes a version
whose notes say nothing but "bundled CLI updated", so anything you left pending
in `[Unreleased]` would ship inside that version undocumented. The script refuses
to run in that case, on purpose, and it checks that **before** the Rust build so
the refusal costs seconds instead of five minutes.

## Prerequisites

| Need                     | Used by                                                |
| ------------------------ | ------------------------------------------------------ |
| PowerShell 7 (`pwsh`)    | everything                                             |
| `gh`, authenticated      | `sync-upstream.ps1` (reads the last release notes)     |
| Rust toolchain (`cargo`) | `sync-upstream.ps1 -Release`, unless `-SkipLocalCheck` |
| `ai-usagebar` on `PATH`  | `check-cli-contract.ps1`                               |
| Push rights on `origin`  | `release.ps1`                                          |

`release.ps1` needs no Rust: the release workflow compiles the CLI on the runner.

---

## sync-upstream.ps1

Releases do not pin the CLI version. Each one compiles whatever is current on
crates.io, so nothing in the repository tells you a newer one exists. This does.

### Just look (safe, changes nothing)

```powershell
pwsh -File scripts/sync-upstream.ps1
```

It prints three numbers and compares them:

```
     crates.io       1.7.0     what upstream publishes now
     this machine    1.4.0     what `cargo install` put on your PATH
     last release    1.4.0     what the last GitHub Release bundled
```

The third one is read back from the release notes, which is the only record of
it. When the app is behind, it also lists the upstream releases in between and
prints the command to publish the catch-up. Exit code is always 0 here.

### Publish the catch-up

```powershell
pwsh -File scripts/sync-upstream.ps1 -Release
```

In order, it:

1. refuses to continue if `[Unreleased]` in `CHANGELOG.md` has any entries,
2. runs `cargo install ai-usagebar --force --locked` (a Rust build, a few minutes),
3. runs `check-cli-contract.ps1` against the new binary and **stops on failure**,
   with nothing installed into the repo, committed or published,
4. inserts a `## [<next version>] - <today>` section describing the CLI bump,
   with the footer links,
5. hands over to `release.ps1`, which asks for confirmation before pushing.

### Flags

| Flag              | Effect                                                                                                                                           |
| ----------------- | ------------------------------------------------------------------------------------------------------------------------------------------------ |
| `-Release`        | Actually update, verify and publish. Without it nothing is changed.                                                                              |
| `-Force`          | Release even when the CLI is already current, to republish after app changes                                                                     |
| `-SkipLocalCheck` | Skip the local `cargo install` and contract check. Faster, but the contract is then only verified on the runner, after the tag is already public |

### When the contract check fails

Upstream renamed or removed something the app reads. Nothing was committed. Fix
`Models/Interop.cs` and `Services/Renderer.cs` to match, land that as its own
release, then run the catch-up again.

---

## release.ps1

Bumping `<Version>` alone publishes nothing anyone can download: the artifact
only exists after the tag is pushed. This script treats the whole sequence as
one unit.

```powershell
pwsh -File scripts/release.ps1 -Message "feat(ui): add api key management"
```

Always dry run first when unsure:

```powershell
pwsh -File scripts/release.ps1 -Message "..." -DryRun
```

### What it does

1. refuses to run outside `master`, and refuses when there is nothing to release
   (clean tree, nothing unpushed, current version already tagged),
2. works out the next CalVer number, `YEAR.MONTH.REVISION`, revision restarting
   whenever the year or month changes,
3. **reuses a hand-made bump instead of skipping ahead**: if `.csproj` is already
   ahead of the last tag, that number is published as-is,
4. refuses to continue unless `CHANGELOG.md` has a `## [<version>]` heading,
5. writes the version into `AiUsageBar/AiUsageBar.csproj`, commits, tags,
6. asks you to type the tag back before pushing anything.

If you stop at that prompt, the commit and tag stay local and it prints how to
push them later or undo them:

```powershell
git push origin master && git push origin v<version>   # push later
git tag -d v<version>; git reset --soft HEAD~1         # undo
```

### Flags

| Flag       | Effect                                        |
| ---------- | --------------------------------------------- |
| `-Message` | Required. Conventional Commits subject.       |
| `-Version` | Override the computed version. Rarely needed. |
| `-DryRun`  | Print every step, change nothing.             |

### Before running it by hand

Close the changelog first, or step 4 will stop you:

1. rename `## [Unreleased]` to `## [<version>] - <YYYY-MM-DD>`,
2. open a fresh empty `[Unreleased]` above it,
3. point the `[Unreleased]` footer link at the new tag and add the line for the
   version just cut,
4. validate:

   ```powershell
   pwsh -File ~/.claude/skills/changelog/scripts/check-changelog.ps1
   ```

The bracket form of the heading is not cosmetic. `release.ps1` greps for
`## [$next]` and `sync-upstream.ps1` inserts and matches on it, so a bare
`## <version>` heading breaks both.

### After the push

```powershell
gh run watch
```

The workflow compiles the current `ai-usagebar` from crates.io, checks it against
the JSON contract, embeds it, publishes a single self-contained `.exe`, and
records the bundled CLI version in the release notes. A broken contract fails the
release instead of shipping a build that cannot read its own backend.

To replace an already published version with a new binary under the same number:

```powershell
gh release delete v<version> --yes --cleanup-tag
git tag -d v<version>          # only if it still exists locally
git tag v<version>
git push origin v<version>
```

---

## check-cli-contract.ps1

```powershell
pwsh -File scripts/check-cli-contract.ps1
```

Runs the `ai-usagebar` currently on `PATH` and compares the live JSON against the
fields in `Models/Interop.cs`, the severity strings in `SeverityRules.Parse`, and
the section types `Renderer.cs` handles. Exits non-zero when something the app
depends on is gone.

It exists because the dangerous upstream change is the silent one: a renamed
field makes `System.Text.Json` fall back to defaults, so the vendor renders as
`ready` with no bars and nothing looks broken. Note it covers `usage --json`
only. `CliSettings` also depends on `settings show` and `settings apply`, so
after an upstream jump open the settings window once and confirm it still loads.

---

## capture-screenshots.ps1

Captures the popup and settings windows into `screenshots/` and redraws the tray
tooltip, which the shell owns and therefore cannot be captured. It grabs whatever
is already on screen, so the app has to be running with its windows open first:

```powershell
$env:AIUSAGEBAR_WIN_PIN_POPUP = "1"
$env:AIUSAGEBAR_WIN_FIXTURE = "scripts/demo-usage.json"
.\AiUsageBar\bin\x64\Debug\net8.0-windows10.0.19041.0\ai-usagebar-win.exe
# click the tray icon so the popup opens, then:
pwsh -File scripts/capture-screenshots.ps1
pwsh -File scripts/capture-screenshots.ps1 -Only popup   # or settings, hover
```

Pinning the popup is what makes this possible at all: without it, focusing the
terminal to run the script dismisses the very window being captured. The two
variables have no effect unless set:

| Variable                   | Effect                                                                                          |
| -------------------------- | ----------------------------------------------------------------------------------------------- |
| `AIUSAGEBAR_WIN_FIXTURE`   | Render this JSON file instead of running the CLI, see `demo-usage.json`                         |
| `AIUSAGEBAR_WIN_PIN_POPUP` | `1` stops the popup hiding on focus loss, otherwise it vanishes the moment you focus a terminal |

---

## generate-icon.py

```powershell
python scripts/generate-icon.py
```

Redraws `AiUsageBar/Assets/app.ico`, a sparkle inside a severity-banded ring.
Run it only when the icon design changes, and commit the regenerated `.ico`.
