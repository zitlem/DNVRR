using System.IO;
using System.Windows;
using DNVRR.Services;
using DNVRR.Views;

namespace DNVRR;

public partial class App : Application
{
    private SdkManager? _sdk;
    private Database? _db;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        var appDir = AppDomain.CurrentDomain.BaseDirectory;

        // Logging
        var logPath = Path.Combine(appDir, "data", "dnvrr.log");
        Log.Init(logPath);
        Log.Info($"App directory: {appDir}");

        // Database
        var dbPath = Path.Combine(appDir, "data", "dnvrr.db");
        _db = new Database(dbPath);
        Log.Info("Database opened");

        // SDK initialization
        _sdk = new SdkManager();
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? appDir;
        string? sdkDir = null;

        foreach (var candidate in new[] {
            Path.Combine(exeDir, "sdk"),
            Path.Combine(appDir, "sdk"),
        })
        {
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "HCNetSDK.dll")))
            {
                sdkDir = candidate;
                Log.Info($"SDK found at: {sdkDir}");
                break;
            }
        }

        if (sdkDir == null)
        {
            Log.Info("SDK not found on disk, extracting from embedded resources...");
            try
            {
                sdkDir = SdkExtractor.ExtractSdk();
            }
            catch (Exception ex)
            {
                Log.Error("SDK extraction failed", ex);
            }
        }

        if (sdkDir != null && Directory.Exists(sdkDir))
        {
            if (_sdk.Initialize(sdkDir))
                Log.Info("HCNetSDK initialized successfully");
            else
            {
                Log.Error("HCNetSDK failed to initialize");
                MessageBox.Show("HCNetSDK failed to initialize.\nCheck data/dnvrr.log for details.", "DNVRR", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        else
        {
            Log.Error("No SDK available — video preview will not work");
        }

        // Unhandled exceptions
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("Unhandled exception", ex.Exception);
            ex.Handled = true;
        };

        ShutdownMode = ShutdownMode.OnLastWindowClose;

        // Restore saved window states, or create one default window
        var savedStates = _db.GetWindowStates();
        Log.Info($"Restoring {savedStates.Count} window(s)");

        if (savedStates.Count > 0)
        {
            foreach (var state in savedStates)
            {
                var window = new MainWindow(_sdk, _db, state);
                window.Show();
            }
        }
        else
        {
            var window = new MainWindow(_sdk, _db);
            window.Show();
        }

        // No timer needed — windows save state on every change
    }

    public void SaveAllWindowStates()
    {
        if (_db == null) return;
        try
        {
            var states = new List<Models.SavedWindowState>();
            int i = 0;
            foreach (Window window in Windows)
            {
                if (window is MainWindow mw)
                {
                    var state = mw.GetWindowState();
                    state.SortOrder = i++;
                    states.Add(state);
                }
            }
            if (states.Count > 0)
                _db.SaveWindowStates(states);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save window states", ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SaveAllWindowStates();
        Log.Info("App exiting");
        _sdk?.Dispose();
        _db?.Dispose();
        base.OnExit(e);
    }
}
