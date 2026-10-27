using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using NAudio.CoreAudioApi;

namespace MultiSpeaker;

/// <summary>One output device in the list: whether it's used, its volume and its delay.</summary>
sealed class SpeakerRow : INotifyPropertyChanged, IDisposable
{
    static readonly Guid VolumeContext = Guid.NewGuid();  // tags volume changes made by this app

    readonly Dispatcher dispatcher;
    readonly AudioEndpointVolume? volume;
    // Each change can be a Bluetooth round trip, so apply at most every 80 ms while a button is held.
    readonly DispatcherTimer volumeApplyTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    bool isChecked;
    bool isSource;
    bool updatingFromDevice;
    int volumePercent;
    int delayMs;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? CheckedChanged;
    /// <summary>Volume changed, from the box or from outside (e.g. the speaker's buttons or keyboard volume keys).</summary>
    public event Action<SpeakerRow>? VolumeChanged;

    public MMDevice Device { get; }
    public string Name => Device.FriendlyName;
    public SpeakerOutput? Output { get; set; }
    public bool HasVolume => volume != null;
    public bool CanEditDelay => !isSource;

    public SpeakerRow(MMDevice device, Dispatcher dispatcher)
    {
        Device = device;
        this.dispatcher = dispatcher;
        volumeApplyTimer.Tick += (_, _) => ApplyVolume();
        try
        {
            volume = device.AudioEndpointVolume;
            volume.NotificationGuid = VolumeContext;
            volumePercent = ToPercent(volume.MasterVolumeLevelScalar);
            volume.OnVolumeNotification += OnVolumeNotification;
        }
        catch (COMException)
        {
            volume = null;
        }
    }

    public bool IsChecked
    {
        get => isChecked;
        set
        {
            if (Set(ref isChecked, value))
                CheckedChanged?.Invoke();
        }
    }

    /// <summary>The source already plays the sound itself, so it can't be ticked or delayed.</summary>
    public bool IsSource
    {
        get => isSource;
        set
        {
            if (Set(ref isSource, value))
                OnPropertyChanged(nameof(CanEditDelay));
        }
    }

    /// <summary>
    /// The speaker's own Windows volume (for Bluetooth usually its hardware volume). Each speaker keeps
    /// its own level, so a non-default one is often left low.
    /// </summary>
    public int Volume
    {
        get => volumePercent;
        set
        {
            if (!Set(ref volumePercent, Math.Clamp(value, 0, 100)))
                return;
            if (!updatingFromDevice && !volumeApplyTimer.IsEnabled)
                volumeApplyTimer.Start();
            VolumeChanged?.Invoke(this);
        }
    }

    /// <summary>
    /// Changes the volume by a number of decibels. Returns false if the speaker's limit stopped it
    /// short. Needed by auto sync, which measures loudness ratios rather than percentages.
    /// </summary>
    public bool ChangeVolumeByDb(double db)
    {
        if (volume == null)
            return false;
        try
        {
            var range = volume.VolumeRange;
            double wanted = volume.MasterVolumeLevel + db;
            float clamped = (float)Math.Clamp(wanted, range.MinDecibels, range.MaxDecibels);
            if (volume.Mute)
                volume.Mute = false;
            volume.MasterVolumeLevel = clamped;

            updatingFromDevice = true;  // already applied; just show it
            Volume = ToPercent(volume.MasterVolumeLevelScalar);
            updatingFromDevice = false;
            return Math.Abs(clamped - wanted) < 0.1;
        }
        catch (COMException)
        {
            return false;
        }
    }

    public int DelayMs
    {
        get => delayMs;
        set
        {
            if (Set(ref delayMs, Math.Clamp(value, 0, SpeakerOutput.MaxDelayMs)) && Output != null)
                Output.DelayMs = delayMs;
        }
    }

    void ApplyVolume()
    {
        volumeApplyTimer.Stop();
        try
        {
            if (volume!.Mute)
                volume.Mute = false;
            volume.MasterVolumeLevelScalar = volumePercent / 100f;
        }
        catch (COMException) { }  // device just went away
    }

    /// <summary>Follows changes made elsewhere, e.g. the speaker's own volume buttons. Runs on a COM thread.</summary>
    void OnVolumeNotification(AudioVolumeNotificationData data)
    {
        if (data.EventContext == VolumeContext)
            return;  // echo of our own change; reacting to it makes the box fight the user
        dispatcher.BeginInvoke(() =>
        {
            updatingFromDevice = true;
            Volume = ToPercent(data.MasterVolume);
            updatingFromDevice = false;
        });
    }

    static int ToPercent(float scalar) => (int)Math.Round(scalar * 100);

    bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        volumeApplyTimer.Stop();
        if (volume != null)
            volume.OnVolumeNotification -= OnVolumeNotification;
        Device.Dispose();
    }
}
