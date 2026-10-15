using System.Collections.Generic;

namespace AiUsageBar.Models;

// ---------------------------------------------------------------------------
// Popup view-model: only vendors with an identified key (Ok, or configured
// but currently erroring). Login-needed / unconfigured vendors are hidden here
// and surfaced in the settings window instead.
// ---------------------------------------------------------------------------

public sealed class PopupModel
{
    public List<VendorCard> Vendors { get; init; } = new();

    /// <summary>True when no vendor returned usable data, which on a fresh
    /// install means nothing is signed in yet. Showing the raw CLI errors then
    /// tells a new user nothing actionable, so the popup shows
    /// <see cref="SetupHint"/> instead, with <see cref="SetupDetail"/> kept
    /// available underneath for diagnosis.</summary>
    public bool NeedsSetup { get; init; }

    public string SetupHint { get; init; } = "";

    /// <summary>Raw per-vendor errors behind the hint.</summary>
    public string SetupDetail { get; init; } = "";

    /// <summary>Warning above the cards: the refresh behind the data on show
    /// failed, or the report is newer than this build reads. Null when there is
    /// nothing to warn about.</summary>
    public string? Notice { get; init; }

    /// <summary>"Updated 14:32", shown in the footer so stale numbers are never
    /// mistaken for current ones.</summary>
    public string? UpdatedText { get; init; }

    public bool IsEmpty => Vendors.Count == 0;
}

public sealed class VendorCard
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Plan { get; init; }
    /// <summary>"ok" | "error"</summary>
    public string Status { get; init; } = "ok";
    public string? Message { get; init; }
    public List<Bar> Bars { get; init; } = new();

    /// <summary>Rows the CLI files under a heading (Claude Code sessions,
    /// SuperGrok's breakdown). Drawn compactly below the quota bars instead of
    /// as their peers.</summary>
    public List<BarGroup> Groups { get; init; } = new();

    public List<Fact> Facts { get; init; } = new();

    public bool HasPlan => !string.IsNullOrEmpty(Plan);
    public bool HasMessage => !string.IsNullOrEmpty(Message);
}

public sealed class BarGroup
{
    public string Title { get; init; } = "";
    public List<Bar> Bars { get; init; } = new();
}

public sealed class Bar
{
    public string Label { get; init; } = "";
    public int Pct { get; init; }
    public string? Reset { get; init; }
    /// <summary>"low" | "mid" | "high" | "critical"</summary>
    public string Level { get; init; } = "low";

    /// <summary>Right-hand side of the bar header: the percentage, or the CLI's
    /// value string when the metric says the value is its headline (a prepaid
    /// balance). Never the detail text: that grew into a full sentence ("Resets
    /// in 2h 16m, 54% elapsed, 32pts under"), and appending it here overlapped
    /// the label, which shares the same grid cell.</summary>
    public string ValueText { get; init; } = "";

    /// <summary>Detail line rendered under the bar, when the CLI supplies one.</summary>
    public bool HasDetail => !string.IsNullOrWhiteSpace(Reset);
}

public sealed class Fact
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string Text => $"{Label}: {Value}";
}

// ---------------------------------------------------------------------------
// Settings view-model: every supported vendor, configured or not. Editable
// fields are mutated in place by the settings form's two-way bindings.
// ---------------------------------------------------------------------------

public sealed class SettingsModel
{
    public long PollSeconds { get; set; }
    public string Primary { get; set; } = "anthropic";
    public List<VendorSetting> Vendors { get; init; } = new();
}

public sealed class VendorSetting
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Status { get; init; }

    public bool HasStatus => !string.IsNullOrEmpty(Status);
}
