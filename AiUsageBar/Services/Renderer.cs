using System;
using System.Collections.Generic;
using System.Linq;
using AiUsageBar.Models;

namespace AiUsageBar.Services;

public static class Renderer
{
    /// <summary>What the tray needs: the colour (Severity), the hover text, and
    /// how full the ring should be. Percent is null when nothing measurable was
    /// reported, which draws an empty ring rather than a misleading full one.</summary>
    public sealed record Rendered(Severity Severity, string Tooltip, int? Percent);

    public static Rendered Render(UsageJsonRoot root, Config cfg, DateTimeOffset now)
    {
        var severities = new List<Severity>();
        var tipLines = new List<string>();

        var primaryId = PrimaryId(root, cfg);

        foreach (var entry in Ordered(root.Entries, root.Primary))
        {
            if (!ShouldShow(entry, primaryId)) continue;

            severities.Add(GetWorstSeverity(entry));

            // Build tooltip line
            var tag = entry.Id.Substring(0, Math.Min(3, entry.Id.Length));
            if (entry.Id == "anthropic") tag = "cld";
            else if (entry.Id == "openai") tag = "gpt";
            else if (entry.Id == "openrouter") tag = "or";
            else if (entry.Id == "deepseek") tag = "ds";
            else if (entry.Id == "antigravity") tag = "agy";
            else if (entry.Id == "moonshot") tag = "moo";
            else if (entry.Id == "supergrok") tag = "sgk";

            if (entry.Status != "ready")
            {
                tipLines.Add($"{tag}: {entry.Status}");
                continue;
            }

            // Find the most critical metric
            var worstMetric = entry.Metrics.OrderByDescending(m => SeverityRules.Parse(m.Severity ?? "")).FirstOrDefault();
            if (worstMetric != null)
            {
                tipLines.Add($"{tag} {worstMetric.Value} · {worstMetric.Label}");
            }
            else
            {
                // Fallback for providers with facts/balance only
                var fact = entry.Sections.FirstOrDefault(s => s.Type == "fact");
                if (fact != null && !string.IsNullOrEmpty(fact.Text))
                {
                    tipLines.Add($"{tag} {fact.Text}");
                }
                else
                {
                    tipLines.Add($"{tag} ready");
                }
            }
        }

        // Grey means "no idea", and that is the honest reading in three cases:
        // nothing to show, a severity string we could not parse (which would
        // otherwise be masked by a healthy sibling, since Unknown sorts below
        // Low), and nothing readable at all. That last one covers a fresh
        // install with no credentials and a total network outage alike: neither
        // is a blown quota, so neither should paint the tray red.
        var worstSeverity = severities.Count == 0
            || severities.Contains(Severity.Unknown)
            || !HasUsableData(root)
            ? Severity.Unknown
            : severities.Max();

        var tooltip = tipLines.Count == 0
            ? "ai-usagebar - no models configured"
            : string.Join("\n", tipLines);

        return new Rendered(worstSeverity, tooltip, WorstPercent(root, primaryId));
    }

    private static IEnumerable<UsageJsonEntry> Ordered(List<UsageJsonEntry> entries, string? primaryId)
        => entries.OrderBy(r => r.Id != primaryId).ThenBy(r => r.Id);

    /// <summary>The vendor the UI revolves around: what the CLI reported, or the
    /// local config when the CLI could not say (a synthetic error root).</summary>
    private static string PrimaryId(UsageJsonRoot root, Config cfg)
        => string.IsNullOrEmpty(root.Primary) ? cfg.PrimaryStr() : root.Primary;

    // The CLI reports every candidate vendor, configured or not, so the ones the
    // user never set up come back as errors. Rendering those would keep the tray
    // icon permanently red, so only three kinds of entry earn a slot: one with
    // actual data, the primary vendor (an outage there must surface), and the
    // synthetic entry standing in for a CLI failure.
    private static bool ShouldShow(UsageJsonEntry entry, string primaryId)
        => entry.Status == "ready"
        || entry.Id == primaryId
        || entry.Id == UsageJsonEntry.SystemId;

    /// <summary>Highest percentage among the metrics actually on show, which is
    /// the same number the worst severity came from. Null when there is nothing
    /// to measure, so the tray ring stays empty instead of implying zero usage.</summary>
    private static int? WorstPercent(UsageJsonRoot root, string primaryId)
    {
        var percentages = root.Entries
            .Where(e => ShouldShow(e, primaryId) && e.Status == "ready")
            .SelectMany(e => e.Metrics ?? new List<UsageJsonMetric>())
            .Select(m => m.Percent)
            .ToList();

        return percentages.Count == 0 ? null : Math.Clamp(percentages.Max(), 0, 100);
    }

    /// <summary>Whether any vendor actually reported usage. False on a machine
    /// where nothing is signed in, and equally false when every request failed.</summary>
    private static bool HasUsableData(UsageJsonRoot root)
        => root.Entries.Any(e => e.Status == "ready");

