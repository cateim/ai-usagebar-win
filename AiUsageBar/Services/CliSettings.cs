using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace AiUsageBar.Services;

/// <summary>
/// Reads and writes provider settings through the CLI's own frontend API
/// (<c>ai-usagebar settings show</c> and <c>settings apply</c>) instead of
/// editing config.toml here.
///
/// <para>That matters for two reasons. The CLI writes through <c>toml_edit</c>,
/// preserving comments and unrelated keys, and it knows which fields are legal,
/// so we cannot brick it the way a stray <c>poll_seconds</c> once did.</para>
///
/// <para>Secrets only ever travel one way. <c>settings show</c> reports whether
/// a key exists, never its value, and new values are handed over on stdin, never
/// as arguments or environment variables, so they stay out of the process list.</para>
/// </summary>
public static class CliSettings
{
    private const int SchemaVersion = 1;

    /// <summary>Neither command touches the network, so this only has to
    /// outlast a busy machine, not a slow provider.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    // -- Reading -------------------------------------------------------------

    public sealed class Snapshot
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("primary")]
        public string? Primary { get; set; }

        [JsonPropertyName("primary_choices")]
        public List<Choice> PrimaryChoices { get; set; } = new();

        [JsonPropertyName("keys")]
        public List<VendorKey> Keys { get; set; } = new();
    }

    public sealed class Choice
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("label")]
        public string Label { get; set; } = "";
    }

    public sealed class VendorKey
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("label")]
        public string Label { get; set; } = "";

        /// <summary>Environment variable the CLI reads for this vendor. It wins
        /// over a stored key at runtime, so the UI says so rather than letting
        /// someone wonder why their typed value seems ignored.</summary>
        [JsonPropertyName("environment")]
        public string Environment { get; set; } = "";

        [JsonPropertyName("note")]
        public string Note { get; set; } = "";

        [JsonPropertyName("configured")]
        public bool Configured { get; set; }

        [JsonPropertyName("inline_configured")]
        public bool InlineConfigured { get; set; }

        [JsonPropertyName("environment_configured")]
        public bool EnvironmentConfigured { get; set; }
    }

    /// <summary>Reads the CLI's settings off the UI thread. Reading costs a
    /// process launch, and the settings window must not be built on top of a
    /// blocked dispatcher: the tray, the tooltip and the popup all share that
    /// thread.</summary>
    /// <returns>null when the CLI cannot be read, leaving the caller to fall
    /// back to the settings it can manage on its own.</returns>
    public static async Task<Snapshot?> LoadAsync()
    {
        try
        {
            var result = await CliProcess.RunAsync("settings show", null, Timeout).ConfigureAwait(false);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut)) return null;
            return JsonSerializer.Deserialize<Snapshot>(result.StdOut);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // -- Writing -------------------------------------------------------------

    /// <summary>One pending change to a vendor's stored key.</summary>
    public sealed record KeyChange(string VendorId, bool Clear, string? Value);

    /// <summary>Applies a patch, off the UI thread for the same reason as
    /// <see cref="LoadAsync"/>. Vendors absent from <paramref name="changes"/>
    /// are left untouched, which is what lets the UI leave key fields blank and
    /// mean "unchanged" rather than "erase".</summary>
    /// <returns>null on success, otherwise the error text to show.</returns>
    public static async Task<string?> ApplyAsync(string? primary, IEnumerable<KeyChange> changes)
    {
        var keys = new Dictionary<string, object>();
        foreach (var change in changes)
        {
            keys[change.VendorId] = change.Clear
                ? new { action = "clear" }
                : new { action = "set", value = change.Value ?? "" };
        }

        var patch = new Dictionary<string, object?>
        {
            ["schema_version"] = SchemaVersion,
        };

        if (!string.IsNullOrEmpty(primary)) patch["primary"] = primary;
        if (keys.Count > 0) patch["keys"] = keys;

        // Nothing to do: avoid rewriting the config for no reason.
        if (patch.Count == 1) return null;

        try
        {
            // Keys travel on stdin only, never as arguments, so they stay out of
            // the process list. CliProcess owns the pipe handling (both streams
            // drained together), which is what once hung the whole UI thread here.
            var result = await CliProcess.RunAsync("settings apply", JsonSerializer.Serialize(patch), Timeout)
                .ConfigureAwait(false);
            if (result.ExitCode == 0) return null;

            return string.IsNullOrWhiteSpace(result.StdErr)
                ? $"ai-usagebar settings apply exited with {result.ExitCode}."
                : result.StdErr.Trim();
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
