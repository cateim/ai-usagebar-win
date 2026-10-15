using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AiUsageBar.Models;
using AiUsageBar.Services;

namespace AiUsageBar.Views;

/// <summary>Frameless, always-on-top popup anchored near the tray click. It
/// light-dismisses when it loses focus, with a short grace period so the click
/// that opened it does not immediately close it (mirrors the Win32 original).</summary>
public partial class PopupWindow : Window
{
    public event Action? RefreshRequested;
    public event Action? SettingsRequested;
    public event Action? QuitRequested;

    private bool _visible;
    private DateTimeOffset _shownAt;
    private DateTimeOffset _hiddenAt;

    private bool _busy;
    private string? _updatedText;

    /// <summary>Where the popup belongs while it is open: the cursor position
    /// at the moment it opened, the work area of that monitor and the taskbar
    /// edge, all in device-independent pixels. Captured once per opening.</summary>
    private sealed record Anchor(double X, double Y, Rect Work, Dock Edge);

    private Anchor? _anchor;

    public PopupWindow()
    {
        InitializeComponent();
        Deactivated += OnDeactivated;

        // Hide instead of closing, the same guard the settings window has. The
        // app caches this instance and never rebuilds it, so a real close (Alt+F4
        // while the popup has focus) left a destroyed window behind: the next
        // tray click called Show() on it, threw, and with no handler for that the
        // whole tray app died and had to be relaunched from the Start Menu.
        Closing += (_, e) =>
        {
            e.Cancel = true;
            HidePopup();
        };
    }

    public void Toggle(Config cfg, UsageJsonRoot root)
    {
        if (_visible)
        {
            HidePopup();
            return;
        }
        // The same click that dismissed it also re-fires here, so ignore it.
        if ((DateTimeOffset.UtcNow - _hiddenAt).TotalMilliseconds < 300) return;

        Populate(cfg, root);
        Show();
        _anchor = CaptureAnchor();
        Place(_anchor);
        Activate();
        _visible = true;
        _shownAt = DateTimeOffset.UtcNow;
    }

    public void EnsureShown(Config cfg, UsageJsonRoot root)
    {
        if (_visible)
        {
            Activate();
            return;
        }
        Toggle(cfg, root);
    }

    public void HidePopup()
    {
        if (!_visible) return;
        Hide();
        // Clearing this is what lets Toggle open the popup again. Leaving it set
        // made every later click take the "already visible, so hide" branch,
        // hiding an already hidden window forever.
        _visible = false;
        _hiddenAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Update data without altering visibility.</summary>
    public void Refresh(Config cfg, UsageJsonRoot root)
    {
        if (!IsVisible) return;

        Populate(cfg, root);

        // Reposition afterwards. The window sizes to its content, and Top was
        // computed for the height it had when it opened, so a poll that added an
        // error line or a metric bar to an open popup grew it downwards past the
        // taskbar and off the screen.
        //
        // Against the anchor taken when it opened, never the cursor as it is
        // now. A refresh that follows a click on Refresh finds the cursor on that
        // button, not on the tray icon, so re-reading it re-centred the popup on
        // the button and walked it sideways a button's width on every click (and
        // onto another monitor, if the mouse had wandered there meanwhile).
        Place(_anchor ??= CaptureAnchor());
    }

    /// <summary>Whether a poll is running. Safe to call while hidden, so the
    /// footer is already right when the popup opens.</summary>
    public void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        StatusLabel.Text = _busy ? "Updating…" : _updatedText ?? "";
    }

    private void Populate(Config cfg, UsageJsonRoot root)
    {
        var model = Renderer.PopupModel(root, cfg, DateTimeOffset.UtcNow);

        // The setup panel and the vendor list are alternatives, never both: the
        // panel only appears when there is nothing worth listing.
        var showSetup = model.NeedsSetup || model.IsEmpty;
        SetupPanel.Visibility = showSetup ? Visibility.Visible : Visibility.Collapsed;
        VendorsList.Visibility = showSetup ? Visibility.Collapsed : Visibility.Visible;

        SetupHintLabel.Text = string.IsNullOrEmpty(model.SetupHint)
            ? "No usage to show yet."
            : model.SetupHint;

        SetupDetailLabel.Text = model.SetupDetail;
        SetupDetailLabel.Visibility = string.IsNullOrWhiteSpace(model.SetupDetail)
            ? Visibility.Collapsed
            : Visibility.Visible;

        NoticeLabel.Text = model.Notice ?? "";
        NoticePanel.Visibility = string.IsNullOrEmpty(model.Notice)
            ? Visibility.Collapsed
            : Visibility.Visible;

        _updatedText = model.UpdatedText;
        UpdateStatus();

        VendorsList.ItemsSource = model.Vendors;
    }

    /// <summary>Set to 1 to stop the popup from hiding when it loses focus.
    /// Auto-hide makes the popup impossible to screenshot: focusing a terminal to
    /// run a capture command dismisses the very window being captured. Opt-in and
    /// absent in normal use.</summary>
    private const string PinVariable = "AIUSAGEBAR_WIN_PIN_POPUP";

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (Environment.GetEnvironmentVariable(PinVariable) == "1") return;

