using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AiUsageBar.Models;

public sealed class UsageJsonRoot
{
    /// <summary>Version of the report's shape, sent since CLI 1.17. Upstream
    /// raises it only for an incompatible change (adding or omitting fields does
    /// not count), so a value above <see cref="Services.Renderer.SupportedSchema"/>
    /// is surfaced in the popup instead of being trusted silently. Absent on an
    /// older CLI.</summary>
    [JsonPropertyName("schema_version")]
    public int? SchemaVersion { get; set; }

    [JsonPropertyName("entries")]
    public List<UsageJsonEntry> Entries { get; set; } = new();

    /// <summary>An entry id since CLI 1.21 (<c>anthropic@work</c> for a named
    /// account), a bare vendor slug before that. Compare it through
    /// <see cref="Services.Renderer.VendorSlug"/>, never directly.</summary>
    [JsonPropertyName("primary")]
    public string? Primary { get; set; }

    /// <summary>When the CLI produced this report. Set by the poller, never
    /// read from JSON; null on a synthetic error report.</summary>
    [JsonIgnore]
    public DateTimeOffset? ReceivedAt { get; set; }

    /// <summary>Why the latest refresh failed while this older report is still
    /// on show. Set by the poller; null when the report is current.</summary>
    [JsonIgnore]
    public string? RefreshError { get; set; }
}

public sealed class UsageJsonEntry
{
    /// <summary>Id of the synthetic entry injected when the CLI itself fails.</summary>
    public const string SystemId = "system";

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("fetched_at")]
    public DateTimeOffset? FetchedAt { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("metrics")]
    public List<UsageJsonMetric> Metrics { get; set; } = new();

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("plan")]
    public string? Plan { get; set; }

    [JsonPropertyName("sections")]
    public List<UsageJsonSection> Sections { get; set; } = new();

    /// <summary>Three-letter tag the CLI assigns each vendor, unique per vendor
    /// and validated as unique for custom providers too. The tooltip uses it
    /// instead of the guessed abbreviation this app used to derive from the id,
    /// which disagreed with the CLI for most vendors and collided outright for
    /// custom ones.</summary>
    [JsonPropertyName("short_name")]
    public string? ShortName { get; set; }

    [JsonPropertyName("stale")]
    public bool Stale { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";
}

public sealed class UsageJsonMetric
{
    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    /// <summary>Sub-group heading ("Sessions" for Claude Code context rows,
    /// "Breakdown" for SuperGrok product slices). Absent on a quota window that
    /// stands on its own. Grouped rows describe something other than the quota,
    /// so they must not drive the tray colour: see Renderer.QuotaMetrics.</summary>
    [JsonPropertyName("group")]
    public string? Group { get; set; }

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("percent")]
    public int Percent { get; set; }

    [JsonPropertyName("reset_at")]
    public DateTimeOffset? ResetAt { get; set; }

    [JsonPropertyName("severity")]
    public string? Severity { get; set; }

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";
}

public sealed class UsageJsonSection
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    // For "metric" type:
    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    /// <summary>See <see cref="UsageJsonMetric.Group"/>.</summary>
    [JsonPropertyName("group")]
    public string? Group { get; set; }

    /// <summary>"percent" or "value": which of the two the bar should lead
    /// with. A prepaid balance sends "value", where the percentage alone ("37%")
    /// says nothing and the amount ("$62.58") is the point.</summary>
    [JsonPropertyName("headline")]
    public string? Headline { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    /// <summary>On a metric, the bar's fill. On a "text" row it is present only
    /// when the row meters a prepaid cap (Cursor's On-Demand), and then it is
    /// the share of that cap already spent.</summary>
    [JsonPropertyName("percent")]
    public int? Percent { get; set; }

    [JsonPropertyName("reset_at")]
    public DateTimeOffset? ResetAt { get; set; }

    [JsonPropertyName("severity")]
    public string? Severity { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }

    /// <summary>Lines of a "block" section. Vendors that report no quota bars at
    /// all (Grok, DeepSeek, Kilo) put everything they have here, so dropping it
    /// left their card showing a name and nothing else.</summary>
    [JsonPropertyName("body")]
    public List<string>? Body { get; set; }
}
