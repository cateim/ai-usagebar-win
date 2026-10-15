using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AiUsageBar.Models;

namespace AiUsageBar.Services;

public static class Renderer
{
    /// <summary>What the tray needs: the colour (Severity), the hover text, and
    /// how full the ring should be. Percent is null when nothing measurable was
    /// reported, which draws an empty ring rather than a misleading full one.</summary>
    public sealed record Rendered(Severity Severity, string Tooltip, int? Percent);

    /// <summary>The newest <c>usage --json</c> schema this build was written
    /// against. Upstream bumps it only for a shape older readers cannot handle.</summary>
    public const int SupportedSchema = 1;

    public static Rendered Render(UsageJsonRoot root, Config cfg, DateTimeOffset now)
    {
        var severities = new List<Severity>();
        var tipLines = new List<string>();

        var primaryId = PrimaryId(root, cfg);

        // First, because the tooltip is clamped from the end: numbers that did
        // not refresh must never read as current, and this line is what says so.
        if (root.RefreshError != null && root.ReceivedAt is { } receivedAt)
        {
            tipLines.Add("Not refreshed since " + Clock(receivedAt, now));
        }

        foreach (var entry in Ordered(Entries(root), primaryId))
        {
            if (!ShouldShow(entry, primaryId)) continue;

            severities.Add(GetWorstSeverity(entry));

            var tag = Tag(entry);

            if (entry.Status != "ready")
            {
                tipLines.Add($"{tag}: {entry.Status}");
                continue;
            }

            // The most critical quota window, and among equals the fullest one.
            var worstMetric = QuotaMetrics(entry)
                .OrderByDescending(m => SeverityRules.Parse(m.Severity ?? ""))
                .ThenByDescending(m => m.Percent)
                .FirstOrDefault();
            if (worstMetric != null)
            {
                tipLines.Add($"{tag} {worstMetric.Value} · {worstMetric.Label}");
            }
            else
            {
                // Prepaid vendors report no quota bars at all, only a balance,
                // and they send it as a "text" section. Showing "ready" instead
                // of the number was the whole tooltip for those users.
                var text = Sections(entry).FirstOrDefault(s => s.Type == "text" && !string.IsNullOrEmpty(s.Value));
                if (text != null)
                {
                    tipLines.Add($"{tag} {text.Value}");
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

    /// <summary>The primary vendor first, everything else in the order the CLI
    /// sent it. The CLI orders entries the way the user arranged them
    /// (<c>[ui] overview_vendors</c>), and OrderBy is stable, so that order
    /// survives. Sorting the rest by id used to throw it away.</summary>
    private static IEnumerable<UsageJsonEntry> Ordered(List<UsageJsonEntry> entries, string primaryId)
        => entries.OrderBy(r => !IsPrimary(r, primaryId));

    /// <summary>Null-safe metric list. The CLI omits the field on an errored
    /// entry and could start sending an explicit null at any upstream release,
    /// which System.Text.Json writes straight over the property initializer.
    /// Every access goes through here so one such change cannot crash a render.</summary>
    private static List<UsageJsonMetric> Metrics(UsageJsonEntry entry)
        => entry.Metrics ?? new List<UsageJsonMetric>();

    /// <summary>The metrics that speak for the entry's quota.
    ///
    /// <para><c>metrics</c> also carries rows filed under a heading: the context
    /// window of each live Claude Code session ("Sessions"), SuperGrok's product
    /// slices ("Breakdown"). A session at 90% of its context window is not a
    /// quota at 90%, yet counting it painted the tray red while every real quota
    /// sat at 29%. Upstream hit the same bug in its own tray and settled on this
    /// partition: the standalone rows decide, and grouped rows stand in only when
    /// an entry has nothing else.</para></summary>
    private static List<UsageJsonMetric> QuotaMetrics(UsageJsonEntry entry)
    {
        var all = Metrics(entry);
        var standalone = all.Where(m => string.IsNullOrWhiteSpace(m.Group)).ToList();
        return standalone.Count > 0 ? standalone : all;
    }

    private static List<UsageJsonSection> Sections(UsageJsonEntry entry)
        => entry.Sections ?? new List<UsageJsonSection>();

    private static List<UsageJsonEntry> Entries(UsageJsonRoot root)
        => root.Entries ?? new List<UsageJsonEntry>();

    /// <summary>Three-letter tag for the tooltip. The CLI assigns one per vendor
    /// and guarantees it is unique, including for custom providers, so it is the
    /// authority. The truncation is only a fallback for an older CLI that does
    /// not send the field: it disagreed with the CLI for most vendors and made
    /// every custom provider read as "cus".</summary>
    private static string Tag(UsageJsonEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.ShortName)) return entry.ShortName!;
        var id = VendorSlug(entry.Id);
        return id.Length <= 3 ? id : id[..3];
    }

    /// <summary>The vendor part of an entry id. Ids are not bare slugs: a named
    /// account is "anthropic@work" and a custom provider is "custom:&lt;id&gt;".
    /// The primary is an entry id since CLI 1.21 and a bare slug before it (and
    /// in the local config), so both sides are reduced to the vendor before
    /// comparing. Comparing raw ids meant a user with named accounts had no
    /// primary at all, so an expired login on the vendor they care about
    /// stopped surfacing.</summary>
    public static string VendorSlug(string id)
    {
        var at = id.IndexOf('@');
        return at > 0 ? id[..at] : id;
    }

    /// <summary>Every account of the primary vendor counts as primary, not only
    /// the first one the CLI names, so a failed login on any of them surfaces.</summary>
    private static bool IsPrimary(UsageJsonEntry entry, string primaryId)
        => !string.IsNullOrEmpty(primaryId)
        && VendorSlug(entry.Id) == VendorSlug(primaryId);

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
        || IsPrimary(entry, primaryId)
        || entry.Id == UsageJsonEntry.SystemId;

    /// <summary>Highest percentage among the quota windows actually on show,
    /// which is the same number the worst severity came from. Null when there is
    /// nothing to measure, so the tray ring stays empty instead of implying zero
    /// usage.</summary>
    private static int? WorstPercent(UsageJsonRoot root, string primaryId)
    {
        var percentages = Entries(root)
            .Where(e => ShouldShow(e, primaryId) && e.Status == "ready")
            .SelectMany(QuotaMetrics)
            .Select(m => m.Percent)
            .ToList();

        return percentages.Count == 0 ? null : Math.Clamp(percentages.Max(), 0, 100);
    }

    /// <summary>Whether any vendor actually reported usage. False on a machine
    /// where nothing is signed in, and equally false when every request failed.</summary>
    private static bool HasUsableData(UsageJsonRoot root)
        => Entries(root).Any(e => e.Status == "ready");

    private static Severity GetWorstSeverity(UsageJsonEntry entry)
    {
        if (entry.Status != "ready") return Severity.Critical;

        var metrics = QuotaMetrics(entry);
        if (metrics.Count == 0) return Severity.Low;

        var severities = metrics.Select(m => SeverityRules.Parse(m.Severity ?? "")).ToList();

        // A single unreadable metric is enough to distrust the whole entry.
        if (severities.Contains(Severity.Unknown)) return Severity.Unknown;

        return severities.Max();
    }

    /// <summary>A time short enough for a tooltip: the clock alone today, the
    /// date as well otherwise, since "since 14:32" three days later lies.</summary>
    private static string Clock(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToLocalTime();
        return local.Date == now.ToLocalTime().Date
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("MMM d, HH:mm", CultureInfo.InvariantCulture);
    }

    // -- Popup ---------------------------------------------------------------

    public static PopupModel PopupModel(UsageJsonRoot root, Config cfg, DateTimeOffset now)
    {
        var notice = Notice(root, now);
        var updated = root.ReceivedAt is { } at ? "Updated " + Clock(at, now) : null;

        // Nothing readable and no synthetic system entry means the CLI ran fine
        // but has no account to report on, which is what a fresh install looks
        // like. Raw credential errors are useless to someone who has simply not
        // signed in yet, so answer the actual question: what do I do now?
        var systemFailure = Entries(root).Any(e => e.Id == UsageJsonEntry.SystemId);
        if (!HasUsableData(root) && !systemFailure)
        {
            return new PopupModel
            {
                NeedsSetup = true,
                SetupHint =
                    "No provider is set up yet.\n\n" +
                    "Sign in once with the Claude or Codex CLI, or add an API key "
                    + "to the config file through Settings. Usage shows up on the next refresh.",
                SetupDetail = string.Join("\n", Entries(root)
                    .Where(e => !string.IsNullOrWhiteSpace(e.Error))
                    .Select(e => $"{e.DisplayName}: {e.Error!.Trim()}")),
                Notice = notice,
                UpdatedText = updated,
            };
        }

        var model = new PopupModel { Notice = notice, UpdatedText = updated };
        var primaryId = PrimaryId(root, cfg);

        foreach (var entry in Ordered(Entries(root), primaryId))
        {
            if (!ShouldShow(entry, primaryId)) continue;

            if (entry.Status == "ready")
            {
                model.Vendors.Add(OkCard(entry, now));
            }
            else
            {
                model.Vendors.Add(new VendorCard
                {
                    Id = entry.Id,
                    Name = entry.DisplayName,
                    // The CLI names the plan even on a failed fetch (it reads it
                    // from the stored login), and dropping it here is why a card
                    // sometimes lost its plan name the moment a request failed.
                    Plan = entry.Plan,
                    Status = "error",
                    Message = entry.Error ?? entry.Status,
                });
            }
        }
        return model;
    }

    /// <summary>What the popup must say above the numbers, or null.</summary>
    private static string? Notice(UsageJsonRoot root, DateTimeOffset now)
    {
        var lines = new List<string>();

        if (!string.IsNullOrEmpty(root.RefreshError))
        {
            var since = root.ReceivedAt is { } at ? $" Showing data from {Clock(at, now)}." : "";
            lines.Add($"Could not refresh: {root.RefreshError}{since}");
        }

        if (root.SchemaVersion is { } schema && schema > SupportedSchema)
        {
            lines.Add($"The CLI reports schema {schema}, newer than the {SupportedSchema} this app reads. "
                + "Some values may be missing until the app is updated.");
        }

        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private static VendorCard OkCard(UsageJsonEntry entry, DateTimeOffset now)
    {
        var bars = new List<Bar>();
        var groups = new List<BarGroup>();
        var facts = new List<Fact>();

        // The CLI emits exactly four section types: metric, text, block and
        // spacer. This used to handle "metric" and "fact", and "fact" has never
        // existed upstream, so everything that was not a quota bar was dropped:
        // vendors that report no bars at all (Grok, DeepSeek, Kilo) rendered as
        // a name and a plan chip over an empty card, hiding the prepaid balance
        // that was the only number they had. Spacer carries no content and is
        // correctly ignored.
        foreach (var section in Sections(entry))
        {
            switch (section.Type)
            {
                case "metric":
                    var bar = MetricBar(section);
                    if (string.IsNullOrWhiteSpace(section.Group))
                    {
                        bars.Add(bar);
                    }
                    else
                    {
                        GroupFor(groups, section.Group!).Bars.Add(bar);
                    }
                    break;

                case "text":
                    if (string.IsNullOrWhiteSpace(section.Value)) break;

                    // A text row that carries a percentage meters a prepaid cap
                    // (Cursor's On-Demand spend). That is a gauge, so it is drawn
                    // as one, with the amount as its headline.
                    if (section.Percent is { } spent)
                    {
                        bars.Add(new Bar
                        {
                            Label = string.IsNullOrWhiteSpace(section.Label) ? "Spend" : section.Label!,
                            Pct = spent,
                            Level = LevelName(SeverityRules.ForPct(spent)),
                            ValueText = section.Value!,
                        });
                    }
                    else
                    {
                        facts.Add(new Fact
                        {
                            Label = string.IsNullOrWhiteSpace(section.Label) ? "Info" : section.Label!,
                            Value = section.Value!,
                        });
                    }
                    break;

                case "block":
                    // A block is a label over several lines. The card has one
                    // value slot per fact, so the lines are joined rather than
                    // repeating the label for each of them.
                    var body = section.Body;
                    if (body is { Count: > 0 })
                    {
                        var lines = body.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                        if (lines.Count > 0)
                        {
                            facts.Add(new Fact
                            {
                                Label = string.IsNullOrWhiteSpace(section.Label) ? "Info" : section.Label!,
                                Value = string.Join("\n", lines),
                            });
                        }
                    }
                    break;
            }
        }

        // Add a general message if the entry has an error but status is ready (e.g. stale warning)
        string? message = null;
        if (entry.Stale)
        {
            message = entry.FetchedAt is { } fetched
                ? $"Showing data from {Clock(fetched, now)}: the provider could not be reached."
                : "Data is stale (offline).";
        }
        if (!string.IsNullOrEmpty(entry.Error)) message = entry.Error;

        return new VendorCard
        {
            Id = entry.Id,
            Name = entry.DisplayName,
            Plan = entry.Plan,
            Status = "ok",
            Message = message,
            Bars = bars,
            Groups = groups,
            Facts = facts,
        };
    }

    private static Bar MetricBar(UsageJsonSection section)
    {
        var pct = section.Percent ?? 0;
        return new Bar
        {
            Label = section.Label ?? "",
            Pct = pct,
            // An absent severity is painted grey, not green, for the same reason
            // an unknown one is: the popup must never call a quota healthy on a
            // guess.
            Level = section.Severity ?? "",
            // The UI binds Reset as the bar's secondary line, which is where
            // the CLI's detail text belongs.
            Reset = section.Detail,
            ValueText = section.Headline == "value" && !string.IsNullOrWhiteSpace(section.Value)
                ? section.Value!
                : $"{pct}%",
        };
    }

    /// <summary>The group of that title, created on first use, so groups keep
    /// the order the CLI listed them in.</summary>
    private static BarGroup GroupFor(List<BarGroup> groups, string title)
    {
        var group = groups.FirstOrDefault(g => g.Title == title);
        if (group != null) return group;

        group = new BarGroup { Title = title };
        groups.Add(group);
        return group;
    }

    private static string LevelName(Severity severity) => severity switch
    {
        Severity.Critical => "critical",
        Severity.High => "high",
        Severity.Mid => "mid",
        Severity.Low => "low",
        _ => "",
    };

    // -- Settings ------------------------------------------------------------

    public static SettingsModel SettingsModel(Config cfg, UsageJsonRoot root)
    {
        var model = new SettingsModel
        {
            PollSeconds = Math.Max(cfg.PollSeconds ?? 60, 15),
            Primary = cfg.PrimaryStr(),
        };

        foreach (var entry in Entries(root))
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
