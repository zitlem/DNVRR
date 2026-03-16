using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DNVRR.Models;

namespace DNVRR.Services;

public class AdapterInfo
{
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "";
    public string Subnet { get; set; } = "";
    public byte[] Network { get; set; } = [];
    public byte[] Mask { get; set; } = [];
    public override string ToString() => $"{Ip} — {Name} ({Subnet})";
}

public static class NetworkScanner
{
    public static List<AdapterInfo> GetAdapters()
    {
        var results = new List<AdapterInfo>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                var ip = addr.Address.GetAddressBytes();
                var mask = addr.IPv4Mask.GetAddressBytes();
                var network = new byte[4];
                for (int i = 0; i < 4; i++)
                    network[i] = (byte)(ip[i] & mask[i]);

                // Skip duplicates
                if (results.Any(r => r.Network.SequenceEqual(network)))
                    continue;

                results.Add(new AdapterInfo
                {
                    Name = nic.Name,
                    Ip = addr.Address.ToString(),
                    Subnet = SubnetLabel(network, mask),
                    Network = network,
                    Mask = mask,
                });
            }
        }

        return results;
    }

    public static List<(byte[] network, byte[] mask)> GetLocalSubnets()
    {
        var results = new List<(byte[], byte[])>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                var ip = addr.Address.GetAddressBytes();
                var mask = addr.IPv4Mask.GetAddressBytes();
                var network = new byte[4];
                for (int i = 0; i < 4; i++)
                    network[i] = (byte)(ip[i] & mask[i]);

                // Skip duplicates
                if (results.Any(r => r.Item1.SequenceEqual(network)))
                    continue;

                results.Add((network, mask));
            }
        }

        return results;
    }

    public static async Task ScanSubnetAsync(
        byte[] network,
        byte[] mask,
        int port,
        int timeoutMs,
        int concurrency,
        IProgress<(int scanned, int total)>? progress,
        Action<DiscoveredDevice> onDeviceFound,
        HashSet<string> existingIps,
        CancellationToken ct)
    {
        // Generate host IPs
        var hostIps = GenerateHostIps(network, mask);
        if (hostIps.Count == 0) return;

        // Cap to avoid scanning huge subnets
        if (hostIps.Count > 1024)
            hostIps = hostIps.Take(1024).ToList();

        int total = hostIps.Count;
        int scanned = 0;

        var semaphore = new SemaphoreSlim(concurrency);
        var tasks = new List<Task>();

        foreach (var ip in hostIps)
        {
            ct.ThrowIfCancellationRequested();
            await semaphore.WaitAsync(ct);

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var result = await ISAPIClient.ProbeAsync(ip, port, timeoutMs);
                    if (result != null)
                    {
                        var device = new DiscoveredDevice
                        {
                            Ip = result.Value.ip,
                            Name = result.Value.name,
                            Model = result.Value.model,
                            AlreadyAdded = existingIps.Contains(result.Value.ip),
                        };
                        onDeviceFound(device);
                    }
                }
                finally
                {
                    semaphore.Release();
                    Interlocked.Increment(ref scanned);
                    progress?.Report((scanned, total));
                }
            }, ct));
        }

        await Task.WhenAll(tasks);
    }

    private static List<string> GenerateHostIps(byte[] network, byte[] mask)
    {
        // Count host bits
        int hostBits = 0;
        for (int i = 0; i < 4; i++)
            for (int b = 0; b < 8; b++)
                if ((mask[i] & (1 << b)) == 0) hostBits++;

        if (hostBits < 2) return []; // /31 or /32 — nothing to scan

        int hostCount = (1 << hostBits) - 2; // exclude network and broadcast
        var ips = new List<string>(Math.Min(hostCount, 1024));

        for (int h = 1; h <= hostCount; h++)
        {
            var ip = new byte[4];
            for (int i = 0; i < 4; i++)
                ip[i] = (byte)(network[i] | ((h >> ((3 - i) * 8)) & ~mask[i] & 0xFF));

            ips.Add(new IPAddress(ip).ToString());
        }

        return ips;
    }

    public static string SubnetLabel(byte[] network, byte[] mask)
    {
        // e.g., "192.168.1.x" for /24
        var ip = new IPAddress(network);
        var parts = ip.ToString().Split('.');

        // Count host bits to determine label
        int hostBits = 0;
        for (int i = 0; i < 4; i++)
            for (int b = 0; b < 8; b++)
                if ((mask[i] & (1 << b)) == 0) hostBits++;

        if (hostBits <= 8)
            return $"{parts[0]}.{parts[1]}.{parts[2]}.x";
        if (hostBits <= 16)
            return $"{parts[0]}.{parts[1]}.x.x";
        return $"{parts[0]}.x.x.x";
    }
}
