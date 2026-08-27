using System;
using System.Windows.Input;
using AiUsageBar.Models;
using H.NotifyIcon;

namespace AiUsageBar.Services;

/// <summary>Minimal <see cref="ICommand"/> that always executes and forwards to an
/// <see cref="Action"/>. Used to bind tray clicks (H.NotifyIcon's TaskbarIcon
/// exposes LeftClickCommand/RightClickCommand rather than click events).</summary>
internal sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}

/// <summary>Owns the notification-area icon via H.NotifyIcon.Wpf. Any click (left
/// or right, since there is no context menu) toggles the popup, matching the original.
/// <c>TaskbarIcon.Icon</c> is a <c>System.Drawing.Icon</c>; <c>ToolTipText</c> is
/// the hover tooltip; <c>ForceCreate()</c> realizes the icon.</summary>
public sealed class TrayService : IDisposable
{
    private readonly TaskbarIcon _icon = new();

    /// <summary>Raised on the UI thread when the icon is clicked.</summary>
    public event Action? Clicked;

    public void Init()
    {
        _icon.ToolTipText = "ai-usagebar - loading…";
        _icon.Icon = TrayIconFactory.For(Severity.Unknown, null);
        var toggle = new RelayCommand(() => Clicked?.Invoke());
        _icon.LeftClickCommand = toggle;
        _icon.RightClickCommand = toggle;
        _icon.ForceCreate();
    }

    public void Update(Severity severity, string tooltip, int? percent = null)
    {
        _icon.Icon = TrayIconFactory.For(severity, percent);
        _icon.ToolTipText = ClampTooltip(tooltip);
    }

    /// <summary>Win32 tray tooltips are length-limited (~127 chars). Trim defensively.</summary>
    private static string ClampTooltip(string s)
    {
        const int max = 120;
        if (s.Length <= max) return s;
        return string.Concat(s.AsSpan(0, max - 1), "…");
    }

    public void Dispose() => _icon.Dispose();
}
