using System.IO;
using System.Runtime.InteropServices;

namespace DNVRR.Services;

/// <summary>
/// P/Invoke wrapper for Hikvision HCNetSDK.dll.
/// Provides login, real-time preview (with HWND rendering), and PTZ control.
/// </summary>
public static class HCNetSDK
{
    private const string DllName = "HCNetSDK.dll";

    // --- Structures ---

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_DEVICEINFO_V30
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)]
        public byte[] sSerialNumber;
        public byte byAlarmInPortNum;
        public byte byAlarmOutPortNum;
        public byte byDiskNum;
        public byte byDVRType;
        public byte byChanNum;
        public byte byStartChan;
        public byte byAudioChanNum;
        public byte byIPChanNum;
        public byte byZeroChanNum;
        public byte byMainProto;
        public byte bySubProto;
        public byte bySupport;
        public byte bySupport1;
        public byte bySupport2;
        public ushort wDevType;
        public byte bySupport3;
        public byte byMultiStreamProto;
        public byte byStartDChan;
        public byte byStartDTalkChan;
        public byte byHighDChanNum;
        public byte bySupport4;
        public byte byLanguageType;
        public byte byVoiceInChanNum;
        public byte byStartVoiceInChanNo;
        public byte bySupport5;
        public byte bySupport6;
        public byte byMirrorChanNum;
        public ushort wStartMirrorChanNo;
        public byte bySupport7;
        public byte byRes2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_PREVIEWINFO
    {
        public int lChannel;
        public uint dwStreamType;       // 0=main, 1=sub
        public uint dwLinkMode;         // 0=TCP, 1=UDP
        public IntPtr hPlayWnd;         // Window handle for rendering
        public uint bBlocked;           // 0=non-blocking, 1=blocking
        public uint bPassbackRecord;
        public byte byPreviewMode;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] byStreamID;
        public byte byProtoType;        // 0=private, 1=RTSP
        public byte byRes1;
        public byte byVideoCodingType;
        public uint dwDisplayBufNum;
        public byte byNPQMode;
        public byte byRecvMetaData;
        public byte byDataType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 213)]
        public byte[] byRes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_PTZ_PROTOCOL
    {
        public uint dwType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] byDescribe;
    }

    // --- Constants ---
    public const int NET_DVR_NOERROR = 0;

    // PTZ commands
    public const uint LIGHT_PWRON = 2;
    public const uint WIPER_PWRON = 3;
    public const uint PAN_LEFT = 4;
    public const uint PAN_RIGHT = 5;
    public const uint TILT_UP = 6;
    public const uint TILT_DOWN = 7;
    public const uint ZOOM_IN = 8;
    public const uint ZOOM_OUT = 9;
    public const uint FOCUS_NEAR = 10;
    public const uint FOCUS_FAR = 11;
    public const uint IRIS_OPEN = 12;
    public const uint IRIS_CLOSE = 13;
    public const uint PAN_LEFT_UP = 29;
    public const uint PAN_LEFT_DOWN = 30;
    public const uint PAN_RIGHT_UP = 31;
    public const uint PAN_RIGHT_DOWN = 32;
    public const uint GOTO_PRESET = 39;

    // --- SDK Init/Cleanup ---

    [DllImport(DllName)]
    public static extern bool NET_DVR_Init();

    [DllImport(DllName)]
    public static extern bool NET_DVR_Cleanup();

    [DllImport(DllName)]
    public static extern bool NET_DVR_SetConnectTime(uint dwWaitTime, uint dwTryTimes);

    [DllImport(DllName)]
    public static extern bool NET_DVR_SetReconnect(uint dwInterval, bool bEnableRecon);

    [DllImport(DllName)]
    public static extern uint NET_DVR_GetLastError();

    // --- Login/Logout ---

    [DllImport(DllName)]
    public static extern int NET_DVR_Login_V30(
        string sDVRIP,
        int wDVRPort,
        string sUserName,
        string sPassword,
        ref NET_DVR_DEVICEINFO_V30 lpDeviceInfo);

    [DllImport(DllName)]
    public static extern bool NET_DVR_Logout(int lUserID);

    // --- Real-time Preview ---

    public delegate void RealDataCallback(int lPlayHandle, uint dwDataType, IntPtr pBuffer, uint dwBufSize, IntPtr pUser);

    [DllImport(DllName)]
    public static extern int NET_DVR_RealPlay_V40(
        int lUserID,
        ref NET_DVR_PREVIEWINFO lpPreviewInfo,
        RealDataCallback? fRealDataCallBack,
        IntPtr pUser);

    [DllImport(DllName)]
    public static extern bool NET_DVR_StopRealPlay(int lRealHandle);

    // --- PTZ Control ---

    [DllImport(DllName)]
    public static extern bool NET_DVR_PTZControlWithSpeed_Other(
        int lUserID,
        int lChannel,
        uint dwPTZCommand,
        uint dwStop,    // 0=start, 1=stop
        uint dwSpeed);  // 1-7

    [DllImport(DllName)]
    public static extern bool NET_DVR_PTZPreset_Other(
        int lUserID,
        int lChannel,
        uint dwPTZPresetCmd,  // GOTO_PRESET=39
        uint dwPresetIndex);

    // --- Player Index (for GPU decode) ---

    [DllImport(DllName)]
    public static extern int NET_DVR_GetRealPlayerIndex(int lRealHandle);

    // --- PlayCtrl (hardware decode) ---

    private const string PlayDll = "PlayCtrl.dll";

    // Decode types
    public const int CYCBUF_MODE = 0;
    public const int DECODE_NORMAL = 0;
    public const int DECODE_HIKVISION_GPU = 4;  // Hikvision GPU acceleration

    [DllImport(PlayDll)]
    public static extern bool PlayM4_SetStreamOpenMode(int nPort, uint dwMode);

    [DllImport(PlayDll)]
    public static extern bool PlayM4_SetDecodeType(int nPort, uint nDecType);

    [DllImport(PlayDll)]
    public static extern bool PlayM4_OpenStream(int nPort, IntPtr pFileHeadBuf, uint dwSize, uint dwBufPoolSize);

    [DllImport(PlayDll)]
    public static extern uint PlayM4_GetLastError(int nPort);

    // --- SDK Path (for loading dependent DLLs) ---

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetDllDirectory(string lpPathName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr AddDllDirectory(string newDirectory);

    /// <summary>
    /// Initialize SDK with DLL path. Call once at app startup.
    /// </summary>
    public static bool Initialize(string sdkDir)
    {
        if (!Directory.Exists(sdkDir))
            return false;

        // Add SDK directory and HCNetSDKCom subdirectory to DLL search path
        SetDllDirectory(sdkDir);
        AddDllDirectory(sdkDir);
        var comDir = Path.Combine(sdkDir, "HCNetSDKCom");
        if (Directory.Exists(comDir))
            AddDllDirectory(comDir);

        if (!NET_DVR_Init())
            return false;

        NET_DVR_SetConnectTime(5000, 3);
        NET_DVR_SetReconnect(10000, true);
        return true;
    }

    /// <summary>
    /// Create a PREVIEWINFO struct for HWND-based rendering.
    /// </summary>
    public static NET_DVR_PREVIEWINFO MakePreviewInfo(int channel, IntPtr hwnd, uint streamType = 1)
    {
        return new NET_DVR_PREVIEWINFO
        {
            lChannel = channel,
            dwStreamType = streamType,  // 1=sub stream (lower bandwidth for grid view)
            dwLinkMode = 0,             // TCP
            hPlayWnd = hwnd,            // SDK renders directly to this window
            bBlocked = 1,               // blocking mode
            byPreviewMode = 0,
            byStreamID = new byte[32],
            byProtoType = 0,
            byRes1 = 0,
            byVideoCodingType = 0,
            dwDisplayBufNum = 15,
            byNPQMode = 0,
            byRecvMetaData = 0,
            byDataType = 0,
            byRes = new byte[213],
        };
    }
}
