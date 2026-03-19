using DNVRR.Models;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace DNVRR.Services;

/// <summary>
/// Manages HCNetSDK sessions: login, real-time preview to HWNDs, PTZ control.
/// Thread-safe — SDK calls happen on background threads.
/// </summary>
public class SdkManager : IDisposable
{
    private bool _initialized;
    private readonly ConcurrentDictionary<string, int> _loginSessions = new(); // "ip:port" -> userId
    private readonly ConcurrentDictionary<string, int> _startDChans = new();   // "ip:port" -> startDChan
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _loginLocks = new(); // per-NVR login lock
    private readonly ConcurrentDictionary<int, int> _playHandles = new();     // cameraId -> playHandle
    private readonly object _initLock = new();

    public bool Initialize(string sdkDir)
    {
        lock (_initLock)
        {
            if (_initialized) return true;
            _initialized = HCNetSDK.Initialize(sdkDir);
            if (!_initialized)
                Debug.WriteLine($"HCNetSDK init failed. Error: {HCNetSDK.NET_DVR_GetLastError()}");
            return _initialized;
        }
    }

    public bool IsInitialized => _initialized;

    /// <summary>
    /// Login to NVR, returns userId. Caches sessions per IP:port.
    /// </summary>
    public async Task<int> LoginAsync(NvrInfo nvr)
    {
        var key = $"{nvr.Ip}:{nvr.SdkPort}";
        if (_loginSessions.TryGetValue(key, out int existing))
            return existing;

        // Per-NVR lock to prevent concurrent logins to the same device
        var loginLock = _loginLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await loginLock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (_loginSessions.TryGetValue(key, out existing))
                return existing;

            int result = await Task.Run(() =>
            {
                var deviceInfo = new HCNetSDK.NET_DVR_DEVICEINFO_V30();
                int userId = HCNetSDK.NET_DVR_Login_V30(nvr.Ip, nvr.SdkPort, nvr.Username, nvr.Password, ref deviceInfo);
                if (userId < 0)
                {
                    Log.Error($"SDK login failed for {nvr.Ip}:{nvr.SdkPort}, error={HCNetSDK.NET_DVR_GetLastError()}");
                    return -1;
                }

                int ipChanNum = deviceInfo.byIPChanNum + (deviceInfo.byHighDChanNum << 8);
                nvr.Channels = ipChanNum > 0 ? ipChanNum : deviceInfo.byChanNum;
                int startDChan = deviceInfo.byStartDChan;
                Log.Info($"SDK login OK: {nvr.Ip}, userId={userId}, startDChan={startDChan}, ipChannels={ipChanNum}, analogChannels={deviceInfo.byChanNum}");

                _loginSessions[key] = userId;
                _startDChans[key] = startDChan;
                return userId;
            });

            return result;
        }
        finally
        {
            loginLock.Release();
        }
    }

    /// <summary>
    /// Start real-time preview for a camera, rendering directly to the given HWND.
    /// </summary>
    public async Task<int> StartPreviewAsync(CameraInfo camera, IntPtr hwnd, uint streamType = 1)
    {
        if (camera.Nvr == null) return -1;

        int userId = await LoginAsync(camera.Nvr);
        if (userId < 0) return -1;

        var key = $"{camera.Nvr.Ip}:{camera.Nvr.SdkPort}";
        int startDChan = _startDChans.GetValueOrDefault(key, 33);

        return await Task.Run(() =>
        {
            // Try multiple channel mappings:
            // 1. startDChan + (channel - 1) — standard IP camera mapping
            // 2. channel directly — for analog or pre-mapped channels
            // 3. 32 + channel — legacy fallback
            var channelsToTry = new HashSet<int>
            {
                startDChan + camera.Channel - 1,
                camera.Channel,
                32 + camera.Channel,
            };

            foreach (var ch in channelsToTry)
            {
                var previewInfo = HCNetSDK.MakePreviewInfo(ch, hwnd, streamType);
                int handle = HCNetSDK.NET_DVR_RealPlay_V40(userId, ref previewInfo, null, IntPtr.Zero);
                if (handle >= 0)
                {
                    _playHandles[camera.Id] = handle;
                    camera.SdkPlayHandle = handle;
                    Log.Info($"Preview started: {camera.Name}, ch={ch}, handle={handle}");
                    return handle;
                }
                var err = HCNetSDK.NET_DVR_GetLastError();
                Log.Warn($"Preview failed: {camera.Name}, ch={ch}, error={err}");
            }
            return -1;
        });
    }

    /// <summary>
    /// Probe which channels have a live video feed (no window needed).
    /// Returns a dictionary of channel -> connected status.
    /// </summary>
    public async Task<Dictionary<int, bool>> ProbeChannelsAsync(NvrInfo nvr, List<int> channels)
    {
        int userId = await LoginAsync(nvr);
        if (userId < 0)
            return channels.ToDictionary(ch => ch, _ => true); // assume connected if login fails

        var key = $"{nvr.Ip}:{nvr.SdkPort}";
        int startDChan = _startDChans.GetValueOrDefault(key, 33);

        return await Task.Run(() =>
        {
            // Noop callback — SDK sends data but we ignore it
            HCNetSDK.RealDataCallback noop = (h, t, p, s, u) => { };

            var result = new Dictionary<int, bool>();
            int connectedCount = 0;

            foreach (var ch in channels)
            {
                int sdkCh = startDChan - 1 + ch;
                var previewInfo = HCNetSDK.MakePreviewInfo(sdkCh, IntPtr.Zero, streamType: 1);
                int handle = HCNetSDK.NET_DVR_RealPlay_V40(userId, ref previewInfo, noop, IntPtr.Zero);

                if (handle >= 0)
                {
                    HCNetSDK.NET_DVR_StopRealPlay(handle);
                    result[ch] = true;
                    connectedCount++;
                }
                else
                {
                    var err = HCNetSDK.NET_DVR_GetLastError();
                    // Only error 11 (network receive fail) means no camera
                    // Other errors may be NVR quirks — assume connected
                    result[ch] = err != 11;
                    if (err == 11) Log.Warn($"Probe: {nvr.Ip} ch={ch} (sdk={sdkCh}) — no video feed");
                }
            }

            Log.Info($"SDK probe {nvr.Ip}: {connectedCount}/{channels.Count} channels connected (startDChan={startDChan})");
            // Keep noop alive to prevent GC during probing
            GC.KeepAlive(noop);
            return result;
        });
    }

    /// <summary>
    /// Stop preview for a camera.
    /// </summary>
    public async Task StopPreviewAsync(int cameraId)
    {
        if (_playHandles.TryRemove(cameraId, out int handle))
        {
            await Task.Run(() =>
            {
                HCNetSDK.NET_DVR_StopRealPlay(handle);
                Debug.WriteLine($"Preview stopped: cameraId={cameraId}, handle={handle}");
            });
        }
    }

    /// <summary>
    /// PTZ control — start movement.
    /// </summary>
    public async Task PtzMoveAsync(NvrInfo nvr, int channel, uint command, uint speed = 4)
    {
        int userId = _loginSessions.GetValueOrDefault($"{nvr.Ip}:{nvr.SdkPort}", -1);
        if (userId < 0) return;

        await Task.Run(() =>
        {
            HCNetSDK.NET_DVR_PTZControlWithSpeed_Other(userId, channel, command, 0, speed);
        });
    }

    /// <summary>
    /// PTZ control — stop movement.
    /// </summary>
    public async Task PtzStopAsync(NvrInfo nvr, int channel, uint command)
    {
        int userId = _loginSessions.GetValueOrDefault($"{nvr.Ip}:{nvr.SdkPort}", -1);
        if (userId < 0) return;

        await Task.Run(() =>
        {
            HCNetSDK.NET_DVR_PTZControlWithSpeed_Other(userId, channel, command, 1, 4);
        });
    }

    /// <summary>
    /// Go to PTZ preset.
    /// </summary>
    public async Task PtzGotoPresetAsync(NvrInfo nvr, int channel, uint presetIndex)
    {
        int userId = _loginSessions.GetValueOrDefault($"{nvr.Ip}:{nvr.SdkPort}", -1);
        if (userId < 0) return;

        await Task.Run(() =>
        {
            HCNetSDK.NET_DVR_PTZPreset_Other(userId, channel, HCNetSDK.GOTO_PRESET, presetIndex);
        });
    }

    public void Dispose()
    {
        // Stop all previews
        foreach (var handle in _playHandles.Values)
            HCNetSDK.NET_DVR_StopRealPlay(handle);
        _playHandles.Clear();

        // Logout all sessions
        foreach (var userId in _loginSessions.Values)
            HCNetSDK.NET_DVR_Logout(userId);
        _loginSessions.Clear();

        if (_initialized)
        {
            HCNetSDK.NET_DVR_Cleanup();
            _initialized = false;
        }
    }
}
