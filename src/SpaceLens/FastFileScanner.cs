using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DesktopOrganizer;

/// <summary>
/// Enumerates a directory a buffer at a time. On Windows,
/// FILE_ID_EXTD_DIR_INFO (or FILE_ID_BOTH_DIR_INFO as a compatibility fallback)
/// supplies logical and allocated size, timestamps, attributes, reparse tags,
/// and file IDs without opening every file. A managed fallback remains for file
/// systems that implement neither native information class.
/// </summary>
internal static class FastFileScanner
{
    private const int ReportBatchSize = 8_000;
    private const int MaximumQueuedDirectories = 250_000;
    private const int MaximumTraversedDirectories = 1_000_000;
    private const long MaximumDirectoryPathCharacters = 128L * 1024 * 1024;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint CloudTagMask = 0xFFFF0FFF;
    private const uint CloudTagBase = 0x9000001A;
    private const uint ReparseTagNameSurrogate = 0x20000000;

    internal static int NativeBufferSizeForDiagnostics => NativeDirectoryEnumerator.BufferSizeForDiagnostics;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation
    {
        internal FileAttributes FileAttributes;
        internal uint ReparseTag;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes, FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileAttributeTagInformation information, uint bufferSize);

    internal static bool ProbeNativeEnumeration(string directory)
    {
        try
        {
            if (!NativeResolvedPath.TryResolveDirectory(directory, out string resolved, out _)
                || !NativeFileIdentity.TryGet(resolved, true, out NativeFileInformation identity)) return false;
            using var native = new NativeDirectoryEnumerator();
            return native.Enumerate(resolved, identity.Id, resolved, identity.Id.VolumeSerial, IsWholeVolumeRoot(resolved), CancellationToken.None, (_, _) => { }, out _) == NativeEnumerationResult.Complete;
        }
        catch { return false; }
    }

    internal static bool IsKnownCloudFileReparsePoint(string path)
    {
        try
        {
            using SafeFileHandle handle = CreateFileW(NativePath.For(path), 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, FileFlagOpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 9, out FileAttributeTagInformation information, (uint)Marshal.SizeOf<FileAttributeTagInformation>())) return false;
            return (information.FileAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.ReparsePoint
                && IsCloudTag(information.ReparseTag);
        }
        catch { return false; }
    }

    internal static int Scan(string root, IProgress<(List<FileItem>, int)> progress, CancellationToken token, bool strictReparseDirectories = false)
    {
        ArgumentNullException.ThrowIfNull(progress);

        string fullRoot;
        try { fullRoot = Path.GetFullPath(root); }
        catch { return 1; }

        if (!Directory.Exists(fullRoot)) return 1;
        if (!NativeResolvedPath.TryResolveDirectory(fullRoot, out fullRoot, out _)) return 1;

        var pending = new Stack<PendingDirectory>();
        var traversalBudget = new TraversalBudget();
        var visitedDirectories = new HashSet<NativeFileId>();
        var countedFiles = new HashSet<NativeFileId>();
        var batch = new List<FileItem>(ReportBatchSize);
        bool wholeVolumeRoot = IsWholeVolumeRoot(fullRoot);
        uint rootVolume = 0;
        int skipped = 0;

        if (NativeFileIdentity.TryGet(fullRoot, true, out var rootIdentity))
        {
            rootVolume = rootIdentity.Id.VolumeSerial;
            if (rootIdentity.Id.FileIndex != 0) visitedDirectories.Add(rootIdentity.Id);
            traversalBudget.Queue(pending, new(fullRoot, rootIdentity.Id));
        }
        else traversalBudget.Queue(pending, new(fullRoot, default));

        using var native = new NativeDirectoryEnumerator();
        try
        {
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                PendingDirectory queued = pending.Pop();
                NativeEnumerationResult result = native.Enumerate(
                    queued.Path,
                    queued.ExpectedId,
                    fullRoot,
                    rootVolume,
                    wholeVolumeRoot,
                    token,
                    (directory, entry) => ProcessNativeEntry(directory, entry, fullRoot, rootVolume, pending, traversalBudget, visitedDirectories, countedFiles, ref batch, ref skipped, progress, strictReparseDirectories),
                    out string validatedDirectory);

                if (result == NativeEnumerationResult.UnsupportedBeforeFirstEntry)
                {
                    if (strictReparseDirectories) skipped++;
                    else
                    {
                        string fallbackPath = validatedDirectory.Length == 0 ? queued.Path : validatedDirectory;
                        if (!NativeResolvedPath.TryResolveDirectory(fallbackPath, out fallbackPath, out _)
                            || !NativeResolvedPath.IsUnderOrEqual(fallbackPath, fullRoot)
                            || !NativeFileIdentity.TryGet(fallbackPath, true, out NativeFileInformation fallbackIdentity)
                            || (queued.ExpectedId.FileIndex != 0 && fallbackIdentity.Id != queued.ExpectedId)
                            || (rootVolume != 0 && fallbackIdentity.Id.VolumeSerial != rootVolume)) skipped++;
                        else EnumerateManaged(fallbackPath, fullRoot, rootVolume, pending, traversalBudget, visitedDirectories, countedFiles, ref batch, ref skipped, progress, token, false);
                    }
                }
                else if (result == NativeEnumerationResult.Failed)
                {
                    // Do not retry after a partially returned native enumeration:
                    // that would duplicate files already reported to the UI.
                    skipped++;
                }
            }
        }
        catch (DirectoryTraversalLimitException)
        {
            if (batch.Count > 0) progress.Report((batch, skipped));
            throw;
        }
        catch (OperationCanceledException)
        {
            if (batch.Count > 0) progress.Report((batch, skipped));
            throw;
        }