    private static Severity GetWorstSeverity(UsageJsonEntry entry)
    {
        if (entry.Status != "ready") return Severity.Critical;
        if (entry.Metrics == null || entry.Metrics.Count == 0) return Severity.Low;

        var severities = entry.Metrics.Select(m => SeverityRules.Parse(m.Severity ?? "")).ToList();

        // A single unreadable metric is enough to distrust the whole entry.
        if (severities.Contains(Severity.Unknown)) return Severity.Unknown;

        return severities.Max();
    }

    // -- Popup ---------------------------------------------------------------

    public static PopupModel PopupModel(UsageJsonRoot root, Config cfg, DateTimeOffset now)
    {
        // Nothing readable and no synthetic system entry means the CLI ran fine
        // but has no account to report on, which is what a fresh install looks
        // like. Raw credential errors are useless to someone who has simply not
        // signed in yet, so answer the actual question: what do I do now?
        var systemFailure = root.Entries.Any(e => e.Id == UsageJsonEntry.SystemId);
        if (!HasUsableData(root) && !systemFailure)
        {
            return new PopupModel
            {
                NeedsSetup = true,
                SetupHint =
                    "No provider is set up yet.\n\n" +
                    "Sign in once with the Claude or Codex CLI, or add an API key "
                    + "to the config file through Settings. Usage shows up on the next refresh.",
                SetupDetail = string.Join("\n", root.Entries
                    .Where(e => !string.IsNullOrWhiteSpace(e.Error))
                    .Select(e => $"{e.DisplayName}: {e.Error!.Trim()}")),
            };
        }

        var model = new PopupModel();
        var primaryId = PrimaryId(root, cfg);

        foreach (var entry in Ordered(root.Entries, root.Primary))
        {
            if (!ShouldShow(entry, primaryId)) continue;

            if (entry.Status == "ready")
            {
                model.Vendors.Add(OkCard(entry));
            }
            else
            {
                model.Vendors.Add(new VendorCard
                {
                    Id = entry.Id,
                    Name = entry.DisplayName,
                    Status = "error",
                    Message = entry.Error ?? entry.Status,
                });
            }
        }
        return model;
    }

    private static VendorCard OkCard(UsageJsonEntry entry)
    {
        var bars = new List<Bar>();
        var facts = new List<Fact>();

        foreach (var section in entry.Sections)
        {
            if (section.Type == "metric")
            {
                bars.Add(new Bar
                {
                    Label = section.Label ?? "",
                    Pct = section.Percent ?? 0,
                    Level = section.Severity ?? "low",
                    Reset = section.Detail // We'll put detail in Reset since the UI binds to it as a secondary string
                });
            }
            else if (section.Type == "fact" && !string.IsNullOrEmpty(section.Text))
            {
                var parts = section.Text.Split(new[] { ':' }, 2);
                if (parts.Length == 2)
                {
                    facts.Add(new Fact { Label = parts[0].Trim(), Value = parts[1].Trim() });
                }
                else
                {
                    facts.Add(new Fact { Label = "Info", Value = section.Text });
                }
            }
        }

        // Add a general message if the entry has an error but status is ready (e.g. stale warning)
        string? message = null;
        if (entry.Stale) message = "Data is stale (offline).";
        if (!string.IsNullOrEmpty(entry.Error)) message = entry.Error;

        return new VendorCard
        {
            Id = entry.Id,
            Name = entry.DisplayName,
            Plan = entry.Plan,
            Status = "ok",
            Message = message,
            Bars = bars,
            Facts = facts,
        };
    }

    // -- Settings ------------------------------------------------------------

    public static SettingsModel SettingsModel(Config cfg, UsageJsonRoot root)
    {
        var model = new SettingsModel
        {
            PollSeconds = Math.Max(cfg.PollSeconds ?? 60, 15),
            Primary = cfg.PrimaryStr(),
        };

        foreach (var entry in root.Entries)
        {
            model.Vendors.Add(VendorSetting(entry, cfg));
        }

        return model;
    }

    private static VendorSetting VendorSetting(UsageJsonEntry entry, Config cfg)
    {
        var status = entry.Status == "ready" ? "Connected" : $"Error - {entry.Error ?? entry.Status}";

        return new VendorSetting
        {
            Id = entry.Id,
            Name = entry.DisplayName,
            Status = status,
        };
    }
}

public enum Severity { Unknown, Low, Mid, High, Critical }

public static class SeverityRules
{
    /// <summary>Maps a severity string reported by the CLI. An unrecognized value
    /// yields <see cref="Severity.Unknown"/>, never <see cref="Severity.Low"/>:
    /// if the CLI ever renames or adds a level, the UI must not silently claim
    /// that a maxed-out quota is healthy.</summary>
    public static Severity Parse(string level) => level switch
    {
        "critical" => Severity.Critical,
        "high" => Severity.High,
        "mid" => Severity.Mid,
        "low" => Severity.Low,
        _ => Severity.Unknown,
    };

    public static Severity ForPct(int pct) => pct switch
    {
        >= 90 => Severity.Critical,
        >= 75 => Severity.High,
        >= 50 => Severity.Mid,
        _ => Severity.Low,
    };
}
