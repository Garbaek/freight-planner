namespace FreightCopilot;

public static class ScreenshotFolderLocator
{
    private const string GameTail = @"StarCitizen\LIVE";
    private const int SearchDepth = 3;
    private static readonly string[] InstallRoots =
    [
        "RSI", "Roberts Space Industries", @"Program Files\Roberts Space Industries",
        @"Games\RSI", @"Games\Roberts Space Industries", "Games", ""
    ];
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "$Recycle.Bin", "System Volume Information", "ProgramData", "Users", "Recovery"
    };

    /// <summary>
    /// Finds "...\StarCitizen\LIVE\screenshots" on a fixed drive. Common RSI launcher locations are
    /// checked first; otherwise the top folder levels of each drive are searched for the game folder.
    /// </summary>
    public static string? Find()
    {
        var liveFolders = new List<string>();
        foreach (var drive in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed && x.IsReady))
        {
            var known = InstallRoots.Select(x => Path.Combine(drive.RootDirectory.FullName, x, GameTail)).Where(Directory.Exists).ToList();
            if (known.Count == 0) Search(drive.RootDirectory.FullName, SearchDepth, known);
            liveFolders.AddRange(known);
        }

        // The game only creates the screenshots folder with the first screenshot, so an install
        // without one is still a valid answer, just a less likely one than a folder in use.
        return liveFolders.Select(x => Path.Combine(x, "screenshots"))
            .OrderByDescending(Directory.Exists)
            .ThenByDescending(x => Directory.Exists(x) ? Directory.GetLastWriteTimeUtc(x) : DateTime.MinValue)
            .FirstOrDefault();
    }

    private static void Search(string directory, int depth, List<string> found)
    {
        try
        {
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                if (SkippedFolders.Contains(name)) continue;
                if (name.Equals("StarCitizen", StringComparison.OrdinalIgnoreCase))
                {
                    var live = Path.Combine(child, "LIVE");
                    if (Directory.Exists(live)) found.Add(live);
                    continue;
                }
                if (depth > 1 && (File.GetAttributes(child) & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) == 0)
                    Search(child, depth - 1, found);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
    }
}
