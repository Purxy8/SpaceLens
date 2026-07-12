using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace DesktopOrganizer;

internal readonly record struct ElevatedScanResult(int Skipped, bool BackupPrivilegeEnabled, NtfsJournalCheckpoint JournalCheckpoint);
internal readonly record struct ElevatedRefreshProgress(int ReceivedFiles, bool FullScan, int Skipped, string Message = "");
internal sealed record ElevatedRefreshResult(
    bool UsedFullScan,
    List<FileItem> Files,
    List<NativeFileId> Removed,
    int JournalRecords,
    int Skipped,
    bool BackupPrivilegeEnabled,
    NtfsJournalCheckpoint JournalCheckpoint,
    string FallbackReason);

internal readonly record struct ValidatedScanRoot(string Path, NativeFileId Identity);

/// <summary>
/// Runs the existing buffered scanner in a short-lived elevated copy of the
/// same executable. The ordinary UI remains unelevated; only SeBackupPrivilege
/// is requested inside the helper and all results cross a bounded, authenticated
/// named-pipe protocol before being accepted by the UI process.
/// </summary>
internal static class ElevatedScanRunner
{
    private const string HelperSwitch = "--elevated-scan-helper";
    private const string PipePrefix = "SpaceLens.Scan.";
    private const int ErrorCancelled = 1223;
    private const int PipeBufferSize = 64 * 1024;
    private const uint FileFlagBackupSemantics = 0x02000000;
    internal static readonly PipeOptions ServerPipeOptions = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
    internal static readonly PipeOptions HelperClientPipeOptions = PipeOptions.Asynchronous;
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan GracefulExitTimeout = TimeSpan.FromSeconds(2);

    internal static bool IsHelperCommand(string[] args)
    {
        // Keep recognizing the switch while the capability is disabled so an
        // externally supplied helper command reaches RunHelper's fail-closed
        // gate instead of falling through to the ordinary UI.
        return args.Length > 0 && string.Equals(args[0], HelperSwitch, StringComparison.Ordinal);
    }

