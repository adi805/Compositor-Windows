using System.IO.Compression;
using System.Text;
using Compositor.App.Update;
using Xunit;

namespace Compositor.App.Tests;

/// <summary>
/// The swap itself, run for real against temp directories rather than asserted by reading a script.
/// The old version generated a batch file with paths interpolated into nested commands, so the only way
/// to test it was to read the text and hope the quoting was right. Here the same code that runs on a
/// user's machine runs in the test, which is what makes "a failed extraction does not delete the install"
/// a fact rather than a reading.
/// </summary>
public sealed class UpdateApplierTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "compositor-apply-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly string _install;

    public UpdateApplierTests()
    {
        _install = Path.Combine(_root, "app");
        Directory.CreateDirectory(_install);
        File.WriteAllText(Path.Combine(_install, UpdateStager.AppExecutable), "old executable");
        File.WriteAllText(Path.Combine(_install, "readme.txt"), "old readme");
        Directory.CreateDirectory(Path.Combine(_install, "native"));
        File.WriteAllText(Path.Combine(_install, "native", "codec.dll"), "old codec");
    }

    private string Staging => Path.Combine(_install, UpdateStager.StagingDirectoryName);

    /// <summary>A staged folder holding a package that replaces every file with new contents.</summary>
    private string StageValidPackage(string marker = "new")
    {
        Directory.CreateDirectory(Staging);
        var payload = Path.Combine(_root, "payload");
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(payload, UpdateStager.AppExecutable), $"{marker} executable");
        File.WriteAllText(Path.Combine(payload, "readme.txt"), $"{marker} readme");
        Directory.CreateDirectory(Path.Combine(payload, "native"));
        File.WriteAllText(Path.Combine(payload, "native", "codec.dll"), $"{marker} codec");

        var archive = Path.Combine(Staging, "package.zip");
        ZipFile.CreateFromDirectory(payload, archive);
        return archive;
    }

    private string InstallText(string name) => File.ReadAllText(Path.Combine(_install, name));

    // ----- the happy path -----

    [Fact]
    public void AValidPackageReplacesTheInstallAndStartsTheNewBuild()
    {
        StageValidPackage();
        var started = new List<string>();

        var result = UpdateApplier.Apply(Staging, _install, waitForExit: () => true, relaunch: started.Add);

        Assert.True(result.Succeeded, result.Reason);
        Assert.Equal("new executable", InstallText(UpdateStager.AppExecutable));
        Assert.Equal("new readme", InstallText("readme.txt"));
        Assert.Equal("new codec", InstallText(Path.Combine("native", "codec.dll")));
        Assert.Equal(Path.Combine(_install, UpdateStager.AppExecutable), result.Executable);
        Assert.Equal([result.Executable!], started);
    }

    [Fact]
    public void ThePackageAndTheBackupAreGoneAfterASuccessfulUpdate()
    {
        StageValidPackage();

        UpdateApplier.Apply(Staging, _install, waitForExit: () => true);

        Assert.False(File.Exists(Path.Combine(Staging, "package.zip")));
        Assert.False(Directory.Exists(Path.Combine(Staging, UpdateApplier.BackupDirectoryName)));
        Assert.False(Directory.Exists(Path.Combine(Staging, UpdateApplier.UnpackedDirectoryName)));
    }

    [Fact]
    public void TheStagingFolderIsNotCopiedIntoTheBackupOrTheInstall()
    {
        // The staging folder lives inside the install, so a copy that walked the install without
        // excluding it would recurse into itself and never finish. This is the case that would hang.
        StageValidPackage();

        var result = UpdateApplier.Apply(Staging, _install, waitForExit: () => true);

        Assert.True(result.Succeeded, result.Reason);
        Assert.Empty(Directory.GetDirectories(Path.Combine(_install, "native")));
        // The launcher and the staging directory survive the swap: they are not part of the install.
        Assert.True(Directory.Exists(Staging));
    }

    // ----- refusals that must leave the install untouched -----

    [Fact]
    public void AMissingStagingFolderIsRefused()
    {
        var result = UpdateApplier.Apply(Path.Combine(_root, "nope"), _install, waitForExit: () => true);

        Assert.False(result.Succeeded);
        Assert.Contains("staging folder is not there", result.Reason, StringComparison.Ordinal);
        Assert.Equal("old executable", InstallText(UpdateStager.AppExecutable));
    }

    [Fact]
    public void AMissingInstallIsRefused()
    {
        StageValidPackage();

        var result = UpdateApplier.Apply(Staging, Path.Combine(_root, "gone"), waitForExit: () => true);

        Assert.False(result.Succeeded);
        Assert.Contains("install folder is not there", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyStagingFolderIsRefused()
    {
        Directory.CreateDirectory(Staging);

        var result = UpdateApplier.Apply(Staging, _install, waitForExit: () => true);

        Assert.False(result.Succeeded);
        Assert.Contains("package is missing", result.Reason, StringComparison.Ordinal);
        Assert.Equal("old executable", InstallText(UpdateStager.AppExecutable));
    }

    [Fact]
    public void ATruncatedPackageIsRefusedAndTheInstallSurvives()
    {
        // The case the checksum cannot catch: the bytes were correct when they were hashed and the file
        // was cut short afterwards. This is the reported "extraction failure does not prevent deletion".
        var archive = StageValidPackage();
        var bytes = File.ReadAllBytes(archive);
        File.WriteAllBytes(archive, bytes[..(bytes.Length / 2)]);

        var result = UpdateApplier.Apply(Staging, _install, waitForExit: () => true);

        Assert.False(result.Succeeded);
        Assert.Contains("not a readable zip", result.Reason, StringComparison.Ordinal);
        Assert.Equal("old executable", InstallText(UpdateStager.AppExecutable));
        Assert.Equal("old readme", InstallText("readme.txt"));
        Assert.True(File.Exists(archive), "the package is kept so a second attempt does not need another download");
    }

    [Fact]
    public void APackageWithoutTheExecutableIsRefusedBeforeAnythingIsReplaced()
    {
        Directory.CreateDirectory(Staging);
        var payload = Path.Combine(_root, "payload-no-exe");
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(payload, "readme.txt"), "new readme");
        ZipFile.CreateFromDirectory(payload, Path.Combine(Staging, "package.zip"));

        var result = UpdateApplier.Apply(Staging, _install, waitForExit: () => true);

        Assert.False(result.Succeeded);
        Assert.Contains("does not contain", result.Reason, StringComparison.Ordinal);
        Assert.Equal("old executable", InstallText(UpdateStager.AppExecutable));
        Assert.Equal("old readme", InstallText("readme.txt"));
    }

    [Fact]
    public void ARunningApplicationIsWaitedForAndTheUpdateIsAbandonedWhenItWillNotExit()
    {
        // Replacing files a running process holds open fails partway through, so this refusal is what
        // keeps the install from being half-updated.
        StageValidPackage();

        var result = UpdateApplier.Apply(Staging, _install, waitForExit: () => false);

        Assert.False(result.Succeeded);
        Assert.Contains("still running", result.Reason, StringComparison.Ordinal);
        Assert.Equal("old executable", InstallText(UpdateStager.AppExecutable));
        Assert.False(Directory.Exists(Path.Combine(Staging, UpdateApplier.UnpackedDirectoryName)));
    }

    [Fact]
    public void TheExitCheckIsConsultedOnceAndATrueLetsTheUpdateProceed()
    {
        // The applier asks the predicate once: polling until the app exits lives in Program, next to the
        // process table it reads. What matters here is that the answer is consulted at all, and that a
        // "safe to proceed" answer actually proceeds.
        StageValidPackage();
        var checks = 0;

        var result = UpdateApplier.Apply(Staging, _install, waitForExit: () => { checks++; return true; });

        Assert.True(result.Succeeded, result.Reason);
        Assert.Equal(1, checks);
        Assert.Equal("new executable", InstallText(UpdateStager.AppExecutable));
    }

    // ----- the restore path -----

    [Fact]
    public void AFailedCopyRestoresThePreviousFiles()
    {
        // Forcing the failure with a file the destination cannot overwrite: a directory in the install
        // where the package wants to put a file. The copy throws, and the install must come back whole.
        StageValidPackage();
        File.Delete(Path.Combine(_install, "readme.txt"));
        Directory.CreateDirectory(Path.Combine(_install, "readme.txt"));
        File.WriteAllText(Path.Combine(_install, "readme.txt", "keep.txt"), "in the way");

        var result = UpdateApplier.Apply(Staging, _install, waitForExit: () => true);

        Assert.False(result.Succeeded);
        Assert.Contains("could not be copied", result.Reason, StringComparison.Ordinal);
        // Whether the executable had been replaced before the failure or not, the install ends up as it
        // was: without the restore it would hold a new executable beside an old readme, which is exactly
        // the half-updated state the rollback exists to prevent.
        Assert.Equal("old executable", InstallText(UpdateStager.AppExecutable));
        Assert.Equal("in the way", File.ReadAllText(Path.Combine(_install, "readme.txt", "keep.txt")));
        Assert.Equal("old codec", InstallText(Path.Combine("native", "codec.dll")));
        Assert.Contains("restored", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingIsLeftBehindWhenTheUpdateIsRefused()
    {
        StageValidPackage();
        var bytes = File.ReadAllBytes(Path.Combine(Staging, "package.zip"));
        File.WriteAllBytes(Path.Combine(Staging, "package.zip"), bytes[..(bytes.Length / 2)]);

        UpdateApplier.Apply(Staging, _install, waitForExit: () => true);

        Assert.False(Directory.Exists(Path.Combine(Staging, UpdateApplier.UnpackedDirectoryName)));
        Assert.False(Directory.Exists(Path.Combine(Staging, UpdateApplier.BackupDirectoryName)));
    }

    // ----- paths the old script could not have handled -----

    [Theory]
    [InlineData("it's here")]
    [InlineData("100% done")]
    [InlineData("a \"quoted\" name")]
    [InlineData("semi;colon & pipe|name")]
    public void UnusualInstallPathsAreHandledBecauseTheyAreArgumentsNotShellText(string directoryName)
    {
        // Each of these broke the generated script in a different way: an apostrophe ended the PowerShell
        // literal, a percent was expanded by the batch parser, a quote ended the string, and the shell
        // metacharacters were interpreted. Here they are just directory names.
        var awkward = Path.Combine(_root, directoryName);
        Directory.CreateDirectory(awkward);
        File.WriteAllText(Path.Combine(awkward, UpdateStager.AppExecutable), "old executable");

        var staging = Path.Combine(awkward, UpdateStager.StagingDirectoryName);
        Directory.CreateDirectory(staging);
        var payload = Path.Combine(_root, "payload-awkward");
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(payload, UpdateStager.AppExecutable), "new executable");
        ZipFile.CreateFromDirectory(payload, Path.Combine(staging, "package.zip"));

        var result = UpdateApplier.Apply(staging, awkward, waitForExit: () => true);

        Assert.True(result.Succeeded, result.Reason);
        Assert.Equal("new executable", File.ReadAllText(Path.Combine(awkward, UpdateStager.AppExecutable)));
    }

    // ----- what staging writes is what applying reads -----

    [Fact]
    public void StagedThenApplied_EndToEnd()
    {
        // The two halves together, using the real staging writer: a package staged by UpdateStager has to
        // be exactly what UpdateApplier expects to find, or the two are only tested against themselves.
        var install = _install;
        var package = Path.Combine(_root, "release.zip");
        var payload = Path.Combine(_root, "release-payload");
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(payload, UpdateStager.AppExecutable), "shipped executable");
        File.WriteAllText(Path.Combine(payload, "readme.txt"), "shipped readme");
        ZipFile.CreateFromDirectory(payload, package);

        var release = new Compositor.Core.Update.ReleaseInfo(
            "v0.4.0",
            new Compositor.Core.Update.AppVersion(0, 4, 0),
            IsPrerelease: false,
            "https://example.invalid/v0.4.0",
            Path.GetFileName(package),
            "https://example.invalid/package.zip",
            new FileInfo(package).Length,
            "https://example.invalid/SHA256SUMS",
            UpdateStager.Sha256File(package));

        var staged = UpdateStager.Stage(install, package, release, UpdateStager.ConfirmationPhrase);
        Assert.True(staged.Succeeded, staged.Reason);

        var applied = UpdateApplier.Apply(staged.StagingPath!, install, waitForExit: () => true);

        Assert.True(applied.Succeeded, applied.Reason);
        Assert.Equal("shipped executable", InstallText(UpdateStager.AppExecutable));
        Assert.Equal("shipped readme", InstallText("readme.txt"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is a nuisance, not a test failure.
        }
    }
}
