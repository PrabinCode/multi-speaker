using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MultiSpeaker;

/// <summary>Forwards Windows audio device notifications (speaker connected/disconnected, default changed).</summary>
/// <remarks>Callbacks arrive on a COM worker thread and must return quickly.</remarks>
sealed class DeviceWatcher(Action changed) : IMMNotificationClient
{
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => changed();
    public void OnDeviceAdded(string pwstrDeviceId) => changed();
    public void OnDeviceRemoved(string deviceId) => changed();

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia)
            changed();
    }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
}
