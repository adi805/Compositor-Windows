using System.Text.Json;

namespace Compositor.Core.Update;

/// <summary>
/// One GitHub Release, reduced to what the updater needs: which version it is, which asset holds the app,
/// and the checksum the release itself publishes for that asset.
/// </summary>
/// <param name="Tag">The Git tag, <c>v0.4.0</c>. Kept because that is what the user sees named.</param>
/// <param name="Version">The tag without its <c>v</c>, comparable.</param>
/// <param name="IsPrerelease">True for prereleases, which are never offered as updates.</param>
/// <param name="PageUrl">Release page, shown when the user wants to read the notes.</param>
/// <param name="AssetName">File name of the download, matched against the checksum manifest.</param>
/// <param name="DownloadUrl">Where to fetch it.</param>
/// <param name="AssetSizeBytes">Size the release claims, checked against what arrives.</param>
/// <param name="ChecksumsUrl">The sibling <c>SHA256SUMS</c> asset, or null when the release ships none.</param>
/// <param name="Sha256">Hash of the asset, filled in by <see cref="ReleaseManifest.Resolve"/>; null until read.</param>
/// <param name="SignatureUrl">The <c>SHA256SUMS.sig</c> asset authenticating the manifest, or null when absent.</param>
public readonly record struct ReleaseInfo(
    string Tag,
    AppVersion Version,
    bool IsPrerelease,
    string PageUrl,
    string AssetName,
    string DownloadUrl,
    long AssetSizeBytes,
    string? ChecksumsUrl,
    string? Sha256,
    string? SignatureUrl = null)
{
    /// <summary>Ready to download only once the hash is known: an unverified zip never gets staged.</summary>
    public bool IsDownloadable => DownloadUrl.Length > 0 && Sha256 is { Length: > 0 };
}

/// <summary>
/// Reads the GitHub Releases API response and the <c>SHA256SUMS</c> file that sits beside the zip. Both
/// are pure text in, data out, so the whole updater can be tested without a network: the fixtures in the
/// test project are real responses, pinned as written by the release workflow.
/// </summary>
public static class ReleaseManifest
{
    /// <summary>The app asset, named by our own release workflow. Other zips in a release are source
    /// archives, and staging one of those over a running build would replace an executable with .cs files.</summary>
    private const string ZipSuffix = "-win-x64.zip";

    /// <summary>The checksum manifest asset, by convention named exactly this.</summary>
    private const string ChecksumAssetName = "SHA256SUMS";

    /// <summary>
    /// The release a <c>/releases/latest</c> body describes, or null when it names no Windows zip or its
    /// tag is not a version. Malformed JSON is thrown rather than swallowed: a feed we could not read is a
    /// failed check, and reporting one as "no update available" would be a lie about the network.
    /// Unknown JSON keys are ignored on purpose: GitHub adds fields, and an updater that dies on an
    /// unrecognised one stops working the day the API changes rather than the day the app needs it.
    /// </summary>
    public static ReleaseInfo? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !TryGetString(root, "tag_name", out var tag))
        {
            return null;
        }

        if (!AppVersion.TryParse(tag, out var version))
        {
            return null;
        }

        var prerelease = root.TryGetProperty("prerelease", out var flag) && flag.ValueKind == JsonValueKind.True;
        TryGetString(root, "html_url", out var pageUrl);

        string? assetName = null;
        string? downloadUrl = null;
        long size = 0;
        string? checksumsUrl = null;
        string? signatureUrl = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object
                    || !TryGetString(asset, "name", out var name)
                    || !TryGetString(asset, "browser_download_url", out var url))
                {
                    continue;
                }

                if (name.EndsWith(ZipSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    assetName = name;
                    downloadUrl = url;
                    size = asset.TryGetProperty("size", out var sizeElement)
                        && sizeElement.TryGetInt64(out var parsedSize) ? parsedSize : 0;
                }
                else if (string.Equals(name, ChecksumAssetName, StringComparison.OrdinalIgnoreCase)
                    && checksumsUrl is null)
                {
                    checksumsUrl = url;
                }
                else if (string.Equals(name, ReleaseSignature.SignatureAssetName, StringComparison.OrdinalIgnoreCase)
                    && signatureUrl is null)
                {
                    signatureUrl = url;
                }
            }
        }

        if (assetName is null || downloadUrl is null)
        {
            return null;
        }

        return new ReleaseInfo(tag, version, prerelease, pageUrl ?? string.Empty, assetName, downloadUrl, size,
            checksumsUrl, Sha256: null, signatureUrl);
    }

    /// <summary>
    /// Parses <c>sha256sum</c> output: <c>&lt;64 hex&gt;  &lt;name&gt;</c> per line. Blank lines and short
    /// lines are skipped, and a leading <c>*</c> (binary-mode marker) is dropped from the name. A line
    /// that will not parse is skipped rather than throwing, because one malformed line must not make the
    /// app un-updatable; the hash simply stays unknown, which blocks the install.
    /// </summary>
    public static Dictionary<string, string> ParseChecksums(string text)
    {
        var checksums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return checksums;
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length < 66)
            {
                continue;
            }

            var hash = line[..64];
            if (!IsHexOfKnownLength(hash))
            {
                continue;
            }

            // The separator is two spaces in binary mode, one in text mode; either way the name starts
            // after the whitespace, so trim rather than count.
            var name = line[64..].TrimStart();
            if (name.StartsWith('*'))
            {
                name = name[1..];
            }

            if (name.Length > 0)
            {
                checksums[name] = hash.ToLowerInvariant();
            }
        }

        return checksums;
    }

    /// <summary>
    /// Fills in the hash for the asset this release names. Returns null when the manifest has no line for
    /// it: an asset we cannot verify is an asset we do not install, and guessing would be worse.
    /// </summary>
    public static ReleaseInfo? Resolve(ReleaseInfo? release, string checksumsText)
    {
        if (release is not { } info)
        {
            return null;
        }

        var checksums = ParseChecksums(checksumsText);
        return checksums.TryGetValue(info.AssetName, out var hash)
            ? info with { Sha256 = hash }
            : null;
    }

    /// <summary>
    /// Whether a freshly downloaded file matches what the release said it would be. Compared
    /// case-insensitively over hex, so a manifest written in either convention verifies the same.
    /// </summary>
    public static bool Verify(string? expectedSha256, string? actualSha256) =>
        expectedSha256 is { Length: 64 }
        && actualSha256 is { Length: 64 }
        && string.Equals(expectedSha256.Trim(), actualSha256.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool TryGetString(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(property, out var found) || found.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = found.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool IsHexOfKnownLength(string text)
    {
        foreach (var c in text)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return text.Length == 64;
    }
}
