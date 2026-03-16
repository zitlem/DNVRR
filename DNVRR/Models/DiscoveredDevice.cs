namespace DNVRR.Models;

public class DiscoveredDevice
{
    public string Ip { get; set; } = "";
    public int Port { get; set; } = 80;
    public int SdkPort { get; set; }
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public bool AlreadyAdded { get; set; }
    public string SdkPortDisplay => SdkPort > 0 ? SdkPort.ToString() : "...";
}
