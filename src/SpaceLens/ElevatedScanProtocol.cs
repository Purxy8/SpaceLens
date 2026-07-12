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
    Start = 10,
    Cancel = 11
}

internal readonly record struct ElevatedScanFrame(ElevatedScanFrameType Type, byte[] Body);

/// <summary>
/// Small, bounded binary protocol used between the ordinary UI process and the
/// short-lived elevated scanner. Every frame is length-prefixed and every
/// variable-length field is checked before allocation or construction.
/// </summary>
internal static class ElevatedScanProtocol
{
    internal const int Version = 3;
    internal const int NonceLength = 32;
    internal const int MaximumTotalFiles = 10_000_000;

    private const int MaximumFrameLength = 8 * 1024 * 1024;
    private const int MaximumFrameBodyLength = MaximumFrameLength - 1;
    private const int MaximumBatchItems = 8_000;
    private const int MaximumRecordLength = 256 * 1024;
    private const int MaximumPathBytes = 128 * 1024;
    private const int MaximumErrorBytes = 16 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly ArrayPool<byte> FrameBufferPool = ArrayPool<byte>.Create(MaximumFrameBodyLength, 1);

    internal static async Task WriteHelloAsync(Stream stream, byte[] nonce, CancellationToken token)
    {
        if (nonce.Length != NonceLength) throw new ArgumentException("The scan nonce has an invalid length.", nameof(nonce));
        using var body = new MemoryStream(4 + NonceLength);
        using (var writer = new BinaryWriter(body, Utf8, true))
        {
            writer.Write(Version);
            writer.Write(nonce);
        }
        await WriteFrameAsync(stream, ElevatedScanFrameType.Hello, body.ToArray(), token).ConfigureAwait(false);
    }

    internal static byte[] ReadHello(ElevatedScanFrame frame)
    {
        RequireType(frame, ElevatedScanFrameType.Hello);
        if (frame.Body.Length != 4 + NonceLength) throw Invalid("The helper handshake has an invalid length.");
        using var body = new MemoryStream(frame.Body, false);
        using var reader = new BinaryReader(body, Utf8, true);
        if (reader.ReadInt32() != Version) throw Invalid("The elevated scan protocol version is not supported.");
        byte[] nonce = reader.ReadBytes(NonceLength);
        EnsureConsumed(body);
        return nonce;
    }

