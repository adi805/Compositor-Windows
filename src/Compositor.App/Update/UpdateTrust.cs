using System.Reflection;
using Compositor.Core.Update;

namespace Compositor.App.Update;

/// <summary>
/// The publisher key this build trusts, and the decision that follows from it.
///
/// The key is compiled in rather than fetched, which is the whole point: a key that arrives over the same
/// channel as the package authenticates nothing, because whoever can serve one can serve the other. It is
/// injected at build time from the MSBuild property <c>CompositorUpdatePublicKey</c> and lands in the
/// assembly metadata, so a build made without it says so instead of silently accepting everything.
/// </summary>
public static class UpdateTrust
{
    /// <summary>Assembly-metadata key the build stamps the PEM into.</summary>
    public const string MetadataKey = "CompositorUpdatePublicKey";

    /// <summary>
    /// The pinned public key, or empty when this build carries none.
    ///
    /// An empty value is a real state with a real consequence and is reported as such: the updater falls
    /// back to checksum-only verification, which establishes that the download is intact and establishes
    /// nothing about who published it. Saying "unsigned build" out loud is the honest option; pretending
    /// the check happened is not.
    /// </summary>
    public static string PinnedPublicKey { get; } = ReadPinnedKey();

    /// <summary>Whether this build authenticates releases with a publisher key.</summary>
    public static bool HasPinnedKey => ReleaseSignature.PublicKeyValid(PinnedPublicKey);

    /// <summary>
    /// The outcome of checking a manifest against this build's key.
    /// </summary>
    /// <param name="Trusted">
    /// True when the manifest may be used. Either it verified, or no key is pinned and the caller has been
    /// told that in <paramref name="Reason"/>.
    /// </param>
    /// <param name="Reason">What happened, in words a user can act on.</param>
    public readonly record struct Decision(bool Trusted, string Reason)
    {
        public static Decision Authenticated(int keys) =>
            new(true, keys > 1
                ? $"The checksum manifest is signed by one of {keys} trusted keys."
                : "The checksum manifest is signed by the trusted publisher key.");

        public static Decision Unpinned() =>
            new(true,
                "This build carries no publisher key, so the checksum manifest was accepted on its hash alone. "
                + "That shows the download is intact; it does not show who published it.");

        public static Decision Rejected(string reason) => new(false, reason);
    }

    /// <summary>
    /// Decides whether a checksum manifest may be used.
    ///
    /// A build WITH a key refuses a manifest whose signature is missing or wrong. A build WITHOUT one
    /// accepts it and says why that is weaker. Both outcomes are explicit, because the failure mode that
    /// matters is not "verification failed", it is "verification was never possible and nobody noticed".
    /// </summary>
    public static Decision Check(string checksumManifest, string? signatureBase64) =>
        Check(checksumManifest, signatureBase64, PinnedPublicKey);

    /// <summary>
    /// The same decision with the key supplied, which is how the enforcing path is tested: the shipped key
    /// is compiled in, so a test cannot swap it, and a branch that only runs in a signed build would
    /// otherwise never run in CI.
    /// </summary>
    public static Decision Check(string checksumManifest, string? signatureBase64, string? pinnedKey)
    {
        if (!ReleaseSignature.PublicKeyValid(pinnedKey))
        {
            return Decision.Unpinned();
        }

        if (string.IsNullOrWhiteSpace(signatureBase64))
        {
            return Decision.Rejected(
                "This build trusts a publisher key and the release carries no signature for its checksum "
                + "manifest, so the update was not installed.");
        }

        if (!ReleaseSignature.Verify(checksumManifest, signatureBase64, pinnedKey))
        {
            return Decision.Rejected(
                "The checksum manifest is not signed by the publisher key this build trusts, so the update "
                + "was not installed. Either the release was tampered with or it was signed with a key this "
                + "build does not know.");
        }

        return Decision.Authenticated(ReleaseSignature.KeyCount(pinnedKey));
    }

    private static string ReadPinnedKey()
    {
        var metadata = typeof(UpdateTrust).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, MetadataKey, StringComparison.Ordinal));
        return metadata?.Value?.Trim() ?? string.Empty;
    }
}
