using System;
using System.IO;
using System.Reflection;
using System.Threading;

namespace AiUsageBar.Services;

/// <summary>
/// Decides which <c>ai-usagebar</c> executable to run.
///
/// <para>Release builds embed the Rust CLI as a resource (see the conditional
/// <c>EmbeddedResource</c> in the .csproj), so a fresh install works without
/// <c>cargo install</c>. It is extracted once to LocalAppData and re-extracted
/// whenever the app version changes.</para>
///
/// <para>The bundled copy deliberately wins over anything on PATH: everyone then
/// runs the exact CLI build this release was tested against, instead of whatever
/// version each machine happens to have.</para>
///
/// <para>Local builds carry no resource and fall back to PATH, so development
/// keeps working against a locally installed CLI.</para>
/// </summary>
public static class CliBinary
{
    private const string ResourceName = "ai-usagebar.exe";

    /// <summary>Used when nothing is bundled: resolved through PATH by Windows.</summary>
    private const string FallbackCommand = "ai-usagebar";

    private static readonly object Gate = new();

    /// <summary>Cached only on success. A failed extraction is deliberately not
    /// remembered.</summary>
    private static string? _resolved;

    /// <summary>What to hand to <c>ProcessStartInfo.FileName</c>.
    ///
    /// <para>A successful resolution is cached for the process: neither the
    /// embedded resource nor PATH changes while running. A failure is not, and
    /// that distinction matters. Extraction competes with the virus scanner over
    /// a freshly written executable, so it can fail once and succeed a second
    /// later; caching the PATH fallback meant one unlucky moment at logon left a
    /// clean machine with no CLI at all, showing a System Error on every poll
    /// until the user thought to restart the app, with the correct binary
    /// sitting right there on disk. The poller retries every minute anyway, so
    /// retrying here costs nothing and repairs itself.</para></summary>
    public static string Executable
    {
        get
        {
            // Locked because the poller thread and the settings window can both
            // land here at once on first use, and two concurrent extractions
            // fighting over the same temp file was itself a source of failure.
            lock (Gate)
            {
                if (_resolved != null) return _resolved;

                try
                {
                    var extracted = EnsureExtracted();
                    if (extracted != null)
                    {
                        _resolved = extracted;
                        return extracted;
                    }

                    // Nothing bundled: this build genuinely runs off PATH, which
                    // is a permanent answer rather than a failure.
                    _resolved = FallbackCommand;
                    return FallbackCommand;
                }
                catch (Exception)
                {
                    // Extraction is best-effort. A locked file, a full disk or a
                    // locked down profile degrades to "use whatever is on PATH"
                    // for this call only. Poller reports the failure if PATH has
                    // nothing either, and the next poll tries again.
                    return FallbackCommand;
                }
            }
        }
    }

    /// <summary>Forgets the resolved path after it failed to start.
    ///
    /// <para>A successful resolution was cached for the life of the process, so
    /// once the extracted copy disappeared (an antivirus quarantine, someone
    /// cleaning <c>%LOCALAPPDATA%</c>) every poll failed with the same System
    /// Error until the app was restarted. The next call now extracts again.</para></summary>
    public static void Invalidate()
    {
        lock (Gate) _resolved = null;
    }

    /// <summary>Returns the extracted path, or null when no CLI is bundled.</summary>
    private static string? EnsureExtracted()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var resource = assembly.GetManifestResourceStream(ResourceName);
        if (resource == null) return null;

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ai-usagebar-win",
            "bin");
        Directory.CreateDirectory(dir);

        var exePath = Path.Combine(dir, ResourceName);
        var stampPath = exePath + ".version";

        // The stamp records the size as well as the version. Version alone was
        // not enough: an antivirus that truncates the file in place, or a power
        // loss that lands the rename without the data, leaves a matching stamp
        // over a zero-byte executable, and the fast path then skips extraction
        // forever. Reinstalling the same version would not fix it either.
        var stamp = $"{assembly.GetName().Version?.ToString() ?? "unknown"} {resource.Length}";

        if (File.Exists(exePath)
            && ReadStamp(stampPath) == stamp
            && new FileInfo(exePath).Length == resource.Length)
        {
            return exePath;
        }

        // Write to a temp file and move it into place, so an interrupted run
        // never leaves a half-written executable that would fail cryptically.
        // The name carries the process id: two instances starting together would
        // otherwise collide on it, and the loser's IOException used to be read
        // as "there is no bundled CLI".
        var tempPath = $"{exePath}.{Environment.ProcessId}.tmp";
        try
        {
            using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                resource.CopyTo(file);
                file.Flush(flushToDisk: true);
            }

            MoveWithRetry(tempPath, exePath);
            File.WriteAllText(stampPath, stamp);
        }
        finally
        {
            // A failed attempt must not leave megabytes behind on every retry.
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }

        return exePath;
    }

    /// <summary>Moves into place, retrying briefly.
    ///
    /// <para>Windows Defender opens a newly written executable to scan it, which
    /// makes the rename fail with a sharing violation for a moment. That moment
    /// lands exactly at logon, when the app starts from the Run key, which is
    /// the one launch where failing means the user never sees the CLI work.</para></summary>
    private static void MoveWithRetry(string from, string to)
    {
        const int attempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(from, to, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < attempts)
            {
                Thread.Sleep(150 * attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < attempts)
            {
                Thread.Sleep(150 * attempt);
            }
        }
    }

    private static string? ReadStamp(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
