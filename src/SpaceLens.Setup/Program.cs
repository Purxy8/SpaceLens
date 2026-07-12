using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace SpaceLensSetup;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--self-test")) { ApplicationConfiguration.Initialize(); Environment.ExitCode = SetupEngine.SelfTest() ? 0 : 1; return; }
        int waitIndex = Array.IndexOf(args, "--wait-pid"); if (waitIndex >= 0 && waitIndex + 1 < args.Length && int.TryParse(args[waitIndex + 1], out int processId)) try { Process.GetProcessById(processId).WaitForExit(20000); } catch { }
        bool upgrade = args.Contains("--upgrade") || File.Exists(SetupEngine.InstalledExecutable); ApplicationConfiguration.Initialize(); Application.Run(new SetupForm(upgrade)); if (upgrade && Environment.ProcessPath is string self) SetupEngine.ScheduleSelfDelete(self);
    }
}

internal sealed class SetupForm : Form
{
    private readonly CheckBox desktopShortcut = new() { Text = "Create a Desktop shortcut", Checked = true, AutoSize = true };
    private readonly CheckBox launchAfter = new() { Text = "Launch SpaceLens after installation", Checked = true, AutoSize = true };
    private readonly CheckBox automaticUpdates = new() { Text = "Check for updates automatically (contacts GitHub at most once per day)", Checked = true, AutoSize = true };
    private readonly LinkLabel privacyPolicy = new() { Text = "Privacy information", AutoSize = true, LinkColor = Color.FromArgb(0, 102, 184) };
    private readonly Button installButton = new() { Text = "Install", Size = new Size(112, 38), BackColor = Color.FromArgb(0, 120, 215), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly Button cancelButton = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(96, 38) };
    private readonly ProgressBar progress = new() { Style = ProgressBarStyle.Marquee, Visible = false };
    private readonly Label state = new() { Text = "Ready to install", AutoSize = true, ForeColor = Color.DimGray };
    private bool installing;

