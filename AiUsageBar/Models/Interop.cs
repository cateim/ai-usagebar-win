using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AiUsageBar.Models;

public sealed class UsageJsonRoot
{
    [JsonPropertyName("entries")]
    public List<UsageJsonEntry> Entries { get; set; } = new();

    [JsonPropertyName("primary")]
    public string? Primary { get; set; }
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

    [JsonPropertyName("label")]
    public string? Label { get; set; }

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
