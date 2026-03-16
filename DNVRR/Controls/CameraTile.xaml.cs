using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DNVRR.Models;
using DNVRR.Services;
using static DNVRR.Services.Log;

namespace DNVRR.Controls;

public partial class CameraTile : UserControl
{
    private VideoPanel? _videoPanel;
    private CameraInfo? _camera;
    private SdkManager? _sdkManager;

    public CameraTile()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        MouseDoubleClick += OnDoubleClick;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Bottom bar is ~20px, calculate video area from remaining space
        double w = e.NewSize.Width;
        double h = Math.Max(0, e.NewSize.Height - 20);
        double targetRatio = 16.0 / 9.0;

        if (w <= 0 || h <= 0) return;

        double currentRatio = w / h;
        if (currentRatio > targetRatio)
        {
            VideoBorder.Width = h * targetRatio;
            VideoBorder.Height = h;
        }
        else
        {
            VideoBorder.Width = w;
            VideoBorder.Height = w / targetRatio;
        }
    }

    private static readonly SolidColorBrush DotGray = new(Color.FromRgb(0x66, 0x66, 0x66));
    private static readonly SolidColorBrush DotGreen = new(Color.FromRgb(0x22, 0xc5, 0x5e));
    private static readonly SolidColorBrush DotRed = new(Color.FromRgb(0xef, 0x44, 0x44));
    private static readonly SolidColorBrush DotYellow = new(Color.FromRgb(0xea, 0xb3, 0x08));

    public CameraInfo? Camera
    {
        get => _camera;
        set
        {
            _camera = value;
            CameraLabel.Text = value?.Name ?? "";
            if (value == null)
            {
                StatusDot.Fill = DotGray;
                StatusText.Visibility = Visibility.Collapsed;
            }
            else if (!value.Connected)
            {
                StatusDot.Fill = DotRed;
                StatusText.Text = "No Feed";
                StatusText.Visibility = Visibility.Visible;
            }
            else
            {
                StatusDot.Fill = DotYellow;
                StatusText.Text = "Connecting...";
                StatusText.Visibility = Visibility.Visible;
            }
        }
    }

    public SdkManager? SdkManager
    {
        get => _sdkManager;
        set => _sdkManager = value;
    }

    /// <summary>
    /// Event raised when user double-clicks to request fullscreen.
    /// </summary>
    public event EventHandler? FullscreenRequested;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_videoPanel == null)
        {
            _videoPanel = new VideoPanel();
            _videoPanel.NativeDoubleClick += (_, _) =>
                Dispatcher.Invoke(() => FullscreenRequested?.Invoke(this, EventArgs.Empty));
            VideoBorder.Child = _videoPanel;
        }
    }

    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
        await StopPreview();
    }

    /// <summary>
    /// Returns true if preview started successfully.
    /// </summary>
    public async Task<bool> StartPreview()
    {
        if (_camera == null || _sdkManager == null)
            return false;

        // Ensure VideoPanel is created
        if (_videoPanel == null)
        {
            _videoPanel = new VideoPanel();
            _videoPanel.NativeDoubleClick += (_, _) =>
                Dispatcher.Invoke(() => FullscreenRequested?.Invoke(this, EventArgs.Empty));
            VideoBorder.Child = _videoPanel;
        }

        // Wait for the HwndHost to build its native window
        if (_videoPanel.VideoHandle == IntPtr.Zero)
        {
            Log.Info($"Tile [{_camera.Name}]: waiting for VideoPanel HWND...");
            var tcs = new TaskCompletionSource();
            _videoPanel.Loaded += (_, _) => tcs.TrySetResult();
            if (_videoPanel.IsLoaded)
                tcs.TrySetResult();

            await Task.WhenAny(tcs.Task, Task.Delay(2000));

            if (_videoPanel.VideoHandle == IntPtr.Zero)
            {
                Log.Error($"Tile [{_camera.Name}]: VideoPanel HWND still zero after wait");
                StatusDot.Fill = DotRed;
                StatusText.Text = "Video init failed";
                StatusText.Visibility = Visibility.Visible;
                return false;
            }
        }

        Log.Info($"Tile [{_camera.Name}]: HWND={_videoPanel.VideoHandle}, starting SDK preview");
        StatusDot.Fill = DotYellow; // connecting
        StatusText.Visibility = Visibility.Collapsed;
        int handle = await _sdkManager.StartPreviewAsync(_camera, _videoPanel.VideoHandle);
        Log.Info($"Tile [{_camera.Name}]: preview handle={handle}");
        if (handle >= 0)
        {
            _camera.Connected = true;
            StatusDot.Fill = DotGreen; // live
            return true;
        }
        else
        {
            _camera.Connected = false;
            StatusDot.Fill = DotRed; // error
            StatusText.Text = "Connection Failed";
            StatusText.Visibility = Visibility.Visible;
            return false;
        }
    }

    public async Task StopPreview()
    {
        if (_camera == null || _sdkManager == null) return;
        await _sdkManager.StopPreviewAsync(_camera.Id);
        ResetVideoPanel();
        StatusDot.Fill = DotGray;
        StatusText.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Destroy and recreate the VideoPanel to clear the native HWND of stale frames.
    /// </summary>
    private void ResetVideoPanel()
    {
        if (_videoPanel != null)
        {
            VideoBorder.Child = null;
            _videoPanel = null;
        }
    }

    private void OnDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        FullscreenRequested?.Invoke(this, EventArgs.Empty);
    }
}
