using Compositor.Core;
using Compositor.Core.Commands;
using Compositor.Core.Imaging;
using Compositor.Core.Selection;
using Xunit;

namespace Compositor.Core.Tests;

public class GeometryCommandsTests
{
    private static Layer PixelLayer(string name, int w = 8, int h = 8, byte alpha = 255)
    {
        var layer = new Layer(name) { Transform = LayerTransform.ForCanvas(w, h) };
        var surface = new RasterSurface(w, h);
        for (var i = 3; i < surface.Pixels.Length; i += 4)
        {
            surface.Pixels[i] = alpha;
        }
        layer.Pixels = surface;
        return layer;
    }

    // --- anchor offset formula (upstream CanvasSizeOptions.offset) ---

    [Theory]
    [InlineData(0, 4, 4, 8, 8, 0.0, 0.0)]     // top-left: content stays, all extra right/bottom
    [InlineData(4, 4, 4, 8, 8, 2.0, 2.0)]     // center: half each way, floor
    [InlineData(8, 4, 4, 8, 8, 4.0, 4.0)]     // bottom-right: all extra left/top
    [InlineData(4, 4, 4, 7, 8, 1.0, 2.0)]     // center grow 3: floor(3*1/2)=1
    [InlineData(4, 8, 8, 5, 5, -2.0, -2.0)]   // center shrink 3: floor(-3*1/2)=-2
    public void AnchorResize_OffsetFollowsUpstreamFormula(
        int anchor, int oldW, int oldH, int newW, int newH, double expectedDx, double expectedDy)
    {
        var doc = new Document(oldW, oldH);
        var layer = PixelLayer("a");
        layer.Transform = new LayerTransform(0, 0, 4, 4, 0, false, false);
        doc.AddLayer(layer);

        var cmd = CanvasResizeCommand.AnchorResize(doc, newW, newH, anchor);
        cmd.Redo();

        Assert.Equal(newW, doc.Width);
        Assert.Equal(expectedDx, layer.Transform.OriginX);
        Assert.Equal(expectedDy, layer.Transform.OriginY);
    }

