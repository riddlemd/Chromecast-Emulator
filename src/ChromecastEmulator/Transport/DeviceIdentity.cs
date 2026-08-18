using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChromecastEmulator.Transport;

/// Stable device id, bluetooth-ish id and TLS key, persisted so senders that cache
/// device identity keep recognising the emulator across restarts.
public sealed class DeviceIdentity
{
    public required string DeviceId { get; init; }
    public required string BaseStationId { get; init; }
    public required X509Certificate2 Certificate { get; init; }
    public required RSA PrivateKey { get; init; }

    public static DeviceIdentity Load(EmulatorOptions options)
    {
        Directory.CreateDirectory(options.StateDirectory);

        var statePath = Path.Combine(options.StateDirectory, "device.json");
        var certPath = Path.Combine(options.StateDirectory, "device.pfx");

        var state = File.Exists(statePath)
            ? JsonNode.Parse(File.ReadAllText(statePath))!.AsObject()
            : new JsonObject();

        var deviceId = options.DeviceId ?? state["deviceId"]?.GetValue<string>() ?? EmulatorOptions.RandomHex(16);
        var baseStation = state["baseStationId"]?.GetValue<string>() ?? EmulatorOptions.RandomHex(6).ToUpperInvariant();

        state["deviceId"] = deviceId;
        state["baseStationId"] = baseStation;
        File.WriteAllText(statePath, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var certificate = File.Exists(certPath)
            ? X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(certPath), null, X509KeyStorageFlags.Exportable)
            : CreateCertificate(deviceId, certPath);

        return new DeviceIdentity
        {
            DeviceId = deviceId,
            BaseStationId = baseStation,
            Certificate = certificate,
            PrivateKey = certificate.GetRSAPrivateKey()!,
        };
    }

    private static X509Certificate2 CreateCertificate(string deviceId, string path)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={deviceId}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));

        var pfx = generated.Export(X509ContentType.Pfx);
        File.WriteAllBytes(path, pfx);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        // macOS cannot use an in-memory CreateSelfSigned key for SslStream server auth;
        // the PKCS#12 round-trip is what makes the private key usable.
        return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
    }
}
