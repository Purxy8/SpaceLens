namespace DesktopOrganizer;

internal sealed record FolderTotal(
    string Path,
    long DiskBytes,
    long LogicalBytes,
    int FileCount,
    int EstimatedCount,
    int SharedLinkCount,
    int AppFileCount,
    bool DirectFiles = false,
    bool Overflow = false)
{
    internal string Name { get; } = DirectFiles
        ? "[Files directly in this folder]"
        : Overflow
            ? "[Additional folders — safety limit reached]"
            : System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar));

    internal string Kind { get; } = DirectFiles
        ? "Files here"
        : Overflow
            ? "Summary"
            : AnalyzerForm.IsKnownGameLibraryPath(Path) || AppFileCount * 2 >= Math.Max(1, FileCount)
                ? "Game / app"
                : "Folder";
}

internal readonly record struct FolderBuildResult(
    List<FolderTotal> VisibleFolders,
    int FolderCount,
    bool Truncated,
    int MatchingFileCount,
    long DiskBytes,
    long LogicalBytes,
    int EstimatedCount);

/// <summary>
/// Builds a bounded, one-level folder view from the already indexed files.
/// Each matching file contributes to exactly one immediate child of the
/// selected folder (or to the direct-files row), so recursive folder totals
/// are available without retaining a second million-node directory tree.
/// </summary>
internal sealed class FolderAccumulator
{
    private static readonly string[] DetectedInstallMarkers = ["\\steamapps\\common\\", "\\Ubisoft Game Launcher\\games\\", "\\XboxGames\\", "\\Epic Games\\", "\\GOG Games\\", "\\EA Games\\", "\\Riot Games\\", "\\ModifiableWindowsApps\\", "\\WindowsApps\\", "\\Games\\", "\\AppData\\Local\\Programs\\", "\\Program Files (x86)\\", "\\Program Files\\"];
    private const int MaximumFolderBuckets = 250_000;
    private const int VisibleFolderLimit = 10_000;
    private const long MaximumFolderPathCharacters = 64L * 1024 * 1024;

    private readonly string folder;
    private readonly bool detectedGames;
    private readonly Dictionary<string, MutableFolderTotal> children = new(StringComparer.Ordinal);
    private readonly HashSet<string> detectedInstallRoots = new(StringComparer.Ordinal);
    private MutableFolderTotal? directFiles;
    private MutableFolderTotal? overflow;
    private readonly string?[] recentGroupPaths = new string?[8];
    private readonly MutableFolderTotal?[] recentGroups = new MutableFolderTotal?[8];
    private readonly string?[] recentDiscoveryDirectories = new string?[16];
    private readonly string?[] recentAggregationDirectories = new string?[16];
    private readonly string?[] recentAggregationGroups = new string?[16];
    private int recentGroupCursor;
    private int recentDiscoveryDirectoryCursor;
    private int recentAggregationDirectoryCursor;
    private long folderPathCharacters;
    private long detectedInstallPathCharacters;
    private bool bucketLimitReached;
    private int matchingFileCount;
    private long diskBytes;
    private long logicalBytes;
    private int estimatedCount;

    internal FolderAccumulator(string folderPath, bool detectedGames = false)
    {
        folder = NormalizeFolder(folderPath);
        this.detectedGames = detectedGames;
    }

