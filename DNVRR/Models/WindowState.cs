namespace DNVRR.Models;

public class SavedWindowState
{
    public int Id { get; set; }
    public int? ViewId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 1280;
    public double Height { get; set; } = 720;
    public bool IsMaximized { get; set; } = true;
    public bool LeftSidebarOpen { get; set; } = true;
    public bool RightSidebarOpen { get; set; } = true;
    public int SortOrder { get; set; }
}
