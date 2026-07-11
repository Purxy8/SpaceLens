using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DesktopOrganizer;

/// <summary>
/// Temporarily enables only SeBackupPrivilege on the current process token.
/// The elevated scan helper is deliberately short lived, but restoring the
/// prior token state keeps the privilege scope explicit and auditable.
/// </summary>
internal sealed class WindowsBackupPrivilege : IDisposable
{
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const int ErrorNotAllAssigned = 1300;

    private readonly SafeAccessTokenHandle? token;
    private readonly TokenPrivileges previousState;
    private readonly bool restorePreviousState;

    private WindowsBackupPrivilege(SafeAccessTokenHandle? token, TokenPrivileges previousState, bool restorePreviousState, bool enabled)
    {
        this.token = token;
        this.previousState = previousState;
        this.restorePreviousState = restorePreviousState;
        Enabled = enabled;
    }

    internal bool Enabled { get; }

    internal static WindowsBackupPrivilege TryEnable()
    {
        if (!OperatingSystem.IsWindows()) return new(null, default, false, false);

        SafeAccessTokenHandle? token = null;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out token))
                return new(token, default, false, false);

            if (!LookupPrivilegeValueW(null, "SeBackupPrivilege", out Luid luid))
                return new(token, default, false, false);

            var requested = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled
            };

            Marshal.SetLastPInvokeError(0);
            bool adjusted = AdjustTokenPrivileges(
                token,
                false,
                ref requested,
                Marshal.SizeOf<TokenPrivileges>(),
                out TokenPrivileges previous,
                out _);
            int error = Marshal.GetLastPInvokeError();
            bool enabled = adjusted && error != ErrorNotAllAssigned;
            return new(token, previous, enabled, enabled);
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or System.Security.SecurityException)
        {
            token?.Dispose();
            return new(null, default, false, false);
        }
    }

    public void Dispose()
    {
        if (token is null) return;

        try
        {
            if (restorePreviousState && !token.IsInvalid && !token.IsClosed)
            {
                TokenPrivileges previous = previousState;
                _ = AdjustTokenPrivileges(
                    token,
                    false,
                    ref previous,
                    Marshal.SizeOf<TokenPrivileges>(),
                    out _,
                    out _);
            }
        }
        finally
        {
            token.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        internal uint LowPart;
        internal int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        internal uint PrivilegeCount;
        internal Luid Luid;
        internal uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(
        string? systemName,
        string name,
        out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        SafeAccessTokenHandle tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        int bufferLength,
        out TokenPrivileges previousState,
        out int returnLength);
}
