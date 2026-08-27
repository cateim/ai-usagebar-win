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

    /// <summary>Raised on the UI thread after each poll completes.</summary>
    public event Action<Config, UsageJsonRoot>? Updated;

    public Poller(Dispatcher uiThread) => _ui = uiThread;

    public void Start() => _ = LoopAsync(_cts.Token);

    public void TriggerRefresh()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* a refresh is already pending */ }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var cfg = Config.Load();
            var jsonRoot = await FetchJsonAsync(ct).ConfigureAwait(false);

            if (jsonRoot != null)
            {
                _ui.BeginInvoke(() => Updated?.Invoke(cfg, jsonRoot));
            }

            try
            {
                await _wake.WaitAsync(cfg.PollInterval(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

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
                return JsonSerializer.Deserialize<UsageJsonRoot>(File.ReadAllText(fixture));
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
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                if (!process.HasExited) process.Kill();
                return ErrorRoot("Process timed out after 10 seconds.");
            }

            var output = await outputTask.ConfigureAwait(false);
            var stderr = await errorTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                return ErrorRoot($"Process exited with code {process.ExitCode}:\n{stderr.Trim()}");
            }

            if (string.IsNullOrWhiteSpace(output)) return ErrorRoot("Process returned empty output.");

            try
            {
                return JsonSerializer.Deserialize<UsageJsonRoot>(output);
            }
            catch (JsonException ex)
            {
                return ErrorRoot($"Failed to parse JSON:\n{ex.Message}\n\nOutput was:\n{output}");
            }
        }
        catch (Exception ex)
        {
            return ErrorRoot($"Unexpected error:\n{ex.Message}");
        }
    }

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
        _cts.Dispose();
        _wake.Dispose();
    }
}
