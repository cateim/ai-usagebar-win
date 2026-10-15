using System;
using System.Windows.Input;
using System.Windows.Threading;
using AiUsageBar.Models;
using H.NotifyIcon;

namespace AiUsageBar.Services;

/// <summary>Minimal <see cref="ICommand"/> that always executes and forwards to an
/// <see cref="Action"/>. Used to bind tray clicks (H.NotifyIcon's TaskbarIcon
/// exposes LeftClickCommand/RightClickCommand rather than click events).</summary>
internal sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
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
    private readonly Dispatcher _ui;

    /// <summary>Retries creating the icon until the shell accepts it. At logon
    /// the app can start before Explorer's notification area exists, and the
    /// first attempt then fails.</summary>
    private readonly DispatcherTimer _retry;

    private string _tooltip = "";

    /// <summary>Raised on the UI thread when the icon is clicked.</summary>
    public event Action? Clicked;

    public TrayService(Dispatcher ui)
    {
        _ui = ui;
        _retry = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(5) };
        _retry.Tick += (_, _) =>
        {
            if (TryCreate()) _retry.Stop();
        };
    }

    /// <summary>Sets the icon up. Never throws: a tray icon that cannot be shown
    /// yet must not stop the poller from starting, which is what an exception
    /// here used to do, leaving a process with no icon and no data.</summary>
    public void Init()
    {
        _tooltip = "ai-usagebar - loading…";
        _icon.ToolTipText = _tooltip;
        _icon.Icon = TrayIconFactory.For(Severity.Unknown, null);

        // The single-click delay only exists to tell a click from a double click.
        // Nothing is bound to a double click, so all it did was hold every click
        // back by the system double-click time (half a second by default).
        _icon.NoLeftClickDelay = true;

        var toggle = new RelayCommand(OnClick);
        _icon.LeftClickCommand = toggle;
        _icon.RightClickCommand = toggle;

        // H.NotifyIcon re-adds the icon when Explorer restarts, but swallows a
        // failure there. Checking afterwards is what keeps one unlucky restart
        // from leaving the app running with no icon until the next reboot.
        _icon.TrayIcon.MessageWindow.TaskbarCreated += (_, _) => Defer(EnsureCreated);

        if (!TryCreate()) _retry.Start();
    }

    public void Update(Severity severity, string tooltip, int? percent = null)
    {
        _tooltip = ClampTooltip(tooltip);
        try
        {
            _icon.Icon = TrayIconFactory.For(severity, percent);
            _icon.ToolTipText = _tooltip;
        }
        catch (Exception ex)
        {
            // Shell_NotifyIcon refuses changes while Explorer restarts, and
            // H.NotifyIcon reports that as an exception. Re-adding the icon is
            // the repair; the next poll updates it either way.
            AppLog.WarnOnce("tray update failed: " + ex.Message);
            Recreate();
        }
    }

    /// <summary>The click handler for both buttons.
    ///
    /// <para>It runs wherever H.NotifyIcon calls it from, and that is the
    /// problem: a right click arrives inside the native window procedure of its
    /// message window, a left click inside a synchronous Dispatcher.Invoke made
    /// from a timer thread. An exception in either place never reaches the app's
    /// DispatcherUnhandledException handler. It unwinds through native frames or
    /// out of a thread-pool callback, and the process dies on the spot, which is
    /// what the September 2026 crash report looks like. Queueing the real work
    /// makes it an ordinary dispatcher operation, where that handler applies.</para></summary>
    private void OnClick() => Defer(() => Clicked?.Invoke());

    private void Defer(Action action)
    {
        try
        {
            _ui.BeginInvoke(DispatcherPriority.Input, action);
        }
        catch (Exception ex)
        {
            AppLog.Error("tray callback", ex);
        }
    }

    private void EnsureCreated()
    {
        if (!TryCreate()) _retry.Start();
    }

    /// <summary>Removes and re-adds the icon, restoring what it last showed.</summary>
    private void Recreate()
    {
        try { _icon.TrayIcon.TryRemove(); } catch { }
        EnsureCreated();
    }

    private bool TryCreate()
    {
        if (_icon.IsCreated) return true;

        try
        {
            // false matters. H.NotifyIcon's default also switches the whole
            // process into Efficiency Mode: idle priority class, plus EcoQoS on
            // Windows 11. Child processes inherit the idle class, so the CLI ran
            // at idle priority too, and under any real CPU load the popup opened
            // late and the CLI blew through its timeout. That was the freeze.
            _icon.ForceCreate(enablesEfficiencyMode: false);
        }
        catch (Exception ex)
        {
            AppLog.WarnOnce("tray icon not created yet: " + ex.Message);
            return false;
        }

        // Re-creating starts from what the shell was last told, which can lag
        // behind an update that failed while the icon was gone.
        try { _icon.TrayIcon.UpdateToolTip(_tooltip); } catch { }
        return true;
    }

    /// <summary>Win32 tray tooltips are length-limited (~127 chars). Trim defensively.</summary>
    private static string ClampTooltip(string s)
    {
        const int max = 120;
        if (s.Length <= max) return s;
        return string.Concat(s.AsSpan(0, max - 1), "…");
    }

    public void Dispose()
    {
        _retry.Stop();
        _icon.Dispose();
    }
}
