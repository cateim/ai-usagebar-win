using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
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
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

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

    /// <summary>Off-thread <see cref="Load"/>. Reading settings costs a process
    /// launch, and the settings window must not be built on top of a blocked
    /// dispatcher: the tray, the tooltip and the popup all share that thread.</summary>
    public static Task<Snapshot?> LoadAsync() => Task.Run(Load);

    /// <summary>Returns null when the CLI cannot be read, leaving the caller to
    /// fall back to the settings it can manage on its own.</summary>
    public static Snapshot? Load()
    {
        try
        {
            var (exitCode, stdout, _) = Run("settings show", null);
            if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout)) return null;
            return JsonSerializer.Deserialize<Snapshot>(stdout);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // -- Writing -------------------------------------------------------------

    /// <summary>One pending change to a vendor's stored key.</summary>
    public sealed record KeyChange(string VendorId, bool Clear, string? Value);

    /// <summary>Off-thread <see cref="Apply"/>, for the same reason as
    /// <see cref="LoadAsync"/>. The change list is copied first: it is read on
    /// the worker thread, while the UI it came from stays live.</summary>
    public static Task<string?> ApplyAsync(string? primary, IEnumerable<KeyChange> changes)
    {
        var snapshot = new List<KeyChange>(changes);
        return Task.Run(() => Apply(primary, snapshot));
    }

    /// <summary>Applies a patch. Vendors absent from <paramref name="changes"/>
    /// are left untouched, which is what lets the UI leave key fields blank and
    /// mean "unchanged" rather than "erase".</summary>
    /// <returns>null on success, otherwise the error text to show.</returns>
    public static string? Apply(string? primary, IEnumerable<KeyChange> changes)
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
            var (exitCode, _, stderr) = Run("settings apply", JsonSerializer.Serialize(patch));
            if (exitCode == 0) return null;

            return string.IsNullOrWhiteSpace(stderr)
                ? $"ai-usagebar settings apply exited with {exitCode}."
                : stderr.Trim();
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // -- Process plumbing ----------------------------------------------------

    private static (int ExitCode, string StdOut, string StdErr) Run(string arguments, string? stdin)
    {
        var psi = new ProcessStartInfo
        {
            FileName = CliBinary.Executable,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
            Environment = { ["NO_COLOR"] = "1" }
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("could not start ai-usagebar");

        if (stdin != null)
        {
            // UTF-8 without a BOM: a BOM would land at the head of the payload
            // and the JSON parser on the other side would reject it.
            using var writer = new System.IO.StreamWriter(
                process.StandardInput.BaseStream, new UTF8Encoding(false));
            writer.Write(stdin);
        }

        // Both pipes must be drained at the same time. Reading one to the end
        // before touching the other deadlocks the moment the CLI fills the
        // untouched pipe's buffer (roughly 4 KB: a TOML parse error with source
        // context, or a Rust backtrace, gets there): the child blocks writing,
        // so it never exits and never closes the stream being read. This used to
        // hang the whole UI thread with no way out but Task Manager, and the
        // timeout below could not save it, because it sat after the reads.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("ai-usagebar did not finish in time");
        }

        // The overload taking a timeout returns as soon as the process is gone,
        // without waiting for the async readers, so they are joined explicitly.
        // Bounded again: a grandchild holding the pipe open would otherwise
        // reintroduce the very hang this method just escaped.
        Task.WaitAll(new Task[] { stdoutTask, stderrTask }, (int)Timeout.TotalMilliseconds);

        return (process.ExitCode, Text(stdoutTask), Text(stderrTask));
    }

    /// <summary>Result of a reader that may not have finished, as empty text
    /// rather than an exception: a truncated stream is still worth reporting.</summary>
    private static string Text(Task<string> reader) =>
        reader.IsCompletedSuccessfully ? reader.Result : "";
}
