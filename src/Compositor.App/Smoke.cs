using System.Globalization;
using Compositor.App.Imaging;
using Compositor.Core;
using Compositor.Core.Imaging;
using Compositor.Core.Project;

namespace Compositor.App;

/// <summary>
/// The headless self-check CI runs against a published build.
///
/// It renders. The previous smoke round-tripped a blank document, which proves the project format
/// loads and nothing else: a build whose compositor, PNG codec, or native Skia binaries were missing
/// or mismatched would have passed it. The steps below touch every layer that a packaged build has
/// to get right, and each one checks a value it computed rather than merely not throwing.
/// </summary>
public static class Smoke
{
    public static int Run(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);

        // An unexpected exception is a failure of the build under test, not a crash to hand the user as
        // a stack trace: a packaged build that cannot render has to say so and exit non-zero, which is
        // what the release gate reads.
        try
        {
            return RunChecks(output);
        }
        catch (Exception ex)
        {
            return Fail(output, $"an unexpected failure: {ex}");
        }
    }

    private static int RunChecks(TextWriter output)
    {
        var document = SampleDocument();
        var (width, height, rgba) = Flatten.ToRgba(document);

        // 1. Compositing. Two opaque layers, the top one multiplied, so the expected pixel is
        // arithmetic rather than a recorded value: a build that skipped the blend mode would produce
        // the top layer's colour instead.
        var centre = Pixel(rgba, width, height, width / 2, height / 2);
        var expected = ExpectedMultiply();
        if (!Near(centre, expected))
        {
            return Fail(output, $"compositing produced {centre} at the centre; expected {expected}");
        }

        // 2. Project round-trip with real pixels, not a blank document.
        var path = Path.Combine(Path.GetTempPath(), $"compositor-smoke-{Guid.NewGuid():N}.comp");
        try
        {
            ProjectStore.Save(document, path);
            var reloaded = ProjectStore.Load(path);
            if (reloaded.Layers.Count != 2)
            {
                return Fail(output, $"the reloaded project has {reloaded.Layers.Count} layers; expected 2");
            }

            var baseLayer = reloaded.Layers[0].Pixels;
            if (baseLayer is null)
            {
                return Fail(output, "the reloaded base layer came back without pixels");
            }

            if (baseLayer.Width != width || baseLayer.Height != height)
            {
                return Fail(output, $"the reloaded layer is {baseLayer.Width}x{baseLayer.Height}; expected {width}x{height}");
            }

            var basePixel = Pixel(baseLayer.Pixels, baseLayer.Width, baseLayer.Height, 0, 0);
            if (!Near(basePixel, (200, 60, 40, 255)))
            {
                return Fail(output, $"the reloaded base layer pixel is {basePixel}; expected (200, 60, 40, 255)");
            }
        }
        finally
        {
            File.Delete(path);
        }

        // 3. The managed PNG codec, both directions.
        using var png = new MemoryStream();
        Png.Encode(png, width, height, rgba, dpi: 300);
        png.Position = 0;
        var (pngWidth, pngHeight, pngPixels) = Png.Decode(png, out var dpi);
        if (pngWidth != width || pngHeight != height || !pngPixels.AsSpan().SequenceEqual(rgba))
        {
            return Fail(output, $"the PNG round-trip returned {pngWidth}x{pngHeight} with differing pixels");
        }

        if (dpi is null || Math.Abs(dpi.Value - 300) > 1)
        {
            return Fail(output, $"the PNG density came back as {dpi?.ToString(CultureInfo.InvariantCulture) ?? "nothing"}; expected 300");
        }

        // 4. The native Skia codec, which is the part a packaged build can get wrong without any
        // managed code failing: libSkiaSharp is a native asset, and a build that shipped the wrong one
        // throws here rather than in a unit test.
        var jpeg = SkiaCodec.EncodeJpeg(rgba, width, height, new JpegOptions { Quality = 0.85 }, dpi: 300);
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            return Fail(output, "the JPEG encoder did not produce a JPEG");
        }

        var decoded = SkiaCodec.Decode(jpeg);
        if (decoded.Width != width || decoded.Height != height)
        {
            return Fail(output, $"the JPEG decoded as {decoded.Width}x{decoded.Height}; expected {width}x{height}");
        }

        var jpegCentre = Pixel(decoded.Rgba, decoded.Width, decoded.Height, decoded.Width / 2, decoded.Height / 2);
        if (!Near(jpegCentre, expected, tolerance: 6))
        {
            return Fail(output, $"the JPEG decoded to {jpegCentre} at the centre; expected about {expected}");
        }

        output.WriteLine(
            $"Compositor.Windows smoke OK: composite {width}x{height} {centre}, "
            + $"PNG {png.Length} bytes at {dpi:0} dpi, JPEG {jpeg.Length} bytes decoded, project round-trip OK");
        return 0;
    }

    /// <summary>A two-layer canvas whose centre pixel is a known multiply result.</summary>
    private static Document SampleDocument()
    {
        const int size = 64;
        var document = new Document(size, size);

        var bottom = new Layer("base") { Transform = LayerTransform.ForCanvas(size, size) };
        bottom.Pixels = Solid(size, size, 200, 60, 40);
        document.AddLayer(bottom);

        var top = new Layer("multiply") { Blend = BlendMode.Multiply, Transform = LayerTransform.ForCanvas(size, size) };
        top.Pixels = Solid(size, size, 30, 180, 220);
        document.AddLayer(top);

        return document;
    }

    /// <summary>What multiply of the two fixture colours has to produce, derived rather than recorded.</summary>
    private static (byte R, byte G, byte B, byte A) ExpectedMultiply()
    {
        var blend = new RasterSurface(1, 1);
        blend.SetPixel(0, 0, 200, 60, 40, 255);
        var source = new RasterSurface(1, 1);
        source.SetPixel(0, 0, 30, 180, 220, 255);

        Blend.Compose(
            BlendMode.Multiply, 1f,
            blend.Pixels[0], blend.Pixels[1], blend.Pixels[2], blend.Pixels[3],
            source.Pixels[0], source.Pixels[1], source.Pixels[2], source.Pixels[3],
            out var r, out var g, out var b, out var a);
        return (r, g, b, a);
    }

    private static RasterSurface Solid(int width, int height, byte r, byte g, byte b)
    {
        var surface = new RasterSurface(width, height);
        for (var i = 0; i < surface.Pixels.Length; i += 4)
        {
            surface.Pixels[i] = r;
            surface.Pixels[i + 1] = g;
            surface.Pixels[i + 2] = b;
            surface.Pixels[i + 3] = 255;
        }

        return surface;
    }

    private static (byte R, byte G, byte B, byte A) Pixel(byte[] rgba, int width, int height, int x, int y)
    {
        if (x < 0 || y < 0 || x >= width || y >= height)
        {
            return default;
        }

        var i = (((y * width) + x) * 4);
        return (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
    }

    private static bool Near((byte R, byte G, byte B, byte A) a, (byte R, byte G, byte B, byte A) b, int tolerance = 2) =>
        Math.Abs(a.R - b.R) <= tolerance
        && Math.Abs(a.G - b.G) <= tolerance
        && Math.Abs(a.B - b.B) <= tolerance;

    private static int Fail(TextWriter output, string message)
    {
        output.WriteLine($"Compositor.Windows smoke FAILED: {message}");
        return 1;
    }
}
