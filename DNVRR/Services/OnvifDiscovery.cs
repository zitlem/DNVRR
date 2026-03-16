using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DNVRR.Models;

namespace DNVRR.Services;

/// <summary>
/// ONVIF WS-Discovery multicast probe. Sends a SOAP probe to 239.255.255.250:3702
/// and parses ProbeMatch responses to discover IP cameras and NVRs.
/// </summary>
public static class OnvifDiscovery
{
    private const string MulticastAddr = "239.255.255.250";
    private const int MulticastPort = 3702;

    private static readonly string ProbeTemplate = """
        <?xml version="1.0" encoding="utf-8"?>
        <soap:Envelope
            xmlns:soap="http://www.w3.org/2003/05/soap-envelope"
            xmlns:wsa="http://schemas.xmlsoap.org/ws/2004/08/addressing"
            xmlns:wsd="http://schemas.xmlsoap.org/ws/2005/04/discovery"
            xmlns:wsdp="http://schemas.xmlsoap.org/ws/2006/02/devprof">
          <soap:Header>
            <wsa:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</wsa:Action>
            <wsa:MessageID>urn:uuid:{0}</wsa:MessageID>
            <wsa:To>urn:schemas-xmlsoap-org:ws:2005:04:discovery</wsa:To>
          </soap:Header>
          <soap:Body>
            <wsd:Probe>
              <wsd:Types>wsdp:Device</wsd:Types>
            </wsd:Probe>
          </soap:Body>
        </soap:Envelope>
        """;

    public static async Task<List<DiscoveredDevice>> DiscoverAsync(
        string? adapterIp,
        int timeoutSeconds,
        HashSet<string> existingIps,
        Action<DiscoveredDevice>? onDeviceFound,
        CancellationToken ct)
    {
        var probe = Encoding.UTF8.GetBytes(string.Format(ProbeTemplate, Guid.NewGuid()));
        var devices = new Dictionary<string, DiscoveredDevice>();

        // Create socket bound to the specified adapter
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);

        if (!string.IsNullOrEmpty(adapterIp))
        {
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                IPAddress.Parse(adapterIp).GetAddressBytes());
        }

        socket.ReceiveTimeout = 500;

        // Send probe
        var endpoint = new IPEndPoint(IPAddress.Parse(MulticastAddr), MulticastPort);
        try
        {
            socket.SendTo(probe, endpoint);
            Log.Info($"ONVIF probe sent on {adapterIp ?? "default"}");
        }
        catch (Exception ex)
        {
            Log.Warn($"ONVIF probe send failed: {ex.Message}");
            return [];
        }

        // Collect responses
        var buffer = new byte[65535];
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        await Task.Run(() =>
        {
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                try
                {
                    EndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);
                    int received = socket.ReceiveFrom(buffer, ref remoteEp);
                    if (received > 0)
                    {
                        var device = ParseProbeMatch(buffer, received, existingIps);
                        if (device != null && !devices.ContainsKey(device.Ip))
                        {
                            devices[device.Ip] = device;
                            Log.Info($"ONVIF discovered: {device.Name} ({device.Ip})");
                            onDeviceFound?.Invoke(device);
                        }
                    }
                }
                catch (SocketException) { } // timeout, expected
                catch (Exception ex)
                {
                    Log.Warn($"ONVIF recv error: {ex.Message}");
                }
            }
        }, ct);

        return devices.Values.ToList();
    }

    private static DiscoveredDevice? ParseProbeMatch(byte[] data, int length, HashSet<string> existingIps)
    {
        try
        {
            var xml = Encoding.UTF8.GetString(data, 0, length);
            var doc = XDocument.Parse(xml);

            // Find XAddrs
            string? xaddrs = null;
            foreach (var el in doc.Descendants())
            {
                if (el.Name.LocalName == "XAddrs" && !string.IsNullOrEmpty(el.Value))
                {
                    xaddrs = el.Value.Trim();
                    break;
                }
            }

            if (xaddrs == null) return null;

            // Extract IP and port from XAddrs
            var ipMatch = Regex.Match(xaddrs, @"https?://([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)(?::(\d+))?[/]");
            if (!ipMatch.Success) return null;
            string ip = ipMatch.Groups[1].Value;
            int port = ipMatch.Groups[2].Success ? int.Parse(ipMatch.Groups[2].Value) : 80;

            // Parse scopes for device info
            string name = "", model = "", hardware = "";
            foreach (var el in doc.Descendants())
            {
                if (el.Name.LocalName == "Scopes" && !string.IsNullOrEmpty(el.Value))
                {
                    foreach (var scope in el.Value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var lower = scope.ToLowerInvariant();
                        var lastSeg = scope.Split('/').Last().Replace("%20", " ");
                        if (lower.Contains("/name/"))
                            name = lastSeg;
                        else if (lower.Contains("/hardware/"))
                        {
                            hardware = lastSeg;
                            if (string.IsNullOrEmpty(model)) model = hardware;
                        }
                    }
                    break;
                }
            }

            if (string.IsNullOrEmpty(name))
                name = model.Length > 0 ? model : $"Device at {ip}";

            return new DiscoveredDevice
            {
                Ip = ip,
                Port = port,
                Name = name,
                Model = model,
                AlreadyAdded = existingIps.Contains(ip),
            };
        }
        catch
        {
            return null;
        }
    }
}
