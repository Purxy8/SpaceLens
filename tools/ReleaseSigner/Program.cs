using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length == 3 && args[0] == "generate")
{
    if (File.Exists(args[1])) throw new InvalidOperationException("Refusing to overwrite the existing private key.");
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); File.WriteAllText(args[1], key.ExportPkcs8PrivateKeyPem()); File.WriteAllText(args[2], key.ExportSubjectPublicKeyInfoPem()); Console.WriteLine("Release signing key pair created."); return;
}
if (args.Length == 4 && args[0] == "sign")
{
    var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, PropertyNameCaseInsensitive = true };
    var manifest = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(args[2]), options) ?? throw new InvalidDataException("Invalid manifest."); manifest.Signature = "";
    using var key = ECDsa.Create(); key.ImportFromPem(File.ReadAllText(args[1])); manifest.Signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(manifest.Canonical()), HashAlgorithmName.SHA256));
    File.WriteAllText(args[3], JsonSerializer.Serialize(manifest, options) + Environment.NewLine); Console.WriteLine("Update manifest signed."); return;
}
if (args.Length == 3 && args[0] == "verify")
{
    var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true }; var manifest = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(args[2]), options) ?? throw new InvalidDataException("Invalid manifest."); byte[] signature = Convert.FromBase64String(manifest.Signature);
    using var key = ECDsa.Create(); key.ImportFromPem(File.ReadAllText(args[1])); if (!key.VerifyData(Encoding.UTF8.GetBytes(manifest.Canonical()), signature, HashAlgorithmName.SHA256)) throw new CryptographicException("Manifest verification failed."); Console.WriteLine("Update manifest verified."); return;
}
Console.Error.WriteLine("Usage: ReleaseSigner generate <private.pem> <public.pem> | sign <private.pem> <unsigned.json> <signed.json> | verify <public.pem> <signed.json>"); Environment.ExitCode = 2;

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
