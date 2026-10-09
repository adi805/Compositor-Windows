using System.Security.Cryptography;
using System.Text;
using Compositor.App.Update;
using Compositor.Core.Update;
using Xunit;

namespace Compositor.App.Tests;

/// <summary>
/// Publisher authentication: a checksum manifest is only believed when the release's signature over it
/// verifies against the key the build trusts.
///
/// Every key here is generated inside the test, so nothing depends on a real publisher key existing. The
/// keys are RSA-2048 because that is what the release pipeline signs with (<c>openssl dgst -sha256 -sign</c>),
/// and a test that signed with a different primitive would not be testing the shipped path.
/// </summary>
public sealed class UpdateSignatureTests
{
    // Built from explicit "\n" escapes rather than a raw string literal on purpose. A raw string literal
    // takes its line endings from the source file, so this constant would be LF on a Linux checkout and
    // CRLF on a Windows one: the very difference the test below exists to rule out.
    private static readonly string Manifest =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  Compositor-Windows-v0.4.0-win-x64.zip\n"
        + "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb  other.zip\n";

    // ----- the signature itself -----

    [Fact]
    public void ASignatureFromTheTrustedKeyIsAccepted()
    {
        using var key = RSA.Create(2048);

        var signature = Sign(Manifest, key);

        Assert.True(ReleaseSignature.Verify(Manifest, signature, key.ExportSubjectPublicKeyInfoPem()));
    }

    [Fact]
    public void ASignatureFromADifferentKeyIsRejected()
    {
        using var trusted = RSA.Create(2048);
        using var other = RSA.Create(2048);

        var signature = Sign(Manifest, other);

        Assert.False(ReleaseSignature.Verify(Manifest, signature, trusted.ExportSubjectPublicKeyInfoPem()));
    }

    [Fact]
    public void ATamperedManifestIsRejected()
    {
        // The attack this exists for: swap the package and its hash, keep the signature. Changing one
        // character of the manifest has to break it.
        using var key = RSA.Create(2048);
        var signature = Sign(Manifest, key);
        var pem = key.ExportSubjectPublicKeyInfoPem();

        var tampered = Manifest.Replace("aaaaaaaa", "aaaaaaab", StringComparison.Ordinal);

        Assert.NotEqual(Manifest, tampered);
        Assert.False(ReleaseSignature.Verify(tampered, signature, pem));
    }

