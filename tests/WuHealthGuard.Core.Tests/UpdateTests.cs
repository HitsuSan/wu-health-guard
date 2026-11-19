using System.Security.Cryptography;
using System.Text;
using WuHealthGuard.Core.Updates;

namespace WuHealthGuard.Core.Tests;

public class UpdateTests
{
    private const string Url = "https://github.com/example/wu-health-guard/releases/download/v0.2.0/WuHealthGuard.exe";
    private static readonly (string Private, string Public) Keys = UpdateSignature.GenerateKeyPair();
    private static readonly string Hash = Convert.ToHexStringLower(SHA256.HashData("exe"u8.ToArray()));

    private static UpdateManifest Signed(string version = "0.2.0", string url = Url, string? hash = null, string? privateKey = null)
    {
        hash ??= Hash;
        return new UpdateManifest(version, url, hash, 3, null, UpdateSignature.Sign(version, url, hash, privateKey ?? Keys.Private));
    }

    [Fact]
    public void Newer_signed_release_is_available() =>
        Assert.Equal(UpdateStatus.Available, UpdateRules.Evaluate(Signed(), "0.1.0", Keys.Public).Status);

    [Theory]
    [InlineData("0.2.0")]
    [InlineData("0.3.0")]
    [InlineData("0.2.0-dev+abc")]
    public void Same_or_older_release_is_up_to_date(string current) =>
        Assert.Equal(UpdateStatus.UpToDate, UpdateRules.Evaluate(Signed(), current, Keys.Public).Status);

    [Fact]
    public void Build_without_public_key_never_updates() =>
        Assert.Equal(UpdateStatus.Disabled, UpdateRules.Evaluate(Signed(), "0.1.0", null).Status);

    [Fact]
    public void Tampered_version_is_rejected() =>
        Assert.Equal(UpdateStatus.Rejected, UpdateRules.Evaluate(Signed() with { Version = "9.9.9" }, "0.1.0", Keys.Public).Status);

    [Fact]
    public void Tampered_hash_is_rejected() =>
        Assert.Equal(UpdateStatus.Rejected, UpdateRules.Evaluate(Signed() with { Sha256 = new string('a', 64) }, "0.1.0", Keys.Public).Status);

    [Fact]
    public void Tampered_url_is_rejected() =>
        Assert.Equal(UpdateStatus.Rejected, UpdateRules.Evaluate(Signed() with { Url = "https://evil.example/x.exe" }, "0.1.0", Keys.Public).Status);

    [Fact]
    public void Signature_from_another_key_is_rejected()
    {
        var other = UpdateSignature.GenerateKeyPair();
        Assert.Equal(UpdateStatus.Rejected, UpdateRules.Evaluate(Signed(privateKey: other.PrivateKey), "0.1.0", Keys.Public).Status);
    }

    [Fact]
    public void Plain_http_is_rejected_even_if_signed() =>
        Assert.Equal(UpdateStatus.Rejected, UpdateRules.Evaluate(Signed(url: "http://example.com/x.exe"), "0.1.0", Keys.Public).Status);

    [Theory]
    [InlineData("not base64!")]
    [InlineData("")]
    public void Garbage_signature_is_rejected(string signature) =>
        Assert.Equal(UpdateStatus.Rejected, UpdateRules.Evaluate(Signed() with { Signature = signature }, "0.1.0", Keys.Public).Status);

    [Fact]
    public void Public_key_can_be_derived_from_the_private_key() =>
        Assert.Equal(Keys.Public, UpdateSignature.PublicKeyFromPrivate(Keys.Private));

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3-beta.1+sha", "1.2.3")]
    public void Versions_parse(string text, string expected) =>
        Assert.Equal(Version.Parse(expected), UpdateRules.ParseVersion(text));

    [Fact]
    public void Hash_check_detects_a_different_file()
    {
        Assert.True(UpdateRules.HashMatches(new MemoryStream("exe"u8.ToArray()), Hash.ToUpperInvariant()));
        Assert.False(UpdateRules.HashMatches(new MemoryStream(Encoding.UTF8.GetBytes("exe2")), Hash));
    }
}
