using System.Windows;
using System.Windows.Controls;
using DNVRR.Models;
using DNVRR.Services;

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

    public CameraInfo? Camera
    {
        get => _camera;
        set
        {
            _camera = value;
            CameraLabel.Text = value?.Name ?? "";
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
            VideoBorder.Child = _videoPanel;
        }
    }

    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
        await StopPreview();
    }

    public async Task StartPreview()
    {
        if (_camera == null || _sdkManager == null || _videoPanel == null)
            return;

        StatusText.Visibility = Visibility.Collapsed;
        int handle = await _sdkManager.StartPreviewAsync(_camera, _videoPanel.VideoHandle);
        if (handle < 0)
        {
            StatusText.Text = "Connection Failed";
            StatusText.Visibility = Visibility.Visible;
        }
    }

    public async Task StopPreview()
    {
        if (_camera == null || _sdkManager == null) return;
        await _sdkManager.StopPreviewAsync(_camera.Id);
        StatusText.Text = "No Signal";
        StatusText.Visibility = Visibility.Visible;
    }

    private void OnDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        FullscreenRequested?.Invoke(this, EventArgs.Empty);
    }
}
