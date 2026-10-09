using System.Security.Cryptography;
using System.Text;

namespace Compositor.Core.Update;

/// <summary>
/// Publisher authentication for the update feed, independent of the checksums.
///
/// A checksum manifest fetched from the same release as the package proves the download arrived intact
/// and proves nothing about who produced it: whoever can publish a release can replace both the zip and
/// its <c>SHA256SUMS</c>. This is the second lock, the one that needs a key the release pipeline holds and
/// an attacker who only reached the release page does not.
///
/// The signature covers the checksum manifest, not the zip. Signing the small file and then trusting the
/// hashes inside it is what keeps the package out of the verifier: a 40 MB zip is verified by its hash,
/// which is verified by a signature over a few hundred bytes.
/// </summary>
public static class ReleaseSignature
{
    /// <summary>The asset holding the detached signature over <c>SHA256SUMS</c>.</summary>
    public const string SignatureAssetName = "SHA256SUMS.sig";

    /// <summary>
    /// What is actually signed: the manifest text as UTF-8, with CRLF collapsed to LF first.
    ///
    /// The normalisation is the point. The same manifest can reach the verifier with either line ending
    /// depending on how git checked it out or how a server framed it, and a signature that depends on which
    /// is a signature that fails for a reason nobody can see.
    /// </summary>
    public static byte[] Payload(string manifestText)
    {
        ArgumentNullException.ThrowIfNull(manifestText);
        return Encoding.UTF8.GetBytes(manifestText.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    /// <summary>
    /// Verifies a base64 signature over <paramref name="manifestText"/> against a PEM public key, which may
    /// hold several keys: during a rotation both the outgoing and incoming keys are accepted, so a release
    /// signed with either verifies.
    ///
    /// Returns false rather than throwing for every malformed input. This runs on data from the network, and
    /// a bad signature has to be a refusal, not an exception that some caller forgets to catch.
    /// </summary>
    public static bool Verify(string manifestText, string? signatureBase64, string? publicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(manifestText)
            || string.IsNullOrWhiteSpace(signatureBase64)
            || string.IsNullOrWhiteSpace(publicKeyPem))
        {
            return false;
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureBase64.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        return Verify(Payload(manifestText), signature, publicKeyPem);
    }

    /// <summary>Verifies a detached signature over already-normalised bytes.</summary>
    public static bool Verify(byte[] payload, byte[] signature, string publicKeyPem)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(publicKeyPem);

        foreach (var pem in SplitKeys(publicKeyPem))
        {
            if (VerifyWith(payload, signature, pem))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a PEM block holds at least one key this verifier can use.</summary>
    public static bool PublicKeyValid(string? publicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(publicKeyPem))
        {
            return false;
        }

        return SplitKeys(publicKeyPem).Any(pem =>
        {
            try
            {
                using var rsa = ImportKey(pem);
                return rsa is not null && rsa.KeySize >= 2048;
            }
            catch (CryptographicException)
            {
                return false;
            }
        });
    }

    /// <summary>How many usable keys a PEM holds. Used to report a rotation rather than guess at it.</summary>
    public static int KeyCount(string? publicKeyPem) =>
        string.IsNullOrWhiteSpace(publicKeyPem)
            ? 0
            : SplitKeys(publicKeyPem).Count(pem =>
            {
                try
                {
                    using var rsa = ImportKey(pem);
                    return rsa is not null && rsa.KeySize >= 2048;
                }
                catch (CryptographicException)
                {
                    return false;
                }
            });

    private static bool VerifyWith(byte[] payload, byte[] signature, string pem)
    {
        try
        {
            using var rsa = ImportKey(pem);
            if (rsa is null)
            {
                return false;
            }

            return rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Imports either PEM shape. <c>ExportRSAPublicKeyPem</c> and <c>openssl rsa -pubout</c> write
    /// <c>BEGIN PUBLIC KEY</c> (SPKI), while <c>ExportRSAPublicKey</c> and older tooling write
    /// <c>BEGIN RSA PUBLIC KEY</c> (PKCS#1). Accepting only one of them means a key that looks right fails
    /// to load, which reads as a bad signature rather than as a format problem.
    /// </summary>
    private static RSA? ImportKey(string pem)
    {
        var rsa = RSA.Create();
        try
        {
            if (pem.Contains("BEGIN RSA PUBLIC KEY", StringComparison.Ordinal))
            {
                rsa.ImportRSAPublicKey(Convert.FromBase64String(Body(pem)), out _);
            }
            else if (pem.Contains("BEGIN PUBLIC KEY", StringComparison.Ordinal))
            {
                rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(Body(pem)), out _);
            }
            else
            {
                rsa.Dispose();
                return null;
            }

            return rsa;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            rsa.Dispose();
            return null;
        }
    }

    /// <summary>The base64 body of one PEM block, headers and whitespace removed.</summary>
    private static string Body(string pem)
    {
        var lines = pem.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("-----", StringComparison.Ordinal));
        return string.Concat(lines);
    }

    /// <summary>Every PEM block in the text, so a rotation can carry both keys.</summary>
    private static List<string> SplitKeys(string pem)
    {
        var keys = new List<string>();
        var lines = pem.Split('\n');
        var current = new StringBuilder();
        var inBlock = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("-----BEGIN", StringComparison.Ordinal))
            {
                inBlock = true;
                current.Clear();
                current.AppendLine(line);
                continue;
            }

            if (line.StartsWith("-----END", StringComparison.Ordinal))
            {
                if (inBlock)
                {
                    current.AppendLine(line);
                    keys.Add(current.ToString());
                }

                inBlock = false;
                continue;
            }

            if (inBlock)
            {
                current.AppendLine(line);
            }
        }

        return keys;
    }
}
