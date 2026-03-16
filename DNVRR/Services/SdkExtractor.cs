using System.IO;
using System.Reflection;

namespace DNVRR.Services;

public static class SdkExtractor
{
    /// <summary>
    /// Extracts embedded SDK DLLs to a persistent local folder.
    /// Returns the path to the sdk/ directory.
    /// </summary>
    public static string ExtractSdk()
    {
        var sdkDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DNVRR", "sdk");

        var comDir = Path.Combine(sdkDir, "HCNetSDKCom");
        Directory.CreateDirectory(comDir);

        var assembly = Assembly.GetExecutingAssembly();
        var resourceNames = assembly.GetManifestResourceNames();

        // Version marker to re-extract on update
        var versionFile = Path.Combine(sdkDir, ".version");
        var currentVersion = assembly.GetName().Version?.ToString() ?? "0";
        var buildDate = File.GetLastWriteTime(Environment.ProcessPath!).ToString("yyyyMMddHHmmss");
        var marker = $"{currentVersion}-{buildDate}";

        if (File.Exists(versionFile) && File.ReadAllText(versionFile).Trim() == marker)
        {
            Log.Info($"SDK already extracted at {sdkDir}");
            return sdkDir;
        }

        Log.Info($"Extracting {resourceNames.Length} SDK resources to {sdkDir}");

        foreach (var name in resourceNames)
        {
            // Only extract sdk/ resources
            if (!name.StartsWith("sdk/")) continue;

            var relativePath = name.Substring(4); // remove "sdk/"
            var targetPath = Path.Combine(sdkDir, relativePath.Replace('/', Path.DirectorySeparatorChar));

            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream == null) continue;

            using var file = File.Create(targetPath);
            stream.CopyTo(file);
            Log.Info($"  Extracted: {relativePath}");
        }

        File.WriteAllText(versionFile, marker);
        Log.Info("SDK extraction complete");
        return sdkDir;
    }
}
