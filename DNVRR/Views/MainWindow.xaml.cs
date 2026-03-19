using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
    private ViewInfo? _activeView;
    private List<CameraInfo> _allCameras = new();
    private Dictionary<int, NvrInfo> _nvrMap = new();
    private bool _suppressViewSizeChanged;
    private DispatcherTimer? _probeTimer;

    private int? _restoreViewId;
    private bool _restoreMaximized;

    public MainWindow(SdkManager sdk, Database db, Models.SavedWindowState? restoreState = null)
    {
        _sdk = sdk;
        _db = db;
        InitializeComponent();

        if (restoreState != null)
        {
            _restoreViewId = restoreState.ViewId;
            _restoreMaximized = restoreState.IsMaximized;
            // Position in normal state so WPF places the window on the correct monitor
            WindowState = WindowState.Normal;
            Left = restoreState.X;
            Top = restoreState.Y;
            Width = restoreState.Width;
            Height = restoreState.Height;

            if (!restoreState.LeftSidebarOpen)
            {
                LeftSidebar.Visibility = Visibility.Collapsed;
                BtnToggleLeft.Content = "\u25B6";
            }
            if (!restoreState.RightSidebarOpen)
            {
                Sidebar.Visibility = Visibility.Collapsed;
                BtnToggleRight.Content = "\u25C0";
            }
        }

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Maximize after window is shown so it maximizes on the correct monitor
        if (_restoreMaximized)
        {
            WindowState = WindowState.Maximized;
            _restoreMaximized = false;
        }

        LoadSidebar();

        // Restore active view if specified
        if (_restoreViewId.HasValue)
        {
            var views = _db.GetViews();
            var view = views.FirstOrDefault(v => v.Id == _restoreViewId.Value);
            if (view != null)
            {
                _activeView = view;
                _gridCols = view.Cols;
                _gridRows = view.Rows;
                UpdateViewHighlight();
                BuildCamerasList();
            }
        }

        // Probe camera connections before loading previews
        if (_sdk.IsInitialized)
            await ProbeAllCameras();

        await LoadCameras();

        // Re-probe every 5 minutes
        if (_sdk.IsInitialized)
        {
            _probeTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            _probeTimer.Tick += async (_, _) =>
            {
                await ProbeAllCameras();
                BuildCamerasList();
            };
            _probeTimer.Start();
        }
    }

    private async Task ProbeAllCameras()
    {
        if (!_sdk.IsInitialized) return;

        var nvrs = _db.GetNvrs();
        var allCameras = _db.GetCameras();

        foreach (var nvr in nvrs)
        {
            var cameras = allCameras.Where(c => c.NvrId == nvr.Id).ToList();
            if (cameras.Count == 0) continue;

            var channels = cameras.Select(c => c.Channel).ToList();
            var connectedMap = await _sdk.ProbeChannelsAsync(nvr, channels);

            foreach (var cam in cameras)
            {
                bool connected = connectedMap.GetValueOrDefault(cam.Channel, true);
                if (cam.Connected != connected)
                {
                    cam.Connected = connected;
                    _db.UpdateCameraConnected(cam.Id, connected);
                }
            }
        }

        // Update the in-memory camera list and sidebar
        foreach (var cam in _allCameras)
        {
            var fresh = allCameras.FirstOrDefault(c => c.Id == cam.Id);
            if (fresh != null) cam.Connected = fresh.Connected;
        }
        BuildCamerasList();
    }

    public Models.SavedWindowState GetWindowState()
    {
        var isMax = WindowState == WindowState.Maximized;
        return new Models.SavedWindowState
        {
            ViewId = _activeView?.Id,
            X = isMax ? RestoreBounds.Left : Left,
            Y = isMax ? RestoreBounds.Top : Top,
            Width = isMax ? RestoreBounds.Width : Width,
            Height = isMax ? RestoreBounds.Height : Height,
            IsMaximized = isMax,
            LeftSidebarOpen = LeftSidebar.Visibility == Visibility.Visible,
            RightSidebarOpen = Sidebar.Visibility == Visibility.Visible,
        };
    }

    // --- Sidebar ---

    private void LoadSidebar()
    {
        // Load views — create a default if none exist
        var views = _db.GetViews();
        if (views.Count == 0)
        {
            // Auto-populate default view with all enabled cameras
            var cameras = _db.GetCameras().Where(c => c.Enabled).ToList();
            var defaultView = new ViewInfo { Name = "Default", Slug = "default", Cols = 4, Rows = 3 };
            var grid = new int?[12];
            for (int i = 0; i < Math.Min(cameras.Count, grid.Length); i++)
                grid[i] = cameras[i].Id;
            defaultView.SetGridArray(grid);
            defaultView.Id = _db.AddView(defaultView);
            views = [defaultView];
        }

        // Auto-select first view if none active
        if (_activeView == null || views.All(v => v.Id != _activeView.Id))
        {
            _activeView = views[0];
            _gridCols = _activeView.Cols;
            _gridRows = _activeView.Rows;
        }

        ViewsList.ItemsSource = views;
        UpdateViewHighlight();

        // Load cameras grouped by NVR
        var nvrs = _db.GetNvrs();
        _nvrMap = nvrs.ToDictionary(n => n.Id);
        _allCameras = _db.GetCameras().Where(c => c.Enabled).ToList();
        foreach (var cam in _allCameras)
            cam.Nvr = _nvrMap.GetValueOrDefault(cam.NvrId);

        BuildCamerasList();
    }

    private void BuildCamerasList()
    {
        var panel = new StackPanel();
        var grouped = _allCameras.GroupBy(c => c.NvrId);

        // Get camera IDs in current view for dimming
        var inView = new HashSet<int>();
        if (_activeView != null)
        {
            foreach (var id in _activeView.GetGridArray())
                if (id.HasValue) inView.Add(id.Value);
        }

        foreach (var group in grouped)
        {
            var nvrName = _nvrMap.GetValueOrDefault(group.Key)?.DisplayName ?? "Unknown NVR";
            var cameras = group.ToList();

            // NVR group header
            var header = new Border
            {
                Padding = new Thickness(10, 6, 10, 6),
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2a2d35")),
                Cursor = Cursors.Hand,
            };
            var headerPanel = new DockPanel();
            headerPanel.Children.Add(new TextBlock
            {
                Text = $"\u25BC  {nvrName}",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#71717a")),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
            });
            headerPanel.Children.Add(new TextBlock
            {
                Text = cameras.Count.ToString(),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#71717a")),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Right,
                Opacity = 0.6,
            });
            header.Child = headerPanel;

            var camsPanel = new StackPanel();
            foreach (var cam in cameras)
            {
                var isInView = inView.Contains(cam.Id);
                var item = new Border
                {
                    Padding = new Thickness(22, 5, 10, 5),
                    Cursor = Cursors.Hand,
                    Opacity = isInView ? 0.4 : 1.0,
                    Tag = cam,
                    Background = Brushes.Transparent,
                };
                item.PreviewMouseLeftButtonDown += CameraItem_Click;
                item.MouseEnter += (s, _) =>
                {
                    if (s is Border b) b.Background = new SolidColorBrush(Color.FromArgb(10, 255, 255, 255));
                };
                item.MouseLeave += (s, _) =>
                {
                    if (s is Border b) b.Background = Brushes.Transparent;
                };

                var itemPanel = new DockPanel();
                // Status dot
                var dot = new Border
                {
                    Width = 5,
                    Height = 5,
                    CornerRadius = new CornerRadius(2.5),
                    Background = cam.Connected
                        ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#22c55e"))
                        : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#71717a")),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                DockPanel.SetDock(dot, Dock.Left);
                itemPanel.Children.Add(dot);
                itemPanel.Children.Add(new TextBlock
                {
                    Text = cam.Name,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#e4e4e7")),
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                item.Child = itemPanel;
                camsPanel.Children.Add(item);
            }

            // Toggle collapse on header click
            header.MouseLeftButtonDown += (s, _) =>
            {
                camsPanel.Visibility = camsPanel.Visibility == Visibility.Visible
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            };

            panel.Children.Add(header);
            panel.Children.Add(camsPanel);
        }

        CamerasList.Items.Clear();
        CamerasList.Items.Add(panel);
    }

    private void UpdateViewHighlight()
    {
        // Update cols/rows fields
        if (_activeView != null)
        {
            _suppressViewSizeChanged = true;
            TxtViewCols.Text = _activeView.Cols.ToString();
            TxtViewRows.Text = _activeView.Rows.ToString();
            _suppressViewSizeChanged = false;
        }

        // Defer highlight until containers are generated
        Dispatcher.BeginInvoke(ApplyViewHighlight, DispatcherPriority.Loaded);
    }

    private void ApplyViewHighlight()
    {
        for (int i = 0; i < ViewsList.Items.Count; i++)
        {
            var container = ViewsList.ItemContainerGenerator.ContainerFromIndex(i) as ContentPresenter;
            if (container == null) continue;

            var border = VisualTreeHelper.GetChildrenCount(container) > 0
                ? VisualTreeHelper.GetChild(container, 0) as Border
                : null;
            if (border == null) continue;

            var view = ViewsList.Items[i] as ViewInfo;
            bool isActive = view != null && _activeView != null && view.Id == _activeView.Id;

            border.BorderBrush = isActive
                ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3b82f6"))
                : Brushes.Transparent;
            border.Background = isActive
                ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1e293b"))
                : Brushes.Transparent;
        }
    }

    // --- View Actions ---

    private async void ViewItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.DataContext is ViewInfo view)
        {
            _activeView = view;
            _gridCols = view.Cols;
            _gridRows = view.Rows;
            UpdateViewHighlight();
            BuildCamerasList();
            await LoadCameras();
            SaveStateDebounced();
        }
    }

    private async void NewView_Click(object sender, RoutedEventArgs e)
    {
        // Simple input dialog using WPF
        var dialog = new Window
        {
            Title = "New View",
            Width = 300, Height = 180, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1e1e1e")),
            ResizeMode = ResizeMode.NoResize,
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        var label = new TextBlock { Text = "View name:", Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 6) };
        var textBox = new TextBox { Text = "New View", Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333")),
            Foreground = Brushes.White, BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#555")),
            Padding = new Thickness(4, 3, 4, 3), CaretBrush = Brushes.White };
        textBox.SelectAll();
        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var okBtn = new Button { Content = "Create", Padding = new Thickness(16, 4, 16, 4), IsDefault = true };
        okBtn.Click += (_, _) => { dialog.DialogResult = true; };
        var cancelBtn = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(8, 0, 0, 0) };
        cancelBtn.Click += (_, _) => { dialog.DialogResult = false; };
        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        panel.Children.Add(label);
        panel.Children.Add(textBox);
        panel.Children.Add(btnPanel);
        dialog.Content = panel;
        textBox.Focus();

        if (dialog.ShowDialog() != true) return;
        var name = textBox.Text;
        if (string.IsNullOrWhiteSpace(name)) return;

        var view = new ViewInfo
        {
            Name = name,
            Slug = ViewInfo.Slugify(name),
            Cols = _gridCols,
            Rows = _gridRows,
        };
        // Empty grid
        view.SetGridArray(new int?[view.Cols * view.Rows]);
        view.Id = _db.AddView(view);

        _activeView = view;
        LoadSidebar();
        await LoadCameras();
    }

    private async void ViewDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not ViewInfo view) return;
        var result = MessageBox.Show(
            $"Delete view '{view.Name}'?",
            "Delete View", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
        {
            _db.DeleteView(view.Id);
            if (_activeView?.Id == view.Id)
                _activeView = null;
            LoadSidebar();
            await LoadCameras();
        }
    }

    private async void ViewSize_Changed(object sender, TextChangedEventArgs e)
    {
        if (_suppressViewSizeChanged || _activeView == null) return;

        if (int.TryParse(TxtViewCols.Text, out int cols) && cols >= 1 && cols <= 8 &&
            int.TryParse(TxtViewRows.Text, out int rows) && rows >= 1 && rows <= 8)
        {
            _activeView.Cols = cols;
            _activeView.Rows = rows;
            _gridCols = cols;
            _gridRows = rows;

            // Resize grid array
            var oldGrid = _activeView.GetGridArray();
            var newGrid = new int?[cols * rows];
            Array.Copy(oldGrid, newGrid, Math.Min(oldGrid.Length, newGrid.Length));
            _activeView.SetGridArray(newGrid);
            _db.UpdateView(_activeView);
            await LoadCameras();
        }
    }

    // --- Camera Assignment (manual drag via mouse tracking) ---

    private int? _draggingCameraId;
    private bool _isDragging;
    private DispatcherTimer? _dragTimer;
    private int? _pendingDragCameraId;
    private CameraTile? _pendingDragTile;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private POINT _dragStartScreen;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    private void CameraItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (_activeView == null) return;
        if (sender is not Border border || border.Tag is not CameraInfo cam) return;
        GetCursorPos(out _dragStartScreen);
        _pendingDragCameraId = cam.Id;
        _pendingDragTile = null;
        Log.Info($"Drag: sidebar mousedown cam={cam.Name} (id={cam.Id}), start=({_dragStartScreen.X},{_dragStartScreen.Y})");
        StartDrag();
    }

    private void StartDrag()
    {
        // Only start the timer — do NOT set _isDragging here.
        // The timer will detect movement threshold and promote to active drag.
        _dragTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _dragTimer.Tick -= DragTimer_Tick;
        _dragTimer.Tick += DragTimer_Tick;
        _dragTimer.Start();
    }

    private void DragTimer_Tick(object? sender, EventArgs e)
    {
        bool leftDown = (GetAsyncKeyState(0x01) & 0x8000) != 0;

        if (!leftDown)
        {
            _dragTimer?.Stop();
            if (_isDragging)
            {
                Log.Info($"Drag: mouse released while dragging camId={_draggingCameraId}");
                CompleteDrop();
            }
            else
            {
                Log.Info($"Drag: mouse released without drag (click), pending={_pendingDragCameraId}, tile={_pendingDragTile?.Camera?.Name}");
                _pendingDragCameraId = null;
                _pendingDragTile = null;
            }
            return;
        }

        // Check if we should start dragging (pending drag from tile or sidebar)
        if (!_isDragging && (_pendingDragCameraId != null || _pendingDragTile != null))
        {
            GetCursorPos(out var cursorPos);
            int dx = Math.Abs(cursorPos.X - _dragStartScreen.X);
            int dy = Math.Abs(cursorPos.Y - _dragStartScreen.Y);
            if (dx >= SystemParameters.MinimumHorizontalDragDistance ||
                dy >= SystemParameters.MinimumVerticalDragDistance)
            {
                if (_pendingDragTile?.Camera != null)
                {
                    _draggingCameraId = _pendingDragTile.Camera.Id;
                    Log.Info($"Drag: started from tile, cam={_pendingDragTile.Camera.Name}, dx={dx}, dy={dy}");
                    _pendingDragTile = null;
                }
                else if (_pendingDragCameraId != null)
                {
                    _draggingCameraId = _pendingDragCameraId;
                    Log.Info($"Drag: started from sidebar, camId={_pendingDragCameraId}, dx={dx}, dy={dy}");
                    _pendingDragCameraId = null;
                }
                _isDragging = true;
                Mouse.OverrideCursor = Cursors.Hand;
            }
        }
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        // Drag is handled by timer
    }

    private void Window_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // Timer handles drop
    }

    /// <summary>
    /// Convert screen coordinates to position relative to CameraGrid using Win32.
    /// Mouse.GetPosition doesn't work over native HWNDs.
    /// </summary>
    private Point GetGridPositionFromScreen()
    {
        GetCursorPos(out var screenPt);
        // Convert screen coords to window coords
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        ScreenToClient(hwnd, ref screenPt);

        // Convert from physical pixels to WPF DIPs
        var source = PresentationSource.FromVisual(this);
        double dpiX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
        double dpiY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;
        var windowPt = new Point(screenPt.X * dpiX, screenPt.Y * dpiY);

        // Get CameraGrid position relative to window
        var gridOffset = CameraGrid.TransformToAncestor(this).Transform(new Point(0, 0));
        return new Point(windowPt.X - gridOffset.X, windowPt.Y - gridOffset.Y);
    }

    private async void CompleteDrop()
    {
        if (_draggingCameraId == null || _activeView == null)
        {
            Log.Info($"Drag: CompleteDrop cancelled — camId={_draggingCameraId}, view={_activeView?.Name}");
            CancelDrag();
            return;
        }

        int camId = _draggingCameraId.Value;
        CancelDrag();

        var pos = GetGridPositionFromScreen();
        Log.Info($"Drag: drop at grid pos=({pos.X:F0},{pos.Y:F0}), gridSize=({CameraGrid.ActualWidth:F0},{CameraGrid.ActualHeight:F0})");

        if (pos.X < 0 || pos.Y < 0 || pos.X > CameraGrid.ActualWidth || pos.Y > CameraGrid.ActualHeight)
        {
            Log.Info("Drag: drop outside grid, ignoring");
            return;
        }

        int col = Math.Clamp((int)(pos.X / (CameraGrid.ActualWidth / _gridCols)), 0, _gridCols - 1);
        int row = Math.Clamp((int)(pos.Y / (CameraGrid.ActualHeight / _gridRows)), 0, _gridRows - 1);
        int targetSlot = row * _gridCols + col;
        Log.Info($"Drag: target slot={targetSlot} (row={row}, col={col})");

        var grid = _activeView.GetGridArray();
        if (targetSlot >= grid.Length) return;

        // Find source slot
        int sourceSlot = -1;
        for (int i = 0; i < grid.Length; i++)
            if (grid[i] == camId) { sourceSlot = i; break; }

        // Don't change if dropping on same slot
        if (sourceSlot == targetSlot) return;

        // Swap
        int? targetCamId = grid[targetSlot];
        if (sourceSlot >= 0)
            grid[sourceSlot] = targetCamId;
        grid[targetSlot] = camId;

        _activeView.SetGridArray(grid);
        _db.UpdateView(_activeView);
        BuildCamerasList();

        // Update only the affected tiles instead of rebuilding everything
        await SwapTileCamera(targetSlot, camId);
        if (sourceSlot >= 0)
            await SwapTileCamera(sourceSlot, targetCamId);
    }

    private async Task SwapTileCamera(int slotIndex, int? camId)
    {
        if (slotIndex < 0 || slotIndex >= _tiles.Count) return;

        var tile = _tiles[slotIndex];
        await tile.StopPreview();

        if (camId.HasValue)
        {
            var cam = _allCameras.FirstOrDefault(c => c.Id == camId.Value);
            if (cam != null)
            {
                tile.Camera = cam;
                if (_sdk.IsInitialized)
                    _ = tile.StartPreview();
                return;
            }
        }

        tile.Camera = null;
    }

    private void CancelDrag()
    {
        _dragTimer?.Stop();
        _isDragging = false;
        _draggingCameraId = null;
        _pendingDragCameraId = null;
        _pendingDragTile = null;
        Mouse.OverrideCursor = null;
    }

    private async void TileRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_activeView == null) return;
        if (sender is not CameraTile tile || tile.Camera == null) return;

        var grid = _activeView.GetGridArray();
        int slotIndex = _tiles.IndexOf(tile);
        for (int i = 0; i < grid.Length; i++)
        {
            if (grid[i] == tile.Camera.Id)
            {
                grid[i] = null;
                break;
            }
        }
        _activeView.SetGridArray(grid);
        _db.UpdateView(_activeView);
        BuildCamerasList();
        await SwapTileCamera(slotIndex, null);
    }

    // --- Grid Loading ---

    public async Task LoadCameras()
    {
        Log.Info($"LoadCameras: activeView={_activeView?.Name ?? "none"}, grid={_gridCols}x{_gridRows}");

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

        // Reload cameras from DB
        var nvrs = _db.GetNvrs();
        _nvrMap = nvrs.ToDictionary(n => n.Id);
        _allCameras = _db.GetCameras().Where(c => c.Enabled).ToList();
        Log.Info($"LoadCameras: {_allCameras.Count} enabled cameras, {nvrs.Count} NVRs");
        foreach (var cam in _allCameras)
            cam.Nvr = _nvrMap.GetValueOrDefault(cam.NvrId);

        // Refresh sidebar cameras list
        BuildCamerasList();

        var camMap = _allCameras.ToDictionary(c => c.Id);

        int totalSlots = _gridCols * _gridRows;

        if (_activeView != null)
        {
            // View mode: place cameras according to grid array
            var gridArray = _activeView.GetGridArray();
            Log.Info($"LoadCameras: view grid has {gridArray.Length} slots, {gridArray.Count(g => g.HasValue)} cameras assigned");
            for (int i = 0; i < totalSlots; i++)
            {
                int row = i / _gridCols;
                int col = i % _gridCols;

                var tile = new CameraTile
                {
                    SdkManager = _sdk,
                    Margin = new Thickness(1),
                };

                if (i < gridArray.Length && gridArray[i].HasValue)
                {
                    if (camMap.TryGetValue(gridArray[i]!.Value, out var cam))
                        tile.Camera = cam;
                }

                tile.PreviewMouseLeftButtonDown += (s, ev) =>
                {
                    SelectTile(tile);
                    GetCursorPos(out _dragStartScreen);
                    if (tile.Camera != null && _activeView != null)
                    {
                        _pendingDragTile = tile;
                        Log.Info($"Drag: tile WPF mousedown cam={tile.Camera.Name}, start=({_dragStartScreen.X},{_dragStartScreen.Y})");
                        StartDrag();
                    }
                };
                tile.NativeMouseDown += (s, _) =>
                {
                    SelectTile(tile);
                    GetCursorPos(out _dragStartScreen);
                    if (tile.Camera != null && _activeView != null)
                    {
                        _pendingDragTile = tile;
                        Log.Info($"Drag: tile NATIVE mousedown cam={tile.Camera.Name}, start=({_dragStartScreen.X},{_dragStartScreen.Y})");
                        StartDrag();
                    }
                };
                tile.MouseRightButtonDown += TileRightClick;
                tile.NativeRightClick += (s, _) => TileRightClick(tile, null!);
                tile.FullscreenRequested += Tile_FullscreenRequested;

                Grid.SetRow(tile, row);
                Grid.SetColumn(tile, col);
                CameraGrid.Children.Add(tile);
                _tiles.Add(tile);
            }
        }
        else
        {
            // All Cameras mode: sequential layout
            for (int i = 0; i < totalSlots; i++)
            {
                int row = i / _gridCols;
                int col = i % _gridCols;

                var tile = new CameraTile
                {
                    SdkManager = _sdk,
                    Margin = new Thickness(1),
                };

                if (i < _allCameras.Count)
                    tile.Camera = _allCameras[i];

                tile.MouseLeftButtonDown += (s, _) => SelectTile(tile);
                tile.FullscreenRequested += Tile_FullscreenRequested;

                Grid.SetRow(tile, row);
                Grid.SetColumn(tile, col);
                CameraGrid.Children.Add(tile);
                _tiles.Add(tile);
            }
        }

        // Start previews and update connected status in DB
        var cameraTiles = _tiles.Where(t => t.Camera != null).ToList();
        Log.Info($"LoadCameras: {cameraTiles.Count} tiles with cameras, SDK initialized={_sdk.IsInitialized}");
        if (_sdk.IsInitialized)
        {
            foreach (var tile in cameraTiles)
            {
                Log.Info($"StartPreview: {tile.Camera!.Name} (id={tile.Camera.Id})");
                var cam = tile.Camera;
                _ = tile.StartPreview().ContinueWith(_ =>
                {
                    Dispatcher.Invoke(() => _db.UpdateCameraConnected(cam.Id, cam.Connected));
                }, TaskScheduler.Default);
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

    private async void Ptz_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button btn || _selectedTile?.Camera?.Nvr == null) return;
        var tag = btn.Tag?.ToString();
        if (tag != null && PtzCommands.TryGetValue(tag, out uint cmd))
            await _sdk.PtzMoveAsync(_selectedTile.Camera.Nvr, _selectedTile.Camera.Channel, cmd);
    }

    private async void Ptz_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button btn || _selectedTile?.Camera?.Nvr == null) return;
        var tag = btn.Tag?.ToString();
        if (tag != null && PtzCommands.TryGetValue(tag, out uint cmd))
            await _sdk.PtzStopAsync(_selectedTile.Camera.Nvr, _selectedTile.Camera.Channel, cmd);
    }

    private async void PtzStop_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTile?.Camera?.Nvr == null) return;
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

        if (_activeView != null)
        {
            _activeView.Cols = _gridCols;
            _activeView.Rows = _gridRows;
            var oldGrid = _activeView.GetGridArray();
            var newGrid = new int?[_gridCols * _gridRows];
            Array.Copy(oldGrid, newGrid, Math.Min(oldGrid.Length, newGrid.Length));
            _activeView.SetGridArray(newGrid);
            _db.UpdateView(_activeView);
            UpdateViewHighlight();
        }

        await LoadCameras();
    }

    private async void StreamType_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (StreamTypeCombo.SelectedItem is ComboBoxItem item)
        {
            _streamType = uint.Parse(item.Tag?.ToString() ?? "1");
            if (_tiles.Count > 0)
                await LoadCameras();
        }
    }

    // --- Multi-window ---

    private bool _closeSingleOnly;

    private void NewWindow_Click(object sender, RoutedEventArgs e)
    {
        var window = new MainWindow(_sdk, _db);
        window.Show();
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        // Close just this window
        _closeSingleOnly = true;
        Close();
    }

    private void OpenAdmin_Click(object sender, RoutedEventArgs e)
    {
        var admin = new AdminWindow(_sdk, _db, this);
        admin.ShowDialog();
    }

    // --- Fullscreen ---

    private void Tile_FullscreenRequested(object? sender, EventArgs e)
    {
        if (sender is CameraTile tile && tile.Camera != null)
        {
            var fsWindow = new FullscreenWindow(_sdk, tile.Camera);
            // Position on the same monitor as this window using WPF coordinates
            var isMax = WindowState == WindowState.Maximized;
            fsWindow.Left = isMax ? RestoreBounds.Left : Left;
            fsWindow.Top = isMax ? RestoreBounds.Top : Top;
            fsWindow.Show();
            fsWindow.WindowState = WindowState.Maximized;
        }
    }

    // --- Sidebar toggles ---

    private void ToggleLeftSidebar_Click(object sender, RoutedEventArgs e)
    {
        if (LeftSidebar.Visibility == Visibility.Visible)
        {
            LeftSidebar.Visibility = Visibility.Collapsed;
            BtnToggleLeft.Content = "\u25B6";
        }
        else
        {
            LeftSidebar.Visibility = Visibility.Visible;
            BtnToggleLeft.Content = "\u25C0";
        }
        SaveStateDebounced();
    }

    private void ToggleRightSidebar_Click(object sender, RoutedEventArgs e)
    {
        if (Sidebar.Visibility == Visibility.Visible)
        {
            Sidebar.Visibility = Visibility.Collapsed;
            BtnToggleRight.Content = "\u25C0";
        }
        else
        {
            Sidebar.Visibility = Visibility.Visible;
            BtnToggleRight.Content = "\u25B6";
        }
        SaveStateDebounced();
    }

    private DispatcherTimer? _saveDebounce;

    private void Window_StateChanged(object sender, EventArgs e) => SaveStateDebounced();

    private void SaveStateDebounced()
    {
        // Debounce — save 500ms after the last change
        if (_saveDebounce == null)
        {
            _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _saveDebounce.Tick += (_, _) =>
            {
                _saveDebounce.Stop();
                if (Application.Current is App app)
                    app.SaveAllWindowStates();
            };
        }
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _probeTimer?.Stop();
        _saveDebounce?.Stop();
        // X button closes all windows; "Close Window" button closes just this one
        if (!_closeSingleOnly)
        {
            // Save all window states before closing — windows are removed from
            // Application.Current.Windows as they close, so we must save first
            if (Application.Current is App app)
                app.SaveAllWindowStates();

            var others = new List<MainWindow>();
            foreach (Window w in Application.Current.Windows)
                if (w is MainWindow mw && mw != this)
                    others.Add(mw);

            foreach (var mw in others)
            {
                mw._closeSingleOnly = true; // prevent recursive close-all
                mw.Close();
            }
        }

        foreach (var tile in _tiles)
            await tile.StopPreview();
    }
}
