using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using AiUsageBar.Models;

namespace AiUsageBar.Services;

/// <summary>Background polling loop that executes `ai-usagebar usage --json`.</summary>
public sealed class Poller : IDisposable
{
    /// <summary>How long one run of the CLI may take before it is killed.
    ///
    /// <para>It was 10 seconds, shorter than the CLI's own HTTP timeout (30
    /// seconds per request, vendors fetched one after another). A slow provider
    /// or an OAuth token refresh therefore got the CLI killed mid-request, which
    /// both threw the whole report away and risked interrupting a refresh that
    /// rotates the stored token. This is a hang guard, not a latency budget: the
    /// poll runs off the UI thread, and the previous report stays on screen
    /// while it waits.</para></summary>
    private static readonly TimeSpan CliTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long the last good report keeps standing in for failed
    /// refreshes. Past this, the failure itself is shown: data that old would
    /// mislead, and whatever broke is clearly not transient.</summary>
    private static readonly TimeSpan LastGoodLimit = TimeSpan.FromMinutes(15);

    private readonly Dispatcher _ui;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _cts = new();

    /// <summary>The running loop, so Dispose can wait for it.</summary>
    private Task? _loop;

    /// <summary>The newest report the CLI actually produced. Touched only by the
    /// loop, which runs one iteration at a time.</summary>
    private UsageJsonRoot? _lastGood;

    /// <summary>Raised on the UI thread after each poll completes.</summary>
    public event Action<Config, UsageJsonRoot>? Updated;

    /// <summary>Raised on the UI thread when a poll starts (true) and ends
    /// (false), so the popup can say "Updating" instead of looking frozen.</summary>
    public event Action<bool>? BusyChanged;

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
        catch (ObjectDisposedException) { /* quitting */ }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var consecutiveFailures = 0;