    [Fact]
    public void TheSignatureDoesNotSurviveAResignedHash()
    {
        // The full attack, spelled out: the attacker replaces the zip, recomputes its hash, and edits the
        // manifest. The hash inside the manifest is now correct and the manifest no longer verifies, which
        // is exactly why the signature covers the manifest rather than each asset.
        using var key = RSA.Create(2048);
        var signature = Sign(Manifest, key);
        var pem = key.ExportSubjectPublicKeyInfoPem();

        var forged = Manifest.Replace("aaaa", "cccc", StringComparison.Ordinal);

        Assert.True(ReleaseSignature.Verify(Manifest, signature, pem));
        Assert.False(ReleaseSignature.Verify(forged, signature, pem));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64 at all!!")]
    [InlineData("aGVsbG8=")] // valid base64, not a signature
    public void AGarbageSignatureIsRejectedRatherThanThrown(string signature)
    {
        using var key = RSA.Create(2048);

        Assert.False(ReleaseSignature.Verify(Manifest, signature, key.ExportSubjectPublicKeyInfoPem()));
    }

    [Fact]
    public void AnEmptyOrUnusableKeyIsRejectedRatherThanThrown()
    {
        using var key = RSA.Create(2048);
        var signature = Sign(Manifest, key);

        Assert.False(ReleaseSignature.Verify(Manifest, signature, string.Empty));
        Assert.False(ReleaseSignature.Verify(Manifest, signature, "-----BEGIN PUBLIC KEY-----\nnot base64\n-----END PUBLIC KEY-----"));
        Assert.False(ReleaseSignature.Verify(Manifest, null, key.ExportSubjectPublicKeyInfoPem()));
        Assert.False(ReleaseSignature.Verify(string.Empty, signature, key.ExportSubjectPublicKeyInfoPem()));
    }

    // ----- key formats and rotation -----

    [Fact]
    public void BothPemShapesVerifyTheSameSignature()
    {
        // openssl rsa -pubout writes SPKI; older tooling and RSA.ExportRSAPublicKey write PKCS#1. A key that
        // looks correct but fails to load reads as a bad signature, so both have to work.
        using var key = RSA.Create(2048);
        var signature = Sign(Manifest, key);

        Assert.True(ReleaseSignature.Verify(Manifest, signature, key.ExportSubjectPublicKeyInfoPem()));
        Assert.True(ReleaseSignature.Verify(Manifest, signature, key.ExportRSAPublicKeyPem()));
        Assert.Contains("BEGIN RSA PUBLIC KEY", key.ExportRSAPublicKeyPem(), StringComparison.Ordinal);
        Assert.Contains("BEGIN PUBLIC KEY", key.ExportSubjectPublicKeyInfoPem(), StringComparison.Ordinal);
    }

    [Fact]
    public void TwoKeysInOnePemAcceptEitherSignature()
    {
        // A rotation: the release can be signed with the outgoing or the incoming key while both are
        // trusted, so the window where the key changes does not break updates.
        using var outgoing = RSA.Create(2048);
        using var incoming = RSA.Create(2048);
        var both = outgoing.ExportSubjectPublicKeyInfoPem() + "\n" + incoming.ExportSubjectPublicKeyInfoPem();

        Assert.Equal(2, ReleaseSignature.KeyCount(both));
        Assert.True(ReleaseSignature.Verify(Manifest, Sign(Manifest, outgoing), both));
        Assert.True(ReleaseSignature.Verify(Manifest, Sign(Manifest, incoming), both));
    }

    [Fact]
    public void AThirdKeyIsStillRejectedWhenTwoAreTrusted()
    {
        using var outgoing = RSA.Create(2048);
        using var incoming = RSA.Create(2048);
        using var stranger = RSA.Create(2048);
        var both = outgoing.ExportSubjectPublicKeyInfoPem() + "\n" + incoming.ExportSubjectPublicKeyInfoPem();

        Assert.False(ReleaseSignature.Verify(Manifest, Sign(Manifest, stranger), both));
    }

    [Fact]
    public void AShortKeyIsNotAccepted()
    {
        // 1024-bit RSA is past what is worth trusting for a signature that decides what gets installed.
        using var weak = RSA.Create(1024);

        Assert.False(ReleaseSignature.PublicKeyValid(weak.ExportSubjectPublicKeyInfoPem()));
        Assert.Equal(0, ReleaseSignature.KeyCount(weak.ExportSubjectPublicKeyInfoPem()));
    }

    // ----- line endings -----

    [Fact]
    public void TheSignatureCoversTheManifestWhateverLineEndingsItArrivedWith()
    {
        // The same manifest can arrive with either ending depending on how git checked it out or how a
        // server framed it. A signature that depends on which one is a signature that fails invisibly.
        using var key = RSA.Create(2048);
        var signature = Sign(Manifest, key);
        var pem = key.ExportSubjectPublicKeyInfoPem();

        // Same lines, CRLF instead of LF.
        var crlf = ManifestWithLineEndings("\r\n");

        Assert.NotEqual(Manifest, crlf);
        Assert.True(ReleaseSignature.Verify(crlf, signature, pem));
        Assert.Equal(ReleaseSignature.Payload(Manifest), ReleaseSignature.Payload(crlf));

        // The other checkout, and the reason the helper strips before re-joining: building it by appending
        // to an already-terminated manifest would leave two newlines and change the bytes that are signed.
        Assert.Equal(Manifest, ManifestWithLineEndings("\n"));
    }

    // ----- the trust decision the app actually makes -----

    [Fact]
    public void TheUnpinnedBuildSaysSoInsteadOfClaimingVerification()
    {
        // The shipped default: no key compiled in. It has to accept the manifest and say plainly that the
        // check did not happen, because the failure that matters is not a failed check, it is a check that
        // was never possible and nobody noticed.
        var decision = UpdateTrust.Check(Manifest, signatureBase64: null);

        Assert.True(decision.Trusted);
        Assert.Contains("no publisher key", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("does not show who published it", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ThisBuildReportsWhichModeItIsIn()
    {
        // Pins the contract the release pipeline depends on: when the workflow passes the key through, the
        // assembly reports a usable key and the checks become enforcing. In a default build there is none.
        if (UpdateTrust.HasPinnedKey)
        {
            Assert.Equal(1, ReleaseSignature.KeyCount(UpdateTrust.PinnedPublicKey));
        }
        else
        {
            Assert.Equal(string.Empty, UpdateTrust.PinnedPublicKey);
        }
    }

    // ----- the enforcing build: a key IS pinned, so the checks must refuse -----

    [Fact]
    public void APinnedBuildRefusesAReleaseThatCarriesNoSignature()
    {
        // The branch that only runs in a signed build. This is the outcome the issue is about: with a key
        // pinned, an unsigned release must not install, and the refusal has to say which of the two
        // problems it is.
        using var key = RSA.Create(2048);

        var decision = UpdateTrust.Check(Manifest, signatureBase64: null, key.ExportSubjectPublicKeyInfoPem());

        Assert.False(decision.Trusted);
        Assert.Contains("carries no signature", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void APinnedBuildRefusesAManifestSignedByAnotherKey()
    {
        using var trusted = RSA.Create(2048);
        using var attacker = RSA.Create(2048);

        var decision = UpdateTrust.Check(
            Manifest, Sign(Manifest, attacker), trusted.ExportSubjectPublicKeyInfoPem());

        Assert.False(decision.Trusted);
        Assert.Contains("not signed by the publisher key", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void APinnedBuildRefusesAManifestTamperedWithAfterSigning()
    {
        using var key = RSA.Create(2048);
        var signature = Sign(Manifest, key);
        var forged = Manifest.Replace("bbbbbbbb", "cccccccc", StringComparison.Ordinal);

        var decision = UpdateTrust.Check(forged, signature, key.ExportSubjectPublicKeyInfoPem());

        Assert.False(decision.Trusted);
        Assert.Contains("not signed by the publisher key", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void APinnedBuildAcceptsAProperlySignedManifestAndSaysSo()
    {
        using var key = RSA.Create(2048);

        var decision = UpdateTrust.Check(
            Manifest, Sign(Manifest, key), key.ExportSubjectPublicKeyInfoPem());

        Assert.True(decision.Trusted);
        Assert.Contains("signed by the trusted publisher key", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void APinnedBuildWithTwoKeysSaysWhichSituationItIsIn()
    {
        using var outgoing = RSA.Create(2048);
        using var incoming = RSA.Create(2048);
        var both = outgoing.ExportSubjectPublicKeyInfoPem() + "\n" + incoming.ExportSubjectPublicKeyInfoPem();

        var decision = UpdateTrust.Check(Manifest, Sign(Manifest, incoming), both);

        Assert.True(decision.Trusted);
        Assert.Contains("one of 2 trusted keys", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnusablePinnedKeyIsTreatedAsNoKeyRatherThanAsRejection()
    {
        // A build whose key failed to embed has no key to enforce with. Reporting that as "rejected" would
        // block every update on a packaging mistake while looking like a security decision; reporting it as
        // unpinned is what it is, and it says so.
        var decision = UpdateTrust.Check(Manifest, "aGVsbG8=", "-----BEGIN PUBLIC KEY-----\ngarbage\n-----END PUBLIC KEY-----");

        Assert.True(decision.Trusted);
        Assert.Contains("no publisher key", decision.Reason, StringComparison.Ordinal);
    }

    private static string Sign(string manifest, RSA key)
    {
        var signature = key.SignData(
            ReleaseSignature.Payload(manifest), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return Convert.ToBase64String(signature);
    }

    /// <summary>
    /// The same manifest re-framed with <paramref name="ending"/>: identical lines, one trailing newline.
    /// The trailing newline is stripped before re-joining so the helper cannot end up appending a second one.
    /// </summary>
    private static string ManifestWithLineEndings(string ending) =>
        Manifest.TrimEnd('\n').Replace("\n", ending, StringComparison.Ordinal) + ending;
}
