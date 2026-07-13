using System.ComponentModel;
using System.Collections;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

return RestrictedProcessLauncher.Run(args);

internal static class RestrictedProcessLauncher
{
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAssignPrimary = 0x0001;
    private const uint DisableMaxPrivilege = 0x0001;
    private const uint LuaToken = 0x0004;
    private const int TokenPrivileges = 3;
    private const int TokenElevationType = 18;
    private const int TokenElevation = 20;
    private const int TokenElevationTypeDefault = 1;
    private const int TokenElevationTypeLimited = 3;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const int ErrorInsufficientBuffer = 122;
    private const int MaximumTokenPrivilegesBytes = 64 * 1024;
    private const int SecurityImpersonation = 2;
    private const int TokenTypeImpersonation = 2;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 0x00000102;
    private const uint StillActive = 259;
    private const uint TimeoutExitCode = 124;
    private const int ChildTimeoutMilliseconds = 150_000;

    internal static int Run(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Restricted packaged self-tests require Windows.");
            if (args.Length != 2 || !string.Equals(args[1], "--self-test", StringComparison.Ordinal))
                throw new ArgumentException("Usage: RestrictedProcessLauncher.exe <SpaceLens.exe|SpaceLens-Setup.exe> --self-test");

            string target = Path.GetFullPath(args[0]);
            string name = Path.GetFileName(target);
            if ((!string.Equals(name, "SpaceLens.exe", StringComparison.Ordinal) &&
                 !string.Equals(name, "SpaceLens-Setup.exe", StringComparison.Ordinal)) || target.Contains('"'))
                throw new InvalidDataException("The restricted launcher accepts only an exact packaged SpaceLens self-test target.");

            var targetInfo = new FileInfo(target);
            if (!targetInfo.Exists || targetInfo.Length < 4096 || targetInfo.Length > 500L * 1024 * 1024 ||
                (targetInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The packaged self-test target is missing, unbounded, or a reparse point.");

            using var targetLock = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            Span<byte> magic = stackalloc byte[2];
            if (targetLock.Read(magic) != magic.Length || magic[0] != (byte)'M' || magic[1] != (byte)'Z')
                throw new InvalidDataException("The packaged self-test target is not a Windows executable.");
            targetLock.Position = 0;

            return LaunchRestricted(target);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Restricted packaged self-test launch failed: {ex.Message}");
            return 125;
        }
    }

    private static int LaunchRestricted(string target)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAssignPrimary | TokenDuplicate | TokenQuery, out SafeAccessTokenHandle currentToken))
            throw Win32("OpenProcessToken(current)");
        using (currentToken)
        {
            if (GetTokenElevation(currentToken) == 0 && !IsAdministrator(currentToken))
                throw new InvalidOperationException("The restricted launcher is only for elevated CI/build hosts.");

            if (!CreateRestrictedToken(currentToken, DisableMaxPrivilege | LuaToken, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, IntPtr.Zero, out SafeAccessTokenHandle restrictedToken))
                throw Win32("CreateRestrictedToken(LUA)");
            using (restrictedToken)
            {
                VerifyRestrictedNonAdministrator(restrictedToken, "created token");
                return CreateAndWait(target, restrictedToken);
            }
        }
    }

    private static int CreateAndWait(string target, SafeAccessTokenHandle restrictedToken)
    {
        IntPtr job = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        ProcessInformation process = default;
        bool childCreated = false;
        try
        {
            job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw Win32("CreateJobObject");
            var limits = new JobObjectExtendedLimitInformation();
            limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref limits, Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
                throw Win32("SetInformationJobObject(KILL_ON_JOB_CLOSE)");

            var startup = new StartupInfo { Cb = Marshal.SizeOf<StartupInfo>() };
            var commandLine = new StringBuilder($"\"{target}\" --self-test");
            environment = BuildSanitizedEnvironmentBlock();
            if (!CreateProcessAsUser(
                    restrictedToken,
                    target,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    CreateSuspended | CreateUnicodeEnvironment | CreateNoWindow,
                    environment,
                    Path.GetDirectoryName(target),
                    ref startup,
                    out process))
                throw Win32("CreateProcessAsUser");
            childCreated = true;

            if (!AssignProcessToJobObject(job, process.Process)) throw Win32("AssignProcessToJobObject");
            if (!OpenProcessToken(process.Process, TokenDuplicate | TokenQuery, out SafeAccessTokenHandle childToken)) throw Win32("OpenProcessToken(child)");
            using (childToken) VerifyRestrictedNonAdministrator(childToken, "suspended child token");

            if (ResumeThread(process.Thread) == uint.MaxValue) throw Win32("ResumeThread");
            uint wait = WaitForSingleObject(process.Process, ChildTimeoutMilliseconds);
            if (wait == WaitTimeout)
            {
                if (!TerminateJobObject(job, TimeoutExitCode)) throw Win32("TerminateJobObject(timeout)");
                _ = WaitForSingleObject(process.Process, 10_000);
                Console.Error.WriteLine("Restricted packaged self-test timed out and its process tree was terminated.");
                return (int)TimeoutExitCode;
            }
            if (wait != WaitObject0) throw Win32("WaitForSingleObject(child)");
            if (!GetExitCodeProcess(process.Process, out uint exitCode)) throw Win32("GetExitCodeProcess");
            if (exitCode == StillActive) throw new InvalidOperationException("The packaged self-test remained active after a signaled wait.");
            return unchecked((int)exitCode);
        }
        catch
        {
            if (job != IntPtr.Zero) _ = TerminateJobObject(job, 125);
            if (childCreated && process.Process != IntPtr.Zero) _ = TerminateProcess(process.Process, 125);
            throw;
        }
        finally
        {
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
            if (process.Thread != IntPtr.Zero) _ = CloseHandle(process.Thread);
            if (process.Process != IntPtr.Zero) _ = CloseHandle(process.Process);
            if (job != IntPtr.Zero) _ = CloseHandle(job);
        }
    }

    private static IntPtr BuildSanitizedEnvironmentBlock()
    {
        string? hook = Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS");
        string? marker = Environment.GetEnvironmentVariable("SPACELENS_STARTUP_HOOK_MARKER");
        bool hasHook = !string.IsNullOrWhiteSpace(hook);
        bool hasMarker = !string.IsNullOrWhiteSpace(marker);
        if (hasHook != hasMarker) throw new SecurityException("The harmless startup-hook probe variables must be present together or absent together.");

        var entries = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables(EnvironmentVariableTarget.Process))
        {
            if (entry.Key is not string name || entry.Value is not string value || name.Length == 0 || name.Contains('=') || name.Contains('\0') || value.Contains('\0')) continue;
            if (!IsManagedInjectionVariable(name)) entries[name] = value;
        }

        if (hasHook)
        {
            string hookPath = Path.GetFullPath(hook!);
            var hookInfo = new FileInfo(hookPath);
            string markerPath = Path.GetFullPath(marker!);
            var markerParent = new DirectoryInfo(Path.GetDirectoryName(markerPath)!);
            if (!string.Equals(hookInfo.Name, "SpaceLens.StartupHookProbe.dll", StringComparison.Ordinal) ||
                !hookInfo.Exists || hookInfo.Length <= 0 || hookInfo.Length > 10 * 1024 * 1024 || (hookInfo.Attributes & FileAttributes.ReparsePoint) != 0 ||
                !string.Equals(Path.GetFileName(markerPath), "hook-executed.marker", StringComparison.Ordinal) || File.Exists(markerPath) ||
                !markerParent.Exists || (markerParent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new SecurityException("The inherited startup-hook probe pair is not the exact bounded harmless test fixture.");
            entries["DOTNET_STARTUP_HOOKS"] = hookPath;
            entries["SPACELENS_STARTUP_HOOK_MARKER"] = markerPath;
        }

        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windows) || !Path.IsPathFullyQualified(windows))
            throw new SecurityException("Windows could not provide a trusted child PATH root.");
        entries["PATH"] = Path.Combine(windows, "System32") + Path.PathSeparator + windows;

        foreach (string name in entries.Keys)
            if (IsManagedInjectionVariable(name) &&
                !string.Equals(name, "DOTNET_STARTUP_HOOKS", StringComparison.Ordinal) &&
                !string.Equals(name, "SPACELENS_STARTUP_HOOK_MARKER", StringComparison.Ordinal))
                throw new SecurityException("A managed-runtime injection variable survived environment sanitization.");
        if (hasHook != (entries.ContainsKey("DOTNET_STARTUP_HOOKS") && entries.ContainsKey("SPACELENS_STARTUP_HOOK_MARKER")))
            throw new SecurityException("The harmless startup-hook probe pair was not forwarded atomically.");

        string block = string.Join('\0', entries.Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
        if (block.Length > 32_767) throw new InvalidOperationException("The sanitized child environment is unexpectedly large.");
        char[] characters = block.ToCharArray();
        IntPtr pointer = Marshal.AllocHGlobal(characters.Length * sizeof(char));
        try { Marshal.Copy(characters, 0, pointer, characters.Length); return pointer; }
        catch { Marshal.FreeHGlobal(pointer); throw; }
    }

    private static bool IsManagedInjectionVariable(string name) =>
        name.StartsWith("COR_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("COREHOST_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("SPACELENS_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("APP_CONTEXT_", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "NATIVE_DLL_SEARCH_DIRECTORIES", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "__COMPAT_LAYER", StringComparison.OrdinalIgnoreCase);

    private static void VerifyRestrictedNonAdministrator(SafeAccessTokenHandle token, string description)
    {
        if (GetTokenElevation(token) != 0) throw new SecurityException($"The {description} is still elevated.");
        int elevationType = GetTokenInt32(token, TokenElevationType, "TokenElevationType");
        if (elevationType is not TokenElevationTypeDefault and not TokenElevationTypeLimited)
            throw new SecurityException($"The {description} reports an unsafe or unknown elevation type ({elevationType}).");
        VerifyNoPowerfulEnabledPrivileges(token, description);
        if (IsAdministrator(token)) throw new SecurityException($"The {description} still has enabled Administrators membership.");
    }

    private static void VerifyNoPowerfulEnabledPrivileges(SafeAccessTokenHandle token, string description)
    {
        if (GetTokenInformationBuffer(token, TokenPrivileges, IntPtr.Zero, 0, out int requiredBytes))
            throw new SecurityException($"The {description} privilege query unexpectedly succeeded without a buffer.");
        int queryError = Marshal.GetLastPInvokeError();
        if (queryError != ErrorInsufficientBuffer)
            throw Win32(queryError, "GetTokenInformation(TokenPrivileges size)");
        if (requiredBytes < sizeof(uint) || requiredBytes > MaximumTokenPrivilegesBytes)
            throw new SecurityException($"The {description} privilege information size is invalid ({requiredBytes}).");

        IntPtr buffer = Marshal.AllocHGlobal(requiredBytes);
        try
        {
            if (!GetTokenInformationBuffer(token, TokenPrivileges, buffer, requiredBytes, out int returnedBytes))
                throw Win32("GetTokenInformation(TokenPrivileges)");
            if (returnedBytes < sizeof(uint) || returnedBytes > requiredBytes)
                throw new SecurityException($"The {description} privilege information length is invalid ({returnedBytes}).");

            uint count = unchecked((uint)Marshal.ReadInt32(buffer));
            int entrySize = Marshal.SizeOf<LuidAndAttributes>();
            if (entrySize != 12)
                throw new SecurityException($"The native LUID_AND_ATTRIBUTES layout is unexpected ({entrySize}).");
            int maximumCount = (returnedBytes - sizeof(uint)) / entrySize;
            if (count > maximumCount)
                throw new SecurityException($"The {description} privilege count exceeds its bounded buffer.");
            if (!LookupPrivilegeValue(null, "SeChangeNotifyPrivilege", out Luid changeNotify))
                throw Win32("LookupPrivilegeValue(SeChangeNotifyPrivilege)");

            for (uint index = 0; index < count; index++)
            {
                int offset = checked(sizeof(uint) + checked((int)index * entrySize));
                var privilege = Marshal.PtrToStructure<LuidAndAttributes>(IntPtr.Add(buffer, offset));
                if ((privilege.Attributes & SePrivilegeEnabled) != 0 && !privilege.Luid.Equals(changeNotify))
                    throw new SecurityException($"The {description} retains an enabled powerful privilege.");
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static int GetTokenElevation(SafeAccessTokenHandle token)
        => GetTokenInt32(token, TokenElevation, "TokenElevation");

    private static int GetTokenInt32(SafeAccessTokenHandle token, int informationClass, string description)
    {
        if (!GetTokenInformation(token, informationClass, out int information, sizeof(int), out int returned) || returned != sizeof(int))
            throw Win32($"GetTokenInformation({description})");
        return information;
    }

    private static bool IsAdministrator(SafeAccessTokenHandle token)
    {
        if (!DuplicateTokenEx(token, TokenQuery, IntPtr.Zero, SecurityImpersonation, TokenTypeImpersonation, out SafeAccessTokenHandle impersonationToken))
            throw Win32("DuplicateTokenEx(impersonation)");
        using (impersonationToken)
        {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        byte[] sid = new byte[administrators.BinaryLength];
        administrators.GetBinaryForm(sid, 0);
        GCHandle pinned = GCHandle.Alloc(sid, GCHandleType.Pinned);
        try
        {
            if (!CheckTokenMembership(impersonationToken, pinned.AddrOfPinnedObject(), out bool member)) throw Win32("CheckTokenMembership(Administrators)");
            return member;
        }
        finally { pinned.Free(); }
        }
    }

    private static Win32Exception Win32(string operation) => Win32(Marshal.GetLastPInvokeError(), operation);

    private static Win32Exception Win32(int error, string operation) =>
        new(error, $"{operation} failed with Win32 error {error}: {new Win32Exception(error).Message}");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal int Cb;
        internal string? Reserved;
        internal string? Desktop;
        internal string? Title;
        internal int X;
        internal int Y;
        internal int XSize;
        internal int YSize;
        internal int XCountChars;
        internal int YCountChars;
        internal int FillAttribute;
        internal int Flags;
        internal short ShowWindow;
        internal short Reserved2Length;
        internal IntPtr Reserved2;
        internal IntPtr StandardInput;
        internal IntPtr StandardOutput;
        internal IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal IntPtr Process;
        internal IntPtr Thread;
        internal uint ProcessId;
        internal uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSetSize;
        internal UIntPtr MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Luid : IEquatable<Luid>
    {
        internal readonly uint LowPart;
        internal readonly int HighPart;

        public bool Equals(Luid other) => LowPart == other.LowPart && HighPart == other.HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct LuidAndAttributes
    {
        internal readonly Luid Luid;
        internal readonly uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        internal JobObjectBasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal UIntPtr ProcessMemoryLimit;
        internal UIntPtr JobMemoryLimit;
        internal UIntPtr PeakProcessMemoryUsed;
        internal UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateRestrictedToken(
        SafeAccessTokenHandle existingToken,
        uint flags,
        uint disableSidCount,
        IntPtr sidsToDisable,
        uint deletePrivilegeCount,
        IntPtr privilegesToDelete,
        uint restrictedSidCount,
        IntPtr sidsToRestrict,
        out SafeAccessTokenHandle newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, out int information, int informationLength, out int returnLength);

    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformationBuffer(SafeAccessTokenHandle token, int informationClass, IntPtr information, int informationLength, out int returnLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        SafeAccessTokenHandle existingToken,
        uint desiredAccess,
        IntPtr tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out SafeAccessTokenHandle newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CheckTokenMembership(SafeAccessTokenHandle token, IntPtr sidToCheck, [MarshalAs(UnmanagedType.Bool)] out bool isMember);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(
        SafeAccessTokenHandle token,
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref JobObjectExtendedLimitInformation information, int informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, int milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
