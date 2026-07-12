using System.Runtime.InteropServices;

namespace DesktopOrganizer;

/// <summary>
/// Uses the modern Windows shell operation API with FOFX_RECYCLEONDELETE.
/// Unlike the legacy SHFileOperation wrapper, this flag requires deletion to
/// target the Recycle Bin instead of silently falling back to permanent delete.
/// </summary>
internal static class ShellRecycleService
{
    internal sealed record RecycleBatchOutcome(List<FileItem> Queued, List<FileItem> Missing, List<string> Failures, string? OperationError);
    private const uint FofSilent = 0x0004;
    private const uint FofNoConfirmation = 0x0010;
    private const uint FofAllowUndo = 0x0040;
    private const uint FofNoErrorUi = 0x0400;
    private const uint FofNoConnectedElements = 0x2000;
    private const uint FofxRecycleOnDelete = 0x00080000;
    private static readonly Guid FileOperationClassId = new("3AD05575-8857-4850-9277-11B85BDB8E09");
    private static readonly Guid ShellItemId = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    internal static void Recycle(string path, IntPtr ownerWindow)
        => RecycleMany([path], ownerWindow);

    internal static void RecycleMany(IReadOnlyList<string> paths, IntPtr ownerWindow)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) throw new PlatformNotSupportedException("Recycle-only cleanup requires Windows 10 or later.");
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0) return;
        IFileOperation? operation = null;
        try
        {
            operation = CreateOperation();
            ThrowIfFailed(operation.SetOperationFlags(FofSilent | FofNoConfirmation | FofAllowUndo | FofNoErrorUi | FofNoConnectedElements | FofxRecycleOnDelete), "Windows could not enable Recycle Bin-only mode");
            if (ownerWindow != IntPtr.Zero) ThrowIfFailed(operation.SetOwnerWindow(ownerWindow), "Windows could not attach the Recycle Bin operation to SpaceLens");

            foreach (string path in paths)
            {
                IShellItem? item = null;
                try
                {
                    string fullPath = Path.GetFullPath(path);
                    Guid itemId = ShellItemId;
                    ThrowIfFailed(SHCreateItemFromParsingName(fullPath, IntPtr.Zero, ref itemId, out item), "Windows could not open a selected file for recycling");
                    ThrowIfFailed(operation.DeleteItem(item, null), "Windows refused to queue a selected file for recycling");
                }
                finally { ReleaseComObject(item); }
            }

            int performResult = operation.PerformOperations();
            int abortedResult = operation.GetAnyOperationsAborted(out bool aborted);
            ThrowIfFailed(abortedResult, "Windows could not report the Recycle Bin result");
            if (aborted) throw new OperationCanceledException("Windows canceled the Recycle Bin operation. The original file was kept.");
            ThrowIfFailed(performResult, "Windows could not move the file to the Recycle Bin");
        }
        finally
        {
            ReleaseComObject(operation);
        }
    }

    internal static RecycleBatchOutcome RecycleValidated(IReadOnlyList<FileItem> items, string expectedRoot, IntPtr ownerWindow)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) throw new PlatformNotSupportedException("Recycle-only cleanup requires Windows 10 or later.");
        ArgumentNullException.ThrowIfNull(items);
        if (!NativeResolvedPath.TryResolveDirectory(expectedRoot, out string liveRoot, out string rootError)
            || !string.Equals(Path.GetFullPath(expectedRoot).TrimEnd('\\'), liveRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The scanned location changed before cleanup: " + rootError);

        var queued = new List<FileItem>(items.Count);
        var missing = new List<FileItem>();
        var failures = new List<string>();
        string? operationError = null;
        foreach (FileItem item in items)
        {
            IFileOperation? operation = null;
            IShellItem? shellItem = null;
            try
            {
                if (!NativeFileState.TryGet(item.Path, out NativeFileSnapshot current, out int error))
                {
                    if (NativeFileState.IsMissingError(error)) missing.Add(item);
                    else failures.Add($"{item.Name}: could not be revalidated ({NativeFileState.ErrorMessage(error)}); not removed");
                    continue;
                }
                if ((current.Attributes & FileAttributes.Directory) != 0) { failures.Add($"{item.Name}: directories cannot be recycled from a file result; skipped"); continue; }
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0 && !FastFileScanner.IsKnownCloudFileReparsePoint(item.Path)) { failures.Add($"{item.Name}: redirecting or unknown reparse points are not recycled by SpaceLens; skipped"); continue; }
                if (!NativeResolvedPath.TryResolveFile(item.Path, out string resolvedPath, out string resolveError) || !NativeResolvedPath.IsStrictlyUnder(resolvedPath, liveRoot)) { failures.Add($"{item.Name}: its final filesystem path is outside the scanned location or could not be resolved ({resolveError}); skipped"); continue; }
                (_, FileSafety currentSafety) = AnalyzerForm.ClassifyForScan(resolvedPath, current.Attributes, pathIsCanonical: true);
                if ((byte)currentSafety > (byte)AnalyzerForm.EffectiveSafety(item)) { failures.Add($"{item.Name}: its safety level increased since the scan; rescan and confirm it again"); continue; }
                if (current.LogicalBytes != item.LogicalBytes || current.Modified != item.Modified || (item.Created != default && current.Created != item.Created)) { failures.Add($"{item.Name}: changed since the scan; skipped"); continue; }
                if (item.FileIndex != 0)
                {
                    if (!NativeFileIdentity.TryGet(resolvedPath, false, out NativeFileInformation identity) || identity.Id.VolumeSerial != item.VolumeSerial || identity.Id.FileIndex != item.FileIndex) { failures.Add($"{item.Name}: file identity changed since the scan; skipped"); continue; }
                }
                else if (currentSafety != FileSafety.Normal) { failures.Add($"{item.Name}: Windows did not expose a stable identity for this important path; skipped"); continue; }

                operation = CreateOperation();
                ThrowIfFailed(operation.SetOperationFlags(FofSilent | FofNoConfirmation | FofAllowUndo | FofNoErrorUi | FofNoConnectedElements | FofxRecycleOnDelete), "Windows could not enable Recycle Bin-only mode");
                if (ownerWindow != IntPtr.Zero) ThrowIfFailed(operation.SetOwnerWindow(ownerWindow), "Windows could not attach the Recycle Bin operation to SpaceLens");
                Guid itemId = ShellItemId;
                ThrowIfFailed(SHCreateItemFromParsingName(resolvedPath, IntPtr.Zero, ref itemId, out shellItem), "Windows could not bind the selected file for recycling");
                ThrowIfFailed(operation.DeleteItem(shellItem, null), "Windows refused to queue the selected file for recycling");
                int performResult = operation.PerformOperations();
                int abortedResult = operation.GetAnyOperationsAborted(out bool aborted);
                queued.Add(item);
                if (abortedResult < 0) failures.Add($"{item.Name}: {HResultMessage(abortedResult, "Windows could not report the Recycle Bin result")}");
                else if (aborted) failures.Add($"{item.Name}: Windows canceled the Recycle Bin operation.");
                if (performResult < 0) failures.Add($"{item.Name}: {HResultMessage(performResult, "Windows could not move the file to the Recycle Bin")}");
            }
            catch (Exception ex) { failures.Add($"{item.Name}: {ex.Message}"); operationError ??= ex.Message; }
            finally { ReleaseComObject(shellItem); ReleaseComObject(operation); }
        }
        return new(queued, missing, failures, operationError);
    }

    internal static void ValidateAvailability()
    {
        IFileOperation? operation = null;
        try
        {
            operation = CreateOperation();
            ThrowIfFailed(operation.SetOperationFlags(FofAllowUndo | FofNoErrorUi | FofNoConnectedElements | FofxRecycleOnDelete), "Windows does not support Recycle Bin-only cleanup");
        }
        finally { ReleaseComObject(operation); }
    }

    internal static void RunIntegrationSelfTest()
    {
        string root = Path.Combine(Path.GetTempPath(), "SpaceLens-recycle-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string first = Path.Combine(root, "first.tmp"), second = Path.Combine(root, "second.tmp");
        string linkTarget = Path.Combine(root, "link-target.tmp"), link = Path.Combine(root, "link.tmp");
        File.WriteAllBytes(first, [0x53, 0x4C]);
        File.WriteAllBytes(second, [0x53, 0x4C, 0x32]);
        try
        {
            var scanned = new List<FileItem>();
            _ = FastFileScanner.Scan(root, new ImmediateProgress<(List<FileItem> Batch, int Skipped)>(update => scanned.AddRange(update.Batch)), CancellationToken.None);
            if (scanned.Count != 2) throw new InvalidOperationException("Recycle integration test could not scan its disposable files.");
            RecycleBatchOutcome outcome = RecycleValidated(scanned, root, IntPtr.Zero);
            if (outcome.Queued.Count != 2 || outcome.Missing.Count != 0 || outcome.Failures.Count != 0 || File.Exists(first) || File.Exists(second)) throw new InvalidOperationException("Recycle-only integration test did not safely recycle both disposable files.");

            File.WriteAllBytes(linkTarget, [0x53, 0x4C, 0x33]);
            bool linkCreated = false;
            try { File.CreateSymbolicLink(link, linkTarget); linkCreated = true; }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { }
            if (linkCreated)
            {
                var linkItem = new FileItem(link, 0, 0, default, "Other", Attributes: FileAttributes.ReparsePoint, Safety: FileSafety.Normal);
                RecycleBatchOutcome linkOutcome = RecycleValidated([linkItem], root, IntPtr.Zero);
                if (linkOutcome.Queued.Count != 0 || linkOutcome.Failures.Count == 0 || !File.Exists(link) || !File.Exists(linkTarget)) throw new InvalidOperationException("Recycle validation did not reject a symbolic-link replacement.");
            }
        }
        finally
        {
            // If the shell operation failed, remove only the disposable file
            // created by this test. A successful test leaves it recoverable in
            // the user's Recycle Bin and never empties or alters other entries.
            try { if (File.Exists(first)) File.Delete(first); } catch { }
            try { if (File.Exists(second)) File.Delete(second); } catch { }
            try { if (File.Exists(link)) File.Delete(link); } catch { }
            try { if (File.Exists(linkTarget)) File.Delete(linkTarget); } catch { }
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }

    private static void ThrowIfFailed(int result, string message)
    {
        if (result < 0) throw new IOException(message + ": " + Marshal.GetExceptionForHR(result)?.Message, Marshal.GetExceptionForHR(result));
    }

    private static string HResultMessage(int result, string message) => message + ": " + (Marshal.GetExceptionForHR(result)?.Message ?? $"HRESULT 0x{result:X8}");

    private static IFileOperation CreateOperation()
    {
        Type type = Type.GetTypeFromCLSID(FileOperationClassId, throwOnError: true) ?? throw new PlatformNotSupportedException("Windows Shell file operations are unavailable.");
        return (IFileOperation)(Activator.CreateInstance(type) ?? throw new PlatformNotSupportedException("Windows could not create a Shell file operation."));
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is null || !Marshal.IsComObject(value)) return;
        try { _ = Marshal.FinalReleaseComObject(value); } catch { }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem shellItem);

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem;

    [ComImport]
    [Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise([MarshalAs(UnmanagedType.Interface)] object? progressSink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint operationFlags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog([MarshalAs(UnmanagedType.Interface)] object? progressDialog);
        [PreserveSig] int SetProperties([MarshalAs(UnmanagedType.Interface)] object? properties);
        [PreserveSig] int SetOwnerWindow(IntPtr ownerWindow);
        [PreserveSig] int ApplyPropertiesToItem(IShellItem item);
        [PreserveSig] int ApplyPropertiesToItems([MarshalAs(UnmanagedType.Interface)] object items);
        [PreserveSig] int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, [MarshalAs(UnmanagedType.Interface)] object? progressSink);
        [PreserveSig] int RenameItems([MarshalAs(UnmanagedType.Interface)] object items, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        [PreserveSig] int MoveItem(IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? newName, [MarshalAs(UnmanagedType.Interface)] object? progressSink);
        [PreserveSig] int MoveItems([MarshalAs(UnmanagedType.Interface)] object items, IShellItem destinationFolder);
        [PreserveSig] int CopyItem(IShellItem item, IShellItem destinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? copyName, [MarshalAs(UnmanagedType.Interface)] object? progressSink);
        [PreserveSig] int CopyItems([MarshalAs(UnmanagedType.Interface)] object items, IShellItem destinationFolder);
        [PreserveSig] int DeleteItem(IShellItem item, [MarshalAs(UnmanagedType.Interface)] object? progressSink);
        [PreserveSig] int DeleteItems([MarshalAs(UnmanagedType.Interface)] object items);
        [PreserveSig] int NewItem(IShellItem destinationFolder, FileAttributes fileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string? name, [MarshalAs(UnmanagedType.LPWStr)] string? templateName, [MarshalAs(UnmanagedType.Interface)] object? progressSink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
