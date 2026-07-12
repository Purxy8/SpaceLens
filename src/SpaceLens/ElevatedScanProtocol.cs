using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace DesktopOrganizer;

internal enum ElevatedScanFrameType : byte
{
    Hello = 1,
    Batch = 2,
    Completed = 3,
    Error = 4,
    Ready = 5,
    Removed = 6,
    RefreshCompleted = 7,
    RefreshFallback = 8,
    Start = 10,
    Cancel = 11,
    RefreshStart = 12
}

internal readonly record struct ElevatedRefreshCompletion(
    NtfsJournalCheckpoint Checkpoint,
    int JournalRecords,
    int Upserts,
    int Removed);

internal sealed class ElevatedScanDecodeBudget
{
    private readonly int maximumFiles;
    private readonly long maximumDecodedBytes;
    private readonly long maximumPathBytes;
    private int files;
    private long decodedBytes;
    private long pathBytes;

    internal ElevatedScanDecodeBudget(
        int maximumFiles = ElevatedScanProtocol.MaximumTotalFiles,
        long maximumDecodedBytes = ElevatedScanProtocol.MaximumDecodedBytes,
        long maximumPathBytes = ElevatedScanProtocol.MaximumDecodedPathBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFiles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDecodedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPathBytes);
        this.maximumFiles = maximumFiles;
        this.maximumDecodedBytes = maximumDecodedBytes;
        this.maximumPathBytes = maximumPathBytes;
    }

    internal int Files => files;
    internal long DecodedBytes => decodedBytes;
    internal long PathBytes => pathBytes;

    internal void Reserve(int additionalFiles, int additionalDecodedBytes, long additionalPathBytes)
    {
        if (additionalFiles < 0 || additionalDecodedBytes < 0 || additionalPathBytes < 0)
            throw new InvalidDataException("The elevated scan decoder received a negative budget charge.");

        int nextFiles;
        long nextDecodedBytes, nextPathBytes;
        try
        {
            nextFiles = checked(files + additionalFiles);
            nextDecodedBytes = checked(decodedBytes + additionalDecodedBytes);
            nextPathBytes = checked(pathBytes + additionalPathBytes);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("The elevated scan exceeded its cumulative decode budget.", ex);
        }

        if (nextFiles > maximumFiles)
            throw new InvalidDataException("The elevated scan exceeded the supported cumulative file-count limit.");
        if (nextDecodedBytes > maximumDecodedBytes)
            throw new InvalidDataException("The elevated scan exceeded the supported cumulative decoded-byte limit.");
        if (nextPathBytes > maximumPathBytes)
            throw new InvalidDataException("The elevated scan exceeded the supported cumulative path-byte limit.");

        files = nextFiles;
        decodedBytes = nextDecodedBytes;
        pathBytes = nextPathBytes;
    }
}

/// <summary>
/// Owns one received protocol body. Large bodies are rented so full-access scans
/// do not allocate a new large-object-heap array for every file batch.
/// </summary>
internal sealed class ElevatedScanFrame : IDisposable
{
    private byte[]? buffer;
    private readonly int bodyLength;
    private readonly bool pooled;

    internal ElevatedScanFrame(ElevatedScanFrameType type, byte[] body, int length, bool pooledBody)
    {
        if (length < 0 || length > body.Length) throw new ArgumentOutOfRangeException(nameof(length));
        Type = type;
        buffer = body;
        bodyLength = length;
        pooled = pooledBody;
    }

    internal ElevatedScanFrameType Type { get; }

    internal ReadOnlySpan<byte> Body
    {
        get
        {
            byte[] value = Volatile.Read(ref buffer) ?? throw new ObjectDisposedException(nameof(ElevatedScanFrame));
            return value.AsSpan(0, bodyLength);
        }
    }

    internal void ClearBody()
    {
        byte[] value = Volatile.Read(ref buffer) ?? throw new ObjectDisposedException(nameof(ElevatedScanFrame));
        value.AsSpan(0, bodyLength).Clear();
    }

    public void Dispose()
    {
        byte[]? value = Interlocked.Exchange(ref buffer, null);
        if (pooled && value is not null) ArrayPool<byte>.Shared.Return(value);
    }
}

/// <summary>
/// Small, bounded binary protocol used between the ordinary UI process and the
/// short-lived elevated scanner. Every frame is length-prefixed and every
/// variable-length field is checked before allocation or construction.
/// </summary>
internal static class ElevatedScanProtocol
{
    internal const int Version = 5;
    internal const int NonceLength = 32;
    internal const int MaximumTotalFiles = 2_000_000;
    internal const long MaximumDecodedBytes = 512L * 1024 * 1024;
    internal const long MaximumDecodedPathBytes = 384L * 1024 * 1024;

    private const int FrameHeaderLength = sizeof(int) + sizeof(byte);
    private const int MaximumFrameLength = 8 * 1024 * 1024;
    private const int MaximumFrameBodyLength = MaximumFrameLength - 1;
    private const int MaximumBatchItems = 8_000;
    private const int MaximumRecordLength = 256 * 1024;
    private const int MaximumPathBytes = 128 * 1024;
    private const int MaximumErrorBytes = 16 * 1024;
    private const int FixedRecordBytes = 55;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly ArrayPool<byte> FrameBufferPool = ArrayPool<byte>.Create(MaximumFrameBodyLength, 1);

    internal static async Task WriteHelloAsync(Stream stream, byte[] nonce, CancellationToken token)
    {
        if (nonce.Length != NonceLength) throw new ArgumentException("The scan nonce has an invalid length.", nameof(nonce));
        byte[] body = new byte[sizeof(int) + NonceLength];
        BinaryPrimitives.WriteInt32LittleEndian(body, Version);
        nonce.CopyTo(body.AsSpan(sizeof(int)));
        await WriteFrameAsync(stream, ElevatedScanFrameType.Hello, body, token).ConfigureAwait(false);
    }

