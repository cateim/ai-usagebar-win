using System.Drawing;
using System.Drawing.Drawing2D;
using AiUsageBar.Models;

namespace AiUsageBar.Services;

/// <summary>Draws the tray icon in code, with no asset file, so the build stays
/// self-contained. Two things are encoded at once: the colour is the worst-case
/// severity, and the ring fills clockwise with the highest usage on show, so the
/// notification area answers both "is it bad?" and "how much is left?" without
/// opening anything.</summary>
public static class TrayIconFactory
{
    private const int Size = 32;

    /// <summary>Keyed by severity and percentage bucket. Bounded by design:
    /// five severities times 21 buckets plus the empty state, so the icons kept
    /// alive for the process are a fixed, small set.</summary>
    private static readonly Dictionary<(Severity Severity, int Bucket), Icon> Cache = new();

    private static (int R, int G, int B) Rgb(Severity s) => s switch
    {
        Severity.Unknown => (0x9e, 0x9e, 0x9e),   // grey
        Severity.Low => (0x4c, 0xaf, 0x50),       // green
        Severity.Mid => (0xff, 0xc1, 0x07),       // amber
        Severity.High => (0xff, 0x98, 0x00),      // orange
        Severity.Critical => (0xf4, 0x43, 0x36),  // red
        _ => (0x9e, 0x9e, 0x9e),
    };

    /// <summary>Unfilled part of the ring. Dark enough to disappear against a
    /// dark taskbar while still separating the filled arc from nothing.</summary>
    private static readonly Color TrackColor = Color.FromArgb(255, 0x4A, 0x4C, 0x52);

    /// <summary>Percentages are bucketed before becoming a cache key, so a value
    /// drifting by a point does not mint a new icon. Five points is finer than
    /// the eye resolves on a 16px ring.</summary>
    private const int PercentBucket = 5;

    /// <summary>A 32x32 icon: an AI sparkle inside a ring that fills clockwise
    /// with usage, the same mark scripts/generate-icon.py bakes into the .exe, so
    /// the tray and the executable read as one product.
    ///
    /// <para>Colour reports severity, the filled arc reports how much is gone. At
    /// 16px the sparkle is what survives, which is why it carries the shape and
    /// the ring only qualifies it.</para>
    ///
    /// <para><paramref name="percent"/> null means nothing measurable was
    /// reported: the ring is left empty rather than implying zero usage.</para></summary>
    public static Icon For(Severity severity, int? percent = null)
    {
        var bucket = percent is null
            ? -1
            : Math.Clamp(percent.Value, 0, 100) / PercentBucket;

        var key = (severity, bucket);
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var (r, g, b) = Rgb(severity);
        using var bmp = new Bitmap(Size, Size);
        using (var gfx = Graphics.FromImage(bmp))
        {
            gfx.SmoothingMode = SmoothingMode.AntiAlias;
            gfx.Clear(Color.Transparent);

            using var fill = new SolidBrush(Color.FromArgb(255, r, g, b));

            // Stroked as an arc rather than two filled circles, which keeps the
            // ring an even thickness at every size.
            const float ringOuter = Size * 0.43f;
            const float ringInner = Size * 0.375f;
            const float ringWidth = ringOuter - ringInner;
            var ringRadius = (ringOuter + ringInner) / 2f;
            var centre = Size / 2f;
            var box = new RectangleF(
                centre - ringRadius, centre - ringRadius,
                ringRadius * 2, ringRadius * 2);

            using (var track = new Pen(TrackColor, ringWidth))
            {
                gfx.DrawEllipse(track, box);
            }

            // Clockwise from twelve o'clock, so it fills like a dial.
            var sweep = bucket < 0 ? 0f : Math.Clamp(percent!.Value, 0, 100) / 100f * 360f;
            if (sweep > 0)
            {
                using var arc = new Pen(fill, ringWidth);
                gfx.DrawArc(arc, box, -90f, sweep);
            }

            using var spark = Sparkle(centre, centre, Size * 0.30f);
            gfx.FillPath(fill, spark);
        }

        // GetHicon's handle is intentionally kept: each icon lives for the
        // process, and the cache key space is bounded (see Cache).
        var icon = Icon.FromHandle(bmp.GetHicon());
        Cache[key] = icon;
        return icon;
    }

    /// <summary>Four-pointed star with concave sides, traced as the superellipse
    /// |x|^n + |y|^n = 1. The generator script fills the same curve by testing
    /// pixels; here it is sampled into a path, which anti-aliases better.
    /// Exponent 0.62 gives points sharp enough to read as a sparkle without
    /// becoming spindly at 16px.</summary>
    private static GraphicsPath Sparkle(float cx, float cy, float size)
    {
        const double exponent = 0.62;
        const int steps = 96;

        // Polar form: solving the superellipse for r at each angle.
        var points = new PointF[steps];
        for (var i = 0; i < steps; i++)
        {
            var t = 2 * Math.PI * i / steps;
            var cos = Math.Cos(t);
            var sin = Math.Sin(t);
            var denom = Math.Pow(Math.Abs(cos), exponent) + Math.Pow(Math.Abs(sin), exponent);
            var r = size / Math.Pow(denom, 1.0 / exponent);

            points[i] = new PointF(
                (float)(cx + r * cos),
                (float)(cy + r * sin));
        }

        var path = new GraphicsPath();
        path.AddPolygon(points);
        return path;
    }
}
