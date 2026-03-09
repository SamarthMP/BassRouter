using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace BassRouter.Audio;

/// <summary>
/// Manages audio devices (who could have guessed?)
/// </summary>
public sealed class AudioDeviceManager : IDisposable
{
    private readonly MMDeviceEnumerator Enumerator;
    private readonly DeviceNotificationClient NotificationClient;
    private bool Disposed;

    /// <summary>
    /// Raised when audio devices are added, removed, or state changed.
    /// </summary>
    public event EventHandler? DevicesChanged;

    public AudioDeviceManager()
    {
        Enumerator = new MMDeviceEnumerator();
        NotificationClient = new DeviceNotificationClient(this);
        Enumerator.RegisterEndpointNotificationCallback(NotificationClient);
    }

    /// <summary>
    /// Returns all currently active audio output devices.
    /// </summary>
    public List<MMDevice> GetOutputDevices()
    {
        try
        {
            return Enumerator
                .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .ToList();
        }
        catch (Exception)
        {
            return new List<MMDevice>();
        }
    }

    /// <summary>
    /// Gets the default audio render endpoint for the system.
    /// </summary>
    public MMDevice? GetDefaultRenderDevice()
    {
        try
        {
            return Enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Finds a device by its ID string, or null if unavailable.
    /// </summary>
    public MMDevice? GetDeviceById(string deviceId)
    {
        try
        {
            var device = Enumerator.GetDevice(deviceId);
            return device.State == DeviceState.Active ? device : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool IsVirtualDevice(MMDevice device)
    {
        if (device.FriendlyName.Contains("Steam Streaming")) return true;

        return false;
    }

    private void OnDevicesChanged()
    {
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnDefaultDeviceChanged()
    {
        if (!IsVirtualDevice(GetDefaultRenderDevice()))
        {
            // Exit when the device is changed to something that's not a virtual device
            // TODO: Instead make this stop just the audio engine and display a warning
            Environment.Exit(0);
        }
    }

    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;

        try
        {
            Enumerator.UnregisterEndpointNotificationCallback(NotificationClient);
        }
        catch { }

        Enumerator.Dispose();
    }

    private sealed class DeviceNotificationClient : IMMNotificationClient
    {
        private readonly AudioDeviceManager _owner;

        public DeviceNotificationClient(AudioDeviceManager owner) => _owner = owner;

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _owner.OnDevicesChanged();
        public void OnDeviceAdded(string pwstrDeviceId) => _owner.OnDevicesChanged();
        public void OnDeviceRemoved(string deviceId) => _owner.OnDevicesChanged();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => _owner.OnDefaultDeviceChanged();
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
