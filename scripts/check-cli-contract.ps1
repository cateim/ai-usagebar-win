<#
.SYNOPSIS
    Checks that the installed `ai-usagebar` CLI still matches what this app expects.

.DESCRIPTION
    This app is a wrapper: it parses the CLI's JSON and renders it. When the CLI
    changes its schema, most of the damage is silent, because System.Text.Json
    ignores unknown fields and defaults missing ones. A renamed field shows up as
    a vendor with no bars, not as an error.

    Run this after every `cargo install ai-usagebar --force` to catch that early.

    Errors mean the app will misbehave. Notices mean the CLI grew something the
    app does not consume yet, which is informational, not a failure.

.EXAMPLE
    pwsh ./scripts/check-cli-contract.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# Mirrors AiUsageBar/Models/Interop.cs. Keep both sides in sync.
$RootFields    = @('entries', 'primary')
$EntryFields   = @('display_name', 'error', 'fetched_at', 'id', 'metrics',
                   'name', 'plan', 'sections', 'stale', 'status')
# Known to exist and safe to be absent: 'icon' is never read, and 'short_name'
# has a fallback in Renderer.Tag for an older CLI.
$EntryOptional = @('icon', 'short_name')
$MetricFields  = @('detail', 'label', 'percent', 'reset_at', 'severity', 'value')
# Present only when the vendor states the reset window exactly. Deliberately not
# consumed: the app shows the CLI's own detail text rather than computing a pace.
$MetricOptional = @('window_secs')
# A section is a tagged union, so no single entry carries every field: 'metric'
# brings percent and severity, 'text' brings value, 'block' brings body, and
# 'spacer' brings nothing at all. The list is the union of all four, which is why
# missing fields are checked per type below rather than against this.
$SectionFields = @('type', 'body', 'detail', 'label', 'percent', 'reset_at',
                   'severity', 'value', 'window_secs')

# Mirrors SeverityRules.Parse and the section handling in Renderer.cs.
$KnownSeverities  = @('low', 'mid', 'high', 'critical')
# The four the CLI actually emits (ReportSection in report.rs). The app used to
# list a 'fact' type that has never existed upstream while ignoring the real
# 'text' and 'block', so every non-metric row was silently dropped and this
# whitelist is what kept the check quiet about it.
$KnownSectionType = @('metric', 'text', 'block', 'spacer')
$ExpectedSettingsSchema = 1

$contractErrors  = [System.Collections.Generic.List[string]]::new()
$contractNotices = [System.Collections.Generic.List[string]]::new()

function Get-Fields($obj) {
    if ($null -eq $obj) { return @() }
    return $obj.PSObject.Properties.Name
}

function Assert-Section($obj, [string[]]$required, [string]$label) {
    $actual  = Get-Fields $obj
    $missing = $required | Where-Object { $_ -notin $actual }
    if ($missing) { $script:contractErrors.Add("$label is missing: $($missing -join ', ')") }

    $extra = $actual | Where-Object { $_ -notin $script:SectionFields }
    if ($extra) { $script:contractNotices.Add("$label has new fields the app ignores: $($extra -join ', ')") }
}

function Compare-Shape($obj, [string[]]$required, [string]$label, [string[]]$optional = @()) {
    $actual  = Get-Fields $obj
    $missing = $required | Where-Object { $_ -notin $actual }
    $extra   = $actual   | Where-Object { $_ -notin $required -and $_ -notin $optional }
    if ($missing) { $script:contractErrors.Add("$label is missing: $($missing -join ', ')") }
    if ($extra)   { $script:contractNotices.Add("$label has new fields the app ignores: $($extra -join ', ')") }
}

# -- CLI presence and version ------------------------------------------------

$cli = Get-Command ai-usagebar -ErrorAction SilentlyContinue
if (-not $cli) {
    Write-Host "ai-usagebar not found in PATH. Install it with: cargo install ai-usagebar" -ForegroundColor Red
    exit 1
}

$version = (& ai-usagebar --version 2>&1 | Select-Object -First 1)
Write-Host "CLI:  $version"
Write-Host "Path: $($cli.Source)"
Write-Host ""

# -- usage --json ------------------------------------------------------------

$raw = & ai-usagebar usage --json 2>&1
$usageExit = $LASTEXITCODE

# Parse first, judge the exit code second. A machine with no credentials at all
# (a CI runner, for instance) can exit non-zero while still emitting a perfectly
# well-formed document, and the shape is what this script is here to check. An
# unparseable payload is the real failure: that is what a rejected config.toml or
# a changed output mode looks like.
try {
    $usage = $raw | ConvertFrom-Json
} catch {
    Write-Host "'ai-usagebar usage --json' did not return valid JSON (exit $usageExit)." -ForegroundColor Red
    Write-Host $raw
    exit 1
}

