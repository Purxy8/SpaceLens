using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

const int MaxKeyCharacters = 16 * 1024;
const int MaxManifestCharacters = 32 * 1024;
JsonTypeInfo<UpdateManifest> manifestJsonType = ReleaseJsonContext.Default.UpdateManifest;

if (args.Length == 4 && args[0] == "rotate-cng")
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows CNG key storage is available only on Windows.");
    string keyName = ValidateCngKeyName(args[1]);
    string publicTarget = Path.GetFullPath(args[2]);
    string fixtureTarget = Path.GetFullPath(args[3]);
    EnsureDistinctPaths(publicTarget, fixtureTarget);
    if (!string.Equals(Path.GetFileName(publicTarget), "update-public-key.pem", StringComparison.Ordinal) ||
        !string.Equals(Path.GetFileName(fixtureTarget), "update-selftest.json", StringComparison.Ordinal))
        throw new InvalidOperationException("Trust rotation may replace only update-public-key.pem and update-selftest.json.");
    AssertTrustTargetSet(publicTarget, fixtureTarget);
    AssertRegularExistingFile(publicTarget, 16 * 1024);
    AssertRegularExistingFile(fixtureTarget, 32 * 1024);

    string token = Guid.NewGuid().ToString("N");
    string publicNew = Path.Combine(Path.GetDirectoryName(publicTarget)!, $".{Path.GetFileName(publicTarget)}.{token}.new");
    string fixtureNew = Path.Combine(Path.GetDirectoryName(fixtureTarget)!, $".{Path.GetFileName(fixtureTarget)}.{token}.new");
    string unsignedNew = Path.Combine(Path.GetDirectoryName(fixtureTarget)!, $".update-selftest.{token}.unsigned");
    string publicBackup = Path.Combine(Path.GetDirectoryName(publicTarget)!, $".{Path.GetFileName(publicTarget)}.{token}.backup");
    string fixtureBackup = Path.Combine(Path.GetDirectoryName(fixtureTarget)!, $".{Path.GetFileName(fixtureTarget)}.{token}.backup");
    bool publicReplaced = false;
    bool fixtureReplaced = false;
    bool committed = false;
    byte[] originalPublicHash = SHA256.HashData(File.ReadAllBytes(publicTarget));
    byte[] originalFixtureHash = SHA256.HashData(File.ReadAllBytes(fixtureTarget));
    Exception? operationFailure = null;

    using CngKey cngKey = CreateProtectedCngKey(keyName);
    try
    {
        using var privateKey = new ECDsaCng(cngKey);
        string publicPem = privateKey.ExportSubjectPublicKeyInfoPem();
        WriteExclusive(publicNew, publicPem);
        using ECDsa publicKey = ECDsa.Create();
        publicKey.ImportFromPem(publicPem);

        var fixture = new UpdateManifest
        {
            Version = "9.9.9",
            Tag = "v9.9.9",
            Sha256 = new string('0', 64),
            SizeBytes = 123456,
            PublishedUtc = DateTimeOffset.UtcNow,
            Notes = "SpaceLens update signature self-test."
        };
        WriteExclusive(unsignedNew, JsonSerializer.Serialize(fixture, manifestJsonType) + Environment.NewLine);
        SignAndVerifyRelease(privateKey, publicKey, unsignedNew, fixtureNew, manifestJsonType);

        AssertTrustTargetSet(publicTarget, fixtureTarget);
        AssertRegularExistingFile(publicTarget, 16 * 1024);
        AssertRegularExistingFile(fixtureTarget, 32 * 1024);
        File.Replace(publicNew, publicTarget, publicBackup, ignoreMetadataErrors: false);
        publicReplaced = true;
        File.Replace(fixtureNew, fixtureTarget, fixtureBackup, ignoreMetadataErrors: false);
        fixtureReplaced = true;

        using ECDsa installedPublicKey = LoadPublicEcKey(publicTarget, exclusiveRead: false);
        UpdateManifest installedFixture = ParseStrictManifest(ReadBoundedText(fixtureTarget, MaxManifestCharacters, false), manifestJsonType, true);
        byte[] installedSignature = Convert.FromBase64String(installedFixture.Signature);
        byte[] installedCanonical = Encoding.UTF8.GetBytes(installedFixture.Canonical());
        try
        {
            if (installedFixture.Version != "9.9.9" || !installedPublicKey.VerifyData(installedCanonical, installedSignature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new CryptographicException("The atomically installed trust fixture did not verify.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(installedCanonical);
            CryptographicOperations.ZeroMemory(installedSignature);
        }
        committed = true;
    }
    catch (Exception ex)
    {
        operationFailure = ex;
    }

    var recoveryFailures = new List<Exception>();
    if (!committed)
    {
        if (fixtureReplaced)
            TryRecoveryStep(() => File.Replace(fixtureBackup, fixtureTarget, null, ignoreMetadataErrors: false), "restore update-selftest.json", recoveryFailures);
        if (publicReplaced)
            TryRecoveryStep(() => File.Replace(publicBackup, publicTarget, null, ignoreMetadataErrors: false), "restore update-public-key.pem", recoveryFailures);
        TryRecoveryStep(cngKey.Delete, $"delete incomplete CNG key '{keyName}'", recoveryFailures);
        TryRecoveryStep(() =>
        {
            byte[] actualPublic = SHA256.HashData(File.ReadAllBytes(publicTarget));
            byte[] actualFixture = SHA256.HashData(File.ReadAllBytes(fixtureTarget));
            if (!CryptographicOperations.FixedTimeEquals(actualPublic, originalPublicHash) || !CryptographicOperations.FixedTimeEquals(actualFixture, originalFixtureHash))
                throw new IOException("Tracked trust assets were not restored byte-for-byte.");
        }, "verify restored trust asset pair", recoveryFailures);
    }

    TryRecoveryStep(() => DeleteRequiredIfExists(publicNew), "remove staged public key", recoveryFailures);
    TryRecoveryStep(() => DeleteRequiredIfExists(fixtureNew), "remove staged self-test", recoveryFailures);
    TryRecoveryStep(() => DeleteRequiredIfExists(unsignedNew), "remove staged unsigned fixture", recoveryFailures);
    if (committed)
    {
        TryRecoveryStep(() => DeleteRequiredIfExists(publicBackup), "remove public-key backup", recoveryFailures);
        TryRecoveryStep(() => DeleteRequiredIfExists(fixtureBackup), "remove self-test backup", recoveryFailures);
    }

    CryptographicOperations.ZeroMemory(originalPublicHash);
    CryptographicOperations.ZeroMemory(originalFixtureHash);
    if (recoveryFailures.Count != 0)
    {
        var failures = new List<Exception>();
        if (operationFailure is not null) failures.Add(operationFailure);
        failures.AddRange(recoveryFailures);
        if (committed)
            throw new AggregateException(
                $"Trust rotation committed and verified, but backup cleanup was incomplete. Delete only leftover backups '{publicBackup}' and '{fixtureBackup}' after confirming both tracked assets remain present.",
                failures);
        throw new AggregateException(
            $"Trust rotation failed and automatic recovery was incomplete. Preserve any backups at '{publicBackup}' and '{fixtureBackup}', restore both tracked files together, and remove CNG key '{keyName}' before retrying.",
            failures);
    }
    if (operationFailure is not null)
    {
        ExceptionDispatchInfo.Capture(operationFailure).Throw();
    }
    Console.WriteLine("Update trust rotated transactionally: a non-exportable CNG key was created and only the public key plus freshly signed self-test fixture were installed.");
    return;
}

if (args.Length == 5 && args[0] == "sign-release-cng")
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows CNG key storage is available only on Windows.");
    string keyName = ValidateCngKeyName(args[1]);
    string publicPath = Path.GetFullPath(args[2]);
    string unsignedPath = Path.GetFullPath(args[3]);
    string signedPath = Path.GetFullPath(args[4]);
    EnsureDistinctPaths(publicPath, unsignedPath, signedPath);
    if (File.Exists(signedPath)) throw new InvalidOperationException("Refusing to overwrite an existing signed manifest.");

    using CngKey cngKey = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.None);
    if (cngKey.Algorithm != CngAlgorithm.ECDsaP256 || (cngKey.KeyUsage & CngKeyUsages.Signing) == 0)
        throw new CryptographicException("The named CNG key is not an ECDSA P-256 signing key.");
    using var privateKey = new ECDsaCng(cngKey);
    using ECDsa publicKey = LoadPublicEcKey(publicPath, exclusiveRead: false);
    SignAndVerifyRelease(privateKey, publicKey, unsignedPath, signedPath, manifestJsonType);
    Console.WriteLine("Update manifest signed with the user-protected CNG key and verified against the pinned public key.");
    return;
}

