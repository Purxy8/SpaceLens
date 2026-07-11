using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace DesktopOrganizer;

internal enum FileSafety : byte
{
    Unknown,
    Normal,
    Review,
    Protected
}

internal record FileItem(string Path, long DiskBytes, long LogicalBytes, DateTime Modified, string Category, DateTime Created = default, bool AllocationEstimated = false, uint VolumeSerial = 0, ulong FileIndex = 0, FileAttributes Attributes = 0, FileSafety Safety = FileSafety.Unknown)
{
    public string Name { get; } = System.IO.Path.GetFileName(Path);
    public string Extension { get; } = FileExtensions.For(Path);
    public bool IsVideo { get; } = MediaTypes.IsVideo(Path);
    public bool IsScreenshot { get; } = MediaTypes.IsScreenshot(Path);
}

internal static class FileExtensions
{
    private static readonly ConcurrentDictionary<string, string> Pool = new(StringComparer.OrdinalIgnoreCase);
    internal static string For(string path) { string value = System.IO.Path.GetExtension(path); if (string.IsNullOrEmpty(value)) return "(no extension)"; string normalized = value.ToLowerInvariant(); return normalized.Length <= 24 && Pool.Count < 4096 ? Pool.GetOrAdd(normalized, normalized) : normalized; }
}

internal static class MediaTypes
{
    internal static bool IsVideo(string path) => Path.GetExtension(path).ToUpperInvariant() is ".MP4" or ".MKV" or ".MOV" or ".AVI" or ".WEBM" or ".WMV" or ".M4V" or ".MPG" or ".MPEG" or ".3GP";
    internal static bool IsScreenshot(string path)
    {
        string extension = Path.GetExtension(path).ToUpperInvariant(); if (extension is not (".PNG" or ".JPG" or ".JPEG" or ".BMP" or ".WEBP" or ".GIF")) return false;
        string name = Path.GetFileNameWithoutExtension(path); return path.Contains("\\Screenshots\\", StringComparison.OrdinalIgnoreCase) || path.Contains("\\Captures\\", StringComparison.OrdinalIgnoreCase) || name.Contains("screenshot", StringComparison.OrdinalIgnoreCase) || name.Contains("screen shot", StringComparison.OrdinalIgnoreCase) || name.Contains("snipping", StringComparison.OrdinalIgnoreCase) || name.StartsWith("snip", StringComparison.OrdinalIgnoreCase) || name.StartsWith("capture", StringComparison.OrdinalIgnoreCase);
    }
}

internal readonly record struct ScanSummary(long DriveUsedBytes, int SkippedLocations, long ElapsedMilliseconds, bool FullAccess = false, long DriveUsedBytesAtStart = 0);
internal record ScanSnapshot(string Root, DateTime ScannedAt, List<FileItem> Files, ScanSummary Summary = default);
internal record TypeTotal(string Extension, int Count, long Bytes, int EstimatedCount = 0);
internal record CategoryTotal(long Bytes, int Count, int EstimatedCount = 0);
internal record ViewResult(List<FileItem> VisibleFiles, int FilteredCount, long FilteredBytes, long FilteredEstimatedBytes, long IndexedBytes, long EstimatedAllocationBytes, int EstimatedAllocationCount, int FilteredEstimatedAllocationCount, bool TypesTruncated, List<TypeTotal> Types, CategoryTotal ProtectedTotal, Dictionary<string, CategoryTotal>? Categories);

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        CrashLog.Initialize();
        if (ElevatedScanRunner.IsHelperCommand(args)) { Environment.ExitCode = ElevatedScanRunner.RunHelper(args); return; }
        if (args.Contains("--self-test")) { Environment.ExitCode = SelfTest.Run() ? 0 : 1; return; }
        int benchmarkIndex = Array.IndexOf(args, "--benchmark-scan"), reportIndex = Array.IndexOf(args, "--benchmark-report");
        if (benchmarkIndex >= 0 && reportIndex >= 0 && benchmarkIndex + 1 < args.Length && reportIndex + 1 < args.Length) { Environment.ExitCode = ScannerBenchmark.Run(args[benchmarkIndex + 1], args[reportIndex + 1]); return; }
        int manifestIndex = Array.IndexOf(args, "--verify-update-manifest"), installerIndex = Array.IndexOf(args, "--installer");
        if (manifestIndex >= 0 && installerIndex >= 0 && manifestIndex + 1 < args.Length && installerIndex + 1 < args.Length) { Environment.ExitCode = UpdateService.VerifyRelease(args[manifestIndex + 1], args[installerIndex + 1]); return; }
        if (args.Contains("--uninstall-helper")) { ApplicationConfiguration.Initialize(); InstallerLifecycle.RunUninstallHelper(args); return; }
        if (args.Contains("--uninstall")) { ApplicationConfiguration.Initialize(); InstallerLifecycle.BeginUninstall(args.Contains("--quiet")); return; }
        ApplicationConfiguration.Initialize();
        using var uiMutex = new Mutex(true, @"Local\SpaceLens.UI", out bool firstInstance);
        if (!firstInstance) { MessageBox.Show("SpaceLens is already open. Close the existing window before starting another instance.", "SpaceLens is already running", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        Application.Run(new AnalyzerForm());
    }
}

