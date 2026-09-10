using System;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AiUsageBar.Services;

/// <summary>
/// The two version numbers that identify a running install: this app's, and the
/// <c>ai-usagebar</c> CLI actually being executed.
///
/// <para>Both are worth showing because they move independently. Releases do not
/// pin the CLI, so two users on the same app version can be running different
/// backends, and a bug report that names only one of the two cannot be
/// reproduced. The settings window prints both.</para>
/// </summary>
public static class VersionInfo
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static Task<string>? _cliVersion;

    /// <summary>CalVer string as written in the .csproj, without the trailing
    /// revision .NET appends to <c>AssemblyVersion</c>.</summary>
    public static string App { get; } = ReadAppVersion();

    /// <summary>Version reported by the CLI, or null when it cannot be run.
    ///
    /// <para>Started once per process and cached: the number cannot change while
    /// the app is running, and reopening the settings window should not pay for
    /// another process launch.</para></summary>
    public static Task<string> CliAsync() => _cliVersion ??= Task.Run(ReadCliVersion);

    private static string ReadAppVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();

        // InformationalVersion carries the literal <Version> string. It can also
        // carry a "+<commit>" suffix when source link is on, which is noise here.
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        var version = assembly.GetName().Version;
        return version == null
            ? "unknown"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    /// <summary>Runs <c>ai-usagebar --version</c>. Returns "unavailable" rather
    /// than throwing: a missing CLI is already reported as a System Error card,
    /// and this label must never be the thing that takes the window down.</summary>
    private static string ReadCliVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = CliBinary.Executable,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
                Environment = { ["NO_COLOR"] = "1" }
            };

            using var process = Process.Start(psi);
            if (process == null) return "unavailable";

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();

            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return "unavailable";
            }

            // The CLI prints "ai-usagebar 1.14.0". Matching the number rather
            // than splitting on whitespace keeps this working if upstream ever
            // decorates the line.
            var text = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            var match = Regex.Match(text, @"\d+\.\d+\.\d+");
            return match.Success ? match.Value : "unavailable";
        }
        catch (Exception)
        {
            return "unavailable";
        }
    }
}
