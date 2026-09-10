using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using AiUsageBar.Models;

namespace AiUsageBar.Services;

/// <summary>Background polling loop that executes `ai-usagebar usage --json`.</summary>
public sealed class Poller : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _cts = new();

    /// <summary>The running loop, so Dispose can wait for it.</summary>
    private Task? _loop;

    /// <summary>Raised on the UI thread after each poll completes.</summary>
    public event Action<Config, UsageJsonRoot>? Updated;

    public Poller(Dispatcher uiThread) => _ui = uiThread;

    /// <summary>Starts the loop on a worker thread.
    ///
    /// <para>The <c>Task.Run</c> is not decoration. An async method runs
    /// synchronously on the caller until its first await, and the first real
    /// await here is waiting for the CLI to exit. Called straight from
    /// <c>OnStartup</c>, that put a config read, the one-off extraction of the
    /// bundled CLI (megabytes, plus whatever the virus scanner wants) and a
    /// process launch on the UI thread before WPF ever pumped a message. The
    /// tray icon appeared and then ignored clicks for the whole window.</para></summary>
    public void Start() => _loop = Task.Run(() => LoopAsync(_cts.Token));

    public void TriggerRefresh()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* a refresh is already pending */ }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var consecutiveFailures = 0;

        while (!ct.IsCancellationRequested)
        {
            var cfg = Config.Load();
            var jsonRoot = await FetchJsonAsync(ct).ConfigureAwait(false);

            if (ct.IsCancellationRequested) break;

            if (jsonRoot != null)
            {
                consecutiveFailures = IsSystemError(jsonRoot) ? consecutiveFailures + 1 : 0;
                Publish(cfg, jsonRoot);
            }

            // A failure retries sooner than the configured interval. The app is
            // usually launched from the Run key, so the first poll of a session
            // races the network stack coming up: waiting the full minute after
            // that leaves a wrong tray icon on screen for a minute, every login.
            var wait = consecutiveFailures > 0
                ? RetryDelay(consecutiveFailures, cfg.PollInterval())
                : cfg.PollInterval();

            try
            {
                await _wake.WaitAsync(wait, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Marshals one update, absorbing anything the render throws.
    ///
    /// <para>An exception inside a dispatcher callback is rethrown on the UI
    /// thread, where nothing catches it, so a single unexpected shape in the
    /// CLI's output would take the whole tray app down with it. Degrading to an
    /// error card keeps the app alive and says what happened.</para></summary>
    private void Publish(Config cfg, UsageJsonRoot root)
    {
        _ui.BeginInvoke(() =>
        {
            try
            {
                Updated?.Invoke(cfg, root);
            }
            catch (Exception ex)
            {
                try { Updated?.Invoke(cfg, ErrorRoot("Could not render the CLI output:\n" + ex.Message)); }
                catch { }
            }
        });
    }

    /// <summary>5s, 10s, 20s, 40s, then the configured interval. Never longer
    /// than the interval itself, which would make a failure poll less often than
    /// success.</summary>
    private static TimeSpan RetryDelay(int failures, TimeSpan interval)
    {
        var seconds = 5 * Math.Pow(2, Math.Min(failures - 1, 3));
        var backoff = TimeSpan.FromSeconds(seconds);
        return backoff < interval ? backoff : interval;
    }

    /// <summary>Whether this root is one of our synthetic error reports rather
    /// than something the CLI produced.</summary>
    private static bool IsSystemError(UsageJsonRoot root) =>
        root.Entries is { Count: 1 } && root.Entries[0].Id == UsageJsonEntry.SystemId;

    /// <summary>Environment variable pointing at a JSON file to render instead of
    /// running the CLI. Used to drive the app through states that are hard to
    /// reach on demand (many providers, a critical quota, stale data) for
    /// screenshots and manual UI checks. Opt-in and absent in normal use.</summary>
    private const string FixtureVariable = "AIUSAGEBAR_WIN_FIXTURE";

    private static async Task<UsageJsonRoot?> FetchJsonAsync(CancellationToken ct)
    {
        var fixture = Environment.GetEnvironmentVariable(FixtureVariable);
        if (!string.IsNullOrWhiteSpace(fixture))
        {
            try
            {
                return JsonSerializer.Deserialize<UsageJsonRoot>(File.ReadAllText(fixture))
                    ?? ErrorRoot(FixtureVariable + " at " + fixture + " parsed to null.");
            }
            catch (Exception ex)
            {
                return ErrorRoot("Could not read " + FixtureVariable + " at " + fixture + ": " + ex.Message);
            }
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = CliBinary.Executable,
                Arguments = "usage --json",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // The CLI emits UTF-8. Without this, .NET decodes the pipe using
                // the console code page (CP1252 here), so a middle dot arrives as
                // "Â·" and any accented text is mangled.
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
                Environment = { ["NO_COLOR"] = "1" }
            };

            using var process = new Process { StartInfo = psi };

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                process.Start();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return ErrorRoot(
                    $"Could not start the ai-usagebar CLI ({CliBinary.Executable}).\n" +
                    "Released builds ship it bundled, so this usually means the extracted copy " +
                    "was removed or blocked. Reinstall the app, or install the CLI yourself with " +
                    "'cargo install ai-usagebar'.");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(linkedCts.Token);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Kill on either cause. Leaving the child alive outlives the app
                // itself when the CLI is blocked on a stalled provider request.
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch { }

                // Shutdown, not a timeout: the app is going away, so there is
                // nobody left to show an error to. Returning null skips the
                // update instead of marshalling a bogus card onto a dispatcher
                // that may already be torn down.
                if (ct.IsCancellationRequested) return null;

                return ErrorRoot("Process timed out after 10 seconds.");
            }

            var output = await ReadOrEmpty(outputTask).ConfigureAwait(false);
            var stderr = await ReadOrEmpty(errorTask).ConfigureAwait(false);

            // Parse first, judge the exit code second. The CLI exits 1 when every
            // vendor failed (report_exit_code: "all entries have an error"), and
            // that is precisely the fresh install with nothing signed in. It
            // still prints a complete document naming what is wrong with each
            // vendor, so treating a non-zero exit as "no data" threw away the
            // only diagnosis the user had and replaced it with an empty
            // "Process exited with code 1:" card. It also made the first-run
            // setup guidance unreachable, since that path needs the per-vendor
            // entries to exist.
            if (!string.IsNullOrWhiteSpace(output))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<UsageJsonRoot>(output);
                    if (parsed != null) return parsed;
                }
                catch (JsonException ex)
                {
                    // Only report the parse failure when the exit code does not
                    // already explain it: a crashed CLI writes a diagnostic to
                    // stderr, which is more useful than a JSON error about it.
                    if (process.ExitCode == 0)
                    {
                        return ErrorRoot($"Failed to parse JSON:\n{ex.Message}\n\nOutput was:\n{Clamp(output)}");
                    }
                }
            }

            if (process.ExitCode != 0)
            {
                var detail = stderr.Trim();
                return ErrorRoot(detail.Length == 0
                    ? $"Process exited with code {process.ExitCode} without explaining why."
                    : $"Process exited with code {process.ExitCode}:\n{Clamp(detail)}");
            }

            return ErrorRoot(string.IsNullOrWhiteSpace(output)
                ? "Process returned empty output."
                : "Process returned a JSON null.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            return ErrorRoot($"Unexpected error:\n{ex.Message}");
        }
    }

    /// <summary>Awaits a stream reader that may have been abandoned by a kill.
    ///
    /// <para>Disposing the process tears the readers' streams out from under
    /// them, faulting both tasks. Left unobserved those exceptions are collected
    /// at GC, which is harmless today only because nothing subscribes to
    /// <c>TaskScheduler.UnobservedTaskException</c>. Observing them here removes
    /// the trap instead of relying on that.</para></summary>
    private static async Task<string> ReadOrEmpty(Task<string> reader)
    {
        try
        {
            return await reader.ConfigureAwait(false);
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>Caps text that ends up inside an error card. The popup renders
    /// it verbatim, and CLI output is unbounded: a captive portal answering with
    /// an HTML page turns the card into a wall of markup.</summary>
    private static string Clamp(string text, int max = 400) =>
        text.Length <= max ? text : text[..max] + "\n[truncated]";

    private static UsageJsonRoot ErrorRoot(string message)
    {
        return new UsageJsonRoot
        {
            Entries = new System.Collections.Generic.List<UsageJsonEntry>
            {
                new()
                {
                    Id = UsageJsonEntry.SystemId,
                    DisplayName = "System Error",
                    Status = "error",
                    Error = message
                }
            }
        };
    }

    public void Dispose()
    {
        _cts.Cancel();

        // Wait for the loop before tearing down what it is still holding.
        // Cancelling only schedules its continuation, so without this the loop
        // could wake up after the semaphore was disposed and throw on it.
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); }
        catch (Exception) { /* faulted or timed out: shutting down either way */ }

        Updated = null;
        _cts.Dispose();
        _wake.Dispose();
    }
}
