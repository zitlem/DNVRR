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

        return await Task.Run(() =>
        {
            var deviceInfo = new HCNetSDK.NET_DVR_DEVICEINFO_V30();
            int userId = HCNetSDK.NET_DVR_Login_V30(nvr.Ip, nvr.SdkPort, nvr.Username, nvr.Password, ref deviceInfo);
            if (userId < 0)
            {
                Debug.WriteLine($"SDK login failed for {nvr.Ip}:{nvr.SdkPort}, error={HCNetSDK.NET_DVR_GetLastError()}");
                return -1;
            }

            // Update channel info from device
            int ipChanNum = deviceInfo.byIPChanNum + (deviceInfo.byHighDChanNum << 8);
            nvr.Channels = ipChanNum > 0 ? ipChanNum : deviceInfo.byChanNum;
            Debug.WriteLine($"SDK login OK: {nvr.Ip}, userId={userId}, startDChan={deviceInfo.byStartDChan}, ipChannels={ipChanNum}");

            _loginSessions[key] = userId;
            return userId;
        });
    }

    /// <summary>
    /// Start real-time preview for a camera, rendering directly to the given HWND.
    /// </summary>
    public async Task<int> StartPreviewAsync(CameraInfo camera, IntPtr hwnd, uint streamType = 1)
    {
        if (camera.Nvr == null) return -1;

        int userId = await LoginAsync(camera.Nvr);
        if (userId < 0) return -1;

        return await Task.Run(() =>
        {
            // Try channel directly first, then 32+channel for digital channels
            int[] channelsToTry = camera.Channel >= 33
                ? [camera.Channel]
                : [camera.Channel, 32 + camera.Channel];

            foreach (var ch in channelsToTry)
            {
                var previewInfo = HCNetSDK.MakePreviewInfo(ch, hwnd, streamType);
                int handle = HCNetSDK.NET_DVR_RealPlay_V40(userId, ref previewInfo, null, IntPtr.Zero);
                if (handle >= 0)
                {
                    _playHandles[camera.Id] = handle;
                    camera.SdkPlayHandle = handle;
                    Debug.WriteLine($"Preview started: camera={camera.Name}, channel={ch}, handle={handle}");
                    return handle;
                }
                Debug.WriteLine($"Preview failed: channel={ch}, error={HCNetSDK.NET_DVR_GetLastError()}");
            }
            return -1;
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