    internal static byte[] ReadHello(ElevatedScanFrame frame)
    {
        RequireType(frame, ElevatedScanFrameType.Hello);
        ReadOnlySpan<byte> body = frame.Body;
        if (body.Length != sizeof(int) + NonceLength) throw Invalid("The helper handshake has an invalid length.");
        if (BinaryPrimitives.ReadInt32LittleEndian(body) != Version) throw Invalid("The elevated scan protocol version is not supported.");
        return body.Slice(sizeof(int), NonceLength).ToArray();
    }

    internal static async Task WriteStartAsync(Stream stream, string root, NativeFileId rootIdentity, CancellationToken token)
    {
        ValidateRootIdentity(rootIdentity);
        int rootBytes = GetBoundedUtf8ByteCount(root, MaximumPathBytes);
        byte[] body = new byte[checked(sizeof(int) + sizeof(int) + rootBytes + sizeof(uint) + sizeof(ulong))];
        {
            var writer = new SpanWriter(body);
            writer.WriteInt32(Version);
            writer.WriteUtf8String(root, rootBytes);
            writer.WriteUInt32(rootIdentity.VolumeSerial);
            writer.WriteUInt64(rootIdentity.FileIndex);
        }
        await WriteFrameAsync(stream, ElevatedScanFrameType.Start, body, token).ConfigureAwait(false);
    }

    internal static (string Root, NativeFileId RootIdentity) ReadStart(ElevatedScanFrame frame)
    {
        RequireType(frame, ElevatedScanFrameType.Start);
        var reader = new SpanReader(frame.Body);
        if (reader.ReadInt32() != Version) throw Invalid("The elevated scan protocol version is not supported.");
        string root = reader.ReadUtf8String(MaximumPathBytes);
        var rootIdentity = new NativeFileId(reader.ReadUInt32(), reader.ReadUInt64());
        reader.EnsureConsumed();
        ValidateRootIdentity(rootIdentity);
        return (root, rootIdentity);
    }

