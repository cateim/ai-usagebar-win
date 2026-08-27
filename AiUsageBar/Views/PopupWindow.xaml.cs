using System;
using System.Collections.Generic;
using System.Windows;
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

    public PopupWindow()
    {
        InitializeComponent();
        Deactivated += OnDeactivated;
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
        UpdateLayout(); // realize SizeToContent so ActualWidth/Height are known
        PositionAboveTaskbar();
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

    /// <summary>Update data without altering visibility or placement.</summary>
    public void Refresh(Config cfg, UsageJsonRoot root)
    {
        if (IsVisible) Populate(cfg, root);
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

    /// <summary>Anchor the popup just above the taskbar, horizontally near the
    /// tray click. The work area excludes the taskbar, so its bottom edge is the
    /// taskbar's top edge (for a bottom taskbar). Pinning the popup there keeps
    /// it above the taskbar regardless of where the cursor was.</summary>
    private void PositionAboveTaskbar()
    {
        // GetCursorPos is in physical pixels; WPF Left/Top are in DIPs.
        NativeMethods.GetCursorPos(out var pt);
        var dpi = VisualTreeHelper.GetDpi(this);
        var cx = pt.X / dpi.DpiScaleX;

        var w = ActualWidth;
        var h = ActualHeight;
        var work = SystemParameters.WorkArea; // DIPs (primary monitor), taskbar excluded

        const double margin = 8;

        // Horizontally follow the click (the tray icon), centered, clamped on-screen.
        var x = cx - w / 2;
        if (x + w + margin > work.Right) x = work.Right - w - margin;
        if (x < work.Left + margin) x = work.Left + margin;

        // Vertically pin to the bottom of the work area, always above the taskbar.
        var y = work.Bottom - h - margin;
        if (y < work.Top + margin) y = work.Top + margin;

        Left = x;
        Top = y;
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();
    private void OnSettings(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    private void OnQuit(object sender, RoutedEventArgs e) => QuitRequested?.Invoke();
}
