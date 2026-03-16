using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Xml.Linq;
using DNVRR.Models;

namespace DNVRR.Services;

/// <summary>
/// Hikvision ISAPI client for NVR discovery and camera enumeration.
/// </summary>
public class ISAPIClient : IDisposable
{
    private HttpClient? _client;
    private readonly string _baseUrl;
    private readonly string _username;
    private readonly string _password;

    public ISAPIClient(string ip, string username, string password, int port = 80)
    {
        _baseUrl = $"http://{ip}:{port}";
        _username = username;
        _password = password;
    }

    private async Task<HttpClient> GetClient()
    {
        if (_client != null) return _client;

        // Match NVRR approach: try Basic auth first (sent proactively), fall back to Digest
        // Step 1: Basic auth with proactive Authorization header
        var basicClient = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        })
        {
            BaseAddress = new Uri(_baseUrl),
            Timeout = TimeSpan.FromSeconds(10),
        };
        var basicAuth = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{_username}:{_password}"));
        basicClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);

        try
        {
            var resp = await basicClient.GetAsync("/ISAPI/System/deviceInfo");
            Log.Info($"ISAPI Basic {_baseUrl}: status={resp.StatusCode}");

            if (resp.StatusCode != HttpStatusCode.Unauthorized)
            {
                // Basic auth works
                _client = basicClient;
                return _client;
            }
        }
        catch (HttpRequestException ex)
        {
            Log.Warn($"ISAPI {_baseUrl} connection failed: {ex.Message}");
            basicClient.Dispose();
            throw;
        }

        // Step 2: Basic got 401, switch to Digest (like NVRR's httpx.DigestAuth)
        basicClient.Dispose();
        Log.Info($"ISAPI switching to Digest auth for {_baseUrl}");

        var digestHandler = new HttpClientHandler
        {
            Credentials = new CredentialCache
            {
                { new Uri(_baseUrl), "Digest", new NetworkCredential(_username, _password) }
            },
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        _client = new HttpClient(digestHandler)
        {
            BaseAddress = new Uri(_baseUrl),
            Timeout = TimeSpan.FromSeconds(10),
        };
        return _client;
    }

    public async Task<(string name, string model, string serial)?> GetDeviceInfo()
    {
        var client = await GetClient();
        var resp = await client.GetAsync("/ISAPI/System/deviceInfo");
        resp.EnsureSuccessStatusCode();

        var doc = XDocument.Parse(await resp.Content.ReadAsStringAsync());
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

        return (
            doc.Root?.Element(ns + "deviceName")?.Value ?? "Unknown NVR",
            doc.Root?.Element(ns + "model")?.Value ?? "",
            doc.Root?.Element(ns + "serialNumber")?.Value ?? ""
        );
    }

    public async Task<List<CameraInfo>> DiscoverCameras(int nvrId)
    {
        var client = await GetClient();
        var cameras = new List<CameraInfo>();
        var names = await FetchVideoInputNames(client);

        // Try /Streaming/channels
        var resp = await client.GetAsync("/ISAPI/Streaming/channels");
        if (resp.IsSuccessStatusCode)
        {
            var doc = XDocument.Parse(await resp.Content.ReadAsStringAsync());
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
            var seen = new HashSet<int>();

            foreach (var ch in doc.Descendants(ns + "StreamingChannel"))
            {
                var idEl = ch.Element(ns + "id");
                if (idEl == null) continue;

                int chId = int.Parse(idEl.Value);
                if (chId % 100 != 1) continue; // main stream only

                int camNum = chId / 100;
                if (!seen.Add(camNum)) continue;

                var name = names.GetValueOrDefault(camNum)
                    ?? ch.Element(ns + "channelName")?.Value
                    ?? $"Camera {camNum}";

                cameras.Add(new CameraInfo
                {
                    NvrId = nvrId,
                    Channel = camNum,
                    Name = name,
                    RtspUrl = $"rtsp://{_username}:{_password}@{new Uri(_baseUrl).Host}:554/Streaming/Channels/{chId}",
                });
            }
        }
        else if (names.Count > 0)
        {
            foreach (var (camNum, name) in names.OrderBy(kv => kv.Key))
            {
                int chId = camNum * 100 + 1;
                cameras.Add(new CameraInfo
                {
                    NvrId = nvrId,
                    Channel = camNum,
                    Name = name,
                    RtspUrl = $"rtsp://{_username}:{_password}@{new Uri(_baseUrl).Host}:554/Streaming/Channels/{chId}",
                });
            }
        }

        return cameras;
    }

    private async Task<Dictionary<int, string>> FetchVideoInputNames(HttpClient client)
    {
        var names = new Dictionary<int, string>();

        // Try Video/inputs/channels
        try
        {
            var resp = await client.GetAsync("/ISAPI/System/Video/inputs/channels");
            if (resp.IsSuccessStatusCode)
            {
                var doc = XDocument.Parse(await resp.Content.ReadAsStringAsync());
                var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
                foreach (var ch in doc.Descendants(ns + "VideoInputChannel"))
                {
                    var id = ch.Element(ns + "id")?.Value;
                    var name = ch.Element(ns + "name")?.Value?.Trim();
                    if (id != null && !string.IsNullOrEmpty(name) && !int.TryParse(name, out _))
                        names[int.Parse(id)] = name;
                }
                if (names.Count > 0) return names;
            }
        }
        catch { }

        // Try ContentMgmt/InputProxy/channels
        try
        {
            var resp = await client.GetAsync("/ISAPI/ContentMgmt/InputProxy/channels");
            if (resp.IsSuccessStatusCode)
            {
                var doc = XDocument.Parse(await resp.Content.ReadAsStringAsync());
                var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
                foreach (var ch in doc.Descendants(ns + "InputProxyChannel"))
                {
                    var id = ch.Element(ns + "id")?.Value;
                    var name = ch.Element(ns + "name")?.Value?.Trim();
                    if (id != null && !string.IsNullOrEmpty(name) && !int.TryParse(name, out _))
                        names[int.Parse(id)] = name;
                }
            }
        }
        catch { }

        return names;
    }

    /// <summary>
    /// Discover the HTTP port by probing common ports for ISAPI.
    /// </summary>
    public static async Task<int?> DiscoverHttpPortAsync(string ip, int timeoutMs = 3000)
    {
        int[] ports = [80, 81, 82, 83, 84, 85, 443, 8080, 8443, 8000, 8888];

        foreach (var port in ports)
        {
            try
            {
                var scheme = port == 443 || port == 8443 ? "https" : "http";
                using var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
                var resp = await client.GetAsync($"{scheme}://{ip}:{port}/ISAPI/System/deviceInfo");
                if (resp.StatusCode == HttpStatusCode.OK || resp.StatusCode == HttpStatusCode.Unauthorized)
                {
                    Log.Info($"HTTP port discovered: {ip}:{port} ({scheme})");
                    return port;
                }
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Probe SDK ports (8000-8005, 8200) and return the first open one.
    /// </summary>
    public static async Task<int?> DiscoverSdkPortAsync(string ip, int timeoutMs = 2000)
    {
        int[] ports = [8000, 8001, 8002, 8003, 8004, 8005, 8200];

        var tasks = ports.Select(async port =>
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                var connectTask = client.ConnectAsync(ip, port);
                if (await Task.WhenAny(connectTask, Task.Delay(timeoutMs)) == connectTask && client.Connected)
                    return port;
            }
            catch { }
            return (int?)null;
        });

        var results = await Task.WhenAll(tasks);
        return results.FirstOrDefault(p => p.HasValue);
    }

    /// <summary>
    /// Quick probe to check if an IP has ISAPI (no auth needed).
    /// </summary>
    public static async Task<(string ip, string name, string model)?> ProbeAsync(string ip, int port = 80, int timeoutMs = 2000)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            var resp = await client.GetAsync($"http://{ip}:{port}/ISAPI/System/deviceInfo");
            if (resp.StatusCode == HttpStatusCode.OK || resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                string name = $"Hikvision at {ip}";
                string model = "";
                if (resp.StatusCode == HttpStatusCode.OK)
                {
                    try
                    {
                        var doc = XDocument.Parse(await resp.Content.ReadAsStringAsync());
                        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
                        name = doc.Root?.Element(ns + "deviceName")?.Value ?? name;
                        model = doc.Root?.Element(ns + "model")?.Value ?? "";
                    }
                    catch { }
                }
                return (ip, name, model);
            }
        }
        catch { }
        return null;
    }

    public void Dispose()
    {
        _client?.Dispose();
    }
}
