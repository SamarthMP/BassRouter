using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System.Runtime.InteropServices;

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

    /// <summary>
    /// Raised when the default render device changes away from a supported virtual device.
    /// </summary>
    public event EventHandler? DefaultDeviceBecameNonVirtual;

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
            using var enumerator = new MMDeviceEnumerator();
            return enumerator
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
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
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
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(deviceId);
            return device.State == DeviceState.Active ? device : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public AudioOutputDevice? GetFirstVirtualOutputDevice()
    {
        try
        {
            MMDevice? device = GetOutputDevices().FirstOrDefault(IsVirtualDevice);
            return device == null ? null : new AudioOutputDevice(device.ID, device.FriendlyName);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void SetDefaultRenderDevice(string deviceId)
    {
        Type policyConfigType = Type.GetTypeFromCLSID(new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9"))
            ?? throw new InvalidOperationException("PolicyConfig COM type is unavailable.");

        var policyConfig = (IPolicyConfig)Activator.CreateInstance(policyConfigType)!;

        Marshal.ThrowExceptionForHR(policyConfig.SetDefaultEndpoint(deviceId, Role.Console));
        Marshal.ThrowExceptionForHR(policyConfig.SetDefaultEndpoint(deviceId, Role.Multimedia));
        Marshal.ThrowExceptionForHR(policyConfig.SetDefaultEndpoint(deviceId, Role.Communications));
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
        MMDevice? defaultDevice = GetDefaultRenderDevice();
        if (defaultDevice == null || !IsVirtualDevice(defaultDevice))
        {
            DefaultDeviceBecameNonVirtual?.Invoke(this, EventArgs.Empty);
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
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia)
                _owner.OnDefaultDeviceChanged();
        }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        int GetMixFormat();
        int GetDeviceFormat();
        int ResetDeviceFormat();
        int SetDeviceFormat();
        int GetProcessingPeriod();
        int SetProcessingPeriod();
        int GetShareMode();
        int SetShareMode();
        int GetPropertyValue();
        int SetPropertyValue();
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId, Role role);
        int SetEndpointVisibility();
    }
}
