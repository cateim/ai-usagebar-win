<#
.SYNOPSIS
    Checks whether the bundled ai-usagebar CLI is behind upstream, and can cut a
    release that catches up.

.DESCRIPTION
    Releases do not pin the CLI: each one compiles whatever is current on
    crates.io. That keeps upstream fixes flowing without manual migration, but it
    also means nothing tells you a new version exists. This does.

    Run with no arguments to just look:

        pwsh -File scripts/sync-upstream.ps1

    It compares three numbers: what crates.io publishes, what your machine has,
    and what the last GitHub Release bundled. Add -Release to update, verify and
    publish in one go.

    Verification is the point. The CLI has broken this app's assumptions before,
    so before anything is published the new binary is installed locally and
    checked against the JSON contract in Interop.cs. A failure stops the release
    and prints what changed, instead of shipping a build that cannot read its own
    backend. The workflow repeats the same check on the runner.

.PARAMETER Release
    Update the local CLI, verify the contract and publish a new version.
    Without it, nothing is changed.

.PARAMETER Force
    Go through with a release even when the CLI is already current. Useful to
    republish after changing the app itself.

.PARAMETER SkipLocalCheck
    Do not install and verify the CLI locally. Faster (a Rust build takes a few
    minutes) but the contract is then only checked on the runner, after the tag
    is already public.

.EXAMPLE
    pwsh -File scripts/sync-upstream.ps1

.EXAMPLE
    pwsh -File scripts/sync-upstream.ps1 -Release
#>