    internal static async Task WriteStartAsync(Stream stream, string root, CancellationToken token)
    {
        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Utf8, true))
        {
            writer.Write(Version);
            WriteBoundedString(writer, root, MaximumPathBytes);
        }
        await WriteFrameAsync(stream, ElevatedScanFrameType.Start, body.ToArray(), token).ConfigureAwait(false);
    }

    internal static string ReadStart(ElevatedScanFrame frame)
    {
        RequireType(frame, ElevatedScanFrameType.Start);
        using var body = new MemoryStream(frame.Body, false);
        using var reader = new BinaryReader(body, Utf8, true);
        if (reader.ReadInt32() != Version) throw Invalid("The elevated scan protocol version is not supported.");
        string root = ReadBoundedString(reader, MaximumPathBytes);
        EnsureConsumed(body);
        return root;
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
            using var body = new MemoryStream(rentedBuffer, 0, rentedBuffer.Length, true, true);
            int itemIndex = 0;
            while (itemIndex < items.Count)
            {
                body.SetLength(0);
                body.Position = 0;
                using var writer = new BinaryWriter(body, Utf8, true);
                writer.Write(skipped);
                long countPosition = body.Position;
                writer.Write(0);
                int frameItems = 0;

                while (itemIndex < items.Count && frameItems < MaximumBatchItems)
                {
                    token.ThrowIfCancellationRequested();
                    FileItem item = items[itemIndex];
                    int recordLength = GetSerializedRecordLength(item);
                    int nextLength = checked((int)body.Length + sizeof(int) + recordLength);
                    if (frameItems > 0 && nextLength > MaximumFrameBodyLength) break;
                    if (nextLength > MaximumFrameBodyLength) throw Invalid("A file record exceeds the elevated scan frame limit.");

                    writer.Write(recordLength);
                    long recordStart = body.Position;
                    SerializeFileItem(writer, item);
                    if (body.Position - recordStart != recordLength) throw Invalid("The elevated scan record length is inconsistent.");
                    frameItems++;
                    itemIndex++;
                }

                long end = body.Position;
                body.Position = countPosition;
                writer.Write(frameItems);
                body.Position = end;
                writer.Flush();
                if (frameItems <= 0 || body.Length > MaximumFrameBodyLength) throw Invalid("The elevated scan batch is invalid.");
                await WriteFrameAsync(stream, ElevatedScanFrameType.Batch, rentedBuffer.AsMemory(0, checked((int)body.Length)), token).ConfigureAwait(false);
            }
        }
        finally { FrameBufferPool.Return(rentedBuffer); }
    }

    internal static (List<FileItem> Batch, int Skipped) ReadBatch(ElevatedScanFrame frame)
    {
        RequireType(frame, ElevatedScanFrameType.Batch);
        using var body = new MemoryStream(frame.Body, false);
        using var reader = new BinaryReader(body, Utf8, true);
        int skipped = reader.ReadInt32();
        int count = reader.ReadInt32();
        if (skipped < 0 || count <= 0 || count > MaximumBatchItems) throw Invalid("The helper returned invalid batch metadata.");

        var files = new List<FileItem>(count);
        for (int index = 0; index < count; index++)
        {
            int recordLength = reader.ReadInt32();
            if (recordLength <= 0 || recordLength > MaximumRecordLength || recordLength > body.Length - body.Position)
                throw Invalid("The helper returned an invalid file record length.");
            int recordOffset = checked((int)body.Position);
            using (var recordBody = new MemoryStream(frame.Body, recordOffset, recordLength, false, true))
                files.Add(DeserializeFileItem(recordBody));
            body.Position = checked(body.Position + recordLength);
        }
        EnsureConsumed(body);
        return (files, skipped);
    }

    internal static Task WriteCompletedAsync(Stream stream, int skipped, bool backupPrivilegeEnabled, CancellationToken token)
    {
        if (skipped < 0) throw new ArgumentOutOfRangeException(nameof(skipped));
        byte[] body = new byte[5];
        BinaryPrimitives.WriteInt32LittleEndian(body, skipped);
        body[4] = backupPrivilegeEnabled ? (byte)1 : (byte)0;
        return WriteFrameAsync(stream, ElevatedScanFrameType.Completed, body, token);
    }

    internal static ElevatedScanResult ReadCompleted(ElevatedScanFrame frame)
    {
        RequireType(frame, ElevatedScanFrameType.Completed);
        if (frame.Body.Length != 5 || frame.Body[4] > 1) throw Invalid("The helper returned an invalid completion record.");
        int skipped = BinaryPrimitives.ReadInt32LittleEndian(frame.Body);
        if (skipped < 0) throw Invalid("The helper returned a negative skipped-location count.");
        return new(skipped, frame.Body[4] == 1);
    }

    internal static Task WriteReadyAsync(Stream stream, bool backupPrivilegeEnabled, CancellationToken token)
        => WriteFrameAsync(stream, ElevatedScanFrameType.Ready, new byte[] { backupPrivilegeEnabled ? (byte)1 : (byte)0 }, token);

    internal static bool ReadReady(ElevatedScanFrame frame)
    {
        RequireType(frame, ElevatedScanFrameType.Ready);
        if (frame.Body.Length != 1 || frame.Body[0] > 1) throw Invalid("The elevated scanner returned an invalid ready record.");
        return frame.Body[0] == 1;
    }

    internal static Task WriteCancelAsync(Stream stream, CancellationToken token)
        => WriteFrameAsync(stream, ElevatedScanFrameType.Cancel, ReadOnlyMemory<byte>.Empty, token);

    internal static void ReadCancel(ElevatedScanFrame frame)
    {
        RequireType(frame, ElevatedScanFrameType.Cancel);
        if (frame.Body.Length != 0) throw Invalid("The cancel frame is malformed.");
    }

    internal static async Task WriteErrorAsync(Stream stream, string message, CancellationToken token)
    {
        if (message.Length > 4_096)
        {
            int length = 4_096;
            if (char.IsHighSurrogate(message[length - 1])) length--;
            message = message[..length];
        }
        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Utf8, true))
            WriteBoundedString(writer, message, MaximumErrorBytes);
        await WriteFrameAsync(stream, ElevatedScanFrameType.Error, body.ToArray(), token).ConfigureAwait(false);
    }

    internal static string ReadError(ElevatedScanFrame frame)
    {
        RequireType(frame, ElevatedScanFrameType.Error);
        using var body = new MemoryStream(frame.Body, false);
        using var reader = new BinaryReader(body, Utf8, true);
        string message = ReadBoundedString(reader, MaximumErrorBytes);
        EnsureConsumed(body);
        return message;
    }

    internal static async Task<ElevatedScanFrame> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, header, token).ConfigureAwait(false);
        int frameLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (frameLength < 1 || frameLength > MaximumFrameLength) throw Invalid("The elevated scan frame length is invalid.");

        byte[] typeByte = new byte[1];
        await ReadExactlyAsync(stream, typeByte, token).ConfigureAwait(false);
        var type = (ElevatedScanFrameType)typeByte[0];
        byte[] body = new byte[frameLength - 1];
        if (body.Length > 0) await ReadExactlyAsync(stream, body, token).ConfigureAwait(false);
        return new(type, body);
    }

    internal static void RunSelfTest()
    {
        byte[] nonce = Enumerable.Range(0, NonceLength).Select(value => (byte)value).ToArray();
        using (var stream = new MemoryStream())
        {
            WriteHelloAsync(stream, nonce, CancellationToken.None).GetAwaiter().GetResult();
            stream.Position = 0;
            byte[] decoded = ReadHello(ReadFrameAsync(stream, CancellationToken.None).GetAwaiter().GetResult());
            if (!decoded.SequenceEqual(nonce)) throw Invalid("Elevated scan hello round-trip failed.");
        }

        string path = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\", "SpaceLens-protocol-test.bin");
        var source = new FileItem(path, 4_096, 8_192, DateTime.UnixEpoch, "Windows & system", DateTime.UnixEpoch.AddSeconds(1), false, 123, 456, FileAttributes.System, FileSafety.Protected);
        byte[] encodedBatch;
        using (var stream = new MemoryStream())
        {
            WriteBatchesAsync(stream, [source], 7, CancellationToken.None).GetAwaiter().GetResult();
            encodedBatch = stream.ToArray();
            stream.Position = 0;
            (List<FileItem> files, int skipped) = ReadBatch(ReadFrameAsync(stream, CancellationToken.None).GetAwaiter().GetResult());
            if (skipped != 7 || files.Count != 1 || files[0] != source) throw Invalid("Elevated scan batch round-trip failed.");
        }

        encodedBatch[^1] = (byte)FileSafety.Unknown;
        bool invalidSafetyRejected = false;
        try
        {
            using var tampered = new MemoryStream(encodedBatch, false);
            _ = ReadBatch(ReadFrameAsync(tampered, CancellationToken.None).GetAwaiter().GetResult());
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("safety", StringComparison.OrdinalIgnoreCase)) { invalidSafetyRejected = true; }
        if (!invalidSafetyRejected) throw Invalid("Elevated scan accepted an invalid safety value.");

        using (var oversized = new MemoryStream())
        {
            byte[] length = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(length, MaximumFrameLength + 1);
            oversized.Write(length);
            oversized.Position = 0;
            bool oversizedRejected = false;
            try
            {
                _ = ReadFrameAsync(oversized, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (InvalidDataException ex) when (ex.Message.Contains("length", StringComparison.OrdinalIgnoreCase)) { oversizedRejected = true; }
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
        while (stream.Position < stream.Length)
        {
            ElevatedScanFrame frame = ReadFrameAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
            (List<FileItem> files, int skipped) = ReadBatch(frame);
            if (skipped != 17 || files.Count == 0) throw Invalid("Elevated protocol performance fixture returned invalid metadata.");
            decoded = checked(decoded + files.Count);
        }
        timer.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        if (decoded != count) throw Invalid("Elevated protocol performance fixture lost file records.");
        if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw Invalid($"Elevated protocol performance regression ({timer.ElapsedMilliseconds} ms). ");
        if (allocated > 192L * 1024 * 1024) throw Invalid($"Elevated protocol allocation regression ({allocated / (1024 * 1024):N0} MB). ");
    }

    private static int GetSerializedRecordLength(FileItem item)
    {
        ValidateFileItem(item);
        int pathBytes = Utf8.GetByteCount(item.Path);
        if (pathBytes > MaximumPathBytes) throw Invalid("A scanner file path exceeds its safe protocol limit.");
        // path length + path + disk/logical/modified + category code + created
        // + estimate flag + volume + file id + attributes + safety.
        int length = checked(55 + pathBytes);
        if (length <= 0 || length > MaximumRecordLength) throw Invalid("A scanner file record is too large.");
        return length;
    }

    private static void SerializeFileItem(BinaryWriter writer, FileItem item)
    {
        ValidateFileItem(item);
        WriteBoundedString(writer, item.Path, MaximumPathBytes);
        writer.Write(item.DiskBytes);
        writer.Write(item.LogicalBytes);
        writer.Write(item.Modified.ToBinary());
        writer.Write(AnalyzerForm.CategoryCode(item.Category));
        writer.Write(item.Created.ToBinary());
        writer.Write(item.AllocationEstimated);
        writer.Write(item.VolumeSerial);
        writer.Write(item.FileIndex);
        writer.Write((int)item.Attributes);
        writer.Write((byte)AnalyzerForm.EffectiveSafety(item));
    }

    private static void ValidateFileItem(FileItem item)
    {
        if (item.DiskBytes < 0 || item.LogicalBytes < 0 || item.Path.Length > 32_767 || !Path.IsPathFullyQualified(item.Path) || item.Category.Length > 256 || (item.Attributes & FileAttributes.Directory) != 0)
            throw Invalid("The scanner produced an invalid file record.");
        _ = AnalyzerForm.CategoryCode(item.Category);
    }

    private static FileItem DeserializeFileItem(MemoryStream body)
    {
        using var reader = new BinaryReader(body, Utf8, true);
        string path = ReadBoundedString(reader, MaximumPathBytes);
        long diskBytes = reader.ReadInt64();
        long logicalBytes = reader.ReadInt64();
        DateTime modified = DateTime.FromBinary(reader.ReadInt64());
        string category = AnalyzerForm.CategoryFromCode(reader.ReadByte());
        DateTime created = DateTime.FromBinary(reader.ReadInt64());
        byte estimatedValue = reader.ReadByte();
        if (estimatedValue > 1) throw Invalid("The helper returned an invalid allocation-estimate flag.");
        bool estimated = estimatedValue == 1;
        uint volumeSerial = reader.ReadUInt32();
        ulong fileIndex = reader.ReadUInt64();
        FileAttributes attributes = (FileAttributes)reader.ReadInt32();
        FileSafety safety = (FileSafety)reader.ReadByte();
        EnsureConsumed(body);

        if (path.Length == 0 || path.Length > 32_767 || !Path.IsPathFullyQualified(path) || category.Length > 256 || diskBytes < 0 || logicalBytes < 0 || (attributes & FileAttributes.Directory) != 0)
            throw Invalid("The helper returned an invalid file record.");
        if (volumeSerial != 0 && fileIndex == 0) throw Invalid("The helper returned an incomplete file identity.");
        if (safety is not (FileSafety.Normal or FileSafety.Review or FileSafety.Protected)) throw Invalid("The helper returned an invalid file safety value.");
        return new(Path.GetFullPath(path), diskBytes, logicalBytes, modified, category, created, estimated, volumeSerial, fileIndex, attributes, safety);
    }

    private static async Task WriteFrameAsync(
        Stream stream,
        ElevatedScanFrameType type,
        ReadOnlyMemory<byte> body,
        CancellationToken token)
    {
        int frameLength = checked(1 + body.Length);
        if (frameLength > MaximumFrameLength) throw Invalid("The elevated scan frame exceeds the protocol limit.");

        byte[] header = new byte[sizeof(int) + 1];
        BinaryPrimitives.WriteInt32LittleEndian(header, frameLength);
        header[sizeof(int)] = (byte)type;
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        if (!body.IsEmpty) await stream.WriteAsync(body, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
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

    private static void WriteBoundedString(BinaryWriter writer, string value, int maximumBytes)
    {
        int length = Utf8.GetByteCount(value);
        if (length > maximumBytes) throw Invalid("A protocol string exceeds its allowed length.");
        writer.Write(length);
        if (length <= 1024)
        {
            Span<byte> bytes = stackalloc byte[length];
            Utf8.GetBytes(value.AsSpan(), bytes);
            writer.Write(bytes);
            return;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            int written = Utf8.GetBytes(value.AsSpan(), rented.AsSpan(0, length));
            writer.Write(rented, 0, written);
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    private static string ReadBoundedString(BinaryReader reader, int maximumBytes)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > maximumBytes || length > reader.BaseStream.Length - reader.BaseStream.Position)
            throw Invalid("A protocol string has an invalid length.");
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException("A protocol string ended unexpectedly.");
        return Utf8.GetString(bytes);
    }

    private static void RequireType(ElevatedScanFrame frame, ElevatedScanFrameType expected)
    {
        if (frame.Type != expected) throw Invalid($"Expected {expected} but received {frame.Type}.");
    }

    private static void EnsureConsumed(MemoryStream stream)
    {
        if (stream.Position != stream.Length) throw Invalid("The elevated scan frame has trailing data.");
    }

    private static InvalidDataException Invalid(string message) => new(message);
}
