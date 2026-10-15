using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AiUsageBar.Services;

/// <summary>
/// The one place that launches the <c>ai-usagebar</c> CLI.
///
/// <para>The poller, the settings window and the version label each used to
/// carry their own copy of this, and the copies drifted: one read stdout to the
/// end before touching stderr (the deadlock that once froze the whole UI), one
/// checked its timeout only after both reads, one forgot to kill on timeout. A
/// single implementation is the fix that does not depend on remembering.</para>
///
/// <para>Everything runs on the thread pool, whatever thread the caller is on:
/// resolving the executable can extract it, and starting a process can block,
/// and the tray, the tooltip and both windows share the UI thread.</para>
/// </summary>
public static class CliProcess
{
    public sealed record Result(int ExitCode, string StdOut, string StdErr);

    /// <summary>How long to keep reading once the process has exited. A
    /// grandchild that inherited the pipes (a helper the CLI spawned) can hold
    /// them open long after the CLI is gone, and the output is complete by then
    /// anyway.</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Runs the CLI with <paramref name="arguments"/>, optionally feeding
    /// <paramref name="stdin"/>, and returns its exit code and both streams.</summary>
    /// <exception cref="Win32Exception">The executable could not be started.</exception>
    /// <exception cref="TimeoutException">It ran past <paramref name="timeout"/>
    /// and was killed, with its whole process tree.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was
    /// cancelled; the process was killed.</exception>
    public static Task<Result> RunAsync(string arguments, string? stdin, TimeSpan timeout, CancellationToken ct = default)
        => Task.Run(() => RunCoreAsync(arguments, stdin, timeout, ct), ct);

    private static async Task<Result> RunCoreAsync(string arguments, string? stdin, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = CliBinary.Executable,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            // The CLI emits UTF-8. Without this, .NET decodes the pipe using the
            // console code page (CP1252 here), so a middle dot arrives as "Â·"
            // and any accented text is mangled.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
            Environment = { ["NO_COLOR"] = "1" }
        };

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            // The resolved path may have vanished under us (an antivirus
            // quarantine, a cleaned profile). Forgetting it makes the next call
            // extract again instead of failing the same way until a restart.
            CliBinary.Invalidate();
            throw;
        }

        // Both pipes are drained at the same time, and before anything else.
        // Reading one to the end before touching the other deadlocks the moment
        // the CLI fills the untouched pipe's buffer (roughly 4 KB: a TOML parse
        // error with source context, or a Rust backtrace, gets there): the child
        // blocks writing, so it never exits and never closes the stream being read.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        // Kill on either cause, wherever the code is waiting at that moment.
        // Leaving the child alive outlives the app itself when the CLI is
        // blocked on a stalled provider request, and a CLI that never reads its
        // stdin would otherwise hold the write below forever: killing it breaks
        // the pipe, which is what releases that write.
        using var killOnCancel = linkedCts.Token.Register(() => Kill(process));

        try
        {
            if (stdin != null) WriteStdin(process, stdin);
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            Observe(stdoutTask);
            Observe(stderrTask);

            ct.ThrowIfCancellationRequested();
            throw new TimeoutException(
                $"ai-usagebar {arguments} did not finish within {timeout.TotalSeconds:0} seconds.");
        }
        catch (Exception)
        {
            // Whatever failed, the child must not outlive this call.
            Kill(process);
            Observe(stdoutTask);
            Observe(stderrTask);
            throw;
        }

        var both = Task.WhenAll(stdoutTask, stderrTask);
        using (var drainCts = new CancellationTokenSource())
        {
            await Task.WhenAny(both, Task.Delay(DrainTimeout, drainCts.Token)).ConfigureAwait(false);
            drainCts.Cancel();
        }
        Observe(both);

        return new Result(process.ExitCode, Text(stdoutTask), Text(stderrTask));
    }

    /// <summary>UTF-8 without a BOM: a BOM would land at the head of the payload
    /// and the JSON parser on the other side would reject it.</summary>
    private static void WriteStdin(Process process, string stdin)
    {
        try
        {
            using var writer = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false));
            writer.Write(stdin);
        }
        catch (IOException)
        {
            // The CLI exited before reading its input. Its exit code and stderr
            // say why, which is more useful than a broken pipe.
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone, or not ours to kill any more.
        }
    }

    /// <summary>Result of a reader that may not have finished, as empty text
    /// rather than an exception: a truncated stream is still worth reporting.</summary>
    private static string Text(Task<string> reader) =>
        reader.IsCompletedSuccessfully ? reader.Result : "";

    /// <summary>Marks a reader's failure as seen. Disposing the process tears
    /// the streams out from under pending reads, and an unobserved fault would
    /// otherwise surface later through UnobservedTaskException, which the app
    /// logs as an error.</summary>
    private static void Observe(Task task) =>
        task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
