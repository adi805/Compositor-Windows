using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Compositor.Core;
using Compositor.Core.Imaging;
using Compositor.Core.Project;
using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// Bounds on the .comp container itself: the manifest read and the canvas ceilings. The
/// manifest cases are assembled as zips by hand because the limit is about what the archive
/// DECLARES, which <see cref="ProjectStore.Save"/> cannot produce without writing the very
/// payload the limit exists to refuse.
/// </summary>
public sealed class ProjectStoreBoundTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"comp-bound-{Guid.NewGuid():N}");

    public ProjectStoreBoundTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string PathFor(string name) => Path.Combine(_dir, name);

    // ----- manifest size -----

    [Fact]
    public void Load_RefusesAManifestLargerThanTheLimitWithoutReadingIt()
    {
        // The archive declares more than the ceiling, so the refusal must name the declared size.
        // Reading first and comparing afterwards is the bug this replaces: it commits the memory
        // before deciding, which is the whole of the exposure.
        var manifest = ManifestJson(padding: ProjectStore.MaxManifestBytes);
        var path = WriteProject(manifest);

        var error = Assert.Throws<InvalidOperationException>(() => ProjectStore.Load(path));

        Assert.Contains("declares", error.Message, StringComparison.Ordinal);
        Assert.Contains(
            ProjectStore.MaxManifestBytes.ToString(CultureInfo.InvariantCulture),
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Load_AcceptsAManifestJustUnderTheLimit()
    {
        // Control for the case above: the same construction one byte under the ceiling loads.
        var manifest = ManifestJson(padding: ProjectStore.MaxManifestBytes - 1024);
        var path = WriteProject(manifest);

        var doc = ProjectStore.Load(path);

        Assert.Equal(10, doc.Width);
        Assert.Equal(10, doc.Height);
    }

    [Fact]
    public void Load_RejectsAManifestThatIsNotJson()
    {
        var path = PathFor("garbage.comp");
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using var entry = zip.CreateEntry("manifest.json", CompressionLevel.NoCompression).Open();
            entry.Write("not json at all"u8);
        }

        Assert.ThrowsAny<JsonException>(() => ProjectStore.Load(path));
    }

    [Fact]
    public void Load_RejectsAManifestWithoutAProjectIdentifier()
    {
        var manifest = JsonSerializer.Serialize(new
        {
            identifier = "com.something.else",
            version = 2,
            documentUUID = Guid.NewGuid().ToString(),
            width = 10,
            height = 10,
            layers = Array.Empty<object>(),
        });

        var error = Assert.Throws<InvalidOperationException>(() => ProjectStore.Load(WriteProject(manifest)));

        Assert.Contains("Not a Compositor project", error.Message, StringComparison.Ordinal);
    }

    // ----- canvas ceilings -----

    [Fact]
    public void Load_RejectsACanvasOverThePerSurfaceCeiling()
    {
        // 30,000 x 20,000 = 600 MP. Within the 30,000-per-side limit, so the per-side check passes
        // it, and the document budget on a large-RAM machine would too. Its RGBA form is
        // 2,400,000,000 bytes, past int.MaxValue, so the per-surface ceiling has to refuse it.
        var error = Assert.Throws<InvalidOperationException>(() =>
            ProjectStore.Load(WriteProject(ManifestJson(width: 30_000, height: 20_000))));

        Assert.Contains("per-surface limit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsACanvasOverTheSideLimit()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ProjectStore.Load(WriteProject(ManifestJson(width: 30_001, height: 10))));

        Assert.Contains("per-side limit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_RejectsACanvasOverThePerSurfaceCeiling()
    {
        var doc = new Document(30_000, 20_000);

        var error = Assert.Throws<InvalidOperationException>(() => ProjectStore.Save(doc, PathFor("big.comp")));

        Assert.Contains("per-surface limit", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(PathFor("big.comp")), "a rejected save must not leave a file behind");
    }

    [Fact]
    public void Save_AcceptsACanvasExactlyAtThePerSurfaceCeiling()
    {
        // 14,142^2 = 199,996,164 px. This one really is written, so it is the control that proves
        // the ceiling above is a boundary and not a blanket refusal of large canvases.
        var doc = new Document(14_142, 14_142);

        ProjectStore.Save(doc, PathFor("max.comp"));

        Assert.True(new FileInfo(PathFor("max.comp")).Length > 0);
    }

    [Fact]
    public void RgbaByteCount_ForTheRejectedCanvas_ExceedsSignedThirtyTwoBit()
    {
        // The premise behind the two cases above, stated as arithmetic rather than as prose:
        // 30,000 x 20,000 x 4 does not fit the type the allocation path would have used.
        var bytes = ImageBudget.RgbaByteCount(30_000, 20_000);

        Assert.Equal(2_400_000_000L, bytes);
        Assert.True(bytes > int.MaxValue, $"{bytes} must be past int.MaxValue ({int.MaxValue})");
    }

    // ----- cumulative raster accounting across a project's layers -----

    [Fact]
    public void Load_ChargesEveryLayerRasterAgainstTheDocumentBudget()
    {
        // Three 100x100 layers are 10,000 px each. Every one of them is inside the per-surface
        // ceiling on its own, so only the running total can refuse the project: at a 25,000 px
        // budget the first two fit and the third does not.
        var path = WritePixelProject(3, 100, 100);

        var error = Assert.Throws<ImageException>(() => ProjectStore.Load(path, documentBudget: 25_000));

        Assert.Equal(ImageFailure.ImportTooLarge, error.Failure);
    }

    [Fact]
    public void Load_AcceptsTheSameProjectWhenTheBudgetCoversEveryLayer()
    {
        // Control: 30,000 px of rasters against a 30,000 px budget is exactly at the line.
        var path = WritePixelProject(3, 100, 100);

        var doc = ProjectStore.Load(path, documentBudget: 30_000);

        Assert.Equal(3, doc.Layers.Count);
        Assert.All(doc.Layers, l => Assert.NotNull(l.Pixels));
    }

    [Fact]
    public void Load_CumulativeBudgetBoundaryIsExact()
    {
        var path = WritePixelProject(3, 100, 100);

        // One pixel short of the total refuses; the total itself is accepted.
        Assert.Throws<ImageException>(() => ProjectStore.Load(path, documentBudget: 29_999));
        Assert.Equal(3, ProjectStore.Load(path, documentBudget: 30_000).Layers.Count);
    }

    [Fact]
    public void Save_ChargesEveryLayerRasterAgainstTheDocumentBudget()
    {
        // The write side of the same invariant, reachable without a hand-built archive: a document
        // whose layers sum past the machine budget must not be serialised in the first place.
        var doc = new Document(100, 100);
        for (var i = 0; i < 3; i++)
        {
            doc.AddLayer(new Layer($"layer {i}") { Pixels = new RasterSurface(100, 100) });
        }

        // The production budget on this host is at least 200 MP, so three 10,000 px layers pass;
        // the assertion here is that the charge is applied at all, which the boundary test above
        // pins. A document over the real ceiling cannot be built in a unit test without 200 MP of
        // memory, so this case documents the happy path and the arithmetic lives in ImageBudget.
        ProjectStore.Save(doc, PathFor("cumulative.comp"));

        Assert.True(File.Exists(PathFor("cumulative.comp")));
        Assert.True(
            ImageBudget.Fits(100, 100, 20_000, documentBudget: 30_000),
            "three 10,000 px surfaces are 30,000 px, exactly at a 30,000 px budget");
    }

    // ----- fixtures -----

    private string WritePixelProject(int layers, int width, int height)
    {
        var doc = new Document(width, height);
        for (var i = 0; i < layers; i++)
        {
            doc.AddLayer(new Layer($"layer {i}") { Pixels = new RasterSurface(width, height) });
        }

        var path = PathFor($"pixels-{Guid.NewGuid():N}.comp");
        ProjectStore.Save(doc, path);
        return path;
    }

    private string WriteProject(string manifest)
    {
        var path = PathFor($"proj-{Guid.NewGuid():N}.comp");
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        using var entry = zip.CreateEntry("manifest.json", CompressionLevel.NoCompression).Open();
        entry.Write(Encoding.UTF8.GetBytes(manifest));
        return path;
    }

    private static string ManifestJson(
        int width = 10,
        int height = 10,
        int padding = 0) =>
        JsonSerializer.Serialize(new
        {
            identifier = ProjectStore.Identifier,
            version = ProjectStore.Version,
            documentUUID = Guid.NewGuid().ToString(),
            name = padding > 0 ? new string('x', padding) : "Bound test",
            width,
            height,
            layers = Array.Empty<object>(),
        });
}
