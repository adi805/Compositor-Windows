using System.IO.Compression;
using Compositor.App;
using Compositor.App.Update;
using Compositor.Core.Update;
using Xunit;

namespace Compositor.App.Tests;

/// <summary>
/// The one writer in the updater, tested against a temp directory rather than a real install: the rule
/// that matters is that an unconfirmed request leaves the directory empty, and that is observable without
/// being anywhere near a running executable.
/// </summary>
public sealed class UpdateStagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "compositor-stage-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly string _install;
    private readonly string _archive;
    private readonly string _hash;

    public UpdateStagerTests()
    {
        _install = Path.Combine(_root, "app");
        Directory.CreateDirectory(_install);

        // A real zip hashed from its real bytes. A hand-written checksum here would only prove the code
        // can compare two strings I made up to match.
        var payload = Path.Combine(_root, "payload.bin");
        File.WriteAllText(payload, "the package bytes");
        _archive = Path.Combine(_root, "Compositor-Windows-v0.4.0-win-x64.zip");
        using (var zip = ZipFile.Open(_archive, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(payload, "payload.bin");
        }

        _hash = UpdateStager.Sha256File(_archive);
    }

    private ReleaseInfo Release(string? sha256, long? claimedSize = null) => new(
        "v0.4.0",
        new AppVersion(0, 4, 0),
        IsPrerelease: false,
        "https://example.invalid/v0.4.0",
        Path.GetFileName(_archive),
        "https://example.invalid/package.zip",
        claimedSize ?? new FileInfo(_archive).Length,
        "https://example.invalid/SHA256SUMS",
        sha256);

    [Fact]
    public void NothingIsWrittenWithoutTheConfirmationPhrase()
    {
        // The whole safety property of the feature in one assertion: an update attempt that never heard
        // back from the user must leave the install exactly as it was.
        Assert.Empty(Directory.GetFileSystemEntries(_install));

        var refused = UpdateStager.Stage(_install, _archive, Release(_hash), confirmed: null);
        Assert.False(refused.Succeeded);
        Assert.False(UpdateStager.Stage(_install, _archive, Release(_hash), "yes").Succeeded);
        Assert.False(UpdateStager.Stage(_install, _archive, Release(_hash), "install update").Succeeded);
        Assert.False(UpdateStager.Stage(_install, _archive, Release(_hash), "Install update ").Succeeded);

        Assert.Empty(Directory.GetFileSystemEntries(_install));
        Assert.Contains("only installed when you ask", refused.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AWrongChecksumIsRefusedBeforeAnythingIsUnpacked()
    {
        var result = UpdateStager.Stage(_install, _archive,
            Release(new string('0', 64)), UpdateStager.ConfirmationPhrase);

        Assert.False(result.Succeeded);
        Assert.Contains("does not match the release checksum", result.Reason, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(_install));
    }

    [Fact]
    public void AMissingExpectedChecksumIsRefusedNotSkipped()
    {
        // No hash to check against means no way to know what arrived. That has to be a refusal, not a
        // default-allow.
        var result = UpdateStager.Stage(_install, _archive, Release(null), UpdateStager.ConfirmationPhrase);

        Assert.False(result.Succeeded);
        Assert.Empty(Directory.GetFileSystemEntries(_install));
    }

    [Fact]
    public void AShortDownloadIsRefusedEvenWhenItsOwnBytesHashCorrectly()
    {
        // Hand-crafted trap: the hash covers whatever arrived, so a hash-only check would accept a
        // package that was cut off in transit.
        var result = UpdateStager.Stage(_install, _archive,
            Release(_hash, claimedSize: new FileInfo(_archive).Length + 1), UpdateStager.ConfirmationPhrase);

        Assert.False(result.Succeeded);
        Assert.Contains("bytes", result.Reason, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(_install));
    }

    [Fact]
    public void AVerifiedConfirmedPackageIsStagedBesideTheInstall()
    {
        var result = UpdateStager.Stage(_install, _archive, Release(_hash), UpdateStager.ConfirmationPhrase);

        Assert.True(result.Succeeded, result.Reason);
        Assert.Equal(Path.Combine(_install, UpdateStager.StagingDirectoryName), result.StagingPath);
        Assert.Equal("v0.4.0", result.Tag);
        Assert.Equal(_hash, result.Sha256);

        // Nothing replaced the executable: the update waits in its own folder with the swap script.
        var staged = Directory.GetFiles(result.StagingPath!, "*", SearchOption.AllDirectories);
        Assert.Contains(staged, f => Path.GetFileName(f) == Path.GetFileName(_archive));
        Assert.Contains(staged, f => Path.GetFileName(f) == "apply-update.cmd");
        Assert.Equal(2, staged.Length);
    }

    [Fact]
    public void ASecondAttemptReplacesTheLeftoverFromTheFirst()
    {
        var first = UpdateStager.Stage(_install, _archive, Release(_hash), UpdateStager.ConfirmationPhrase);
        var second = UpdateStager.Stage(_install, _archive, Release(_hash), UpdateStager.ConfirmationPhrase);

        Assert.True(second.Succeeded, second.Reason);
        Assert.Equal(first.StagingPath, second.StagingPath);
        // Exactly one package inside, not two generations of attempts.
        Assert.Single(Directory.GetFiles(second.StagingPath!, "*.zip"));
    }

    [Fact]
    public void AMissingArchiveIsRefusedRatherThanThrown()
    {
        var result = UpdateStager.Stage(_install, Path.Combine(_root, "nope.zip"), Release(_hash),
            UpdateStager.ConfirmationPhrase);

        Assert.False(result.Succeeded);
        Assert.Contains("not there", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLauncherNamesNoPathOfItsOwn()
    {
        // The launcher is the only shell text left, and it deliberately contains no filesystem path:
        // %~dp0 is the folder it sits in, so nothing user-controlled reaches the batch parser, which
        // would expand a percent sign in a path before the command ran. Everything else happens in the
        // app, where paths travel as arguments.
        var result = UpdateStager.Stage(_install, _archive, Release(_hash), UpdateStager.ConfirmationPhrase);
        var launcher = File.ReadAllText(Path.Combine(result.StagingPath!, UpdateStager.ApplyLauncherName));

        Assert.StartsWith("@echo off", launcher, StringComparison.Ordinal);
        Assert.Contains("%~dp0", launcher, StringComparison.Ordinal);
        Assert.Contains(Program.ApplyUpdateSwitch, launcher, StringComparison.Ordinal);
        Assert.Contains(UpdateStager.AppExecutable, launcher, StringComparison.Ordinal);
        Assert.DoesNotContain(_install, launcher, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetFileName(_archive), launcher, StringComparison.Ordinal);
    }

    [Fact]
    public void StagingProducesExactlyThePackageAndTheLauncher()
    {
        var result = UpdateStager.Stage(_install, _archive, Release(_hash), UpdateStager.ConfirmationPhrase);

        var staged = Directory.GetFiles(result.StagingPath!, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetFileName(f) ?? string.Empty)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([Path.GetFileName(_archive), UpdateStager.ApplyLauncherName], staged);
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
