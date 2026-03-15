using System.Windows;
using DNVRR.Models;
using DNVRR.Services;

namespace DNVRR.Views;

public partial class AdminWindow : Window
{
    private readonly SdkManager _sdk;
    private readonly Database _db;
    private readonly MainWindow _mainWindow;

    public AdminWindow(SdkManager sdk, Database db, MainWindow mainWindow)
    {
        _sdk = sdk;
        _db = db;
        _mainWindow = mainWindow;
        InitializeComponent();
        Loaded += (_, _) => LoadNvrs();
    }

    private void LoadNvrs()
    {
        NvrList.ItemsSource = _db.GetNvrs();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var ip = TxtNvrIp.Text.Trim();
        var user = TxtNvrUser.Text.Trim();
        var pass = TxtNvrPass.Password;
        int port = int.TryParse(TxtNvrPort.Text, out var p) ? p : 80;
        int sdkPort = int.TryParse(TxtSdkPort.Text, out var sp) ? sp : 8000;

        if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(user))
        {
            StatusText.Text = "IP and username are required.";
            return;
        }

        StatusText.Text = $"Connecting to {ip}...";

        try
        {
            // Probe ISAPI for device info
            using var isapi = new ISAPIClient(ip, user, pass, port);
            var info = await isapi.GetDeviceInfo();
            if (info == null)
            {
                StatusText.Text = "Could not connect to NVR.";
                return;
            }

            var (name, model, serial) = info.Value;
            StatusText.Text = $"Found: {name} ({model}). Discovering cameras...";

            // Save NVR
            var nvr = new NvrInfo
            {
                Name = name,
                Ip = ip,
                Username = user,
                Password = pass,
                Port = port,
                SdkPort = sdkPort,
            };
            int nvrId = _db.AddNvr(nvr);
            nvr.Id = nvrId;

            // Discover cameras via ISAPI
            var cameras = await isapi.DiscoverCameras(nvrId);
            foreach (var cam in cameras)
                _db.AddCamera(cam);

            // Update NVR channel count
            nvr.Channels = cameras.Count;
            _db.AddNvr(nvr);

            // Try SDK login
            if (_sdk.IsInitialized)
            {
                int userId = await _sdk.LoginAsync(nvr);
                StatusText.Text = userId >= 0
                    ? $"Connected! {cameras.Count} cameras found. SDK login OK."
                    : $"Connected! {cameras.Count} cameras found. SDK login failed (will retry on preview).";
            }
            else
            {
                StatusText.Text = $"Connected! {cameras.Count} cameras found. (SDK not loaded — place DLLs in sdk/ folder)";
            }

            LoadNvrs();
            await _mainWindow.LoadCameras();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (NvrList.SelectedItem is not NvrInfo nvr) return;

        StatusText.Text = $"Refreshing cameras for {nvr.DisplayName}...";
        try
        {
            using var isapi = new ISAPIClient(nvr.Ip, nvr.Username, nvr.Password, nvr.Port);
            var cameras = await isapi.DiscoverCameras(nvr.Id);
            _db.DeleteCamerasForNvr(nvr.Id);
            foreach (var cam in cameras)
                _db.AddCamera(cam);

            nvr.Channels = cameras.Count;
            _db.AddNvr(nvr);

            StatusText.Text = $"Refreshed: {cameras.Count} cameras.";
            LoadNvrs();
            await _mainWindow.LoadCameras();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (NvrList.SelectedItem is not NvrInfo nvr) return;

        var result = MessageBox.Show(
            $"Delete NVR '{nvr.DisplayName}' and all its cameras?",
            "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            _db.DeleteNvr(nvr.Id);
            LoadNvrs();
            await _mainWindow.LoadCameras();
            StatusText.Text = "NVR deleted.";
        }
    }
}
