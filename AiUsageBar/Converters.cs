using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AiUsageBar;

/// <summary>Maps a bar severity level ("low"/"mid"/"high"/"critical") to its
/// fill brush, matching the palette used by the tray icon.</summary>
public sealed class LevelToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Low = new(Color.FromArgb(0xFF, 0x4C, 0xAF, 0x50));
    private static readonly SolidColorBrush Mid = new(Color.FromArgb(0xFF, 0xFF, 0xC1, 0x07));
    private static readonly SolidColorBrush High = new(Color.FromArgb(0xFF, 0xFF, 0x98, 0x00));
    private static readonly SolidColorBrush Critical = new(Color.FromArgb(0xFF, 0xF4, 0x43, 0x36));

    /// <summary>Grey, for a level string this build does not know.</summary>
    private static readonly SolidColorBrush Unknown = new(Color.FromArgb(0xFF, 0x9E, 0x9E, 0x9E));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (value as string) switch
    {
        "low" => Low,
        "mid" => Mid,
        "high" => High,
        "critical" => Critical,
        // Never green by default. The tray already refuses to read an
        // unrecognized severity as healthy (SeverityRules.Parse maps it to
        // Unknown), and the popup is the surface where the actual numbers are
        // read, so a renamed level upstream must not paint a maxed-out quota
        // green here while the icon correctly says "no idea".
        _ => Unknown,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool → Visibility. Pass "invert" as the parameter to flip the sense.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is bool v && v;
        if (parameter as string == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