    internal SetupForm(bool upgrade = false)
    {
        Text = upgrade ? "Update SpaceLens" : "Install SpaceLens"; ClientSize = new Size(590, 472); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 10); BackColor = Color.FromArgb(246, 248, 251);
        if (upgrade) desktopShortcut.Checked = SetupEngine.HasDesktopShortcut;
        automaticUpdates.Checked = SetupEngine.AutomaticUpdateChecksEnabled;
        var header = new Panel { Dock = DockStyle.Top, Height = 108, BackColor = Color.FromArgb(25, 33, 48) };
        header.Controls.Add(new Label { Text = "SpaceLens", Font = new Font("Segoe UI", 25, FontStyle.Bold), ForeColor = Color.White, AutoSize = true, Location = new Point(28, 20) });
        header.Controls.Add(new Label { Text = "Fast, friendly disk space analysis", ForeColor = Color.FromArgb(180, 195, 215), AutoSize = true, Location = new Point(31, 68) });
        var title = new Label { Text = upgrade ? $"Update SpaceLens to version {SetupEngine.SetupVersionText}" : "Install SpaceLens for this Windows account", Font = new Font("Segoe UI", 15, FontStyle.Bold), AutoSize = true, Location = new Point(30, 137) };
        var description = new Label { Text = upgrade ? "Your saved scans and preferences will be preserved.\nSpaceLens will replace only its installed application files." : "Setup creates a Start Menu entry, an Apps & Features uninstall entry,\nand—if selected—a Desktop shortcut. Administrator access is not required.", AutoSize = true, ForeColor = Color.FromArgb(70, 80, 95), Location = new Point(32, 177) };
        var path = new Label { Text = SetupEngine.InstallDirectory, AutoEllipsis = true, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White, Location = new Point(32, 229), Size = new Size(524, 29), Padding = new Padding(6, 4, 6, 4) };
        desktopShortcut.Location = new Point(33, 274); launchAfter.Location = new Point(33, 304); automaticUpdates.Location = new Point(33, 334); privacyPolicy.Location = new Point(34, 368);
        privacyPolicy.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(SetupEngine.PrivacyPage) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, $"Setup could not open the privacy page.\n\n{ex.Message}", "Could not open link", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        progress.Location = new Point(32, 410); progress.Size = new Size(280, 22); state.Location = new Point(32, 442);
        cancelButton.Location = new Point(348, 403); installButton.Text = upgrade ? "Update" : "Install"; installButton.Location = new Point(450, 403); installButton.Click += async (_, _) => await InstallAsync();
        FormClosing += (_, e) => { if (installing) e.Cancel = true; };
        Controls.AddRange([header, title, description, path, desktopShortcut, launchAfter, automaticUpdates, privacyPolicy, progress, state, cancelButton, installButton]); CancelButton = cancelButton; AcceptButton = installButton;
    }

    private async Task InstallAsync()
    {
        bool createDesktopShortcut = desktopShortcut.Checked, enableAutomaticUpdates = automaticUpdates.Checked; installing = true; installButton.Enabled = false; cancelButton.Enabled = false; desktopShortcut.Enabled = false; launchAfter.Enabled = false; automaticUpdates.Enabled = false; privacyPolicy.Enabled = false; progress.Visible = true; state.Text = "Verifying and installing SpaceLens…"; bool completed = false;
        try
        {
            await Task.Run(() => SetupEngine.Install(createDesktopShortcut, enableAutomaticUpdates)); state.Text = "Installation complete.";
            completed = true;
            if (launchAfter.Checked) try { Process.Start(new ProcessStartInfo(SetupEngine.InstalledExecutable) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(this, $"SpaceLens was installed, but Windows could not launch it.\n\n{ex.Message}", "Installed successfully", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        catch (Exception ex) { state.Text = "Installation failed."; MessageBox.Show(this, ex.Message, "Could not install SpaceLens", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally
        {
            installing = false; progress.Visible = false; cancelButton.Enabled = true; desktopShortcut.Enabled = true; launchAfter.Enabled = true; automaticUpdates.Enabled = true; privacyPolicy.Enabled = true; if (!completed) installButton.Enabled = true;
        }
        if (completed) { MessageBox.Show(this, "SpaceLens is installed and ready to use.", "Installation complete", MessageBoxButtons.OK, MessageBoxIcon.Information); Close(); }
    }
}

internal static class SetupEngine
{
    private const string InstallMutexName = "SpaceLens.Setup.Install";
    internal static Version SetupVersion { get; } = ReadSetupVersion();
    internal static string SetupVersionText => SetupVersion.ToString(3);
    internal static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SpaceLens");
    internal static string InstalledExecutable => Path.Combine(InstallDirectory, "SpaceLens.exe");
    internal const string PrivacyPage = "https://github.com/Purxy8/SpaceLens/blob/main/PRIVACY.md";
    private static string UpdateStatePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceLens", "update-state.json");
    private static readonly JsonSerializerOptions UpdateStateJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false };
    private static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "SpaceLens.lnk");
    private static string StartMenuDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "SpaceLens");
    private static string StartMenuShortcut => Path.Combine(StartMenuDirectory, "SpaceLens.lnk");
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SpaceLens";
    internal static bool HasDesktopShortcut => File.Exists(DesktopShortcut);
    internal static bool AutomaticUpdateChecksEnabled => ReadAutomaticUpdatePreference();

    internal static void Install(bool createDesktopShortcut, bool enableAutomaticUpdates)
    {
        using var installMutex = new Mutex(false, InstallMutexName); bool ownsMutex;
        try { ownsMutex = installMutex.WaitOne(0); } catch (AbandonedMutexException) { ownsMutex = true; }
        if (!ownsMutex) throw new InvalidOperationException("Another SpaceLens installation is already running. Finish or close it, then try again.");
        try
        {
            Directory.CreateDirectory(InstallDirectory); RecoverInterruptedInstall(); EnsureNotRunning();
            string staging = InstalledExecutable + ".installing", backup = InstalledExecutable + ".backup"; bool replaced = false;
            var desktopState = FileState.Capture(DesktopShortcut); var startMenuState = FileState.Capture(StartMenuShortcut); var updateState = FileState.Capture(UpdateStatePath); var registryState = UninstallRegistryState.Capture();
            try
            {
                ExtractVerifiedPayload(staging);
                if (TryReadSpaceLensVersion(InstalledExecutable, out var installedVersion) && installedVersion > SetupVersion) throw new InvalidOperationException($"A newer SpaceLens version ({installedVersion.ToString(3)}) is already installed. Setup {SetupVersionText} will not downgrade it.");
                if (File.Exists(InstalledExecutable)) File.Move(InstalledExecutable, backup, true);
                File.Move(staging, InstalledExecutable, true); replaced = true;
                Shortcut.Create(StartMenuShortcut, InstalledExecutable, InstallDirectory, "Analyze disk space with SpaceLens");
                if (createDesktopShortcut) Shortcut.Create(DesktopShortcut, InstalledExecutable, InstallDirectory, "Analyze disk space with SpaceLens"); else TryDelete(DesktopShortcut);
                RegisterUninstaller(); WriteAutomaticUpdatePreference(enableAutomaticUpdates); TryDelete(backup);
            }
            catch (Exception installError)
            {
                var rollbackErrors = new List<Exception>();
                TryRollback(() => { if (File.Exists(staging)) File.Delete(staging); }, rollbackErrors);
                TryRollback(() => { if (replaced && File.Exists(InstalledExecutable)) File.Delete(InstalledExecutable); }, rollbackErrors);
                TryRollback(() => { if (File.Exists(backup)) File.Move(backup, InstalledExecutable, true); }, rollbackErrors);
                TryRollback(() => startMenuState.Restore(StartMenuShortcut), rollbackErrors);
                TryRollback(() => desktopState.Restore(DesktopShortcut), rollbackErrors);
                TryRollback(() => updateState.Restore(UpdateStatePath), rollbackErrors);
                TryRollback(registryState.Restore, rollbackErrors);
                if (rollbackErrors.Count > 0) { rollbackErrors.Insert(0, installError); throw new AggregateException("SpaceLens installation failed and the previous installation could not be restored completely.", rollbackErrors); }
                throw;
            }
        }
        finally { installMutex.ReleaseMutex(); }
    }

    private static Version ReadSetupVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? throw new InvalidOperationException("Setup version metadata is missing.");
        if (version.Major < 0 || version.Minor < 0 || version.Build < 0 || version.Revision != 0) throw new InvalidOperationException("Setup version metadata must be a strict three-part version.");
        return new Version(version.Major, version.Minor, version.Build);
    }

    private sealed class UpdatePreference
    {
        public DateTimeOffset? LastAutomaticAttemptUtc { get; set; }
        public bool? AutomaticChecksEnabled { get; set; }
    }

    private static bool ReadAutomaticUpdatePreference()
    {
        if (!File.Exists(UpdateStatePath)) return true;
        try
        {
            using var file = new FileStream(UpdateStatePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length <= 0 || file.Length > 4096) return false;
            return JsonSerializer.Deserialize<UpdatePreference>(file, UpdateStateJsonOptions)?.AutomaticChecksEnabled == true;
        }
        catch { return false; }
    }

    private static void WriteAutomaticUpdatePreference(bool enabled)
    {
        UpdatePreference preference = new() { AutomaticChecksEnabled = enabled };
        try
        {
            using var file = new FileStream(UpdateStatePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length > 0 && file.Length <= 4096) preference.LastAutomaticAttemptUtc = JsonSerializer.Deserialize<UpdatePreference>(file, UpdateStateJsonOptions)?.LastAutomaticAttemptUtc;
        }
        catch { }
        string directory = Path.GetDirectoryName(UpdateStatePath)!;
        Directory.CreateDirectory(directory);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(preference, UpdateStateJsonOptions);
        if (json.Length > 4096) throw new InvalidDataException("The update preference file is too large.");
        string temp = Path.Combine(directory, $".update-state-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { output.Write(json); output.Flush(true); }
            File.Move(temp, UpdateStatePath, true);
        }
        finally { TryDelete(temp); }
    }

    private static void RecoverInterruptedInstall()
    {
        string staging = InstalledExecutable + ".installing", backup = InstalledExecutable + ".backup"; TryDelete(staging);
        if (!File.Exists(backup)) return;
        bool installedIsValid = TryReadSpaceLensVersion(InstalledExecutable, out _), backupIsValid = TryReadSpaceLensVersion(backup, out _);
        if (installedIsValid) { File.Delete(backup); return; }
        if (!backupIsValid) throw new InvalidDataException("Setup found an interrupted installation, but its recovery backup is not a valid SpaceLens application.");
        if (File.Exists(InstalledExecutable)) File.Delete(InstalledExecutable);
        File.Move(backup, InstalledExecutable, true);
    }

    private static bool TryReadSpaceLensVersion(string path, out Version version)
    {
        version = new Version(0, 0, 0); if (!File.Exists(path)) return false;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!string.Equals(info.ProductName, "SpaceLens", StringComparison.Ordinal) || info.FileMajorPart < 0 || info.FileMinorPart < 0 || info.FileBuildPart < 0 || info.FilePrivatePart != 0) return false;
            version = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart); return true;
        }
        catch { return false; }
    }

    private static Version ValidatePayloadIdentity(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        if (!string.Equals(info.ProductName, "SpaceLens", StringComparison.Ordinal)) throw new InvalidDataException("The embedded application product identity is invalid.");
        if (!TryReadSpaceLensVersion(path, out var payloadVersion)) throw new InvalidDataException("The embedded application version is not a strict three-part SpaceLens version.");
        if (payloadVersion != SetupVersion) throw new InvalidDataException($"Setup {SetupVersionText} contains SpaceLens {payloadVersion.ToString(3)}. The payload and Setup versions must match exactly.");
        return payloadVersion;
    }

    private static void TryRollback(Action action, List<Exception> errors)
    {
        try { action(); } catch (Exception ex) { errors.Add(ex); }
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
        ValidatePayloadIdentity(destination);
    }

    private static Stream Resource(string name) => Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new InvalidDataException($"Setup resource is missing: {name}");

    private static void RegisterUninstaller()
    {
        ValidatePayloadIdentity(InstalledExecutable); Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); using var key = Registry.CurrentUser.CreateSubKey(UninstallKey, true) ?? throw new InvalidOperationException("Could not create the uninstall entry.");
        key.SetValue("DisplayName", "SpaceLens"); key.SetValue("DisplayVersion", SetupVersionText); key.SetValue("Publisher", "SpaceLens"); key.SetValue("DisplayIcon", $"\"{InstalledExecutable}\",0"); key.SetValue("InstallLocation", InstallDirectory); key.SetValue("UninstallString", $"\"{InstalledExecutable}\" --uninstall"); key.SetValue("QuietUninstallString", $"\"{InstalledExecutable}\" --uninstall --quiet"); key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)); key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, new FileInfo(InstalledExecutable).Length / 1024), RegistryValueKind.DWord); key.SetValue("NoModify", 1, RegistryValueKind.DWord); key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private sealed class FileState
    {
        private readonly bool existed; private readonly byte[]? contents;
        private FileState(bool existed, byte[]? contents) { this.existed = existed; this.contents = contents; }
        internal static FileState Capture(string path) => File.Exists(path) ? new(true, File.ReadAllBytes(path)) : new(false, null);
        internal void Restore(string path)
        {
            if (!existed) { if (File.Exists(path)) File.Delete(path); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, contents ?? throw new InvalidOperationException("The shortcut rollback data is missing."));
        }
    }

    private sealed class UninstallRegistryState
    {
        private readonly RegistryNode? root;
        private UninstallRegistryState(RegistryNode? root) => this.root = root;
        internal static UninstallRegistryState Capture() { using var key = Registry.CurrentUser.OpenSubKey(UninstallKey); return new(key is null ? null : RegistryNode.Capture(key)); }
        internal void Restore()
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); if (root is null) return;
            using var key = Registry.CurrentUser.CreateSubKey(UninstallKey, true) ?? throw new InvalidOperationException("Could not restore the previous uninstall entry."); root.Restore(key);
        }
    }

    private sealed class RegistryNode
    {
        private readonly List<RegistryValueState> values = []; private readonly Dictionary<string, RegistryNode> children = new(StringComparer.OrdinalIgnoreCase);
        internal static RegistryNode Capture(RegistryKey key)
        {
            var node = new RegistryNode();
            foreach (string name in key.GetValueNames())
            {
                object value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) ?? throw new InvalidDataException($"Registry value '{name}' could not be read.");
                node.values.Add(new(name, CloneRegistryValue(value), key.GetValueKind(name)));
            }
            foreach (string name in key.GetSubKeyNames()) { using var child = key.OpenSubKey(name) ?? throw new InvalidDataException($"Registry subkey '{name}' could not be read."); node.children.Add(name, Capture(child)); }
            return node;
        }
        internal void Restore(RegistryKey key)
        {
            foreach (var value in values) key.SetValue(value.Name, CloneRegistryValue(value.Value), value.Kind);
            foreach (var child in children) { using var keyChild = key.CreateSubKey(child.Key, true) ?? throw new InvalidOperationException($"Could not restore registry subkey '{child.Key}'."); child.Value.Restore(keyChild); }
        }
        private static object CloneRegistryValue(object value) => value switch { byte[] bytes => bytes.ToArray(), string[] strings => strings.ToArray(), _ => value };
    }

    private sealed record RegistryValueState(string Name, object Value, RegistryValueKind Kind);

    internal static bool SelfTest()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SpaceLens-Setup-Test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory); string payload = Path.Combine(directory, "SpaceLens.exe"); ExtractVerifiedPayload(payload); string shortcut = Path.Combine(directory, "SpaceLens.lnk"); Shortcut.Create(shortcut, payload, directory, "SpaceLens test");
            using var process = Process.Start(new ProcessStartInfo(payload) { UseShellExecute = false, ArgumentList = { "--self-test" } });
            if (process is null) return false;
            if (!process.WaitForExit(60000)) { try { process.Kill(true); process.WaitForExit(5000); } catch { } return false; }
            if (process.ExitCode != 0) return false;
            string? shortcutTarget = Shortcut.ReadTarget(shortcut); var payloadVersion = ValidatePayloadIdentity(payload);
            using var form = new SetupForm(true); if (form.Handle == IntPtr.Zero) return false;
            bool updateChoiceVisible = form.Controls.OfType<CheckBox>().Any(control => control.Text.StartsWith("Check for updates automatically", StringComparison.Ordinal));
            bool privacyLinkVisible = form.Controls.OfType<LinkLabel>().Any(control => control.Text == "Privacy information");
            bool layoutInsideClient = form.Controls.Cast<Control>().Where(control => control.Visible).All(control => form.ClientRectangle.Contains(control.Bounds));
            return File.Exists(payload) && new FileInfo(payload).Length > 1_000_000 && File.Exists(shortcut) && Path.GetFullPath(shortcutTarget ?? "").Equals(Path.GetFullPath(payload), StringComparison.OrdinalIgnoreCase) && payloadVersion == SetupVersion && SetupVersionText == payloadVersion.ToString(3) && Path.GetFullPath(InstallDirectory).StartsWith(Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), StringComparison.OrdinalIgnoreCase) && updateChoiceVisible && privacyLinkVisible && layoutInsideClient;
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
