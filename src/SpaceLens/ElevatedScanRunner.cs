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

internal readonly record struct ElevatedScanResult(int Skipped, bool BackupPrivilegeEnabled);

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
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan GracefulExitTimeout = TimeSpan.FromSeconds(2);

    internal static bool IsHelperCommand(string[] args)
        => args.Length > 0 && string.Equals(args[0], HelperSwitch, StringComparison.Ordinal);

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
        try { _ = NormalizeAndValidateRoot(root); reason = string.Empty; return true; }
        catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            reason = ex.Message;
            return false;
        }
    }

    internal static async Task<ElevatedScanResult> ScanAsync(
        string root,
        IProgress<(List<FileItem> Batch, int Skipped)> progress,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Elevated scanning is available only on Windows.");
        token.ThrowIfCancellationRequested();

        string fullRoot = NormalizeAndValidateRoot(root);
        string pipeName = PipePrefix + Guid.NewGuid().ToString("N");
        byte[] nonce = RandomNumberGenerator.GetBytes(ElevatedScanProtocol.NonceLength);
        string nonceText = Convert.ToHexString(nonce);
        int parentPid = Environment.ProcessId;

        using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            PipeBufferSize,
            PipeBufferSize);

        Process? child = null;
        Task cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, token);
        try
        {
            child = await StartHelperAsync(pipeName, nonceText, parentPid, token).ConfigureAwait(false);
            await WaitForVerifiedClientAsync(server, child, cancellationTask, token).ConfigureAwait(false);

            ElevatedScanFrame hello = await ElevatedScanProtocol.ReadFrameAsync(server, token).ConfigureAwait(false);
            byte[] receivedNonce = ElevatedScanProtocol.ReadHello(hello);
            if (!CryptographicOperations.FixedTimeEquals(nonce, receivedNonce))
                throw new InvalidDataException("The elevated scanner failed authentication.");

            await ElevatedScanProtocol.WriteStartAsync(server, fullRoot, token).ConfigureAwait(false);

            int receivedFiles = 0;
            int lastSkipped = 0;
            while (true)
            {
                Task<ElevatedScanFrame> readTask = ElevatedScanProtocol.ReadFrameAsync(server, CancellationToken.None);
                Task completed = await Task.WhenAny(readTask, cancellationTask).ConfigureAwait(false);
                if (completed == cancellationTask)
                {
                    await TrySendCancelAsync(server).ConfigureAwait(false);
                    await Task.WhenAny(readTask, child.WaitForExitAsync(), Task.Delay(GracefulExitTimeout)).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    throw new OperationCanceledException(token);
                }

                ElevatedScanFrame frame = await readTask.ConfigureAwait(false);
                switch (frame.Type)
                {
                    case ElevatedScanFrameType.Batch:
                        {
                            (List<FileItem> batch, int skipped) = ElevatedScanProtocol.ReadBatch(frame);
                            if (skipped < lastSkipped) throw new InvalidDataException("The helper skipped-location count moved backwards.");
                            foreach (FileItem item in batch)
                            {
                                if (!IsPathUnderRoot(item.Path, fullRoot))
                                    throw new InvalidDataException("The helper returned a path outside the selected scan root.");
                            }
                            receivedFiles = checked(receivedFiles + batch.Count);
                            if (receivedFiles > ElevatedScanProtocol.MaximumTotalFiles)
                                throw new InvalidDataException("The elevated scan exceeded the supported file-count limit.");
                            lastSkipped = skipped;
                            progress.Report((batch, skipped));
                            break;
                        }
                    case ElevatedScanFrameType.Completed:
                        {
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
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            pipe.ConnectAsync((int)ConnectionTimeout.TotalMilliseconds, scanCancellation.Token).GetAwaiter().GetResult();

            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverPid) || serverPid != unchecked((uint)parentPid))
                throw new InvalidDataException("The elevated scan pipe is not owned by the requesting process.");

            ElevatedScanProtocol.WriteHelloAsync(pipe, nonce, CancellationToken.None).GetAwaiter().GetResult();
            handshakeSent = true;
            if (!IsAdministrator) throw new UnauthorizedAccessException("The scan helper did not receive administrator rights.");

            ElevatedScanFrame startFrame = ElevatedScanProtocol.ReadFrameAsync(pipe, scanCancellation.Token).GetAwaiter().GetResult();
            string root = NormalizeAndValidateRoot(ElevatedScanProtocol.ReadStart(startFrame));
            cancelWatcher = WatchForCancelAsync(pipe, scanCancellation);

            using WindowsBackupPrivilege backupPrivilege = WindowsBackupPrivilege.TryEnable();
            var reporter = new ImmediateProgress<(List<FileItem> Batch, int Skipped)>(update =>
            {
                scanCancellation.Token.ThrowIfCancellationRequested();
                foreach (FileItem item in update.Batch)
                {
                    if (!IsPathUnderRoot(item.Path, root))
                        throw new InvalidDataException("The scanner attempted to return a path outside the selected root.");
                }
                ElevatedScanProtocol.WriteBatchesAsync(pipe, update.Batch, update.Skipped, scanCancellation.Token).GetAwaiter().GetResult();
            });

            int skipped = FastFileScanner.Scan(root, reporter, scanCancellation.Token, strictReparseDirectories: true);
            scanCancellation.Token.ThrowIfCancellationRequested();
            ElevatedScanProtocol.WriteCompletedAsync(pipe, skipped, backupPrivilege.Enabled, scanCancellation.Token).GetAwaiter().GetResult();
            return 0;
        }
        catch (OperationCanceledException)
        {
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
            try { cancellation.Cancel(); } catch { }
        }
    }

    private static async Task WatchForCancelAsync(Stream pipe, CancellationTokenSource cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                ElevatedScanFrame frame = await ElevatedScanProtocol.ReadFrameAsync(pipe, cancellation.Token).ConfigureAwait(false);
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

    private static string NormalizeAndValidateRoot(string root)
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
        if (!Directory.Exists(fullRoot)) throw new DirectoryNotFoundException("The selected elevated scan root does not exist.");
        string? driveRoot = Path.GetPathRoot(fullRoot);
        if (string.IsNullOrWhiteSpace(driveRoot) || new DriveInfo(driveRoot).DriveType != DriveType.Fixed)
            throw new ArgumentException("Full access scan supports local fixed drives only.", nameof(root));
        if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("A reparse-point directory cannot be used as a full access scan root.", nameof(root));
        return fullRoot;
    }

    private static bool ParentUsesSameExecutable(int parentPid)
    {
        try
        {
            string? currentExecutable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExecutable)) return false;
            string currentPath = Path.GetFullPath(currentExecutable);
            using Process parent = Process.GetProcessById(parentPid);
            string? parentExecutable = parent.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(parentExecutable)) return false;
            string parentPath = Path.GetFullPath(parentExecutable);
            if (NativeFileIdentity.TryGet(currentPath, false, out NativeFileInformation currentIdentity)
                && NativeFileIdentity.TryGet(parentPath, false, out NativeFileInformation parentIdentity))
                return currentIdentity.Id == parentIdentity.Id;
            return string.Equals(currentPath, parentPath, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool IsPathUnderRoot(string path, string root)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string prefix = fullRoot + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
