namespace DNVRR.Models;

public class NvrInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Alias { get; set; } = "";
    public string Ip { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public int Port { get; set; } = 80;
    public int SdkPort { get; set; } = 8000;
    public int Channels { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? Name : Alias;
}