        while (!ct.IsCancellationRequested)
        {
            var cfg = new Config();

            // One bad iteration must not end the loop. This runs under Task.Run,
            // so an exception escaping here would fault the task quietly and
            // polling would stop for good: a tray frozen on old numbers with
            // nothing on screen to say why, which is the symptom this whole
            // class exists to prevent.
            try
            {
                cfg = Config.Load();

                RaiseBusy(true);
                var jsonRoot = await FetchJsonAsync(ct).ConfigureAwait(false);

                if (ct.IsCancellationRequested) break;

                if (jsonRoot != null)
                {
                    var failed = IsSystemError(jsonRoot);
                    consecutiveFailures = failed ? consecutiveFailures + 1 : 0;

                    if (failed)
                    {
                        // The first line only. What follows it can be CLI output,
                        // and a TOML parse error quotes the config line it choked
                        // on, which may be the one holding an API key.
                        AppLog.WarnOnce("poll failed: " + FirstLine(jsonRoot.Entries[0].Error));
                        jsonRoot = WithLastGood(jsonRoot);
                    }
                    else
                    {
                        AppLog.ResetWarnings();
                        _lastGood = jsonRoot;
                    }

                    Publish(cfg, jsonRoot);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                consecutiveFailures++;
                AppLog.Error("poll loop", ex);
                Publish(cfg, ErrorRoot("Unexpected error while polling:\n" + ex.Message));
            }
            finally
            {
                RaiseBusy(false);
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

    /// <summary>Keeps the last good report on screen through a transient
    /// failure, flagged with why the refresh failed.
    ///
    /// <para>A single timed-out poll used to replace every vendor's numbers with
    /// one "System Error" card and turn the tray grey, for a failure that the
    /// next poll usually cleared on its own. Numbers a few minutes old, clearly
    /// marked as such, are more useful than no numbers.</para></summary>
    private UsageJsonRoot WithLastGood(UsageJsonRoot failure)
    {
        var good = _lastGood;
        if (good?.ReceivedAt is not { } receivedAt) return failure;
        if (DateTimeOffset.Now - receivedAt > LastGoodLimit) return failure;

        return new UsageJsonRoot
        {
            SchemaVersion = good.SchemaVersion,
            Primary = good.Primary,
            // A copy: the UI thread may still be reading the list it was handed
            // with the previous update.
            Entries = new System.Collections.Generic.List<UsageJsonEntry>(good.Entries ?? new()),
            ReceivedAt = receivedAt,
            RefreshError = FirstLine(failure.Entries[0].Error),
        };
    }

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var trimmed = text.Trim();
        var newline = trimmed.IndexOf('\n');
        return newline < 0 ? trimmed : trimmed[..newline].TrimEnd(':', ' ', '\r');
    }

    /// <summary>Marshals one update, absorbing anything the render throws.
    ///
    /// <para>An exception inside a dispatcher callback is rethrown on the UI
    /// thread, where nothing catches it, so a single unexpected shape in the
    /// CLI's output would take the whole tray app down with it. Degrading to an
    /// error card keeps the app alive and says what happened.</para></summary>
    private void Publish(Config cfg, UsageJsonRoot root)
    {
        try
        {
            _ui.BeginInvoke(() =>
            {
                try
                {
                    Updated?.Invoke(cfg, root);
                }
                catch (Exception ex)
                {
                    AppLog.Error("render", ex);
                    try { Updated?.Invoke(cfg, ErrorRoot("Could not render the CLI output:\n" + ex.Message)); }
                    catch { }
                }
            });
        }
        catch (Exception)
        {
            // The dispatcher is shutting down: nobody is left to show it to.
        }
    }

    private void RaiseBusy(bool busy)
    {
        try
        {
            _ui.BeginInvoke(() =>
            {
                try { BusyChanged?.Invoke(busy); }
                catch (Exception ex) { AppLog.Error("busy indicator", ex); }
            });
        }
        catch (Exception)
        {
            // Same as Publish: shutting down.
        }
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
                var root = JsonSerializer.Deserialize<UsageJsonRoot>(File.ReadAllText(fixture));
                if (root == null) return ErrorRoot(FixtureVariable + " at " + fixture + " parsed to null.");
                root.ReceivedAt = DateTimeOffset.Now;
                return root;
            }
            catch (Exception ex)
            {
                return ErrorRoot("Could not read " + FixtureVariable + " at " + fixture + ": " + ex.Message);
            }
        }

        CliProcess.Result result;
        try
        {
            result = await CliProcess.RunAsync("usage --json", null, CliTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown, not a failure: the app is going away, so there is nobody
            // left to show an error to. Returning null skips the update instead
            // of marshalling a bogus card onto a dispatcher that may already be
            // torn down.
            return null;
        }
        catch (TimeoutException)
        {
            return ErrorRoot($"The CLI did not answer within {CliTimeout.TotalSeconds:0} seconds.");
        }
        catch (Win32Exception)
        {
            return ErrorRoot(
                $"Could not start the ai-usagebar CLI ({CliBinary.Executable}).\n" +
                "Released builds ship it bundled, so this usually means the extracted copy " +
                "was removed or blocked. Reinstall the app, or install the CLI yourself with " +
                "'cargo install ai-usagebar'.");
        }
        catch (Exception ex)
        {
            return ErrorRoot($"Unexpected error:\n{ex.Message}");
        }

        var output = result.StdOut;

        // Parse first, judge the exit code second. Before 1.20.2 the CLI exited
        // 1 when every vendor failed, which is precisely the fresh install with
        // nothing signed in, while still printing a complete document naming
        // what is wrong with each vendor. Treating a non-zero exit as "no data"
        // threw away the only diagnosis the user had and replaced it with an
        // empty "Process exited with code 1:" card. It also made the first-run
        // setup guidance unreachable, since that path needs the per-vendor
        // entries to exist.
        if (!string.IsNullOrWhiteSpace(output))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<UsageJsonRoot>(output);
                if (parsed != null)
                {
                    parsed.ReceivedAt = DateTimeOffset.Now;
                    return parsed;
                }
            }
            catch (JsonException ex)
            {
                // Only report the parse failure when the exit code does not
                // already explain it: a crashed CLI writes a diagnostic to
                // stderr, which is more useful than a JSON error about it.
                if (result.ExitCode == 0)
                {
                    return ErrorRoot($"Failed to parse JSON:\n{ex.Message}\n\nOutput was:\n{Clamp(output)}");
                }
            }
        }

        if (result.ExitCode != 0)
        {
            var detail = result.StdErr.Trim();
            return ErrorRoot(detail.Length == 0
                ? $"Process exited with code {result.ExitCode} without explaining why."
                : $"Process exited with code {result.ExitCode}:\n{Clamp(detail)}");
        }

        return ErrorRoot(string.IsNullOrWhiteSpace(output)
            ? "Process returned empty output."
            : "Process returned a JSON null.");
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
        BusyChanged = null;
        _cts.Dispose();
        _wake.Dispose();
    }
}
