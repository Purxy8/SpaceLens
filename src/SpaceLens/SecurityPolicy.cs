using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace DesktopOrganizer;

/// <summary>
/// Central fail-closed switches for security-sensitive capabilities.
///
/// The elevated scanner is intentionally unavailable in this release. A managed
/// executable launched through UAC can be influenced by inherited .NET startup
/// configuration before managed code gets a chance to authenticate its parent.
/// Re-enable this only after the privileged entry point has moved to a minimal
/// native, signed broker with an explicitly constructed environment.
/// </summary>
internal static class SecurityPolicy
{
    // Keep this runtime-readonly rather than const so fail-closed checks remain
    // executable code and cannot be optimized into unreachable branches at build.
    internal static readonly bool ElevatedFullAccessAvailable = false;

    internal const string ElevatedFullAccessUnavailableReason =
        "Full access scanning is temporarily disabled while its Administrator helper is being replaced with a hardened native broker. " +
        "Use the standard scan; it remains read-only and does not require Administrator access.";

    internal static bool TryGetCurrentProcessElevation(out bool elevated, out string error)
    {
        elevated = false;
        error = "";
        if (!OperatingSystem.IsWindows()) return true;

        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            TokenElevation information;
            if (!GetTokenInformation(identity.AccessToken, 20, out information, Marshal.SizeOf<TokenElevation>(), out _))
            {
                error = new Win32Exception(Marshal.GetLastPInvokeError()).Message;
                return false;
            }

            elevated = information.TokenIsElevated != 0;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevation
    {
        internal int TokenIsElevated;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        Microsoft.Win32.SafeHandles.SafeAccessTokenHandle tokenHandle,
        int tokenInformationClass,
        out TokenElevation tokenInformation,
        int tokenInformationLength,
        out int returnLength);
}