    internal void Add(FileItem item)
    {
        if (detectedGames)
        {
            if (!TryGetRecentAggregationGroup(item.Path, out string groupRoot))
            {
                groupRoot = string.Empty;
                if (TryGetDetectedInstallRoot(item.Path, out string installRoot)
                    && detectedInstallRoots.Contains(installRoot)
                    && TryGetVisibleInstallRoot(installRoot, out string visibleRoot)) groupRoot = visibleRoot;
                RememberAggregationGroup(item.Path, groupRoot);
            }

            if (groupRoot.Length == 0) return;
            AddScopeTotals(item);
            AddDetectedInstall(groupRoot, item);
            return;
        }

        ReadOnlySpan<char> path = item.Path.AsSpan();
        if (!TryGetRelativeStart(path, folder.AsSpan(), out int relativeStart)) return;
        AddScopeTotals(item);

        int nestedSeparator = IndexOfSeparator(path[relativeStart..]);
        if (nestedSeparator < 0)
        {
            directFiles ??= new MutableFolderTotal(folder);
            directFiles.Add(item);
            return;
        }

        int groupLength = checked(relativeStart + nestedSeparator);
        ReadOnlySpan<char> groupSpan = path[..groupLength];
        MutableFolderTotal total;
        if (!TryGetRecentGroup(groupSpan, out total!))
        {
            string groupPath = new(groupSpan);
            if (!children.TryGetValue(groupPath, out total!))
            {
                long nextCharacters;
                try { nextCharacters = checked(folderPathCharacters + groupPath.Length); }
                catch (OverflowException) { nextCharacters = long.MaxValue; }
                if (children.Count >= MaximumFolderBuckets || nextCharacters > MaximumFolderPathCharacters)
                {
                    bucketLimitReached = true;
                    overflow ??= new MutableFolderTotal(folder, overflow: true);
                    overflow.Add(item);
                    return;
                }

                total = new MutableFolderTotal(groupPath);
                children.Add(groupPath, total);
                folderPathCharacters = nextCharacters;
            }
            RememberGroup(total);
        }
        total.Add(item);
    }

    internal void DiscoverInstall(FileItem item)
    {
        if (!detectedGames || item.Category != "Apps & games") return;
        ReadOnlySpan<char> directory = GetDirectorySpan(item.Path);
        if (IsRecentDiscoveryDirectory(directory)) return;
        RememberDiscoveryDirectory(directory);
        if (!TryGetDetectedInstallRoot(item.Path, out string installRoot)
            || !TryGetVisibleInstallRoot(installRoot, out _)) return;
        if (detectedInstallRoots.Contains(installRoot)) return;
        long nextCharacters;
        try { nextCharacters = checked(detectedInstallPathCharacters + installRoot.Length); }
        catch (OverflowException) { nextCharacters = long.MaxValue; }
        if (detectedInstallRoots.Count >= MaximumFolderBuckets || nextCharacters > MaximumFolderPathCharacters)
        {
            bucketLimitReached = true;
            return;
        }
        detectedInstallRoots.Add(installRoot);
        detectedInstallPathCharacters = nextCharacters;
    }

