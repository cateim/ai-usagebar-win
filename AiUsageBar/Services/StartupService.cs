using System;
using Microsoft.Win32;

namespace AiUsageBar.Services;

/// <summary>Manages "start with Windows" for this unpackaged app via the per-user
/// Run key (<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>). Enabling
/// writes the current executable path; disabling removes the value. Per-user, so
/// it needs no administrator rights.</summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AiUsageBar";

    /// <summary>Path of the running executable. For a single-file published app
    /// this is the bundle .exe (the thing to relaunch), which is what we want.</summary>
    private static string ExePath => Environment.ProcessPath ?? "";

    /// <summary>True if a Run entry for this app exists.</summary>
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
    }

    /// <summary>Add or remove the Run entry. Best-effort: registry access failures
    /// are swallowed so a toggle never crashes the app.</summary>
    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return;

            if (enabled)
            {
                var path = ExePath;
                if (!string.IsNullOrEmpty(path))
                    key.SetValue(ValueName, $"\"{path}\""); // quote: path may contain spaces
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Best-effort: never let a startup-toggle failure take down the app.
        }
    }

    /// <summary>Rewrites the Run entry when the executable has moved.
    ///
    /// <para>The path is stamped in when the checkbox is ticked and was never
    /// revalidated. Someone who runs the download from their Downloads folder,
    /// ticks "start with Windows" and then files the .exe away somewhere tidy
    /// gets an entry pointing at a file that no longer exists. Windows fails it
    /// silently, so the app simply stops starting at logon, and the settings
    /// checkbox still shows as ticked, giving no hint that anything is wrong.
    /// The Start Menu shortcut already repairs itself this way on every
    /// launch.</para></summary>
    public static void RepairIfMoved()
    {
        try
        {
            var path = ExePath;
            if (string.IsNullOrEmpty(path)) return;

            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is not string current) return;

            var expected = $"\"{path}\"";
            if (string.Equals(current.Trim(), expected, StringComparison.OrdinalIgnoreCase)) return;

            key.SetValue(ValueName, expected);
        }
        catch
        {
            // Same contract as SetEnabled: never fail a launch over this.
        }
    }
}
