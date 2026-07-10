using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace SpaceLensSetup;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--self-test")) { ApplicationConfiguration.Initialize(); Environment.ExitCode = SetupEngine.SelfTest() ? 0 : 1; return; }
        int waitIndex = Array.IndexOf(args, "--wait-pid"); if (waitIndex >= 0 && waitIndex + 1 < args.Length && int.TryParse(args[waitIndex + 1], out int processId)) try { Process.GetProcessById(processId).WaitForExit(20000); } catch { }
        bool upgrade = args.Contains("--upgrade"); ApplicationConfiguration.Initialize(); Application.Run(new SetupForm(upgrade)); if (upgrade && Environment.ProcessPath is string self) SetupEngine.ScheduleSelfDelete(self);
    }
}

internal sealed class SetupForm : Form
{
    private readonly CheckBox desktopShortcut = new() { Text = "Create a Desktop shortcut", Checked = true, AutoSize = true };
    private readonly CheckBox launchAfter = new() { Text = "Launch SpaceLens after installation", Checked = true, AutoSize = true };
    private readonly Button installButton = new() { Text = "Install", Size = new Size(112, 38), BackColor = Color.FromArgb(0, 120, 215), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly ProgressBar progress = new() { Style = ProgressBarStyle.Marquee, Visible = false };
    private readonly Label state = new() { Text = "Ready to install", AutoSize = true, ForeColor = Color.DimGray };

    internal SetupForm(bool upgrade = false)
    {
        Text = upgrade ? "Update SpaceLens" : "Install SpaceLens"; ClientSize = new Size(590, 390); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 10); BackColor = Color.FromArgb(246, 248, 251);
        var header = new Panel { Dock = DockStyle.Top, Height = 108, BackColor = Color.FromArgb(25, 33, 48) };
        header.Controls.Add(new Label { Text = "SpaceLens", Font = new Font("Segoe UI", 25, FontStyle.Bold), ForeColor = Color.White, AutoSize = true, Location = new Point(28, 20) });
        header.Controls.Add(new Label { Text = "Fast, friendly disk space analysis", ForeColor = Color.FromArgb(180, 195, 215), AutoSize = true, Location = new Point(31, 68) });
        var title = new Label { Text = upgrade ? "Update SpaceLens to version 1.1.0" : "Install SpaceLens for this Windows account", Font = new Font("Segoe UI", 15, FontStyle.Bold), AutoSize = true, Location = new Point(30, 137) };
        var description = new Label { Text = upgrade ? "Your saved scans and preferences will be preserved.\nSpaceLens will replace only its installed application files." : "Setup creates a Start Menu entry, an Apps & Features uninstall entry,\nand—if selected—a Desktop shortcut. Administrator access is not required.", AutoSize = true, ForeColor = Color.FromArgb(70, 80, 95), Location = new Point(32, 177) };
        var path = new Label { Text = SetupEngine.InstallDirectory, AutoEllipsis = true, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White, Location = new Point(32, 229), Size = new Size(524, 29), Padding = new Padding(6, 4, 6, 4) };
        desktopShortcut.Location = new Point(33, 277); launchAfter.Location = new Point(33, 307);
        progress.Location = new Point(32, 346); progress.Size = new Size(280, 22); state.Location = new Point(32, 372);
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(96, 38), Location = new Point(348, 331) }; installButton.Text = upgrade ? "Update" : "Install"; installButton.Location = new Point(450, 331); installButton.Click += async (_, _) => await InstallAsync();
        Controls.AddRange([header, title, description, path, desktopShortcut, launchAfter, progress, state, cancel, installButton]); CancelButton = cancel; AcceptButton = installButton;
    }

    private async Task InstallAsync()
    {
        installButton.Enabled = false; progress.Visible = true; state.Text = "Verifying and installing SpaceLens…";
        try
        {
            await Task.Run(() => SetupEngine.Install(desktopShortcut.Checked)); state.Text = "Installation complete.";
            if (launchAfter.Checked) Process.Start(new ProcessStartInfo(SetupEngine.InstalledExecutable) { UseShellExecute = true });
            MessageBox.Show(this, "SpaceLens is installed and ready to use.", "Installation complete", MessageBoxButtons.OK, MessageBoxIcon.Information); Close();
        }
        catch (Exception ex) { state.Text = "Installation failed."; MessageBox.Show(this, ex.Message, "Could not install SpaceLens", MessageBoxButtons.OK, MessageBoxIcon.Error); installButton.Enabled = true; }
        finally { progress.Visible = false; }
    }
}

