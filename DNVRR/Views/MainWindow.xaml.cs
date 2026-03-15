using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using DNVRR.Controls;
using DNVRR.Models;
using DNVRR.Services;

namespace DNVRR.Views;

public partial class MainWindow : Window
{
    private readonly SdkManager _sdk;
    private readonly Database _db;
    private readonly List<CameraTile> _tiles = new();
    private CameraTile? _selectedTile;
    private int _gridCols = 4;
    private int _gridRows = 3;
    private uint _streamType = 1; // 0=main, 1=sub

    public MainWindow(SdkManager sdk, Database db)
    {
        _sdk = sdk;
        _db = db;
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadCameras();
    }

    public async Task LoadCameras()
    {
        // Stop existing previews
        foreach (var tile in _tiles)
            await tile.StopPreview();
        _tiles.Clear();
        CameraGrid.Children.Clear();
        CameraGrid.RowDefinitions.Clear();
        CameraGrid.ColumnDefinitions.Clear();

        // Build grid
        for (int c = 0; c < _gridCols; c++)
            CameraGrid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int r = 0; r < _gridRows; r++)
            CameraGrid.RowDefinitions.Add(new RowDefinition());

        // Load cameras from DB
        var nvrs = _db.GetNvrs();
        var nvrMap = nvrs.ToDictionary(n => n.Id);
        var cameras = _db.GetCameras().Where(c => c.Enabled).ToList();

        // Attach NVR info to each camera
        foreach (var cam in cameras)
            cam.Nvr = nvrMap.GetValueOrDefault(cam.NvrId);

        int totalSlots = _gridCols * _gridRows;
        for (int i = 0; i < totalSlots; i++)
        {
            int row = i / _gridCols;
            int col = i % _gridCols;

            var tile = new CameraTile
            {
                SdkManager = _sdk,
                Margin = new Thickness(1),
            };

            if (i < cameras.Count)
                tile.Camera = cameras[i];

            tile.MouseLeftButtonDown += (s, _) => SelectTile(tile);
            tile.FullscreenRequested += Tile_FullscreenRequested;

            Grid.SetRow(tile, row);
            Grid.SetColumn(tile, col);
            CameraGrid.Children.Add(tile);
            _tiles.Add(tile);
        }

        // Start previews for all cameras
        if (_sdk.IsInitialized)
        {
            foreach (var tile in _tiles.Where(t => t.Camera != null))
            {
                _ = tile.StartPreview(); // fire and forget — each tile connects independently
            }
        }
    }

    private void SelectTile(CameraTile tile)
    {
        _selectedTile = tile;
        PtzCameraName.Text = tile.Camera?.Name ?? "Empty slot";
    }

    // --- PTZ ---

    private static readonly Dictionary<string, uint> PtzCommands = new()
    {
        ["PAN_LEFT"] = HCNetSDK.PAN_LEFT,
        ["PAN_RIGHT"] = HCNetSDK.PAN_RIGHT,
        ["TILT_UP"] = HCNetSDK.TILT_UP,
        ["TILT_DOWN"] = HCNetSDK.TILT_DOWN,
        ["ZOOM_IN"] = HCNetSDK.ZOOM_IN,
        ["ZOOM_OUT"] = HCNetSDK.ZOOM_OUT,
        ["PAN_LEFT_UP"] = HCNetSDK.PAN_LEFT_UP,
        ["PAN_LEFT_DOWN"] = HCNetSDK.PAN_LEFT_DOWN,
        ["PAN_RIGHT_UP"] = HCNetSDK.PAN_RIGHT_UP,
        ["PAN_RIGHT_DOWN"] = HCNetSDK.PAN_RIGHT_DOWN,
    };

    private async void Ptz_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Button btn || _selectedTile?.Camera?.Nvr == null) return;
        var tag = btn.Tag?.ToString();
        if (tag != null && PtzCommands.TryGetValue(tag, out uint cmd))
        {
            await _sdk.PtzMoveAsync(_selectedTile.Camera.Nvr, _selectedTile.Camera.Channel, cmd);
        }
    }

    private async void Ptz_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Button btn || _selectedTile?.Camera?.Nvr == null) return;
        var tag = btn.Tag?.ToString();
        if (tag != null && PtzCommands.TryGetValue(tag, out uint cmd))
        {
            await _sdk.PtzStopAsync(_selectedTile.Camera.Nvr, _selectedTile.Camera.Channel, cmd);
        }
    }

    private async void PtzStop_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTile?.Camera?.Nvr == null) return;
        // Stop all directions
        await _sdk.PtzStopAsync(_selectedTile.Camera.Nvr, _selectedTile.Camera.Channel, HCNetSDK.PAN_LEFT);
        await _sdk.PtzStopAsync(_selectedTile.Camera.Nvr, _selectedTile.Camera.Channel, HCNetSDK.TILT_UP);
        await _sdk.PtzStopAsync(_selectedTile.Camera.Nvr, _selectedTile.Camera.Channel, HCNetSDK.ZOOM_IN);
    }

    // --- Layout ---

    private async void Layout_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        int size = int.Parse(btn.Tag?.ToString() ?? "4");
        switch (size)
        {
            case 2: _gridCols = 2; _gridRows = 2; break;
            case 3: _gridCols = 3; _gridRows = 3; break;
            case 4: _gridCols = 4; _gridRows = 3; break;
            case 16: _gridCols = 4; _gridRows = 4; break;
        }
        await LoadCameras();
    }

    private async void StreamType_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (StreamTypeCombo.SelectedItem is ComboBoxItem item)
        {
            _streamType = uint.Parse(item.Tag?.ToString() ?? "1");
            if (_tiles.Count > 0)
                await LoadCameras(); // restart with new stream type
        }
    }

    // --- Multi-window ---

    private void NewWindow_Click(object sender, RoutedEventArgs e)
    {
        var window = new MainWindow(_sdk, _db);
        window.Show();
    }

    private void OpenAdmin_Click(object sender, RoutedEventArgs e)
    {
        var admin = new AdminWindow(_sdk, _db, this);
        admin.ShowDialog();
    }

    // --- Fullscreen ---

    private void Tile_FullscreenRequested(object? sender, EventArgs e)
    {
        // TODO: open camera in fullscreen window on current monitor
        if (sender is CameraTile tile && tile.Camera != null)
        {
            var fsWindow = new FullscreenWindow(_sdk, tile.Camera);
            fsWindow.Show();
        }
    }

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        foreach (var tile in _tiles)
            await tile.StopPreview();
    }
}
