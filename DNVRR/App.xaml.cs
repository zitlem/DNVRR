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
        // Database location: next to exe
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var dbPath = Path.Combine(appDir, "data", "dnvrr.db");
        _db = new Database(dbPath);

        // SDK initialization
        _sdk = new SdkManager();
        var sdkDir = Path.Combine(appDir, "sdk");
        if (Directory.Exists(sdkDir))
        {
            if (!_sdk.Initialize(sdkDir))
                MessageBox.Show("HCNetSDK failed to initialize. Video preview will not work.\nPlace SDK DLLs in the 'sdk' folder.", "DNVRR", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        // No warning if sdk/ doesn't exist yet — user can add it later

        var mainWindow = new MainWindow(_sdk, _db);
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _sdk?.Dispose();
        _db?.Dispose();
        base.OnExit(e);
    }
}
