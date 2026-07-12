using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopOrganizer;

internal sealed class UpdateManifest
{
    public int SchemaVersion { get; set; }
    public string Version { get; set; } = "";
    public string Tag { get; set; } = "";
    public string AssetName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTimeOffset PublishedUtc { get; set; }
    public string Notes { get; set; } = "";
    public string Signature { get; set; } = "";
    public string Canonical() => string.Join('\n', "spacelens-update-v1", SchemaVersion.ToString(CultureInfo.InvariantCulture), Version, Tag, AssetName, Sha256.ToUpperInvariant(), SizeBytes.ToString(CultureInfo.InvariantCulture), PublishedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), Convert.ToBase64String(Encoding.UTF8.GetBytes(Notes)));
}

internal readonly record struct SemanticVersion(int Major, int Minor, int Patch) : IComparable<SemanticVersion>
{
    public int CompareTo(SemanticVersion other) { int value = Major.CompareTo(other.Major); if (value != 0) return value; value = Minor.CompareTo(other.Minor); return value != 0 ? value : Patch.CompareTo(other.Patch); }
    public override string ToString() => $"{Major}.{Minor}.{Patch}";
    public static bool TryParse(string value, out SemanticVersion version)
    {
        version = default; string[] parts = value.Split('.'); if (parts.Length != 3) return false; Span<int> numbers = stackalloc int[3];
        for (int i = 0; i < 3; i++) { if (parts[i].Length == 0 || (parts[i].Length > 1 && parts[i][0] == '0') || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]) || numbers[i] < 0) return false; }
        version = new(numbers[0], numbers[1], numbers[2]); return true;
    }
}

internal static class UpdateService
{
    internal static string CurrentVersionText { get; } = ResolveCurrentVersion();
    private const string Repository = "Purxy8/SpaceLens";
    private const string AssetName = "SpaceLens-Setup.exe";
    private const string FeedUrl = "https://github.com/Purxy8/SpaceLens/releases/latest/download/update.json";
    internal const string ReleasePage = "https://github.com/Purxy8/SpaceLens/releases";
    internal const string PrivacyPage = "https://github.com/Purxy8/SpaceLens/blob/main/PRIVACY.md";
    private const long MaximumInstallerBytes = 250L * 1024 * 1024;
    private const int MaximumManifestBytes = 64 * 1024;
    private const int MaximumStateBytes = 4 * 1024;
    private const int MaximumAttemptStampBytes = 128;
    private const int MaximumRedirects = 5;
    private static readonly string[] AllowedUpdateHosts = ["github.com", "release-assets.githubusercontent.com"];
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static readonly object StateGate = new();
    private static string StatePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceLens", "update-state.json");
    private static string AttemptStampPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceLens", "update-attempt.txt");
    private static DateTimeOffset? processAutomaticAttemptUtc;

    static UpdateService()
    {
        if (Environment.GetCommandLineArgs().Contains("--self-test", StringComparer.Ordinal)) RunSecuritySelfTest();
    }

    private sealed class UpdateState
    {
        public DateTimeOffset? LastAutomaticAttemptUtc { get; set; }
        public bool AutomaticChecksEnabled { get; set; }
    }

    private static string ResolveCurrentVersion()
    {
        Version? version = Assembly.GetExecutingAssembly().GetName().Version;
        string value = version is null ? "" : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        if (!SemanticVersion.TryParse(value, out _)) throw new InvalidOperationException("The installed application version is invalid.");
        return value;
    }

    internal static bool AutomaticChecksEnabled()
    {
        lock (StateGate) return ReadState().AutomaticChecksEnabled;
    }

    internal static bool AutomaticCheckIsDue()
    {
        lock (StateGate)
        {
            UpdateState state = ReadState();
            if (!state.AutomaticChecksEnabled) return false;
            DateTimeOffset? last = Latest(state.LastAutomaticAttemptUtc, ReadAttemptStamp(), processAutomaticAttemptUtc);
            return AutomaticAttemptIsDue(last, DateTimeOffset.UtcNow);
        }
    }

    internal static void RecordAutomaticAttempt()
    {
        lock (StateGate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            processAutomaticAttemptUtc = now;
            // Keep an independent, bounded attempt marker. If update-state.json is
            // temporarily locked, a later launch can still avoid checking again.
            TryWriteAttemptStamp(now);
            try { UpdateState state = ReadState(); state.LastAutomaticAttemptUtc = now; WriteState(state); } catch { }
        }
    }