if (args.Length == 3 && args[0] == "verify")
{
    UpdateManifest manifest = ParseStrictManifest(ReadBoundedText(args[2], MaxManifestCharacters, false), manifestJsonType, true);
    byte[] signature = Convert.FromBase64String(manifest.Signature);
    byte[] canonical = Encoding.UTF8.GetBytes(manifest.Canonical());
    try
    {
        using ECDsa key = LoadPublicEcKey(args[1], exclusiveRead: false);
        if (!key.VerifyData(canonical, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new CryptographicException("Manifest verification failed.");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(canonical);
        CryptographicOperations.ZeroMemory(signature);
    }
    Console.WriteLine("Update manifest verified.");
    return;
}

if (args.Length == 1 && args[0] == "self-test")
{
    RunSelfTests(manifestJsonType);
    Console.WriteLine("ReleaseSigner self-tests passed.");
    return;
}

if (args.Length > 0 && args[0] is "generate" or "generate-cng" or "sign-release")
    throw new NotSupportedException("Standalone/exportable key generation is disabled. Use only the transactional rotate-cng flow and non-exportable CNG signing.");
Console.Error.WriteLine("Usage: ReleaseSigner rotate-cng <key-name> <tracked-public.pem> <tracked-selftest.json> | sign-release-cng <key-name> <public.pem> <unsigned.json> <signed.json> | verify <public.pem> <signed.json> | self-test");
Environment.ExitCode = 2;

static CngKey CreateProtectedCngKey(string keyName)
{
    var provider = CngProvider.MicrosoftSoftwareKeyStorageProvider;
    if (CngKey.Exists(keyName, provider)) throw new InvalidOperationException("A CNG key with this name already exists.");
    return CngKey.Create(CngAlgorithm.ECDsaP256, keyName, new CngKeyCreationParameters
    {
        Provider = provider,
        ExportPolicy = CngExportPolicies.None,
        KeyUsage = CngKeyUsages.Signing,
        KeyCreationOptions = CngKeyCreationOptions.None,
        UIPolicy = new CngUIPolicy(CngUIProtectionLevels.ForceHighProtection, "SpaceLens update signing key")
    });
}

static void AssertRegularExistingFile(string path, long maximumBytes)
{
    var info = new FileInfo(path);
    if (!info.Exists || info.Length <= 0 || info.Length > maximumBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        throw new InvalidDataException($"Trust target is missing, too large, or a reparse point: {path}");
}

static void AssertTrustTargetSet(string publicTarget, string fixtureTarget)
{
    string publicDirectory = Path.GetDirectoryName(publicTarget)!;
    string fixtureDirectory = Path.GetDirectoryName(fixtureTarget)!;
    if (!string.Equals(publicDirectory, fixtureDirectory, StringComparison.Ordinal))
        throw new InvalidOperationException("Update trust targets must share the exact assets directory.");

    AssertNoReparseAncestors(publicTarget);
    AssertNoReparseAncestors(fixtureTarget);
    string[] entries = Directory.GetFileSystemEntries(publicDirectory, "update-*", SearchOption.TopDirectoryOnly);
    if (entries.Length != 2 ||
        !entries.Any(path => string.Equals(Path.GetFileName(path), "update-public-key.pem", StringComparison.Ordinal)) ||
        !entries.Any(path => string.Equals(Path.GetFileName(path), "update-selftest.json", StringComparison.Ordinal)))
        throw new InvalidOperationException("The assets directory contains an unexpected update-trust target.");
}

static void AssertNoReparseAncestors(string path)
{
    string fullPath = Path.GetFullPath(path);
    var file = new FileInfo(fullPath);
    if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0)
        throw new InvalidDataException($"Trust target is missing or a reparse point: {fullPath}");

    DirectoryInfo? directory = file.Directory;
    while (directory is not null)
    {
        directory.Refresh();
        if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Trust target has a missing or reparse-point ancestor: {directory.FullName}");
        directory = directory.Parent;
    }
}

static void DeleteRequiredIfExists(string path)
{
    if (File.Exists(path)) File.Delete(path);
}

static void TryRecoveryStep(Action action, string description, List<Exception> failures)
{
    try { action(); }
    catch (Exception ex) { failures.Add(new IOException($"Recovery step failed: {description}.", ex)); }
}

static void SignAndVerifyRelease(ECDsa privateKey, ECDsa publicKey, string unsignedPath, string signedPath, JsonTypeInfo<UpdateManifest> manifestType)
{
    const int maxManifestCharacters = 32 * 1024;
    UpdateManifest manifest = ParseStrictManifest(ReadBoundedText(unsignedPath, maxManifestCharacters, false), manifestType, false);
    manifest.Signature = string.Empty;
    byte[] canonical = Encoding.UTF8.GetBytes(manifest.Canonical());
    try
    {
        byte[] signature = privateKey.SignData(canonical, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        if (signature.Length != 64 || !publicKey.VerifyData(canonical, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new CryptographicException("The signing key does not match the pinned public key.");

        manifest.Signature = Convert.ToBase64String(signature);
        WriteExclusive(signedPath, JsonSerializer.Serialize(manifest, manifestType) + Environment.NewLine);

        UpdateManifest written = ParseStrictManifest(ReadBoundedText(signedPath, maxManifestCharacters, false), manifestType, true);
        byte[] writtenSignature = Convert.FromBase64String(written.Signature);
        byte[] writtenCanonical = Encoding.UTF8.GetBytes(written.Canonical());
        try
        {
            if (!publicKey.VerifyData(writtenCanonical, writtenSignature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new CryptographicException("The written manifest did not verify.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(writtenCanonical);
            CryptographicOperations.ZeroMemory(writtenSignature);
        }
        CryptographicOperations.ZeroMemory(signature);
    }
    catch
    {
        if (File.Exists(signedPath)) File.Delete(signedPath);
        throw;
    }
    finally
    {
        CryptographicOperations.ZeroMemory(canonical);
    }
}

static string ValidateCngKeyName(string value)
{
    if (string.IsNullOrWhiteSpace(value) || value.Length is < 3 or > 100 || value.Any(ch => !(char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_' or '.')))
        throw new ArgumentException("CNG key name must be 3-100 letters, digits, spaces, dots, hyphens, or underscores.");
    return value;
}

static ECDsa LoadPublicEcKey(string path, bool exclusiveRead)
{
    string pem = ReadBoundedText(path, MaxKeyCharacters, exclusiveRead);
    AssertPublicOnlyPem(pem);
    var key = ECDsa.Create();
    try
    {
        key.ImportFromPem(pem);
        if (key.KeySize != 256)
            throw new CryptographicException("Only ECDSA P-256 release keys are accepted.");
        ECParameters parameters = key.ExportParameters(false);
        if (parameters.Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            throw new CryptographicException("Only the NIST P-256 curve is accepted.");
        bool privateExported = false;
        try { privateExported = key.ExportParameters(true).D is { Length: > 0 }; }
        catch (CryptographicException) { }
        if (privateExported) throw new CryptographicException("A private key was supplied where a public-only SPKI key is required.");
        return key;
    }
    catch
    {
        key.Dispose();
        throw;
    }
}

static void AssertPublicOnlyPem(string pem)
{
    string value = pem.Trim();
    if (value.Contains("PRIVATE KEY", StringComparison.Ordinal) ||
        (!value.StartsWith("-----BEGIN PUBLIC KEY-----\n", StringComparison.Ordinal) && !value.StartsWith("-----BEGIN PUBLIC KEY-----\r\n", StringComparison.Ordinal)) ||
        !value.EndsWith("-----END PUBLIC KEY-----", StringComparison.Ordinal) ||
        value.IndexOf("-----BEGIN PUBLIC KEY-----", 1, StringComparison.Ordinal) >= 0)
        throw new CryptographicException("A public-only SPKI PUBLIC KEY PEM block is required.");
}

static string ReadBoundedText(string path, int maxCharacters, bool exclusiveRead)
{
    string fullPath = Path.GetFullPath(path);
    using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, exclusiveRead ? FileShare.None : FileShare.Read, 4096, FileOptions.SequentialScan);
    if (stream.Length <= 0 || stream.Length > maxCharacters * 4L)
        throw new InvalidDataException($"Input file has an invalid size: {Path.GetFileName(fullPath)}");
    using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, 4096, leaveOpen: false);
    char[] buffer = new char[maxCharacters + 1];
    int count = 0;
    while (count < buffer.Length)
    {
        int read = reader.Read(buffer, count, buffer.Length - count);
        if (read == 0) break;
        count += read;
    }
    if (count == 0 || count > maxCharacters || reader.Peek() != -1)
        throw new InvalidDataException($"Input file is empty or too large: {Path.GetFileName(fullPath)}");
    return new string(buffer, 0, count);
}

static void WriteExclusive(string path, string content)
{
    string fullPath = Path.GetFullPath(path);
    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
    using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
    using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true);
    writer.Write(content);
    writer.Flush();
    stream.Flush(true);
}

static void EnsureDistinctPaths(params string[] paths)
{
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (string path in paths)
        if (!seen.Add(Path.GetFullPath(path)))
            throw new InvalidOperationException("Signing inputs and outputs must use distinct paths.");
}

static UpdateManifest ParseStrictManifest(string json, JsonTypeInfo<UpdateManifest> manifestType, bool requireSignature)
{
    using (var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8 }))
    {
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid manifest.");
        var expectedNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "schemaVersion", "version", "tag", "assetName", "sha256", "sizeBytes", "publishedUtc", "notes", "signature"
        };
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
            if (!names.Add(property.Name)) throw new InvalidDataException("Manifest contains duplicate fields.");
            else if (!expectedNames.Contains(property.Name)) throw new InvalidDataException("Manifest contains an unexpected field.");
        if (names.Count != expectedNames.Count) throw new InvalidDataException("Manifest is missing a required field.");
    }

    UpdateManifest manifest = JsonSerializer.Deserialize(json, manifestType) ?? throw new InvalidDataException("Invalid manifest.");
    if (manifest.SchemaVersion != 1 || !IsStrictSemanticVersion(manifest.Version) || manifest.Tag != "v" + manifest.Version || manifest.AssetName != "SpaceLens-Setup.exe")
        throw new InvalidDataException("Manifest identifies an unsupported release.");
    if (manifest.SizeBytes <= 0 || manifest.SizeBytes > 250L * 1024 * 1024 ||
        manifest.Sha256 is null || manifest.Sha256.Length != 64 || !manifest.Sha256.All(Uri.IsHexDigit) ||
        manifest.Notes is null || manifest.Notes.Length > 4000 ||
        manifest.PublishedUtc < new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero) ||
        manifest.PublishedUtc > DateTimeOffset.UtcNow.AddDays(1) || manifest.Signature is null)
        throw new InvalidDataException("Manifest contains invalid values.");
    if (requireSignature)
    {
        byte[] signature;
        try { signature = Convert.FromBase64String(manifest.Signature); }
        catch (FormatException) { throw new CryptographicException("Manifest signature is invalid."); }
        if (signature.Length != 64) throw new CryptographicException("Manifest signature has an invalid length.");
    }
    else if (manifest.Signature.Length != 0)
    {
        throw new InvalidDataException("Unsigned manifest must have an empty signature.");
    }
    return manifest;
}

static bool IsStrictSemanticVersion(string value)
{
    if (value is null) return false;
    string[] parts = value.Split('.');
    if (parts.Length != 3) return false;
    foreach (string part in parts)
        if (part.Length == 0 || (part.Length > 1 && part[0] == '0') ||
            !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < 0)
            return false;
    return true;
}

static void RunSelfTests(JsonTypeInfo<UpdateManifest> manifestType)
{
    var manifest = new UpdateManifest
    {
        Version = "1.6.1",
        Tag = "v1.6.1",
        Sha256 = new string('A', 64),
        SizeBytes = 1234,
        PublishedUtc = DateTimeOffset.UtcNow,
        Notes = "self-test"
    };
    string unsigned = JsonSerializer.Serialize(manifest, manifestType);
    _ = ParseStrictManifest(unsigned, manifestType, false);

    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    ExpectFailure(() => AssertPublicOnlyPem(key.ExportPkcs8PrivateKeyPem()));
    byte[] canonical = Encoding.UTF8.GetBytes(manifest.Canonical());
    byte[] signature = key.SignData(canonical, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    manifest.Signature = Convert.ToBase64String(signature);
    UpdateManifest parsed = ParseStrictManifest(JsonSerializer.Serialize(manifest, manifestType), manifestType, true);
    if (!key.VerifyData(Encoding.UTF8.GetBytes(parsed.Canonical()), Convert.FromBase64String(parsed.Signature), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        throw new CryptographicException("ReleaseSigner positive self-test failed.");

    ExpectFailure(() => ParseStrictManifest(unsigned.Replace("\"version\": \"1.6.1\"", "\"version\": \"1.6.1\", \"version\": \"9.9.9\"", StringComparison.Ordinal), manifestType, false));
    ExpectFailure(() => ParseStrictManifest(unsigned.Replace(new string('A', 64), "not-a-hash", StringComparison.Ordinal), manifestType, false));
    manifest.Signature = Convert.ToBase64String(new byte[63]);
    ExpectFailure(() => ParseStrictManifest(JsonSerializer.Serialize(manifest, manifestType), manifestType, true));

    CryptographicOperations.ZeroMemory(canonical);
    CryptographicOperations.ZeroMemory(signature);
}

static void ExpectFailure(Action action)
{
    try { action(); }
    catch { return; }
    throw new InvalidOperationException("A ReleaseSigner negative self-test unexpectedly succeeded.");
}

internal sealed class UpdateManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Version { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public string AssetName { get; set; } = "SpaceLens-Setup.exe";
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTimeOffset PublishedUtc { get; set; }
    public string Notes { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
    public string Canonical() => string.Join('\n',
        "spacelens-update-v1",
        SchemaVersion.ToString(CultureInfo.InvariantCulture),
        Version,
        Tag,
        AssetName,
        Sha256.ToUpperInvariant(),
        SizeBytes.ToString(CultureInfo.InvariantCulture),
        PublishedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        Convert.ToBase64String(Encoding.UTF8.GetBytes(Notes)));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(UpdateManifest))]
internal sealed partial class ReleaseJsonContext : JsonSerializerContext { }