    [Fact]
    public void AnchorResize_RejectsAnchorOutOfRange()
    {
        var doc = new Document(8, 8);
        Assert.Throws<ArgumentOutOfRangeException>(() => CanvasResizeCommand.AnchorResize(doc, 8, 8, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CanvasResizeCommand.AnchorResize(doc, 8, 8, 9));
    }

    [Fact]
    public void AnchorResize_PixelsStayUntouched_NonDestructive()
    {
        var doc = new Document(8, 8);
        var layer = PixelLayer("a");
        doc.AddLayer(layer);
        var before = (byte[])layer.Pixels!.Pixels.Clone();

        var cmd = CanvasResizeCommand.AnchorResize(doc, 16, 16, 4);
        cmd.Redo();

        Assert.Equal(before, layer.Pixels!.Pixels);
        Assert.Equal(8, layer.Pixels.Width); // non-destructive: surface untouched
    }

    [Fact]
    public void CanvasResize_UndoRestoresSizeAndOrigins()
    {
        var doc = new Document(8, 8);
        var layer = PixelLayer("a");
        layer.Transform = new LayerTransform(2, 3, 4, 4, 0, false, false);
        doc.AddLayer(layer);

        var cmd = CanvasResizeCommand.AnchorResize(doc, 12, 10, 4);
        cmd.Redo();
        Assert.Equal((12, 10), (doc.Width, doc.Height));

        cmd.Undo();
        Assert.Equal((8, 8), (doc.Width, doc.Height));
        Assert.Equal((2.0, 3.0), (layer.Transform.OriginX, layer.Transform.OriginY));
    }

    // --- fill extension layer ---

    [Fact]
    public void FillExtension_InsertedAtBottom_WithTransparentHole()
    {
        var doc = new Document(4, 4);
        var art = PixelLayer("art");
        doc.AddLayer(art);

        var cmd = CanvasResizeCommand.AnchorResize(doc, 8, 8, 0, (10, 20, 30));
        cmd.Redo();

        var fill = doc.Layers[0];
        Assert.Equal("Canvas Extension", fill.Name);
        Assert.Equal((8, 8), (doc.Width, doc.Height));
        // Anchor 0: offset (0,0) -> hole at (0,0,4,4) stays transparent.
        Assert.Equal((byte)0, fill.Pixels!.GetPixel(0, 0).A);
        // Outside the hole: solid fill.
        var (r, g, b, a) = fill.Pixels.GetPixel(7, 7);
        Assert.Equal((byte)10, r);
        Assert.Equal((byte)20, g);
        Assert.Equal((byte)30, b);
        Assert.Equal((byte)255, a);

        cmd.Undo();
        Assert.Single(doc.Layers);
        Assert.Equal((4, 4), (doc.Width, doc.Height));
    }

    [Fact]
    public void FillExtension_NoFillWhenCanvasOnlyShrinks()
    {
        var doc = new Document(8, 8);
        doc.AddLayer(PixelLayer("art"));

        var cmd = CanvasResizeCommand.AnchorResize(doc, 6, 6, 4, (1, 2, 3));
        cmd.Redo();

        Assert.Single(doc.Layers);
    }

    // --- crop ---

    [Fact]
    public void Crop_ShiftsContentByNegativeOrigin()
    {
        var doc = new Document(16, 16);
        var layer = PixelLayer("a");
        doc.AddLayer(layer);

        var cmd = CanvasResizeCommand.Crop(doc, 4, 6, 8, 8);
        cmd.Redo();

        Assert.Equal((8, 8), (doc.Width, doc.Height));
        Assert.Equal(-4.0, layer.Transform.OriginX);
        Assert.Equal(-6.0, layer.Transform.OriginY);

        cmd.Undo();
        Assert.Equal((16, 16), (doc.Width, doc.Height));
        Assert.Equal(0.0, layer.Transform.OriginX);
    }

    [Theory]
    [InlineData(0, 0, 0, 8)]        // width < 1
    [InlineData(0, 0, 8, 0)]        // height < 1
    [InlineData(0, 0, 30_001, 8)]   // width > 30000
    [InlineData(1_000_001, 0, 8, 8)] // origin out of range
    public void Crop_RejectsInvalidRects(int x, int y, int w, int h)
    {
        var doc = new Document(16, 16);
        Assert.Throws<ArgumentOutOfRangeException>(() => CanvasResizeCommand.Crop(doc, x, y, w, h));
    }

    [Fact]
    public void GeometryOps_ClearSelection()
    {
        var doc = new Document(16, 16);
        doc.AddLayer(PixelLayer("a"));
        doc.Selection = DocumentSelection.FromShape(
            SelectionShape.Rectangle(2, 2, 4, 4, SelectionMode.Replace));

        var cmd = CanvasResizeCommand.Crop(doc, 0, 0, 8, 8);
        cmd.Redo();
        Assert.Null(doc.Selection);
    }

    // --- image size ---

    [Fact]
    public void ImageSize_SameDimensions_OnlyUpdatesResolution()
    {
        var doc = new Document(8, 8);
        var layer = PixelLayer("a");
        var before = (byte[])layer.Pixels!.Pixels.Clone();
        doc.AddLayer(layer);

        var cmd = new ImageSizeCommand(doc, 8, 8, 300);
        cmd.Redo();

        Assert.Equal(300, doc.Resolution);
        Assert.Equal(before, layer.Pixels!.Pixels);
    }

    [Fact]
    public void ImageSize_Upscale_ResamplesPixelsAndScalesTransform()
    {
        var doc = new Document(4, 4);
        var layer = PixelLayer("a", 4, 4);
        doc.AddLayer(layer);

        var cmd = new ImageSizeCommand(doc, 8, 8, 72);
        cmd.Redo();

        Assert.Equal((8, 8), (doc.Width, doc.Height));
        Assert.Equal((8, 8), (layer.Pixels!.Width, layer.Pixels.Height));
        Assert.Equal((8.0, 8.0), (layer.Transform.Width, layer.Transform.Height));
        Assert.Equal(0.0, layer.Transform.OriginX);

        cmd.Undo();
        Assert.Equal((4, 4), (doc.Width, doc.Height));
        Assert.Equal((4, 4), (layer.Pixels!.Width, layer.Pixels.Height));
        Assert.Same(doc.Layers[0], layer);
    }

    [Fact]
    public void ImageSize_CoverCanvasBlankLayer_GetsNewCanvasTransform()
    {
        var doc = new Document(4, 4);
        doc.AddLayer(new Layer("blank"));

        var cmd = new ImageSizeCommand(doc, 6, 10, 72);
        cmd.Redo();

        Assert.Equal(LayerTransform.ForCanvas(6, 10), doc.Layers[0].Transform);
    }

    [Fact]
    public void ImageSize_RepeatedUndoRedo_RestoresLayersEveryTime()
    {
        // The bug: Undo() cleared the captured originals but left the capture flag set, so the
        // second Redo skipped recapturing and the second Undo then walked an empty list. The
        // document size came back and the layers did not, which is the state the issue describes.
        var doc = new Document(4, 4) { Resolution = 72 };
        var layer = PixelLayer("a", 4, 4);
        doc.AddLayer(layer);
        var originalPixels = (byte[])layer.Pixels!.Pixels.Clone();
        var originalTransform = layer.Transform;

        var cmd = new ImageSizeCommand(doc, 8, 8, 300);
        cmd.Redo();
        cmd.Undo();
        cmd.Redo();
        cmd.Undo();

        Assert.Equal((4, 4), (doc.Width, doc.Height));
        Assert.Equal(72, doc.Resolution);
        Assert.Equal((4, 4), (layer.Pixels!.Width, layer.Pixels.Height));
        Assert.Equal(originalPixels, layer.Pixels.Pixels);
        Assert.Equal(originalTransform, layer.Transform);
    }

    [Fact]
    public void ImageSize_ManyUndoRedoCycles_AreStable()
    {
        // Same invariant over more cycles: the restore must be exact on every one of them, not
        // just the first, because the command is retained on the redo stack for the session.
        var doc = new Document(6, 3) { Resolution = 96 };
        var layer = PixelLayer("a", 6, 3);
        doc.AddLayer(layer);
        var originalPixels = (byte[])layer.Pixels!.Pixels.Clone();
        var originalTransform = layer.Transform;

        var cmd = new ImageSizeCommand(doc, 12, 6, 150);
        for (var cycle = 0; cycle < 4; cycle++)
        {
            cmd.Redo();
            Assert.Equal((12, 6), (doc.Width, doc.Height));
            Assert.Equal(150, doc.Resolution);
            Assert.Equal((12, 6), (layer.Pixels!.Width, layer.Pixels.Height));

            cmd.Undo();
            Assert.Equal((6, 3), (doc.Width, doc.Height));
            Assert.Equal(96, doc.Resolution);
            Assert.Equal((6, 3), (layer.Pixels!.Width, layer.Pixels.Height));
            Assert.Equal(originalPixels, layer.Pixels.Pixels);
            Assert.Equal(originalTransform, layer.Transform);
        }
    }

    [Fact]
    public void ImageSize_UndoRedoThroughHistory_RestoresLayers()
    {
        // The same sequence driven through UndoHistory rather than the command directly, which is
        // how the app reaches it: Push executes on record, then the user's Ctrl+Z / Ctrl+Y.
        var doc = new Document(4, 4);
        var layer = PixelLayer("a", 4, 4);
        doc.AddLayer(layer);
        var originalPixels = (byte[])layer.Pixels!.Pixels.Clone();

        var history = new UndoHistory();
        history.Push(new ImageSizeCommand(doc, 8, 8, 72));
        history.Undo();
        history.Redo();
        history.Undo();

        Assert.Equal((4, 4), (doc.Width, doc.Height));
        Assert.Equal((4, 4), (layer.Pixels!.Width, layer.Pixels.Height));
        Assert.Equal(originalPixels, layer.Pixels.Pixels);
    }

    [Fact]
    public void ImageSize_RejectsCaps()
    {
        var doc = new Document(8, 8);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageSizeCommand(doc, 30_001, 8, 72));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageSizeCommand(doc, 20_000, 20_000, 72)); // 400 MP, past the 200 MP surface ceiling
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageSizeCommand(doc, 8, 8, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageSizeCommand(doc, 8, 8, 9601));
    }

    // --- resampler quality ---

    [Fact]
    public void Resample_Identity_PreservesPixels()
    {
        var surface = new RasterSurface(3, 2);
        surface.SetPixel(1, 1, 200, 100, 50, 255);

        var result = SurfaceOps.ResampleBilinear(surface, 3, 2);

        Assert.Equal(surface.Pixels, result.Pixels);
    }

    [Fact]
    public void Resample_IntegerUpscale_SolidAreaStaysSolid()
    {
        var surface = new RasterSurface(2, 2);
        for (var i = 0; i < surface.Pixels.Length; i += 4)
        {
            surface.Pixels[i] = 100;
            surface.Pixels[i + 3] = 255;
        }

        var up = SurfaceOps.ResampleBilinear(surface, 4, 4);

        Assert.Equal((4, 4), (up.Width, up.Height));
        for (var i = 0; i < up.Pixels.Length; i += 4)
        {
            Assert.Equal((byte)100, up.Pixels[i]);
            Assert.Equal((byte)255, up.Pixels[i + 3]);
        }
    }

    [Fact]
    public void Resample_MidpointBetweenValues_Interpolates()
    {
        var surface = new RasterSurface(2, 1);
        surface.SetPixel(0, 0, 0, 0, 0, 255);
        surface.SetPixel(1, 0, 200, 0, 0, 255);

        var wide = SurfaceOps.ResampleBilinear(surface, 4, 1);

        // Destination centers at src -0.25, 0.25, 0.75, 1.25 -> edge clamp.
        Assert.Equal((byte)0, wide.GetPixel(0, 0).R);
        Assert.Equal((byte)50, wide.GetPixel(1, 0).R);
        Assert.Equal((byte)150, wide.GetPixel(2, 0).R);
        Assert.Equal((byte)200, wide.GetPixel(3, 0).R);
    }

    [Fact]
    public void Resample_Downscale_KeepsFlatColor()
    {
        var surface = new RasterSurface(4, 4);
        for (var i = 0; i < surface.Pixels.Length; i += 4)
        {
            surface.Pixels[i] = 80;
            surface.Pixels[i + 1] = 160;
            surface.Pixels[i + 3] = 255;
        }

        var down = SurfaceOps.ResampleBilinear(surface, 2, 2);

        Assert.Equal((byte)80, down.GetPixel(0, 0).R);
        Assert.Equal((byte)160, down.GetPixel(1, 1).G);
        Assert.Equal((byte)255, down.GetPixel(0, 0).A);
    }
}
