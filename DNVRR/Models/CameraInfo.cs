namespace DNVRR.Models;

public class CameraInfo
{
    public int Id { get; set; }
    public int NvrId { get; set; }
    public int Channel { get; set; }
    public string Name { get; set; } = "";
    public string RtspUrl { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool PtzEnabled { get; set; }
    public bool Connected { get; set; } = true;
    public string? OnvifHost { get; set; }
    public int OnvifPort { get; set; } = 80;

    // Runtime state (not persisted)
    public int SdkPlayHandle { get; set; } = -1;
    public NvrInfo? Nvr { get; set; }
}