if ($usageExit -ne 0) {
    $contractNotices.Add("'ai-usagebar usage --json' exited with $usageExit but returned valid JSON (expected when no vendor is configured).")
}

function Test-Report($report, [string]$source) {
    Compare-Shape $report $RootFields "$source root"

    $entries = @($report.entries)
    Write-Host "$source entries: $($entries.Count)"

    foreach ($entry in $entries) {
        $id = if ($entry.id) { $entry.id } else { '<no id>' }
        Compare-Shape $entry $EntryFields "entry '$id'" $EntryOptional

        foreach ($metric in @($entry.metrics)) {
            Compare-Shape $metric $MetricFields "entry '$id' metric" $MetricOptional
            if ($metric.severity -and $metric.severity -notin $KnownSeverities) {
                $contractErrors.Add("entry '$id' has unknown severity '$($metric.severity)'. Renderer.cs will treat it as Unknown (grey).")
            }
    }

        foreach ($section in @($entry.sections)) {
            if ($section.type -notin $KnownSectionType) {
                $contractNotices.Add("entry '$id' has section type '$($section.type)', which the app does not render.")
            }

            # Each variant carries its own fields, so each is checked for the ones
            # the renderer actually reads. Checking only 'metric' was how the app
            # went several releases reading a section type that did not exist.
            switch ($section.type) {
                'metric' { Assert-Section $section @('type', 'label', 'percent', 'severity') "entry '$id' metric section" }
                'text'   { Assert-Section $section @('type', 'label', 'value')               "entry '$id' text section" }
                'block'  { Assert-Section $section @('type', 'label', 'body')                "entry '$id' block section" }
            }

            # A float percent would throw on deserialization into `int Percent` and
            # turn every poll into a System Error card, so the type is asserted and
            # not only the field's presence.
            if ($section.type -eq 'metric' -and $null -ne $section.percent) {
                if ($section.percent -isnot [int] -and $section.percent -isnot [long]) {
                    $contractErrors.Add("entry '$id' metric percent is $($section.percent.GetType().Name), not an integer. Interop.cs declares int and will throw.")
                }
                }
        }
    }
}

Test-Report $usage 'usage'

# -- The fixture, checked against the same shape -----------------------------

# An errored entry carries no metrics and no sections, so on a machine with no
# credentials (the release runner, every time) the loops above check the root and
# the entries and nothing else. That is most of this script skipped precisely
# where it is trusted most. The fixture fills the gap: it is a full report with
# every section type, so the metric and section assertions always run.
#
# It also keeps the fixture honest. It was written in a shape the CLI has never
# emitted, and being the only report the screenshots ever rendered is exactly why
# nobody noticed.

$fixturePath = Join-Path $PSScriptRoot 'demo-usage.json'
if (Test-Path $fixturePath) {
    try {
        $fixture = Get-Content $fixturePath -Raw | ConvertFrom-Json
        Test-Report $fixture 'fixture'
    } catch {
        $contractErrors.Add("demo-usage.json is not valid JSON: $($_.Exception.Message)")
    }
} else {
    $contractNotices.Add("demo-usage.json not found, fixture shape not checked.")
}

# -- settings show -----------------------------------------------------------

$settingsRaw = & ai-usagebar settings show 2>&1
if ($LASTEXITCODE -ne 0) {
    $contractNotices.Add("'ai-usagebar settings show' is unavailable in this version.")
} else {
    try {
        $settings = $settingsRaw | ConvertFrom-Json
        if ($settings.schema_version -ne $ExpectedSettingsSchema) {
            $contractErrors.Add("settings schema_version is $($settings.schema_version), expected $ExpectedSettingsSchema.")
        }
    } catch {
        $contractErrors.Add("'ai-usagebar settings show' did not return valid JSON.")
    }
}

# -- Report ------------------------------------------------------------------

Write-Host ""
foreach ($n in $contractNotices) { Write-Host "NOTICE  $n" -ForegroundColor Yellow }
foreach ($e in $contractErrors)  { Write-Host "ERROR   $e" -ForegroundColor Red }

Write-Host ""
if ($contractErrors.Count -gt 0) {
    Write-Host "Contract check FAILED with $($contractErrors.Count) error(s)." -ForegroundColor Red
    Write-Host "Update AiUsageBar/Models/Interop.cs and Renderer.cs, then re-run."
    exit 1
}

Write-Host "Contract OK." -ForegroundColor Green
if ($contractNotices.Count -gt 0) {
    Write-Host "$($contractNotices.Count) notice(s) above are informational."
}
exit 0
