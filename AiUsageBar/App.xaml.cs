using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
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

    /// <summary>Whether a poll is running right now.</summary>
    private bool _busy;

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

        // Last line of defence. A tray app that vanishes leaves the user with no
        // window, no message and nothing to report: an unhandled exception on the
        // dispatcher used to do exactly that. Staying alive with a visibly broken
        // icon is strictly better than disappearing.
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // These two cannot keep the process alive, but they can leave a stack
        // trace behind. The crash that prompted them left only a WER entry.
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        StartShowPopupListener();

        // Never throws, even when the shell is not ready for the icon yet, so
        // the poller below always starts.
        _tray = new TrayService(Dispatcher);
        _tray.Clicked += OnTrayClicked;
        _tray.Init();

        _poller = new Poller(Dispatcher);
        _poller.Updated += OnUpdated;
        _poller.BusyChanged += OnBusyChanged;
        _poller.Start();

        // Housekeeping that nobody is waiting on, moved off the startup path.
        // The shortcut check instantiates a shell COM object and reads a .lnk,
        // and the popup costs its BAML parse and visual tree on first use, which
        // is what made the very first tray click of a session feel slow.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            try
            {
                ShortcutService.EnsureStartMenuShortcut();
                StartupService.RepairIfMoved();
                _popup ??= CreatePopup();
            }
            catch (Exception)
            {
                // None of this is worth failing a launch over.
            }
        });
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        AppLog.Error("dispatcher", e.Exception);

        try
        {
            _tray?.Update(Severity.Unknown, "ai-usagebar - internal error\n" + e.Exception.Message);
        }
        catch (Exception)
        {
            // The tray is the only reporting surface there is. If even that
            // fails, there is nothing further to try.
        }
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            AppLog.Error(e.IsTerminating ? "unhandled, process terminating" : "unhandled", ex);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Error("unobserved task", e.Exception);
        e.SetObserved();
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
        _popup.SetBusy(_busy);
        _popup.EnsureShown(_cfg, _root);
    }

    /// <summary>Runs on the UI thread when a poll starts or ends.</summary>
    private void OnBusyChanged(bool busy)
    {
        _busy = busy;
        _popup?.SetBusy(busy);
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
        _popup.SetBusy(_busy);
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
        // Poller first. It marshals updates onto the dispatcher, so disposing the
        // tray icon before stopping it left a poll in flight able to call Update
        // on an already-disposed icon.
        _poller.Dispose();
        _tray.Dispose();
        _showEvent?.Dispose();

        // Only the thread that created the mutex owns it. Quit always runs on
        // that thread today, but a release that throws here would skip the
        // Shutdown below and leave a process with no icon behind.
        try { _instanceMutex?.ReleaseMutex(); }
        catch (ApplicationException) { }
        _instanceMutex?.Dispose();
        Shutdown();
    }
}
