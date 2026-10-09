using System.Reflection;
using Compositor.Core.Update;

namespace Compositor.App.Update;

/// <summary>
/// Decides what an update check may do, in one place, so the rule can be tested without a network or a
/// dialog. Upstream hands the whole decision to Sparkle; the part here that must not be delegated is the
/// confirmation: a caller that never heard the user say the phrase gets a refusal naming the phrase.
/// </summary>
/// <remarks>
/// The transport is injected rather than an <c>HttpClient</c> field so the headless tests prove the
/// mapping and the gate. Unlike Sparkle, nothing contacts the feed on launch: a dev preview that quietly
/// replaces its own binary is not something worth shipping, so a check happens only when the menu asks.
/// </remarks>
public sealed class UpdateChecker
{
    /// <summary>The release endpoint for this project. Per-call for tests and for forks.</summary>
    public const string DefaultFeedUrl =
        "https://api.github.com/repos/adi805/Compositor-Windows/releases/latest";

    /// <summary>The exact words a caller must hand to <see cref="InstallAsync"/> before a byte is staged.</summary>
    public const string ConfirmationPhrase = UpdateStager.ConfirmationPhrase;

    private readonly Func<string, Task<string>> _readText;
    private readonly Func<string, Task<string>> _downloadArchive;

    /// <param name="readText">Fetch a text resource: the release JSON and the checksum manifest.</param>
    /// <param name="downloadArchive">Fetch a package to a local path and return that path.</param>
    public UpdateChecker(
        Func<string, Task<string>> readText,
        Func<string, Task<string>>? downloadArchive = null)
    {
        _readText = readText ?? throw new ArgumentNullException(nameof(readText));
        _downloadArchive = downloadArchive ?? DownloadOverHttpAsync;
    }

    /// <summary>Production checker: reads the GitHub Releases feed over HTTPS.</summary>
    public UpdateChecker()
        : this(ReadTextOverHttpAsync)
    {
    }

