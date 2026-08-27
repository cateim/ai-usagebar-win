using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using AiUsageBar.Models;
using AiUsageBar.Services;
using AiUsageBar.Views;

namespace AiUsageBar;

/// <summary>
/// Tray-first WPF app: there is no main window. <see cref="OnStartup"/> installs
/// the notification-area icon and starts the background poller; the popup and
/// settings windows are created lazily on first use.
///
/// A background thread polls every vendor on an interval and marshals results to
/// the UI thread (via the dispatcher), which owns the tray icon and the windows.
/// </summary>
public partial class App : Application
{
    // Single-instance coordination (per user session). The mutex marks "an
    // instance is running"; the event lets a second launch ask the first to
    // surface its popup instead of spawning a duplicate tray icon.
    private const string InstanceMutexName = @"Local\AiUsageBar.SingleInstance";
    private const string ShowPopupEventName = @"Local\AiUsageBar.ShowPopup";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;

    private TrayService _tray = null!;
    private Poller _poller = null!;

    private PopupWindow? _popup;
    private SettingsWindow? _settings;

    // Latest poll result, handed to windows when they open.
    private Config _cfg = new();
    private UsageJsonRoot _root = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single instance: if one is already running, ask it to show its popup
        // (e.g. the user re-launched from Windows Search) and exit immediately.
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirst);
        if (!isFirst)
        {
            SignalExistingInstance();
            Shutdown();
            return;
        }

        // No window keeps the process alive. Only the tray icon does.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Make the app findable in Windows Search via a Start Menu shortcut.
        ShortcutService.EnsureStartMenuShortcut();

        StartShowPopupListener();

        _tray = new TrayService();
        _tray.Clicked += OnTrayClicked;
        _tray.Init();

        _poller = new Poller(Dispatcher);
        _poller.Updated += OnUpdated;
        _poller.Start();
    }

    /// <summary>Owns the named event the running instance waits on. A background
    /// thread blocks on it and, when a second launch sets it, marshals a
    /// "show popup" back onto the UI thread.</summary>
    private void StartShowPopupListener()
    {
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowPopupEventName);
        var thread = new Thread(() =>
        {
            try
            {
                while (_showEvent.WaitOne())
                    Dispatcher.BeginInvoke(new Action(ShowPopupFromExternal));
            }
            catch
            {
                // The handle is disposed on Quit, which unblocks us; just exit.
            }
        })
        {
            IsBackground = true,
            Name = "ShowPopupListener",
        };
        thread.Start();
    }

    /// <summary>Tell the already-running instance to surface its popup, by setting
    /// the named event its listener thread waits on.</summary>
    private static void SignalExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowPopupEventName, out var ev))
            {
                ev.Set();
                ev.Dispose();
            }
        }
        catch
        {
            // The other instance may be mid-startup/shutdown, so there is nothing to do.
        }
    }

    private void ShowPopupFromExternal()
    {
        _popup ??= CreatePopup();
        _popup.EnsureShown(_cfg, _root);
    }

    /// <summary>Runs on the UI thread after each poll.</summary>
    private void OnUpdated(Config cfg, UsageJsonRoot root)
    {
        _cfg = cfg;
        _root = root;

        var rendered = Renderer.Render(root, cfg, DateTimeOffset.UtcNow);
        _tray.Update(rendered.Severity, rendered.Tooltip, rendered.Percent);

        // Only the popup rebuilds live. The settings form is intentionally not
        // refreshed on every poll, which would clobber unsaved edits. It is
        // repopulated when opened and again right after a save.
        _popup?.Refresh(cfg, root);
    }

    private void OnTrayClicked()
    {
        _popup ??= CreatePopup();
        _popup.Toggle(_cfg, _root);
    }

    private PopupWindow CreatePopup()
    {
        var p = new PopupWindow();
        p.RefreshRequested += () => _poller.TriggerRefresh();
        p.SettingsRequested += OpenSettings;
        p.QuitRequested += Quit;
        return p;
    }

    private void OpenSettings()
    {
        _popup?.HidePopup();
        _settings ??= CreateSettings();
        _settings.ShowWith(_cfg, _root);
    }

    private SettingsWindow CreateSettings()
    {
        var s = new SettingsWindow();
        s.Saved += () => _poller.TriggerRefresh();
        return s;
    }

    private void Quit()
    {
        _tray.Dispose();
        _poller.Dispose();
        _showEvent?.Dispose();
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        Shutdown();
    }
}
