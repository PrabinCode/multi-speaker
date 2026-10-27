using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Forms = System.Windows.Forms;

namespace MultiSpeaker;

public partial class MainWindow : Window
{
    sealed record SourceItem(MMDevice Device, string Name)
    {
        public override string ToString() => Name;  // shown in the dropdown's selection box
    }

    readonly MMDeviceEnumerator enumerator = new();
    readonly AppSettings settings = AppSettings.Load();
    readonly ObservableCollection<SpeakerRow> rows = [];
    readonly ObservableCollection<SourceItem> sources = [];
    readonly Forms.NotifyIcon tray = new() { Text = "MultiSpeaker" };
    readonly Forms.ToolStripMenuItem trayStartItem = new("Start");
    // Bluetooth connects fire several notifications in a burst; act once they settle.
    readonly DispatcherTimer deviceSettleTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    // Follows the source's volume while playing, for speakers whose volume Windows applies in software.
    readonly DispatcherTimer compensationTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DeviceWatcher watcher;

    AudioRouter? router;
    bool wantRunning;    // user pressed Start; keep trying to play across device changes
    bool outputFailed;   // a speaker or the capture stopped with an error
    bool loading;
    bool darkMode;
    bool calibrating;    // auto sync is changing volumes itself; don't link them
    CancellationTokenSource? calibrationCancel;  // lets Stop interrupt Sync delay / Match volume
    bool applyingLink;
    bool resettingChannel;  // putting a source back to Both because its balance can't be used
    // Volumes captured when linking started; linked changes scale these so ratios survive hitting 0 or 100 %.
    readonly Dictionary<SpeakerRow, double> linkBase = [];
    string deviceSignature = "";

    public MainWindow()
    {
        darkMode = settings.DarkMode ?? ThemeManager.SystemPrefersDark();
        ThemeManager.Apply(darkMode);  // before the controls are created so they pick up the theme
        InitializeComponent();
        VersionText.Text = $"({AppVersion()})";
        UpdateThemeButton();
        SpeakerList.ItemsSource = rows;
        SourceBox.ItemsSource = sources;
        LinkVolumesBox.IsChecked = settings.LinkVolumes;

        using (var icon = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/MultiSpeaker.ico")).Stream)
            tray.Icon = new System.Drawing.Icon(icon, Forms.SystemInformation.SmallIconSize);
        var trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.Add("Open", null, (_, _) => ShowFromTray());
        trayMenu.Items.Add(trayStartItem);
        trayMenu.Items.Add("Exit", null, (_, _) => Close());
        trayStartItem.Click += (_, _) => ToggleRunning();
        tray.ContextMenuStrip = trayMenu;
        tray.DoubleClick += (_, _) => ShowFromTray();
        tray.Visible = true;

        deviceSettleTimer.Tick += (_, _) => { deviceSettleTimer.Stop(); OnDevicesSettled(); };
        compensationTimer.Tick += (_, _) => UpdateSourceCompensation();
        SourceInitialized += (_, _) => ThemeManager.ApplyToWindow(this, darkMode);
        StateChanged += (_, _) => OnWindowStateChanged();
        Closing += (_, _) => CleanUp();

        LoadDevices();
        RestoreSourceBalance();  // in case the last session ended without stopping (e.g. a crash)
        deviceSignature = DeviceSignature();
        watcher = new DeviceWatcher(() => RunOnUi(RestartSettleTimer));
        enumerator.RegisterEndpointNotificationCallback(watcher);
    }

    // ---- Title bar ----

    public const string RepoUrl = "https://github.com/PrabinCode/multi-speaker";

    void OnRepoLinkClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(RepoUrl) { UseShellExecute = true });

    /// <summary>"v1.2.3" for release builds, "dev" for local ones.</summary>
    static string AppVersion()
    {
        string? version = typeof(MainWindow).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion.Split('+')[0];  // drop the "+commit" suffix
        return version is null || version.Contains("-dev") ? "dev" : $"v{version}";
    }

    HelpWindow? helpWindow;