internal static class SetupEngine
{
    internal static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SpaceLens");
    internal static string InstalledExecutable => Path.Combine(InstallDirectory, "SpaceLens.exe");
    private static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "SpaceLens.lnk");
    private static string StartMenuDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "SpaceLens");
    private static string StartMenuShortcut => Path.Combine(StartMenuDirectory, "SpaceLens.lnk");
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SpaceLens";

    internal static void Install(bool createDesktopShortcut)
    {
        EnsureNotRunning(); Directory.CreateDirectory(InstallDirectory); string staging = InstalledExecutable + ".installing", backup = InstalledExecutable + ".backup"; bool replaced = false;
        try
        {
            ExtractVerifiedPayload(staging);
            if (File.Exists(InstalledExecutable)) File.Move(InstalledExecutable, backup, true);
            File.Move(staging, InstalledExecutable, true); replaced = true;
            Shortcut.Create(StartMenuShortcut, InstalledExecutable, InstallDirectory, "Analyze disk space with SpaceLens");
            if (createDesktopShortcut) Shortcut.Create(DesktopShortcut, InstalledExecutable, InstallDirectory, "Analyze disk space with SpaceLens"); else TryDelete(DesktopShortcut);
            RegisterUninstaller(); TryDelete(backup);
        }
        catch
        {
            TryDelete(staging); if (replaced) TryDelete(InstalledExecutable); if (File.Exists(backup)) File.Move(backup, InstalledExecutable, true);
            if (File.Exists(InstalledExecutable)) { try { Shortcut.Create(StartMenuShortcut, InstalledExecutable, InstallDirectory, "Analyze disk space with SpaceLens"); if (createDesktopShortcut) Shortcut.Create(DesktopShortcut, InstalledExecutable, InstallDirectory, "Analyze disk space with SpaceLens"); } catch { } }
            else { TryDelete(StartMenuShortcut); TryDelete(DesktopShortcut); }
            throw;
        }
    }

    private static void EnsureNotRunning()
    {
        foreach (var process in Process.GetProcessesByName("SpaceLens"))
        {
            try { if (process.MainModule?.FileName is string path && Path.GetFullPath(path).Equals(Path.GetFullPath(InstalledExecutable), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("SpaceLens is currently running. Close it, then run Setup again."); }
            finally { process.Dispose(); }
        }
    }

    private static void ExtractVerifiedPayload(string destination)
    {
        TryDelete(destination); using Stream source = Resource("SpaceLens.Payload.exe"); using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.WriteThrough)) { source.CopyTo(output); output.Flush(true); }
        string expected; using (var reader = new StreamReader(Resource("SpaceLens.Payload.sha256"))) expected = reader.ReadToEnd().Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToUpperInvariant();
        using var input = File.OpenRead(destination); string actual = Convert.ToHexString(SHA256.HashData(input)); if (actual != expected) { TryDelete(destination); throw new InvalidDataException("The embedded SpaceLens application failed its integrity check."); }
        using var check = File.OpenRead(destination); if (check.ReadByte() != 'M' || check.ReadByte() != 'Z') throw new InvalidDataException("The embedded application is not a valid Windows executable.");
    }

    private static Stream Resource(string name) => Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new InvalidDataException($"Setup resource is missing: {name}");

    private static void RegisterUninstaller()
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey, true) ?? throw new InvalidOperationException("Could not create the uninstall entry."); var info = FileVersionInfo.GetVersionInfo(InstalledExecutable);
        key.SetValue("DisplayName", "SpaceLens"); key.SetValue("DisplayVersion", info.ProductVersion ?? "1.0.0"); key.SetValue("Publisher", "SpaceLens"); key.SetValue("DisplayIcon", $"\"{InstalledExecutable}\",0"); key.SetValue("InstallLocation", InstallDirectory); key.SetValue("UninstallString", $"\"{InstalledExecutable}\" --uninstall"); key.SetValue("QuietUninstallString", $"\"{InstalledExecutable}\" --uninstall --quiet"); key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd")); key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, new FileInfo(InstalledExecutable).Length / 1024), RegistryValueKind.DWord); key.SetValue("NoModify", 1, RegistryValueKind.DWord); key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    internal static bool SelfTest()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SpaceLens-Setup-Test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory); string payload = Path.Combine(directory, "SpaceLens.exe"); ExtractVerifiedPayload(payload); string shortcut = Path.Combine(directory, "SpaceLens.lnk"); Shortcut.Create(shortcut, payload, directory, "SpaceLens test");
            var process = Process.Start(new ProcessStartInfo(payload) { UseShellExecute = false, ArgumentList = { "--self-test" } }); if (process is null || !process.WaitForExit(20000) || process.ExitCode != 0) return false;
            string? shortcutTarget = Shortcut.ReadTarget(shortcut); var version = FileVersionInfo.GetVersionInfo(payload);
            using var form = new SetupForm(); if (form.Handle == IntPtr.Zero) return false;
            return File.Exists(payload) && new FileInfo(payload).Length > 1_000_000 && File.Exists(shortcut) && Path.GetFullPath(shortcutTarget ?? "").Equals(Path.GetFullPath(payload), StringComparison.OrdinalIgnoreCase) && version.ProductName == "SpaceLens" && Path.GetFullPath(InstallDirectory).StartsWith(Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
        finally { try { Directory.Delete(directory, true); } catch { } }
    }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    internal static void ScheduleSelfDelete(string self)
    {
        try
        {
            string full = Path.GetFullPath(self), temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), name = Path.GetFileName(full);
            if (!Path.GetDirectoryName(full)!.Equals(temp, StringComparison.OrdinalIgnoreCase) || !name.StartsWith("SpaceLens-Setup-", StringComparison.Ordinal) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
            string script = Path.Combine(temp, $"SpaceLens-Update-Cleanup-{Guid.NewGuid():N}.cmd"); File.WriteAllText(script, $"@echo off\r\nfor /L %%i in (1,1,20) do (\r\n  del /f /q \"{full}\" >nul 2>&1\r\n  if not exist \"{full}\" goto done\r\n  ping 127.0.0.1 -n 2 >nul\r\n)\r\n:done\r\ndel /f /q \"%~f0\"\r\n");
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }; start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c"); start.ArgumentList.Add(script); Process.Start(start);
        }
        catch { }
    }
}

internal static class Shortcut
{
    internal static void Create(string shortcutPath, string targetPath, string workingDirectory, string description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!); Type type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new PlatformNotSupportedException("Windows shortcut support is unavailable."); object shell = Activator.CreateInstance(type)!; object? link = null;
        try { dynamic dynamicShell = shell; link = dynamicShell.CreateShortcut(shortcutPath); dynamic shortcut = link; shortcut.TargetPath = targetPath; shortcut.WorkingDirectory = workingDirectory; shortcut.Description = description; shortcut.IconLocation = targetPath + ",0"; shortcut.Save(); }
        finally { if (link is not null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link); if (Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell); }
    }
    internal static string? ReadTarget(string shortcutPath)
    {
        Type type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new PlatformNotSupportedException("Windows shortcut support is unavailable."); object shell = Activator.CreateInstance(type)!; object? link = null;
        try { dynamic dynamicShell = shell; link = dynamicShell.CreateShortcut(shortcutPath); dynamic shortcut = link; return (string?)shortcut.TargetPath; }
        finally { if (link is not null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link); if (Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell); }
    }
}
