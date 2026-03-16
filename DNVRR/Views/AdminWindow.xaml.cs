using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DNVRR.Models;
using DNVRR.Services;

namespace DNVRR.Views;

public partial class AdminWindow : Window
{
    private readonly SdkManager _sdk;
    private readonly Database _db;
    private readonly MainWindow _mainWindow;
    private CancellationTokenSource? _scanCts;
    private readonly ObservableCollection<DiscoveredDevice> _scanResults = new();

    public AdminWindow(SdkManager sdk, Database db, MainWindow mainWindow)
    {
        _sdk = sdk;
        _db = db;
        _mainWindow = mainWindow;
        InitializeComponent();
        ScanResults.ItemsSource = _scanResults;
        AdapterCombo.ItemsSource = NetworkScanner.GetAdapters();
        if (AdapterCombo.Items.Count > 0) AdapterCombo.SelectedIndex = 0;
        Loaded += (_, _) => { LoadNvrs(); LoadCameras(); };
    }

    private void LoadNvrs()
    {
        NvrList.ItemsSource = _db.GetNvrs();
    }

    private void NvrRename_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not NvrInfo nvr) return;

        var dialog = new Window
        {
            Title = "Rename NVR",
            Width = 300, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1e1e1e")),
            ResizeMode = ResizeMode.NoResize,
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = $"Device: {nvr.Name}",
            Foreground = System.Windows.Media.Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 6),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Display name:",
            Foreground = System.Windows.Media.Brushes.White,
            Margin = new Thickness(0, 0, 0, 4),
        });
        var textBox = new TextBox
        {
            Text = nvr.DisplayName,
            Style = (Style)FindResource("DarkTextBox"),
        };
        textBox.SelectAll();
        panel.Children.Add(textBox);
        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        var okBtn = new Button { Content = "Save", Padding = new Thickness(16, 4, 16, 4), IsDefault = true };
        okBtn.Click += (_, _) => { dialog.DialogResult = true; };
        var cancelBtn = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(8, 0, 0, 0) };
        cancelBtn.Click += (_, _) => { dialog.DialogResult = false; };
        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        panel.Children.Add(btnPanel);
        dialog.Content = panel;
        textBox.Focus();

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(textBox.Text))
        {
            nvr.Alias = textBox.Text.Trim();
            _db.UpdateNvrAlias(nvr.Id, nvr.Alias);
            LoadNvrs();
            StatusText.Text = $"Renamed to '{nvr.DisplayName}'.";
        }
    }

    private void LoadCameras()
    {
        var nvrs = _db.GetNvrs().ToDictionary(n => n.Id, n => n.DisplayName);
        var cameras = _db.GetCameras();
        foreach (var cam in cameras)
            cam.NvrName = nvrs.GetValueOrDefault(cam.NvrId, "Unknown");

        var view = CollectionViewSource.GetDefaultView(cameras);
        view.GroupDescriptions.Add(new PropertyGroupDescription("NvrName"));
        CameraList.ItemsSource = view;
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        _scanResults.Clear();
        ScanResults.Visibility = Visibility.Visible;
        BtnScan.IsEnabled = false;
        BtnStopScan.IsEnabled = true;

        var existingIps = _db.GetNvrs().Select(n => n.Ip).ToHashSet();

        try
        {
            if (AdapterCombo.SelectedItem is not AdapterInfo adapter)
            {
                ScanProgress.Text = "Select a network adapter.";
                return;
            }

            // Phase 1: ONVIF WS-Discovery (fast multicast probe)
            ScanProgress.Text = "Phase 1: ONVIF discovery...";
            var onvifFound = await OnvifDiscovery.DiscoverAsync(
                adapter.Ip,
                timeoutSeconds: 5,
                existingIps,
                device => Dispatcher.Invoke(() =>
                {
                    _scanResults.Add(device);
                    _ = ProbeSdkPortForDevice(device);
                }),
                ct);

            var foundIps = _scanResults.Select(d => d.Ip).ToHashSet();
            ScanProgress.Text = $"ONVIF found {onvifFound.Count}. Phase 2: ISAPI scan...";

            // Phase 2: ISAPI subnet scan (slower, finds devices ONVIF missed)
            var label = adapter.Subnet;
            var progress = new Progress<(int scanned, int total)>(p =>
                ScanProgress.Text = $"ISAPI scanning {label}... {p.scanned}/{p.total}");

            await NetworkScanner.ScanSubnetAsync(
                adapter.Network, adapter.Mask,
                port: 80,
                timeoutMs: 2000,
                concurrency: 40,
                progress,
                device =>
                {
                    if (!foundIps.Contains(device.Ip))
                        Dispatcher.Invoke(() =>
                        {
                            _scanResults.Add(device);
                            _ = ProbeSdkPortForDevice(device);
                        });
                },
                existingIps,
                ct);

            ScanProgress.Text = $"Done. Found {_scanResults.Count} device(s).";
        }
        catch (OperationCanceledException)
        {
            ScanProgress.Text = $"Scan stopped. Found {_scanResults.Count} device(s).";
        }
        catch (Exception ex)
        {
            ScanProgress.Text = $"Scan error: {ex.Message}";
        }
        finally
        {
            BtnScan.IsEnabled = true;
            BtnStopScan.IsEnabled = false;
        }
    }

    private void StopScan_Click(object sender, RoutedEventArgs e)
    {
        _scanCts?.Cancel();
    }

    private async Task ProbeSdkPortForDevice(DiscoveredDevice device)
    {
        var sdkPort = await ISAPIClient.DiscoverSdkPortAsync(device.Ip);
        if (sdkPort.HasValue)
        {
            device.SdkPort = sdkPort.Value;
            // Force UI refresh for this item
            var idx = _scanResults.IndexOf(device);
            if (idx >= 0)
            {
                _scanResults.RemoveAt(idx);
                _scanResults.Insert(idx, device);
            }
        }
    }

    private void UseDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is DiscoveredDevice device)
        {
            TxtNvrIp.Text = device.Ip;
            TxtNvrPort.Text = device.Port.ToString();
            if (device.SdkPort > 0)
                TxtSdkPort.Text = device.SdkPort.ToString();
            StatusText.Text = $"HTTP: {device.Port}, SDK: {(device.SdkPort > 0 ? device.SdkPort : "?")}. Enter credentials and click Connect.";
        }
    }

    private async void CameraToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is CameraInfo cam)
        {
            _db.UpdateCamera(cam.Id, cam.Enabled, cam.PtzEnabled);
            await _mainWindow.LoadCameras();
        }
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

        StatusText.Text = $"Connecting to {ip}:{port}...";
        Log.Info($"Connect attempt: {ip}:{port} user={user}");

        try
        {
            // Try connecting, auto-detect port if it fails
            ISAPIClient isapi;
            (string name, string model, string serial)? info;

            try
            {
                isapi = new ISAPIClient(ip, user, pass, port);
                info = await isapi.GetDeviceInfo();
            }
            catch (Exception ex)
            {
                Log.Warn($"Connect failed on port {port}: {ex.Message}");
                StatusText.Text = $"Port {port} failed. Auto-detecting HTTP port...";

                // Try to find the correct port
                var detectedPort = await ISAPIClient.DiscoverHttpPortAsync(ip);
                if (detectedPort == null)
                {
                    StatusText.Text = $"Could not connect to {ip}. This may be a camera (not an NVR). Cameras are discovered through their NVR.";
                    Log.Error($"No ISAPI port found for {ip} — likely a standalone camera, not an NVR");
                    return;
                }

                port = detectedPort.Value;
                TxtNvrPort.Text = port.ToString();
                StatusText.Text = $"Found on port {port}. Connecting...";
                isapi = new ISAPIClient(ip, user, pass, port);
                info = await isapi.GetDeviceInfo();
            }

            if (info == null)
            {
                StatusText.Text = "Could not get device info from NVR.";
                return;
            }

            var (name, model, serial) = info.Value;

            // Auto-discover SDK port only if still at default
            if (sdkPort == 8000)
            {
                StatusText.Text = $"Found: {name} ({model}). Discovering SDK port...";
                var discoveredSdkPort = await ISAPIClient.DiscoverSdkPortAsync(ip);
                if (discoveredSdkPort.HasValue)
                {
                    sdkPort = discoveredSdkPort.Value;
                    TxtSdkPort.Text = sdkPort.ToString();
                }
            }

            StatusText.Text = $"Found: {name} ({model}). SDK port: {sdkPort}. Discovering cameras...";

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
                StatusText.Text = $"Connected! {cameras.Count} cameras found. (SDK not loaded)";
            }

            isapi.Dispose();
            LoadNvrs();
            LoadCameras();
            await _mainWindow.LoadCameras();
        }
        catch (Exception ex)
        {
            Log.Error($"Connect error: {ex.Message}");
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
            LoadCameras();
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
            LoadCameras();
            await _mainWindow.LoadCameras();
            StatusText.Text = "NVR deleted.";
        }
    }
}
