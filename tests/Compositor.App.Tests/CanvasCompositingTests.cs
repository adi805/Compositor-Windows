using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Compositor.App;
using Compositor.Core;
using Compositor.Core.Imaging;
using Xunit;

namespace Compositor.App.Tests;

/// <summary>
/// The canvas has to show what the file will contain. It drew each layer's bitmap straight into
/// its placement rect, applying neither opacity nor blend mode, so a layer at zero opacity stayed
/// visible on screen and disappeared from the export, and Multiply rendered as ordinary alpha-over.
///
/// These tests read the actual rendered frame and compare it against the exporter's own output for
/// the same document, which is the only comparison that catches the class of bug: asserting that
/// the canvas "applies opacity" would pass for any formula that happens to be monotonic.
/// </summary>
public sealed class CanvasCompositingTests
{
    // Fully opaque, so premultiplication is the identity and a mismatch cannot hide in the alpha.
    private static readonly (byte R, byte G, byte B) Base = (200, 60, 40);
    private static readonly (byte R, byte G, byte B) Top = (30, 180, 220);

    [AvaloniaFact]
    public void Canvas_ZeroOpacityLayer_MatchesAHiddenOne()
    {
        // The reported symptom, stated as a comparison: an opaque layer at zero opacity has to look
        // exactly like the same layer hidden. Both must show the checkerboard, and before the fix
        // the zero-opacity one showed the layer's colour instead.
        var invisible = Frame(OpacityDocument(0.0));
        var hidden = Frame(HiddenDocument());

        Assert.Equal(Sample(hidden), Sample(invisible));
    }

    [AvaloniaFact]
    public void Canvas_OpacityChangesWhatIsDrawn()
    {
        // Control for the case above: the comparison has to be able to fail, so full opacity must
        // differ from zero opacity and from the half-transparent blend.
        var opaque = Sample(Frame(OpacityDocument(1.0)));
        var half = Sample(Frame(OpacityDocument(0.5)));
        var none = Sample(Frame(OpacityDocument(0.0)));

        Assert.NotEqual(none, opaque);
        Assert.NotEqual(none, half);
        Assert.NotEqual(half, opaque);
    }

    [AvaloniaFact]
    public void Canvas_MatchesTheExportForEveryBlendMode()
    {
        // The comparison that covers the class: for every blend mode the format supports, the pixel
        // the canvas draws at the canvas centre must be the pixel the exporter writes there.
        var failures = new List<string>();
        foreach (var mode in Enum.GetValues<BlendMode>())
        {
            var doc = BlendDocument(mode);
            var exported = ExportCenter(doc);
            var rendered = Sample(Frame(doc));

            if (!Close(exported, rendered))
            {
                failures.Add($"{mode}: export {exported} canvas {rendered}");
            }
        }

        Assert.Empty(failures);
    }

    [AvaloniaFact]
    public void Canvas_MultiplyIsNotOrdinaryAlphaOver()
    {
        // The specific misreading in the report. If the canvas ignored the blend mode, Multiply and
        // Normal would render the same pixels; they must not, and the Multiply pixel must be the one
        // the exporter produces.
        var multiply = Frame(BlendDocument(BlendMode.Multiply));
        var normal = Frame(BlendDocument(BlendMode.Normal));

        Assert.NotEqual(Sample(normal), Sample(multiply));
        Assert.True(
            Close(ExportCenter(BlendDocument(BlendMode.Multiply)), Sample(multiply)),
            "the canvas must draw what the export writes");
    }

    // ----- fixtures -----

    /// <summary>One opaque layer over the whole canvas at the given opacity.</summary>
    private static Document OpacityDocument(double opacity)
    {
        var doc = new Document(64, 64);
        var layer = new Layer("top") { Opacity = opacity, Transform = LayerTransform.ForCanvas(64, 64) };
        layer.Pixels = Solid(64, 64, Top);
        doc.AddLayer(layer);
        return doc;
    }

    private static Document HiddenDocument()
    {
        var doc = new Document(64, 64);
        var layer = new Layer("top") { IsVisible = false, Transform = LayerTransform.ForCanvas(64, 64) };
        layer.Pixels = Solid(64, 64, Top);
        doc.AddLayer(layer);
        return doc;
    }

