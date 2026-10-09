using System.IO.Compression;

namespace Compositor.App.Update;

/// <summary>
/// The swap half of the updater: takes a staged package and replaces the install with it.
///
/// This lives in C# rather than in a generated batch or PowerShell script on purpose. The previous
/// version interpolated raw filesystem paths into nested <c>-Command</c> strings, where an apostrophe in
/// a directory name ends the literal and the rest of the path is parsed as code, and where a percent sign
/// in a path is expanded by the batch parser before anything runs. .NET passes paths as arguments, so
/// there is no quoting rule to get wrong, and the whole flow is testable without a Windows machine.
///
/// The order below is the safety property: extract and validate first, back up second, replace third, and
/// restore on any failure. A package that turns out to be truncated cannot have deleted anything by the
/// time that is discovered, and a failed copy leaves the install as it was.
/// </summary>
public static class UpdateApplier
{
    /// <summary>Where the extracted package waits while it is being validated, inside the staging directory.</summary>
    public const string UnpackedDirectoryName = "unpacked";

    /// <summary>Where the install is copied before it is replaced, inside the staging directory.</summary>
    public const string BackupDirectoryName = "backup";

    /// <summary>How long to wait for the running application to exit before giving up.</summary>
    public static TimeSpan ExitWait { get; } = TimeSpan.FromSeconds(30);

    /// <summary>How often to check whether it has exited.</summary>
    public static TimeSpan ExitPollInterval { get; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Applies a staged package. Returns a result rather than throwing: this runs at startup, and a failed
    /// update has to report why and leave the application usable, not prevent it from opening.
    /// </summary>
    /// <param name="stagingDirectory">The folder holding the package and the launcher.</param>
    /// <param name="installDirectory">The install to replace.</param>
    /// <param name="waitForExit">
    /// Called to find out whether the application is still running. Injected because the real check reads
    /// the process table, and a test that needs a second copy of the app running to exercise this would not
    /// be a test anybody keeps.
    /// </param>
    /// <param name="relaunch">Called with the executable to start once the swap succeeded.</param>
    public static ApplyResult Apply(
        string stagingDirectory,
        string installDirectory,
        Func<bool>? waitForExit = null,
        Action<string>? relaunch = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);

        if (!Directory.Exists(stagingDirectory))
        {
            return ApplyResult.Failed($"The staging folder is not there: {stagingDirectory}.");
        }

        if (!Directory.Exists(installDirectory))
        {
            return ApplyResult.Failed($"The install folder is not there: {installDirectory}.");
        }

        var archive = Directory.GetFiles(stagingDirectory, "*.zip").FirstOrDefault();
        if (archive is null)
        {
            return ApplyResult.Failed("The staged package is missing; nothing was changed.");
        }

        // A package that arrived as a partial download is the case the staging checksum cannot catch,
        // because the checksum was taken before it was copied here.
        try
        {
            using var probe = ZipFile.OpenRead(archive);
            _ = probe.Entries.Count;
        }
        catch (InvalidDataException ex)
        {
            return ApplyResult.Failed($"The staged package is not a readable zip ({ex.Message}); nothing was changed.");
        }

        if (waitForExit is not null && !waitForExit())
        {
            return ApplyResult.Failed(
                $"The application is still running after {ExitWait.TotalSeconds:0} seconds. "
                + "Close it and run the launcher again; nothing was changed.");
        }

        var unpacked = Path.Combine(stagingDirectory, UnpackedDirectoryName);
        var backup = Path.Combine(stagingDirectory, BackupDirectoryName);
        Delete(unpacked);
        Delete(backup);

        // 1. Extract beside the install and check the package actually contains an application. Doing
        // this before anything is replaced is what keeps a truncated package from deleting files on its
        // way to failing.
        try
        {
            ZipFile.ExtractToDirectory(archive, unpacked);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            Delete(unpacked);
            return ApplyResult.Failed($"The package could not be unpacked ({ex.Message}); nothing was changed.");
        }

        var payload = Path.Combine(unpacked, UpdateStager.AppExecutable);
        if (!File.Exists(payload))
        {
            Delete(unpacked);
            return ApplyResult.Failed(
                $"The package does not contain {UpdateStager.AppExecutable}; nothing was changed.");
        }

        // 2. Copy the install aside. Only the top level is walked, and the staging folder is skipped by
        // construction: it lives inside the install, so copying it into itself would recurse forever.
        Directory.CreateDirectory(backup);
        try
        {
            CopyEntries(installDirectory, backup, skip: stagingDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Delete(unpacked);
            Delete(backup);
            return ApplyResult.Failed($"The install could not be backed up ({ex.Message}); nothing was changed.");
        }

        // 3. Replace, and put the backup back if that fails partway. A half-copied install is the one
        // outcome this feature must not produce, so the rollback is unconditional rather than best-effort.
        try
        {
            CopyEntries(unpacked, installDirectory, skip: null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var restored = TryRestore(backup, installDirectory);
            Delete(unpacked);
            return ApplyResult.Failed(
                $"The new build could not be copied ({ex.Message}). "
                + (restored
                    ? "The previous files were restored."
                    : "The previous files could NOT be restored; they are in " + backup + "."));
        }

        var executable = Path.Combine(installDirectory, UpdateStager.AppExecutable);
        if (!File.Exists(executable))
        {
            var restored = TryRestore(backup, installDirectory);
            Delete(unpacked);
            return ApplyResult.Failed(
                "The new build did not land. "
                + (restored ? "The previous files were restored." : "The previous files are in " + backup + "."));
        }

        // 4. Only now is the package expendable. Keeping it on failure is the point: a second attempt
        // should not mean another download.
        Delete(unpacked);
        Delete(backup);
        try
        {
            File.Delete(archive);
        }
        catch (IOException)
        {
            // A package that cannot be deleted is untidy, not a failed update: the install is already new.
        }

        relaunch?.Invoke(executable);
        return ApplyResult.Applied(executable);
    }

    /// <summary>
    /// Copies the top-level entries of <paramref name="source"/> into <paramref name="destination"/>,
    /// leaving <paramref name="skip"/> alone. Directories are copied whole; the skip is by full path so it
    /// can exclude the staging folder that lives inside the install.
    /// </summary>
    private static void CopyEntries(string source, string destination, string? skip)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.GetFileSystemEntries(source))
        {
            if (skip is not null && PathsEqual(entry, skip))
            {
                continue;
            }

            var target = Path.Combine(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                CopyTree(entry, target);
            }
            else
            {
                File.Copy(entry, target, overwrite: true);
            }
        }
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static bool TryRestore(string backup, string installDirectory)
    {
        try
        {
            CopyEntries(backup, installDirectory, skip: null);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void Delete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort: a leftover folder is untidy, not a reason to abandon an update.
        }
    }
}

/// <summary>Outcome of applying a staged package.</summary>
public readonly record struct ApplyResult(bool Succeeded, string Reason, string? Executable)
{
    public static ApplyResult Applied(string executable) =>
        new(true, "The update was applied.", executable);

    public static ApplyResult Failed(string reason) => new(false, reason, null);
}
