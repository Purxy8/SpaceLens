using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

if (args.Length == 3 && args[0] == "generate")
{
    if (File.Exists(args[1])) throw new InvalidOperationException("Refusing to overwrite the existing private key.");
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); File.WriteAllText(args[1], key.ExportPkcs8PrivateKeyPem()); File.WriteAllText(args[2], key.ExportSubjectPublicKeyInfoPem()); Console.WriteLine("Release signing key pair created."); return;
}
if (args.Length == 4 && args[0] == "sign")
{
    var manifest = ParseStrictManifest(File.ReadAllText(args[2]), jsonOptions, false); manifest.Signature = "";
    using var key = ECDsa.Create(); key.ImportFromPem(File.ReadAllText(args[1])); manifest.Signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(manifest.Canonical()), HashAlgorithmName.SHA256));
    File.WriteAllText(args[3], JsonSerializer.Serialize(manifest, jsonOptions) + Environment.NewLine); Console.WriteLine("Update manifest signed."); return;
}
if (args.Length == 3 && args[0] == "verify")
{
    var manifest = ParseStrictManifest(File.ReadAllText(args[2]), jsonOptions, true); byte[] signature = Convert.FromBase64String(manifest.Signature);
    using var key = ECDsa.Create(); key.ImportFromPem(File.ReadAllText(args[1])); if (!key.VerifyData(Encoding.UTF8.GetBytes(manifest.Canonical()), signature, HashAlgorithmName.SHA256)) throw new CryptographicException("Manifest verification failed."); Console.WriteLine("Update manifest verified."); return;
}
Console.Error.WriteLine("Usage: ReleaseSigner generate <private.pem> <public.pem> | sign <private.pem> <unsigned.json> <signed.json> | verify <public.pem> <signed.json>"); Environment.ExitCode = 2;

static UpdateManifest ParseStrictManifest(string json, JsonSerializerOptions options, bool requireSignature)
{
    using (var document = JsonDocument.Parse(json))
    {
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid manifest."); var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject()) if (!names.Add(property.Name)) throw new InvalidDataException("Manifest contains duplicate fields.");
    }
    var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, options) ?? throw new InvalidDataException("Invalid manifest.");
    if (manifest.SchemaVersion != 1 || !IsStrictSemanticVersion(manifest.Version) || manifest.Tag != "v" + manifest.Version || manifest.AssetName != "SpaceLens-Setup.exe") throw new InvalidDataException("Manifest identifies an unsupported release.");
    if (manifest.SizeBytes <= 0 || manifest.SizeBytes > 250L * 1024 * 1024 || manifest.Sha256.Length != 64 || !manifest.Sha256.All(Uri.IsHexDigit) || manifest.Notes.Length > 4000 || manifest.PublishedUtc > DateTimeOffset.UtcNow.AddDays(1)) throw new InvalidDataException("Manifest contains invalid values.");
    if (requireSignature) try { Convert.FromBase64String(manifest.Signature); } catch (FormatException) { throw new CryptographicException("Manifest signature is invalid."); }
    else if (manifest.Signature.Length != 0) throw new InvalidDataException("Unsigned manifest must have an empty signature.");
    return manifest;
}

static bool IsStrictSemanticVersion(string value)
{
    string[] parts = value.Split('.'); if (parts.Length != 3) return false;
    foreach (string part in parts) if (part.Length == 0 || (part.Length > 1 && part[0] == '0') || !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < 0) return false;
    return true;
}

internal sealed class UpdateManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Version { get; set; } = "";
    public string Tag { get; set; } = "";
    public string AssetName { get; set; } = "SpaceLens-Setup.exe";
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTimeOffset PublishedUtc { get; set; }
    public string Notes { get; set; } = "";
    public string Signature { get; set; } = "";
    public string Canonical() => string.Join('\n', "spacelens-update-v1", SchemaVersion.ToString(CultureInfo.InvariantCulture), Version, Tag, AssetName, Sha256.ToUpperInvariant(), SizeBytes.ToString(CultureInfo.InvariantCulture), PublishedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), Convert.ToBase64String(Encoding.UTF8.GetBytes(Notes)));
}
