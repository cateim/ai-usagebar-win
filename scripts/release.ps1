<#
.SYNOPSIS
    Commits, versions, tags and pushes a release in one go.

.DESCRIPTION
    A release only exists once the tag is pushed: bumping <Version> alone
    produces nothing anyone can download. Every step here has been forgotten at
    least once by hand, so the whole sequence is scripted:

      1. refuse to run outside master, or with nothing to release
      2. work out the next CalVer number (YEAR.MONTH.REVISION)
      3. write it into AiUsageBar.csproj
      4. refuse to continue unless CHANGELOG.md documents that version
      5. commit, tag, and push both
      6. report the release URL

    The version is only bumped when the .csproj still matches the last published
    tag. If you already bumped it by hand, that number is used as-is instead of
    skipping ahead.

.PARAMETER Message
    Commit subject. Should follow Conventional Commits, e.g.
    "feat(ui): add api key management".

.PARAMETER Version
    Override the computed version. Rarely needed.

.PARAMETER DryRun
    Print what would happen and change nothing.

.EXAMPLE
    pwsh -File scripts/release.ps1 -Message "feat(ui): add api key management"

.EXAMPLE
    pwsh -File scripts/release.ps1 -Message "fix: repair popup" -DryRun
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Message,

    [string]$Version,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo

$csproj = Join-Path $repo 'AiUsageBar\AiUsageBar.csproj'
$changelog = Join-Path $repo 'CHANGELOG.md'

function Fail([string]$text) {
    Write-Host "ERROR  $text" -ForegroundColor Red
    exit 1
}

function Step([string]$text) {
    Write-Host "==> $text" -ForegroundColor Cyan
}

function Run([string]$command) {
    if ($DryRun) {
        Write-Host "     [dry run] $command" -ForegroundColor DarkGray
        return
    }
    Write-Host "     $command" -ForegroundColor DarkGray
    Invoke-Expression $command
    if ($LASTEXITCODE -ne 0) { Fail "command failed: $command" }
}

# -- Preconditions -----------------------------------------------------------

Step "Checking the repository"

$branch = (git rev-parse --abbrev-ref HEAD).Trim()
if ($branch -ne 'master') {
    Fail "on branch '$branch'. Releases are cut from master."
}

$xml = [xml](Get-Content $csproj)
$current = @($xml.Project.PropertyGroup.Version | Where-Object { $_ })[0]
if (-not $current) { Fail "no <Version> found in $csproj" }

$dirty = (git status --porcelain) -join "`n"
$hasLocalCommits = (git rev-list --count '@{u}..HEAD' 2>$null)
$currentIsTagged = [bool](git tag -l "v$current")

# Three ways there is still work to do: uncommitted changes, commits not pushed,
# or a version that was committed but never tagged. That last one is the easiest
# to hit, because the commit makes it feel finished while nothing is published.
if (-not $dirty -and ($hasLocalCommits -eq '0' -or -not $hasLocalCommits) -and $currentIsTagged) {
    Fail "nothing to release: $current is already committed, pushed and tagged."
}

if (-not $dirty -and -not $currentIsTagged) {
    Write-Host "     nothing to commit, but $current has no tag yet: publishing it" -ForegroundColor Yellow
}

# -- Version -----------------------------------------------------------------

Step "Working out the version"

$lastTag = (git tag -l 'v*' | ForEach-Object { $_.TrimStart('v') } |
    Where-Object { $_ -match '^\d+\.\d+\.\d+$' } |
    Sort-Object { [version]$_ } | Select-Object -Last 1)

Write-Host "     .csproj says   $current"
Write-Host "     last tag is    $(if ($lastTag) { "v$lastTag" } else { '<none>' })"

if ($Version) {
    $next = $Version
    Write-Host "     using override $next"
}
elseif ($lastTag -and ([version]$current -gt [version]$lastTag)) {
    # Already bumped by hand and never published: publish that, do not skip ahead.
    $next = $current
    Write-Host "     .csproj is ahead of the last tag, releasing $next"
}
else {
    # CalVer: revision restarts whenever the year or month changes.
    $now = Get-Date
    $prefix = "$($now.Year).$($now.Month)."
    $revision = 1
    if ($current -like "$prefix*") {
        $revision = [int]($current -replace [regex]::Escape($prefix), '') + 1
    }
    $next = "$prefix$revision"
    Write-Host "     bumping to     $next"
}

$tag = "v$next"

if (git tag -l $tag) { Fail "tag $tag already exists. Delete it first, or pass -Version." }

# -- Changelog ---------------------------------------------------------------

Step "Checking CHANGELOG.md"

if (-not (Select-String -Path $changelog -SimpleMatch "## $next" -Quiet)) {
    Fail @"
CHANGELOG.md has no '## $next' section.

Describe the release before publishing it: the notes are the only thing telling
users what changed. Add the section, then run this again.
"@
}
Write-Host "     found '## $next'"

# -- Apply -------------------------------------------------------------------

Step "Writing the version"

if ($current -ne $next) {
    if (-not $DryRun) {
        (Get-Content $csproj -Raw) -replace
            "<Version>$([regex]::Escape($current))</Version>", "<Version>$next</Version>" |
            Set-Content $csproj -NoNewline
    }
    Write-Host "     $current -> $next"
} else {
    Write-Host "     already $next, nothing to write"
}

Step "Committing"

# Re-read: writing the version may have dirtied an otherwise clean tree.
if ((git status --porcelain) -join "`n") {
    Run "git add -A"
    Run "git commit -m `"$Message`""
} else {
    Write-Host "     nothing to commit, tagging the current commit"
}

Step "Tagging"
Run "git tag $tag"

# -- Publish -----------------------------------------------------------------

Step "Pushing"
Write-Host ""
Write-Host "About to push branch master and tag $tag to origin." -ForegroundColor Yellow
Write-Host "This publishes a GitHub Release and cannot be undone quietly." -ForegroundColor Yellow

if (-not $DryRun) {
    $answer = Read-Host "Type the tag ($tag) to confirm"
    if ($answer -ne $tag) {
        Write-Host ""
        Write-Host "Stopped. The commit and tag exist locally; nothing was pushed." -ForegroundColor Yellow
        Write-Host "Push later with:  git push origin master && git push origin $tag"
        Write-Host "Or undo with:     git tag -d $tag; git reset --soft HEAD~1"
        exit 0
    }
}

Run "git push origin master"
Run "git push origin $tag"

Write-Host ""
Write-Host "Released $tag" -ForegroundColor Green
Write-Host "The release workflow is building now. Watch it with:"
Write-Host "  gh run watch"
Write-Host "It compiles the current ai-usagebar from crates.io, checks it against"
Write-Host "the app's JSON contract, and fails the release if the contract broke."