[CmdletBinding()]
param(
    [switch]$Release,
    [switch]$Force,
    [switch]$SkipLocalCheck
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo

$UpstreamRepo = 'akitaonrails/ai-usagebar'
$Crate = 'ai-usagebar'

function Fail([string]$text) {
    Write-Host "ERROR  $text" -ForegroundColor Red
    exit 1
}

function Step([string]$text) {
    Write-Host "==> $text" -ForegroundColor Cyan
}

# -- Gather the three versions ----------------------------------------------

Step "Looking up versions"

# crates.io rejects requests without an identifying User-Agent.
try {
    $crate = Invoke-RestMethod -Uri "https://crates.io/api/v1/crates/$Crate" -TimeoutSec 30 -Headers @{
        'User-Agent' = 'ai-usagebar-win release tooling (github.com/CaTeIM/ai-usagebar-win)'
    }
    $latest = $crate.crate.max_version
} catch {
    Fail "could not reach crates.io: $($_.Exception.Message)"
}

$localVersion = $null
if (Get-Command ai-usagebar -ErrorAction SilentlyContinue) {
    $localVersion = ((& ai-usagebar --version) -split '\s+')[-1]
}

# The release notes record what each build actually shipped, which is the only
# trace of it: the version is not pinned anywhere in the repository.
$bundled = $null
try {
    # Joined into one string on purpose: -match against an array filters the
    # array and never populates $Matches, so the capture would come back empty.
    $body = (gh release view --json body --jq '.body' 2>$null) -join "`n"
    if ($body -match 'Bundled CLI:\s*\S*ai-usagebar\s+([0-9]+\.[0-9]+\.[0-9]+)') {
        $bundled = $Matches[1]
    }
} catch { }

Write-Host "     crates.io       $latest"
Write-Host "     this machine    $(if ($localVersion) { $localVersion } else { '<not installed>' })"
Write-Host "     last release    $(if ($bundled) { $bundled } else { '<unknown>' })"
Write-Host ""

$behind = $bundled -and ([version]$latest -gt [version]$bundled)

if ($behind) {
    Write-Host "The published app bundles $bundled, upstream is at $latest." -ForegroundColor Yellow
} else {
    Write-Host "The published app is up to date with upstream." -ForegroundColor Green
}

# -- What changed upstream ---------------------------------------------------

$notes = @()
if ($behind) {
    Step "Upstream releases in between"
    try {
        $releases = gh api "repos/$UpstreamRepo/releases?per_page=30" --jq '.[] | "\(.tag_name)"' 2>$null
        foreach ($tag in $releases) {
            $num = $tag.TrimStart('v')
            if ($num -match '^\d+\.\d+\.\d+$' -and
                [version]$num -gt [version]$bundled -and
                [version]$num -le [version]$latest) {
                $notes += $num
            }
        }
        # The API returns newest first; read them the way they happened.
        $notes = @($notes | Sort-Object { [version]$_ })
        $notes | ForEach-Object { Write-Host "     v$_" }
    } catch {
        Write-Host "     (could not list upstream releases)" -ForegroundColor DarkGray
    }
    Write-Host ""
}

if (-not $Release) {
    if ($behind) {
        Write-Host "To update and publish:  pwsh -File scripts/sync-upstream.ps1 -Release"
    }
    exit 0
}

if (-not $behind -and -not $Force) {
    Write-Host "Nothing to do. Use -Force to republish anyway." -ForegroundColor Yellow
    exit 0
}

# -- Verify before publishing ------------------------------------------------

if (-not $SkipLocalCheck) {
    Step "Installing $Crate $latest locally (a Rust build, this takes a few minutes)"
    cargo install $Crate --force --locked
    if ($LASTEXITCODE -ne 0) { Fail "cargo install failed" }

    $localVersion = ((& ai-usagebar --version) -split '\s+')[-1]
    Write-Host "     now on $localVersion"

    Step "Checking the JSON contract"
    & (Join-Path $PSScriptRoot 'check-cli-contract.ps1')
    if ($LASTEXITCODE -ne 0) {
        Fail @"
the contract check failed against $Crate $latest.

Upstream changed something this app depends on. Fix Interop.cs and Renderer.cs
to match, then run this again. Nothing was committed or published.
"@
    }
} else {
    Write-Host "Skipping the local check: the contract is only verified on the runner." -ForegroundColor Yellow
}

# -- Changelog ---------------------------------------------------------------

Step "Writing the changelog entry"

# Mirror release.ps1's CalVer rule so the section header matches the version it
# will compute.
$xml = [xml](Get-Content 'AiUsageBar\AiUsageBar.csproj')
$current = @($xml.Project.PropertyGroup.Version | Where-Object { $_ })[0]

$now = Get-Date
$prefix = "$($now.Year).$($now.Month)."
$revision = 1
if ($current -like "$prefix*") {
    $revision = [int]($current -replace [regex]::Escape($prefix), '') + 1
}
$next = "$prefix$revision"

$changelog = 'CHANGELOG.md'
$content = Get-Content $changelog -Raw

if ($content -match "(?m)^## $([regex]::Escape($next))\s*$") {
    Write-Host "     '## $next' already exists, leaving it alone"
} else {
    $from = if ($bundled) { $bundled } else { 'the previous version' }
    $line = "- Updated the bundled ``ai-usagebar`` CLI from $from to $latest."
    if ($notes.Count -gt 1) {
        $line += " That covers upstream releases $($notes -join ', ')."
    }
    $line += "`n  Upstream notes: <https://github.com/$UpstreamRepo/releases/tag/v$latest>"

    $entry = "## $next`n`n### Changed`n$line`n`n"

    # Insert above the newest existing section, keeping the file's header intact.
    $idx = $content.IndexOf("`n## ")
    if ($idx -lt 0) { Fail "could not find a section heading in $changelog" }

    $content = $content.Substring(0, $idx + 1) + $entry + $content.Substring($idx + 1)
    Set-Content $changelog $content -NoNewline
    Write-Host "     added '## $next'"
}

# -- Publish -----------------------------------------------------------------

Step "Handing over to release.ps1"
Write-Host ""

& (Join-Path $PSScriptRoot 'release.ps1') -Message "chore(cli): bundle ai-usagebar $latest"
