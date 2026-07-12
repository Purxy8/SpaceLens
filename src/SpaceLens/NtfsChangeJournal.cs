using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DesktopOrganizer;

internal readonly record struct NtfsJournalCheckpoint(uint VolumeSerial, ulong JournalId, long NextUsn)
{
    internal bool IsValid => VolumeSerial != 0 && JournalId != 0 && NextUsn >= 0;
}

internal sealed record NtfsJournalDelta(
    NtfsJournalCheckpoint Checkpoint,
    List<FileItem> Upserts,
    List<NativeFileId> Removed,
    int JournalRecords,
    bool RequiresFullScan,
    string Message)
{
    internal static NtfsJournalDelta Fallback(string message)
        => new(default, [], [], 0, true, message);
}

/// <summary>
/// Reads the documented NTFS change journal for a whole local fixed volume.
/// The journal is only a change detector: every live file is reopened by its
/// stable file ID and all metadata used by SpaceLens is read from that handle.
/// Ambiguous directory, hard-link, stream, journal, or identity changes cause
/// a full-scan fallback rather than a potentially incomplete incremental view.
/// </summary>
internal static class NtfsChangeJournal
{
    private const uint GenericRead = 0x80000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileNameSurrogateReparseTag = 0x20000000;
    private const uint FsctlReadUsnJournal = 0x000900BB;
    private const uint FsctlQueryUsnJournal = 0x000900F4;
    private const int ErrorHandleEof = 38;
    private const int JournalBufferSize = 1024 * 1024;
    private const int MaximumJournalRecords = 500_000;
    private const int MinimumUsnRecordV2Length = 60;
    private const uint UsnReasonNamedDataOverwrite = 0x00000010;
    private const uint UsnReasonNamedDataExtend = 0x00000020;
    private const uint UsnReasonNamedDataTruncation = 0x00000040;
    private const uint UsnReasonFileCreate = 0x00000100;
    private const uint UsnReasonFileDelete = 0x00000200;
    private const uint UsnReasonRenameOldName = 0x00001000;
    private const uint UsnReasonRenameNewName = 0x00002000;
    private const uint UsnReasonHardLinkChange = 0x00010000;
    private const uint UsnReasonReparsePointChange = 0x00100000;
    private const uint UsnReasonStreamChange = 0x00200000;
    private const uint UsnReasonClose = 0x80000000;
    private const uint DirectoryStructuralReasons = UsnReasonFileCreate | UsnReasonFileDelete | UsnReasonRenameOldName | UsnReasonRenameNewName | UsnReasonReparsePointChange;
    private const uint UnsupportedFileReasons = UsnReasonNamedDataOverwrite | UsnReasonNamedDataExtend | UsnReasonNamedDataTruncation | UsnReasonHardLinkChange | UsnReasonStreamChange;

