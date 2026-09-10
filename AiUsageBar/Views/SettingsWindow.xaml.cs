using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AiUsageBar.Models;
using AiUsageBar.Services;
// Wpf.Ui.Controls is deliberately NOT imported: it redefines TextBlock,
// PasswordBox and other names that collide with System.Windows.Controls. The two
// WPF-UI types this file needs are written out in full instead.

namespace AiUsageBar.Views;

public partial class SettingsWindow : Wpf.Ui.Controls.FluentWindow
{
    public event Action? Saved;

    private UsageJsonRoot _root = new();
    private SettingsModel _model = new();

    private CliSettings.Snapshot? _snapshot;

    /// <summary>Password box per vendor id, so Save can read what was typed. A
    /// PasswordBox cannot be data-bound (WPF deliberately does not expose the
    /// value as a dependency property), so rows are built and tracked by hand.</summary>
    private readonly Dictionary<string, PasswordBox> _keyBoxes = new();

    /// <summary>Vendors the user asked to clear before saving.</summary>
    private readonly HashSet<string> _pendingClears = new();

    public SettingsWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    public void ShowWith(Config cfg, UsageJsonRoot root)
    {
        _root = root;

        // Everything cheap first, then the window, then the CLI. Reading the
        // provider list costs a process launch, and doing that before Show()
        // meant the click on Settings produced nothing at all on screen until
        // the CLI exited, with the tray frozen alongside it because the whole
        // app shares this thread.
        PopulateLocal(cfg);
        Show();
        Activate();

        ShowVersions();
        _ = LoadProvidersAsync();
    }