    internal static void SetAutomaticChecksEnabled(bool enabled)
    {
        lock (StateGate)
        {
            UpdateState state = ReadState();
            state.AutomaticChecksEnabled = enabled;
            WriteState(state);
        }
    }

    private static UpdateState ReadState()
    {
        try
        {
            using var file = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length <= 0 || file.Length > MaximumStateBytes) return new UpdateState();
            return JsonSerializer.Deserialize<UpdateState>(file, JsonOptions) ?? new UpdateState();
        }
        catch { return new UpdateState(); }
    }

    private static void WriteState(UpdateState state)
    {
        string directory = Path.GetDirectoryName(StatePath)!;
        Directory.CreateDirectory(directory);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        if (json.Length > MaximumStateBytes) throw new InvalidDataException("The update preference file is too large.");
        string temp = Path.Combine(directory, $".update-state-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { file.Write(json); file.Flush(true); }
            File.Move(temp, StatePath, true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    private static DateTimeOffset? Latest(params DateTimeOffset?[] values)
    {
        DateTimeOffset? latest = null;
        foreach (DateTimeOffset? value in values)
            if (value is DateTimeOffset candidate && (latest is null || candidate > latest.Value)) latest = candidate;
        return latest;
    }

    private static bool AutomaticAttemptIsDue(DateTimeOffset? last, DateTimeOffset now)
        => last is not DateTimeOffset recorded || now - recorded >= TimeSpan.FromHours(24);

    private static DateTimeOffset? ReadAttemptStamp()
    {
        try
        {
            using var file = new FileStream(AttemptStampPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length <= 0 || file.Length > MaximumAttemptStampBytes) return null;
            using var reader = new StreamReader(file, new UTF8Encoding(false, true), false, 256, false);
            string value = reader.ReadToEnd().Trim();
            return DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed) ? parsed : null;
        }
        catch { return null; }
    }

    private static void TryWriteAttemptStamp(DateTimeOffset value)
    {
        string directory = Path.GetDirectoryName(AttemptStampPath)!;
        string temp = Path.Combine(directory, $".update-attempt-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            byte[] bytes = Encoding.UTF8.GetBytes(value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            if (bytes.Length > MaximumAttemptStampBytes) return;
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256, FileOptions.WriteThrough)) { file.Write(bytes); file.Flush(true); }
            File.Move(temp, AttemptStampPath, true);
        }
        catch { }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    internal static async Task<UpdateManifest?> CheckAsync(CancellationToken token)
    {
        using HttpClient client = CreateClient(TimeSpan.FromMinutes(1)); using var response = await GetWithValidatedRedirectsAsync(client, new Uri(FeedUrl), token); response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumManifestBytes) throw new InvalidDataException("The update manifest is too large."); await using Stream manifestStream = await response.Content.ReadAsStreamAsync(token); byte[] bytes = await ReadBoundedAsync(manifestStream, MaximumManifestBytes, token);
        UpdateManifest manifest = ParseAndValidate(bytes); if (!SemanticVersion.TryParse(CurrentVersionText, out var current)) throw new InvalidOperationException("The installed application version is invalid."); if (!SemanticVersion.TryParse(manifest.Version, out var available)) throw new InvalidDataException("The update version is invalid."); return available.CompareTo(current) > 0 ? manifest : null;
    }

    internal static UpdateManifest ParseAndValidate(ReadOnlyMemory<byte> json)
    {
        using (var document = JsonDocument.Parse(json))
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The update manifest is invalid."); var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject()) if (!names.Add(property.Name)) throw new InvalidDataException("The update manifest contains duplicate fields.");
        }
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(json.Span, JsonOptions) ?? throw new InvalidDataException("The update manifest is invalid.");
        if (manifest.SchemaVersion != 1 || !SemanticVersion.TryParse(manifest.Version, out _) || manifest.Tag != "v" + manifest.Version || manifest.AssetName != AssetName) throw new InvalidDataException("The update manifest identifies an unsupported release.");
        if (manifest.SizeBytes <= 0 || manifest.SizeBytes > MaximumInstallerBytes || manifest.Sha256.Length != 64 || !manifest.Sha256.All(Uri.IsHexDigit) || manifest.Notes.Length > 4000 || manifest.PublishedUtc < new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero) || manifest.PublishedUtc > DateTimeOffset.UtcNow.AddDays(1)) throw new InvalidDataException("The update manifest contains invalid values.");
        byte[] signature; try { signature = Convert.FromBase64String(manifest.Signature); } catch (FormatException) { throw new CryptographicException("The update manifest signature is invalid."); }
        if (signature.Length != 64) throw new CryptographicException("The update manifest signature has an invalid length.");
        using var key = ECDsa.Create(); using Stream publicKey = Assembly.GetExecutingAssembly().GetManifestResourceStream("SpaceLens.UpdatePublicKey.pem") ?? throw new InvalidOperationException("The update verification key is missing."); using var reader = new StreamReader(publicKey); string publicPem = reader.ReadToEnd();
        if (!publicPem.Contains("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal) || publicPem.Contains("PRIVATE KEY", StringComparison.Ordinal)) throw new InvalidOperationException("The embedded update verification key is not public-only SPKI material.");
        key.ImportFromPem(publicPem);
        bool containsPrivateKey;
        try { _ = key.ExportParameters(true); containsPrivateKey = true; }
        catch (CryptographicException) { containsPrivateKey = false; }
        if (containsPrivateKey) throw new InvalidOperationException("The embedded update verification asset contains private key material.");
        if (key.KeySize != 256 || !key.VerifyData(Encoding.UTF8.GetBytes(manifest.Canonical()), signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) throw new CryptographicException("The update manifest was not signed by SpaceLens."); return manifest;
    }

    internal static async Task<bool> OfferAndInstallAsync(Form owner, UpdateManifest manifest)
    {
        if (ProcessSecurity.IsElevated)
        {
            MessageBox.Show(owner, ProcessSecurity.ElevatedPerUserOperationMessage, "Restart SpaceLens normally", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        string notes = string.IsNullOrWhiteSpace(manifest.Notes) ? "This release includes improvements and fixes." : manifest.Notes;
        if (MessageBox.Show(owner, $"SpaceLens {manifest.Version} is available.\n\n{notes}\n\nDownload and start the verified installer?", "SpaceLens update available", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return false;
        using var progressForm = new UpdateProgressForm(manifest.Version); progressForm.Show(owner);
        string? installer = null; bool launched = false;
        try
        {
            installer = await DownloadInstallerAsync(manifest, progressForm.Progress, progressForm.Token); progressForm.MarkComplete(); progressForm.Close();
            if (ProcessSecurity.IsElevated) throw new SecurityException(ProcessSecurity.ElevatedPerUserOperationMessage);
            // Re-hash through a no-write/no-delete sharing handle and retain that
            // handle until CreateProcess has opened the verified image.
            using FileStream launchLock = OpenVerifiedInstaller(installer, manifest);
            var start = new System.Diagnostics.ProcessStartInfo(installer) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(installer)! }; start.ArgumentList.Add("--upgrade"); start.ArgumentList.Add("--wait-pid"); start.ArgumentList.Add(Environment.ProcessId.ToString());
            using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(start);
            if (process is null) throw new InvalidOperationException("Windows could not start the update installer.");
            launched = true; Application.Exit(); return true;
        }
        catch (OperationCanceledException) { progressForm.Close(); return false; }
        catch (Exception ex) { progressForm.Close(); MessageBox.Show(owner, $"The update was not installed.\n\n{ex.Message}", "Update failed", MessageBoxButtons.OK, MessageBoxIcon.Error); return false; }
        finally { if (!launched && installer is not null) TryDeleteInstaller(installer); }
    }

    private static async Task<string> DownloadInstallerAsync(UpdateManifest manifest, IProgress<(long Received, long Total)> progress, CancellationToken token)
    {
        string url = BuildInstallerUrl(manifest); string partial = Path.Combine(Path.GetTempPath(), $"SpaceLens-Setup-{manifest.Version}-{Guid.NewGuid():N}.partial"), complete = Path.ChangeExtension(partial, ".exe");
        try
        {
            using HttpClient client = CreateClient(TimeSpan.FromMinutes(10)); using var response = await GetWithValidatedRedirectsAsync(client, new Uri(url), token); response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != manifest.SizeBytes) throw new InvalidDataException("The installer size does not match the signed release manifest.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); await using var source = await response.Content.ReadAsStreamAsync(token); await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                byte[] buffer = new byte[1024 * 1024]; long received = 0; int read;
                while ((read = await source.ReadAsync(buffer, token)) > 0) { received += read; if (received > manifest.SizeBytes || received > MaximumInstallerBytes) throw new InvalidDataException("The installer download exceeded its signed size."); hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), token); progress.Report((received, manifest.SizeBytes)); }
                await output.FlushAsync(token); if (received != manifest.SizeBytes) throw new InvalidDataException("The installer download was incomplete.");
            }
            byte[] expected = Convert.FromHexString(manifest.Sha256), actual = hash.GetHashAndReset(); if (!CryptographicOperations.FixedTimeEquals(expected, actual)) throw new CryptographicException("The installer hash does not match the signed release manifest.");
            VerifyInstallerFile(partial, manifest); File.Move(partial, complete); return complete;
        }
        catch { try { if (File.Exists(partial)) File.Delete(partial); if (File.Exists(complete)) File.Delete(complete); } catch { } throw; }
    }

    internal static string BuildInstallerUrl(UpdateManifest manifest)
    {
        var uri = new Uri($"https://github.com/{Repository}/releases/download/{manifest.Tag}/{AssetName}");
        ValidateTransportUri(uri);
        return uri.AbsoluteUri;
    }
    internal static void VerifyInstallerFile(string path, UpdateManifest manifest)
    {
        using FileStream stream = OpenVerifiedInstaller(path, manifest);
    }

    private static FileStream OpenVerifiedInstaller(string path, UpdateManifest manifest)
    {
        FileStream? stream = null;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            if (stream.Length != manifest.SizeBytes) throw new InvalidDataException("The installer size does not match the signed release manifest.");
            if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z') throw new InvalidDataException("The update is not a valid Windows executable.");
            stream.Position = 0;
            byte[] expected = Convert.FromHexString(manifest.Sha256), actual = SHA256.HashData(stream);
            if (!CryptographicOperations.FixedTimeEquals(expected, actual)) throw new CryptographicException("The installer hash does not match the signed release manifest.");
            stream.Position = 0;
            FileStream verified = stream; stream = null; return verified;
        }
        finally { stream?.Dispose(); }
    }

    internal static int VerifyRelease(string manifestPath, string installerPath)
    {
        try
        {
            using var manifestStream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read); if (manifestStream.Length > MaximumManifestBytes) throw new InvalidDataException("The update manifest is too large."); byte[] json = ReadBoundedAsync(manifestStream, MaximumManifestBytes, CancellationToken.None).GetAwaiter().GetResult();
            UpdateManifest manifest = ParseAndValidate(json);
            if (!manifest.Version.Equals(CurrentVersionText, StringComparison.Ordinal)) throw new InvalidDataException($"The manifest version {manifest.Version} does not match SpaceLens {CurrentVersionText}.");
            VerifyInstallerFile(installerPath, manifest); return 0;
        }
        catch { return 1; }
    }

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, CheckCertificateRevocationList = true, AutomaticDecompression = DecompressionMethods.None, UseCookies = false }; var client = new HttpClient(handler) { Timeout = timeout }; client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SpaceLens", CurrentVersionText)); return client;
    }

    private static async Task<HttpResponseMessage> GetWithValidatedRedirectsAsync(HttpClient client, Uri initialUri, CancellationToken token)
    {
        Uri current = initialUri;
        for (int redirects = 0; ; redirects++)
        {
            ValidateTransportUri(current);
            HttpResponseMessage? response = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (!IsRedirect(response.StatusCode)) return response;
                if (redirects >= MaximumRedirects) throw new SecurityException("The GitHub update request exceeded the redirect limit.");
                Uri? location = response.Headers.Location;
                if (location is null) throw new SecurityException("GitHub returned an update redirect without a destination.");
                Uri next = location.IsAbsoluteUri ? location : new Uri(current, location);
                ValidateTransportUri(next);
                current = next;
            }
            finally
            {
                if (response is not null && IsRedirect(response.StatusCode)) response.Dispose();
            }
        }
    }

    private static bool IsRedirect(HttpStatusCode status)
        => (int)status is 301 or 302 or 303 or 307 or 308;

    private static void ValidateTransportUri(Uri? uri)
    {
        if (uri is null
            || !uri.IsAbsoluteUri
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0
            || uri.Fragment.Length != 0
            || !AllowedUpdateHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase))
            throw new SecurityException("The update request targeted an unexpected network location.");
    }
    internal static async Task<byte[]> ReadBoundedAsync(Stream source, int maximumBytes, CancellationToken token)
    {
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        using var destination = new MemoryStream(Math.Min(maximumBytes, 16 * 1024));
        byte[] buffer = new byte[Math.Min(16 * 1024, maximumBytes + 1)];
        while (true)
        {
            int remaining = maximumBytes + 1 - checked((int)destination.Length);
            if (remaining <= 0) throw new InvalidDataException("The update manifest is too large.");
            int read = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
            if (read == 0) return destination.ToArray();
            destination.Write(buffer, 0, read);
            if (destination.Length > maximumBytes) throw new InvalidDataException("The update manifest is too large.");
        }
    }
    private static void TryDeleteInstaller(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    private static void RunSecuritySelfTest()
    {
        ValidateTransportUri(new Uri(FeedUrl));
        ValidateTransportUri(new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/example"));
        string[] rejected =
        [
            "http://github.com/Purxy8/SpaceLens/releases/latest/download/update.json",
            "https://github.com.evil.example/update.json",
            "https://raw.githubusercontent.com/Purxy8/SpaceLens/main/update.json",
            "https://release-assets.githubusercontent.com.evil.example/file",
            "https://github.com:444/update.json",
            "https://user@github.com/update.json",
            "https://github.com/update.json#fragment"
        ];
        foreach (string value in rejected)
        {
            bool failedClosed = false;
            try { ValidateTransportUri(new Uri(value)); }
            catch (SecurityException) { failedClosed = true; }
            if (!failedClosed) throw new InvalidOperationException("Update transport allowlist self-test failed.");
        }
        using (var allowedHandler = new RedirectSelfTestHandler((request, number) => number switch
        {
            1 => Redirect("https://release-assets.githubusercontent.com/github-production-release-asset/file"),
            2 when request.RequestUri?.Host == "release-assets.githubusercontent.com" => new HttpResponseMessage(HttpStatusCode.OK),
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        }))
        using (var client = new HttpClient(allowedHandler))
        using (HttpResponseMessage response = GetWithValidatedRedirectsAsync(client, new Uri(FeedUrl), CancellationToken.None).GetAwaiter().GetResult())
            if (response.StatusCode != HttpStatusCode.OK || allowedHandler.Requests != 2) throw new InvalidOperationException("Allowed update redirect self-test failed.");

        using (var rejectedHandler = new RedirectSelfTestHandler((_, _) => Redirect("https://evil.example/update.json")))
        using (var client = new HttpClient(rejectedHandler))
        {
            bool rejectedHop = false;
            try { using HttpResponseMessage _ = GetWithValidatedRedirectsAsync(client, new Uri(FeedUrl), CancellationToken.None).GetAwaiter().GetResult(); }
            catch (SecurityException) { rejectedHop = true; }
            if (!rejectedHop || rejectedHandler.Requests != 1) throw new InvalidOperationException("Redirect-hop rejection self-test failed.");
        }
        if (!ProcessSecurity.ShouldRefusePerUserOperation(true) || ProcessSecurity.ShouldRefusePerUserOperation(false)) throw new InvalidOperationException("Elevated update policy self-test failed.");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!AutomaticAttemptIsDue(null, now) || AutomaticAttemptIsDue(now.AddHours(-23), now) || !AutomaticAttemptIsDue(now.AddHours(-25), now) || AutomaticAttemptIsDue(now.AddHours(1), now)) throw new InvalidOperationException("Automatic update cadence self-test failed.");
        InstallerLifecycle.RunSecuritySelfTest();
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private sealed class RedirectSelfTestHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HttpResponseMessage response = responseFactory(request, ++Requests);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}

internal sealed class UpdateProgressForm : Form
{
    private readonly ProgressBar bar = new() { Dock = DockStyle.Top, Height = 24, Style = ProgressBarStyle.Continuous };
    private readonly Label message = new() { Dock = DockStyle.Top, Height = 34, Text = "Preparing download…" };
    private readonly Button cancel = new() { Text = "Cancel", Dock = DockStyle.Bottom, Height = 34 };
    private readonly CancellationTokenSource cancellation = new(); private bool complete;
    internal CancellationToken Token => cancellation.Token;
    internal IProgress<(long Received, long Total)> Progress { get; }
    internal UpdateProgressForm(string version)
    {
        Text = $"Downloading SpaceLens {version}"; ClientSize = new Size(460, 150); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 10); Padding = new Padding(18);
        Progress = new Progress<(long Received, long Total)>(value => { int percent = value.Total <= 0 ? 0 : (int)Math.Clamp(value.Received * 100 / value.Total, 0, 100); bar.Value = percent; message.Text = $"Downloaded {percent}% ({Format(value.Received)} of {Format(value.Total)})"; }); cancel.Click += (_, _) => cancellation.Cancel(); FormClosing += (_, e) => { if (!complete) cancellation.Cancel(); };
        Controls.Add(cancel); Controls.Add(bar); Controls.Add(message);
    }
    internal void MarkComplete() => complete = true;
    private static string Format(long bytes) => ByteFormatter.Format(bytes, CultureInfo.CurrentCulture);
}