    [StructLayout(LayoutKind.Sequential)]
    private struct ReadUsnJournalDataV1
    {
        internal long StartUsn;
        internal uint ReasonMask;
        internal uint ReturnOnlyOnClose;
        internal ulong Timeout;
        internal ulong BytesToWaitFor;
        internal ulong UsnJournalId;
        internal ushort MinMajorVersion;
        internal ushort MaxMajorVersion;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct FileIdDescriptor
    {
        [FieldOffset(0)] internal uint Size;
        [FieldOffset(4)] internal int Type;
        [FieldOffset(8)] internal long FileId;
        [FieldOffset(8)] private Guid ObjectId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal uint CreationTimeLow;
        internal uint CreationTimeHigh;
        internal uint LastAccessTimeLow;
        internal uint LastAccessTimeHigh;
        internal uint LastWriteTimeLow;
        internal uint LastWriteTimeHigh;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation
    {
        internal uint FileAttributes;
        internal uint ReparseTag;
    }

    private readonly record struct JournalData(ulong JournalId, long FirstUsn, long NextUsn, long LowestValidUsn);
    private readonly record struct ChangedRecord(
        ulong FileId,
        uint AllReasons,
        uint LatestCycleReasons,
        FileAttributes Attributes);
    private readonly record struct ParsedUsnRecord(ulong FileId, long Usn, uint Reasons, FileAttributes Attributes);

    internal static bool SupportsRoot(string root, out string reason)
    {
        try
        {
            _ = GetVolumeContext(root, out _, out _, out _);
            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            reason = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Captures the journal cursor before a full scan starts. Storing the
    /// pre-scan cursor ensures that changes racing with the full scan are read
    /// by the next quick refresh rather than skipped permanently.
    /// </summary>
    internal static bool TryCapture(string root, out NtfsJournalCheckpoint checkpoint, out string reason)
    {
        checkpoint = default;
        reason = string.Empty;
        try
        {
            using SafeFileHandle volume = OpenVolume(root, out uint volumeSerial, out _, out _);
            JournalData data = QueryJournal(volume);
            checkpoint = new(volumeSerial, data.JournalId, data.NextUsn);
            if (!checkpoint.IsValid) throw new InvalidDataException("Windows returned an invalid NTFS journal checkpoint.");
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidDataException)
        {
            reason = SafeReason(ex);
            return false;
        }
    }

    internal static NtfsJournalDelta ReadDelta(string root, NtfsJournalCheckpoint checkpoint, CancellationToken token)
    {
        if (!checkpoint.IsValid) return NtfsJournalDelta.Fallback("The saved scan has no valid NTFS journal checkpoint.");
        try
        {
            using SafeFileHandle volume = OpenVolume(root, out uint volumeSerial, out string fullRoot, out _);
            if (volumeSerial != checkpoint.VolumeSerial)
                return NtfsJournalDelta.Fallback("The volume identity changed since the saved scan.");

            JournalData current = QueryJournal(volume);
            if (current.JournalId != checkpoint.JournalId)
                return NtfsJournalDelta.Fallback("The NTFS change journal was recreated since the saved scan.");
            if (checkpoint.NextUsn < current.LowestValidUsn || checkpoint.NextUsn < current.FirstUsn)
                return NtfsJournalDelta.Fallback("Required NTFS journal records have expired.");
            if (checkpoint.NextUsn > current.NextUsn)
                return NtfsJournalDelta.Fallback("The saved NTFS journal cursor is newer than the volume.");

            NtfsJournalCheckpoint nextCheckpoint = new(volumeSerial, current.JournalId, current.NextUsn);
            if (checkpoint.NextUsn == current.NextUsn)
                return new(nextCheckpoint, [], [], 0, false, "No files changed since the saved scan.");

            Dictionary<ulong, ChangedRecord> changed = ReadChangedRecords(volume, checkpoint.NextUsn, current.NextUsn, current.JournalId, token, out int recordCount, out string? fallbackReason);
            if (fallbackReason is not null) return NtfsJournalDelta.Fallback(fallbackReason);

            var upserts = new List<FileItem>(changed.Count);
            var removed = new List<NativeFileId>();
            foreach (ChangedRecord change in changed.Values)
            {
                token.ThrowIfCancellationRequested();
                if ((change.Attributes & FileAttributes.Directory) != 0)
                {
                    if ((change.AllReasons & DirectoryStructuralReasons) != 0)
                        return NtfsJournalDelta.Fallback("A directory was created, deleted, moved, renamed, or changed its reparse target.");
                    continue;
                }
                if ((change.LatestCycleReasons & UsnReasonClose) == 0)
                    return NtfsJournalDelta.Fallback("A changed file is still open, so its journal reasons may be incomplete.");
                if ((change.AllReasons & UnsupportedFileReasons) != 0)
                    return NtfsJournalDelta.Fallback("A hard link or alternate data stream changed and requires a full allocation scan.");

                NativeFileId id = new(volumeSerial, change.FileId);
                FileResolution resolution = ResolveChangedFile(volume, fullRoot, id);
                if (resolution.RequiresFullScan) return NtfsJournalDelta.Fallback(resolution.Message);
                if (resolution.Item is null) removed.Add(id);
                else upserts.Add(resolution.Item);
            }
            return new(nextCheckpoint, upserts, removed, recordCount, false,
                $"Applied {recordCount:N0} NTFS journal records.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidDataException or OverflowException)
        {
            return NtfsJournalDelta.Fallback(SafeReason(ex));
        }
    }

    private static Dictionary<ulong, ChangedRecord> ReadChangedRecords(
        SafeFileHandle volume,
        long startUsn,
        long targetNextUsn,
        ulong journalId,
        CancellationToken token,
        out int recordCount,
        out string? fallbackReason)
    {
        var changed = new Dictionary<ulong, ChangedRecord>();
        byte[] buffer = new byte[JournalBufferSize];
        long cursor = startUsn;
        recordCount = 0;
        fallbackReason = null;
        while (cursor < targetNextUsn)
        {
            token.ThrowIfCancellationRequested();
            var input = new ReadUsnJournalDataV1
            {
                StartUsn = cursor,
                ReasonMask = uint.MaxValue,
                ReturnOnlyOnClose = 0,
                Timeout = 0,
                BytesToWaitFor = 0,
                UsnJournalId = journalId,
                MinMajorVersion = 2,
                MaxMajorVersion = 2
            };
            if (!DeviceIoControl(volume, FsctlReadUsnJournal, ref input, Marshal.SizeOf<ReadUsnJournalDataV1>(), buffer, buffer.Length, out int returned, IntPtr.Zero))
            {
                int error = Marshal.GetLastPInvokeError();
                if (error == ErrorHandleEof)
                {
                    fallbackReason = "The NTFS journal ended before the requested checkpoint range was covered.";
                    return changed;
                }
                throw new Win32Exception(error, "Windows could not read the NTFS change journal.");
            }
            if (returned < sizeof(long)) throw new InvalidDataException("The NTFS journal returned a truncated buffer.");
            long nextCursor = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(0, sizeof(long)));
            if (nextCursor <= cursor && returned == sizeof(long))
            {
                fallbackReason = "The NTFS journal made no progress before the requested range was covered.";
                return changed;
            }
            if (nextCursor < cursor) throw new InvalidDataException("The NTFS journal cursor moved backwards.");

            int offset = sizeof(long);
            while (offset < returned)
            {
                if (returned - offset < MinimumUsnRecordV2Length) throw new InvalidDataException("The NTFS journal returned a truncated record.");
                ReadOnlySpan<byte> record = buffer.AsSpan(offset, returned - offset);
                int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record));
                if (length < MinimumUsnRecordV2Length || length > returned - offset || (length & 7) != 0)
                    throw new InvalidDataException("The NTFS journal returned an invalid record length.");

                ReadOnlySpan<byte> exact = buffer.AsSpan(offset, length);
                ParsedUsnRecord parsed;
                try { parsed = ParseUsnRecord(exact, cursor, nextCursor); }
                catch (NotSupportedException ex) { fallbackReason = ex.Message; return changed; }
                if (parsed.Usn < targetNextUsn)
                {
                    recordCount = checked(recordCount + 1);
                    if (recordCount > MaximumJournalRecords)
                    {
                        fallbackReason = $"More than {MaximumJournalRecords:N0} journal records changed; a full scan is safer.";
                        return changed;
                    }
                    if (changed.TryGetValue(parsed.FileId, out ChangedRecord previous))
                        changed[parsed.FileId] = AccumulateChangedRecord(previous, parsed);
                    else
                        changed.Add(parsed.FileId, new(parsed.FileId, parsed.Reasons, parsed.Reasons, parsed.Attributes));
                }
                offset += length;
            }
            if (offset != returned) throw new InvalidDataException("The NTFS journal buffer ended between records.");
            if (nextCursor <= cursor) throw new InvalidDataException("The NTFS journal made no forward progress.");
            cursor = Math.Min(nextCursor, targetNextUsn);
        }
        if (cursor != targetNextUsn)
        {
            fallbackReason = "The NTFS journal did not cover the complete requested range.";
            return changed;
        }
        return changed;
    }

    private static ChangedRecord AccumulateChangedRecord(ChangedRecord previous, ParsedUsnRecord parsed)
    {
        if (previous.FileId != parsed.FileId) throw new InvalidDataException("NTFS journal aggregation mixed unrelated file identities.");
        uint latestCycleReasons = (previous.LatestCycleReasons & UsnReasonClose) != 0
            ? parsed.Reasons
            : previous.LatestCycleReasons | parsed.Reasons;
        return new(
            parsed.FileId,
            previous.AllReasons | parsed.Reasons,
            latestCycleReasons,
            parsed.Attributes);
    }

    private static ParsedUsnRecord ParseUsnRecord(ReadOnlySpan<byte> record, long cursor, long nextCursor)
    {
        if (record.Length < MinimumUsnRecordV2Length) throw new InvalidDataException("The NTFS journal returned a truncated record.");
        int declaredLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record));
        if (declaredLength != record.Length || (declaredLength & 7) != 0) throw new InvalidDataException("The NTFS journal returned an invalid record length.");
        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        if (major != 2) throw new NotSupportedException($"NTFS returned unsupported USN record version {major}.");
        ulong fileId = BinaryPrimitives.ReadUInt64LittleEndian(record[8..]);
        long usn = BinaryPrimitives.ReadInt64LittleEndian(record[24..]);
        uint reasons = BinaryPrimitives.ReadUInt32LittleEndian(record[40..]);
        FileAttributes attributes = (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(record[52..]);
        ushort nameBytes = BinaryPrimitives.ReadUInt16LittleEndian(record[56..]);
        ushort nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[58..]);
        if (fileId == 0 || nameOffset < MinimumUsnRecordV2Length || (nameOffset & 1) != 0 || (nameBytes & 1) != 0 || nameOffset > declaredLength || nameBytes > declaredLength - nameOffset)
            throw new InvalidDataException("The NTFS journal returned invalid record fields.");
        if (usn < cursor || usn >= nextCursor) throw new InvalidDataException("The NTFS journal returned a record outside its reported cursor range.");
        return new(fileId, usn, reasons, attributes);
    }

