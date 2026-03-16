using System.Text.Json;

namespace DNVRR.Models;

public class ViewInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public int Cols { get; set; } = 4;
    public int Rows { get; set; } = 3;
    public string Grid { get; set; } = "[]";
    public int SortOrder { get; set; }

    public int?[] GetGridArray()
    {
        try
        {
            return JsonSerializer.Deserialize<int?[]>(Grid) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void SetGridArray(int?[] arr)
    {
        Grid = JsonSerializer.Serialize(arr);
    }

    public static string Slugify(string name)
    {
        return string.Join("-", name.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-')
            .Select(c => c == ' ' ? '-' : c)
            .Aggregate("", (s, c) => s + c)
            .Split('-', StringSplitOptions.RemoveEmptyEntries));
    }
}