        if (batch.Count > 0) progress.Report((batch, skipped));
        return skipped;
    }

    private static void ProcessNativeEntry(
        string directory,
        NativeDirectoryEntry entry,
        string scanRoot,
        uint rootVolume,
        Stack<PendingDirectory> pending,
        TraversalBudget traversalBudget,
        HashSet<NativeFileId> visitedDirectories,
        HashSet<NativeFileId> countedFiles,
        ref List<FileItem> batch,
        ref int skipped,
        IProgress<(List<FileItem>, int)> progress,
        bool strictReparseDirectories)
    {
        string path;
        try
        {
            if (Path.IsPathRooted(entry.Name) || entry.Name.Contains(Path.DirectorySeparatorChar) || entry.Name.Contains(Path.AltDirectorySeparatorChar) || entry.Name is "." or "..") { skipped++; return; }
            path = Path.Combine(directory, entry.Name);
        }
        catch { skipped++; return; }

        bool isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
        bool isReparsePoint = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
        NativeFileId id = rootVolume != 0 && entry.FileIndex != 0 ? new(rootVolume, entry.FileIndex) : default;

        if (isDirectory)
        {
            if (isReparsePoint)
            {
                bool cloudDirectory = entry.ReparseTagKnown
                    ? IsCloudTag(entry.ReparseTag)
                    : IsCloudDirectory(path);
                if (!cloudDirectory
                    || !NativeResolvedPath.TryResolveDirectory(path, out string resolvedDirectory, out _)
                    || !NativeResolvedPath.IsStrictlyUnder(resolvedDirectory, scanRoot)
                    || !NativeFileIdentity.TryGet(resolvedDirectory, true, out NativeFileInformation resolvedIdentity)
                    || (rootVolume != 0 && resolvedIdentity.Id.VolumeSerial != rootVolume)) { skipped++; return; }
                path = resolvedDirectory;
                id = resolvedIdentity.Id;
            }
            if (id.FileIndex != 0 && !visitedDirectories.Add(id)) { skipped++; return; }
            traversalBudget.Queue(pending, new(path, id));
            return;
        }

        // Cloud placeholders are also reparse points, but are not links. Keep
        // them so their local allocation (often zero) remains visible.
        if (isReparsePoint
            && (entry.ReparseTagKnown ? IsNameSurrogateTag(entry.ReparseTag) : IsFileLink(path))) { skipped++; return; }

        try
        {
            (string category, FileSafety safety) = AnalyzerForm.ClassifyForScan(path, entry.Attributes, pathIsCanonical: true);
            FileItem item = new(
                path,
                entry.AllocationSize,
                entry.EndOfFile,
                ToLocalFileTime(entry.LastWriteTime),
                category,
                ToLocalFileTime(entry.CreationTime),
                AllocationEstimated: false,
                id.VolumeSerial,
                id.FileIndex,
                entry.Attributes,
                safety);
            if (id.FileIndex != 0 && !countedFiles.Add(id)) item = item with { DiskBytes = 0 };
            batch.Add(item);
            FlushIfFull(ref batch, skipped, progress);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException or OverflowException)
        {
            skipped++;
        }
    }

    private static void EnumerateManaged(
        string directory,
        string scanRoot,
        uint rootVolume,
        Stack<PendingDirectory> pending,
        TraversalBudget traversalBudget,
        HashSet<NativeFileId> visitedDirectories,
        HashSet<NativeFileId> countedFiles,
        ref List<FileItem> batch,
        ref int skipped,
        IProgress<(List<FileItem>, int)> progress,
        CancellationToken token,
        bool strictReparseDirectories)
    {
        try
        {
            foreach (string child in Directory.EnumerateDirectories(directory))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    string candidate = child;
                    var info = new DirectoryInfo(candidate);
                    bool reparse = (info.Attributes & FileAttributes.ReparsePoint) != 0;
                    if (reparse)
                    {
                        if (!IsCloudDirectory(candidate)
                            || !NativeResolvedPath.TryResolveDirectory(candidate, out candidate, out _)
                            || !NativeResolvedPath.IsStrictlyUnder(candidate, scanRoot)) { skipped++; continue; }
                    }
                    NativeFileId childId = default;
                    if (NativeFileIdentity.TryGet(candidate, true, out var identity))
                    {
                        if ((rootVolume != 0 && identity.Id.VolumeSerial != rootVolume) || !visitedDirectories.Add(identity.Id)) { skipped++; continue; }
                        childId = identity.Id;
                    }
                    else if (reparse) { skipped++; continue; }
                    traversalBudget.Queue(pending, new(candidate, childId));
                }
                catch (DirectoryTraversalLimitException) { throw; }
                catch (Exception ex) when (IsExpectedFileSystemFailure(ex)) { skipped++; }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (DirectoryTraversalLimitException) { throw; }
        catch (Exception ex) when (IsExpectedFileSystemFailure(ex)) { skipped++; }

        try
        {
            foreach (string path in Directory.EnumerateFiles(directory))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(path);
                    FileAttributes attributes = info.Attributes;
                    if ((attributes & FileAttributes.ReparsePoint) != 0 && info.LinkTarget is not null) { skipped++; continue; }

                    long logical = info.Length;
                    bool hasIdentity = NativeFileIdentity.TryGet(path, false, out var identity);
                    long? measured = hasIdentity ? identity.AllocatedBytes : null;
                    measured ??= NativeDiskSize.TryAllocatedBytes(path);
                    bool estimated = measured is null;
                    long allocated = measured ?? logical;
                    uint volumeSerial = 0;
                    ulong fileIndex = 0;
                    if (hasIdentity)
                    {
                        volumeSerial = identity.Id.VolumeSerial;
                        fileIndex = identity.Id.FileIndex;
                    }
                    (string category, FileSafety safety) = AnalyzerForm.ClassifyForScan(path, attributes, pathIsCanonical: true);
                    FileItem item = new(path, allocated, logical, info.LastWriteTime, category, info.CreationTime, estimated, volumeSerial, fileIndex, attributes, safety);
                    if (hasIdentity && !countedFiles.Add(identity.Id)) item = item with { DiskBytes = 0, AllocationEstimated = false };
                    batch.Add(item);
                }
                catch (Exception ex) when (IsExpectedFileSystemFailure(ex)) { skipped++; }
                FlushIfFull(ref batch, skipped, progress);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (IsExpectedFileSystemFailure(ex)) { skipped++; }
    }

    private static bool IsExpectedFileSystemFailure(Exception ex)
        => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException or OverflowException;

    internal static bool ManagedCancellationPropagatesForTest(string directory)
    {
        string fullRoot = Path.GetFullPath(directory);
        uint rootVolume = NativeFileIdentity.TryGet(fullRoot, true, out NativeFileInformation rootIdentity) ? rootIdentity.Id.VolumeSerial : 0;
        var pending = new Stack<PendingDirectory>();
        var traversalBudget = new TraversalBudget();
        var visitedDirectories = new HashSet<NativeFileId>();
        var countedFiles = new HashSet<NativeFileId>();
        var batch = new List<FileItem>();
        int skipped = 0;
        try
        {
            EnumerateManaged(fullRoot, fullRoot, rootVolume, pending, traversalBudget, visitedDirectories, countedFiles, ref batch, ref skipped, new Progress<(List<FileItem>, int)>(), new CancellationToken(canceled: true), false);
            return false;
        }
        catch (OperationCanceledException) { return true; }
    }

    private static bool IsCloudDirectory(string path)
    {
        try
        {
            using SafeFileHandle handle = CreateFileW(NativePath.For(path), 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 9, out FileAttributeTagInformation information, (uint)Marshal.SizeOf<FileAttributeTagInformation>())) return false;
            return IsCloudTag(information.ReparseTag);
        }
        catch { return false; }
    }

    private static bool IsFileLink(string path)
    {
        try { return new FileInfo(path).LinkTarget is not null; }
        catch { return true; }
    }

    private static bool IsCloudTag(uint tag) => (tag & CloudTagMask) == CloudTagBase;

    private static bool IsNameSurrogateTag(uint tag) => (tag & ReparseTagNameSurrogate) != 0;

    private static bool IsWholeVolumeRoot(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(fullPath);
            string full = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return root is not null
                && full.Equals(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static DateTime ToLocalFileTime(long value)
    {
        if (value <= 0) return default;
        try { return DateTime.FromFileTimeUtc(value).ToLocalTime(); }
        catch (ArgumentOutOfRangeException) { return default; }
    }

    private static void FlushIfFull(ref List<FileItem> batch, int skipped, IProgress<(List<FileItem>, int)> progress)
    {
        if (batch.Count < ReportBatchSize) return;
        // Progress<T> may post asynchronously. Transfer ownership of the full
        // list and continue with a fresh one so the scanner never mutates a
        // batch after publishing it and does not clone thousands of records.
        List<FileItem> completed = batch;
        batch = new List<FileItem>(ReportBatchSize);
        progress.Report((completed, skipped));
    }

    private enum NativeEnumerationResult
    {
        Complete,
        UnsupportedBeforeFirstEntry,
        Failed
    }

    private readonly record struct PendingDirectory(string Path, NativeFileId ExpectedId);

    private sealed class TraversalBudget
    {
        private int traversedDirectories;
        private long directoryPathCharacters;

        internal void Queue(Stack<PendingDirectory> pending, PendingDirectory directory)
        {
            long nextPathCharacters;
            try { nextPathCharacters = checked(directoryPathCharacters + directory.Path.Length); }
            catch (OverflowException) { throw new DirectoryTraversalLimitException(); }
            ThrowIfDirectoryBudgetExceeded(traversedDirectories, pending.Count, nextPathCharacters);
            pending.Push(directory);
            traversedDirectories++;
            directoryPathCharacters = nextPathCharacters;
        }
    }

    private sealed class DirectoryTraversalLimitException : IOException
    {
        internal DirectoryTraversalLimitException()
            : base($"The selected location exceeds the safe traversal budget ({MaximumTraversedDirectories:N0} directories, {MaximumQueuedDirectories:N0} queued directories, or {MaximumDirectoryPathCharacters:N0} path characters). The scan stopped to protect application memory.") { }
    }

    private static void ThrowIfDirectoryBudgetExceeded(int traversedDirectories, int queuedDirectories, long directoryPathCharacters)
    {
        if (traversedDirectories < 0 || queuedDirectories < 0 || directoryPathCharacters < 0 || traversedDirectories >= MaximumTraversedDirectories || queuedDirectories >= MaximumQueuedDirectories || directoryPathCharacters > MaximumDirectoryPathCharacters)
            throw new DirectoryTraversalLimitException();
    }

    internal static bool DirectoryBudgetAcceptsForTest(int traversedDirectories, int queuedDirectories, long directoryPathCharacters)
    {
        try { ThrowIfDirectoryBudgetExceeded(traversedDirectories, queuedDirectories, directoryPathCharacters); return true; }
        catch (DirectoryTraversalLimitException) { return false; }
    }

    private readonly record struct NativeDirectoryEntry(
        string Name,
        long EndOfFile,
        long AllocationSize,
        long CreationTime,
        long LastWriteTime,
        FileAttributes Attributes,
        ulong FileIndex,
        uint ReparseTag,
        bool ReparseTagKnown);

    private sealed class NativeDirectoryEnumerator : IDisposable
    {
        // 64 KiB remains the default after a same-tree benchmark showed that
        // 256 KiB reduced throughput on a directory-heavy Windows tree. The
        // bounded environment override keeps larger buffers easy to benchmark
        // on other storage without imposing that regression on every machine.
        private const int DefaultBufferSize = 64 * 1024;
        private const int MinimumBufferSize = 64 * 1024;
        private const int MaximumBufferSize = 1024 * 1024;
        private const int BothHeaderSize = 104;
        private const int ExtendedHeaderSize = 88;
        private const int FileIdBothDirectoryInfo = 10;
        private const int FileIdBothDirectoryRestartInfo = 11;
        private const int FileIdExtdDirectoryInfo = 19;
        private const int FileIdExtdDirectoryRestartInfo = 20;
        private const int ErrorNoMoreFiles = 18;
        private const uint FileListDirectory = 0x00000001;
        private const uint FileFlagBackupSemantics = 0x02000000;

        private static readonly int ConfiguredBufferSize = ResolveBufferSize();
        private readonly int bufferSize = ConfiguredBufferSize;
        private readonly IntPtr buffer = Marshal.AllocHGlobal(ConfiguredBufferSize);
        private bool? extendedDirectoryInfoSupported;

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            internal uint FileAttributes;
            internal uint CreationTimeLow; internal uint CreationTimeHigh;
            internal uint LastAccessTimeLow; internal uint LastAccessTimeHigh;
            internal uint LastWriteTimeLow; internal uint LastWriteTimeHigh;
            internal uint VolumeSerialNumber;
            internal uint FileSizeHigh; internal uint FileSizeLow;
            internal uint NumberOfLinks;
            internal uint FileIndexHigh; internal uint FileIndexLow;
        }

        internal static int BufferSizeForDiagnostics => ConfiguredBufferSize;

        internal NativeEnumerationResult Enumerate(
            string path,
            NativeFileId expectedId,
            string scanRoot,
            uint rootVolume,
            bool wholeVolumeRoot,
            CancellationToken token,
            Action<string, NativeDirectoryEntry> accept,
            out string resolvedPath)
        {
            resolvedPath = string.Empty;
            SafeFileHandle handle;
            try
            {
                handle = CreateFileW(
                    NativePath.For(path),
                    FileListDirectory,
                    FileShare.ReadWrite | FileShare.Delete,
                    IntPtr.Zero,
                    FileMode.Open,
                    FileFlagBackupSemantics,
                    IntPtr.Zero);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                return NativeEnumerationResult.Failed;
            }
            using (handle)
            {
                if (handle.IsInvalid)
                {
                    int openError = Marshal.GetLastPInvokeError();
                    return IsUnsupportedError(openError) ? NativeEnumerationResult.UnsupportedBeforeFirstEntry : NativeEnumerationResult.Failed;
                }

                if (!TryGetDirectoryIdentity(handle, out NativeFileId liveId)
                    || (expectedId.FileIndex != 0 && liveId != expectedId)
                    || (rootVolume != 0 && liveId.VolumeSerial != rootVolume)) return NativeEnumerationResult.Failed;

                // On a whole-volume scan, a nonzero expected ID came from the
                // already-validated parent enumeration. Matching it after open
                // prevents an object swap, and the volume check provides the
                // containment boundary without resolving every directory path.
                // Folder scans still resolve every handle because a directory
                // could be moved outside that narrower root and linked back.
                if (wholeVolumeRoot && expectedId.FileIndex != 0 && NativeResolvedPath.IsUnderOrEqual(path, scanRoot))
                    resolvedPath = path;
                else if (!NativeResolvedPath.TryResolveHandle(handle, out resolvedPath, out _)
                    || !NativeResolvedPath.IsUnderOrEqual(resolvedPath, scanRoot))
                    return NativeEnumerationResult.Failed;

                bool firstCall = true;
                bool returnedAny = false;
                bool useExtended = extendedDirectoryInfoSupported != false;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    int informationClass = useExtended
                        ? (firstCall ? FileIdExtdDirectoryRestartInfo : FileIdExtdDirectoryInfo)
                        : (firstCall ? FileIdBothDirectoryRestartInfo : FileIdBothDirectoryInfo);
                    if (!GetFileInformationByHandleEx(handle, informationClass, buffer, bufferSize))
                    {
                        int error = Marshal.GetLastPInvokeError();
                        if (error == ErrorNoMoreFiles)
                        {
                            if (useExtended) extendedDirectoryInfoSupported ??= true;
                            return NativeEnumerationResult.Complete;
                        }
                        if (useExtended && !returnedAny && IsUnsupportedError(error))
                        {
                            // Restart the still-empty enumeration with the older
                            // 64-bit record class on file systems that do not
                            // implement FILE_ID_EXTD_DIR_INFO.
                            extendedDirectoryInfoSupported = false;
                            useExtended = false;
                            firstCall = true;
                            continue;
                        }
                        return returnedAny || !IsUnsupportedError(error) ? NativeEnumerationResult.Failed : NativeEnumerationResult.UnsupportedBeforeFirstEntry;
                    }

                    if (useExtended) extendedDirectoryInfoSupported ??= true;
                    firstCall = false;
                    int offset = 0;
                    int headerSize = useExtended ? ExtendedHeaderSize : BothHeaderSize;
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        if (offset < 0 || offset > bufferSize - headerSize) return NativeEnumerationResult.Failed;

                        uint nextOffset = unchecked((uint)Marshal.ReadInt32(buffer, offset));
                        uint nameBytes = unchecked((uint)Marshal.ReadInt32(buffer, offset + 60));
                        int availableNameBytes = bufferSize - offset - headerSize;
                        if ((nameBytes & 1) != 0 || nameBytes > (uint)availableNameBytes) return NativeEnumerationResult.Failed;

                        string? name = Marshal.PtrToStringUni(IntPtr.Add(buffer, offset + headerSize), checked((int)nameBytes / sizeof(char)));
                        if (!string.IsNullOrEmpty(name) && name is not "." and not "..")
                        {
                            long endOfFile = Marshal.ReadInt64(buffer, offset + 40);
                            long allocationSize = Marshal.ReadInt64(buffer, offset + 48);
                            if (endOfFile < 0 || allocationSize < 0) return NativeEnumerationResult.Failed;

                            ulong fileIndex;
                            uint reparseTag;
                            bool reparseTagKnown;
                            if (useExtended)
                            {
                                ulong low = unchecked((ulong)Marshal.ReadInt64(buffer, offset + 72));
                                ulong high = unchecked((ulong)Marshal.ReadInt64(buffer, offset + 80));
                                // SpaceLens currently stores a 64-bit file ID.
                                // Do not truncate a genuine 128-bit ReFS ID and
                                // accidentally merge unrelated entries.
                                fileIndex = high == 0 ? low : 0;
                                reparseTag = unchecked((uint)Marshal.ReadInt32(buffer, offset + 68));
                                reparseTagKnown = true;
                            }
                            else
                            {
                                fileIndex = unchecked((ulong)Marshal.ReadInt64(buffer, offset + 96));
                                reparseTag = 0;
                                reparseTagKnown = false;
                            }

                            accept(resolvedPath, new(
                                name,
                                endOfFile,
                                allocationSize,
                                Marshal.ReadInt64(buffer, offset + 8),
                                Marshal.ReadInt64(buffer, offset + 24),
                                unchecked((FileAttributes)(uint)Marshal.ReadInt32(buffer, offset + 56)),
                                fileIndex,
                                reparseTag,
                                reparseTagKnown));
                            returnedAny = true;
                        }

                        if (nextOffset == 0) break;
                        int minimumEntrySize = checked((headerSize + (int)nameBytes + 7) & ~7);
                        if ((nextOffset & 7) != 0 || nextOffset < minimumEntrySize || nextOffset > bufferSize - offset) return NativeEnumerationResult.Failed;
                        offset = checked(offset + (int)nextOffset);
                    }
                }
            }
        }

        private static bool TryGetDirectoryIdentity(SafeFileHandle handle, out NativeFileId id)
        {
            id = default;
            if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information)
                || (information.FileAttributes & (uint)FileAttributes.Directory) == 0) return false;
            ulong index = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
            if (index == 0) return false;
            id = new(information.VolumeSerialNumber, index);
            return true;
        }

        private static int ResolveBufferSize()
        {
            string? configured = Environment.GetEnvironmentVariable("SPACELENS_SCAN_BUFFER_KB");
            if (int.TryParse(configured, out int kilobytes))
            {
                int bytes;
                try { bytes = checked(kilobytes * 1024); }
                catch (OverflowException) { return DefaultBufferSize; }
                if (bytes >= MinimumBufferSize && bytes <= MaximumBufferSize && (bytes & (bytes - 1)) == 0)
                    return bytes;
            }
            return DefaultBufferSize;
        }

        private static bool IsUnsupportedError(int error) => error is 1 or 50 or 87;

        public void Dispose() => Marshal.FreeHGlobal(buffer);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            FileShare shareMode,
            IntPtr securityAttributes,
            FileMode creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(
            SafeFileHandle file,
            int informationClass,
            IntPtr fileInformation,
            int bufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);
    }
}
