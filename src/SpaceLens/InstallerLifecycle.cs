using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace DesktopOrganizer;

internal static class ProcessSecurity
{
    internal const string ElevatedPerUserOperationMessage = "SpaceLens is running with Administrator rights. Close it and run it normally, then try again. SpaceLens updates, installation, and uninstall do not require Administrator access.";

    internal static bool IsElevated
    {
        get
        {
            return SecurityPolicy.TryGetCurrentProcessElevation(out bool elevated, out _) ? elevated : true;
        }
    }

    internal static bool ShouldRefusePerUserOperation(bool elevated) => elevated;
}

internal static class InstallerLifecycle
{
    internal static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SpaceLens");
    internal static string InstalledExecutable => Path.Combine(InstallDirectory, "SpaceLens.exe");
    internal static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "SpaceLens.lnk");
    internal static string StartMenuDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "SpaceLens");
    internal static string StartMenuShortcut => Path.Combine(StartMenuDirectory, "SpaceLens.lnk");
    internal static string CacheDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceLens");
    internal const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SpaceLens";

    internal static void BeginUninstall(bool quiet)
    {
        if (ProcessSecurity.IsElevated)
        {
            if (!quiet) MessageBox.Show(ProcessSecurity.ElevatedPerUserOperationMessage, "Restart SpaceLens normally", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        string? current = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(current) || !Path.GetFullPath(current).Equals(Path.GetFullPath(InstalledExecutable), StringComparison.OrdinalIgnoreCase))
        {
            if (!quiet) MessageBox.Show("SpaceLens is not running from its installed location.", "Cannot uninstall", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        bool removeCache = true;
        if (!quiet)
        {
            using var dialog = new UninstallForm(); if (dialog.ShowDialog() != DialogResult.OK) return; removeCache = dialog.RemoveCache;
        }
        try
        {
            string helper = Path.Combine(Path.GetTempPath(), $"SpaceLens-Uninstall-{Guid.NewGuid():N}.exe"); File.Copy(current, helper, false);
            using FileStream helperLock = OpenVerifiedHelperCopy(current, helper);
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(helper)! }; start.ArgumentList.Add("--uninstall-helper"); start.ArgumentList.Add(Environment.ProcessId.ToString()); start.ArgumentList.Add(removeCache ? "1" : "0"); start.ArgumentList.Add(quiet ? "1" : "0");
            using Process? process = Process.Start(start);
            if (process is null) throw new InvalidOperationException("Windows could not start the uninstaller.");
        }
        catch (Exception ex) { if (!quiet) MessageBox.Show($"Could not start the uninstaller.\n\n{ex.Message}", "Uninstall failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    internal static void RunUninstallHelper(string[] args)
    {
        bool quiet = args.Length == 4 && args[3] == "1"; bool removeCache = args.Length == 4 && args[2] == "1";
        try
        {
            if (ProcessSecurity.IsElevated) throw new SecurityException(ProcessSecurity.ElevatedPerUserOperationMessage);
            if (args.Length != 4 || args[0] != "--uninstall-helper" || !int.TryParse(args[1], out int processId) || processId <= 0 || args[2] is not ("0" or "1") || args[3] is not ("0" or "1")) throw new InvalidDataException("The uninstall helper arguments are invalid.");
            string self = Environment.ProcessPath ?? throw new InvalidOperationException("The uninstall helper path is unavailable.");
            if (!IsOwnedTemporaryExecutable(self, "SpaceLens-Uninstall-")) throw new SecurityException("The uninstall helper is not running from its owned temporary path.");
            try { using Process parent = Process.GetProcessById(processId); parent.WaitForExit(15000); } catch { }
            string expected = Path.GetFullPath(InstallDirectory).TrimEnd(Path.DirectorySeparatorChar); string executable = Path.GetFullPath(InstalledExecutable);
            if (!executable.StartsWith(expected + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The install path failed safety validation.");
            TryDeleteFile(DesktopShortcut); TryDeleteFile(StartMenuShortcut); TryDeleteDirectoryIfEmpty(StartMenuDirectory);
            DeleteValidatedDirectory(expected, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SpaceLens"));
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", true)) key?.DeleteSubKeyTree("SpaceLens", false);
            if (removeCache) DeleteValidatedDirectory(CacheDirectory, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceLens"));
            if (!quiet) MessageBox.Show("SpaceLens was removed successfully.", "Uninstall complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { if (!quiet) MessageBox.Show($"SpaceLens could not be completely removed.\n\n{ex.Message}", "Uninstall incomplete", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { if (Environment.ProcessPath is string self) ScheduleSelfDelete(self); }
    }

    private static void DeleteValidatedDirectory(string actual, string expected)
    {
        actual = Path.GetFullPath(actual).TrimEnd(Path.DirectorySeparatorChar); expected = Path.GetFullPath(expected).TrimEnd(Path.DirectorySeparatorChar);
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Refused to remove an unexpected directory.");
        if (!Directory.Exists(actual)) return;
        if ((File.GetAttributes(actual) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Refused to recursively remove a linked directory.");
        if (!NativeResolvedPath.TryResolveDirectory(actual, out string resolved, out _) || !Path.GetFullPath(resolved).TrimEnd(Path.DirectorySeparatorChar).Equals(actual, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Refused to remove a directory whose filesystem target changed.");
        for (int attempt = 0; attempt < 5; attempt++) { try { if (Directory.Exists(actual)) Directory.Delete(actual, true); return; } catch (IOException) when (attempt < 4) { Thread.Sleep(350); } catch (UnauthorizedAccessException) when (attempt < 4) { Thread.Sleep(350); } }
    }
    private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectoryIfEmpty(string path) { try { if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); } catch { } }
    private static void ScheduleSelfDelete(string self)
    {
        try
        {
            string full = Path.GetFullPath(self);
            if (!IsOwnedTemporaryExecutable(full, "SpaceLens-Uninstall-")) return;
            string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(powershell)) throw new FileNotFoundException("Windows PowerShell is unavailable.", powershell);
            var start = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("-NoLogo"); start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-WindowStyle"); start.ArgumentList.Add("Hidden"); start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("$p=$env:SPACELENS_SELF_DELETE_TARGET; for($i=0;$i -lt 20 -and (Test-Path -LiteralPath $p);$i++){ Start-Sleep -Milliseconds 500; Remove-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue }");
            start.Environment["SPACELENS_SELF_DELETE_TARGET"] = full;
            using Process? process = Process.Start(start);
            if (process is null) throw new InvalidOperationException("Windows could not schedule uninstall-helper cleanup.");
        }
        catch { MoveFileEx(self, null, 4); }
    }

    private static FileStream OpenVerifiedHelperCopy(string sourcePath, string helperPath)
    {
        byte[] expected;
        using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan)) expected = SHA256.HashData(source);
        FileStream? helper = null;
        try
        {
            helper = new FileStream(helperPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            byte[] actual = SHA256.HashData(helper);
            if (!CryptographicOperations.FixedTimeEquals(expected, actual)) throw new CryptographicException("The temporary uninstall helper does not match the running SpaceLens executable.");
            helper.Position = 0; FileStream verified = helper; helper = null; return verified;
        }
        finally { helper?.Dispose(); }
    }

    private static bool IsOwnedTemporaryExecutable(string path, string prefix)
    {
        string full; try { full = Path.GetFullPath(path); } catch { return false; }
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), temp, StringComparison.OrdinalIgnoreCase)) return false;
        string name = Path.GetFileName(full);
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        string token = name[prefix.Length..^4];
        return token.Length == 32 && token.All(Uri.IsHexDigit);
    }

    internal static void RunSecuritySelfTest()
    {
        string temp = Path.GetFullPath(Path.GetTempPath());
        string valid = Path.Combine(temp, "SpaceLens-Uninstall-0123456789abcdef0123456789abcdef.exe");
        if (!IsOwnedTemporaryExecutable(valid, "SpaceLens-Uninstall-")
            || IsOwnedTemporaryExecutable(Path.Combine(temp, "SpaceLens-Uninstall-not-a-guid.exe"), "SpaceLens-Uninstall-")
            || IsOwnedTemporaryExecutable(Path.Combine(temp, "SpaceLens-Setup-0123456789abcdef0123456789abcdef.exe"), "SpaceLens-Uninstall-")
            || IsOwnedTemporaryExecutable(Path.Combine(temp, "nested", Path.GetFileName(valid)), "SpaceLens-Uninstall-"))
            throw new InvalidOperationException("Uninstall helper path policy self-test failed.");

        bool mismatchRejected = false;
        try { DeleteValidatedDirectory(Path.Combine(temp, "SpaceLens-delete-a"), Path.Combine(temp, "SpaceLens-delete-b")); }
        catch (InvalidOperationException) { mismatchRejected = true; }
        if (!mismatchRejected) throw new InvalidOperationException("Uninstall directory safety self-test failed.");
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool MoveFileEx(string existingFileName, string? newFileName, int flags);
}

internal sealed class UninstallForm : Form
{
    private readonly CheckBox removeCache = new() { Text = "Remove saved scan cache", Checked = true, AutoSize = true };
    internal bool RemoveCache => removeCache.Checked;
    internal UninstallForm()
    {
        Text = "Uninstall SpaceLens"; ClientSize = new Size(430, 190); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 10);
        var title = new Label { Text = "Remove SpaceLens?", Font = new Font("Segoe UI", 16, FontStyle.Bold), AutoSize = true, Location = new Point(22, 20) };
        var note = new Label { Text = "The application and its shortcuts will be removed.\nScanned personal files are never touched.", AutoSize = true, Location = new Point(24, 61), ForeColor = Color.DimGray };
        removeCache.Location = new Point(24, 112);
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(92, 32), Location = new Point(222, 145) };
        var uninstall = new Button { Text = "Uninstall", DialogResult = DialogResult.OK, Size = new Size(92, 32), Location = new Point(320, 145), BackColor = Color.FromArgb(196, 43, 28), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        Controls.AddRange([title, note, removeCache, cancel, uninstall]); AcceptButton = uninstall; CancelButton = cancel;
    }
}
