using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DesktopOrganizer;

/// <summary>
/// Enumerates a directory a buffer at a time. On Windows, FILE_ID_BOTH_DIR_INFO
/// supplies the logical size, allocated size, timestamps, attributes, and file
/// ID without opening every file. The managed path is retained for file systems
/// that do not implement that information class.
/// </summary>
internal static class FastFileScanner
{
    private const int ReportBatchSize = 8_000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint CloudTagMask = 0xFFFF0FFF;
    private const uint CloudTagBase = 0x9000001A;

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
            return native.Enumerate(resolved, identity.Id, resolved, identity.Id.VolumeSerial, CancellationToken.None, (_, _) => { }, out _) == NativeEnumerationResult.Complete;
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
                && (information.ReparseTag & CloudTagMask) == CloudTagBase;
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
        var visitedDirectories = new HashSet<NativeFileId>();
        var countedFiles = new HashSet<NativeFileId>();
        var batch = new List<FileItem>(ReportBatchSize);
        uint rootVolume = 0;
        int skipped = 0;

        if (NativeFileIdentity.TryGet(fullRoot, true, out var rootIdentity))
        {
            rootVolume = rootIdentity.Id.VolumeSerial;
            if (rootIdentity.Id.FileIndex != 0) visitedDirectories.Add(rootIdentity.Id);
            pending.Push(new(fullRoot, rootIdentity.Id));
        }
        else pending.Push(new(fullRoot, default));

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
                    token,
                    (directory, entry) => ProcessNativeEntry(directory, entry, fullRoot, rootVolume, pending, visitedDirectories, countedFiles, batch, ref skipped, progress, strictReparseDirectories),
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
                        else EnumerateManaged(fallbackPath, fullRoot, rootVolume, pending, visitedDirectories, countedFiles, batch, ref skipped, progress, token, false);
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
        catch (OperationCanceledException)
        {
            if (batch.Count > 0) progress.Report((new List<FileItem>(batch), skipped));
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
        HashSet<NativeFileId> visitedDirectories,
        HashSet<NativeFileId> countedFiles,
        List<FileItem> batch,
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
                if (!IsCloudDirectory(path)
                    || !NativeResolvedPath.TryResolveDirectory(path, out string resolvedDirectory, out _)
                    || !NativeResolvedPath.IsStrictlyUnder(resolvedDirectory, scanRoot)
                    || !NativeFileIdentity.TryGet(resolvedDirectory, true, out NativeFileInformation resolvedIdentity)
                    || (rootVolume != 0 && resolvedIdentity.Id.VolumeSerial != rootVolume)) { skipped++; return; }
                path = resolvedDirectory;
                id = resolvedIdentity.Id;
            }
            if (id.FileIndex != 0 && !visitedDirectories.Add(id)) { skipped++; return; }
            pending.Push(new(path, id));
            return;
        }

        // Cloud placeholders are also reparse points, but are not links. Keep
        // them so their local allocation (often zero) remains visible.
        if (isReparsePoint && IsFileLink(path)) { skipped++; return; }

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
            FlushIfFull(batch, skipped, progress);
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
        HashSet<NativeFileId> visitedDirectories,
        HashSet<NativeFileId> countedFiles,
        List<FileItem> batch,
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
                    pending.Push(new(candidate, childId));
                }
                catch { skipped++; }
            }
        }
        catch { skipped++; }

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
                catch { skipped++; }
                FlushIfFull(batch, skipped, progress);
            }
        }
        catch { skipped++; }
    }

    private static bool IsDirectoryLink(string path)
    {
        try { return new DirectoryInfo(path).LinkTarget is not null; }
        catch { return true; }
    }

    private static bool IsCloudDirectory(string path)
    {
        try
        {
            using SafeFileHandle handle = CreateFileW(NativePath.For(path), 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 9, out FileAttributeTagInformation information, (uint)Marshal.SizeOf<FileAttributeTagInformation>())) return false;
            return (information.ReparseTag & CloudTagMask) == CloudTagBase;
        }
        catch { return false; }
    }

    private static bool IsFileLink(string path)
    {
        try { return new FileInfo(path).LinkTarget is not null; }
        catch { return true; }
    }

    private static DateTime ToLocalFileTime(long value)
    {
        if (value <= 0) return default;
        try { return DateTime.FromFileTimeUtc(value).ToLocalTime(); }
        catch (ArgumentOutOfRangeException) { return default; }
    }

    private static void FlushIfFull(List<FileItem> batch, int skipped, IProgress<(List<FileItem>, int)> progress)
    {
        if (batch.Count < ReportBatchSize) return;
        // Progress<T> posts to the UI thread asynchronously. Never publish the
        // mutable working list itself and then clear it, or the UI can receive
        // an empty batch after a fast scan has already reused that list.
        progress.Report((new List<FileItem>(batch), skipped));
        batch.Clear();
    }

    private enum NativeEnumerationResult
    {
        Complete,
        UnsupportedBeforeFirstEntry,
        Failed
    }

    private readonly record struct PendingDirectory(string Path, NativeFileId ExpectedId);

    private readonly record struct NativeDirectoryEntry(
        string Name,
        long EndOfFile,
        long AllocationSize,
        long CreationTime,
        long LastWriteTime,
        FileAttributes Attributes,
        ulong FileIndex);

    private sealed class NativeDirectoryEnumerator : IDisposable
    {
        private const int BufferSize = 64 * 1024;
        private const int HeaderSize = 104;
        private const int FileIdBothDirectoryInfo = 10;
        private const int FileIdBothDirectoryRestartInfo = 11;
        private const int ErrorNoMoreFiles = 18;
        private const uint FileListDirectory = 0x00000001;
        private const uint FileFlagBackupSemantics = 0x02000000;

        private readonly IntPtr buffer = Marshal.AllocHGlobal(BufferSize);

        internal NativeEnumerationResult Enumerate(
            string path,
            NativeFileId expectedId,
            string scanRoot,
            uint rootVolume,
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

                if (!NativeResolvedPath.TryResolveHandle(handle, out resolvedPath, out _)
                    || !NativeResolvedPath.IsUnderOrEqual(resolvedPath, scanRoot)
                    || !NativeFileIdentity.TryGet(handle, out NativeFileInformation liveIdentity)
                    || (expectedId.FileIndex != 0 && liveIdentity.Id != expectedId)
                    || (rootVolume != 0 && liveIdentity.Id.VolumeSerial != rootVolume)) return NativeEnumerationResult.Failed;

                bool firstCall = true;
                bool returnedAny = false;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    int informationClass = firstCall ? FileIdBothDirectoryRestartInfo : FileIdBothDirectoryInfo;
                    if (!GetFileInformationByHandleEx(handle, informationClass, buffer, BufferSize))
                    {
                        int error = Marshal.GetLastPInvokeError();
                        if (error == ErrorNoMoreFiles) return NativeEnumerationResult.Complete;
                        return returnedAny || !IsUnsupportedError(error) ? NativeEnumerationResult.Failed : NativeEnumerationResult.UnsupportedBeforeFirstEntry;
                    }

                    firstCall = false;
                    int offset = 0;
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        if (offset < 0 || offset > BufferSize - HeaderSize) return NativeEnumerationResult.Failed;

                        uint nextOffset = unchecked((uint)Marshal.ReadInt32(buffer, offset));
                        uint nameBytes = unchecked((uint)Marshal.ReadInt32(buffer, offset + 60));
                        if ((nameBytes & 1) != 0 || nameBytes > BufferSize - HeaderSize || offset + HeaderSize + nameBytes > BufferSize) return NativeEnumerationResult.Failed;

                        string? name = Marshal.PtrToStringUni(IntPtr.Add(buffer, offset + HeaderSize), checked((int)nameBytes / sizeof(char)));
                        if (!string.IsNullOrEmpty(name) && name is not "." and not "..")
                        {
                            long endOfFile = Marshal.ReadInt64(buffer, offset + 40);
                            long allocationSize = Marshal.ReadInt64(buffer, offset + 48);
                            if (endOfFile < 0 || allocationSize < 0) return NativeEnumerationResult.Failed;

                            accept(resolvedPath, new(
                                name,
                                endOfFile,
                                allocationSize,
                                Marshal.ReadInt64(buffer, offset + 8),
                                Marshal.ReadInt64(buffer, offset + 24),
                                unchecked((FileAttributes)(uint)Marshal.ReadInt32(buffer, offset + 56)),
                                unchecked((ulong)Marshal.ReadInt64(buffer, offset + 96))));
                            returnedAny = true;
                        }

                        if (nextOffset == 0) break;
                        int minimumEntrySize = checked((HeaderSize + (int)nameBytes + 7) & ~7);
                        if ((nextOffset & 7) != 0 || nextOffset < minimumEntrySize || nextOffset > BufferSize - offset) return NativeEnumerationResult.Failed;
                        offset = checked(offset + (int)nextOffset);
                    }
                }
            }
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
    }
}
