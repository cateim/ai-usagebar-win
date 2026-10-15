using System;
using System.IO;
using Tomlyn;
using Tomlyn.Model;

namespace AiUsageBar.Services;

/// <summary>
/// Settings for the tray app, deliberately split across two files.
///
/// <para><c>[ui] primary</c> belongs to the Rust CLI: the CLI reads it to pick
/// the vendor to query when no <c>--vendor</c> is passed, so it has to live in
/// the CLI's own config.toml.</para>
///
/// <para><c>poll_seconds</c> is ours alone and is written to a separate file.
/// The CLI rejects unknown top-level keys, so writing it into the CLI's config
/// made every `ai-usagebar` call fail with "unknown field `poll_seconds`",
/// which took the entire app down with a System Error.</para>
/// </summary>
public sealed class Config
{
    private const string PollKey = "poll_seconds";

    public long? PollSeconds { get; set; }
    public UiConfig Ui { get; set; } = new();

    /// <summary>True when the CLI's config still carries the legacy
    /// <c>poll_seconds</c>. That key makes the CLI refuse to parse the file at
    /// all, so it has to be removed, and removing it is the only reason this app
    /// ever rewrites that file itself.</summary>
    private bool _hasLegacyPollKey;

    public static Config Load()
    {
        var cfg = new Config();

        var cli = ReadTable(DefaultPath());
        if (cli != null)
        {
            if (cli.TryGetValue("ui", out var uiObj) && uiObj is TomlTable ui
                && ui.TryGetValue("primary", out var primary))
            {
                cfg.Ui.Primary = primary as string;
            }

            // Older builds wrote poll_seconds into the CLI's file. Read it so an
            // existing preference survives the move; Save() strips it from there.
            if (cli.TryGetValue(PollKey, out var legacy))
            {
                cfg._hasLegacyPollKey = true;
                if (legacy is long legacySeconds) cfg.PollSeconds = legacySeconds;
            }
        }

        // Our own file wins over the legacy location.
        var own = ReadTable(AppConfigPath());
        if (own != null && own.TryGetValue(PollKey, out var poll) && poll is long seconds)
        {
            cfg.PollSeconds = seconds;
        }

        return cfg;
    }

    public TimeSpan PollInterval() => TimeSpan.FromSeconds(Math.Max(PollSeconds ?? 60, 15));

    public void Save()
    {
        SaveOwnSettings();
        RepairLegacyCliConfig();
    }

    private void SaveOwnSettings()
    {
        var path = AppConfigPath() ?? throw new InvalidOperationException("could not resolve the app settings directory");

        // Starting from an empty table when this file is unreadable is safe: it
        // holds poll_seconds and nothing else. The CLI's file, below, is a very
        // different matter.
        var table = ReadTable(path) ?? new TomlTable();
        table[PollKey] = Math.Max(PollSeconds ?? 60, 15);
        Write(path, table);
    }

    /// <summary>Removes the legacy <c>poll_seconds</c> from the CLI's config,
    /// and nothing else.
    ///
    /// <para>Older builds wrote that key there. The CLI denies unknown top-level
    /// fields, so its presence makes every invocation fail to parse the file and
    /// the app shows nothing but a System Error. Removing it is the repair.</para>
    ///
    /// <para>It runs only when the key is actually present, and this is the only
    /// code that writes that file. <c>[ui] primary</c> deliberately goes through
    /// <see cref="CliSettings.ApplyAsync"/> instead: the CLI writes with toml_edit and
    /// keeps comments, while a Tomlyn round trip drops every one of them (the
    /// model API has no representation for a comment) and would rewrite a file
    /// full of the user's API keys on every single Save.</para></summary>
    private void RepairLegacyCliConfig()
    {
        if (!_hasLegacyPollKey) return;

        var path = DefaultPath();
        if (path == null) return;

        // Refuse to write over a file that exists but could not be read. The old
        // code fell back to an empty table here, so one unreadable moment (a
        // hand-edited typo, an antivirus holding the file open) replaced the
        // user's entire config, API keys included, with two lines of [ui].
        if (!TryReadTable(path, out var table) || table == null) return;

        if (!table.Remove(PollKey)) return;

        Write(path, table);
        _hasLegacyPollKey = false;
    }

    private static TomlTable? ReadTable(string? path)
        => path != null && TryReadTable(path, out var table) ? table : null;

    /// <summary>Distinguishes "no file yet" from "there is a file and I could
    /// not read it". Both used to come back as null, and conflating them is what
    /// turned a transient read failure into data loss.</summary>
    /// <returns>False only when the file exists but could not be read or parsed.
    /// A missing file returns true with a null table.</returns>
    private static bool TryReadTable(string path, out TomlTable? table)
    {
        table = null;
        if (!File.Exists(path)) return true;

        try
        {
            table = Toml.ToModel(File.ReadAllText(path));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Writes through a temporary file in the same directory.
    ///
    /// <para>A plain write truncates first, so losing power or being killed in
    /// that window leaves the file empty. For the CLI's config that means every
    /// stored API key is gone, with no backup. The rename is what makes a reader
    /// see either the old file or the new one, never a half of either.</para></summary>
    private static void Write(string path, TomlTable table)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var text = Toml.FromModel(table);
        var temp = path + ".tmp";

        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(text);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        // File.Move handles both cases; Replace would throw when there is no
        // original to replace, which is the first save on a clean machine.
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The Rust CLI's config file. Owned by the CLI; we touch only
    /// <c>[ui] primary</c>. This is what the "Open config.toml" button opens.</summary>
    public static string? DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrEmpty(appData) ? null : Path.Combine(appData, "ai-usagebar", "config", "config.toml");
    }

    /// <summary>This app's own settings file. The CLI never reads it, so keys
    /// here can never break the CLI's parser.</summary>
    public static string? AppConfigPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrEmpty(appData) ? null : Path.Combine(appData, "ai-usagebar-win", "settings.toml");
    }

    public string PrimaryStr() => string.IsNullOrEmpty(Ui.Primary) ? "anthropic" : Ui.Primary;
}

public sealed class UiConfig
{
    public string? Primary { get; set; }
}
