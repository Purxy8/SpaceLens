using System.Runtime.InteropServices;

namespace DesktopOrganizer;

/// <summary>
/// Uses the modern Windows shell operation API with FOFX_RECYCLEONDELETE.
/// Unlike the legacy SHFileOperation wrapper, this flag requires deletion to
/// target the Recycle Bin instead of silently falling back to permanent delete.
/// </summary>
internal static class ShellRecycleService
{
    private const uint FofSilent = 0x0004;
    private const uint FofNoConfirmation = 0x0010;
    private const uint FofAllowUndo = 0x0040;
    private const uint FofNoErrorUi = 0x0400;
    private const uint FofNoConnectedElements = 0x2000;
    private const uint FofxRecycleOnDelete = 0x00080000;
    private static readonly Guid FileOperationClassId = new("3AD05575-8857-4850-9277-11B85BDB8E09");
    private static readonly Guid ShellItemId = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    internal static void Recycle(string path, IntPtr ownerWindow)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) throw new PlatformNotSupportedException("Recycle-only cleanup requires Windows 10 or later.");
        string fullPath = Path.GetFullPath(path);
        IFileOperation? operation = null;
        IShellItem? item = null;
        try
        {
            operation = CreateOperation();
            ThrowIfFailed(operation.SetOperationFlags(FofSilent | FofNoConfirmation | FofAllowUndo | FofNoErrorUi | FofNoConnectedElements | FofxRecycleOnDelete), "Windows could not enable Recycle Bin-only mode");
            if (ownerWindow != IntPtr.Zero) ThrowIfFailed(operation.SetOwnerWindow(ownerWindow), "Windows could not attach the Recycle Bin operation to SpaceLens");

            Guid itemId = ShellItemId;
            ThrowIfFailed(SHCreateItemFromParsingName(fullPath, IntPtr.Zero, ref itemId, out item), "Windows could not open the selected file for recycling");
            ThrowIfFailed(operation.DeleteItem(item, null), "Windows refused to queue the file for recycling");

            int performResult = operation.PerformOperations();
            int abortedResult = operation.GetAnyOperationsAborted(out bool aborted);
            ThrowIfFailed(abortedResult, "Windows could not report the Recycle Bin result");
            if (aborted) throw new OperationCanceledException("Windows canceled the Recycle Bin operation. The original file was kept.");
            ThrowIfFailed(performResult, "Windows could not move the file to the Recycle Bin");
        }
        finally
        {
            ReleaseComObject(item);
            ReleaseComObject(operation);
        }
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
        string path = Path.Combine(Path.GetTempPath(), "SpaceLens-recycle-selftest-" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllBytes(path, [0x53, 0x4C]);
        try
        {
            Recycle(path, IntPtr.Zero);
            if (File.Exists(path)) throw new InvalidOperationException("Recycle-only integration test left the original file in place.");
        }
        finally
        {
            // If the shell operation failed, remove only the disposable file
            // created by this test. A successful test leaves it recoverable in
            // the user's Recycle Bin and never empties or alters other entries.
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    private static void ThrowIfFailed(int result, string message)
    {
        if (result < 0) throw new IOException(message + ": " + Marshal.GetExceptionForHR(result)?.Message, Marshal.GetExceptionForHR(result));
    }

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