    /// <summary>Fills the footer label with both version numbers.
    ///
    /// <para>The app's is immediate; the CLI's costs a process launch, so it is
    /// awaited rather than blocking the window on it, and lands a moment later.
    /// <see cref="VersionInfo.CliAsync"/> caches, so reopening this window never
    /// pays that cost twice.</para></summary>
    private async void ShowVersions()
    {
        // async void because this is fire and forget from a UI entry point. The
        // try/catch is what keeps that safe: an exception escaping here would go
        // unobserved and take the process down.
        try
        {
            VersionLabel.Text = "App " + VersionInfo.App;
            VersionLabel.ToolTip = "Running: " + CliBinary.Executable;

            var cli = await VersionInfo.CliAsync();
            VersionLabel.Text = $"App {VersionInfo.App}  ·  CLI {cli}";
        }
        catch (Exception)
        {
            VersionLabel.Text = "App " + VersionInfo.App;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    /// <summary>The half of the form that needs nothing but local state.</summary>
    private void PopulateLocal(Config cfg)
    {
        _model = Renderer.SettingsModel(cfg, _root);
        PollBox.Value = _model.PollSeconds;
        StartupBox.IsChecked = StartupService.IsEnabled();

        SaveErrorLabel.Visibility = Visibility.Collapsed;
        _pendingClears.Clear();
        _keyBoxes.Clear();
        KeysPanel.Children.Clear();
        _snapshot = null;

        KeysUnavailableLabel.Text = "Reading providers from the CLI...";
        KeysUnavailableLabel.Visibility = Visibility.Visible;

        // Saving before the provider list arrives would write a primary chosen
        // from an empty combo box.
        SaveButton.IsEnabled = false;
    }

    /// <summary>The half that needs the CLI, off the UI thread.
    ///
    /// <para>The CLI is the authority on which vendors exist and which of them
    /// may lead the tooltip. The usage entries are the fallback, so the window
    /// still works on a broken install.</para></summary>
    private async Task LoadProvidersAsync()
    {
        try
        {
            _snapshot = await CliSettings.LoadAsync();
        }
        catch (Exception)
        {
            _snapshot = null;
        }

        PopulatePrimary();
        PopulateKeys();
        SaveButton.IsEnabled = true;
    }

    private void PopulatePrimary()
    {
        PrimaryBox.Items.Clear();

        var choices = _snapshot?.PrimaryChoices;
        if (choices is { Count: > 0 })
        {
            foreach (var choice in choices) PrimaryBox.Items.Add(choice.Label);

            var index = choices.FindIndex(c => c.Id == _model.Primary);
            PrimaryBox.SelectedIndex = index >= 0 ? index : 0;
            return;
        }

        foreach (var entry in _root.Entries) PrimaryBox.Items.Add(entry.DisplayName);
        var fallback = _root.Entries.FindIndex(e => e.Id == _model.Primary);
        PrimaryBox.SelectedIndex = fallback >= 0 ? fallback : 0;
    }

    private void PopulateKeys()
    {
        KeysPanel.Children.Clear();
        _keyBoxes.Clear();

        var keys = _snapshot?.Keys;
        if (keys is not { Count: > 0 })
        {
            KeysUnavailableLabel.Text = "The CLI did not report its settings, so keys cannot be edited here.";
            KeysUnavailableLabel.Visibility = Visibility.Visible;
            return;
        }

        KeysUnavailableLabel.Visibility = Visibility.Collapsed;
        foreach (var key in keys) KeysPanel.Children.Add(BuildKeyRow(key));
    }

    private UIElement BuildKeyRow(CliSettings.VendorKey key)
    {
        // Two columns, label then field. Inside a plain StackPanel the box was
        // collapsing to its minimum width, so the field column stretches instead
        // of relying on a fixed Width.
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock
        {
            Text = key.Label,
            FontSize = 12,
            Foreground = (Brush)FindResource("TextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var state = DescribeState(key);
        if (state != null)
        {
            labels.Children.Add(new TextBlock
            {
                Text = state,
                FontSize = 10,
                Opacity = 0.7,
                Foreground = (Brush)FindResource("MutedBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = state,
            });
        }

        Grid.SetColumn(labels, 0);
        grid.Children.Add(labels);

        var field = new StackPanel { Orientation = Orientation.Horizontal };

        var box = new PasswordBox
        {
            MinWidth = 180,
            Height = 28,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        box.ToolTip = BuildTooltip(key);
        _keyBoxes[key.Id] = box;
        field.Children.Add(box);

        // Only offer to erase a key stored in the config file. One coming from an
        // environment variable is not ours to remove.
        if (key.InlineConfigured)
        {
            var clear = new Wpf.Ui.Controls.Button
            {
                Content = "Clear",
                Margin = new Thickness(6, 0, 0, 0),
                Height = 28,
                Tag = key.Id,
            };
            clear.Click += OnClearKey;
            field.Children.Add(clear);
        }

        Grid.SetColumn(field, 1);
        grid.Children.Add(field);

        return grid;
    }

    /// <summary>Second line under the vendor name, or null when there is nothing
    /// worth saying. An unconfigured provider gets no line at all: printing the
    /// environment variable eleven times crowded the column and truncated. That
    /// hint lives in the field's tooltip instead.</summary>
    private static string? DescribeState(CliSettings.VendorKey key)
    {
        if (key.EnvironmentConfigured) return "from " + key.Environment;
        if (key.InlineConfigured) return "saved";
        return null;
    }

    /// <summary>Everything the row cannot show inline: what the key is for, and
    /// which environment variable can supply it.</summary>
    private static string? BuildTooltip(CliSettings.VendorKey key)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(key.Note)) parts.Add(key.Note.Trim());
        if (!string.IsNullOrWhiteSpace(key.Environment)) parts.Add("Can also come from " + key.Environment + ".");
        return parts.Count == 0 ? null : string.Join("  ", parts);
    }

    private void OnClearKey(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button button) return;
        if (button.Tag is not string vendorId) return;

        _pendingClears.Add(vendorId);
        if (_keyBoxes.TryGetValue(vendorId, out var box)) box.Clear();

        ShowMessage(vendorId + ": key will be removed when you save.");
    }

    private void OnOpenConfig(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Config.DefaultPath();
            if (path != null)
            {
                var dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                if (!System.IO.File.Exists(path)) System.IO.File.WriteAllText(path, "");
                Process.Start("notepad.exe", path);
            }
        }
        catch { }
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        // async void because this is a click handler. Everything inside is
        // guarded, so nothing escapes to the dispatcher.
        var cfg = Config.Load();
        cfg.PollSeconds = PollBox.Value is double d ? (long)d : 60;

        var primaryId = SelectedPrimaryId();
        if (primaryId != null) cfg.Ui.Primary = primaryId;

        // Disabled while the CLI runs: the call is off the UI thread now, so
        // without this the button stays live and a second click would fire a
        // competing write of the same file.
        SaveButton.IsEnabled = false;
        try
        {
            // Vendor keys and the primary go through the CLI, which owns that
            // file and rewrites it without losing comments. We only write what it
            // knows nothing about. Bail out before touching anything else if it
            // refuses the patch.
            string? error;
            try
            {
                error = await CliSettings.ApplyAsync(primaryId, CollectKeyChanges());
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            if (error != null)
            {
                ShowMessage("Could not save provider settings: " + error);
                return;
            }

            try
            {
                await Task.Run(cfg.Save);
            }
            catch (Exception ex)
            {
                ShowMessage("Could not save app settings: " + ex.Message);
                return;
            }

            StartupService.SetEnabled(StartupBox.IsChecked == true);

            Saved?.Invoke();
            Hide();
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private string? SelectedPrimaryId()
    {
        var index = PrimaryBox.SelectedIndex;
        if (index < 0) return null;

        var choices = _snapshot?.PrimaryChoices;
        if (choices is { Count: > 0 }) return index < choices.Count ? choices[index].Id : null;

        return index < _root.Entries.Count ? _root.Entries[index].Id : null;
    }

    /// <summary>A blank box means "leave it alone", so only typed values and
    /// explicit clears become changes. That is what keeps a save from wiping
    /// keys the window never had the values for.</summary>
    private List<CliSettings.KeyChange> CollectKeyChanges()
    {
        var changes = new List<CliSettings.KeyChange>();

        foreach (var vendorId in _pendingClears)
        {
            changes.Add(new CliSettings.KeyChange(vendorId, true, null));
        }

        foreach (var pair in _keyBoxes)
        {
            var typed = pair.Value.Password;
            if (string.IsNullOrWhiteSpace(typed)) continue;
            if (_pendingClears.Contains(pair.Key)) continue;

            changes.Add(new CliSettings.KeyChange(pair.Key, false, typed.Trim()));
        }

        return changes;
    }

    private void ShowMessage(string message)
    {
        SaveErrorLabel.Text = message;
        SaveErrorLabel.Visibility = Visibility.Visible;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Hide();
}