    void OnHelpClick(object sender, RoutedEventArgs e)
    {
        if (helpWindow == null)
        {
            helpWindow = new HelpWindow(this, darkMode);
            helpWindow.Closed += (_, _) => helpWindow = null;
            helpWindow.Show();
        }
        else
        {
            helpWindow.Activate();
        }
    }

    void OnThemeClick(object sender, RoutedEventArgs e)
    {
        darkMode = !darkMode;
        settings.DarkMode = darkMode;
        ThemeManager.Apply(darkMode);
        ThemeManager.ApplyToWindow(this, darkMode);
        if (helpWindow != null)
            ThemeManager.ApplyToWindow(helpWindow, darkMode);
        UpdateThemeButton();
    }

    void UpdateThemeButton()
    {
        ThemeButton.Content = darkMode ? "" : "";  // sun / moon
        ThemeButton.ToolTip = darkMode ? "Switch to light theme" : "Switch to dark theme";
    }

    void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    void OnWindowStateChanged()
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();  // live in the tray
            return;
        }

        bool maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "" : "";
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
        // A maximized custom-chrome window hangs off-screen by its resize border; pad it back in.
        var border = SystemParameters.WindowResizeBorderThickness;
        RootGrid.Margin = maximized
            ? new Thickness(border.Left + 4, border.Top + 4, border.Right + 4, border.Bottom + 4)
            : new Thickness(0);
    }

    void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    // ---- Devices ----

    void LoadDevices()
    {
        if (rows.Count > 0)
            SaveSettings();

        loading = true;
        foreach (var row in rows)
            row.Dispose();
        rows.Clear();
        sources.Clear();

        // Each FriendlyName read walks the device's whole property store through COM, which can take
        // a long time with some Bluetooth drivers, so read it once per device.
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => (Device: d, Name: d.FriendlyName))
            .OrderBy(d => d.Name);
        foreach (var (device, name) in devices)
        {
            var saved = settings.Speakers.GetValueOrDefault(device.ID);
            var row = new SpeakerRow(device, name, Dispatcher)
            {
                IsChecked = saved?.Enabled ?? false,
                DelayMs = saved?.DelayMs ?? 0,
                Channel = saved?.Channel ?? ChannelMode.Both,
            };
            row.CheckedChanged += OnSpeakerToggled;
            row.ChannelChanged += OnChannelChanged;
            row.VolumeChanged += OnRowVolumeChanged;
            rows.Add(row);
            sources.Add(new SourceItem(device, name));
        }

        // The source is always the Windows default output, so the dropdown mirrors it.
        string? defaultId = DefaultOutputId();
        SourceBox.SelectedItem = sources.FirstOrDefault(s => s.Device.ID == defaultId);
        loading = false;
        UpdateSourceMarker();
    }

    string? DefaultOutputId()
    {
        try { return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; }
        catch (COMException) { return null; }  // no output device at all
    }

    MMDevice ResolveSource() =>
        (SourceBox.SelectedItem as SourceItem)?.Device
        ?? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

    void UpdateSourceMarker()
    {
        string? sourceId = null;
        try { sourceId = ResolveSource().ID; }
        catch (COMException) { }  // no default output device

        foreach (var row in rows)
            row.IsSource = row.Device.ID == sourceId;
        ResetLinkBase();
    }

    // ---- Linked volumes ----

    /// <summary>Speakers whose volumes move together: the source plus every ticked speaker.</summary>
    IEnumerable<SpeakerRow> LinkGroup() => rows.Where(r => r.HasVolume && (r.IsSource || r.IsChecked));

    void ResetLinkBase()
    {
        linkBase.Clear();
        foreach (var row in LinkGroup())
            linkBase[row] = row.Volume;
    }

    void OnLinkVolumesChanged(object sender, RoutedEventArgs e)
    {
        settings.LinkVolumes = LinkVolumesBox.IsChecked == true;
        ResetLinkBase();
    }

    void OnRowVolumeChanged(SpeakerRow changed)
    {
        if (LinkVolumesBox.IsChecked != true || applyingLink || calibrating)
            return;
        if (!linkBase.TryGetValue(changed, out double baseVolume) || baseVolume <= 0)
        {
            ResetLinkBase();  // nothing to scale from (e.g. it was at 0 %); start a new ratio
            return;
        }

        double scale = changed.Volume / baseVolume;
        applyingLink = true;
        try
        {
            foreach (var (row, rowBase) in linkBase)
                if (row != changed)
                    row.Volume = (int)Math.Round(Math.Clamp(rowBase * scale, 0, 100));
        }
        finally
        {
            applyingLink = false;
        }
    }

    void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading || SourceBox.SelectedItem is not SourceItem item || item.Device.ID == DefaultOutputId())
            return;

        // Make it the laptop's sound output, so apps actually play to the device we copy from.
        try
        {
            DefaultAudioDevice.Set(item.Device.ID);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't make {item.Name} the Windows sound output.\n\n{ex.Message}",
                "Sound source", MessageBoxButton.OK, MessageBoxImage.Warning);
            loading = true;
            SourceBox.SelectedItem = sources.FirstOrDefault(s => s.Device.ID == DefaultOutputId());
            loading = false;
            return;
        }

        // Handled here right away; the device notification that follows sees nothing new.
        deviceSignature = DeviceSignature();
        UpdateSourceMarker();
        if (wantRunning)
            Restart();
    }

    void OnSpeakerToggled()
    {
        if (loading)
            return;
        ResetLinkBase();
        if (wantRunning)
            Restart();
    }

    /// <summary>Extra speakers switch sides live; the source needs its balance re-applied.</summary>
    void OnChannelChanged(SpeakerRow row)
    {
        if (!loading && !resettingChannel && row.IsSource && wantRunning)
            Restart();
    }

    // ---- Source balance ----

    /// <summary>Splits the source speaker with its Windows balance, after checking once that it's safe.</summary>
    void ApplySourceBalance(SpeakerRow source)
    {
        if (!settings.BalanceSafe.TryGetValue(source.Device.ID, out bool safe))
        {
            bool? probed = SourceBalance.Probe(source.Device);
            if (probed is null)
            {
                StatusText.Text = $"Turn up {source.Name} to use {source.Channel} only";
                return;
            }
            safe = probed.Value;
            settings.BalanceSafe[source.Device.ID] = safe;
        }

        if (!safe)
        {
            resettingChannel = true;
            source.Channel = ChannelMode.Both;
            resettingChannel = false;
            MessageBox.Show(this,
                $"{source.Name} can't play only one side while it's the sound source: Windows would remove that side " +
                "from the sound MultiSpeaker copies, too.\n\nMake another speaker the sound source, or keep this one on Both.",
                "Channel", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SourceBalance.Apply(source.Device, source.Channel);
        settings.BalancedDeviceId = source.Device.ID;
        settings.Save();  // so a crash can't leave the laptop playing one side
    }

    void RestoreSourceBalance()
    {
        if (settings.BalancedDeviceId is not { } id)
            return;
        try
        {
            using var device = enumerator.GetDevice(id);
            SourceBalance.Restore(device);
        }
        catch (COMException) { }  // device is gone; Windows resets it when it returns
        settings.BalancedDeviceId = null;
        settings.Save();
    }

    void OnRefreshClick(object sender, RoutedEventArgs e) => Reload();

    /// <summary>Rebuilds the device list and resumes playback if it was running.</summary>
    void Reload()
    {
        StopRouting();
        LoadDevices();
        deviceSignature = DeviceSignature();
        outputFailed = false;
        if (wantRunning)
            StartRouting(showErrors: false);
    }

    void RestartSettleTimer()
    {
        deviceSettleTimer.Stop();
        deviceSettleTimer.Start();
    }

    void OnDevicesSettled()
    {
        string signature = DeviceSignature();
        if (signature == deviceSignature && !outputFailed)
            return;  // unrelated change, e.g. a microphone
        Reload();
    }

    /// <summary>Identifies the set of active outputs plus the default, so irrelevant notifications are ignored.</summary>
    string DeviceSignature()
    {
        string defaultId = DefaultOutputId() ?? "";
        var ids = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Select(d => d.ID).Order();
        return defaultId + "|" + string.Join(";", ids);
    }

    // ---- Playback ----

    void OnStartClick(object sender, RoutedEventArgs e) => ToggleRunning();

    void ToggleRunning()
    {
        calibrationCancel?.Cancel();  // Stop also ends a measurement in progress
        wantRunning = !wantRunning;
        if (wantRunning)
            StartRouting(showErrors: true);
        else
            StopRouting();
        UpdateRunningUi();
    }

    void UpdateRunningUi()
    {
        StartButton.Content = wantRunning ? "Stop" : "Start";
        SyncDelayButton.IsEnabled = router != null;
        MatchVolumeButton.IsEnabled = router != null;
        trayStartItem.Text = wantRunning ? "Stop" : "Start";
        if (!wantRunning)
            StatusText.Text = "Stopped";
        string tip = $"MultiSpeaker - {StatusText.Text}";
        tray.Text = tip.Length > 63 ? tip[..63] : tip;  // NotifyIcon rejects longer text
    }

    void Restart()
    {
        StopRouting();
        StartRouting(showErrors: false);
    }

    void StartRouting(bool showErrors)
    {
        try
        {
            var source = ResolveSource();
            var targets = rows.Where(r => r.IsChecked && r.Device.ID != source.ID).ToList();
            if (targets.Count == 0)
            {
                StatusText.Text = "Tick at least one speaker";
                return;
            }

            router = new AudioRouter(source);
            router.CaptureStopped += ex => OnAudioError("Source", ex);
            foreach (var row in targets)
            {
                row.Output = router.AddSpeaker(row.Device, row.Name, row.DelayMs);
                row.Output.Channel = row.Channel;
                row.Output.Stopped += (output, ex) => OnAudioError(output.Name, ex);
            }
            UpdateSourceCompensation();
            router.Start();
            compensationTimer.Start();
            if (rows.FirstOrDefault(r => r.IsSource) is { Channel: not ChannelMode.Both } sourceRow)
                ApplySourceBalance(sourceRow);
            StatusText.Text = $"Playing on {targets.Count} extra speaker(s)";
        }
        catch (Exception ex)
        {
            StopRouting();
            if (showErrors)
            {
                wantRunning = false;
                MessageBox.Show(this, ex.Message, "Could not start", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                // Typically a speaker mid-reconnect; the next device notification retries.
                StatusText.Text = $"Waiting for devices: {ex.Message}";
            }
        }
        finally
        {
            UpdateRunningUi();
        }
    }

    void OnSyncDelayClick(object sender, RoutedEventArgs e) => RunCalibration(syncDelay: true);

    void OnMatchVolumeClick(object sender, RoutedEventArgs e) => RunCalibration(syncDelay: false);

    /// <summary>Measures every playing speaker with the microphone, then fixes either its delay or its volume.</summary>
    async void RunCalibration(bool syncDelay)
    {
        var targets = rows.Where(r => r.Output != null).ToList();
        if (router == null || targets.Count == 0)
            return;

        string title = syncDelay ? "Sync delay" : "Match volume";
        using var cancel = new CancellationTokenSource();
        calibrationCancel = cancel;
        SetCalibrationLock(true);
        calibrating = true;
        try
        {
            var calibrator = new SyncCalibrator(
                ResolveSource(),
                targets.Select(r => r.Output!).ToList(),
                (output, db) => targets.First(r => r.Output == output).ChangeVolumeByDb(db),
                new Progress<string>(message => StatusText.Text = message));
            var results = await calibrator.RunAsync(matchVolume: !syncDelay, cancel.Token);

            var report = new List<string>();
            foreach (var result in results)
            {
                var row = targets.First(r => r.Output == result.Speaker);
                if (result.OffsetMs is not int offset)
                    report.Add($"{row.Name}: couldn't hear it. Turn it up, or adjust it by ear.");
                else if (syncDelay)
                    report.Add($"{row.Name}: {ApplyDelay(row, offset)}");
                else
                    report.Add($"{row.Name}: {DescribeVolume(row, result)}");
            }
            if (!syncDelay)
                ResetLinkBase();  // the matched volumes are the new ratio to keep
            StatusText.Text = $"{title} finished";
            MessageBox.Show(this, string.Join("\n\n", report), title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            // Stop was pressed; ToggleRunning already updated the status
        }
        catch (Exception ex)
        {
            StatusText.Text = $"{title} failed";
            MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            calibrating = false;
            calibrationCancel = null;
            SetCalibrationLock(false);
        }
    }

    /// <summary>While measuring, lock everything except Stop, which cancels the measurement.</summary>
    void SetCalibrationLock(bool locked)
    {
        SourceBox.IsEnabled = !locked;
        SpeakerCard.IsEnabled = !locked;
        LinkVolumesBox.IsEnabled = !locked;
        RefreshButton.IsEnabled = !locked;
        SyncDelayButton.IsEnabled = !locked && router != null;
        MatchVolumeButton.IsEnabled = !locked && router != null;
    }

    static string ApplyDelay(SpeakerRow row, int offsetMs)
    {
        int wanted = row.DelayMs - offsetMs;
        row.DelayMs = wanted;  // clamped to 0..max by the row
        return wanted < 0
            ? $"still {-wanted} ms late at 0 ms delay. Choose it as the sound source instead (set it as the Windows default)."
            : $"delay set to {row.DelayMs} ms (it was {Math.Abs(offsetMs)} ms {(offsetMs > 0 ? "late" : "early")}).";
    }

    static string DescribeVolume(SpeakerRow row, SyncCalibrator.Result result) => result.LevelDb switch
    {
        null => "couldn't measure its loudness. Adjust it by ear.",
        <= 3 and >= -3 => $"volume matched at {row.Volume} %.",
        > 0 when result.VolumeLimited => $"lowered to {row.Volume} %, the lowest Match volume goes, but it's still {result.LevelDb:0} dB louder. " +
            "Is it much closer to the laptop than the source speaker? Measure from where you sit, or adjust it by ear.",
        < 0 when result.VolumeLimited => $"raised to {row.Volume} % but it's still {-result.LevelDb:0} dB quieter. " +
            "Lower the source speaker a little and run Match volume again. (Using the laptop's own speakers as the source? They're right next to the microphone, so they always sound loudest to it.)",
        _ => $"set to {row.Volume} %, still {Math.Abs(result.LevelDb.Value):0} dB {(result.LevelDb > 0 ? "louder" : "quieter")}. Run Match volume again or adjust it by ear.",
    };

    /// <summary>Undo the source's software volume on the copies (see SpeakerRow.LoopbackCompensation).</summary>
    void UpdateSourceCompensation()
    {
        if (router != null)
            router.Gain = rows.FirstOrDefault(r => r.IsSource)?.LoopbackCompensation() ?? 1f;
    }

    void StopRouting()
    {
        compensationTimer.Stop();
        RestoreSourceBalance();
        router?.Dispose();
        router = null;
        foreach (var row in rows)
            row.Output = null;
    }

    /// <summary>Called from audio threads; a null exception means a normal stop.</summary>
    void OnAudioError(string what, Exception? ex)
    {
        if (ex == null)
            return;
        RunOnUi(() =>
        {
            StatusText.Text = $"{what} stopped: {ex.Message}";
            outputFailed = true;
            RestartSettleTimer();
        });
    }

    void RunOnUi(Action action)
    {
        if (!Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(action);
    }

    void CleanUp()
    {
        enumerator.UnregisterEndpointNotificationCallback(watcher);
        deviceSettleTimer.Stop();
        StopRouting();
        SaveSettings();
        foreach (var row in rows)
            row.Dispose();
        tray.Visible = false;
        tray.Dispose();
    }

    void SaveSettings()
    {
        foreach (var row in rows)
            settings.Speakers[row.Device.ID] = new SpeakerSettings { Enabled = row.IsChecked, DelayMs = row.DelayMs, Channel = row.Channel };
        settings.Save();
    }
}
