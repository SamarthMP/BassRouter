using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using BassRouter.Audio;
using BassRouter.Audio.Latency;
using SamsidParty;

namespace BassRouter;

/// <summary>
/// Lets the user pick a microphone, then measures the latency difference between the outputs and lines them up.
/// </summary>
public partial class LatencyDetectionWindow : Window
{
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromRgb(0xF1, 0xEC, 0xF3));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.FromRgb(255, 100, 100));
    private static readonly IBrush DimBrush = new SolidColorBrush(Color.FromRgb(144, 144, 168));

    private readonly AudioEngineController Controller;
    private List<AudioInputDevice> Microphones = new();
    private CancellationTokenSource? Detection;
    private bool ShowingNoMicrophones;
    private bool IsClosed;

    public LatencyDetectionWindow(AudioEngineController controller)
    {
        Controller = controller;

        InitializeComponent();

        Opened += (s, e) =>
        {
            if (OperatingSystem.IsWindows() && TryGetPlatformHandle()?.Handle is nint handle)
                WindowHelpers.FixWindows11Corners(handle);
        };

        TitleBar.PointerPressed += OnTitleBarPointerPressed;
        ButtonClose.Click += (_, _) => Close();
        ButtonCancel.Click += (_, _) => Close();
        ButtonStart.Click += OnStartClicked;
        Controller.DevicesChanged += OnDevicesChanged;

        // Closing while the test runs cancels it
        Closing += (_, _) => Detection?.Cancel();
        Closed += OnClosed;

        RefreshMicrophones();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Escape)
            Close();
    }

    private void RefreshMicrophones()
    {
        string? selectedId = GetSelectedMicrophoneId();
        Microphones = Controller.GetInputDevices().ToList();

        ComboMicrophone.Items.Clear();
        foreach (AudioInputDevice microphone in Microphones)
            ComboMicrophone.Items.Add(microphone.Name);

        int index = Microphones.FindIndex(microphone => microphone.Id == selectedId);
        if (index < 0)
            index = Microphones.FindIndex(microphone => microphone.IsDefault);
        if (index < 0 && Microphones.Count > 0)
            index = 0;

        ComboMicrophone.SelectedIndex = index;

        if (Detection == null && Microphones.Count == 0)
        {
            ShowStatus("No microphones found. Connect one to detect latency.", ErrorBrush);
            ShowingNoMicrophones = true;
        }
        else if (ShowingNoMicrophones)
        {
            PanelStatus.IsVisible = false;
            ShowingNoMicrophones = false;
        }

        UpdateControls();
    }

    private string? GetSelectedMicrophoneId()
    {
        int index = ComboMicrophone.SelectedIndex;
        return index >= 0 && index < Microphones.Count ? Microphones[index].Id : null;
    }

    private async void OnStartClicked(object? sender, RoutedEventArgs e)
    {
        string? microphoneId = GetSelectedMicrophoneId();
        if (microphoneId == null || Detection != null)
            return;

        using var detection = new CancellationTokenSource();
        Detection = detection;
        UpdateControls();
        ShowStatus("Playing test sounds, keep quiet…", DimBrush);

        try
        {
            LatencyDetectionResult result = await Controller.DetectLatencyAsync(microphoneId, detection.Token);
            if (IsClosed)
                return;

            ShowStatus(Describe(result), TextBrush);
            ButtonStart.Content = "Run Again";
            ButtonCancel.Content = "Done";
        }
        catch (OperationCanceledException)
        {
            // The window was closed
        }
        catch (Exception ex)
        {
            if (IsClosed)
                return;

            ShowStatus(ex.Message, ErrorBrush);
            ButtonStart.Content = "Try Again";
        }
        finally
        {
            Detection = null;
            if (!IsClosed)
                UpdateControls();
        }
    }

    private static string Describe(LatencyDetectionResult result)
    {
        if (result.HeadphoneDelayMs == 0 && result.SubDelayMs == 0)
            return "Both outputs already play in sync, so both latencies are now 0 ms.";

        return result.HeadphoneDelayMs > 0
            ? $"The subwoofer plays {result.HeadphoneDelayMs} ms after the primary output. Primary output latency is now {result.HeadphoneDelayMs} ms and subwoofer latency 0 ms."
            : $"The primary output plays {result.SubDelayMs} ms after the subwoofer. Subwoofer latency is now {result.SubDelayMs} ms and primary output latency 0 ms.";
    }

    private void ShowStatus(string message, IBrush brush)
    {
        ShowingNoMicrophones = false;
        PanelStatus.IsVisible = true;
        TextStatus.Text = message;
        TextStatus.Foreground = brush;
    }

    private void UpdateControls()
    {
        bool running = Detection != null;

        ComboMicrophone.IsEnabled = !running;
        ButtonStart.IsEnabled = !running && Microphones.Count > 0;
        ProgressDetection.IsVisible = running;
    }

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsClosed)
                RefreshMicrophones();
        });
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        IsClosed = true;
        Controller.DevicesChanged -= OnDevicesChanged;
    }
}