    /// <summary>
    /// The version this build reports, which is what every comparison starts from. Read from the
    /// informational attribute because that is the one the build stamps from <c>&lt;Version&gt;</c>; the
    /// commit metadata GitHub appends after a <c>+</c> is not part of a version.
    /// </summary>
    public static string InstalledVersion
    {
        get
        {
            var informational = typeof(UpdateChecker).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(informational))
            {
                return "0.0.0";
            }

            var plus = informational.IndexOf('+');
            return plus < 0 ? informational.Trim() : informational[..plus].Trim();
        }
    }

    /// <summary>
    /// What the feed says, checked against what is installed. Reports no release when the feed is
    /// unreadable or names nothing newer: an update check that cannot reach the network is a shrug, not
    /// an error dialog.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(string installedVersion, string feedUrl = DefaultFeedUrl)
    {
        try
        {
            var first = ReleaseManifest.Parse(await _readText(feedUrl).ConfigureAwait(false));
            if (first is not { } release || release.ChecksumsUrl is not { Length: > 0 } sumsUrl)
            {
                // No manifest asset means no way to verify what we would download. That is not an update.
                return UpdateCheckResult.UpToDate(installedVersion);
            }

            var sums = await _readText(sumsUrl).ConfigureAwait(false);

            // Publisher authentication, before the hashes inside the manifest are believed. The signature
            // covers the manifest, so verifying it here is what makes every hash below trustworthy; doing
            // it after would authenticate nothing, since the hash is what the signature is there to vouch
            // for. A build with no pinned key accepts and says so in the result.
            var signature = release.SignatureUrl is { Length: > 0 } signatureUrl
                ? await _readText(signatureUrl).ConfigureAwait(false)
                : null;
            var trust = UpdateTrust.Check(sums, signature);
            if (!trust.Trusted)
            {
                return UpdateCheckResult.Rejected(installedVersion, trust.Reason);
            }

            var resolved = ReleaseManifest.Resolve(release, sums);
            if (resolved is not { } candidate)
            {
                return UpdateCheckResult.UpToDate(installedVersion);
            }

            return !candidate.IsPrerelease && AppVersion.IsNewer(candidate.Tag, installedVersion)
                ? UpdateCheckResult.Available(candidate, installedVersion, trust.Reason)
                : UpdateCheckResult.UpToDate(installedVersion);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or TaskCanceledException
            or InvalidOperationException or NotSupportedException or System.Text.Json.JsonException
            or UriFormatException)
        {
            return UpdateCheckResult.Failed(installedVersion, error.Message);
        }
    }

    /// <summary>
    /// Stage a release that <see cref="CheckAsync"/> already verified the shape of. Without the
    /// confirmation phrase nothing is written and the refusal says why. The hash always comes from the
    /// checked release, never from the caller: an install that could name its own expected checksum could
    /// as well name its own package.
    /// </summary>
    public async Task<UpdateStagerOutcome> InstallAsync(
        UpdateCheckResult check, string installDirectory, string? confirmation)
    {
        if (!check.HasRelease)
        {
            return UpdateStagerOutcome.NothingToInstall;
        }

        if (confirmation != ConfirmationPhrase)
        {
            return UpdateStagerOutcome.NotConfirmed;
        }

        try
        {
            var path = await _downloadArchive(check.Release!.Value.DownloadUrl).ConfigureAwait(false);
            var staged = UpdateStager.Stage(installDirectory, path, check.Release.Value, confirmation);
            return new UpdateStagerOutcome(staged.Succeeded, staged.Reason, staged.StagingPath);
        }
        catch (Exception error) when (error is HttpRequestException or IOException
            or UnauthorizedAccessException or TaskCanceledException)
        {
            return new UpdateStagerOutcome(false, error.Message, null);
        }
    }

    private static async Task<string> ReadTextOverHttpAsync(string url)
    {
        using var client = NewClient();
        return await client.GetStringAsync(url).ConfigureAwait(false);
    }

    private static async Task<string> DownloadOverHttpAsync(string url)
    {
        using var client = NewClient();
        var path = Path.Combine(Path.GetTempPath(),
            "compositor-update-" + Guid.NewGuid().ToString("N")[..8] + ".zip");
        await using (var stream = File.Create(path))
        {
            using var response = await client.GetStreamAsync(url).ConfigureAwait(false);
            await response.CopyToAsync(stream).ConfigureAwait(false);
        }

        return path;
    }

    private static HttpClient NewClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        // The GitHub API answers 403 to a request with no identifying User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Compositor.Windows");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}

/// <summary>Result of a version check: what is installed, and whether the feed offers anything newer.</summary>
public readonly record struct UpdateCheckResult(
    string InstalledVersion,
    ReleaseInfo? Release,
    string? Error,
    bool SignatureRejected = false,
    string? Trust = null)
{
    public bool HasRelease => Release is not null;

    public static UpdateCheckResult UpToDate(string installed) => new(installed, null, null);

    public static UpdateCheckResult Available(ReleaseInfo release, string installed, string? trust = null) =>
        new(installed, release, null, SignatureRejected: false, trust);

    public static UpdateCheckResult Failed(string installed, string error) => new(installed, null, error);

    /// <summary>
    /// The release exists but its checksum manifest could not be authenticated. Kept distinct from
    /// <see cref="Failed"/> because the two need different words: a network problem is transient and worth
    /// retrying, a signature that does not verify is a release that must not be installed.
    /// </summary>
    public static UpdateCheckResult Rejected(string installed, string reason) =>
        new(installed, null, reason, SignatureRejected: true);

    /// <summary>One line for the status strip: enough to act on, and no more.</summary>
    public string Summary => HasRelease
        ? $"Version {Release!.Value.Tag} is available. You have {InstalledVersion}. Confirm to install it."
        : SignatureRejected
            ? $"Update refused: {Error}"
            : Error is { Length: > 0 }
                ? $"Update check failed: {Error}"
                : $"You are on the newest version ({InstalledVersion}).";
}

/// <summary>Whether an install went ahead, and why not if it did not.</summary>
public readonly record struct UpdateStagerOutcome(bool Succeeded, string Reason, string? StagingPath)
{
    public static UpdateStagerOutcome NothingToInstall => new(false, "There is nothing newer to install.", null);

    public static UpdateStagerOutcome NotConfirmed => new(false,
        $"Nothing was written: an install needs the words \"{UpdateChecker.ConfirmationPhrase}\".", null);
}
