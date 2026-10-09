using System.Security.Cryptography;
using Compositor.Core.Update;
using Xunit;

namespace Compositor.App.Tests;

/// <summary>
/// Cross-implementation proof: a signature produced by <c>openssl</c> in the release workflow has to verify
/// with the .NET code that ships in the app.
///
/// The unit tests elsewhere generate their keys in process and sign with <c>RSA.SignData</c>, which proves
/// the verifier is self-consistent and proves nothing about the producer. The two sides only meet in
/// production, where a mismatch would show up as every user's update being refused, so the fixture below
/// pins a real pair: the manifest was signed with
/// <c>openssl dgst -sha256 -sign test.key -out SHA256SUMS.sig SHA256SUMS</c> and verified there with
/// <c>openssl dgst -sha256 -verify test.pub</c> before being written down here.
///
/// The key is a throwaway generated for this fixture and the private half was deleted. A public key and a
/// signature are not secrets; the private key is, and it is not in this repository.
/// </summary>
public sealed class UpdateSignatureInteropTests
{
    /// <summary>Exactly the bytes openssl signed.</summary>
    private const string Manifest =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  "
        + "Compositor-Windows-v0.4.0-win-x64.zip\n";

    /// <summary>The public half of the fixture key, as <c>openssl rsa -pubout</c> wrote it.</summary>
    private const string PublicKey = """
        -----BEGIN PUBLIC KEY-----
        MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAryq0SkXtW1sssGYuaAfu
        szHa7JTok0DNrEDJNV0a76wMx/76yxIfoyv9VS5UpVxkJitUD4Sqm6DUqohE0OwA
        oTBrUY2u27cNqjlpiQVs1li6sFP5Kwyl7GBihhkwyHeAroKY+tATqYpl9oV451Cw
        0yokz8YK5/oICNUQX+Ur3/umWpc8cnE1IUbAl0eUn1WUlcVny8fCNWZmTRi/TaWy
        w1SIFMx07qRj8SAq1KSY55LFFwSOn1JZYVwtivQ0Xixol6TWpX/ByM9vZi+rVVXS
        xG3s6nqx4A3e84yiG0DhMHwF+01HasHM89Y5YC5D8fzMJ8SeHDkvlgze6l+iHihp
        aQIDAQAB
        -----END PUBLIC KEY-----
        """;

    /// <summary>base64 of the detached signature openssl wrote.</summary>
    private const string Signature =
        "Fwz0J1y2Bw8R0BtBPr2gi0pfcaECHZIP4KxCU7pTxNj5mF3zV+YDgQOWGwQ2KMxFKn2FO8UR1r76Gz26GmoC"
        + "m9yJGlNK/JDfR5vJ5mvrb3BzDnvilLa0yfcp5ph32HSw5RAZ2SB1R2BWhdbwP/x2cwuO6f1JMrZL0srcVSKebp9"
        + "TC9vH8NBoRlTRT24IjuTFNwCAZ8UP2nVvamQnMUDjpElBTk4s8g1lLDFcAKofpatX2E7INA080rHM+7VgnoVVJs"
        + "hBhTaGhJhDNI5QF7XAJggd9CZHAHrCtzWDovthb2zVVBJNQZA2vH+Z/mDlT/7Rou3k0FuKOhC83zfm/XN2YQ==";

    [Fact]
    public void AnOpensslSignatureVerifiesWithTheShippedVerifier()
    {
        Assert.True(ReleaseSignature.Verify(Manifest, Signature, PublicKey));
    }

    [Fact]
    public void TheSameSignatureCoversTheManifestAfterLineEndingNormalisation()
    {
        // GitHub serves the file as committed and git can check it out either way; the verifier normalises,
        // so a manifest that arrives with CRLF still verifies against a signature over LF.
        Assert.True(ReleaseSignature.Verify(
            Manifest.Replace("\n", "\r\n", StringComparison.Ordinal), Signature, PublicKey));
    }

    [Fact]
    public void OneChangedCharacterBreaksTheOpensslSignature()
    {
        // The negative control for the fixture: the pair is real, so tampering has to break it. Without this
        // the test above would still pass if the verifier returned true unconditionally.
        var forged = Manifest.Replace("aaaa", "bbbb", StringComparison.Ordinal);

        Assert.False(ReleaseSignature.Verify(forged, Signature, PublicKey));
    }

    [Fact]
    public void ADifferentKeyDoesNotVerifyTheOpensslSignature()
    {
        using var stranger = RSA.Create(2048);

        Assert.False(ReleaseSignature.Verify(Manifest, Signature, stranger.ExportSubjectPublicKeyInfoPem()));
    }

    [Fact]
    public void TheFixtureKeyIsUsableAndCountsAsOneKey()
    {
        Assert.True(ReleaseSignature.PublicKeyValid(PublicKey));
        Assert.Equal(1, ReleaseSignature.KeyCount(PublicKey));
    }
}
