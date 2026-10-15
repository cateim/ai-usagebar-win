using System;
using System.Reflection;
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
    private const string Unavailable = "unavailable";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>The CLI's version and the path it runs from.</summary>
    public sealed record CliInfo(string Version, string Path);

    private static Task<CliInfo>? _cli;

    /// <summary>CalVer string as written in the .csproj, without the trailing
    /// revision .NET appends to <c>AssemblyVersion</c>.</summary>
    public static string App { get; } = ReadAppVersion();

    /// <summary>Version and path of the CLI being run.
    ///
    /// <para>A successful read is cached for the process: the number cannot
    /// change while the app is running, and reopening the settings window should
    /// not pay for another process launch. A failed one is not, so a CLI that
    /// was briefly unavailable does not stay "unavailable" until a restart.
    /// Called from the UI thread only.</para></summary>
    public static async Task<CliInfo> CliAsync()
    {
        var task = _cli ??= ReadCliAsync();
        var info = await task;
        if (info.Version == Unavailable && ReferenceEquals(_cli, task)) _cli = null;
        return info;
    }

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

    /// <summary>Runs <c>ai-usagebar --version</c>. Never throws: a missing CLI is
    /// already reported as a System Error card, and this label must never be
    /// the thing that takes the window down.
    ///
    /// <para>The path is resolved off the UI thread as well. Resolving it can
    /// mean extracting the bundled CLI, or waiting on the poller doing exactly
    /// that, and the settings window used to do it inline while opening.</para></summary>
    private static async Task<CliInfo> ReadCliAsync()
    {
        var path = "ai-usagebar";
        try
        {
            path = await Task.Run(() => CliBinary.Executable).ConfigureAwait(false);
            var result = await CliProcess.RunAsync("--version", null, Timeout).ConfigureAwait(false);

            // The CLI prints "ai-usagebar 1.14.0". Matching the number rather
            // than splitting on whitespace keeps this working if upstream ever
            // decorates the line.
            var text = string.IsNullOrWhiteSpace(result.StdOut) ? result.StdErr : result.StdOut;
            var match = Regex.Match(text, @"\d+\.\d+\.\d+");
            return new CliInfo(match.Success ? match.Value : Unavailable, path);
        }
        catch (Exception)
        {
            return new CliInfo(Unavailable, path);
        }
    }
}