internal sealed class AnalyzerForm : Form
{
    private static readonly string[] CachePathMarkers = ["\\CACHE\\", "\\CACHES\\", "\\LOCALCACHE\\", "\\CODE CACHE\\", "\\GPUCACHE\\", "\\DXCACHE\\", "\\GLCACHE\\", "\\INETCACHE\\", "\\NV_CACHE\\", "\\TEMP\\", "\\TMP\\", "\\CRASHDUMPS\\", "\\CRASHREPORTS\\", "\\LOGS\\"];
    private static readonly string[] PersonalPathMarkers = ["\\DESKTOP\\", "\\DOCUMENTS\\", "\\PICTURES\\", "\\VIDEOS\\", "\\MUSIC\\", "\\ONEDRIVE\\"];
    private static readonly ConcurrentDictionary<string, SafetyRootSet> SafetyRoots = new(StringComparer.OrdinalIgnoreCase);
    private sealed record SafetyRootSet(string[] Protected, string[] Review);
    private readonly ComboBox location = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly ComboBox categoryFilter = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox mediaFilter = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ModernButton scanButton = MakeButton("Scan now", AppTheme.Primary, Color.White);
    private readonly ModernButton stopButton = MakeButton("Stop", Color.FromArgb(226, 233, 242), AppTheme.Text);
    private readonly ModernButton deleteButton = MakeButton("Recycle selected", Color.FromArgb(205, 53, 47), Color.White);
    private readonly ModernButton browseButton = MakeButton("Browse…", Color.FromArgb(226, 233, 242), AppTheme.Text);
    private readonly ModernButton updateButton = MakeButton("Check for updates", Color.FromArgb(49, 67, 91), Color.White);
    private readonly CheckBox fullAccessScan = new() { Text = "Full access scan (Administrator)", AutoSize = true, Checked = true, ForeColor = Color.FromArgb(72, 85, 105), Margin = new Padding(14, 1, 0, 0) };
    private readonly TextBox search = new() { PlaceholderText = "Search scanned files…", Dock = DockStyle.Fill };
    private readonly DataGridView filesGrid = MakeGrid();
    private readonly DataGridView typesGrid = MakeGrid();
    private readonly Label driveCapacity = Metric();
    private readonly Label driveUsed = Metric();
    private readonly Label driveFree = Metric();
    private readonly Label indexedSize = Metric();
    private readonly Label fileCount = Metric();
    private readonly Label unindexedSize = Metric();
    private readonly Label filterSummary = new() { AutoSize = true, ForeColor = Color.FromArgb(55, 65, 81), Margin = new Padding(0, 8, 14, 0) };
    private readonly ListView categoryList = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, BorderStyle = BorderStyle.None };
    private readonly ToolStripStatusLabel status = new("Ready. A saved scan will load automatically when available.") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel freshness = new() { ForeColor = Color.FromArgb(0, 102, 184) };
    private readonly ToolStripProgressBar progress = new() { Style = ProgressBarStyle.Marquee, Visible = false, Width = 140 };
    private readonly ActivityStrip activityStrip = new() { Dock = DockStyle.Top, Height = 4 };
    private readonly ToolTip tips = new() { AutoPopDelay = 12000, InitialDelay = 350, ReshowDelay = 100 };
    private readonly Font safetyFont = new("Segoe UI Semibold", 9.5f, FontStyle.Bold);
    private List<FileItem> allFiles = [];
    private List<FileItem> visibleFiles = [];
    private List<TypeTotal> visibleTypes = [];
    private long visibleTypesBytes;
    private Dictionary<string, CategoryTotal> categoryTotals = new(StringComparer.OrdinalIgnoreCase);
    private CategoryTotal protectedTotal = new(0, 0);
    private CancellationTokenSource? cancellation;
    private long liveIndexedBytes;
    private long liveEstimatedBytes;
    private int liveEstimatedCount;
    private DateTime? scannedAt;
    private string? sortColumn = "DiskSize";
    private bool sortAscending;
    private bool updatingCategories;
    private string activeCategory = "All files";
    private string activeMedia = "All types";
    private int cacheLoadGeneration;
    private readonly System.Windows.Forms.Timer searchTimer = new() { Interval = 275 };
    private CancellationTokenSource? viewCancellation;
    private int viewGeneration;
    private bool viewUpdating;
    private long indexedBytesTotal;
    private long estimatedBytesTotal;
    private long scanDriveUsedBytes;
    private long scanDriveUsedBytesAtStart;
    private int scanSkippedLocations;
    private long scanElapsedMilliseconds;
    private bool scanFullAccess;
    private bool cacheLoading;
    private CancellationTokenSource? cacheLoadCancellation;
    private string? resultsRoot;
    private readonly SemaphoreSlim cacheSaveGate = new(1, 1);
    private readonly ConcurrentDictionary<string, int> cacheSaveVersions = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? updateCancellation;
    private bool updateBusy;
    private bool recycleBusy;

    public AnalyzerForm()
    {
        Text = $"SpaceLens {UpdateService.CurrentVersionText} — Disk Space Analyzer";
        Size = new Size(1360, 840); MinimumSize = new Size(1024, 660); StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f); BackColor = AppTheme.Canvas; DoubleBuffered = true;
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady)) location.Items.Add(drive.RootDirectory.FullName);
        location.Text = ScanCache.LastLocation() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        categoryFilter.Items.AddRange(["All files", "Cleanup candidates", "Protected / important", "Downloads", "Temporary & caches", "Personal files", "Apps & games", "Other user data", "Windows & system", "Other"]);
        categoryFilter.SelectedIndex = 0;
        mediaFilter.Items.AddRange(["All types", "Screenshots & videos", "Videos only", "Screenshots only"]); mediaFilter.SelectedIndex = 0;
        categoryList.Columns.Add("Category", 148); categoryList.Columns.Add("Size", 78, HorizontalAlignment.Right); categoryList.Columns.Add("Files", 58, HorizontalAlignment.Right);
        categoryList.BackColor = Color.White; categoryList.ForeColor = AppTheme.Text; categoryList.Font = new Font("Segoe UI", 9.5f); categoryList.HeaderStyle = ColumnHeaderStyle.Nonclickable;

        filesGrid.Columns.Add("Name", "Name");
        filesGrid.Columns.Add("DiskSize", "Size on disk");
        filesGrid.Columns.Add("LogicalSize", "File size");
        filesGrid.Columns.Add("Category", "Category");
        filesGrid.Columns.Add("Safety", "Safety note");
        filesGrid.Columns.Add("Modified", "Modified");
        filesGrid.Columns.Add("Path", "Path");
        filesGrid.Columns.Add(new DataGridViewButtonColumn { Name = "Action", HeaderText = "", UseColumnTextForButtonValue = false, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 82 });
        filesGrid.Columns[0].FillWeight = 17; filesGrid.Columns[1].FillWeight = 9; filesGrid.Columns[2].FillWeight = 8; filesGrid.Columns[3].FillWeight = 11; filesGrid.Columns[4].FillWeight = 15; filesGrid.Columns[5].FillWeight = 11; filesGrid.Columns[6].FillWeight = 29;
        filesGrid.Columns[1].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight; filesGrid.Columns[2].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        filesGrid.VirtualMode = true; filesGrid.RowCount = 0;
        filesGrid.Columns[1].HeaderCell.ToolTipText = "Click to sort by real size on disk. The active category and search filters stay applied.";
        filesGrid.Columns[2].HeaderCell.ToolTipText = "Click to sort by logical file size. The active filters stay applied.";
        filesGrid.Columns[3].HeaderCell.ToolTipText = "This sorts rows by category. To filter, use the category panel on the left or the dropdown above.";
        foreach (DataGridViewColumn column in filesGrid.Columns) if (column.Name != "Action") column.SortMode = DataGridViewColumnSortMode.Programmatic;
        typesGrid.Columns.Add("Type", "File type"); typesGrid.Columns.Add("Files", "Files"); typesGrid.Columns.Add("DiskSize", "Size on disk"); typesGrid.Columns.Add("Percent", "% of shown size");
        typesGrid.Columns[0].FillWeight = 34; typesGrid.Columns[1].FillWeight = 18; typesGrid.Columns[2].FillWeight = 25; typesGrid.Columns[3].FillWeight = 23;
        foreach (DataGridViewColumn column in typesGrid.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
        typesGrid.VirtualMode = true; typesGrid.RowCount = 0;
        typesGrid.CellValueNeeded += (_, e) => { if (e.RowIndex < 0 || e.RowIndex >= visibleTypes.Count) return; TypeTotal item = visibleTypes[e.RowIndex]; e.Value = typesGrid.Columns[e.ColumnIndex].Name switch { "Type" => item.Extension, "Files" => item.Count, "DiskSize" => item.Bytes, "Percent" => visibleTypesBytes == 0 ? 0d : item.Bytes * 100.0 / visibleTypesBytes, _ => null }; };
        typesGrid.CellFormatting += (_, e) => { if (e.RowIndex >= 0 && e.Value is long bytes && typesGrid.Columns[e.ColumnIndex].Name == "DiskSize") { e.Value = FormatSize(bytes) + (e.RowIndex < visibleTypes.Count && visibleTypes[e.RowIndex].EstimatedCount > 0 ? "*" : ""); e.FormattingApplied = true; } else if (e.RowIndex >= 0 && e.Value is double percent && typesGrid.Columns[e.ColumnIndex].Name == "Percent") { e.Value = $"{percent:F1}%"; e.FormattingApplied = true; } };

        scanButton.Click += async (_, _) => await StartScanAsync(); stopButton.Click += (_, _) => cancellation?.Cancel(); deleteButton.Click += async (_, _) => await RecycleSelectedAsync();
        updateButton.AutoSize = false; updateButton.Size = new Size(154, 34); updateButton.Click += async (_, _) => { if (recycleBusy) return; if (updateButton.Tag is UpdateManifest manifest && cancellation is null && !cacheLoading) await UpdateService.OfferAndInstallAsync(this, manifest); else await CheckForUpdatesAsync(true); };
        search.TextChanged += (_, _) => { searchTimer.Stop(); if (!recycleBusy && cancellation is null && !cacheLoading) searchTimer.Start(); }; searchTimer.Tick += (_, _) => { searchTimer.Stop(); if (!recycleBusy && cancellation is null && !cacheLoading) PopulateResults(false); };
        categoryFilter.SelectedIndexChanged += (_, _) => { if (!recycleBusy && !updatingCategories) { activeCategory = categoryFilter.SelectedItem?.ToString() ?? "All files"; HighlightCategory(activeCategory); PopulateResults(false); } };
        mediaFilter.SelectedIndexChanged += (_, _) => { if (!recycleBusy) { activeMedia = mediaFilter.SelectedItem?.ToString() ?? "All types"; PopulateResults(false); } };
        categoryList.SelectedIndexChanged += (_, _) => { if (!recycleBusy && !updatingCategories && categoryList.SelectedItems.Count > 0 && categoryList.SelectedItems[0].Tag is string key) SelectCategory(key); };
        location.SelectionChangeCommitted += async (_, _) => await LoadCachedAsync(location.Text);
        location.TextChanged += (_, _) => InvalidateResultsForLocationChange();
        filesGrid.CellValueNeeded += (_, e) => { if (FileAt(e.RowIndex) is FileItem item) e.Value = filesGrid.Columns[e.ColumnIndex].Name switch { "Name" => item.Name, "DiskSize" => item.DiskBytes, "LogicalSize" => item.LogicalBytes, "Category" => item.Category, "Safety" => SafetyLabel(item), "Modified" => item.Modified, "Path" => item.Path, "Action" => EffectiveSafety(item) == FileSafety.Normal ? "Recycle" : "Review…", _ => null }; };
        filesGrid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && filesGrid.Columns[e.ColumnIndex].Name != "Action") ShowInExplorer(FileAt(e.RowIndex)); };
        filesGrid.CellContentClick += async (_, e) => { if (e.RowIndex >= 0 && filesGrid.Columns[e.ColumnIndex].Name == "Action" && FileAt(e.RowIndex) is FileItem item) await RecycleItemsAsync([item]); };
        filesGrid.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Delete) { await RecycleSelectedAsync(); e.Handled = true; } };
        filesGrid.CellMouseDown += (_, e) => { if (e.Button != MouseButtons.Right) return; filesGrid.ClearSelection(); if (e.RowIndex >= 0) { filesGrid.Rows[e.RowIndex].Selected = true; filesGrid.CurrentCell = filesGrid.Rows[e.RowIndex].Cells[Math.Max(0, e.ColumnIndex)]; } else filesGrid.CurrentCell = null; };
        filesGrid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0) return; string name = filesGrid.Columns[e.ColumnIndex].Name;
            FileItem? item = FileAt(e.RowIndex); FileSafety safety = item is null ? FileSafety.Normal : EffectiveSafety(item);
            e.CellStyle.ForeColor = safety switch { FileSafety.Protected => Color.FromArgb(150, 45, 35), FileSafety.Review => Color.FromArgb(142, 86, 15), _ => filesGrid.DefaultCellStyle.ForeColor };
            if (name == "Safety") e.CellStyle.Font = safety == FileSafety.Normal ? filesGrid.Font : safetyFont;
            if (e.Value is long bytes && name is "DiskSize" or "LogicalSize") { e.Value = name == "DiskSize" && item?.AllocationEstimated == true ? "≈ " + FormatSize(bytes) : FormatSize(bytes); e.FormattingApplied = true; }
            else if (e.Value is DateTime date && name == "Modified") { e.Value = date.ToString("g"); e.FormattingApplied = true; }
        };
        filesGrid.CellToolTipTextNeeded += (_, e) => { if (e.RowIndex >= 0 && FileAt(e.RowIndex) is FileItem item && EffectiveSafety(item) != FileSafety.Normal) e.ToolTipText = SafetyExplanation(item); };
        filesGrid.ColumnHeaderMouseClick += (_, e) => ChangeSort(filesGrid.Columns[e.ColumnIndex].Name);
        var menu = new ContextMenuStrip(); menu.Items.Add("Show in File Explorer", null, (_, _) => ShowInExplorer(CurrentItem())); menu.Items.Add("Move to Recycle Bin…", null, async (_, _) => await RecycleSelectedAsync()); menu.Opening += (_, e) => e.Cancel = CurrentItem() is null || recycleBusy || cancellation is not null || cacheLoading; filesGrid.ContextMenuStrip = menu;
        stopButton.Enabled = false; deleteButton.Enabled = false;

        var header = new GradientHeaderPanel { Dock = DockStyle.Top, Height = 84, Padding = new Padding(22, 12, 22, 10) };
        header.Controls.Add(new Label { Text = "SpaceLens", BackColor = Color.Transparent, ForeColor = Color.White, Font = new Font("Segoe UI", 21, FontStyle.Bold), AutoSize = true, Location = new Point(20, 7) });
        header.Controls.Add(new Label { Text = $"v{UpdateService.CurrentVersionText}", BackColor = Color.FromArgb(45, 103, 145), ForeColor = Color.FromArgb(221, 241, 255), Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold), AutoSize = true, Padding = new Padding(7, 3, 7, 3), Location = new Point(166, 17) });
        header.Controls.Add(new Label { Text = "Clear storage insights · safe review-first cleanup · decimal GB / MB units", BackColor = Color.Transparent, ForeColor = Color.FromArgb(190, 211, 230), AutoSize = true, Location = new Point(23, 49) });
        header.Controls.Add(updateButton); header.Resize += (_, _) => updateButton.Location = new Point(Math.Max(20, header.ClientSize.Width - updateButton.Width - 22), 24);

        var picker = new TableLayoutPanel { Dock = DockStyle.Top, Height = 82, BackColor = AppTheme.Canvas, Padding = new Padding(20, 9, 20, 7), ColumnCount = 7, RowCount = 2 };
        picker.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); picker.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); picker.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); picker.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); picker.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175)); picker.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175)); picker.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 235));
        picker.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); picker.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        browseButton.Click += (_, _) => Browse();
        var locationHeading = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty }; locationHeading.Controls.Add(SmallLabel("DRIVE OR FOLDER")); locationHeading.Controls.Add(fullAccessScan);
        picker.Controls.Add(locationHeading, 0, 0); picker.SetColumnSpan(locationHeading, 4); picker.Controls.Add(SmallLabel("CATEGORY (LOCATION / PURPOSE)"), 4, 0); picker.Controls.Add(SmallLabel("MEDIA FILTER"), 5, 0); picker.Controls.Add(SmallLabel("SEARCH"), 6, 0);
        picker.Controls.Add(location, 0, 1); picker.Controls.Add(browseButton, 1, 1); picker.Controls.Add(scanButton, 2, 1); picker.Controls.Add(stopButton, 3, 1); picker.Controls.Add(categoryFilter, 4, 1); picker.Controls.Add(mediaFilter, 5, 1); picker.Controls.Add(search, 6, 1);

        var metrics = new TableLayoutPanel { Dock = DockStyle.Top, Height = 116, BackColor = AppTheme.Canvas, Padding = new Padding(20, 9, 20, 8), ColumnCount = 6 };
        for (int i = 0; i < 6; i++) metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 16.66f));
        metrics.Controls.Add(Card("DRIVE CAPACITY", driveCapacity, AppTheme.Primary), 0, 0); metrics.Controls.Add(Card("DRIVE USED", driveUsed, AppTheme.Violet), 1, 0); metrics.Controls.Add(Card("DRIVE FREE", driveFree, AppTheme.Green), 2, 0); metrics.Controls.Add(Card("FILES ACCOUNTED", indexedSize, AppTheme.Teal), 3, 0); metrics.Controls.Add(Card("SYSTEM / PROTECTED", unindexedSize, AppTheme.Amber), 4, 0); metrics.Controls.Add(Card("FILES INDEXED", fileCount, Color.FromArgb(82, 105, 137)), 5, 0);

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(16, 6), Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold) };
        var filesTab = new TabPage("Largest files") { Padding = new Padding(8), BackColor = Color.White, UseVisualStyleBackColor = false }; filesTab.Controls.Add(filesGrid);
        var typesTab = new TabPage("File types") { Padding = new Padding(8), BackColor = Color.White, UseVisualStyleBackColor = false }; typesTab.Controls.Add(typesGrid); tabs.TabPages.Add(filesTab); tabs.TabPages.Add(typesTab);
        var categoryPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(9), BorderStyle = BorderStyle.FixedSingle };
        categoryPanel.Controls.Add(categoryList); categoryPanel.Controls.Add(new Label { Text = "CATEGORIES — CLICK TO FILTER", Dock = DockStyle.Top, Height = 27, Font = new Font("Segoe UI", 8, FontStyle.Bold), ForeColor = Color.FromArgb(80, 95, 115) });
        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterDistance = 315, SplitterWidth = 6, IsSplitterFixed = false, BackColor = AppTheme.Canvas }; split.Panel1.Padding = new Padding(0, 0, 8, 0); split.Panel1.Controls.Add(categoryPanel); split.Panel2.Controls.Add(tabs);
        var tabArea = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 5, 20, 12) }; tabArea.Controls.Add(split);

        var bottomActions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, BackColor = AppTheme.Canvas, Padding = new Padding(20, 5, 20, 5), FlowDirection = FlowDirection.RightToLeft };
        bottomActions.Controls.Add(deleteButton); bottomActions.Controls.Add(filterSummary);
        var statusStrip = new StatusStrip { BackColor = Color.White, SizingGrip = false, Items = { status, freshness, progress } };
        Controls.Add(tabArea); Controls.Add(bottomActions); Controls.Add(metrics); Controls.Add(picker); Controls.Add(activityStrip); Controls.Add(header); Controls.Add(statusStrip);
        tips.SetToolTip(unindexedSize, "Space Windows reports as used but does not expose as ordinary files: NTFS metadata, restore points, reserved storage, protected locations, alternate streams, and similar system storage.");
        tips.SetToolTip(indexedSize, "Allocated size represented by indexed files. Sparse and compressed files are measured physically and NTFS hard-link allocation is counted once. An asterisk means the total contains one or more explicit estimates.");
        tips.SetToolTip(categoryList, "Click a category to filter. Cleanup candidates combines Downloads with recognized temporary and cache files; review every file before recycling it. An asterisk marks categories containing one or more allocation estimates.");
        tips.SetToolTip(typesGrid, "File-type totals include measured allocation and clearly marked estimates. An asterisk marks a type containing one or more approximate allocations.");
        tips.SetToolTip(driveUsed, "For a current complete drive scan, this is the used-space snapshot captured with that scan. Run Scan now to refresh it.");
        tips.SetToolTip(driveFree, "For a current complete drive scan, this is the free-space snapshot captured with that scan. Run Scan now to refresh it.");
        tips.SetToolTip(metrics, "SpaceLens uses decimal display units: 1 KB = 1,000 bytes, 1 MB = 1,000,000 bytes, and 1 GB = 1,000,000,000 bytes.");
        tips.SetToolTip(fullAccessScan, "Uses a scan-only, short-lived Administrator helper with Windows backup privilege on local fixed drives. Cleanup stays unelevated, ownership and permissions never change, and filesystem-managed storage can still remain in System / protected.");
        UpdateDriveMetrics();
        Shown += async (_, _) => { await LoadCachedAsync(location.Text); if (UpdateService.AutomaticCheckIsDue()) _ = CheckForUpdatesAsync(false); };
        FormClosing += (_, e) => { if (recycleBusy) { e.Cancel = true; MessageBox.Show(this, "SpaceLens is still finishing the Recycle Bin operation and saving the updated scan. Please wait for it to finish before closing the app.", "Cleanup is still running", MessageBoxButtons.OK, MessageBoxIcon.Information); } };
        FormClosed += (_, _) => { cancellation?.Cancel(); viewCancellation?.Cancel(); cacheLoadCancellation?.Cancel(); updateCancellation?.Cancel(); searchTimer.Stop(); searchTimer.Dispose(); tips.Dispose(); safetyFont.Dispose(); };
    }

    private async Task LoadCachedAsync(string root)
    {
        if (recycleBusy || !Directory.Exists(root) || cancellation is not null) return;
        cacheLoadCancellation?.Cancel(); cacheLoadCancellation?.Dispose(); cacheLoadCancellation = new CancellationTokenSource(); CancellationToken cacheToken = cacheLoadCancellation.Token;
        int generation = Interlocked.Increment(ref cacheLoadGeneration); string requestedRoot = NormalizeLocation(root);
        cacheLoading = true; searchTimer.Stop(); ClearVisibleGrid(); SetCacheLoading(true); status.Text = "Looking for a saved scan…"; progress.Visible = true;
        try
        {
            var snapshot = await Task.Run(() => { var loaded = ScanCache.Load(root, cacheToken); if (loaded is not null) for (int i = 0; i < loaded.Files.Count; i++) { if ((i & 4095) == 0) cacheToken.ThrowIfCancellationRequested(); var item = loaded.Files[i]; string category = Classify(item.Path); loaded.Files[i] = item with { Category = category, Safety = ClassifySafety(item.Path, category, item.Attributes) }; } return loaded; }, cacheToken);
            if (generation != cacheLoadGeneration || cancellation is not null || NormalizeLocation(location.Text) != requestedRoot) return;
            if (snapshot is null) { allFiles.Clear(); resultsRoot = null; indexedBytesTotal = estimatedBytesTotal = scanDriveUsedBytes = scanDriveUsedBytesAtStart = scanElapsedMilliseconds = 0; scanSkippedLocations = 0; scanFullAccess = false; scannedAt = null; PopulateResults(); status.Text = "No saved scan for this location. Click Scan now."; freshness.Text = "NOT YET SCANNED"; }
            else
            {
                allFiles.Clear(); allFiles.AddRange(snapshot.Files);
                indexedBytesTotal = snapshot.Files.Where(item => !item.AllocationEstimated).Aggregate(0L, (total, item) => checked(total + item.DiskBytes));
                estimatedBytesTotal = snapshot.Files.Where(item => item.AllocationEstimated).Aggregate(0L, (total, item) => checked(total + item.DiskBytes));
                scanDriveUsedBytes = snapshot.Summary.DriveUsedBytes; scanDriveUsedBytesAtStart = snapshot.Summary.DriveUsedBytesAtStart; scanSkippedLocations = snapshot.Summary.SkippedLocations; scanElapsedMilliseconds = snapshot.Summary.ElapsedMilliseconds; scanFullAccess = snapshot.Summary.FullAccess;
                resultsRoot = Path.GetFullPath(snapshot.Root); scannedAt = snapshot.ScannedAt; PopulateResults(); status.Text = snapshot.Summary.DriveUsedBytes > 0 ? $"Showing saved {(scanFullAccess ? "full-access " : "")}results from {snapshot.ScannedAt:g}. Click Scan now to refresh." : $"Showing a compatible older saved scan from {snapshot.ScannedAt:g}. Run Scan now once to calculate system/protected storage."; freshness.Text = $"SAVED {(scanFullAccess ? "FULL ACCESS · " : "")}SCAN: {Age(snapshot.ScannedAt)}";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (generation == cacheLoadGeneration) { allFiles.Clear(); resultsRoot = null; indexedBytesTotal = estimatedBytesTotal = scanDriveUsedBytes = scanDriveUsedBytesAtStart = scanElapsedMilliseconds = 0; scanSkippedLocations = 0; scanFullAccess = false; scannedAt = null; PopulateResults(); status.Text = $"Saved scan could not be loaded: {ex.Message}"; freshness.Text = "CACHE UNAVAILABLE"; } }
        finally { if (generation == cacheLoadGeneration) { cacheLoading = false; SetCacheLoading(false); progress.Visible = false; UpdateDriveMetrics(); } }
    }

    private async Task StartScanAsync()
    {
        if (recycleBusy) return;
        string root = location.Text.Trim();
        if (!Directory.Exists(root)) { MessageBox.Show(this, "Choose an existing drive or folder.", "Location not found", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        bool fullAccess = fullAccessScan.Checked;
        if (fullAccess && !ElevatedScanRunner.SupportsRoot(root, out string fullAccessReason))
        {
            MessageBox.Show(this, $"Full access scan is limited to ordinary folders on local fixed drives.\n\n{fullAccessReason}\n\nClear 'Full access scan (Administrator)' to run a standard scan of this location.", "Full access is unavailable for this location", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (fullAccess && !ElevatedScanRunner.IsAdministrator)
        {
            var answer = MessageBox.Show(this, "Full access scan starts a short-lived Administrator helper so Windows can expose more protected file entries. It does not change ownership, permissions, or Windows protection, and some filesystem-managed storage will still remain in System / protected.\n\nContinue to the Windows UAC prompt?", "Start full access scan", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (answer != DialogResult.Yes) return;
        }
        await ScanAsync(root, fullAccess);
    }

    private async Task ScanAsync(string root, bool fullAccess)
    {
        List<FileItem> previousFiles = allFiles; string? previousRoot = resultsRoot; DateTime? previousScannedAt = scannedAt; long previousDriveUsed = scanDriveUsedBytes; long previousDriveUsedAtStart = scanDriveUsedBytesAtStart; int previousSkipped = scanSkippedLocations; long previousElapsed = scanElapsedMilliseconds; bool previousFullAccess = scanFullAccess;
        long driveUsedAtStart = TryGetWholeDriveUsed(root);
        cacheLoadCancellation?.Cancel(); Interlocked.Increment(ref cacheLoadGeneration); cacheLoading = false; cancellation?.Dispose(); cancellation = new CancellationTokenSource(); InvalidateCacheSaves(root); allFiles = []; resultsRoot = null; ClearVisibleGrid(); categoryTotals.Clear(); protectedTotal = new(0, 0); indexedBytesTotal = estimatedBytesTotal = scanDriveUsedBytes = scanDriveUsedBytesAtStart = scanElapsedMilliseconds = 0; scanSkippedLocations = 0; scanFullAccess = false; filterSummary.Text = "Scanning — delete actions are disabled"; liveIndexedBytes = liveEstimatedBytes = 0; liveEstimatedCount = 0; scannedAt = null; searchTimer.Stop(); ScanCache.RememberLastLocation(root); SetScanning(true); UpdateDriveMetrics(); status.Text = $"{(fullAccess ? "Full access scan" : "Scanning")} {root}…"; freshness.Text = fullAccess ? "LIVE FULL ACCESS SCAN" : "LIVE SCAN";
        var scanTimer = System.Diagnostics.Stopwatch.StartNew(); bool receivedProgress = false;
        var reporter = new Progress<(List<FileItem> Batch, int Skipped)>(update =>
        {
            receivedProgress |= update.Batch.Count > 0; allFiles.AddRange(update.Batch); liveIndexedBytes += update.Batch.Where(f => !f.AllocationEstimated).Sum(f => f.DiskBytes); liveEstimatedBytes += update.Batch.Where(f => f.AllocationEstimated).Sum(f => f.DiskBytes); liveEstimatedCount += update.Batch.Count(f => f.AllocationEstimated); fileCount.Text = allFiles.Count.ToString("N0"); indexedSize.Text = FormatSize(checked(liveIndexedBytes + liveEstimatedBytes)) + (liveEstimatedCount > 0 ? "*" : "");
            double seconds = Math.Max(0.001, scanTimer.Elapsed.TotalSeconds); long rate = (long)(allFiles.Count / seconds);
            status.Text = $"{(fullAccess ? "Full access scanning" : "Scanning")}… {allFiles.Count:N0} files · {rate:N0} files/s · {update.Skipped:N0} inaccessible or linked locations skipped{(liveEstimatedCount == 0 ? "" : $" · {liveEstimatedCount:N0} allocation estimates")}";
        });
        bool completed = false;
        try
        {
            int skipped; bool backupPrivilege = false;
            if (fullAccess)
            {
                ElevatedScanResult result = await ElevatedScanRunner.ScanAsync(root, reporter, cancellation.Token); skipped = result.Skipped; backupPrivilege = result.BackupPrivilegeEnabled;
            }
            else skipped = await Task.Run(() => ScanFiles(root, reporter, cancellation.Token));
            scanTimer.Stop(); completed = true; resultsRoot = Path.GetFullPath(root); scannedAt = DateTime.Now; stopButton.Enabled = false;
            bool achievedFullAccess = fullAccess && backupPrivilege;
            scanDriveUsedBytes = TryGetWholeDriveUsed(root); scanDriveUsedBytesAtStart = driveUsedAtStart; scanSkippedLocations = skipped; scanElapsedMilliseconds = scanTimer.ElapsedMilliseconds; scanFullAccess = achievedFullAccess;
            long finalRate = (long)(allFiles.Count / Math.Max(0.001, scanTimer.Elapsed.TotalSeconds)); string privilegeNote = fullAccess && !backupPrivilege ? " Backup privilege was unavailable, so some protected locations may remain skipped." : "";
            status.Text = $"{(fullAccess ? "Full access scan" : "Scan")} complete: {allFiles.Count:N0} files · {finalRate:N0} files/s · {skipped:N0} inaccessible or linked locations skipped. Saving results…";
            var summary = new ScanSummary(scanDriveUsedBytes, skipped, scanElapsedMilliseconds, achievedFullAccess, driveUsedAtStart); var snapshot = new ScanSnapshot(root, scannedAt.Value, [.. allFiles], summary); await SaveCacheSnapshotAsync(snapshot, true);
            status.Text = $"{(achievedFullAccess ? "Full access scan" : "Scan")} complete and saved in {scanTimer.Elapsed.TotalSeconds:N1}s · {allFiles.Count:N0} files · {finalRate:N0} files/s · {skipped:N0} locations skipped.{privilegeNote}"; freshness.Text = achievedFullAccess ? "SAVED FULL ACCESS SCAN" : "SAVED JUST NOW";
        }
        catch (OperationCanceledException)
        {
            if (fullAccess && !receivedProgress) { allFiles = previousFiles; resultsRoot = previousRoot; scannedAt = previousScannedAt; scanDriveUsedBytes = previousDriveUsed; scanDriveUsedBytesAtStart = previousDriveUsedAtStart; scanSkippedLocations = previousSkipped; scanElapsedMilliseconds = previousElapsed; scanFullAccess = previousFullAccess; status.Text = "Full access scan was cancelled before it started. Previous results were preserved."; freshness.Text = previousScannedAt is null ? "NOT YET SCANNED" : "PREVIOUS RESULTS"; }
            else { status.Text = $"Scan stopped. Partial results are shown but were not saved ({allFiles.Count:N0} files)."; freshness.Text = "PARTIAL — NOT SAVED"; }
        }
        catch (Exception ex)
        {
            if (fullAccess && !receivedProgress) { allFiles = previousFiles; resultsRoot = previousRoot; scannedAt = previousScannedAt; scanDriveUsedBytes = previousDriveUsed; scanDriveUsedBytesAtStart = previousDriveUsedAtStart; scanSkippedLocations = previousSkipped; scanElapsedMilliseconds = previousElapsed; scanFullAccess = previousFullAccess; status.Text = $"Full access scan did not start: {ex.Message} Previous results were preserved."; freshness.Text = previousScannedAt is null ? "NOT YET SCANNED" : "PREVIOUS RESULTS"; }
            else { status.Text = $"Scan error: {ex.Message}"; freshness.Text = "SCAN INCOMPLETE"; }
        }
        finally { scanTimer.Stop(); if (!completed && allFiles.Count > 0 && resultsRoot is null) resultsRoot = Path.GetFullPath(root); cancellation?.Dispose(); cancellation = null; SetScanning(false); PopulateResults(); if (!completed) UpdateDriveMetrics(); }
    }

    internal static int ScanFiles(string root, IProgress<(List<FileItem>, int)> progress, CancellationToken token)
        => FastFileScanner.Scan(root, progress, token);

    private void PopulateResults(bool refreshCategories = true) => _ = PopulateResultsAsync(refreshCategories);

    private async Task PopulateResultsAsync(bool refreshCategories)
    {
        viewCancellation?.Cancel(); viewCancellation?.Dispose(); viewCancellation = new CancellationTokenSource(); CancellationToken token = viewCancellation.Token;
        int generation = Interlocked.Increment(ref viewGeneration); FileItem[] snapshot = [.. allFiles]; string category = activeCategory; string media = activeMedia; string text = search.Text.Trim(); string? column = sortColumn; bool ascending = sortAscending;
        viewUpdating = true; deleteButton.Enabled = false; filesGrid.ClearSelection(); filesGrid.Enabled = false; if (filesGrid.ContextMenuStrip is not null) filesGrid.ContextMenuStrip.Enabled = false; filterSummary.Text = $"Updating view… category: {category} · media: {media}";
        try
        {
            ViewResult result = await Task.Run(() => BuildView(snapshot, category, media, text, column, ascending, refreshCategories, token), token);
            if (token.IsCancellationRequested || generation != viewGeneration || IsDisposed) return;
            indexedBytesTotal = result.IndexedBytes; estimatedBytesTotal = result.EstimatedAllocationBytes; protectedTotal = result.ProtectedTotal; if (result.Categories is not null) categoryTotals = result.Categories;
            visibleFiles = result.VisibleFiles; filesGrid.ClearSelection(); filesGrid.RowCount = 0; filesGrid.RowCount = visibleFiles.Count; filesGrid.Invalidate();
            visibleTypes = result.Types; visibleTypesBytes = checked(result.FilteredBytes + result.FilteredEstimatedBytes); typesGrid.RowCount = 0; typesGrid.RowCount = visibleTypes.Count; typesGrid.Invalidate();
            indexedSize.Text = FormatSize(checked(result.IndexedBytes + result.EstimatedAllocationBytes)) + (result.EstimatedAllocationCount > 0 ? "*" : ""); fileCount.Text = snapshot.Length.ToString("N0");
            string displayNote = result.FilteredCount > result.VisibleFiles.Count ? $"{result.FilteredCount:N0} matching; displaying the first {result.VisibleFiles.Count:N0} for this sort" : $"Showing {result.FilteredCount:N0} files";
            string estimateNote = result.FilteredEstimatedAllocationCount == 0 ? "" : $" · {result.FilteredEstimatedAllocationCount:N0} estimated";
            string typeNote = result.TypesTruncated ? " · file-type list limited" : "";
            filterSummary.Text = $"{displayNote} · {FormatSize(checked(result.FilteredBytes + result.FilteredEstimatedBytes))} accounted{(result.FilteredEstimatedAllocationCount > 0 ? "*" : "")}{estimateNote} · category: {category} · media: {media}{(text.Length == 0 ? "" : $" · search: {text}")}{typeNote}";
            viewUpdating = false; filesGrid.Enabled = !recycleBusy && cancellation is null && !cacheLoading; if (filesGrid.ContextMenuStrip is not null) filesGrid.ContextMenuStrip.Enabled = filesGrid.Enabled; if (refreshCategories) PopulateCategoryList(category); UpdateSortGlyph(); deleteButton.Enabled = filesGrid.Enabled && result.FilteredCount > 0; UpdateDriveMetrics();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (generation == viewGeneration && !IsDisposed) { viewUpdating = false; filesGrid.Enabled = !recycleBusy && cancellation is null && !cacheLoading; if (filesGrid.ContextMenuStrip is not null) filesGrid.ContextMenuStrip.Enabled = filesGrid.Enabled; filterSummary.Text = "Could not update this view."; status.Text = $"View error: {ex.Message}"; } }
    }

    internal static bool MatchesCategory(FileItem item, string category) => category switch
    {
        "All files" => true,
        "Cleanup candidates" => item.Category is "Downloads" or "Temporary & caches",
        "Protected / important" => EffectiveSafety(item) != FileSafety.Normal,
        _ => item.Category == category
    };

    internal static bool MatchesMedia(FileItem item, string media)
    {
        if (media == "All types") return true;
        return media switch { "Screenshots & videos" => item.IsScreenshot || item.IsVideo, "Videos only" => item.IsVideo, "Screenshots only" => item.IsScreenshot, _ => true };
    }

    internal static ViewResult BuildView(IReadOnlyList<FileItem> source, string category, string media, string text, string? column, bool ascending, bool includeCategories, CancellationToken token)
    {
        const int visibleLimit = 10_000;
        var visibleQueue = new PriorityQueue<FileItem, FileItem>(new WorstFirstComparer(column, ascending));
        var typeTotals = new Dictionary<string, CategoryTotal>(StringComparer.OrdinalIgnoreCase); Dictionary<string, CategoryTotal>? categories = includeCategories ? new(StringComparer.OrdinalIgnoreCase) : null;
        long indexed = 0, estimatedBytes = 0, filteredBytes = 0, filteredEstimatedBytes = 0, protectedBytes = 0; int filteredCount = 0, estimatedCount = 0, filteredEstimatedCount = 0, protectedCount = 0, protectedEstimatedCount = 0;
        for (int i = 0; i < source.Count; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested(); FileItem item = source[i];
            if (item.AllocationEstimated) { estimatedCount++; estimatedBytes = checked(estimatedBytes + item.DiskBytes); } else indexed = checked(indexed + item.DiskBytes);
            if (categories is not null) AddTotal(categories, item.Category, item.DiskBytes, item.AllocationEstimated);
            if (EffectiveSafety(item) != FileSafety.Normal) { protectedBytes = checked(protectedBytes + item.DiskBytes); protectedCount++; if (item.AllocationEstimated) protectedEstimatedCount++; }
            if (!MatchesCategory(item, category) || !MatchesMedia(item, media) || (text.Length > 0 && !item.Path.Contains(text, StringComparison.OrdinalIgnoreCase))) continue;
            filteredCount++; if (item.AllocationEstimated) { filteredEstimatedCount++; filteredEstimatedBytes = checked(filteredEstimatedBytes + item.DiskBytes); } else filteredBytes = checked(filteredBytes + item.DiskBytes); AddTotal(typeTotals, item.Extension, item.DiskBytes, item.AllocationEstimated);
            if (visibleQueue.Count < visibleLimit) visibleQueue.Enqueue(item, item);
            else if (CompareFiles(item, visibleQueue.Peek(), column, ascending) < 0) { visibleQueue.Dequeue(); visibleQueue.Enqueue(item, item); }
        }
        token.ThrowIfCancellationRequested(); var visible = visibleQueue.UnorderedItems.Select(entry => entry.Element).ToList(); visible.Sort((left, right) => CompareFiles(left, right, column, ascending));
        bool typesTruncated = typeTotals.Count > 5000; var types = typeTotals.Select(pair => new TypeTotal(pair.Key, pair.Value.Count, pair.Value.Bytes, pair.Value.EstimatedCount)).OrderByDescending(item => item.Bytes).ThenBy(item => item.Extension, StringComparer.OrdinalIgnoreCase).Take(5000).ToList();
        return new(visible, filteredCount, filteredBytes, filteredEstimatedBytes, indexed, estimatedBytes, estimatedCount, filteredEstimatedCount, typesTruncated, types, new(protectedBytes, protectedCount, protectedEstimatedCount), categories);
    }

    private static void AddTotal(Dictionary<string, CategoryTotal> totals, string key, long bytes, bool estimated) { totals.TryGetValue(key, out var current); totals[key] = new(checked((current?.Bytes ?? 0) + bytes), (current?.Count ?? 0) + 1, (current?.EstimatedCount ?? 0) + (estimated ? 1 : 0)); }

    private static int CompareFiles(FileItem left, FileItem right, string? column, bool ascending)
    {
        int value = column switch { "Name" => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name), "LogicalSize" => left.LogicalBytes.CompareTo(right.LogicalBytes), "Category" => StringComparer.OrdinalIgnoreCase.Compare(left.Category, right.Category), "Safety" => EffectiveSafety(left).CompareTo(EffectiveSafety(right)), "Modified" => left.Modified.CompareTo(right.Modified), "Path" => StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path), _ => left.DiskBytes.CompareTo(right.DiskBytes) };
        if (!ascending) value = value < 0 ? 1 : value > 0 ? -1 : 0; return value != 0 ? value : StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path);
    }

    private sealed class WorstFirstComparer(string? column, bool ascending) : IComparer<FileItem>
    {
        public int Compare(FileItem? left, FileItem? right)
        {
            if (ReferenceEquals(left, right)) return 0; if (left is null) return 1; if (right is null) return -1;
            int value = CompareFiles(left, right, column, ascending); return value < 0 ? 1 : value > 0 ? -1 : 0;
        }
    }

    private void ChangeSort(string column)
    {
        if (column == "Action") return;
        if (sortColumn == column) sortAscending = !sortAscending;
        else { sortColumn = column; sortAscending = column is not ("DiskSize" or "LogicalSize" or "Modified"); }
        PopulateResults(false);
    }

    private void UpdateSortGlyph()
    {
        foreach (DataGridViewColumn column in filesGrid.Columns) column.HeaderCell.SortGlyphDirection = SortOrder.None;
        if (sortColumn is not null && filesGrid.Columns.Contains(sortColumn) && filesGrid.Columns[sortColumn] is DataGridViewColumn selected) selected.HeaderCell.SortGlyphDirection = sortAscending ? SortOrder.Ascending : SortOrder.Descending;
    }

    private void PopulateCategoryList(string selected)
    {
        updatingCategories = true;
        try
        {
            categoryList.BeginUpdate(); categoryList.Items.Clear();
            long allBytes = checked(indexedBytesTotal + estimatedBytesTotal); int allCount = allFiles.Count;
            foreach (string key in categoryFilter.Items.Cast<string>())
            {
                long bytes; int count, estimates;
                if (key == "All files") { bytes = allBytes; count = allCount; estimates = categoryTotals.Values.Sum(item => item.EstimatedCount); }
                else if (key == "Cleanup candidates")
                {
                    categoryTotals.TryGetValue("Downloads", out var downloads); categoryTotals.TryGetValue("Temporary & caches", out var caches); bytes = (downloads?.Bytes ?? 0) + (caches?.Bytes ?? 0); count = (downloads?.Count ?? 0) + (caches?.Count ?? 0); estimates = (downloads?.EstimatedCount ?? 0) + (caches?.EstimatedCount ?? 0);
                }
                else if (key == "Protected / important") { bytes = protectedTotal.Bytes; count = protectedTotal.Count; estimates = protectedTotal.EstimatedCount; }
                else if (categoryTotals.TryGetValue(key, out var summary)) { bytes = summary.Bytes; count = summary.Count; estimates = summary.EstimatedCount; }
                else { bytes = 0; count = 0; estimates = 0; }
                var row = new ListViewItem([key, FormatSize(bytes) + (estimates > 0 ? "*" : ""), count.ToString("N0")]) { Tag = key }; categoryList.Items.Add(row); if (key == selected) row.Selected = true;
            }
            categoryList.EndUpdate();
        }
        finally { updatingCategories = false; }
    }

    private void SelectCategory(string key)
    {
        int index = categoryFilter.Items.IndexOf(key); if (index < 0) return;
        activeCategory = key;
        if (categoryFilter.SelectedIndex == index) PopulateResults(false);
        else { updatingCategories = true; categoryFilter.SelectedIndex = index; updatingCategories = false; PopulateResults(false); }
    }

    private void HighlightCategory(string key)
    {
        updatingCategories = true;
        try { foreach (ListViewItem item in categoryList.Items) item.Selected = item.Tag is string value && value == key; }
        finally { updatingCategories = false; }
    }

    private async Task RecycleSelectedAsync()
    {
        if (recycleBusy || cancellation is not null || cacheLoading || viewUpdating || resultsRoot is null || NormalizeLocation(location.Text) != NormalizeLocation(resultsRoot)) return;
        var selected = filesGrid.SelectedRows.Cast<DataGridViewRow>().Select(r => FileAt(r.Index)).Where(f => f is not null).Cast<FileItem>().Distinct().ToList();
        if (selected.Count == 0 && CurrentItem() is FileItem current) selected.Add(current);
        if (selected.Count == 0) { MessageBox.Show(this, "Select one or more complete rows first.", "Nothing selected", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        await RecycleItemsAsync(selected);
    }

    private async Task RecycleItemsAsync(List<FileItem> selected)
    {
        if (recycleBusy || cancellation is not null || cacheLoading || viewUpdating || resultsRoot is null || NormalizeLocation(location.Text) != NormalizeLocation(resultsRoot)) return;
        List<FileItem> protectedItems = selected.Where(item => EffectiveSafety(item) == FileSafety.Protected).ToList(); List<FileItem> reviewItems = selected.Where(item => EffectiveSafety(item) == FileSafety.Review).ToList(); bool sensitive = protectedItems.Count > 0 || reviewItems.Count > 0;
        ReclaimEstimate reclaim = EstimateReclaim(selected);
        string warning = protectedItems.Count > 0 ? $"\n\nDANGER: {protectedItems.Count:N0} protected Windows/system file{(protectedItems.Count == 1 ? " is" : "s are")} selected. A typed confirmation will be required." : reviewItems.Count > 0 ? $"\n\nCAUTION: {reviewItems.Count:N0} application or user-state file{(reviewItems.Count == 1 ? " is" : "s are")} selected. Removing them may break software or remove settings." : "";
        var orderedExamples = protectedItems.Select(item => $"[PROTECTED] {item.Path}").Concat(reviewItems.Select(item => $"[IMPORTANT] {item.Path}")).Concat(selected.Where(item => EffectiveSafety(item) == FileSafety.Normal).Select(item => item.Path));
        string examples = string.Join("\n", orderedExamples.Take(6)); if (selected.Count > 6) examples += $"\n…and {selected.Count - 6} more";
        string sizeText = reclaim.Estimated ? $"up to approximately {FormatSize(reclaim.Bytes)} expected to be reclaimed" : $"approximately {FormatSize(reclaim.Bytes)} expected to be reclaimed";
        if (reclaim.SharedLinksRemain) sizeText += "; other hard links remain, so their shared data is not included";
        var answer = MessageBox.Show(this, $"Move {selected.Count} file{(selected.Count == 1 ? "" : "s")} ({sizeText}) to the Recycle Bin?{warning}\n\n{examples}\n\nSpace is reclaimed only after the Recycle Bin is emptied.", sensitive ? "Confirm potentially dangerous recycle" : "Confirm recycle", MessageBoxButtons.YesNo, sensitive ? MessageBoxIcon.Stop : MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes) return;
        if (protectedItems.Count > 0 && !ProtectedRecycleDialog.Confirm(this, protectedItems)) return;

        int removed = 0, stale = 0;
        var failures = new List<string>();
        var removedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SetRecycleBusy(true);
        try
        {
            for (int index = 0; index < selected.Count; index++)
            {
                FileItem item = selected[index];
                try
                {
                    if (!NativeFileState.TryGet(item.Path, out var current, out int error))
                    {
                        if (NativeFileState.IsMissingError(error)) { removedPaths.Add(item.Path); stale++; continue; }
                        failures.Add($"{item.Name}: could not be revalidated ({NativeFileState.ErrorMessage(error)}); not removed");
                        continue;
                    }
                    FileSafety currentSafety = ClassifySafety(item.Path, Classify(item.Path), current.Attributes);
                    if ((byte)currentSafety > (byte)EffectiveSafety(item)) { failures.Add($"{item.Name}: its safety level increased since the scan; rescan and confirm it again"); continue; }
                    if ((current.Attributes & FileAttributes.ReparsePoint) != 0 && IsFileLink(item.Path)) { failures.Add($"{item.Name}: symbolic links are not recycled by SpaceLens; skipped"); continue; }
                    if (current.LogicalBytes != item.LogicalBytes || current.Modified != item.Modified || (item.Created != default && current.Created != item.Created)) { failures.Add($"{item.Name}: changed since the scan; skipped"); continue; }
                    if (item.FileIndex != 0)
                    {
                        if (!NativeFileIdentity.TryGet(item.Path, false, out var identity) || identity.Id.VolumeSerial != item.VolumeSerial || identity.Id.FileIndex != item.FileIndex) { failures.Add($"{item.Name}: file identity changed since the scan; skipped"); continue; }
                    }

                    ShellRecycleService.Recycle(item.Path, Handle);
                    if (NativeFileState.TryGet(item.Path, out _, out int postDeleteError)) { failures.Add($"{item.Name}: Windows left the original file in place; it was not removed from the results"); continue; }
                    if (!NativeFileState.IsMissingError(postDeleteError)) { failures.Add($"{item.Name}: SpaceLens could not verify that Windows recycled it ({NativeFileState.ErrorMessage(postDeleteError)}); the result was retained"); continue; }
                    removedPaths.Add(item.Path);
                    removed++;
                }
                catch (Exception ex) { failures.Add($"{item.Name}: {ex.Message}"); }
                if ((index & 15) == 15)
                {
                    status.Text = $"Recycling files… {index + 1:N0} of {selected.Count:N0}";
                    await Task.Yield();
                }
            }

            if (removedPaths.Count > 0)
            {
                var affectedLinks = selected.Where(item => item.FileIndex != 0).Select(item => new NativeFileId(item.VolumeSerial, item.FileIndex)).ToHashSet();
                allFiles.RemoveAll(item => removedPaths.Contains(item.Path));
                RebalanceHardLinkAllocations(affectedLinks);
                scanDriveUsedBytes = scanDriveUsedBytesAtStart = 0;
                scanFullAccess = false;

                if (scannedAt is DateTime time && resultsRoot is string root)
                {
                    var snapshot = new ScanSnapshot(root, time, [.. allFiles], new ScanSummary(0, scanSkippedLocations, scanElapsedMilliseconds));
                    try { await SaveCacheSnapshotAsync(snapshot, true); }
                    catch (Exception ex)
                    {
                        bool discarded = true;
                        try { ScanCache.Delete(root); } catch { discarded = false; }
                        failures.Add(discarded ? $"The saved scan could not be updated and was discarded: {ex.Message}" : $"The saved scan could not be updated or discarded. Run a new scan before relying on cached results: {ex.Message}");
                    }
                }
            }

            await PopulateResultsAsync(true);
            status.Text = removedPaths.Count > 0 ? $"Moved {removed} file{(removed == 1 ? "" : "s")} to the Recycle Bin{(stale == 0 ? "" : $"; removed {stale} missing file record{(stale == 1 ? "" : "s")}")}. Run a new scan to refresh drive accounting." : "No selected files were recycled.";
            freshness.Text = removedPaths.Count > 0 ? "RESCAN AFTER RECYCLE" : freshness.Text;
        }
        catch (Exception ex)
        {
            failures.Add($"Cleanup could not finish: {ex.Message}");
            status.Text = "Cleanup stopped before it could finish.";
        }
        finally { SetRecycleBusy(false); }

        if (failures.Count > 0) MessageBox.Show(this, string.Join("\n", failures.Take(8)), "Some files were skipped", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    internal static ReclaimEstimate EstimateReclaim(List<FileItem> selected)
    {
        long bytes = 0; bool estimated = false, sharedLinksRemain = false; var identities = new Dictionary<NativeFileId, List<FileItem>>();
        foreach (var item in selected)
        {
            if (item.FileIndex == 0) { bytes = checked(bytes + item.DiskBytes); estimated |= item.AllocationEstimated; continue; }
            var id = new NativeFileId(item.VolumeSerial, item.FileIndex); if (!identities.TryGetValue(id, out var group)) { group = []; identities.Add(id, group); }
            group.Add(item);
        }
        foreach (var pair in identities)
        {
            FileItem? existing = null; NativeFileInformation current = default; bool identityUnavailable = false, anyExisting = false;
            foreach (var candidate in pair.Value)
            {
                if (!NativeFileState.TryGet(candidate.Path, out _, out int stateError)) { if (!NativeFileState.IsMissingError(stateError)) identityUnavailable = true; continue; }
                anyExisting = true;
                if (!NativeFileIdentity.TryGet(candidate.Path, false, out var candidateIdentity)) { identityUnavailable = true; continue; }
                if (candidateIdentity.Id == pair.Key) { existing = candidate; current = candidateIdentity; break; }
            }
            if (existing is null)
            {
                if (identityUnavailable) bytes = checked(bytes + pair.Value.Max(item => item.DiskBytes));
                estimated |= anyExisting; continue;
            }
            if (current.NumberOfLinks > (uint)pair.Value.Count) { sharedLinksRemain = true; continue; }
            long? measured = current.AllocatedBytes ?? NativeDiskSize.TryAllocatedBytes(existing.Path); bytes = checked(bytes + (measured ?? pair.Value.Max(item => item.LogicalBytes))); estimated |= measured is null;
        }
        return new(bytes, estimated, sharedLinksRemain);
    }

    internal readonly record struct ReclaimEstimate(long Bytes, bool Estimated, bool SharedLinksRemain);

    internal static FileSafety EffectiveSafety(FileItem item) => item.Safety != FileSafety.Unknown ? item.Safety : ClassifySafety(item.Path, item.Category, item.Attributes);

    internal static FileSafety ClassifySafety(string path, string category, FileAttributes attributes = 0)
    {
        try
        {
            string full = Path.GetFullPath(path).ToUpperInvariant(); string root = (Path.GetPathRoot(full) ?? "C:\\").TrimEnd('\\').ToUpperInvariant();
            SafetyRootSet safetyRoots = SafetyRoots.GetOrAdd(root, static value => new(
                [value + "\\WINDOWS", value + "\\RECOVERY", value + "\\BOOT", value + "\\EFI", value + "\\SYSTEM VOLUME INFORMATION", value + "\\$RECYCLE.BIN", value + "\\$EXTEND", value + "\\$WINDOWS.~BT", value + "\\$WINREAGENT"],
                [value + "\\PROGRAM FILES", value + "\\PROGRAM FILES (X86)", value + "\\PROGRAMDATA"]));
            if (IsUnderAny(full, safetyRoots.Protected)) return FileSafety.Protected;
            string name = Path.GetFileName(full);
            if (name is "PAGEFILE.SYS" or "HIBERFIL.SYS" or "SWAPFILE.SYS" or "BOOTMGR" or "BOOTNXT" or "BOOTSECT.BAK" || name.StartsWith("NTUSER.DAT", StringComparison.Ordinal) || name.StartsWith("USRCLASS.DAT", StringComparison.Ordinal)) return FileSafety.Protected;
            if ((attributes & FileAttributes.System) != 0 || category == "Windows & system") return FileSafety.Protected;
            if (IsUnderAny(full, safetyRoots.Review) || category is "Apps & games" or "Other user data" || full.Contains("\\APPDATA\\", StringComparison.Ordinal)) return FileSafety.Review;
            if ((attributes & FileAttributes.ReparsePoint) != 0) return FileSafety.Review;
            if (category is "Downloads" or "Temporary & caches") return FileSafety.Normal;
            return FileSafety.Normal;
        }
        catch { return category switch { "Windows & system" => FileSafety.Protected, "Apps & games" or "Other user data" => FileSafety.Review, _ => FileSafety.Normal }; }
    }

    internal static string SafetyLabel(FileItem item) => EffectiveSafety(item) switch
    {
        FileSafety.Protected => "PROTECTED — do not delete",
        FileSafety.Review => "IMPORTANT — review first",
        _ => "Standard file"
    };

    internal static string SafetyExplanation(FileItem item) => EffectiveSafety(item) switch
    {
        FileSafety.Protected => "Protected Windows or system file. Recycling it can prevent Windows from starting, updating, recovering, or working correctly. SpaceLens requires typed confirmation and Windows may still refuse the operation.",
        FileSafety.Review => "Application or user-state file. Recycling it may break installed software, remove settings, or lose application data. Review its exact path before continuing.",
        _ => "Standard file. Review the path and contents before recycling it."
    };

    private void RebalanceHardLinkAllocations(HashSet<NativeFileId> affected)
    {
        if (affected.Count == 0) return; var groups = new Dictionary<NativeFileId, List<int>>();
        for (int i = 0; i < allFiles.Count; i++)
        {
            var item = allFiles[i]; var id = new NativeFileId(item.VolumeSerial, item.FileIndex); if (!affected.Contains(id)) continue;
            if (!groups.TryGetValue(id, out var indices)) { indices = []; groups.Add(id, indices); }
            indices.Add(i);
        }
        foreach (var indices in groups.Values)
        {
            FileItem first = allFiles[indices[0]]; long? measured = NativeDiskSize.TryAllocatedBytes(first.Path); allFiles[indices[0]] = first with { DiskBytes = measured ?? first.LogicalBytes, AllocationEstimated = measured is null };
            for (int i = 1; i < indices.Count; i++) allFiles[indices[i]] = allFiles[indices[i]] with { DiskBytes = 0, AllocationEstimated = false };
        }
    }

    internal static bool IsSensitivePath(FileItem item)
        => EffectiveSafety(item) != FileSafety.Normal;

    private static bool IsFileLink(string path)
    {
        try { return new FileInfo(path).LinkTarget is not null; }
        catch { return true; }
    }

    private void UpdateDriveMetrics()
    {
        try
        {
            string full = Path.GetFullPath(location.Text).TrimEnd(Path.DirectorySeparatorChar); string root = (Path.GetPathRoot(full) ?? "").TrimEnd(Path.DirectorySeparatorChar); var drive = new DriveInfo(root + Path.DirectorySeparatorChar);
            long liveUsed = drive.TotalSize - drive.TotalFreeSpace; bool wholeDriveScan = string.Equals(full, root, StringComparison.OrdinalIgnoreCase) && allFiles.Count > 0 && scannedAt is not null; bool hasScanSnapshot = wholeDriveScan && scanDriveUsedBytes > 0;
            long usedAtScan = hasScanSnapshot ? scanDriveUsedBytes : liveUsed; long accounted = checked(indexedBytesTotal + estimatedBytesTotal);
            driveCapacity.Text = FormatSize(drive.TotalSize); driveFree.Text = FormatSize(Math.Max(0, drive.TotalSize - usedAtScan)); driveUsed.Text = FormatSize(usedAtScan);
            if (hasScanSnapshot)
            {
                long protectedBytes = CalculateProtectedBytes(usedAtScan, indexedBytesTotal, estimatedBytesTotal); unindexedSize.Text = FormatSize(protectedBytes);
                string estimates = estimatedBytesTotal == 0 ? "" : $" The accounted total includes {FormatSize(estimatedBytesTotal)} of explicit estimates.";
                string overlap = accounted <= usedAtScan ? "" : $" Files changed during the scan or estimates exceeded current allocation, producing a {FormatSize(accounted - usedAtScan)} temporary overlap.";
                string skipped = scanSkippedLocations == 0 ? "" : $" {scanSkippedLocations:N0} inaccessible or linked locations were skipped.";
                string changed = scanDriveUsedBytesAtStart > 0 && scanDriveUsedBytesAtStart != scanDriveUsedBytes ? $" Drive usage changed by {FormatSize(Math.Abs(scanDriveUsedBytes - scanDriveUsedBytesAtStart))} while the scan was running, so the residual is a best-effort reconciliation." : "";
                string mode = scanFullAccess ? " This was a full-access Administrator scan with expanded protected-location coverage." : " This was a standard scan; Full access scan may expose more protected file entries.";
                tips.SetToolTip(unindexedSize, $"Space used at scan time but not represented by ordinary indexed files: NTFS metadata, restore points, reserved storage, protected locations, alternate streams, and similar Windows storage.{mode}{skipped}{estimates}{changed}{overlap}");
            }
            else
            {
                unindexedSize.Text = wholeDriveScan ? "RESCAN" : "—";
                tips.SetToolTip(unindexedSize, wholeDriveScan ? "This saved scan predates system/protected snapshot accounting. Run Scan now once to calculate it accurately." : "System/protected storage is calculated only for a complete drive scan.");
            }
        }
        catch { driveCapacity.Text = driveUsed.Text = driveFree.Text = unindexedSize.Text = "—"; tips.SetToolTip(unindexedSize, "System/protected storage is unavailable until a complete drive scan succeeds."); }
    }

    private static long TryGetWholeDriveUsed(string locationPath)
    {
        try
        {
            string full = Path.GetFullPath(locationPath).TrimEnd(Path.DirectorySeparatorChar); string root = (Path.GetPathRoot(full) ?? "").TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return 0; var drive = new DriveInfo(root + Path.DirectorySeparatorChar); return checked(drive.TotalSize - drive.TotalFreeSpace);
        }
        catch { return 0; }
    }

    internal static long CalculateProtectedBytes(long driveUsedBytes, long knownFileBytes, long estimatedFileBytes)
    {
        if (driveUsedBytes <= 0) return 0; long accounted = checked(Math.Max(0, knownFileBytes) + Math.Max(0, estimatedFileBytes)); return Math.Max(0, driveUsedBytes - accounted);
    }

    internal static string Classify(string path)
    {
        string p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); string upper = p.ToUpperInvariant(); string root = (Path.GetPathRoot(p) ?? "C:\\").TrimEnd('\\').ToUpperInvariant();
        string fileName = Path.GetFileName(upper);
        if (IsUnder(upper, root + "\\WINDOWS") || upper.Contains("\\SYSTEM VOLUME INFORMATION\\") || upper.Contains("\\$RECYCLE.BIN\\") || fileName is "PAGEFILE.SYS" or "HIBERFIL.SYS" or "SWAPFILE.SYS") return "Windows & system";
        if (IsUnder(upper, KnownPaths.Downloads) || (upper.Contains("\\USERS\\") && upper.Contains("\\DOWNLOADS\\"))) return "Downloads";
        string extension = Path.GetExtension(upper);
        if (ContainsAny(upper, CachePathMarkers) || extension is ".TMP" or ".DMP" or ".LOG") return "Temporary & caches";
        if (IsUnder(upper, root + "\\PROGRAM FILES") || IsUnder(upper, root + "\\PROGRAM FILES (X86)") || IsUnder(upper, root + "\\PROGRAMDATA") || upper.Contains("\\APPDATA\\LOCAL\\PROGRAMS\\") || upper.Contains("\\STEAMAPPS\\") || upper.Contains("\\RIOT GAMES\\") || upper.Contains("\\GAMES\\")) return "Apps & games";
        if (ContainsAny(upper, PersonalPathMarkers)) return "Personal files";
        if (upper.Contains("\\APPDATA\\")) return "Other user data";
        if (upper.Contains("\\USERS\\")) return "Personal files"; return "Other";
    }

    private static bool IsUnder(string path, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        string normalized = folder.TrimEnd('\\');
        return path.Equals(normalized, StringComparison.OrdinalIgnoreCase) || (path.Length > normalized.Length && path[normalized.Length] == '\\' && path.StartsWith(normalized, StringComparison.OrdinalIgnoreCase));
    }
    private static bool IsUnderAny(string path, string[] folders) { foreach (string folder in folders) if (IsUnder(path, folder)) return true; return false; }
    private static bool ContainsAny(string value, string[] markers) { foreach (string marker in markers) if (value.Contains(marker, StringComparison.Ordinal)) return true; return false; }

    private FileItem? FileAt(int rowIndex) => rowIndex >= 0 && rowIndex < visibleFiles.Count ? visibleFiles[rowIndex] : null;
    private FileItem? CurrentItem() => filesGrid.CurrentRow is DataGridViewRow row ? FileAt(row.Index) : null;
    private void InvalidateResultsForLocationChange()
    {
        if (recycleBusy || cancellation is not null) return; string current = NormalizeLocation(location.Text);
        if (resultsRoot is not null && NormalizeLocation(resultsRoot) == current) return;
        cacheLoadCancellation?.Cancel(); Interlocked.Increment(ref cacheLoadGeneration); cacheLoading = false; SetCacheLoading(false); progress.Visible = false; allFiles.Clear(); resultsRoot = null; scannedAt = null; indexedBytesTotal = estimatedBytesTotal = scanDriveUsedBytes = scanDriveUsedBytesAtStart = scanElapsedMilliseconds = 0; scanSkippedLocations = 0; scanFullAccess = false; categoryTotals.Clear(); protectedTotal = new(0, 0); ClearVisibleGrid(); deleteButton.Enabled = false; freshness.Text = "LOCATION CHANGED"; status.Text = "Location changed. Choose a valid folder, then load or run a scan."; UpdateDriveMetrics();
    }
    private async Task CheckForUpdatesAsync(bool interactive)
    {
        if (recycleBusy || updateBusy || cancellation is not null || cacheLoading) return; updateBusy = true; updateButton.Enabled = false; string previousText = updateButton.Text; updateButton.Text = "Checking…"; updateButton.SetPalette(Color.FromArgb(49, 67, 91), Color.White);
        updateCancellation?.Cancel(); updateCancellation?.Dispose(); updateCancellation = new CancellationTokenSource(); if (!interactive) UpdateService.RecordAutomaticAttempt();
        try
        {
            UpdateManifest? manifest = await UpdateService.CheckAsync(updateCancellation.Token);
            if (manifest is null) { updateButton.Tag = null; updateButton.Text = "Up to date"; updateButton.SetPalette(Color.FromArgb(49, 67, 91), Color.White); if (interactive) MessageBox.Show(this, $"SpaceLens {UpdateService.CurrentVersionText} is up to date.", "No updates available", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            else
            {
                updateButton.Tag = manifest; updateButton.Text = $"Update {manifest.Version}"; updateButton.SetPalette(Color.FromArgb(20, 130, 85), Color.White);
                if (interactive && cancellation is null && !cacheLoading) await UpdateService.OfferAndInstallAsync(this, manifest);
            }
        }
        catch (OperationCanceledException) { updateButton.Text = previousText; }
        catch (Exception ex) { updateButton.Tag = null; updateButton.Text = "Check for updates"; updateButton.SetPalette(Color.FromArgb(49, 67, 91), Color.White); if (interactive) MessageBox.Show(this, $"SpaceLens could not check for updates.\n\n{ex.Message}\n\nReleases will be available at:\n{UpdateService.ReleasePage}", "Update check failed", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { updateBusy = false; updateButton.Enabled = !recycleBusy && cancellation is null && !cacheLoading; }
    }

    private void InvalidateCacheSaves(string root) => cacheSaveVersions.AddOrUpdate(NormalizeLocation(root), 1, (_, value) => unchecked(value + 1));

    private async Task SaveCacheSnapshotAsync(ScanSnapshot snapshot, bool throwOnError)
    {
        string key = NormalizeLocation(snapshot.Root); int generation = cacheSaveVersions.AddOrUpdate(key, 1, (_, value) => unchecked(value + 1)); updateButton.Enabled = false;
        try
        {
            await cacheSaveGate.WaitAsync();
            try
            {
                if (!cacheSaveVersions.TryGetValue(key, out int latest) || latest != generation) return;
                await Task.Run(() => ScanCache.Save(snapshot));
            }
            finally { cacheSaveGate.Release(); }
        }
        catch (Exception ex)
        {
            if (throwOnError) throw;
            try { if (!IsDisposed) status.Text = $"Files were recycled, but the saved scan could not be updated: {ex.Message}"; } catch { }
        }
        finally
        {
            if (cacheSaveVersions.TryGetValue(key, out int latest) && latest == generation)
                try { if (!IsDisposed) updateButton.Enabled = !recycleBusy && cancellation is null && !cacheLoading && !updateBusy; } catch { }
        }
    }

    private void ClearVisibleGrid() { viewCancellation?.Cancel(); Interlocked.Increment(ref viewGeneration); viewUpdating = false; visibleFiles = []; visibleTypes = []; visibleTypesBytes = 0; filesGrid.ClearSelection(); filesGrid.RowCount = 0; typesGrid.RowCount = 0; categoryList.Items.Clear(); }
    private void SetCacheLoading(bool loading) { bool idle = !loading && !recycleBusy; bool gridReady = idle && !viewUpdating; filesGrid.Enabled = gridReady; categoryList.Enabled = idle; categoryFilter.Enabled = idle; mediaFilter.Enabled = idle; search.Enabled = idle; fullAccessScan.Enabled = idle; updateButton.Enabled = idle && !updateBusy; deleteButton.Enabled = false; activityStrip.SetActive(loading || cancellation is not null || recycleBusy); if (filesGrid.ContextMenuStrip is not null) filesGrid.ContextMenuStrip.Enabled = gridReady; }
    private void SetScanning(bool scanning) { bool idle = !scanning && !recycleBusy; bool gridReady = idle && !viewUpdating; scanButton.Enabled = idle; stopButton.Enabled = scanning; deleteButton.Enabled = gridReady && allFiles.Count > 0; progress.Visible = scanning; categoryFilter.Enabled = idle; mediaFilter.Enabled = idle; categoryList.Enabled = idle; filesGrid.Enabled = gridReady; search.Enabled = idle; location.Enabled = idle; fullAccessScan.Enabled = idle; browseButton.Enabled = idle; updateButton.Enabled = idle && !updateBusy; activityStrip.SetActive(scanning || cacheLoading || recycleBusy); if (filesGrid.ContextMenuStrip is not null) filesGrid.ContextMenuStrip.Enabled = gridReady; }
    private void SetRecycleBusy(bool busy)
    {
        recycleBusy = busy;
        bool idle = !busy && cancellation is null && !cacheLoading;
        bool gridReady = idle && !viewUpdating;
        scanButton.Enabled = idle;
        stopButton.Enabled = cancellation is not null;
        location.Enabled = idle;
        browseButton.Enabled = idle;
        fullAccessScan.Enabled = idle;
        categoryFilter.Enabled = idle;
        mediaFilter.Enabled = idle;
        categoryList.Enabled = idle;
        search.Enabled = idle;
        updateButton.Enabled = idle && !updateBusy;
        filesGrid.Enabled = gridReady;
        deleteButton.Enabled = gridReady && allFiles.Count > 0;
        if (filesGrid.ContextMenuStrip is not null) filesGrid.ContextMenuStrip.Enabled = gridReady;
        activityStrip.SetActive(busy || cancellation is not null || cacheLoading);
    }
    private void Browse() { if (recycleBusy) return; using var dialog = new FolderBrowserDialog { Description = "Choose a drive or folder to scan", SelectedPath = location.Text, UseDescriptionForTitle = true, ShowNewFolderButton = false }; if (dialog.ShowDialog(this) == DialogResult.OK) { location.Text = dialog.SelectedPath; _ = LoadCachedAsync(dialog.SelectedPath); } }
    private static void ShowInExplorer(FileItem? file) { if (file is null) return; try { var start = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true }; start.ArgumentList.Add($"/select,{Path.GetFullPath(file.Path)}"); System.Diagnostics.Process.Start(start); } catch { } }
    private static string Age(DateTime value) { var age = DateTime.Now - value; return age.TotalMinutes < 2 ? "just now" : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago" : age.TotalDays < 1 ? $"{(int)age.TotalHours} hr ago" : value.ToString("g"); }
    private static string NormalizeLocation(string value) { try { return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant(); } catch { return value.Trim().TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant(); } }
    private static string FormatSize(long value) => ByteFormatter.Format(Math.Max(0, value));
    private static ModernButton MakeButton(string text, Color back, Color fore) { var button = new ModernButton { Text = text, AutoSize = true, Margin = new Padding(5, 0, 0, 0) }; button.SetPalette(back, fore); return button; }
    private static DataGridView MakeGrid() => new SmoothDataGridView
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = true,
        RowHeadersVisible = false,
        BackgroundColor = Color.White,
        BorderStyle = BorderStyle.None,
        CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
        GridColor = AppTheme.Border,
        EnableHeadersVisualStyles = false,
        ColumnHeadersHeight = 35,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(239, 244, 250), ForeColor = AppTheme.Text, Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold), SelectionBackColor = Color.FromArgb(239, 244, 250), SelectionForeColor = AppTheme.Text },
        DefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.White, ForeColor = AppTheme.Text, SelectionBackColor = Color.FromArgb(216, 236, 252), SelectionForeColor = Color.FromArgb(18, 43, 70), Padding = new Padding(3, 0, 3, 0) },
        AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(249, 251, 253), SelectionBackColor = Color.FromArgb(216, 236, 252), SelectionForeColor = Color.FromArgb(18, 43, 70) },
        RowTemplate = { Height = 30 }
    };
    private static Label Metric() => new() { Text = "—", Dock = DockStyle.Fill, BackColor = Color.Transparent, Font = new Font("Segoe UI Semibold", 16.5f, FontStyle.Bold), ForeColor = AppTheme.Text, TextAlign = ContentAlignment.MiddleLeft };
    private static Label SmallLabel(string text) => new() { Text = text, Dock = DockStyle.Fill, AutoSize = true, Font = new Font("Segoe UI Semibold", 8f, FontStyle.Bold), ForeColor = AppTheme.Muted };
    private static Control Card(string captionText, Label value, Color accent) => new MetricCardPanel(captionText, value, accent);
}

internal static class KnownPaths
{
    public static string Downloads { get; } = ResolveDownloads();
    private static string ResolveDownloads()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            var raw = key?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") as string;
            if (!string.IsNullOrWhiteSpace(raw)) return Path.GetFullPath(Environment.ExpandEnvironmentVariables(raw)).TrimEnd('\\').ToUpperInvariant();
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads").TrimEnd('\\').ToUpperInvariant();
    }
}

internal readonly record struct NativeFileId(uint VolumeSerial, ulong FileIndex);
internal readonly record struct NativeFileInformation(NativeFileId Id, uint NumberOfLinks, long? AllocatedBytes);
internal readonly record struct NativeFileSnapshot(FileAttributes Attributes, long LogicalBytes, DateTime Modified, DateTime Created);

internal static class NativePath
{
    internal static string For(string path)
    {
        string full = Path.GetFullPath(path); if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }
}

internal static class NativeFileIdentity
{
    private const uint FileFlagBackupSemantics = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal uint CreationTimeLow; internal uint CreationTimeHigh;
        internal uint LastAccessTimeLow; internal uint LastAccessTimeHigh;
        internal uint LastWriteTimeLow; internal uint LastWriteTimeHigh;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh; internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh; internal uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInformation
    {
        internal long AllocationSize;
        internal long EndOfFile;
        internal uint NumberOfLinks;
        [MarshalAs(UnmanagedType.U1)] internal bool DeletePending;
        [MarshalAs(UnmanagedType.U1)] internal bool Directory;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes, FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out ByHandleFileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandleEx(Microsoft.Win32.SafeHandles.SafeFileHandle file, int informationClass, out FileStandardInformation information, uint bufferSize);

    internal static bool TryGet(string path, bool directory, out NativeFileInformation identity)
    {
        identity = default;
        try
        {
            using var handle = CreateFileW(NativePath.For(path), 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, directory ? FileFlagBackupSemantics : 0, IntPtr.Zero);
            if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) return false;
            ulong index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow; if (index == 0) return false;
            long? allocated = GetFileInformationByHandleEx(handle, 1, out var standard, (uint)Marshal.SizeOf<FileStandardInformation>()) && standard.AllocationSize >= 0 ? standard.AllocationSize : null;
            identity = new(new(info.VolumeSerialNumber, index), info.NumberOfLinks, allocated); return true;
        }
        catch { return false; }
    }
}

internal static class NativeFileState
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Win32FileAttributeData
    {
        internal FileAttributes FileAttributes;
        internal uint CreationTimeLow; internal uint CreationTimeHigh;
        internal uint LastAccessTimeLow; internal uint LastAccessTimeHigh;
        internal uint LastWriteTimeLow; internal uint LastWriteTimeHigh;
        internal uint FileSizeHigh; internal uint FileSizeLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileAttributesExW(string fileName, int informationLevel, out Win32FileAttributeData data);

    internal static bool TryGet(string path, out NativeFileSnapshot snapshot, out int error)
    {
        snapshot = default; error = 0;
        try
        {
            if (!GetFileAttributesExW(NativePath.For(path), 0, out var data)) { error = Marshal.GetLastPInvokeError(); return false; }
            long logical = ((long)data.FileSizeHigh << 32) | data.FileSizeLow;
            snapshot = new(data.FileAttributes, logical, ToDateTime(data.LastWriteTimeHigh, data.LastWriteTimeLow), ToDateTime(data.CreationTimeHigh, data.CreationTimeLow));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            error = ex is UnauthorizedAccessException or System.Security.SecurityException ? 5 : ex is IOException io && (io.HResult & 0xFFFF) is int nativeError && nativeError > 0 ? nativeError : 87;
            return false;
        }
    }

    internal static bool IsMissingError(int error) => error is 2 or 3;
    internal static string ErrorMessage(int error) => error == 0 ? "Windows did not expose the file" : new System.ComponentModel.Win32Exception(error).Message;

    private static DateTime ToDateTime(uint high, uint low)
    {
        long value = ((long)high << 32) | low;
        if (value <= 0) return default;
        try { return DateTime.FromFileTimeUtc(value).ToLocalTime(); }
        catch (ArgumentOutOfRangeException) { return default; }
    }
}

internal static class NativeDiskSize
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetCompressedFileSizeW(string fileName, out uint high);
    public static long? TryAllocatedBytes(string path)
    {
        string nativePath; try { nativePath = NativePath.For(path); } catch { return null; }
        Marshal.SetLastPInvokeError(0); uint low = GetCompressedFileSizeW(nativePath, out uint high); int error = Marshal.GetLastPInvokeError();
        if (low == uint.MaxValue && error != 0) return null; return ((long)high << 32) | low;
    }
}

internal static class ScanCache
{
    private const int Version = 5;
    private const int PreviousVersion = 4;
    private const int LegacyVersion = 3;
    private const int MaximumFileRecords = 10_000_000;
    private const int MaximumPathBytes = 128 * 1024;
    private const int MaximumCategoryBytes = 2 * 1024;
    private static readonly object Sync = new();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static string DirectoryName => Environment.GetEnvironmentVariable("SPACELENS_CACHE_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceLens");
    private static string LastPath => Path.Combine(DirectoryName, "last-location.txt");
    public static void RememberLastLocation(string root) { try { Directory.CreateDirectory(DirectoryName); File.WriteAllText(LastPath, Path.GetFullPath(root)); } catch { } }
    public static string? LastLocation() { try { var value = File.ReadAllText(LastPath); return Directory.Exists(value) ? value : null; } catch { return null; } }
    private static string CachePath(string root) { var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())))[..20]; return Path.Combine(DirectoryName, $"scan-{key}.slc"); }
    public static void Save(ScanSnapshot snapshot) { lock (Sync) SaveCore(snapshot); }
    public static void Delete(string root) { lock (Sync) { string path = CachePath(root); if (File.Exists(path)) File.Delete(path); } }
    private static void SaveCore(ScanSnapshot snapshot)
    {
        Directory.CreateDirectory(DirectoryName); string storedRoot = Path.GetFullPath(snapshot.Root); string target = CachePath(storedRoot), temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            using (var writer = new BinaryWriter(gzip, Encoding.UTF8))
            {
                uint rootVolume = NativeFileIdentity.TryGet(storedRoot, true, out var rootIdentity) ? rootIdentity.Id.VolumeSerial : 0;
                if (snapshot.Summary.DriveUsedBytes < 0 || snapshot.Summary.DriveUsedBytesAtStart < 0 || snapshot.Summary.SkippedLocations < 0 || snapshot.Summary.ElapsedMilliseconds < 0) throw new InvalidDataException("Scan contains invalid summary information.");
                if (snapshot.Files.Count > MaximumFileRecords) throw new InvalidDataException($"Scan contains more than {MaximumFileRecords:N0} files and cannot be cached safely.");
                writer.Write(Version); WriteBoundedString(writer, storedRoot, MaximumPathBytes); writer.Write(rootVolume); writer.Write(snapshot.ScannedAt.ToBinary()); writer.Write(snapshot.Summary.DriveUsedBytes); writer.Write(snapshot.Summary.SkippedLocations); writer.Write(snapshot.Summary.ElapsedMilliseconds); writer.Write(snapshot.Summary.FullAccess); writer.Write(snapshot.Summary.DriveUsedBytesAtStart); writer.Write(snapshot.Files.Count);
                foreach (var item in snapshot.Files)
                {
                    if (item.DiskBytes < 0 || item.LogicalBytes < 0 || item.Path.Length > 32767 || item.Category.Length > 256 || !IsUnderRoot(item.Path, storedRoot)) throw new InvalidDataException("Scan contains an invalid file record.");
                    FileSafety safety = AnalyzerForm.EffectiveSafety(item);
                    WriteBoundedString(writer, Path.GetFullPath(item.Path), MaximumPathBytes); writer.Write(item.DiskBytes); writer.Write(item.LogicalBytes); writer.Write(item.Modified.ToBinary()); WriteBoundedString(writer, item.Category, MaximumCategoryBytes); writer.Write(item.Created.ToBinary()); writer.Write(item.AllocationEstimated); writer.Write(item.VolumeSerial); writer.Write(item.FileIndex); writer.Write((int)item.Attributes); writer.Write((byte)safety);
                }
            }
            File.Move(temp, target, true); RememberLastLocation(storedRoot);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }
    public static ScanSnapshot? Load(string root, CancellationToken token = default) { lock (Sync) { token.ThrowIfCancellationRequested(); return LoadCore(root, token); } }
    private static ScanSnapshot? LoadCore(string root, CancellationToken token)
    {
        string path = CachePath(root); if (!File.Exists(path)) return null;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1024 * 1024); using var gzip = new GZipStream(file, CompressionMode.Decompress); using var reader = new BinaryReader(gzip, Encoding.UTF8);
        int version = reader.ReadInt32(); if (version is not (Version or PreviousVersion or LegacyVersion)) return null; string storedRoot = Path.GetFullPath(version == Version ? ReadBoundedString(reader, MaximumPathBytes, false) : ReadBoundedString(reader, MaximumPathBytes, true)); if (!storedRoot.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) return null;
        uint storedVolume = reader.ReadUInt32(); if (storedVolume != 0 && (!NativeFileIdentity.TryGet(storedRoot, true, out var currentRoot) || currentRoot.Id.VolumeSerial != storedVolume)) return null;
        DateTime scanned = DateTime.FromBinary(reader.ReadInt64()); if (scanned < new DateTime(2000, 1, 1) || scanned > DateTime.Now.AddDays(1)) throw new InvalidDataException("Saved scan contains an invalid timestamp.");
        ScanSummary summary = version >= PreviousVersion ? ReadSummary(reader, version) : default;
        if (summary.DriveUsedBytes < 0 || summary.DriveUsedBytesAtStart < 0 || summary.SkippedLocations < 0 || summary.ElapsedMilliseconds < 0) throw new InvalidDataException("Saved scan contains invalid summary information.");
        int count = reader.ReadInt32(); if (count < 0 || count > MaximumFileRecords) throw new InvalidDataException("Saved scan contains an invalid file count.");
        long totalDisk = 0, totalLogical = 0; var files = new List<FileItem>(count);
        for (int i = 0; i < count; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested(); string itemPath = ReadBoundedString(reader, MaximumPathBytes, version != Version); long disk = reader.ReadInt64(), logical = reader.ReadInt64(); DateTime modified = DateTime.FromBinary(reader.ReadInt64()); string category = ReadBoundedString(reader, MaximumCategoryBytes, version != Version); DateTime created = DateTime.FromBinary(reader.ReadInt64()); bool estimated = reader.ReadBoolean(); uint volumeSerial = reader.ReadUInt32(); ulong fileIndex = reader.ReadUInt64(); FileAttributes attributes = version == Version ? (FileAttributes)reader.ReadInt32() : 0; FileSafety safety = version == Version ? (FileSafety)reader.ReadByte() : AnalyzerForm.ClassifySafety(itemPath, category, attributes);
            if (itemPath.Length > 32767 || category.Length > 256 || disk < 0 || logical < 0 || !IsUnderRoot(itemPath, storedRoot) || (volumeSerial != 0 && fileIndex == 0) || safety is not (FileSafety.Normal or FileSafety.Review or FileSafety.Protected)) throw new InvalidDataException("Saved scan contains an invalid record.");
            totalDisk = checked(totalDisk + disk); totalLogical = checked(totalLogical + logical); files.Add(new(Path.GetFullPath(itemPath), disk, logical, modified, category, created, estimated, volumeSerial, fileIndex, attributes, safety));
        }
        return new(storedRoot, scanned, files, summary);
    }

    private static ScanSummary ReadSummary(BinaryReader reader, int version)
    {
        long used = reader.ReadInt64(); int skipped = reader.ReadInt32(); long elapsed = reader.ReadInt64();
        return version == Version ? new(used, skipped, elapsed, reader.ReadBoolean(), reader.ReadInt64()) : new(used, skipped, elapsed);
    }

    private static void WriteBoundedString(BinaryWriter writer, string value, int maximumBytes)
    {
        byte[] bytes = StrictUtf8.GetBytes(value);
        if (bytes.Length > maximumBytes) throw new InvalidDataException("Scan contains a string that exceeds its safe storage limit.");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadBoundedString(BinaryReader reader, int maximumBytes, bool legacySevenBitLength)
    {
        int length = legacySevenBitLength ? reader.Read7BitEncodedInt() : reader.ReadInt32();
        if (length < 0 || length > maximumBytes) throw new InvalidDataException("Saved scan contains an oversized string.");
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException("Saved scan ended inside a string.");
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("Saved scan contains invalid text.", ex); }
    }

    private static bool IsUnderRoot(string path, string root)
    {
        try
        {
            string fullPath = Path.GetFullPath(path), fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

internal static class SelfTest
{
    public static bool Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "SpaceLens-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (!ByteFormatter.Validate()) return Fail("Decimal byte formatting failed");
            ElevatedScanProtocol.RunSelfTest();
            ShellRecycleService.ValidateAvailability();
            if (string.Equals(Environment.GetEnvironmentVariable("SPACELENS_RECYCLE_INTEGRATION_TEST"), "1", StringComparison.Ordinal)) ShellRecycleService.RunIntegrationSelfTest();
            var scannerRegression = ScannerRegressionTests.Run(); if (!scannerRegression.Passed) return Fail(scannerRegression.Failure!);
            Directory.CreateDirectory(Path.Combine(root, "Downloads")); string file = Path.Combine(root, "Downloads", "sample.bin"); File.WriteAllBytes(file, new byte[4096]);
            string hardLinkSource = Path.Combine(root, "hard-link-source.bin"), hardLinkAlias = Path.Combine(root, "hard-link-alias.bin"); File.WriteAllBytes(hardLinkSource, new byte[8192]); bool hardLinksSupported = CreateHardLinkW(hardLinkAlias, hardLinkSource, IntPtr.Zero);
            string longDirectory = root; while (longDirectory.Length < 285) longDirectory = Path.Combine(longDirectory, "long-path-segment"); Directory.CreateDirectory(longDirectory); string longFile = Path.Combine(longDirectory, "long-file.bin"); File.WriteAllBytes(longFile, new byte[128]);
            string sparseFile = Path.Combine(root, "sparse-test.bin"); bool sparseSupported; using (var stream = new FileStream(sparseFile, FileMode.Create, FileAccess.ReadWrite, FileShare.Read)) { sparseSupported = MakeSparse(stream.SafeFileHandle); stream.SetLength(64L * 1024 * 1024); stream.Flush(true); }
            if (sparseSupported && (NativeDiskSize.TryAllocatedBytes(sparseFile) is not long sparseBytes || sparseBytes >= 64L * 1024 * 1024)) return Fail("Sparse allocation measurement failed");
            if (AnalyzerForm.Classify(file) != "Downloads") return Fail("Downloads classification failed");
            string driveRoot = Path.GetPathRoot(root)!;
            if (AnalyzerForm.Classify(Path.Combine(driveRoot, "Users", "Sam", "AppData", "Local", "Spotify", "Cache", "data.bin")) != "Temporary & caches") return Fail("Cache classification failed");
            if (AnalyzerForm.Classify(Path.Combine(driveRoot, "Users", "Sam", "AppData", "Local", "Programs", "Example", "app.exe")) != "Apps & games") return Fail("App classification failed");
            if (AnalyzerForm.Classify(Path.Combine(driveRoot, "Windows", "System32", "kernel.dll")) != "Windows & system") return Fail("System classification failed");
            var system = new FileItem(Path.Combine(driveRoot, "Windows", "System32", "kernel.dll"), 100, 100, DateTime.Now, "Windows & system");
            if (!AnalyzerForm.MatchesCategory(system, "Windows & system") || AnalyzerForm.MatchesCategory(system, "Apps & games")) return Fail("System filter failed");
            if (AnalyzerForm.EffectiveSafety(system) != FileSafety.Protected || !AnalyzerForm.MatchesCategory(system, "Protected / important") || !AnalyzerForm.SafetyLabel(system).Contains("PROTECTED", StringComparison.Ordinal)) return Fail("Protected-file safety classification failed");
            var appState = new FileItem(Path.Combine(driveRoot, "Users", "Sam", "AppData", "Roaming", "Example", "settings.db"), 100, 100, DateTime.Now, "Other user data");
            if (AnalyzerForm.EffectiveSafety(appState) != FileSafety.Review || !AnalyzerForm.MatchesCategory(appState, "Protected / important")) return Fail("Important application-state safety classification failed");
            var recycleEntry = new FileItem(Path.Combine(driveRoot, "$Recycle.Bin", "S-1-5-21", "$R0001.bin"), 100, 100, DateTime.Now, "Windows & system");
            if (AnalyzerForm.EffectiveSafety(recycleEntry) != FileSafety.Protected) return Fail("Recycle Bin safety classification failed");
            var ordinaryDownload = new FileItem(Path.Combine(driveRoot, "Users", "Sam", "Downloads", "archive.zip"), 100, 100, DateTime.Now, "Downloads");
            if (AnalyzerForm.EffectiveSafety(ordinaryDownload) != FileSafety.Normal || AnalyzerForm.MatchesCategory(ordinaryDownload, "Protected / important")) return Fail("Standard download safety classification failed");
            var systemAttributeFile = new FileItem(Path.Combine(driveRoot, "Users", "Sam", "Desktop", "system-marked.bin"), 100, 100, DateTime.Now, "Personal files", Attributes: FileAttributes.System);
            if (AnalyzerForm.EffectiveSafety(systemAttributeFile) != FileSafety.Protected) return Fail("System-attribute safety classification failed");
            var programCache = new FileItem(Path.Combine(driveRoot, "Program Files", "Example", "Cache", "state.tmp"), 100, 100, DateTime.Now, "Temporary & caches");
            var programDataLog = new FileItem(Path.Combine(driveRoot, "ProgramData", "Example", "Logs", "service.log"), 100, 100, DateTime.Now, "Temporary & caches");
            if (AnalyzerForm.EffectiveSafety(programCache) != FileSafety.Review || AnalyzerForm.EffectiveSafety(programDataLog) != FileSafety.Review) return Fail("Application cache safety precedence failed");
            var hiveLog = new FileItem(Path.Combine(driveRoot, "Users", "Sam", "NTUSER.DAT.LOG1"), 100, 100, DateTime.Now, "Personal files");
            var usrClassLog = new FileItem(Path.Combine(driveRoot, "Users", "Sam", "AppData", "Local", "Microsoft", "Windows", "USRCLASS.DAT.LOG2"), 100, 100, DateTime.Now, "Other user data");
            if (AnalyzerForm.EffectiveSafety(hiveLog) != FileSafety.Protected || AnalyzerForm.EffectiveSafety(usrClassLog) != FileSafety.Protected) return Fail("User hive safety classification failed");
            var reparseDownload = new FileItem(Path.Combine(driveRoot, "Users", "Sam", "Downloads", "cloud.bin"), 0, 100, DateTime.Now, "Downloads", Attributes: FileAttributes.ReparsePoint);
            if (AnalyzerForm.EffectiveSafety(reparseDownload) != FileSafety.Review) return Fail("Reparse-point safety classification failed");
            var video = new FileItem(Path.Combine(driveRoot, "Users", "Sam", "Desktop", "recording.mp4"), 100, 100, DateTime.Now, "Personal files");
            if (!AnalyzerForm.MatchesMedia(video, "Screenshots & videos") || AnalyzerForm.MatchesMedia(video, "Screenshots only")) return Fail("Video filter failed");
            var screenshot = new FileItem(Path.Combine(driveRoot, "Users", "Sam", "Desktop", "Screenshot 2026-07-10.png"), 100, 100, DateTime.Now, "Personal files");
            if (!AnalyzerForm.MatchesMedia(screenshot, "Screenshots & videos") || !AnalyzerForm.MatchesMedia(screenshot, "Screenshots only")) return Fail("Screenshot filter failed");
            using (Stream fixture = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("SpaceLens.UpdateSelfTest.json") ?? throw new InvalidOperationException("Update fixture missing")) { using var memory = new MemoryStream(); fixture.CopyTo(memory); byte[] signedManifest = memory.ToArray(); var verified = UpdateService.ParseAndValidate(signedManifest); if (verified.Version != "9.9.9") return Fail("Update signature validation failed"); string tampered = Encoding.UTF8.GetString(signedManifest).Replace("signature self-test", "tampered self-test", StringComparison.Ordinal); try { UpdateService.ParseAndValidate(Encoding.UTF8.GetBytes(tampered)); return Fail("Tampered update manifest was accepted"); } catch (CryptographicException) { } }
            if (!SemanticVersion.TryParse("1.10.0", out var newer) || !SemanticVersion.TryParse("1.9.0", out var older) || newer.CompareTo(older) <= 0 || SemanticVersion.TryParse("01.1.0", out _) || SemanticVersion.TryParse("1.1.0-beta", out _)) return Fail("Semantic version validation failed");
            string fakeInstaller = Path.Combine(root, "fake-setup.exe"); File.WriteAllBytes(fakeInstaller, [0x4D, 0x5A, 1, 2, 3, 4]); var fakeManifest = new UpdateManifest { Version = "9.9.9", Tag = "v9.9.9", AssetName = "SpaceLens-Setup.exe", SizeBytes = new FileInfo(fakeInstaller).Length, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fakeInstaller))) }; UpdateService.VerifyInstallerFile(fakeInstaller, fakeManifest); if (UpdateService.BuildInstallerUrl(fakeManifest) != "https://github.com/Purxy8/SpaceLens/releases/download/v9.9.9/SpaceLens-Setup.exe") return Fail("Pinned update URL failed"); File.WriteAllBytes(fakeInstaller, [0x4D, 0x5A, 1, 2, 3, 5]); try { UpdateService.VerifyInstallerFile(fakeInstaller, fakeManifest); return Fail("Tampered installer was accepted"); } catch (CryptographicException) { }
            var scanned = new List<FileItem>(); AnalyzerForm.ScanFiles(root, new ImmediateProgress<(List<FileItem> Batch, int Skipped)>(update => scanned.AddRange(update.Batch)), CancellationToken.None); if (!scanned.Any(item => item.Path == file) || !scanned.Any(item => item.Path == longFile)) return Fail("Scanner traversal/long-path test failed");
            if (sparseSupported && scanned.First(item => item.Path == sparseFile) is FileItem sparseItem && (sparseItem.AllocationEstimated || sparseItem.DiskBytes >= sparseItem.LogicalBytes)) return Fail("Scanner sparse-allocation accounting failed");
            if (hardLinksSupported)
            {
                var linked = scanned.Where(item => item.Path == hardLinkSource || item.Path == hardLinkAlias).ToList();
                if (linked.Count != 2 || linked[0].FileIndex == 0 || linked[0].VolumeSerial != linked[1].VolumeSerial || linked[0].FileIndex != linked[1].FileIndex || linked.Sum(item => item.DiskBytes) != linked.Max(item => item.DiskBytes)) return Fail("Hard-link allocation deduplication failed");
                var oneLink = AnalyzerForm.EstimateReclaim([linked[0]]); var bothLinks = AnalyzerForm.EstimateReclaim(linked);
                if (oneLink.Bytes != 0 || !oneLink.SharedLinksRemain || bothLinks.Bytes <= 0 || bothLinks.SharedLinksRemain) return Fail("Hard-link reclaim estimate failed");
            }
            if (NativeFileIdentity.TryGet(file, false, out var sourceIdentity) && scanned.First(item => item.Path == file) is FileItem scannedFile && (scannedFile.VolumeSerial != sourceIdentity.Id.VolumeSerial || scannedFile.FileIndex != sourceIdentity.Id.FileIndex)) return Fail("Scanner file identity test failed");
            if (!NativeFileState.TryGet(file, out var fileState, out _) || fileState.LogicalBytes != 4096 || (fileState.Attributes & FileAttributes.Directory) != 0) return Fail("Native file-state validation failed");
            Marshal.SetLastPInvokeError(2);
            if (NativeFileState.TryGet("invalid\0path", out _, out int invalidPathError) || NativeFileState.IsMissingError(invalidPathError)) return Fail("Managed path errors were mistaken for missing files");
            var many = Enumerable.Range(0, 200_000).Select(i => new FileItem(Path.Combine(driveRoot, "Users", "Sam", i % 2 == 0 ? "Downloads" : "AppData", $"file-{i}.bin"), i + 1, i + 1, DateTime.UnixEpoch.AddSeconds(i), i % 2 == 0 ? "Downloads" : "Other user data")).ToList();
            var timer = System.Diagnostics.Stopwatch.StartNew(); var view = AnalyzerForm.BuildView(many, "Downloads", "All types", "", "DiskSize", false, true, CancellationToken.None); timer.Stop();
            if (view.FilteredCount != 100_000 || view.VisibleFiles.Count != 10_000 || view.VisibleFiles[0].DiskBytes != 199_999 || view.VisibleFiles[^1].DiskBytes != 180_001 || view.VisibleFiles.Any(item => item.Category != "Downloads") || view.Categories is null || timer.Elapsed > TimeSpan.FromSeconds(5)) return Fail($"View performance/filter test failed ({timer.ElapsedMilliseconds} ms)");
            var protectedView = AnalyzerForm.BuildView(many, "Protected / important", "All types", "", "DiskSize", false, true, CancellationToken.None);
            if (protectedView.FilteredCount != 100_000 || protectedView.ProtectedTotal.Count != 100_000 || protectedView.VisibleFiles.Any(item => AnalyzerForm.EffectiveSafety(item) == FileSafety.Normal)) return Fail("Protected-file filter failed");
            var estimate = new FileItem(Path.Combine(root, "estimated.bin"), 50_000, 50_000, DateTime.Now, "Other", AllocationEstimated: true); var estimatedView = AnalyzerForm.BuildView([new FileItem(file, 4096, 4096, DateTime.Now, "Downloads"), estimate], "All files", "All types", "", "DiskSize", false, true, CancellationToken.None);
            if (estimatedView.IndexedBytes != 4096 || estimatedView.FilteredBytes != 4096 || estimatedView.EstimatedAllocationBytes != 50_000 || estimatedView.FilteredEstimatedBytes != 50_000 || estimatedView.EstimatedAllocationCount != 1 || estimatedView.FilteredEstimatedAllocationCount != 1 || estimatedView.Categories?["Other"].Bytes != 50_000 || estimatedView.Types.Single(type => type.Extension == ".bin").Bytes != 54_096) return Fail("Estimated allocation accounting failed");
            if (AnalyzerForm.CalculateProtectedBytes(386_301_222_912, 310_727_223_365, 44_316_729_344) != 31_257_270_203) return Fail("Protected/system storage accounting failed");
            using (var canceled = new CancellationTokenSource()) { canceled.Cancel(); try { AnalyzerForm.BuildView(many, "All files", "All types", "", "DiskSize", false, true, canceled.Token); return Fail("View cancellation failed"); } catch (OperationCanceledException) { } }
            if (!AnalyzerForm.IsSensitivePath(system)) return Fail("Sensitive path protection failed");
            using (var confirmation = new ProtectedRecycleDialog([system])) if (confirmation.RequiredPhrase != "RECYCLE PROTECTED FILE") return Fail("Protected recycle confirmation failed");
            Environment.SetEnvironmentVariable("SPACELENS_CACHE_DIR", Path.Combine(root, "cache"));
            NativeFileIdentity.TryGet(file, false, out var cachedIdentity); var item = new FileItem(file, 4096, 4096, File.GetLastWriteTime(file), "Downloads", File.GetCreationTime(file), false, cachedIdentity.Id.VolumeSerial, cachedIdentity.Id.FileIndex, File.GetAttributes(file), FileSafety.Normal); var snapshot = new ScanSnapshot(root, DateTime.Now, [item], new ScanSummary(123_456, 7, 890, true)); ScanCache.Save(snapshot); var loaded = ScanCache.Load(root);
            if (loaded is null || loaded.Files.Count != 1 || loaded.Files[0].Path != file || loaded.Files[0].DiskBytes != 4096 || loaded.Files[0].Created != item.Created || loaded.Files[0].FileIndex != item.FileIndex || loaded.Files[0].Attributes != item.Attributes || loaded.Files[0].Safety != FileSafety.Normal || loaded.Summary != snapshot.Summary) return Fail("Cache round-trip failed");
            string legacyCachePath = Directory.GetFiles(Path.Combine(root, "cache"), "scan-*.slc").Single(); WriteLegacyV3Cache(legacyCachePath, snapshot); var legacyLoaded = ScanCache.Load(root);
            if (legacyLoaded is null || legacyLoaded.Files.Count != 1 || legacyLoaded.Files[0].Path != file || legacyLoaded.Summary != default) return Fail("Legacy v3 cache compatibility failed");
            WriteLegacyV4Cache(legacyCachePath, snapshot); var previousLoaded = ScanCache.Load(root);
            if (previousLoaded is null || previousLoaded.Files.Count != 1 || previousLoaded.Files[0].Path != file || previousLoaded.Summary.DriveUsedBytes != snapshot.Summary.DriveUsedBytes || previousLoaded.Summary.FullAccess) return Fail("Previous v4 cache compatibility failed");
            using (var canceled = new CancellationTokenSource()) { canceled.Cancel(); try { ScanCache.Load(root, canceled.Token); return Fail("Cache cancellation failed"); } catch (OperationCanceledException) { } }
            if (!Path.GetFullPath(InstallerLifecycle.InstallDirectory).StartsWith(Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), StringComparison.OrdinalIgnoreCase)) return Fail("Installer path validation failed");
            using (var form = new AnalyzerForm()) { if (form.Handle == IntPtr.Zero) return Fail("UI construction failed"); }
            return true;
        }
        catch (Exception ex) { return Fail(ex.ToString()); }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
    private static bool Fail(string message) { try { var path = Environment.GetEnvironmentVariable("SPACELENS_SELFTEST_LOG"); if (!string.IsNullOrEmpty(path)) File.WriteAllText(path, message); } catch { } return false; }
    private static void WriteLegacyV3Cache(string cachePath, ScanSnapshot snapshot)
    {
        using var file = new FileStream(cachePath, FileMode.Create, FileAccess.Write, FileShare.None); using var gzip = new GZipStream(file, CompressionLevel.Fastest); using var writer = new BinaryWriter(gzip, Encoding.UTF8);
        uint rootVolume = NativeFileIdentity.TryGet(snapshot.Root, true, out var rootIdentity) ? rootIdentity.Id.VolumeSerial : 0;
        writer.Write(3); writer.Write(Path.GetFullPath(snapshot.Root)); writer.Write(rootVolume); writer.Write(snapshot.ScannedAt.ToBinary()); writer.Write(snapshot.Files.Count);
        foreach (FileItem item in snapshot.Files) { writer.Write(Path.GetFullPath(item.Path)); writer.Write(item.DiskBytes); writer.Write(item.LogicalBytes); writer.Write(item.Modified.ToBinary()); writer.Write(item.Category); writer.Write(item.Created.ToBinary()); writer.Write(item.AllocationEstimated); writer.Write(item.VolumeSerial); writer.Write(item.FileIndex); }
    }
    private static void WriteLegacyV4Cache(string cachePath, ScanSnapshot snapshot)
    {
        using var file = new FileStream(cachePath, FileMode.Create, FileAccess.Write, FileShare.None); using var gzip = new GZipStream(file, CompressionLevel.Fastest); using var writer = new BinaryWriter(gzip, Encoding.UTF8);
        uint rootVolume = NativeFileIdentity.TryGet(snapshot.Root, true, out var rootIdentity) ? rootIdentity.Id.VolumeSerial : 0;
        writer.Write(4); writer.Write(Path.GetFullPath(snapshot.Root)); writer.Write(rootVolume); writer.Write(snapshot.ScannedAt.ToBinary()); writer.Write(snapshot.Summary.DriveUsedBytes); writer.Write(snapshot.Summary.SkippedLocations); writer.Write(snapshot.Summary.ElapsedMilliseconds); writer.Write(snapshot.Files.Count);
        foreach (FileItem item in snapshot.Files) { writer.Write(Path.GetFullPath(item.Path)); writer.Write(item.DiskBytes); writer.Write(item.LogicalBytes); writer.Write(item.Modified.ToBinary()); writer.Write(item.Category); writer.Write(item.Created.ToBinary()); writer.Write(item.AllocationEstimated); writer.Write(item.VolumeSerial); writer.Write(item.FileIndex); }
    }
    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
    private static bool MakeSparse(Microsoft.Win32.SafeHandles.SafeFileHandle handle) { try { return DeviceIoControl(handle, 0x000900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero); } catch { return false; } }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint controlCode, IntPtr input, int inputSize, IntPtr output, int outputSize, out int bytesReturned, IntPtr overlapped);
}
