using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AiUsageBar.Services;

/// <summary>
/// A small local log for the failures nobody is watching when they happen.
///
/// <para>A tray app has no console and no window to print into, so a crash used
/// to leave nothing behind but a Windows Error Reporting entry with no stack
/// trace. That is exactly what the September 2026 crash looked like: an
/// unhandled exception after four days of uptime, and no way to tell what threw.
/// Everything that reaches a last-resort handler, and every poll that fails, is
/// written here instead.</para>
///
/// <para>The file lives at <c>%LOCALAPPDATA%\ai-usagebar-win\logs\app.log</c>,
/// is capped at <see cref="MaxBytes"/> with one rotated predecessor, and never
/// throws: logging must not become a second way to crash.</para>
/// </summary>
public static class AppLog
{
    private const long MaxBytes = 256 * 1024;

    private static readonly object Gate = new();

    /// <summary>Last message written by <see cref="WarnOnce"/>, so a CLI that
    /// fails the same way on every poll fills one line, not the whole file.</summary>
    private static string? _lastWarning;

    public static string? Directory
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrEmpty(local) ? null : Path.Combine(local, "ai-usagebar-win", "logs");
        }
    }

    public static void Error(string context, Exception ex) =>
        Write("ERROR", context + ": " + ex);

    public static void Warn(string message) => Write("WARN", message);

    /// <summary>Writes the message unless it repeats the previous warning.</summary>
    public static void WarnOnce(string message)
    {
        lock (Gate)
        {
            if (message == _lastWarning) return;
            _lastWarning = message;
        }
        Write("WARN", message);
    }

    /// <summary>Forgets the last warning, so the next failure is logged even if
    /// it matches one from before a successful poll.</summary>
    public static void ResetWarnings()
    {
        lock (Gate) _lastWarning = null;
    }

    /// <summary>Quoted values after a secret-looking key (<c>api_key = "..."</c>,
    /// <c>"token": "..."</c>) and bearer tokens.</summary>
    private static readonly Regex Secret = new(
        @"(?i)((?:key|token|secret|password|authorization)[A-Za-z_]*[""']?\s*[:=]\s*[""']?)(?:Bearer\s+)?[^""'\s,}]+",
        RegexOptions.Compiled);

    /// <summary>Masks anything shaped like a credential before it reaches disk.
    ///
    /// <para>Callers already log only what they need, but the text often comes
    /// from the CLI, and a TOML parse error quotes the offending line of
    /// config.toml, which can be the one holding an API key. Keys must never be
    /// logged (see CLAUDE.md), so this is the backstop, not the policy.</para></summary>
    public static string Scrub(string text) => Secret.Replace(text, "$1***");

    private static void Write(string level, string message)
    {
        try
        {
            var dir = Directory;
            if (dir == null) return;

            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "app.log");

                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(path, path + ".1", overwrite: true);
                }

                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {level} [{VersionInfo.App}] {Scrub(message)}{Environment.NewLine}";
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch
        {
            // A full disk or a locked file is not worth a crash.
        }
    }
}
