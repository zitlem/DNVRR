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

        // Try basic auth first, fall back to digest
        var basicHandler = new HttpClientHandler { Credentials = new NetworkCredential(_username, _password) };
        var basicClient = new HttpClient(basicHandler) { BaseAddress = new Uri(_baseUrl), Timeout = TimeSpan.FromSeconds(10) };

        try
        {
            var resp = await basicClient.GetAsync("/ISAPI/System/deviceInfo");
            if (resp.StatusCode != HttpStatusCode.Unauthorized)
            {
                _client = basicClient;
                return _client;
            }
        }
        catch { }

        basicClient.Dispose();
        basicHandler.Dispose();

        // Digest auth
        var digestHandler = new HttpClientHandler
        {
            Credentials = new CredentialCache
            {
                { new Uri(_baseUrl), "Digest", new NetworkCredential(_username, _password) }
            }
        };
        _client = new HttpClient(digestHandler) { BaseAddress = new Uri(_baseUrl), Timeout = TimeSpan.FromSeconds(10) };
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
