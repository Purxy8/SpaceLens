using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DesktopOrganizer;

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
            string helper = Path.Combine(Path.GetTempPath(), $"SpaceLens-Uninstall-{Guid.NewGuid():N}.exe"); File.Copy(current, helper, true);
            var start = new ProcessStartInfo(helper) { UseShellExecute = false }; start.ArgumentList.Add("--uninstall-helper"); start.ArgumentList.Add(Environment.ProcessId.ToString()); start.ArgumentList.Add(removeCache ? "1" : "0"); start.ArgumentList.Add(quiet ? "1" : "0"); Process.Start(start);
        }
        catch (Exception ex) { if (!quiet) MessageBox.Show($"Could not start the uninstaller.\n\n{ex.Message}", "Uninstall failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    internal static void RunUninstallHelper(string[] args)
    {
        bool quiet = args.Length > 3 && args[3] == "1"; bool removeCache = args.Length > 2 && args[2] == "1";
        try
        {
            if (args.Length > 1 && int.TryParse(args[1], out int processId)) try { Process.GetProcessById(processId).WaitForExit(15000); } catch { }
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
        for (int attempt = 0; attempt < 5; attempt++) { try { if (Directory.Exists(actual)) Directory.Delete(actual, true); return; } catch (IOException) when (attempt < 4) { Thread.Sleep(350); } catch (UnauthorizedAccessException) when (attempt < 4) { Thread.Sleep(350); } }
    }
    private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectoryIfEmpty(string path) { try { if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); } catch { } }
    private static void ScheduleSelfDelete(string self)
    {
        try
        {
            string full = Path.GetFullPath(self), temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar); string name = Path.GetFileName(full);
            if (!Path.GetDirectoryName(full)!.Equals(temp, StringComparison.OrdinalIgnoreCase) || !name.StartsWith("SpaceLens-Uninstall-", StringComparison.Ordinal) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
            string script = Path.Combine(temp, $"SpaceLens-Cleanup-{Guid.NewGuid():N}.cmd");
            File.WriteAllText(script, $"@echo off\r\nfor /L %%i in (1,1,20) do (\r\n  del /f /q \"{full}\" >nul 2>&1\r\n  if not exist \"{full}\" goto done\r\n  ping 127.0.0.1 -n 2 >nul\r\n)\r\n:done\r\ndel /f /q \"%~f0\"\r\n");
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }; start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c"); start.ArgumentList.Add(script); Process.Start(start);
        }
        catch { MoveFileEx(self, null, 4); }
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
