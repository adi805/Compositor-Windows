using System.Security.Cryptography;
using Compositor.Core.Update;

namespace Compositor.App.Update;

/// <summary>
/// The install half of the updater, and the only place in this app allowed to write an update to disk.
/// Sparkle does this with a signed appcast and a privileged helper; we have neither, so the guarantee is
/// smaller and stated plainly: nothing is written until the user asks for it by name, and a package that
/// does not match the checksum the release published is refused before anything is unpacked.
/// </summary>
/// <remarks>
/// The running <c>Compositor.App.exe</c> cannot overwrite itself while Windows holds the file open, so a
/// staged update waits beside the install with a script that does the swap on the next launch. Staging is
/// reversible by deleting one directory, which is the point: the worst this feature can do is leave a
/// folder behind.
/// </remarks>
public static class UpdateStager
{
    /// <summary>Where a staged update waits for the next launch, relative to the install directory.</summary>
    public const string StagingDirectoryName = "update-staging";

    /// <summary>
    /// What has to be said before anything is written. A bool travelling through the call chain is how a
    /// "definitely optional" prompt quietly becomes always-on, so the gate is a phrase, not a flag.
    /// </summary>
    public const string ConfirmationPhrase = "Install update";

    /// <summary>Name of the executable the apply script restarts.</summary>
    public const string AppExecutable = "Compositor.App.exe";

    /// <summary>Name of the launcher that runs the staged update.</summary>
    public const string ApplyLauncherName = "apply-update.cmd";

    /// <summary>
    /// Verifies and stages a downloaded release. Every refusal reports a reason without touching the disk;
    /// only a confirmed, verified package produces files.
    /// </summary>
    /// <param name="installDirectory">Directory holding the running executable.</param>
    /// <param name="archivePath">The downloaded zip.</param>
    /// <param name="release">The manifest entry that names the expected hash.</param>
    /// <param name="confirmed">Must be exactly <see cref="ConfirmationPhrase"/>; anything else writes nothing.</param>
    public static StageResult Stage(
        string installDirectory,
        string archivePath,
        ReleaseInfo release,
        string? confirmed)
    {
        if (confirmed != ConfirmationPhrase)
        {
            return StageResult.Refused(
                "An update is only installed when you ask for it by name; nothing was written.");
        }

        if (!File.Exists(archivePath))
        {
            return StageResult.Refused($"The downloaded file is not there: {FileName(archivePath)}.");
        }

        var actual = Sha256File(archivePath);
        if (!ReleaseManifest.Verify(release.Sha256, actual))
        {
            return StageResult.Refused(
                $"The download does not match the release checksum (expected {Short(release.Sha256)}, got "
                + $"{Short(actual)}). Nothing was installed.");
        }

        if (release.AssetSizeBytes > 0 && new FileInfo(archivePath).Length != release.AssetSizeBytes)
        {
            return StageResult.Refused(
                $"The download is {new FileInfo(archivePath).Length} bytes but the release says "
                + $"{release.AssetSizeBytes}. A truncated file would verify only by luck, so it was refused.");
        }

        var staging = Path.Combine(installDirectory, StagingDirectoryName);
        // A leftover from an earlier attempt must not survive next to this one: the apply script takes
        // whatever it finds, so it gets exactly this package.
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        Directory.CreateDirectory(staging);
        var stagedArchive = Path.Combine(staging, release.AssetName);
        File.Copy(archivePath, stagedArchive);
        File.WriteAllText(Path.Combine(staging, ApplyLauncherName), ApplyLauncher());

        return StageResult.Staged(staging, release.Tag, actual);
    }

    /// <summary>Hash of a file, streamed so a 40 MB package does not need 40 MB of free memory.</summary>
    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        using var hasher = SHA256.Create();
        return Convert.ToHexString(hasher.ComputeHash(stream)).ToLowerInvariant();
    }

    private static string FileName(string path) => Path.GetFileName(path);

    private static string Short(string? hash) => hash is { Length: >= 12 } ? hash[..12] : "none";

    /// <summary>
    /// The launcher the user runs after closing the app. It contains no paths of its own on purpose:
    /// <c>%~dp0</c> is the folder the file sits in, so the executable beside it is found without anything
    /// user-controlled ever entering a batch context. A path written into a <c>.cmd</c> is subject to
    /// percent expansion, so a directory named <c>%TEMP%</c> would be rewritten before the command ran.
    ///
    /// All the work happens in the app itself, which is the point: quoting rules for nested batch and
    /// PowerShell commands are the injection surface, and .NET passes paths as arguments without any.
    /// </summary>
    private static string ApplyLauncher() =>
        string.Join(
            "\r\n",
            "@echo off",
            "rem Runs the staged update. Nothing is written here on purpose: %~dp0 is this file's folder,",
            "rem so no path from the filesystem is ever expanded by the batch parser.",
            $"\"%~dp0..\\{AppExecutable}\" --apply-update \"%~dp0.\"",
            "exit /b %errorlevel%");
}


/// <summary>Outcome of a staging attempt: either where it landed, or why nothing was written.</summary>
public readonly record struct StageResult(bool Succeeded, string Reason, string? StagingPath, string Tag, string? Sha256)
{
    public static StageResult Refused(string reason) => new(false, reason, null, string.Empty, null);

    public static StageResult Staged(string path, string tag, string sha256) =>
        new(true, "Staged. Close the app, then run update-staging\\apply-update.cmd to install it.", path, tag, sha256);
}
