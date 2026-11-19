using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WuHealthGuard.Core.Updates;

/// <summary>
/// What a release publishes as <c>update.json</c>. <see cref="Signature"/> covers version, URL and hash, so
/// nobody without the release signing key can make the app install something else.
/// </summary>
public sealed record UpdateManifest(string Version, string Url, string Sha256, long Size, string? Notes, string Signature)
{
    /// <summary>
    /// Major version of the .NET Desktop Runtime the update needs, e.g. "10". The app won't install an update
    /// whose runtime isn't present, since it couldn't start. Not signed: tampering can only block an update,
    /// which withholding the manifest could do anyway.
    /// </summary>
    public string? DesktopRuntime { get; init; }
}

public enum UpdateStatus
{
    /// <summary>This build has no update feed or public key (developer and CI builds).</summary>
    Disabled,

    UpToDate,

    Available,

    /// <summary>The manifest is malformed, not HTTPS, or its signature doesn't verify. Never install.</summary>
    Rejected,
}

public sealed record UpdateDecision(UpdateStatus Status, string Message);

/// <summary>ECDSA P-256 / SHA-256 signing of update manifests.</summary>
public static class UpdateSignature
{
    private const DSASignatureFormat Format = DSASignatureFormat.IeeeP1363FixedFieldConcatenation;

    /// <summary>The exact bytes that are signed. Changing this breaks every existing release.</summary>
    public static byte[] Payload(string version, string url, string sha256) =>
        Encoding.UTF8.GetBytes($"wu-health-guard-update/v1\nversion={version}\nurl={url}\nsha256={sha256.ToLowerInvariant()}\n");

    /// <summary>Base64 PKCS#8 private key and base64 SubjectPublicKeyInfo public key.</summary>
    public static (string PrivateKey, string PublicKey) GenerateKeyPair()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(key.ExportPkcs8PrivateKey()), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    public static string PublicKeyFromPrivate(string privateKeyBase64)
    {
        using var key = ImportPrivate(privateKeyBase64);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    public static string Sign(string version, string url, string sha256, string privateKeyBase64)
    {
        using var key = ImportPrivate(privateKeyBase64);
        return Convert.ToBase64String(key.SignData(Payload(version, url, sha256), HashAlgorithmName.SHA256, Format));
    }

    public static bool Verify(UpdateManifest manifest, string publicKeyBase64)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return key.VerifyData(
                Payload(manifest.Version, manifest.Url, manifest.Sha256),
                Convert.FromBase64String(manifest.Signature),
                HashAlgorithmName.SHA256,
                Format);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    private static ECDsa ImportPrivate(string privateKeyBase64)
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyBase64.Trim()), out _);
        return key;
    }
}

public static class UpdateRules
{
    /// <summary>Parses "1.2.3", "v1.2.3" or "1.2.3-beta+abc" as 1.2.3.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var core = text.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(core, out var v) ? v : null;
    }

    public static UpdateDecision Evaluate(UpdateManifest? manifest, string currentVersion, string? publicKey)
    {
        if (string.IsNullOrWhiteSpace(publicKey))
        {
            return new(UpdateStatus.Disabled, "Updates are off in this build.");
        }

        if (manifest is null || ParseVersion(manifest.Version) is not { } offered)
        {
            return new(UpdateStatus.Rejected, "The update information is malformed.");
        }

        if (!Uri.TryCreate(manifest.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return new(UpdateStatus.Rejected, "The update download isn't HTTPS.");
        }

        if (manifest.Sha256.Length != 64 || !manifest.Sha256.All(Uri.IsHexDigit))
        {
            return new(UpdateStatus.Rejected, "The update has no valid SHA-256 hash.");
        }

        if (!UpdateSignature.Verify(manifest, publicKey))
        {
            return new(UpdateStatus.Rejected, "The update's signature doesn't match. It was not installed.");
        }

        var current = ParseVersion(currentVersion) ?? new Version(0, 0);
        return offered > current
            ? new(UpdateStatus.Available, $"Version {manifest.Version} is available.")
            : new(UpdateStatus.UpToDate, $"Version {currentVersion} is the latest.");
    }

    public static string Sha256Hex(Stream stream) =>
        Convert.ToHexStringLower(SHA256.HashData(stream));

    public static bool HashMatches(Stream stream, string expectedSha256) =>
        string.Equals(Sha256Hex(stream), expectedSha256, StringComparison.OrdinalIgnoreCase);

    public static string FormatSize(long bytes) =>
        (bytes / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
}