    /// <summary>An opaque base with an opaque top layer in the given blend mode, over the whole canvas.</summary>
    private static Document BlendDocument(BlendMode mode)
    {
        var doc = new Document(64, 64);
        var bottom = new Layer("base") { Transform = LayerTransform.ForCanvas(64, 64) };
        bottom.Pixels = Solid(64, 64, Base);
        var top = new Layer("top") { Blend = mode, Transform = LayerTransform.ForCanvas(64, 64) };
        top.Pixels = Solid(64, 64, Top);
        doc.AddLayer(bottom);
        doc.AddLayer(top);
        return doc;
    }

    private static RasterSurface Solid(int width, int height, (byte R, byte G, byte B) colour)
    {
        var surface = new RasterSurface(width, height);
        for (var i = 0; i < surface.Pixels.Length; i += 4)
        {
            surface.Pixels[i] = colour.R;
            surface.Pixels[i + 1] = colour.G;
            surface.Pixels[i + 2] = colour.B;
            surface.Pixels[i + 3] = 255;
        }

        return surface;
    }

    /// <summary>The exporter's own pixel at the centre of the canvas, as RGBA.</summary>
    private static (byte R, byte G, byte B, byte A) ExportCenter(Document doc)
    {
        var (width, height, rgba) = Flatten.ToRgba(doc);
        var i = (((height / 2) * width) + (width / 2)) * 4;
        return (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
    }

    /// <summary>
    /// Renders the editor window headlessly and returns the frame together with the point on it
    /// that corresponds to the centre of the canvas, so a sample lands on the document rather than
    /// on the surrounding chrome.
    /// </summary>
    private static Shot Frame(Document doc)
    {
        var window = new MainWindow(new EditorViewModel(doc));
        window.Show();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        var canvas = window.FindControl<CanvasView>("EditorCanvas")!;
        var origin = canvas.TranslatePoint(new Point(0, 0), window) ?? new Point(0, 0);
        var (x, y, w, h) = canvas.ViewModel!.CanvasRect(canvas.Bounds.Width, canvas.Bounds.Height);
        var point = new Point(origin.X + x + (w / 2), origin.Y + y + (h / 2));

        // The frame can be smaller than the window when the platform scales it, so keep the sample
        // inside it rather than reading past the end.
        return new Shot(frame!, new Point(
            Math.Clamp(point.X, 0, frame!.PixelSize.Width - 1),
            Math.Clamp(point.Y, 0, frame.PixelSize.Height - 1)));
    }

    private sealed record Shot(WriteableBitmap Frame, Point Point);

    /// <summary>Reads one pixel from the captured frame, honouring its channel order and premultiplication.</summary>
    private static (byte R, byte G, byte B, byte A) Sample(Shot shot)
    {
        var x = (int)shot.Point.X;
        var y = (int)shot.Point.Y;
        using var fb = shot.Frame.Lock();
        var bytes = new byte[4];
        Marshal.Copy(fb.Address + (y * fb.RowBytes) + (x * 4), bytes, 0, 4);

        var format = fb.Format;
        var (r, g, b, a) = format == PixelFormat.Bgra8888
            ? (bytes[2], bytes[1], bytes[0], bytes[3])
            : (bytes[0], bytes[1], bytes[2], bytes[3]);

        // A premultiplied buffer stores colour already scaled by alpha; the composite is straight
        // alpha, so undo it before comparing.
        if (format == PixelFormat.Bgra8888 && a != 0 && a != 255)
        {
            r = (byte)Math.Min(255, r * 255 / a);
            g = (byte)Math.Min(255, g * 255 / a);
            b = (byte)Math.Min(255, b * 255 / a);
        }

        return (r, g, b, a);
    }

    private static bool Close((byte R, byte G, byte B, byte A) a, (byte R, byte G, byte B, byte A) b) =>
        Math.Abs(a.R - b.R) <= 2 && Math.Abs(a.G - b.G) <= 2 && Math.Abs(a.B - b.B) <= 2;
}
