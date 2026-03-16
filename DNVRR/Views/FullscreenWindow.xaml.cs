using System.Windows;
using System.Windows.Input;
using DNVRR.Controls;
using DNVRR.Models;
using DNVRR.Services;

namespace DNVRR.Views;

public partial class FullscreenWindow : Window
{
    private readonly SdkManager _sdk;
    private readonly CameraInfo _camera;
    private VideoPanel? _videoPanel;

    public FullscreenWindow(SdkManager sdk, CameraInfo camera)
    {
        _sdk = sdk;
        _camera = camera;
        InitializeComponent();
        CameraLabel.Text = camera.Name;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _videoPanel = new VideoPanel();
        _videoPanel.NativeDoubleClick += (_, _) => Dispatcher.Invoke(Close);
        VideoBorder.Child = _videoPanel;

        // Small delay to let HWND initialize
        await Task.Delay(100);

        if (_camera.Nvr != null)
        {
            // Use main stream for fullscreen (high quality)
            await _sdk.StartPreviewAsync(_camera, _videoPanel.VideoHandle, streamType: 0);
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
    }

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        await _sdk.StopPreviewAsync(_camera.Id);
    }
}
