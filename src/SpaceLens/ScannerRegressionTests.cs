using System.Diagnostics;
using System.Text.Json;

namespace DesktopOrganizer;

internal readonly record struct ScannerRegressionResult(string? Failure, int FileCount, TimeSpan Elapsed)
{
    internal bool Passed => Failure is null;
    internal double FilesPerSecond => Elapsed.TotalSeconds <= 0 ? double.PositiveInfinity : FileCount / Elapsed.TotalSeconds;
}

/// <summary>
/// End-to-end checks for the native scanner. This deliberately uses real files instead of
/// mocked directory entries so a regression back to one handle-open per file is measurable.
/// </summary>
internal static class ScannerRegressionTests
{
    private const int SmallFileCount = 8_000;
    private const double MinimumLocalFilesPerSecond = 3_000;

    internal static ScannerRegressionResult Run()
    {
        string id = Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), "SpaceLens-scanner-test-" + id);
        string outsideRoot = Path.Combine(Path.GetTempPath(), "SpaceLens-scanner-outside-" + id);
        var stopwatch = new Stopwatch();
        int expectedFileCount = 0;

        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(outsideRoot);
            if (OperatingSystem.IsWindows() && IsNtfs(root) && !FastFileScanner.ProbeNativeEnumeration(root))
                return Fail("Native batch directory enumeration is unavailable on a local NTFS test directory.", 0, TimeSpan.Zero);

            byte[][] contents = Enumerable.Range(1, 31).Select(length => new byte[length]).ToArray();
            long expectedLogicalBytes = 0;
            for (int directoryIndex = 0; directoryIndex < 80; directoryIndex++)
            {
                string directory = Path.Combine(root, $"batch-{directoryIndex:D2}");
                Directory.CreateDirectory(directory);
                for (int fileIndex = 0; fileIndex < SmallFileCount / 80; fileIndex++)
                {
                    byte[] content = contents[(directoryIndex * 100 + fileIndex) % contents.Length];
                    File.WriteAllBytes(Path.Combine(directory, $"file-{fileIndex:D3}.bin"), content);
                    expectedLogicalBytes += content.Length;
                    expectedFileCount++;
                }
            }

            string allocationProbe = Path.Combine(root, "allocation-probe.bin");
            byte[] probeContent = new byte[1024 * 1024];
            Random.Shared.NextBytes(probeContent);
            File.WriteAllBytes(allocationProbe, probeContent);
            expectedLogicalBytes += probeContent.Length;
            expectedFileCount++;

            string outsideSentinel = Path.Combine(outsideRoot, "must-not-be-indexed.bin");
            File.WriteAllBytes(outsideSentinel, [1, 2, 3, 4]);
            bool linkedDirectoryCreated = TryCreateDirectoryLink(Path.Combine(root, "outside-link"), outsideRoot);

            var scanned = new List<FileItem>(expectedFileCount);
            int progressCalls = 0;
            stopwatch.Start();
            int skipped = AnalyzerForm.ScanFiles(
                root,
                new ImmediateProgress<(List<FileItem> Batch, int Skipped)>(update =>
                {
                    progressCalls++;
                    scanned.AddRange(update.Batch);
                }),
                CancellationToken.None);
            stopwatch.Stop();

            if (scanned.Count != expectedFileCount)
                return Fail($"Scanner count mismatch: expected {expectedFileCount:N0}, received {scanned.Count:N0}.", scanned.Count, stopwatch.Elapsed);
            if (progressCalls < 2)
                return Fail("Scanner did not stream large scans in multiple progress batches.", scanned.Count, stopwatch.Elapsed);
            if (scanned.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != scanned.Count)
                return Fail("Scanner emitted a file path more than once.", scanned.Count, stopwatch.Elapsed);
            if (scanned.Any(item => !IsUnderRoot(item.Path, root)))
                return Fail("Scanner escaped the selected root through a linked directory.", scanned.Count, stopwatch.Elapsed);
            if (linkedDirectoryCreated && skipped < 1)
                return Fail("Scanner did not report the linked directory as skipped.", scanned.Count, stopwatch.Elapsed);
            if (scanned.Sum(item => item.LogicalBytes) != expectedLogicalBytes)
                return Fail("Scanner logical-byte total does not match the fixture.", scanned.Count, stopwatch.Elapsed);

            bool ntfs = IsNtfs(root);
            if (ntfs && scanned.Any(item => item.AllocationEstimated))
                return Fail("NTFS scanner unexpectedly estimated one or more allocation sizes.", scanned.Count, stopwatch.Elapsed);
            if (ntfs && scanned.Any(item => item.VolumeSerial == 0 || item.FileIndex == 0))
                return Fail("NTFS scanner did not retain a stable identity for every file.", scanned.Count, stopwatch.Elapsed);

            FileItem? allocationItem = scanned.FirstOrDefault(item => string.Equals(item.Path, allocationProbe, StringComparison.OrdinalIgnoreCase));
            if (allocationItem is null)
                return Fail("Allocation accounting probe was not indexed.", scanned.Count, stopwatch.Elapsed);
            FileAttributes probeAttributes = File.GetAttributes(allocationProbe);
            bool probeMayCompress = (probeAttributes & (FileAttributes.Compressed | FileAttributes.SparseFile)) != 0;
            if (ntfs && !probeMayCompress && allocationItem.DiskBytes < allocationItem.LogicalBytes)
                return Fail("Allocated-byte total is smaller than an ordinary uncompressed file.", scanned.Count, stopwatch.Elapsed);

            if (ShouldEnforcePerformance(root) && new ScannerRegressionResult(null, scanned.Count, stopwatch.Elapsed).FilesPerSecond < MinimumLocalFilesPerSecond)
                return Fail($"Scanner performance regression: {scanned.Count / stopwatch.Elapsed.TotalSeconds:N0} files/s; expected at least {MinimumLocalFilesPerSecond:N0} files/s on a local disk.", scanned.Count, stopwatch.Elapsed);

            string missingRoot = Path.Combine(root, "directory-that-does-not-exist");
            var missingFiles = new List<FileItem>();
            int unavailableSkipped = AnalyzerForm.ScanFiles(missingRoot, new ImmediateProgress<(List<FileItem> Batch, int Skipped)>(update => missingFiles.AddRange(update.Batch)), CancellationToken.None);
            if (missingFiles.Count != 0 || unavailableSkipped == 0)
                return Fail("Unavailable-directory handling did not return an empty, skipped result.", scanned.Count, stopwatch.Elapsed);

            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                try
                {
                    AnalyzerForm.ScanFiles(root, new ImmediateProgress<(List<FileItem> Batch, int Skipped)>(_ => { }), canceled.Token);
                    return Fail("A pre-cancelled scanner operation completed normally.", scanned.Count, stopwatch.Elapsed);
                }
                catch (OperationCanceledException)
                {
                }
            }

            using (var canceled = new CancellationTokenSource())
            {
                int partialCount = 0;
                try
                {
                    AnalyzerForm.ScanFiles(
                        root,
                        new ImmediateProgress<(List<FileItem> Batch, int Skipped)>(update =>
                        {
                            partialCount += update.Batch.Count;
                            canceled.Cancel();
                        }),
                        canceled.Token);
                    return Fail("An in-flight scanner cancellation completed normally.", scanned.Count, stopwatch.Elapsed);
                }
                catch (OperationCanceledException)
                {
                    if (partialCount <= 0 || partialCount >= expectedFileCount)
                        return Fail("In-flight cancellation did not stop after a partial progress batch.", scanned.Count, stopwatch.Elapsed);
                }
            }

            var success = new ScannerRegressionResult(null, scanned.Count, stopwatch.Elapsed);
            WriteBenchmark(success);
            return success;
        }
        catch (Exception ex)
        {
            return Fail("Scanner regression test crashed: " + ex, expectedFileCount, stopwatch.Elapsed);
        }
        finally
        {
            TryDeleteDirectory(root);
            TryDeleteDirectory(outsideRoot);
        }
    }

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool IsUnderRoot(string path, string root)
    {
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNtfs(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            return root is not null && string.Equals(new DriveInfo(root).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool ShouldEnforcePerformance(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (root is null) return false;
            DriveType type = new DriveInfo(root).DriveType;
            return type is DriveType.Fixed or DriveType.Ram;
        }
        catch
        {
            return false;
        }
    }

    private static ScannerRegressionResult Fail(string message, int fileCount, TimeSpan elapsed) => new(message, fileCount, elapsed);

    private static void WriteBenchmark(ScannerRegressionResult result)
    {
        try
        {
            string? path = Environment.GetEnvironmentVariable("SPACELENS_SCANNER_BENCHMARK_LOG");
            if (!string.IsNullOrWhiteSpace(path)) File.WriteAllText(path, $"{result.FileCount}|{result.Elapsed.TotalMilliseconds:F3}|{result.FilesPerSecond:F3}");
        }
        catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch
        {
        }
    }

    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

internal static class ScannerBenchmark
{
    internal static int Run(string root, string reportPath)
    {
        try
        {
            int files = 0, estimatedFiles = 0; long bytes = 0, estimatedBytes = 0; var timer = Stopwatch.StartNew();
            int skipped = AnalyzerForm.ScanFiles(root, new ImmediateProgress<(List<FileItem> Batch, int Skipped)>(update =>
            {
                files += update.Batch.Count;
                foreach (FileItem item in update.Batch) { bytes = checked(bytes + item.DiskBytes); if (item.AllocationEstimated) { estimatedFiles++; estimatedBytes = checked(estimatedBytes + item.DiskBytes); } }
            }), CancellationToken.None);
            timer.Stop();
            var report = new { files, skipped, bytes, estimatedFiles, estimatedBytes, elapsedMilliseconds = timer.ElapsedMilliseconds, filesPerSecond = files / Math.Max(0.001, timer.Elapsed.TotalSeconds) };
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report)); return 0;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(reportPath, JsonSerializer.Serialize(new { error = ex.GetType().Name, message = ex.Message })); } catch { }
            return 1;
        }
    }

    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}