    internal static bool IsAdministrator
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    internal static bool SupportsRoot(string root, out string reason)
    {
        if (!SecurityPolicy.ElevatedFullAccessAvailable)
        {
            reason = SecurityPolicy.ElevatedFullAccessUnavailableReason;
            return false;
        }
        try { _ = ResolveAndValidateRoot(root); reason = string.Empty; return true; }
        catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            reason = ex.Message;
            return false;
        }
    }

    internal static async Task<ElevatedScanResult> ScanAsync(
        string root,
        IProgress<(List<FileItem> Batch, int Skipped)> progress,
        CancellationToken token,
        IProgress<bool>? started = null)
    {
        EnsureFullAccessEnabled();
        ArgumentNullException.ThrowIfNull(progress);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Elevated scanning is available only on Windows.");
        if (IsDotNetHost(Environment.ProcessPath)) throw new InvalidOperationException("Full access scan is available only from the packaged SpaceLens application.");
        token.ThrowIfCancellationRequested();

        ValidatedScanRoot validatedRoot = ResolveAndValidateRoot(root);
        string fullRoot = validatedRoot.Path;
        var rootContext = new CanonicalRootContext(fullRoot);
        string pipeName = PipePrefix + Guid.NewGuid().ToString("N");
        byte[] nonce = RandomNumberGenerator.GetBytes(ElevatedScanProtocol.NonceLength);
        string nonceText = Convert.ToHexString(nonce);
        int parentPid = Environment.ProcessId;

        using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            ServerPipeOptions,
            PipeBufferSize,
            PipeBufferSize);

        Process? child = null;
        Task cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, token);
        try
        {
            child = await StartHelperAsync(pipeName, nonceText, parentPid, token).ConfigureAwait(false);
            await WaitForVerifiedClientAsync(server, child, cancellationTask, token).ConfigureAwait(false);

            using (ElevatedScanFrame hello = await ReadHelperFrameAsync(server, child, token).ConfigureAwait(false))
            {
                try
                {
                    byte[] receivedNonce = ElevatedScanProtocol.ReadHello(hello);
                    try
                    {
                        if (!CryptographicOperations.FixedTimeEquals(nonce, receivedNonce))
                            throw new InvalidDataException("The elevated scanner failed authentication.");
                    }
                    finally { CryptographicOperations.ZeroMemory(receivedNonce); }
                }
                finally { hello.ClearBody(); }
            }

            await ElevatedScanProtocol.WriteStartAsync(server, fullRoot, validatedRoot.Identity, token).ConfigureAwait(false);

            int lastSkipped = 0;
            bool readyReceived = false;
            var decodeBudget = new ElevatedScanDecodeBudget();
            while (true)
            {
                Task<ElevatedScanFrame> readTask = ElevatedScanProtocol.ReadFrameAsync(server, CancellationToken.None);
                Task completed = await Task.WhenAny(readTask, cancellationTask).ConfigureAwait(false);
                if (completed == cancellationTask)
                {
                    await TrySendCancelAsync(server).ConfigureAwait(false);
                    await Task.WhenAny(readTask, child.WaitForExitAsync(), Task.Delay(GracefulExitTimeout)).ConfigureAwait(false);
                    if (readTask.IsCompleted) await ObserveAndDisposeFrameAsync(readTask).ConfigureAwait(false);
                    else _ = ObserveAndDisposeFrameAsync(readTask);
                    token.ThrowIfCancellationRequested();
                    throw new OperationCanceledException(token);
                }

                ElevatedScanFrame frame;
                try { frame = await readTask.ConfigureAwait(false); }
                catch (EndOfStreamException ex) { throw await HelperDisconnectedAsync(child, ex).ConfigureAwait(false); }
                using (frame)
                {
                    switch (frame.Type)
                    {
                        case ElevatedScanFrameType.Ready:
                            if (readyReceived) throw new InvalidDataException("The elevated scanner returned more than one ready record.");
                            readyReceived = true;
                            started?.Report(ElevatedScanProtocol.ReadReady(frame));
                            break;
                        case ElevatedScanFrameType.Batch:
                            {
                                if (!readyReceived) throw new InvalidDataException("The elevated scanner returned files before it was ready.");
                                (List<FileItem> batch, int skipped) = ElevatedScanProtocol.ReadBatch(frame, decodeBudget);
                                if (skipped < lastSkipped) throw new InvalidDataException("The helper skipped-location count moved backwards.");
                                foreach (FileItem item in batch)
                                {
                                    // ReadBatch canonicalizes each untrusted path once. This
                                    // precomputed prefix check is the authenticated-boundary
                                    // containment decision and deliberately remains mandatory.
                                    if (!rootContext.ContainsCanonicalPath(item.Path))
                                        throw new InvalidDataException("The helper returned a path outside the selected scan root.");
                                }
                                lastSkipped = skipped;
                                progress.Report((batch, skipped));
                                break;
                            }
                        case ElevatedScanFrameType.Completed:
                            {
                                if (!readyReceived) throw new InvalidDataException("The elevated scanner completed before it was ready.");
                                ElevatedScanResult result = ElevatedScanProtocol.ReadCompleted(frame);
                                if (result.Skipped < lastSkipped)
                                    throw new InvalidDataException("The helper returned an inconsistent final skipped-location count.");
                                return result;
                            }
                        case ElevatedScanFrameType.Error:
                            throw new InvalidOperationException("Elevated scan failed: " + ElevatedScanProtocol.ReadError(frame));
                        default:
                            throw new InvalidDataException($"The elevated scanner returned an unexpected {frame.Type} frame.");
                    }
                }
            }
        }
        finally
        {
            try { server.Dispose(); } catch { }
            if (child is not null)
            {
                await StopChildAsync(child).ConfigureAwait(false);
                child.Dispose();
            }
            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    internal static async Task<ElevatedRefreshResult> RefreshAsync(
        string root,
        NtfsJournalCheckpoint checkpoint,
        long baselineDriveUsedBytes,
        CancellationToken token,
        IProgress<ElevatedRefreshProgress>? progress = null,
        IProgress<bool>? started = null)
    {
        EnsureFullAccessEnabled();
        if (!checkpoint.IsValid) throw new ArgumentException("The saved scan has no valid quick-refresh checkpoint.", nameof(checkpoint));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baselineDriveUsedBytes);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("NTFS quick refresh is available only on Windows.");
        if (IsDotNetHost(Environment.ProcessPath)) throw new InvalidOperationException("Quick refresh is available only from the packaged SpaceLens application.");
        token.ThrowIfCancellationRequested();

        ValidatedScanRoot validatedRoot = ResolveAndValidateRoot(root);
        string fullRoot = validatedRoot.Path;
        if (!NtfsChangeJournal.SupportsRoot(fullRoot, out string refreshReason)) throw new NotSupportedException(refreshReason);
        if (checkpoint.VolumeSerial != validatedRoot.Identity.VolumeSerial)
            throw new InvalidOperationException("The saved quick-refresh checkpoint belongs to a different root volume.");
        var rootContext = new CanonicalRootContext(fullRoot);
        string pipeName = PipePrefix + Guid.NewGuid().ToString("N");
        byte[] nonce = RandomNumberGenerator.GetBytes(ElevatedScanProtocol.NonceLength);
        string nonceText = Convert.ToHexString(nonce);
        int parentPid = Environment.ProcessId;

        using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            ServerPipeOptions,
            PipeBufferSize,
            PipeBufferSize);

        Process? child = null;
        Task cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, token);
        try
        {
            child = await StartHelperAsync(pipeName, nonceText, parentPid, token).ConfigureAwait(false);
            await WaitForVerifiedClientAsync(server, child, cancellationTask, token).ConfigureAwait(false);
            using (ElevatedScanFrame hello = await ReadHelperFrameAsync(server, child, token).ConfigureAwait(false))
            {
                byte[] receivedNonce = ElevatedScanProtocol.ReadHello(hello);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(nonce, receivedNonce))
                        throw new InvalidDataException("The elevated scanner failed authentication.");
                }
                finally { CryptographicOperations.ZeroMemory(receivedNonce); hello.ClearBody(); }
            }

            await ElevatedScanProtocol.WriteRefreshStartAsync(server, fullRoot, validatedRoot.Identity, checkpoint, baselineDriveUsedBytes, token).ConfigureAwait(false);
            var files = new List<FileItem>();
            var removed = new List<NativeFileId>();
            var decodeBudget = new ElevatedScanDecodeBudget();
            int lastSkipped = 0;
            bool readyReceived = false, fallbackReceived = false;
            bool backupPrivilege = false;
            string fallbackReason = string.Empty;
            while (true)
            {
                Task<ElevatedScanFrame> readTask = ElevatedScanProtocol.ReadFrameAsync(server, CancellationToken.None);
                Task completed = await Task.WhenAny(readTask, cancellationTask).ConfigureAwait(false);
                if (completed == cancellationTask)
                {
                    await TrySendCancelAsync(server).ConfigureAwait(false);
                    await Task.WhenAny(readTask, child.WaitForExitAsync(), Task.Delay(GracefulExitTimeout)).ConfigureAwait(false);
                    if (readTask.IsCompleted) await ObserveAndDisposeFrameAsync(readTask).ConfigureAwait(false);
                    else _ = ObserveAndDisposeFrameAsync(readTask);
                    token.ThrowIfCancellationRequested();
                    throw new OperationCanceledException(token);
                }

                ElevatedScanFrame frame;
                try { frame = await readTask.ConfigureAwait(false); }
                catch (EndOfStreamException ex) { throw await HelperDisconnectedAsync(child, ex).ConfigureAwait(false); }
                using (frame)
                {
                    switch (frame.Type)
                    {
                        case ElevatedScanFrameType.Ready:
                            if (readyReceived) throw new InvalidDataException("The elevated scanner returned more than one ready record.");
                            readyReceived = true;
                            backupPrivilege = ElevatedScanProtocol.ReadReady(frame);
                            started?.Report(backupPrivilege);
                            break;
                        case ElevatedScanFrameType.RefreshFallback:
                            if (!readyReceived || fallbackReceived || files.Count != 0 || removed.Count != 0)
                                throw new InvalidDataException("The helper returned an out-of-order quick-refresh fallback.");
                            fallbackReceived = true;
                            fallbackReason = ElevatedScanProtocol.ReadRefreshFallback(frame);
                            progress?.Report(new(0, true, 0, fallbackReason));
                            break;
                        case ElevatedScanFrameType.Batch:
                            {
                                if (!readyReceived) throw new InvalidDataException("The elevated scanner returned files before it was ready.");
                                (List<FileItem> batch, int skipped) = ElevatedScanProtocol.ReadBatch(frame, decodeBudget);
                                if (!fallbackReceived && skipped != 0) throw new InvalidDataException("A quick-refresh delta reported skipped full-scan locations.");
                                if (fallbackReceived && skipped < lastSkipped) throw new InvalidDataException("The helper skipped-location count moved backwards.");
                                foreach (FileItem item in batch)
                                {
                                    if (!rootContext.ContainsCanonicalPath(item.Path))
                                        throw new InvalidDataException("The helper returned a path outside the selected scan root.");
                                }
                                if (files.Count > ElevatedScanProtocol.MaximumTotalFiles - batch.Count)
                                    throw new InvalidDataException("The elevated refresh exceeded the supported file-count limit.");
                                files.AddRange(batch);
                                if ((long)files.Count + removed.Count > ElevatedScanProtocol.MaximumTotalFiles)
                                    throw new InvalidDataException("The elevated refresh exceeded the combined affected-file limit.");
                                lastSkipped = skipped;
                                progress?.Report(new(files.Count, fallbackReceived, skipped));
                                break;
                            }
                        case ElevatedScanFrameType.Removed:
                            {
                                if (!readyReceived || fallbackReceived) throw new InvalidDataException("The helper returned removed IDs during a full-scan fallback.");
                                List<NativeFileId> batch = ElevatedScanProtocol.ReadRemoved(frame, decodeBudget);
                                foreach (NativeFileId id in batch)
                                    if (id.VolumeSerial != checkpoint.VolumeSerial) throw new InvalidDataException("A removed-file identity belongs to a different volume.");
                                if (removed.Count > ElevatedScanProtocol.MaximumTotalFiles - batch.Count)
                                    throw new InvalidDataException("The elevated refresh exceeded the supported removed-file limit.");
                                removed.AddRange(batch);
                                if ((long)files.Count + removed.Count > ElevatedScanProtocol.MaximumTotalFiles)
                                    throw new InvalidDataException("The elevated refresh exceeded the combined affected-file limit.");
                                break;
                            }
                        case ElevatedScanFrameType.RefreshCompleted:
                            {
                                if (!readyReceived || fallbackReceived) throw new InvalidDataException("The helper returned an unexpected quick-refresh completion.");
                                ElevatedRefreshCompletion result = ElevatedScanProtocol.ReadRefreshCompleted(frame);
                                long affectedCount = checked((long)files.Count + removed.Count);
                                if (!backupPrivilege
                                    || affectedCount > ElevatedScanProtocol.MaximumTotalFiles
                                    || result.JournalRecords < affectedCount
                                    || result.Upserts != files.Count
                                    || result.Removed != removed.Count
                                    || result.Checkpoint.VolumeSerial != checkpoint.VolumeSerial
                                    || result.Checkpoint.JournalId != checkpoint.JournalId
                                    || result.Checkpoint.NextUsn < checkpoint.NextUsn)
                                    throw new InvalidDataException("The helper returned inconsistent quick-refresh totals.");
                                return new(false, files, removed, result.JournalRecords, 0, backupPrivilege, result.Checkpoint, string.Empty);
                            }
                        case ElevatedScanFrameType.Completed:
                            {
                                if (!readyReceived || !fallbackReceived) throw new InvalidDataException("The helper returned an unexpected full-scan completion.");
                                ElevatedScanResult result = ElevatedScanProtocol.ReadCompleted(frame);
                                if (result.Skipped < lastSkipped) throw new InvalidDataException("The helper returned an inconsistent final skipped-location count.");
                                return new(true, files, [], 0, result.Skipped, result.BackupPrivilegeEnabled, result.JournalCheckpoint, fallbackReason);
                            }
                        case ElevatedScanFrameType.Error:
                            throw new InvalidOperationException("Elevated refresh failed: " + ElevatedScanProtocol.ReadError(frame));
                        default:
                            throw new InvalidDataException($"The elevated scanner returned an unexpected {frame.Type} frame.");
                    }
                }
            }
        }
        finally
        {
            try { server.Dispose(); } catch { }
            if (child is not null)
            {
                await StopChildAsync(child).ConfigureAwait(false);
                child.Dispose();
            }
            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    internal static int RunHelper(string[] args)
    {
        if (!SecurityPolicy.ElevatedFullAccessAvailable) return 6;
        if (!TryParseHelperArguments(args, out string pipeName, out byte[] nonce, out int parentPid)) return 2;

        NamedPipeClientStream? pipe = null;
        CancellationTokenSource? scanCancellation = null;
        Task? parentWatcher = null;
        Task? cancelWatcher = null;
        bool handshakeSent = false;
        try
        {
            if (!ParentUsesSameExecutable(parentPid))
                throw new InvalidDataException("The elevated scan request did not originate from this SpaceLens executable.");
            scanCancellation = new CancellationTokenSource();
            parentWatcher = WatchParentAsync(parentPid, scanCancellation);
            pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                HelperClientPipeOptions);
            pipe.ConnectAsync((int)ConnectionTimeout.TotalMilliseconds, scanCancellation.Token).GetAwaiter().GetResult();

            // Send the authenticated hello as soon as the OS pipe connection is
            // established. If a later startup check fails, the parent can now
            // receive the real error instead of an unexplained end-of-stream.
            ElevatedScanProtocol.WriteHelloAsync(pipe, nonce, CancellationToken.None).GetAwaiter().GetResult();
            handshakeSent = true;

            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid) || serverPid != unchecked((uint)parentPid))
                throw new InvalidDataException("The elevated scan pipe is not owned by the requesting process.");

            if (!IsAdministrator) throw new UnauthorizedAccessException("The scan helper did not receive administrator rights.");

            using WindowsBackupPrivilege backupPrivilege = WindowsBackupPrivilege.TryEnable();
            string requestedRoot;
            NativeFileId requestedRootIdentity;
            bool refreshRequested;
            NtfsJournalCheckpoint refreshCheckpoint = default;
            long baselineDriveUsedBytes = 0;
            using (ElevatedScanFrame startFrame = ElevatedScanProtocol.ReadFrameAsync(pipe, scanCancellation.Token).GetAwaiter().GetResult())
            {
                refreshRequested = startFrame.Type == ElevatedScanFrameType.RefreshStart;
                if (refreshRequested)
                {
                    (requestedRoot, requestedRootIdentity, refreshCheckpoint, baselineDriveUsedBytes) = ElevatedScanProtocol.ReadRefreshStart(startFrame);
                }
                else (requestedRoot, requestedRootIdentity) = ElevatedScanProtocol.ReadStart(startFrame);
            }
            ValidatedScanRoot validatedRoot = ResolveAndValidateBoundRoot(requestedRoot, requestedRootIdentity);
            string root = validatedRoot.Path;
            if (refreshRequested && refreshCheckpoint.VolumeSerial != validatedRoot.Identity.VolumeSerial)
                throw new InvalidDataException("The quick-refresh checkpoint belongs to a different root volume.");
            var rootContext = new CanonicalRootContext(root);
            cancelWatcher = WatchForCancelAsync(pipe, scanCancellation);

            ElevatedScanProtocol.WriteReadyAsync(pipe, backupPrivilege.Enabled, scanCancellation.Token).GetAwaiter().GetResult();

            if (refreshRequested)
            {
                NtfsJournalDelta delta = backupPrivilege.Enabled
                    ? NtfsChangeJournal.ReadDelta(root, refreshCheckpoint, scanCancellation.Token)
                    : NtfsJournalDelta.Fallback("Windows backup privilege is unavailable, so a complete scan is required.");
                if (!delta.RequiresFullScan)
                {
                    long currentDriveUsedBytes = NtfsChangeJournal.TryGetWholeDriveUsed(root);
                    string? allocationFallback = RefreshAllocationFallbackReason(baselineDriveUsedBytes, currentDriveUsedBytes, delta.JournalRecords);
                    if (allocationFallback is not null) delta = NtfsJournalDelta.Fallback(allocationFallback);
                }
                if (!delta.RequiresFullScan)
                {
                    foreach (FileItem item in delta.Upserts)
                        if (!rootContext.ContainsCanonicalPath(item.Path)) throw new InvalidDataException("The journal returned a path outside the selected root.");
                    ElevatedScanProtocol.WriteBatchesAsync(pipe, delta.Upserts, 0, scanCancellation.Token).GetAwaiter().GetResult();
                    ElevatedScanProtocol.WriteRemovedAsync(pipe, delta.Removed, scanCancellation.Token).GetAwaiter().GetResult();
                    ElevatedScanProtocol.WriteRefreshCompletedAsync(pipe, new(delta.Checkpoint, delta.JournalRecords, delta.Upserts.Count, delta.Removed.Count), scanCancellation.Token).GetAwaiter().GetResult();
                    return 0;
                }
                ElevatedScanProtocol.WriteRefreshFallbackAsync(pipe, delta.Message, scanCancellation.Token).GetAwaiter().GetResult();
            }

            NtfsJournalCheckpoint scanCheckpoint = default;
            _ = NtfsChangeJournal.TryCapture(root, out scanCheckpoint, out _);
            var reporter = new ImmediateProgress<(List<FileItem> Batch, int Skipped)>(update =>
            {
                scanCancellation.Token.ThrowIfCancellationRequested();
                foreach (FileItem item in update.Batch)
                {
                    // FastFileScanner emits paths from its already-canonical root.
                    // Keep the helper-side defense without re-normalizing root and
                    // every path; the parent independently canonicalizes and checks.
                    if (!rootContext.ContainsCanonicalPath(item.Path))
                        throw new InvalidDataException("The scanner attempted to return a path outside the selected root.");
                }
                ElevatedScanProtocol.WriteBatchesAsync(pipe, update.Batch, update.Skipped, scanCancellation.Token).GetAwaiter().GetResult();
            });

            int skipped = FastFileScanner.Scan(root, reporter, scanCancellation.Token, strictReparseDirectories: true);
            scanCancellation.Token.ThrowIfCancellationRequested();
            ElevatedScanProtocol.WriteCompletedAsync(pipe, skipped, backupPrivilege.Enabled, scanCancellation.Token, scanCheckpoint).GetAwaiter().GetResult();
            return 0;
        }
        catch (OperationCanceledException)
        {
            if (pipe is not null && pipe.IsConnected && handshakeSent)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    ElevatedScanProtocol.WriteErrorAsync(pipe, "The elevated scan was canceled or its parent connection was lost.", timeout.Token).GetAwaiter().GetResult();
                }
                catch { }
            }
            return 5;
        }
        catch (Exception ex)
        {
            if (pipe is not null && pipe.IsConnected && handshakeSent)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    ElevatedScanProtocol.WriteErrorAsync(pipe, SafeErrorMessage(ex), timeout.Token).GetAwaiter().GetResult();
                }
                catch { }
            }
            return 4;
        }
        finally
        {
            scanCancellation?.Cancel();
            ObserveWatcher(parentWatcher);
            ObserveWatcher(cancelWatcher);
            scanCancellation?.Dispose();
            pipe?.Dispose();
            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    private static async Task<Process> StartHelperAsync(
        string pipeName,
        string nonce,
        int parentPid,
        CancellationToken token)
    {
        EnsureFullAccessEnabled();
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                ProcessStartInfo startInfo = CreateHelperStartInfo(pipeName, nonce, parentPid);
                return Process.Start(startInfo) ?? throw new InvalidOperationException("Windows did not start the elevated scan helper.");
            }, token).ConfigureAwait(false);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            throw new OperationCanceledException("Administrator permission was canceled.", ex);
        }
    }

    private static ProcessStartInfo CreateHelperStartInfo(string pipeName, string nonce, int parentPid)
    {
        EnsureFullAccessEnabled();
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("The SpaceLens executable path is unavailable.");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        string? entryAssembly = Assembly.GetEntryAssembly()?.Location;
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(entryAssembly)
            && string.Equals(Path.GetExtension(entryAssembly), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(entryAssembly);
        }

        startInfo.ArgumentList.Add(HelperSwitch);
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add(nonce);
        startInfo.ArgumentList.Add(parentPid.ToString(CultureInfo.InvariantCulture));
        return startInfo;
    }

    private static async Task WaitForVerifiedClientAsync(
        NamedPipeServerStream server,
        Process child,
        Task cancellationTask,
        CancellationToken token)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            Task connectionTask = server.WaitForConnectionAsync(CancellationToken.None);
            Task exitTask = child.WaitForExitAsync(CancellationToken.None);
            Task timeoutTask = Task.Delay(ConnectionTimeout);
            Task completed = await Task.WhenAny(connectionTask, exitTask, timeoutTask, cancellationTask).ConfigureAwait(false);
            if (completed == cancellationTask)
            {
                token.ThrowIfCancellationRequested();
                throw new OperationCanceledException(token);
            }
            if (completed == exitTask)
                throw new InvalidOperationException($"The elevated scan helper exited before connecting (code {child.ExitCode}).");
            if (completed == timeoutTask)
                throw new TimeoutException("The elevated scan helper did not connect in time.");

            await connectionTask.ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out uint clientPid))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not verify the elevated scan helper process.");
            if (clientPid == unchecked((uint)child.Id)) return;

            server.Disconnect();
        }
        throw new InvalidDataException("An unexpected process repeatedly connected to the elevated scan pipe.");
    }

    private static async Task TrySendCancelAsync(NamedPipeServerStream server)
    {
        try
        {
            if (!server.IsConnected) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await ElevatedScanProtocol.WriteCancelAsync(server, timeout.Token).ConfigureAwait(false);
        }
        catch { }
    }

    private static async Task StopChildAsync(Process child)
    {
        try
        {
            if (child.HasExited) return;
            Task exitTask = child.WaitForExitAsync();
            if (await Task.WhenAny(exitTask, Task.Delay(GracefulExitTimeout)).ConfigureAwait(false) == exitTask)
            {
                await exitTask.ConfigureAwait(false);
                return;
            }
        }
        catch { }

        try
        {
            if (!child.HasExited) child.Kill(true);
        }
        catch { }
    }

    private static async Task WatchParentAsync(int parentPid, CancellationTokenSource cancellation)
    {
        try
        {
            using Process parent = Process.GetProcessById(parentPid);
            await parent.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
            if (!cancellation.IsCancellationRequested) cancellation.Cancel();
        }
        catch (OperationCanceledException) { }
        catch
        {
            // Failure to create a process watcher is not proof that the parent
            // exited. The authenticated pipe watcher remains the liveness
            // fallback; canceling here caused valid scans to end silently.
        }
    }

    private static async Task<ElevatedScanFrame> ReadHelperFrameAsync(
        NamedPipeServerStream server,
        Process child,
        CancellationToken token)
    {
        try
        {
            return await ElevatedScanProtocol.ReadFrameAsync(server, token).ConfigureAwait(false);
        }
        catch (EndOfStreamException ex)
        {
            throw await HelperDisconnectedAsync(child, ex).ConfigureAwait(false);
        }
    }

    private static async Task<Exception> HelperDisconnectedAsync(Process child, EndOfStreamException inner)
    {
        try
        {
            if (!child.HasExited)
                await Task.WhenAny(child.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
            if (child.HasExited)
                return new InvalidOperationException($"The elevated scan helper closed unexpectedly (exit code {child.ExitCode}).", inner);
        }
        catch { }
        return new InvalidOperationException("The elevated scan helper closed unexpectedly before reporting a result.", inner);
    }

    private static async Task ObserveAndDisposeFrameAsync(Task<ElevatedScanFrame> frameTask)
    {
        try
        {
            using ElevatedScanFrame frame = await frameTask.ConfigureAwait(false);
        }
        catch
        {
            // This is used only after cancellation has already won. Observing the
            // read prevents an unobserved exception; a successfully completed
            // pooled frame is disposed immediately.
        }
    }

    private static async Task WatchForCancelAsync(Stream pipe, CancellationTokenSource cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                using ElevatedScanFrame frame = await ElevatedScanProtocol.ReadFrameAsync(pipe, cancellation.Token).ConfigureAwait(false);
                ElevatedScanProtocol.ReadCancel(frame);
                cancellation.Cancel();
                return;
            }
        }
        catch (OperationCanceledException) { }
        catch
        {
            try { cancellation.Cancel(); } catch { }
        }
    }

    private static void ObserveWatcher(Task? watcher)
    {
        if (watcher is null) return;
        try { watcher.Wait(TimeSpan.FromSeconds(1)); } catch { }
    }

    private static bool TryParseHelperArguments(string[] args, out string pipeName, out byte[] nonce, out int parentPid)
    {
        pipeName = string.Empty;
        nonce = [];
        parentPid = 0;
        if (args.Length != 4 || !string.Equals(args[0], HelperSwitch, StringComparison.Ordinal)) return false;

        string candidatePipe = args[1];
        string suffix = candidatePipe.StartsWith(PipePrefix, StringComparison.Ordinal) ? candidatePipe[PipePrefix.Length..] : string.Empty;
        if (suffix.Length != 32 || suffix.Any(character => !Uri.IsHexDigit(character))) return false;
        if (args[2].Length != ElevatedScanProtocol.NonceLength * 2 || args[2].Any(character => !Uri.IsHexDigit(character))) return false;
        if (!int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out parentPid) || parentPid <= 0) return false;

        try { nonce = Convert.FromHexString(args[2]); }
        catch (FormatException) { return false; }
        if (nonce.Length != ElevatedScanProtocol.NonceLength) return false;
        pipeName = candidatePipe;
        return true;
    }

    private static ValidatedScanRoot ResolveAndValidateRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || root.IndexOf('\0') >= 0 || root.Length > 32_767)
            throw new ArgumentException("The scan root is invalid.", nameof(root));

        string fullRoot = Path.GetFullPath(root);
        if (!Path.IsPathFullyQualified(fullRoot)
            || fullRoot.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase)
            || fullRoot.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
            || fullRoot.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase)
            || fullRoot.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase)
            || fullRoot.Length > 32_767)
            throw new ArgumentException("Full access scan supports local fixed-drive paths only.", nameof(root));

        using SafeFileHandle handle = CreateFileW(
            NativePath.For(fullRoot),
            0,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            throw new IOException("The selected elevated scan root could not be opened safely: " + new Win32Exception(error).Message);
        }
        if (!NativeResolvedPath.TryResolveHandle(handle, out string resolvedRoot, out string resolutionError))
            throw new IOException("The selected elevated scan root could not be resolved safely: " + resolutionError);
        if (!NativeFileIdentity.TryGet(handle, out NativeFileInformation identity)
            || (identity.Attributes & FileAttributes.Directory) == 0
            || identity.Id.VolumeSerial == 0
            || identity.Id.FileIndex == 0)
            throw new IOException("Windows did not expose a stable directory identity for the selected elevated scan root.");

        string? driveRoot = Path.GetPathRoot(resolvedRoot);
        if (string.IsNullOrWhiteSpace(driveRoot) || new DriveInfo(driveRoot).DriveType != DriveType.Fixed)
            throw new ArgumentException("Full access scan supports local fixed drives only.", nameof(root));
        return new(resolvedRoot, identity.Id);
    }

    private static ValidatedScanRoot ResolveAndValidateBoundRoot(string root, NativeFileId expectedIdentity)
    {
        if (expectedIdentity.VolumeSerial == 0 || expectedIdentity.FileIndex == 0)
            throw new InvalidDataException("The elevated scan request has an incomplete root identity.");
        ValidatedScanRoot liveRoot = ResolveAndValidateRoot(root);
        if (liveRoot.Identity != expectedIdentity)
            throw new InvalidDataException("The selected scan root changed filesystem identity while administrator permission was requested.");
        return liveRoot;
    }

    private static bool ParentUsesSameExecutable(int parentPid)
    {
        try
        {
            string? currentExecutable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExecutable)) return false;
            if (IsDotNetHost(currentExecutable)) return false;
            string currentPath = Path.GetFullPath(currentExecutable);
            using Process parent = Process.GetProcessById(parentPid);
            string? parentExecutable = parent.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(parentExecutable)) return false;
            string parentPath = Path.GetFullPath(parentExecutable);
            if (!string.Equals(currentPath, parentPath, StringComparison.OrdinalIgnoreCase)) return false;
            if (!NativeFileIdentity.TryGet(currentPath, false, out NativeFileInformation currentIdentity)
                || !NativeFileIdentity.TryGet(parentPath, false, out NativeFileInformation parentIdentity)
                || currentIdentity.Id.VolumeSerial == 0
                || currentIdentity.Id.FileIndex == 0
                || parentIdentity.Id.VolumeSerial == 0
                || parentIdentity.Id.FileIndex == 0) return false;
            return currentIdentity.Id == parentIdentity.Id;
        }
        catch { return false; }
    }

    private static bool IsDotNetHost(string? executable)
        => !string.IsNullOrWhiteSpace(executable)
            && string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase);

    private static void EnsureFullAccessEnabled()
    {
        if (!SecurityPolicy.ElevatedFullAccessAvailable)
            throw new NotSupportedException(SecurityPolicy.ElevatedFullAccessUnavailableReason);
    }

    private static string SafeErrorMessage(Exception exception)
    {
        string message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return string.IsNullOrWhiteSpace(message) ? exception.GetType().Name : message;
    }

    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    internal static string? RefreshAllocationFallbackReason(long baselineDriveUsedBytes, long currentDriveUsedBytes, int journalRecords)
    {
        if (baselineDriveUsedBytes <= 0) return "The saved scan has no trustworthy drive-allocation baseline.";
        if (currentDriveUsedBytes <= 0) return "Current drive allocation could not be read safely.";
        if (journalRecords == 0 && currentDriveUsedBytes != baselineDriveUsedBytes)
            return "Drive allocation changed without a complete closed-file journal record.";
        return null;
    }

    internal static void RunSecuritySelfTest()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!SecurityPolicy.ElevatedFullAccessAvailable)
        {
            bool launchGateRejected = false;
            try { EnsureFullAccessEnabled(); }
            catch (NotSupportedException ex) when (ex.Message == SecurityPolicy.ElevatedFullAccessUnavailableReason) { launchGateRejected = true; }
            if (!launchGateRejected || RunHelper([]) != 6)
                throw new InvalidDataException("The disabled elevated helper did not fail closed.");
        }

        string driveRoot = Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\";
        string caseRoot = Path.Combine(driveRoot, "SpaceLens-CaseRoot");
        var caseContext = new CanonicalRootContext(caseRoot);
        if (!caseContext.ContainsCanonicalPath(Path.Combine(caseRoot, "child.bin"))
            || caseContext.ContainsCanonicalPath(Path.Combine(driveRoot, "spacelens-caseroot", "child.bin")))
            throw new InvalidDataException("Elevated root containment did not preserve case-sensitive boundaries.");

        string suffix = Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), "SpaceLens-root-binding-" + suffix);
        string replacement = Path.Combine(Path.GetTempPath(), "SpaceLens-root-binding-replacement-" + suffix);
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(replacement);
            ValidatedScanRoot original = ResolveAndValidateRoot(root);
            ValidatedScanRoot replacementRoot = ResolveAndValidateRoot(replacement);
            if (original.Identity == replacementRoot.Identity)
                throw new InvalidDataException("The root-binding fixture did not produce distinct directory identities.");
            if (ResolveAndValidateBoundRoot(original.Path, original.Identity) != original)
                throw new InvalidDataException("The root-binding self-test rejected an unchanged directory identity.");

            bool forgedRejected = false;
            try { _ = ResolveAndValidateBoundRoot(replacementRoot.Path, original.Identity); }
            catch (InvalidDataException ex) when (ex.Message.Contains("changed", StringComparison.OrdinalIgnoreCase)) { forgedRejected = true; }
            if (!forgedRejected) throw new InvalidDataException("The root-binding self-test accepted a substituted directory identity.");

            bool missingRejected = false;
            try { _ = ResolveAndValidateBoundRoot(original.Path, default); }
            catch (InvalidDataException ex) when (ex.Message.Contains("identity", StringComparison.OrdinalIgnoreCase)) { missingRejected = true; }
            if (!missingRejected) throw new InvalidDataException("The root-binding self-test accepted an incomplete directory identity.");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            try { if (Directory.Exists(replacement)) Directory.Delete(replacement, true); } catch { }
        }
    }

    /// <summary>
    /// A scan root is resolved through a Windows handle before this context is
    /// constructed. Received paths are canonicalized once by ReadBatch; scanner
    /// output is constructed beneath the same canonical root. Prefix comparison
    /// can therefore preserve strict containment without repeated GetFullPath
    /// calls and per-file root-prefix allocations.
    /// </summary>
    private sealed class CanonicalRootContext
    {
        private readonly string pathPrefix;

        internal CanonicalRootContext(string canonicalRoot)
        {
            string rootWithoutSeparator = canonicalRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            pathPrefix = rootWithoutSeparator + Path.DirectorySeparatorChar;
        }

        internal bool ContainsCanonicalPath(string path)
            => path.Length > pathPrefix.Length
                && Path.IsPathFullyQualified(path)
                && path.StartsWith(pathPrefix, StringComparison.Ordinal);
    }

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
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