    internal static async Task WriteRefreshStartAsync(Stream stream, string root, NativeFileId rootIdentity, NtfsJournalCheckpoint checkpoint, long baselineDriveUsedBytes, CancellationToken token)
    {
        ValidateRootIdentity(rootIdentity);
        if (!checkpoint.IsValid) throw new ArgumentException("The quick-refresh checkpoint is invalid.", nameof(checkpoint));
        if (checkpoint.VolumeSerial != rootIdentity.VolumeSerial) throw new ArgumentException("The quick-refresh checkpoint belongs to a different root volume.", nameof(checkpoint));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baselineDriveUsedBytes);
        int rootBytes = GetBoundedUtf8ByteCount(root, MaximumPathBytes);
        byte[] body = new byte[checked(sizeof(int) + sizeof(int) + rootBytes + sizeof(uint) + sizeof(ulong) + sizeof(uint) + sizeof(ulong) + sizeof(long) + sizeof(long))];
        {
            var writer = new SpanWriter(body);
            writer.WriteInt32(Version);
            writer.WriteUtf8String(root, rootBytes);
            writer.WriteUInt32(rootIdentity.VolumeSerial);
            writer.WriteUInt64(rootIdentity.FileIndex);
            writer.WriteUInt32(checkpoint.VolumeSerial);
            writer.WriteUInt64(checkpoint.JournalId);
            writer.WriteInt64(checkpoint.NextUsn);
            writer.WriteInt64(baselineDriveUsedBytes);
        }
        await WriteFrameAsync(stream, ElevatedScanFrameType.RefreshStart, body, token).ConfigureAwait(false);
    }

    internal static (string Root, NativeFileId RootIdentity, NtfsJournalCheckpoint Checkpoint, long BaselineDriveUsedBytes) ReadRefreshStart(ElevatedScanFrame frame)
    {
        RequireType(frame, ElevatedScanFrameType.RefreshStart);
        var reader = new SpanReader(frame.Body);
        if (reader.ReadInt32() != Version) throw Invalid("The elevated scan protocol version is not supported.");
        string root = reader.ReadUtf8String(MaximumPathBytes);
        var rootIdentity = new NativeFileId(reader.ReadUInt32(), reader.ReadUInt64());
        var checkpoint = new NtfsJournalCheckpoint(reader.ReadUInt32(), reader.ReadUInt64(), reader.ReadInt64());
        long baselineDriveUsedBytes = reader.ReadInt64();
        reader.EnsureConsumed();
        ValidateRootIdentity(rootIdentity);
        if (!checkpoint.IsValid || checkpoint.VolumeSerial != rootIdentity.VolumeSerial || baselineDriveUsedBytes <= 0)
            throw Invalid("The quick-refresh request contains invalid baseline metadata.");
        return (root, rootIdentity, checkpoint, baselineDriveUsedBytes);
    }

    internal static async Task WriteBatchesAsync(
        Stream stream,
        IReadOnlyList<FileItem> items,
        int skipped,
        CancellationToken token)
    {
        if (skipped < 0) throw new ArgumentOutOfRangeException(nameof(skipped));
        if (items.Count == 0) return;

        byte[] rentedBuffer = FrameBufferPool.Rent(MaximumFrameBodyLength);
        try
        {
            int itemIndex = 0;
            while (itemIndex < items.Count)
            {
                int bodyLength = BuildBatchBody(rentedBuffer, items, ref itemIndex, skipped, token);
                await WriteFrameAsync(
                    stream,
                    ElevatedScanFrameType.Batch,
                    rentedBuffer.AsMemory(0, bodyLength),
                    token,
                    flush: false).ConfigureAwait(false);
            }
        }
        finally { FrameBufferPool.Return(rentedBuffer); }
    }

    internal static (List<FileItem> Batch, int Skipped) ReadBatch(ElevatedScanFrame frame, ElevatedScanDecodeBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        RequireType(frame, ElevatedScanFrameType.Batch);
        BatchMetadata metadata = InspectBatch(frame.Body);
        budget.Reserve(metadata.Count, frame.Body.Length, metadata.PathBytes);

        var reader = new SpanReader(frame.Body);
        _ = reader.ReadInt32();
        _ = reader.ReadInt32();
        var files = new List<FileItem>(metadata.Count);
        for (int index = 0; index < metadata.Count; index++)
        {
            int recordLength = reader.ReadInt32();
            files.Add(DeserializeFileItem(reader.ReadBytes(recordLength)));
        }
        reader.EnsureConsumed();
        return (files, metadata.Skipped);
    }

    internal static Task WriteCompletedAsync(Stream stream, int skipped, bool backupPrivilegeEnabled, CancellationToken token, NtfsJournalCheckpoint checkpoint = default)
    {
        if (skipped < 0) throw new ArgumentOutOfRangeException(nameof(skipped));
        if (checkpoint != default && !checkpoint.IsValid) throw Invalid("The scan completion contains a partial NTFS journal checkpoint.");
        byte[] body = new byte[26];
        var writer = new SpanWriter(body);
        writer.WriteInt32(skipped);
        writer.WriteByte(backupPrivilegeEnabled ? (byte)1 : (byte)0);
        writer.WriteByte(checkpoint.IsValid ? (byte)1 : (byte)0);
        writer.WriteUInt32(checkpoint.IsValid ? checkpoint.VolumeSerial : 0);
        writer.WriteUInt64(checkpoint.IsValid ? checkpoint.JournalId : 0);
        writer.WriteInt64(checkpoint.IsValid ? checkpoint.NextUsn : 0);
        return WriteFrameAsync(stream, ElevatedScanFrameType.Completed, body, token);
    }

    internal static ElevatedScanResult ReadCompleted(ElevatedScanFrame frame)
    {
        RequireType(frame, ElevatedScanFrameType.Completed);
        var reader = new SpanReader(frame.Body);
        int skipped = reader.ReadInt32();
        byte backup = reader.ReadByte(), hasCheckpoint = reader.ReadByte();
        var checkpoint = new NtfsJournalCheckpoint(reader.ReadUInt32(), reader.ReadUInt64(), reader.ReadInt64());
        reader.EnsureConsumed();
        if (backup > 1 || hasCheckpoint > 1 || (hasCheckpoint == 1) != checkpoint.IsValid || (hasCheckpoint == 0 && checkpoint != default))
            throw Invalid("The elevated scanner returned an invalid completion record.");
        if (skipped < 0) throw Invalid("The helper returned a negative skipped-location count.");
        return new(skipped, backup == 1, checkpoint);
    }

    internal static Task WriteReadyAsync(Stream stream, bool backupPrivilegeEnabled, CancellationToken token)
        => WriteFrameAsync(stream, ElevatedScanFrameType.Ready, new byte[] { backupPrivilegeEnabled ? (byte)1 : (byte)0 }, token);

    internal static bool ReadReady(ElevatedScanFrame frame)
    {
        RequireType(frame, ElevatedScanFrameType.Ready);
        ReadOnlySpan<byte> body = frame.Body;
        if (body.Length != 1 || body[0] > 1) throw Invalid("The elevated scanner returned an invalid ready record.");
        return body[0] == 1;
    }

    internal static Task WriteCancelAsync(Stream stream, CancellationToken token)
        => WriteFrameAsync(stream, ElevatedScanFrameType.Cancel, ReadOnlyMemory<byte>.Empty, token);

    internal static void ReadCancel(ElevatedScanFrame frame)
    {
        RequireType(frame, ElevatedScanFrameType.Cancel);
        if (frame.Body.Length != 0) throw Invalid("The cancel frame is malformed.");
    }

    internal static async Task WriteRemovedAsync(Stream stream, IReadOnlyList<NativeFileId> removed, CancellationToken token)
    {
        int index = 0;
        while (index < removed.Count)
        {
            int count = Math.Min(MaximumBatchItems, removed.Count - index);
            byte[] body = new byte[checked(sizeof(int) + count * (sizeof(uint) + sizeof(ulong)))];
            var writer = new SpanWriter(body);
            writer.WriteInt32(count);
            for (int offset = 0; offset < count; offset++)
            {
                NativeFileId id = removed[index++];
                if (id.VolumeSerial == 0 || id.FileIndex == 0) throw Invalid("A removed-file identity is incomplete.");
                writer.WriteUInt32(id.VolumeSerial);
                writer.WriteUInt64(id.FileIndex);
            }
            await WriteFrameAsync(stream, ElevatedScanFrameType.Removed, body, token, flush: false).ConfigureAwait(false);
        }
    }

    internal static List<NativeFileId> ReadRemoved(ElevatedScanFrame frame, ElevatedScanDecodeBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        RequireType(frame, ElevatedScanFrameType.Removed);
        var reader = new SpanReader(frame.Body);
        int count = reader.ReadInt32();
        if (count <= 0 || count > MaximumBatchItems) throw Invalid("The helper returned an invalid removed-file count.");
        int expectedBytes = checked(count * (sizeof(uint) + sizeof(ulong)));
        if (reader.Remaining != expectedBytes) throw Invalid("The helper returned an invalid removed-file body length.");
        budget.Reserve(count, frame.Body.Length, 0);
        var removed = new List<NativeFileId>(count);
        for (int index = 0; index < count; index++)
        {
            var id = new NativeFileId(reader.ReadUInt32(), reader.ReadUInt64());
            if (id.VolumeSerial == 0 || id.FileIndex == 0) throw Invalid("The helper returned an incomplete removed-file identity.");
            removed.Add(id);
        }
        reader.EnsureConsumed();
        return removed;
    }

    internal static Task WriteRefreshCompletedAsync(Stream stream, ElevatedRefreshCompletion completion, CancellationToken token)
    {
        if (!completion.Checkpoint.IsValid || completion.JournalRecords < 0 || completion.Upserts < 0 || completion.Removed < 0)
            throw Invalid("The quick-refresh completion metadata is invalid.");
        byte[] body = new byte[32];
        var writer = new SpanWriter(body);
        writer.WriteUInt32(completion.Checkpoint.VolumeSerial);
        writer.WriteUInt64(completion.Checkpoint.JournalId);
        writer.WriteInt64(completion.Checkpoint.NextUsn);
        writer.WriteInt32(completion.JournalRecords);
        writer.WriteInt32(completion.Upserts);
        writer.WriteInt32(completion.Removed);
        return WriteFrameAsync(stream, ElevatedScanFrameType.RefreshCompleted, body, token);
    }

    internal static ElevatedRefreshCompletion ReadRefreshCompleted(ElevatedScanFrame frame)
    {
        RequireType(frame, ElevatedScanFrameType.RefreshCompleted);
        var reader = new SpanReader(frame.Body);
        var completion = new ElevatedRefreshCompletion(
            new(reader.ReadUInt32(), reader.ReadUInt64(), reader.ReadInt64()),
            reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
        reader.EnsureConsumed();
        if (!completion.Checkpoint.IsValid || completion.JournalRecords < 0 || completion.Upserts < 0 || completion.Removed < 0)
            throw Invalid("The helper returned invalid quick-refresh completion metadata.");
        return completion;
    }

    internal static async Task WriteRefreshFallbackAsync(Stream stream, string message, CancellationToken token)
    {
        if (message.Length > 4_096)
        {
            int length = 4_096;
            if (char.IsHighSurrogate(message[length - 1])) length--;
            message = message[..length];
        }
        int messageBytes = GetBoundedUtf8ByteCount(message, MaximumErrorBytes);
        byte[] body = new byte[checked(sizeof(int) + messageBytes)];
        var writer = new SpanWriter(body);
        writer.WriteUtf8String(message, messageBytes);
        await WriteFrameAsync(stream, ElevatedScanFrameType.RefreshFallback, body, token).ConfigureAwait(false);
    }

    internal static string ReadRefreshFallback(ElevatedScanFrame frame)
    {
        RequireType(frame, ElevatedScanFrameType.RefreshFallback);
        var reader = new SpanReader(frame.Body);
        string message = reader.ReadUtf8String(MaximumErrorBytes);
        reader.EnsureConsumed();
        return message;
    }

    internal static async Task WriteErrorAsync(Stream stream, string message, CancellationToken token)
    {
        if (message.Length > 4_096)
        {
            int length = 4_096;
            if (char.IsHighSurrogate(message[length - 1])) length--;
            message = message[..length];
        }
        int messageBytes = GetBoundedUtf8ByteCount(message, MaximumErrorBytes);
        byte[] body = new byte[checked(sizeof(int) + messageBytes)];
        {
            var writer = new SpanWriter(body);
            writer.WriteUtf8String(message, messageBytes);
        }
        await WriteFrameAsync(stream, ElevatedScanFrameType.Error, body, token).ConfigureAwait(false);
    }

    internal static string ReadError(ElevatedScanFrame frame)
    {
        RequireType(frame, ElevatedScanFrameType.Error);
        var reader = new SpanReader(frame.Body);
        string message = reader.ReadUtf8String(MaximumErrorBytes);
        reader.EnsureConsumed();
        return message;
    }

    internal static async Task<ElevatedScanFrame> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        byte[] header = ArrayPool<byte>.Shared.Rent(FrameHeaderLength);
        int frameLength;
        ElevatedScanFrameType type;
        try
        {
            await ReadExactlyAsync(stream, header.AsMemory(0, sizeof(int)), token).ConfigureAwait(false);
            frameLength = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (frameLength < 1 || frameLength > MaximumFrameLength) throw Invalid("The elevated scan frame length is invalid.");
            await ReadExactlyAsync(stream, header.AsMemory(sizeof(int), sizeof(byte)), token).ConfigureAwait(false);
            type = (ElevatedScanFrameType)header[sizeof(int)];
        }
        finally { ArrayPool<byte>.Shared.Return(header); }

        int bodyLength = frameLength - 1;
        if (bodyLength == 0) return new(type, Array.Empty<byte>(), 0, pooledBody: false);

        byte[] body = ArrayPool<byte>.Shared.Rent(bodyLength);
        try
        {
            await ReadExactlyAsync(stream, body.AsMemory(0, bodyLength), token).ConfigureAwait(false);
            return new(type, body, bodyLength, pooledBody: true);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(body);
            throw;
        }
    }

    internal static void RunSelfTest()
    {
        byte[] nonce = Enumerable.Range(0, NonceLength).Select(value => (byte)value).ToArray();
        using (var stream = new MemoryStream())
        {
            WriteHelloAsync(stream, nonce, CancellationToken.None).GetAwaiter().GetResult();
            stream.Position = 0;
            using ElevatedScanFrame frame = ReadFrameAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
            byte[] decoded = ReadHello(frame);
            if (!decoded.SequenceEqual(nonce)) throw Invalid("Elevated scan hello round-trip failed.");
        }

        string path = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\", "SpaceLens-protocol-test.bin");
        string protocolRoot = Path.GetPathRoot(path)!;
        var rootIdentity = new NativeFileId(123, 456);
        var source = new FileItem(path, 4_096, 8_192, DateTime.UnixEpoch, "Windows & system", DateTime.UnixEpoch.AddSeconds(1), false, 123, 456, FileAttributes.System, FileSafety.Protected);

        using (var stream = new MemoryStream())
        {
            WriteStartAsync(stream, protocolRoot, rootIdentity, CancellationToken.None).GetAwaiter().GetResult();
            stream.Position = 0;
            using ElevatedScanFrame frame = ReadFrameAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
            (string decodedRoot, NativeFileId decodedIdentity) = ReadStart(frame);
            if (decodedRoot != protocolRoot || decodedIdentity != rootIdentity) throw Invalid("Elevated scan start round-trip failed.");
        }

        int protocolRootBytes = GetBoundedUtf8ByteCount(protocolRoot, MaximumPathBytes);
        byte[] missingIdentityBody = new byte[checked(sizeof(int) + sizeof(int) + protocolRootBytes)];
        var missingIdentityWriter = new SpanWriter(missingIdentityBody);
        missingIdentityWriter.WriteInt32(Version);
        missingIdentityWriter.WriteUtf8String(protocolRoot, protocolRootBytes);
        bool missingIdentityRejected = false;
        try
        {
            using var frame = new ElevatedScanFrame(ElevatedScanFrameType.Start, missingIdentityBody, missingIdentityBody.Length, pooledBody: false);
            _ = ReadStart(frame);
        }
        catch (InvalidDataException) { missingIdentityRejected = true; }
        if (!missingIdentityRejected) throw Invalid("Elevated scan accepted a start request without a root identity.");

        byte[] oldVersionBody = new byte[checked(sizeof(int) + sizeof(int) + protocolRootBytes + sizeof(uint) + sizeof(ulong))];
        var oldVersionWriter = new SpanWriter(oldVersionBody);
        oldVersionWriter.WriteInt32(Version - 1);
        oldVersionWriter.WriteUtf8String(protocolRoot, protocolRootBytes);
        oldVersionWriter.WriteUInt32(rootIdentity.VolumeSerial);
        oldVersionWriter.WriteUInt64(rootIdentity.FileIndex);
        bool oldVersionRejected = false;
        try
        {
            using var frame = new ElevatedScanFrame(ElevatedScanFrameType.Start, oldVersionBody, oldVersionBody.Length, pooledBody: false);
            _ = ReadStart(frame);
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("version", StringComparison.OrdinalIgnoreCase)) { oldVersionRejected = true; }
        if (!oldVersionRejected) throw Invalid("Elevated scan accepted an incompatible pre-identity protocol version.");

        bool zeroIdentityRejected = false;
        try
        {
            using var stream = new MemoryStream();
            WriteStartAsync(stream, protocolRoot, default, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("identity", StringComparison.OrdinalIgnoreCase)) { zeroIdentityRejected = true; }
        if (!zeroIdentityRejected) throw Invalid("Elevated scan accepted an incomplete root identity.");

        byte[] encodedBatch;
        using (var stream = new MemoryStream())
        {
            WriteBatchesAsync(stream, [source], 7, CancellationToken.None).GetAwaiter().GetResult();
            encodedBatch = stream.ToArray();
            stream.Position = 0;
            using ElevatedScanFrame frame = ReadFrameAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
            (List<FileItem> files, int skipped) = ReadBatch(frame, new ElevatedScanDecodeBudget());
            if (skipped != 7 || files.Count != 1 || files[0] != source) throw Invalid("Elevated scan batch round-trip failed.");
        }

        int encodedBodyLength = encodedBatch.Length - FrameHeaderLength;
        int encodedPathBytes = Utf8.GetByteCount(source.Path);
        var fileBudget = new ElevatedScanDecodeBudget(1, encodedBodyLength * 2L, encodedPathBytes * 2L);
        using (var first = new MemoryStream(encodedBatch, false))
        {
            using ElevatedScanFrame frame = ReadFrameAsync(first, CancellationToken.None).GetAwaiter().GetResult();
            _ = ReadBatch(frame, fileBudget);
        }
        bool fileBudgetRejected = false;
        try
        {
            using var second = new MemoryStream(encodedBatch, false);
            using ElevatedScanFrame frame = ReadFrameAsync(second, CancellationToken.None).GetAwaiter().GetResult();
            _ = ReadBatch(frame, fileBudget);
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("file-count", StringComparison.OrdinalIgnoreCase)) { fileBudgetRejected = true; }
        if (!fileBudgetRejected || fileBudget.Files != 1) throw Invalid("Elevated scan cumulative file budget did not fail closed.");

        bool decodedByteBudgetRejected = false;
        try
        {
            using var stream = new MemoryStream(encodedBatch, false);
            using ElevatedScanFrame frame = ReadFrameAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
            _ = ReadBatch(frame, new ElevatedScanDecodeBudget(2, encodedBodyLength - 1L, encodedPathBytes));
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("decoded-byte", StringComparison.OrdinalIgnoreCase)) { decodedByteBudgetRejected = true; }
        if (!decodedByteBudgetRejected) throw Invalid("Elevated scan cumulative decoded-byte budget did not fail closed.");

        bool pathBudgetRejected = false;
        try
        {
            using var stream = new MemoryStream(encodedBatch, false);
            using ElevatedScanFrame frame = ReadFrameAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
            _ = ReadBatch(frame, new ElevatedScanDecodeBudget(2, encodedBodyLength, encodedPathBytes - 1L));
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("path-byte", StringComparison.OrdinalIgnoreCase)) { pathBudgetRejected = true; }
        if (!pathBudgetRejected) throw Invalid("Elevated scan cumulative path-byte budget did not fail closed.");

        byte[] invalidSafety = (byte[])encodedBatch.Clone();
        invalidSafety[^1] = (byte)FileSafety.Unknown;
        bool invalidSafetyRejected = false;
        try
        {
            using var tampered = new MemoryStream(invalidSafety, false);
            using ElevatedScanFrame frame = ReadFrameAsync(tampered, CancellationToken.None).GetAwaiter().GetResult();
            _ = ReadBatch(frame, new ElevatedScanDecodeBudget());
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("safety", StringComparison.OrdinalIgnoreCase)) { invalidSafetyRejected = true; }
        if (!invalidSafetyRejected) throw Invalid("Elevated scan accepted an invalid safety value.");

        byte[] invalidUtf8 = (byte[])encodedBatch.Clone();
        int firstPathByte = FrameHeaderLength + sizeof(int) + sizeof(int) + sizeof(int) + sizeof(int);
        invalidUtf8[firstPathByte] = 0xFF;
        bool invalidUtf8Rejected = false;
        try
        {
            using var tampered = new MemoryStream(invalidUtf8, false);
            using ElevatedScanFrame frame = ReadFrameAsync(tampered, CancellationToken.None).GetAwaiter().GetResult();
            _ = ReadBatch(frame, new ElevatedScanDecodeBudget());
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("UTF-8", StringComparison.OrdinalIgnoreCase)) { invalidUtf8Rejected = true; }
        if (!invalidUtf8Rejected) throw Invalid("Elevated scan accepted an invalid UTF-8 path.");

        using (var oversized = new MemoryStream())
        {
            byte[] length = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(length, MaximumFrameLength + 1);
            oversized.Write(length);
            oversized.Position = 0;
            bool oversizedRejected = false;
            try
            {
                using ElevatedScanFrame frame = ReadFrameAsync(oversized, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (InvalidDataException ex) when (ex.Message.Contains("length", StringComparison.OrdinalIgnoreCase)) { oversizedRejected = true; }
            catch (EndOfStreamException) { oversizedRejected = false; }
            if (!oversizedRejected) throw Invalid("Elevated scan accepted an oversized frame.");
        }

        bool directoryRejected = false;
        try
        {
            using var stream = new MemoryStream();
            var directory = source with { Attributes = FileAttributes.Directory };
            WriteBatchesAsync(stream, [directory], 0, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("file record", StringComparison.OrdinalIgnoreCase)) { directoryRejected = true; }
        if (!directoryRejected) throw Invalid("Elevated scan accepted a directory as a file record.");

        using (var stream = new MemoryStream())
        {
            WriteReadyAsync(stream, true, CancellationToken.None).GetAwaiter().GetResult();
            stream.Position = 0;
            ElevatedScanFrame frame = ReadFrameAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
            frame.Dispose();
            bool disposedRejected = false;
            try { _ = frame.Body.Length; }
            catch (ObjectDisposedException) { disposedRejected = true; }
            if (!disposedRejected) throw Invalid("A returned elevated frame remained readable after disposal.");
        }

        var checkpoint = new NtfsJournalCheckpoint(123, 456, 789);
        using (var stream = new MemoryStream())
        {
            WriteRefreshStartAsync(stream, protocolRoot, rootIdentity, checkpoint, 123_456, CancellationToken.None).GetAwaiter().GetResult();
            stream.Position = 0;
            using ElevatedScanFrame frame = ReadFrameAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
            (string refreshRoot, NativeFileId refreshRootIdentity, NtfsJournalCheckpoint decodedCheckpoint, long baselineDriveUsedBytes) = ReadRefreshStart(frame);
            if (refreshRoot != protocolRoot || refreshRootIdentity != rootIdentity || decodedCheckpoint != checkpoint || baselineDriveUsedBytes != 123_456) throw Invalid("Quick-refresh start round-trip failed.");
        }
        using (var stream = new MemoryStream())
        {
            WriteCompletedAsync(stream, 9, true, CancellationToken.None, checkpoint).GetAwaiter().GetResult();
            stream.Position = 0;
            using ElevatedScanFrame frame = ReadFrameAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
            ElevatedScanResult completed = ReadCompleted(frame);
            if (completed.Skipped != 9 || !completed.BackupPrivilegeEnabled || completed.JournalCheckpoint != checkpoint) throw Invalid("Checkpoint completion round-trip failed.");
        }
        using (var stream = new MemoryStream())
        {
            var removed = new List<NativeFileId> { new(123, 1), new(123, 2) };
            WriteRemovedAsync(stream, removed, CancellationToken.None).GetAwaiter().GetResult();
            stream.Position = 0;
            using ElevatedScanFrame frame = ReadFrameAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
            if (!ReadRemoved(frame, new ElevatedScanDecodeBudget()).SequenceEqual(removed)) throw Invalid("Removed-file round-trip failed.");
        }
        using (var stream = new MemoryStream())
        {
            var completion = new ElevatedRefreshCompletion(checkpoint, 12, 3, 4);
            WriteRefreshCompletedAsync(stream, completion, CancellationToken.None).GetAwaiter().GetResult();
            stream.Position = 0;
            using ElevatedScanFrame frame = ReadFrameAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
            if (ReadRefreshCompleted(frame) != completion) throw Invalid("Quick-refresh completion round-trip failed.");
        }
        ElevatedScanRunner.RunSecuritySelfTest();
    }

    internal static void RunPerformanceSelfTest()
    {
        const int count = 50_000;
        string root = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\", "SpaceLens-protocol-performance");
        var source = new List<FileItem>(count);
        for (int index = 0; index < count; index++)
        {
            string category = (index % 3) switch { 0 => "Downloads", 1 => "Temporary & caches", _ => "Personal files" };
            source.Add(new(Path.Combine(root, $"folder-{index % 101:D3}", $"file-{index:D6}.bin"), index + 1, index + 2, DateTime.UnixEpoch.AddSeconds(index), category, DateTime.UnixEpoch));
        }

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        using var stream = new MemoryStream();
        WriteBatchesAsync(stream, source, 17, CancellationToken.None).GetAwaiter().GetResult();
        stream.Position = 0;
        int decoded = 0;
        var budget = new ElevatedScanDecodeBudget();
        while (stream.Position < stream.Length)
        {
            using ElevatedScanFrame frame = ReadFrameAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
            (List<FileItem> files, int skipped) = ReadBatch(frame, budget);
            if (skipped != 17 || files.Count == 0) throw Invalid("Elevated protocol performance fixture returned invalid metadata.");
            decoded = checked(decoded + files.Count);
        }
        timer.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        if (decoded != count) throw Invalid("Elevated protocol performance fixture lost file records.");
        if (timer.Elapsed > TimeSpan.FromSeconds(8)) throw Invalid($"Elevated protocol performance regression ({timer.ElapsedMilliseconds} ms). ");
        if (allocated > 112L * 1024 * 1024) throw Invalid($"Elevated protocol allocation regression ({allocated / (1024 * 1024):N0} MB). ");
    }

    private static BatchMetadata InspectBatch(ReadOnlySpan<byte> body)
    {
        var reader = new SpanReader(body);
        int skipped = reader.ReadInt32();
        int count = reader.ReadInt32();
        if (skipped < 0 || count <= 0 || count > MaximumBatchItems)
            throw Invalid("The helper returned invalid batch metadata.");

        long pathBytes = 0;
        for (int index = 0; index < count; index++)
        {
            int recordLength = reader.ReadInt32();
            if (recordLength < sizeof(int) || recordLength > MaximumRecordLength || recordLength > reader.Remaining)
                throw Invalid("The helper returned an invalid file record length.");
            ReadOnlySpan<byte> record = reader.ReadBytes(recordLength);
            int recordPathBytes = BinaryPrimitives.ReadInt32LittleEndian(record);
            if (recordPathBytes < 0 || recordPathBytes > MaximumPathBytes || recordPathBytes > record.Length - sizeof(int))
                throw Invalid("The helper returned an invalid file path length.");
            pathBytes = checked(pathBytes + recordPathBytes);
        }
        reader.EnsureConsumed();
        return new(skipped, count, pathBytes);
    }

    private static int BuildBatchBody(
        byte[] buffer,
        IReadOnlyList<FileItem> items,
        ref int itemIndex,
        int skipped,
        CancellationToken token)
    {
        var writer = new SpanWriter(buffer.AsSpan(0, MaximumFrameBodyLength));
        writer.WriteInt32(skipped);
        int countPosition = writer.Position;
        writer.WriteInt32(0);
        int frameItems = 0;

        while (itemIndex < items.Count && frameItems < MaximumBatchItems)
        {
            token.ThrowIfCancellationRequested();
            FileItem item = items[itemIndex];
            SerializedRecord record = MeasureFileItem(item);
            int nextLength = checked(writer.Position + sizeof(int) + record.Length);
            if (frameItems > 0 && nextLength > MaximumFrameBodyLength) break;
            if (nextLength > MaximumFrameBodyLength) throw Invalid("A file record exceeds the elevated scan frame limit.");

            writer.WriteInt32(record.Length);
            int recordStart = writer.Position;
            SerializeFileItem(ref writer, item, record);
            if (writer.Position - recordStart != record.Length) throw Invalid("The elevated scan record length is inconsistent.");
            frameItems++;
            itemIndex++;
        }

        if (frameItems <= 0 || writer.Position > MaximumFrameBodyLength) throw Invalid("The elevated scan batch is invalid.");
        writer.WriteInt32At(countPosition, frameItems);
        return writer.Position;
    }

    private static SerializedRecord MeasureFileItem(FileItem item)
    {
        ValidateFileItem(item);
        int pathBytes = GetBoundedUtf8ByteCount(item.Path, MaximumPathBytes);
        byte category = AnalyzerForm.CategoryCode(item.Category);
        FileSafety effectiveSafety = AnalyzerForm.EffectiveSafety(item);
        if (effectiveSafety is not (FileSafety.Normal or FileSafety.Review or FileSafety.Protected))
            throw Invalid("The scanner produced an invalid file safety value.");
        int length = checked(FixedRecordBytes + pathBytes);
        if (length <= 0 || length > MaximumRecordLength) throw Invalid("A scanner file record is too large.");
        return new(length, pathBytes, category, (byte)effectiveSafety);
    }

    private static void SerializeFileItem(ref SpanWriter writer, FileItem item, SerializedRecord record)
    {
        writer.WriteUtf8String(item.Path, record.PathBytes);
        writer.WriteInt64(item.DiskBytes);
        writer.WriteInt64(item.LogicalBytes);
        writer.WriteInt64(item.Modified.ToBinary());
        writer.WriteByte(record.Category);
        writer.WriteInt64(item.Created.ToBinary());
        writer.WriteByte(item.AllocationEstimated ? (byte)1 : (byte)0);
        writer.WriteUInt32(item.VolumeSerial);
        writer.WriteUInt64(item.FileIndex);
        writer.WriteInt32((int)item.Attributes);
        writer.WriteByte(record.Safety);
    }

    private static void ValidateFileItem(FileItem item)
    {
        bool identityComplete = (item.VolumeSerial == 0) == (item.FileIndex == 0);
        if (item.DiskBytes < 0 || item.LogicalBytes < 0 || item.Path.Length > 32_767 || !Path.IsPathFullyQualified(item.Path) || item.Category.Length > 256 || !identityComplete || (item.Attributes & FileAttributes.Directory) != 0)
            throw Invalid("The scanner produced an invalid file record.");
    }

    private static FileItem DeserializeFileItem(ReadOnlySpan<byte> body)
    {
        var reader = new SpanReader(body);
        string path = reader.ReadUtf8String(MaximumPathBytes);
        long diskBytes = reader.ReadInt64();
        long logicalBytes = reader.ReadInt64();
        DateTime modified = ReadDateTime(ref reader);
        string category = AnalyzerForm.CategoryFromCode(reader.ReadByte());
        DateTime created = ReadDateTime(ref reader);
        byte estimatedValue = reader.ReadByte();
        if (estimatedValue > 1) throw Invalid("The helper returned an invalid allocation-estimate flag.");
        bool estimated = estimatedValue == 1;
        uint volumeSerial = reader.ReadUInt32();
        ulong fileIndex = reader.ReadUInt64();
        FileAttributes attributes = (FileAttributes)reader.ReadInt32();
        FileSafety safety = (FileSafety)reader.ReadByte();
        reader.EnsureConsumed();

        if (path.Length == 0 || path.Length > 32_767 || !Path.IsPathFullyQualified(path) || category.Length > 256 || diskBytes < 0 || logicalBytes < 0 || (attributes & FileAttributes.Directory) != 0)
            throw Invalid("The helper returned an invalid file record.");
        if ((volumeSerial == 0) != (fileIndex == 0)) throw Invalid("The helper returned an incomplete file identity.");
        if (safety is not (FileSafety.Normal or FileSafety.Review or FileSafety.Protected)) throw Invalid("The helper returned an invalid file safety value.");

        string canonicalPath;
        try { canonicalPath = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException("The helper returned a file path that could not be canonicalized.", ex);
        }
        return new(canonicalPath, diskBytes, logicalBytes, modified, category, created, estimated, volumeSerial, fileIndex, attributes, safety);
    }

    private static DateTime ReadDateTime(ref SpanReader reader)
    {
        try { return DateTime.FromBinary(reader.ReadInt64()); }
        catch (ArgumentException ex) { throw new InvalidDataException("The helper returned an invalid file timestamp.", ex); }
    }

    private static async Task WriteFrameAsync(
        Stream stream,
        ElevatedScanFrameType type,
        ReadOnlyMemory<byte> body,
        CancellationToken token,
        bool flush = true)
    {
        int frameLength = checked(1 + body.Length);
        if (frameLength > MaximumFrameLength) throw Invalid("The elevated scan frame exceeds the protocol limit.");

        byte[] header = ArrayPool<byte>.Shared.Rent(FrameHeaderLength);
        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(header, frameLength);
            header[sizeof(int)] = (byte)type;
            await stream.WriteAsync(header.AsMemory(0, FrameHeaderLength), token).ConfigureAwait(false);
            if (!body.IsEmpty) await stream.WriteAsync(body, token).ConfigureAwait(false);
            if (flush) await stream.FlushAsync(token).ConfigureAwait(false);
        }
        finally { ArrayPool<byte>.Shared.Return(header); }
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> destination, CancellationToken token)
    {
        int offset = 0;
        while (offset < destination.Length)
        {
            int read = await stream.ReadAsync(destination[offset..], token).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("The elevated scan connection closed unexpectedly.");
            offset += read;
        }
    }

    private static int GetBoundedUtf8ByteCount(string value, int maximumBytes)
    {
        int length;
        try { length = Utf8.GetByteCount(value); }
        catch (EncoderFallbackException ex) { throw new InvalidDataException("A protocol string is not valid UTF-16.", ex); }
        if (length > maximumBytes) throw Invalid("A protocol string exceeds its allowed length.");
        return length;
    }

    private static void ValidateRootIdentity(NativeFileId rootIdentity)
    {
        if (rootIdentity.VolumeSerial == 0 || rootIdentity.FileIndex == 0)
            throw Invalid("The elevated scan root identity is incomplete.");
    }

    private static void RequireType(ElevatedScanFrame frame, ElevatedScanFrameType expected)
    {
        if (frame.Type != expected) throw Invalid($"Expected {expected} but received {frame.Type}.");
    }

    private static InvalidDataException Invalid(string message) => new(message);

    private readonly record struct SerializedRecord(int Length, int PathBytes, byte Category, byte Safety);
    private readonly record struct BatchMetadata(int Skipped, int Count, long PathBytes);

    private ref struct SpanReader
    {
        private readonly ReadOnlySpan<byte> source;
        private int position;

        internal SpanReader(ReadOnlySpan<byte> source)
        {
            this.source = source;
            position = 0;
        }

        internal int Remaining => source.Length - position;

        internal byte ReadByte() => ReadBytes(sizeof(byte))[0];
        internal int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(sizeof(int)));
        internal uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(sizeof(uint)));
        internal long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(ReadBytes(sizeof(long)));
        internal ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(sizeof(ulong)));

        internal ReadOnlySpan<byte> ReadBytes(int count)
        {
            if (count < 0 || count > Remaining) throw Invalid("The elevated scan frame ended unexpectedly.");
            ReadOnlySpan<byte> value = source.Slice(position, count);
            position += count;
            return value;
        }

        internal string ReadUtf8String(int maximumBytes)
        {
            int length = ReadInt32();
            if (length < 0 || length > maximumBytes || length > Remaining) throw Invalid("A protocol string has an invalid length.");
            try { return Utf8.GetString(ReadBytes(length)); }
            catch (DecoderFallbackException ex) { throw new InvalidDataException("A protocol string contains invalid UTF-8.", ex); }
        }

        internal void EnsureConsumed()
        {
            if (Remaining != 0) throw Invalid("The elevated scan frame has trailing data.");
        }
    }

    private ref struct SpanWriter
    {
        private readonly Span<byte> destination;
        private int position;

        internal SpanWriter(Span<byte> destination)
        {
            this.destination = destination;
            position = 0;
        }

        internal int Position => position;

        internal void WriteByte(byte value) => Reserve(sizeof(byte))[0] = value;
        internal void WriteInt32(int value) => BinaryPrimitives.WriteInt32LittleEndian(Reserve(sizeof(int)), value);
        internal void WriteUInt32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Reserve(sizeof(uint)), value);
        internal void WriteInt64(long value) => BinaryPrimitives.WriteInt64LittleEndian(Reserve(sizeof(long)), value);
        internal void WriteUInt64(ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Reserve(sizeof(ulong)), value);

        internal void WriteInt32At(int offset, int value)
        {
            if (offset < 0 || offset > destination.Length - sizeof(int)) throw Invalid("The elevated scan frame writer used an invalid offset.");
            BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset, sizeof(int)), value);
        }

        internal void WriteUtf8String(string value, int encodedLength)
        {
            WriteInt32(encodedLength);
            Span<byte> bytes = Reserve(encodedLength);
            int written = Utf8.GetBytes(value.AsSpan(), bytes);
            if (written != encodedLength) throw Invalid("A protocol string changed while it was being encoded.");
        }

        private Span<byte> Reserve(int count)
        {
            if (count < 0 || count > destination.Length - position) throw Invalid("The elevated scan frame exceeded its bounded buffer.");
            Span<byte> value = destination.Slice(position, count);
            position += count;
            return value;
        }
    }
}