        // Grace period so the activating click does not instantly dismiss.
        if ((DateTimeOffset.UtcNow - _shownAt).TotalMilliseconds < 400) return;
        HidePopup();
    }

    /// <summary>Reads where the popup should go: just above the taskbar,
    /// horizontally near the tray click. The work area excludes the taskbar, so
    /// its bottom edge is the taskbar's top edge (for a bottom taskbar). Pinning
    /// the popup there keeps it above the taskbar regardless of where the cursor
    /// was.</summary>
    private Anchor CaptureAnchor()
    {
        NativeMethods.GetCursorPos(out var pt);

        // The monitor under the cursor, not the primary one. SystemParameters
        // .WorkArea only ever describes the primary monitor, so a taskbar on a
        // secondary screen sent the popup to the other display entirely, nowhere
        // near the icon that was clicked.
        var monitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);

        var info = new NativeMethods.MONITORINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>()
        };

        if (monitor == 0 || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return AnchorOnPrimary(pt);
        }

        // That monitor's scale, not this window's. The process is PerMonitorV2,
        // so on a 150% laptop panel beside a 100% external screen the two differ
        // and the cursor position came out off by the ratio.
        var scale = 1.0;
        if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0
            && dpiX > 0)
        {
            scale = dpiX / 96.0;
        }
        else
        {
            scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        }

        var work = new Rect(
            info.rcWork.Left / scale,
            info.rcWork.Top / scale,
            (info.rcWork.Right - info.rcWork.Left) / scale,
            (info.rcWork.Bottom - info.rcWork.Top) / scale);

        return new Anchor(pt.X / scale, pt.Y / scale, work, TaskbarEdge(info));
    }

    /// <summary>Fallback when the monitor cannot be resolved: the old primary
    /// monitor behaviour, which is right on the single-screen setup that is by
    /// far the common case.</summary>
    private Anchor AnchorOnPrimary(NativeMethods.POINT pt)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Anchor(pt.X / dpi.DpiScaleX, pt.Y / dpi.DpiScaleY, SystemParameters.WorkArea, Dock.Bottom);
    }

    private const double ScreenMargin = 8;

    /// <summary>Which edge the taskbar is docked to, derived from the gap
    /// between the monitor and its work area.</summary>
    private static Dock TaskbarEdge(NativeMethods.MONITORINFO info)
    {
        var left = info.rcWork.Left - info.rcMonitor.Left;
        var top = info.rcWork.Top - info.rcMonitor.Top;
        var right = info.rcMonitor.Right - info.rcWork.Right;
        var bottom = info.rcMonitor.Bottom - info.rcWork.Bottom;

        var widest = Math.Max(Math.Max(left, right), Math.Max(top, bottom));

        // No gap at all means the taskbar auto-hides. Bottom is the default and
        // the overwhelmingly common case, so it is the right guess.
        if (widest <= 0) return Dock.Bottom;
        if (widest == bottom) return Dock.Bottom;
        if (widest == top) return Dock.Top;
        if (widest == left) return Dock.Left;
        return Dock.Right;
    }

    /// <summary>Anchors the popup against the taskbar's own edge, following the
    /// click along the other axis and clamped inside the work area. The width
    /// is fixed, so for a given anchor only the height can change the result,
    /// and it moves the popup away from the taskbar edge, never sideways.</summary>
    private void Place(Anchor anchor)
    {
        var (cursorX, cursorY, work, edge) = anchor;

        // Capped to the work area before measuring, so a popup with more cards
        // than the screen holds scrolls inside itself instead of being clamped
        // off the top of the monitor. Then laid out, so the size used below is
        // the one it will actually have.
        MaxHeight = Math.Max(200, work.Height - 2 * ScreenMargin);
        UpdateLayout();

        var w = ActualWidth;
        var h = ActualHeight;
        const double margin = ScreenMargin;

        double x, y;

        if (edge == Dock.Left || edge == Dock.Right)
        {
            // A vertical taskbar puts the tray at its bottom, so follow the
            // click vertically and pin to the side the bar occupies.
            x = edge == Dock.Left ? work.Left + margin : work.Right - w - margin;
            y = cursorY - h / 2;
        }
        else
        {
            x = cursorX - w / 2;
            y = edge == Dock.Top ? work.Top + margin : work.Bottom - h - margin;
        }

        if (x + w + margin > work.Right) x = work.Right - w - margin;
        if (x < work.Left + margin) x = work.Left + margin;
        if (y + h + margin > work.Bottom) y = work.Bottom - h - margin;
        if (y < work.Top + margin) y = work.Top + margin;

        Left = x;
        Top = y;
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();
    private void OnSettings(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    private void OnQuit(object sender, RoutedEventArgs e) => QuitRequested?.Invoke();
}