    private readonly record struct FileResolution(FileItem? Item, bool RequiresFullScan, string Message);

    private static FileResolution ResolveChangedFile(SafeFileHandle volume, string root, NativeFileId expectedId)
    {
        var descriptor = new FileIdDescriptor { Size = (uint)Marshal.SizeOf<FileIdDescriptor>(), Type = 0, FileId = unchecked((long)expectedId.FileIndex) };
        using SafeFileHandle file = OpenFileById(volume, ref descriptor, FileReadAttributes, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileFlagBackupSemantics | FileFlagOpenReparsePoint);
        if (file.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            if (NativeFileState.IsMissingError(error))
                return new(null, false, string.Empty);
            return new(null, true, "A changed file could not be reopened safely: " + new Win32Exception(error).Message);
        }
        if (!NativeFileIdentity.TryGet(file, out NativeFileInformation identity) || identity.Id != expectedId)
            return new(null, true, "A changed file no longer has the expected filesystem identity.");
        if (identity.NumberOfLinks > 1)
            return new(null, true, "A changed file has multiple hard-link paths.");
        if ((identity.Attributes & FileAttributes.Directory) != 0)
            return new(null, true, "A journal file record now resolves to a directory.");

        if ((identity.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            if (!GetFileInformationByHandleEx(file, 9, out FileAttributeTagInformation tag, (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
                return new(null, true, "Windows did not expose the reparse tag for a changed file.");
            if ((tag.ReparseTag & FileNameSurrogateReparseTag) != 0)
                return new(null, false, string.Empty);
        }
        if (!NativeResolvedPath.TryResolveHandle(file, out string path, out string pathError) || !IsStrictlyUnderCanonicalRoot(path, root))
            return new(null, true, "A changed file path could not be contained under the scanned volume: " + pathError);
        if (!GetFileInformationByHandle(file, out ByHandleFileInformation info))
            return new(null, true, "Windows did not expose metadata for a changed file.");
        ulong liveIndex = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        if (info.VolumeSerialNumber != expectedId.VolumeSerial || liveIndex != expectedId.FileIndex)
            return new(null, true, "A changed file identity changed while it was being refreshed.");

        try
        {
            long logical = checked(((long)info.FileSizeHigh << 32) | info.FileSizeLow);
            long modifiedFileTime = checked(((long)info.LastWriteTimeHigh << 32) | info.LastWriteTimeLow);
            long createdFileTime = checked(((long)info.CreationTimeHigh << 32) | info.CreationTimeLow);
            DateTime modified = DateTime.FromFileTimeUtc(modifiedFileTime).ToLocalTime();
            DateTime created = DateTime.FromFileTimeUtc(createdFileTime).ToLocalTime();
            bool estimated = identity.AllocatedBytes is null;
            long allocated = identity.AllocatedBytes ?? logical;
            (string category, FileSafety safety) = AnalyzerForm.ClassifyForScan(path, identity.Attributes, pathIsCanonical: true);
            return new(new(path, allocated, logical, modified, category, created, estimated, expectedId.VolumeSerial, expectedId.FileIndex, identity.Attributes, safety), false, string.Empty);
        }
        catch (ArgumentOutOfRangeException ex) { return new(null, true, "A changed file has invalid timestamps: " + ex.Message); }
        catch (OverflowException ex) { return new(null, true, "A changed file has invalid size metadata: " + ex.Message); }
    }

    private static SafeFileHandle OpenVolume(string root, out uint volumeSerial, out string fullRoot, out string volumePath)
    {
        _ = GetVolumeContext(root, out fullRoot, out volumePath, out volumeSerial);
        SafeFileHandle handle = CreateFileW(volumePath, GenericRead, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error, "Windows could not open the NTFS volume for journal access.");
        }
        return handle;
    }

    internal static long TryGetWholeDriveUsed(string root)
    {
        try
        {
            _ = GetVolumeContext(root, out _, out _, out _);
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);
            return checked(drive.TotalSize - drive.TotalFreeSpace);
        }
        catch { return 0; }
    }

    private static bool GetVolumeContext(string root, out string fullRoot, out string volumePath, out uint volumeSerial)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("NTFS quick refresh is available only on Windows.");
        fullRoot = Path.GetFullPath(root);
        string? driveRoot = Path.GetPathRoot(fullRoot);
        if (string.IsNullOrEmpty(driveRoot) || driveRoot.Length < 2 || driveRoot[1] != ':')
            throw new NotSupportedException("NTFS quick refresh requires a drive-letter volume.");
        string normalizedRoot = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedDrive = Path.GetFullPath(driveRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!normalizedRoot.Equals(normalizedDrive, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("NTFS quick refresh is available only for a whole drive scan.");
        var drive = new DriveInfo(driveRoot);
        if (!drive.IsReady || drive.DriveType != DriveType.Fixed || !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("NTFS quick refresh requires a ready local fixed NTFS drive.");
        fullRoot = drive.RootDirectory.FullName;
        if (!NativeFileIdentity.TryGet(fullRoot, true, out NativeFileInformation rootIdentity) || rootIdentity.Id.VolumeSerial == 0)
            throw new IOException("Windows did not expose a stable volume identity.");
        volumeSerial = rootIdentity.Id.VolumeSerial;
        volumePath = @"\\.\" + char.ToUpperInvariant(driveRoot[0]) + ":";
        return true;
    }

    private static JournalData QueryJournal(SafeFileHandle volume)
    {
        byte[] output = new byte[128];
        if (!DeviceIoControl(volume, FsctlQueryUsnJournal, IntPtr.Zero, 0, output, output.Length, out int returned, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Windows could not query the NTFS change journal.");
        if (returned < 56) throw new InvalidDataException("Windows returned truncated NTFS journal information.");
        return ParseJournalData(output.AsSpan(0, returned));
    }

    private static JournalData ParseJournalData(ReadOnlySpan<byte> output)
    {
        if (output.Length < 56) throw new InvalidDataException("Windows returned truncated NTFS journal information.");
        var data = new JournalData(
            BinaryPrimitives.ReadUInt64LittleEndian(output),
            BinaryPrimitives.ReadInt64LittleEndian(output[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(output[16..]),
            BinaryPrimitives.ReadInt64LittleEndian(output[24..]));
        if (data.JournalId == 0 || data.FirstUsn < 0 || data.NextUsn < 0 || data.LowestValidUsn < 0 || data.FirstUsn > data.NextUsn || data.LowestValidUsn > data.NextUsn)
            throw new InvalidDataException("Windows returned inconsistent NTFS journal information.");
        return data;
    }

    private static bool IsStrictlyUnderCanonicalRoot(string path, string root)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return fullPath.Length > fullRoot.Length
                && fullPath[fullRoot.Length] == Path.DirectorySeparatorChar
                && fullPath.StartsWith(fullRoot, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static string SafeReason(Exception ex)
    {
        string text = ex.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 512 ? text : text[..512];
    }

    internal static bool RunParserSelfTest(out string error)
    {
        error = string.Empty;
        try
        {
            byte[] recordBuffer = new byte[64];
            Span<byte> record = recordBuffer;
            BinaryPrimitives.WriteUInt32LittleEndian(record, 64);
            BinaryPrimitives.WriteUInt16LittleEndian(record[4..], 2);
            BinaryPrimitives.WriteUInt64LittleEndian(record[8..], 42);
            BinaryPrimitives.WriteUInt64LittleEndian(record[16..], 5);
            BinaryPrimitives.WriteInt64LittleEndian(record[24..], 100);
            BinaryPrimitives.WriteUInt32LittleEndian(record[40..], UsnReasonFileCreate);
            BinaryPrimitives.WriteUInt32LittleEndian(record[52..], (uint)FileAttributes.Archive);
            BinaryPrimitives.WriteUInt16LittleEndian(record[56..], 2);
            BinaryPrimitives.WriteUInt16LittleEndian(record[58..], 60);
            record[60] = (byte)'x';
            ParsedUsnRecord parsed = ParseUsnRecord(record, 99, 101);
            if (parsed.FileId != 42 || parsed.Usn != 100 || parsed.Reasons != UsnReasonFileCreate || parsed.Attributes != FileAttributes.Archive)
                throw new InvalidDataException("Synthetic USN record parsing failed.");

            byte[] invalidVersion = (byte[])recordBuffer.Clone(); BinaryPrimitives.WriteUInt16LittleEndian(invalidVersion.AsSpan(4), 3);
            try { _ = ParseUsnRecord(invalidVersion, 99, 101); throw new InvalidDataException("Unsupported USN record version was accepted."); } catch (NotSupportedException) { }
            byte[] invalidName = (byte[])recordBuffer.Clone(); BinaryPrimitives.WriteUInt16LittleEndian(invalidName.AsSpan(58), 61);
            try { _ = ParseUsnRecord(invalidName, 99, 101); throw new InvalidDataException("Invalid USN name range was accepted."); } catch (InvalidDataException ex) when (ex.Message.Contains("fields", StringComparison.Ordinal)) { }
            byte[] invalidRange = (byte[])recordBuffer.Clone(); BinaryPrimitives.WriteInt64LittleEndian(invalidRange.AsSpan(24), 101);
            try { _ = ParseUsnRecord(invalidRange, 99, 101); throw new InvalidDataException("Out-of-range USN was accepted."); } catch (InvalidDataException ex) when (ex.Message.Contains("cursor", StringComparison.Ordinal)) { }
            byte[] invalidLength = (byte[])recordBuffer.Clone(); BinaryPrimitives.WriteUInt32LittleEndian(invalidLength, 62);
            try { _ = ParseUsnRecord(invalidLength, 99, 101); throw new InvalidDataException("Unaligned USN record length was accepted."); } catch (InvalidDataException ex) when (ex.Message.Contains("length", StringComparison.Ordinal)) { }

            byte[] journal = new byte[56]; BinaryPrimitives.WriteUInt64LittleEndian(journal, 7); BinaryPrimitives.WriteInt64LittleEndian(journal.AsSpan(8), 10); BinaryPrimitives.WriteInt64LittleEndian(journal.AsSpan(16), 20); BinaryPrimitives.WriteInt64LittleEndian(journal.AsSpan(24), 5);
            JournalData journalData = ParseJournalData(journal); if (journalData.JournalId != 7 || journalData.FirstUsn != 10 || journalData.NextUsn != 20 || journalData.LowestValidUsn != 5) throw new InvalidDataException("Synthetic journal metadata parsing failed.");
            byte[] invalidJournal = (byte[])journal.Clone(); BinaryPrimitives.WriteInt64LittleEndian(invalidJournal.AsSpan(8), 21);
            try { _ = ParseJournalData(invalidJournal); throw new InvalidDataException("Invalid journal ordering was accepted."); } catch (InvalidDataException ex) when (ex.Message.Contains("inconsistent", StringComparison.Ordinal)) { }

            const uint basicInfoChange = 0x00008000;
            uint renameReasons = UsnReasonRenameOldName | UsnReasonRenameNewName;
            ChangedRecord directoryCycles = new(77, renameReasons, renameReasons, FileAttributes.Directory);
            directoryCycles = AccumulateChangedRecord(directoryCycles, new(77, 102, renameReasons | UsnReasonClose, FileAttributes.Directory));
            directoryCycles = AccumulateChangedRecord(directoryCycles, new(77, 103, basicInfoChange, FileAttributes.Directory));
            directoryCycles = AccumulateChangedRecord(directoryCycles, new(77, 104, basicInfoChange | UsnReasonClose, FileAttributes.Directory));
            if ((directoryCycles.AllReasons & renameReasons) != renameReasons
                || (directoryCycles.LatestCycleReasons & renameReasons) != 0
                || (directoryCycles.LatestCycleReasons & (basicInfoChange | UsnReasonClose)) != (basicInfoChange | UsnReasonClose))
                throw new InvalidDataException("Cross-cycle directory reasons were not preserved safely.");

            uint unsupportedReasons = UsnReasonNamedDataExtend | UsnReasonHardLinkChange;
            ChangedRecord fileCycles = new(88, unsupportedReasons | UsnReasonClose, unsupportedReasons | UsnReasonClose, FileAttributes.Archive);
            fileCycles = AccumulateChangedRecord(fileCycles, new(88, 105, basicInfoChange | UsnReasonClose, FileAttributes.Archive));
            if ((fileCycles.AllReasons & UnsupportedFileReasons) != unsupportedReasons
                || (fileCycles.LatestCycleReasons & UnsupportedFileReasons) != 0)
                throw new InvalidDataException("Cross-cycle stream or hard-link reasons were not preserved safely.");
            string containmentRoot = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\", "SpaceLens-CaseRoot");
            string containedPath = Path.Combine(containmentRoot, "child.bin");
            string caseVariant = Path.Combine(Path.GetDirectoryName(containmentRoot)!, "spacelens-caseroot", "child.bin");
            if (!IsStrictlyUnderCanonicalRoot(containedPath, containmentRoot) || IsStrictlyUnderCanonicalRoot(caseVariant, containmentRoot))
                throw new InvalidDataException("Canonical journal containment did not preserve case-sensitive boundaries.");
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes, FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode, IntPtr input, int inputSize, byte[] output, int outputSize, out int bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode, ref ReadUsnJournalDataV1 input, int inputSize, byte[] output, int outputSize, out int bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(SafeFileHandle volumeHint, ref FileIdDescriptor fileId, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes, uint flagsAndAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileAttributeTagInformation information, uint bufferSize);
}