    internal static bool TryGetDetectedInstallRoot(string filePath, out string installRoot)
    {
        installRoot = string.Empty;
        string full;
        try { full = System.IO.Path.GetFullPath(filePath); }
        catch { return false; }
        foreach (string marker in DetectedInstallMarkers)
        {
            int markerIndex = full.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0) continue;
            if (marker.Equals("\\Games\\", StringComparison.OrdinalIgnoreCase) && !AnalyzerForm.IsKnownGameLibraryPath(full)) continue;
            int componentStart = checked(markerIndex + marker.Length);
            int componentLength = IndexOfSeparator(full.AsSpan(componentStart));
            if (componentLength <= 0) continue;
            installRoot = full[..checked(componentStart + componentLength)];
            return true;
        }
        return false;
    }

    internal FolderBuildResult Complete(string? column, bool ascending, CancellationToken token)
    {
        var totals = new List<FolderTotal>(children.Count + (directFiles is null ? 0 : 1) + (overflow is null ? 0 : 1));
        int index = 0;
        foreach (MutableFolderTotal total in children.Values)
        {
            if ((index++ & 4095) == 0) token.ThrowIfCancellationRequested();
            totals.Add(total.Freeze());
        }
        if (directFiles is not null) totals.Add(directFiles.Freeze(directFiles: true));
        if (overflow is not null) totals.Add(overflow.Freeze(overflow: true));
        token.ThrowIfCancellationRequested();

        totals.Sort((left, right) => Compare(left, right, column, ascending));
        int totalCount = totals.Count;
        bool truncated = bucketLimitReached || totalCount > VisibleFolderLimit;
        if (totals.Count > VisibleFolderLimit) totals.RemoveRange(VisibleFolderLimit, totals.Count - VisibleFolderLimit);
        return new(totals, totalCount, truncated, matchingFileCount, diskBytes, logicalBytes, estimatedCount);
    }

    internal static bool TryGetImmediateChildForTest(string filePath, string folderPath, out string childPath, out bool direct)
    {
        string normalizedFolder = NormalizeFolder(folderPath);
        ReadOnlySpan<char> path = System.IO.Path.GetFullPath(filePath).AsSpan();
        if (!TryGetRelativeStart(path, normalizedFolder.AsSpan(), out int relativeStart))
        {
            childPath = string.Empty;
            direct = false;
            return false;
        }
        int separator = IndexOfSeparator(path[relativeStart..]);
        direct = separator < 0;
        childPath = direct ? normalizedFolder : new string(path[..checked(relativeStart + separator)]);
        return true;
    }

    private static int Compare(FolderTotal left, FolderTotal right, string? column, bool ascending)
    {
        int value = column switch
        {
            "Name" => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name),
            "LogicalSize" => left.LogicalBytes.CompareTo(right.LogicalBytes),
            "Files" => left.FileCount.CompareTo(right.FileCount),
            "Kind" => StringComparer.OrdinalIgnoreCase.Compare(left.Kind, right.Kind),
            "Path" => StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path),
            _ => left.DiskBytes.CompareTo(right.DiskBytes)
        };
        if (!ascending) value = value < 0 ? 1 : value > 0 ? -1 : 0;
        return value != 0 ? value : StringComparer.Ordinal.Compare(left.Path, right.Path);
    }

    private bool TryGetRecentGroup(ReadOnlySpan<char> path, out MutableFolderTotal? total)
    {
        for (int index = 0; index < recentGroupPaths.Length; index++)
        {
            string? recentPath = recentGroupPaths[index];
            if (recentPath is not null && path.SequenceEqual(recentPath.AsSpan()))
            {
                total = recentGroups[index];
                return total is not null;
            }
        }
        total = null;
        return false;
    }

    private void AddScopeTotals(FileItem item)
    {
        matchingFileCount = checked(matchingFileCount + 1);
        diskBytes = checked(diskBytes + item.DiskBytes);
        logicalBytes = checked(logicalBytes + item.LogicalBytes);
        if (item.AllocationEstimated) estimatedCount = checked(estimatedCount + 1);
    }

    private void AddDetectedInstall(string installRoot, FileItem item)
    {
        if (!children.TryGetValue(installRoot, out MutableFolderTotal? total))
        {
            long nextCharacters;
            try { nextCharacters = checked(folderPathCharacters + installRoot.Length); }
            catch (OverflowException) { nextCharacters = long.MaxValue; }
            if (children.Count >= MaximumFolderBuckets || nextCharacters > MaximumFolderPathCharacters)
            {
                bucketLimitReached = true;
                overflow ??= new MutableFolderTotal(folder, overflow: true);
                overflow.Add(item);
                return;
            }
            total = new MutableFolderTotal(installRoot);
            children.Add(installRoot, total);
            folderPathCharacters = nextCharacters;
        }
        total.Add(item);
    }

    private void RememberGroup(MutableFolderTotal total)
    {
        int index = recentGroupCursor++ & (recentGroupPaths.Length - 1);
        recentGroupPaths[index] = total.Path;
        recentGroups[index] = total;
    }

    private bool IsRecentDiscoveryDirectory(ReadOnlySpan<char> directory)
    {
        for (int index = 0; index < recentDiscoveryDirectories.Length; index++)
        {
            string? recent = recentDiscoveryDirectories[index];
            if (recent is not null && directory.SequenceEqual(recent.AsSpan())) return true;
        }
        return false;
    }

    private void RememberDiscoveryDirectory(ReadOnlySpan<char> directory)
    {
        int index = recentDiscoveryDirectoryCursor++ & (recentDiscoveryDirectories.Length - 1);
        recentDiscoveryDirectories[index] = new string(directory);
    }

    private bool TryGetRecentAggregationGroup(string filePath, out string groupRoot)
    {
        ReadOnlySpan<char> directory = GetDirectorySpan(filePath);
        for (int index = 0; index < recentAggregationDirectories.Length; index++)
        {
            string? recent = recentAggregationDirectories[index];
            if (recent is not null && directory.SequenceEqual(recent.AsSpan()))
            {
                groupRoot = recentAggregationGroups[index] ?? string.Empty;
                return true;
            }
        }
        groupRoot = string.Empty;
        return false;
    }

    private void RememberAggregationGroup(string filePath, string groupRoot)
    {
        int index = recentAggregationDirectoryCursor++ & (recentAggregationDirectories.Length - 1);
        recentAggregationDirectories[index] = new string(GetDirectorySpan(filePath));
        recentAggregationGroups[index] = groupRoot;
    }

    private bool TryGetVisibleInstallRoot(string installRoot, out string groupRoot)
    {
        if (installRoot.AsSpan().SequenceEqual(folder.AsSpan())
            || TryGetRelativeStart(folder.AsSpan(), installRoot.AsSpan(), out _))
        {
            groupRoot = folder;
            return true;
        }
        if (TryGetRelativeStart(installRoot.AsSpan(), folder.AsSpan(), out _))
        {
            groupRoot = installRoot;
            return true;
        }
        groupRoot = string.Empty;
        return false;
    }

    private static ReadOnlySpan<char> GetDirectorySpan(string filePath)
    {
        ReadOnlySpan<char> path = filePath.AsSpan();
        int primary = path.LastIndexOf(System.IO.Path.DirectorySeparatorChar);
        int alternate = path.LastIndexOf(System.IO.Path.AltDirectorySeparatorChar);
        int separator = Math.Max(primary, alternate);
        return separator > 0 ? path[..separator] : ReadOnlySpan<char>.Empty;
    }

    private static bool TryGetRelativeStart(ReadOnlySpan<char> filePath, ReadOnlySpan<char> folderPath, out int relativeStart)
    {
        relativeStart = 0;
        if (filePath.Length <= folderPath.Length + 1
            || !filePath.StartsWith(folderPath, StringComparison.Ordinal)
            || !IsSeparator(filePath[folderPath.Length])) return false;
        relativeStart = folderPath.Length + 1;
        return true;
    }

    private static int IndexOfSeparator(ReadOnlySpan<char> value)
    {
        int primary = value.IndexOf(System.IO.Path.DirectorySeparatorChar);
        int alternate = value.IndexOf(System.IO.Path.AltDirectorySeparatorChar);
        if (primary < 0) return alternate;
        if (alternate < 0) return primary;
        return Math.Min(primary, alternate);
    }

    private static bool IsSeparator(char value)
        => value == System.IO.Path.DirectorySeparatorChar || value == System.IO.Path.AltDirectorySeparatorChar;

    private static string NormalizeFolder(string value)
        => System.IO.Path.GetFullPath(value).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

    private sealed class MutableFolderTotal(string path, bool overflow = false)
    {
        internal string Path { get; } = path;
        private long diskBytes;
        private long logicalBytes;
        private int fileCount;
        private int estimatedCount;
        private int sharedLinkCount;
        private int appFileCount;
        private readonly bool isOverflow = overflow;

        internal void Add(FileItem item)
        {
            diskBytes = checked(diskBytes + item.DiskBytes);
            logicalBytes = checked(logicalBytes + item.LogicalBytes);
            fileCount = checked(fileCount + 1);
            if (item.AllocationEstimated) estimatedCount = checked(estimatedCount + 1);
            if (item.FileIndex != 0 && item.LogicalBytes > 0 && item.DiskBytes == 0) sharedLinkCount = checked(sharedLinkCount + 1);
            if (item.Category == "Apps & games") appFileCount = checked(appFileCount + 1);
        }

        internal FolderTotal Freeze(bool directFiles = false, bool overflow = false)
            => new(Path, diskBytes, logicalBytes, fileCount, estimatedCount, sharedLinkCount, appFileCount, directFiles, overflow || isOverflow);
    }
}
