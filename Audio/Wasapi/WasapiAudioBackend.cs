using BassRouter.Audio.Latency;
using NAudio.CoreAudioApi;
using System.Runtime.Versioning;

namespace BassRouter.Audio.Wasapi;

/// <summary>
/// Routes audio on Windows by capturing a virtual device via WASAPI loopback.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioBackend : IAudioBackend
{
    private readonly AudioDeviceManager DeviceManager;
    private readonly AudioEngine Engine;

    public event EventHandler? DevicesChanged;
    public event EventHandler<string>? StopRequested;

    public event EventHandler<AudioEngineStateChangedEventArgs>? StateChanged
    {
        add => Engine.StateChanged += value;
        remove => Engine.StateChanged -= value;
    }

    public event EventHandler<string>? Error
    {
        add => Engine.Error += value;
        remove => Engine.Error -= value;
    }

    public bool IsRunning => Engine.IsRunning;

    public WasapiAudioBackend()
    {
        DeviceManager = new AudioDeviceManager();
        Engine = new AudioEngine();

        DeviceManager.DevicesChanged += OnDevicesChanged;
        DeviceManager.DefaultDeviceBecameNonVirtual += OnDefaultDeviceBecameNonVirtual;
    }

    public IReadOnlyList<AudioOutputDevice> GetOutputDevices()
    {
        return DeviceManager
            .GetOutputDevices()
            .Select(device => new AudioOutputDevice(device.ID, device.FriendlyName))
            .ToArray();
    }

    public IReadOnlyList<AudioInputDevice> GetInputDevices()
    {
        string? defaultId = DeviceManager.GetDefaultCaptureDevice()?.ID;

        return DeviceManager
            .GetInputDevices()
            .Select(device => new AudioInputDevice(device.ID, device.FriendlyName, device.ID == defaultId))
            .ToArray();
    }

    public AudioEngineState GetState() => Engine.GetState();

    public void SetHeadphoneDevice(string? deviceId) => Engine.SetHeadphoneDevice(GetDevice(deviceId));

    public void SetSubDevice(string? deviceId) => Engine.SetSubDevice(GetDevice(deviceId));

    public void SetHeadphoneDelayMs(int milliseconds) => Engine.SetHeadphoneDelayMs(milliseconds);

    public void SetSubDelayMs(int milliseconds) => Engine.SetSubDelayMs(milliseconds);

    public void SetLowPassFrequency(float frequency) => Engine.SetLowPassFrequency(frequency);

    public void SetHeadphoneVolume(float volume) => Engine.SetHeadphoneVolume(volume);

    public void SetSubVolume(float volume) => Engine.SetSubVolume(volume);

    public void Start()
    {
        AudioOutputDevice? virtualDevice = DeviceManager.GetFirstVirtualOutputDevice()
            ?? throw new InvalidOperationException("No virtual audio output device was found. Start aborted.");

        try
        {
            DeviceManager.SetDefaultRenderDevice(virtualDevice.Id);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to switch default output to {virtualDevice.Name}: {ex.Message}", ex);
        }

        Engine.Start();
    }

    public void Stop() => Engine.Stop();

    public LatencyTestRecording RecordLatencyTest(string microphoneId, LatencyTestSignal signal, CancellationToken cancellationToken)
    {
        MMDevice microphone = DeviceManager.GetDeviceById(microphoneId)
            ?? throw new LatencyDetectionException("The selected microphone isn't available anymore.");

        return Engine.RecordLatencyTest(microphone, signal, cancellationToken);
    }

    public void Dispose()
    {
        DeviceManager.DevicesChanged -= OnDevicesChanged;
        DeviceManager.DefaultDeviceBecameNonVirtual -= OnDefaultDeviceBecameNonVirtual;

        Engine.Dispose();
        DeviceManager.Dispose();
    }

    private MMDevice? GetDevice(string? deviceId)
    {
        return string.IsNullOrWhiteSpace(deviceId) ? null : DeviceManager.GetDeviceById(deviceId);
    }

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnDefaultDeviceBecameNonVirtual(object? sender, EventArgs e)
    {
        StopRequested?.Invoke(this, "Default output device changed. Audio routing has been stopped.");
    }
}
