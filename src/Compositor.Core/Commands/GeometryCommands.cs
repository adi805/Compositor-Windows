using Compositor.Core.Imaging;

namespace Compositor.Core.Commands;

/// <summary>
/// Canvas geometry resize, upstream CanvasResizer: every layer transform
/// shifts by the content offset, pixel data stays untouched (non-destructive).
/// An optional fill color creates a separate bottom "Canvas Extension" layer
/// whose old-canvas area stays transparent. Crop is the same op with an
/// explicit offset (-rect.origin) and no fill (upstream commitCrop).
/// </summary>
public sealed class CanvasResizeCommand : IUndoCommand
{
    /// <summary>Upstream anchor limit (row-major, top-left through bottom-right).</summary>
    public const int MaxAnchor = 8;

    private readonly Document _doc;
    private readonly int _newWidth;
    private readonly int _newHeight;
    private readonly double _offsetX;
    private readonly double _offsetY;
    private readonly (byte R, byte G, byte B)? _fill;
    private readonly int _oldWidth;
    private readonly int _oldHeight;
    private Layer? _fillLayer;

    private CanvasResizeCommand(
        Document doc, int newWidth, int newHeight,
        double offsetX, double offsetY, (byte R, byte G, byte B)? fill)
    {
        _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        if (newWidth is < 1 or > 30_000 || newHeight is < 1 or > 30_000)
        {
            throw new ArgumentOutOfRangeException(nameof(newWidth), "Canvas size must be 1..30000.");
        }
        if (newWidth * (long)newHeight > ImageBudget.MaxSurfacePixels)
        {
            throw new ArgumentOutOfRangeException(nameof(newWidth), $"Canvas exceeds the {ImageBudget.MaxSurfaceMegapixels} megapixel cap.");
        }
        if (!double.IsFinite(offsetX) || !double.IsFinite(offsetY)
            || Math.Abs(offsetX) > 1_000_000 || Math.Abs(offsetY) > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(offsetX), "Content offset out of range.");
        }
        _newWidth = newWidth;
        _newHeight = newHeight;
        _offsetX = offsetX;
        _offsetY = offsetY;
        _fill = fill;
        _oldWidth = doc.Width;
        _oldHeight = doc.Height;
    }

    /// <summary>
    /// Canvas Size: resize around the given 0..8 anchor, upstream floor formula
    /// (extra pixel to the right/bottom when growing, removed left/top when
    /// shrinking around the center).
    /// </summary>
    public static CanvasResizeCommand AnchorResize(
        Document doc, int newWidth, int newHeight, int anchor, (byte R, byte G, byte B)? fill = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (anchor is < 0 or > MaxAnchor)
        {
            throw new ArgumentOutOfRangeException(nameof(anchor), "Anchor must be 0..8.");
        }
        var dx = Math.Floor((newWidth - doc.Width) * (anchor % 3) / 2.0);
        var dy = Math.Floor((newHeight - doc.Height) * (anchor / 3) / 2.0);
        return new CanvasResizeCommand(doc, newWidth, newHeight, dx, dy, fill);
    }

    /// <summary>Crop to the given document-space rectangle (upstream CropGeometry.valid bounds).</summary>
    public static CanvasResizeCommand Crop(Document doc, int x, int y, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (width is < 1 or > 30_000 || height is < 1 or > 30_000)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Crop size must be 1..30000.");
        }
        if (Math.Abs(x) > 1_000_000 || Math.Abs(y) > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Crop origin out of range.");
        }
        return new CanvasResizeCommand(doc, width, height, -x, -y, fill: null);
    }

    public void Redo()
    {
        foreach (var layer in _doc.Layers)
        {
            var t = layer.Transform;
            layer.Transform = t with { OriginX = t.OriginX + _offsetX, OriginY = t.OriginY + _offsetY };
        }
        if (_fill is { } color && (_newWidth > _doc.Width || _newHeight > _doc.Height) && _fillLayer is null)
        {
            _fillLayer = BuildFillLayer(color, _newWidth, _newHeight, (int)_offsetX, (int)_offsetY, _doc.Width, _doc.Height);
        }
        if (_fillLayer is { } fill)
        {
            _doc.Layers.Insert(0, fill);
        }
        _doc.SetSize(_newWidth, _newHeight);
        _doc.Selection = null;
    }

    public void Undo()
    {
        if (_fillLayer is { } fill)
        {
            _doc.Layers.Remove(fill);
        }
        foreach (var layer in _doc.Layers)
        {
            var t = layer.Transform;
            layer.Transform = t with { OriginX = t.OriginX - _offsetX, OriginY = t.OriginY - _offsetY };
        }
        _doc.SetSize(_oldWidth, _oldHeight);
        _doc.Selection = null;
    }

    /// <summary>
    /// The bottom extension layer: solid fill everywhere except the old canvas
    /// rect at its shifted position, which stays transparent (upstream clears
    /// CGRect(origin: offset, size: old size) so existing artwork holes keep).
    /// </summary>
    private static Layer BuildFillLayer(
        (byte R, byte G, byte B) color,
        int newWidth, int newHeight, int holeX, int holeY, int oldWidth, int oldHeight)
    {
        var pixels = new byte[newWidth * newHeight * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.R;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.B;
            pixels[i + 3] = 255;
        }
        for (var y = Math.Max(0, holeY); y < Math.Min(newHeight, holeY + oldHeight); y++)
        {
            var x0 = Math.Max(0, holeX);
            var x1 = Math.Min(newWidth, holeX + oldWidth);
            if (x1 <= x0)
            {
                continue;
            }
            Array.Clear(pixels, (y * newWidth * 4) + (x0 * 4), (x1 - x0) * 4);
        }
        return new Layer("Canvas Extension")
        {
            Pixels = new RasterSurface(newWidth, newHeight, pixels),
            Transform = LayerTransform.ForCanvas(newWidth, newHeight),
        };
    }
}

/// <summary>
/// Image Size resample, upstream ImageResizer: every layer transform scales by
/// (sx, sy); pixel surfaces resample bilinearly to the scaled size (max 1px).
/// Same-dimension requests only update the resolution. Surface cap 200 megapixels.
/// </summary>
public sealed class ImageSizeCommand : IUndoCommand
{
    private readonly Document _doc;
    private readonly int _newWidth;
    private readonly int _newHeight;
    private readonly double _resolution;
    private readonly List<(Layer Layer, LayerTransform Transform, RasterSurface? Pixels)> _originals = [];

    public ImageSizeCommand(Document doc, int newWidth, int newHeight, double resolution)
    {
        _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        if (newWidth is < 1 or > 30_000 || newHeight is < 1 or > 30_000)
        {
            throw new ArgumentOutOfRangeException(nameof(newWidth), "Image size must be 1..30000.");
        }
        if (!double.IsFinite(resolution) || resolution is < 1 or > 9600)
        {
            throw new ArgumentOutOfRangeException(nameof(resolution), "Resolution must be 1..9600 DPI.");
        }
        if (newWidth * (long)newHeight > ImageBudget.MaxSurfacePixels)
        {
            throw new ArgumentOutOfRangeException(nameof(newWidth), $"Image exceeds the {ImageBudget.MaxSurfaceMegapixels} megapixel cap.");
        }
        _newWidth = newWidth;
        _newHeight = newHeight;
        _resolution = resolution;
    }

    public void Redo()
    {
        CaptureOriginals();
        if (_newWidth == _doc.Width && _newHeight == _doc.Height)
        {
            _doc.Resolution = _resolution;
            return;
        }
        var sx = (double)_newWidth / _doc.Width;
        var sy = (double)_newHeight / _doc.Height;
        foreach (var layer in _doc.Layers)
        {
            var t = layer.Transform;
            if (t.CoversCanvas)
            {
                // Cover-canvas layers stretch with the canvas. Painted ones
                // (BeginStroke materializes pixels but leaves the transform)
                // must also resample to the new canvas size.
                layer.Transform = LayerTransform.ForCanvas(_newWidth, _newHeight);
                if (layer.Pixels is { } cover && (cover.Width != _newWidth || cover.Height != _newHeight))
                {
                    layer.Pixels = SurfaceOps.ResampleBilinear(cover, _newWidth, _newHeight);
                }
                continue;
            }
            var w = Math.Max(1, (int)Math.Round(t.Width * sx, MidpointRounding.AwayFromZero));
            var h = Math.Max(1, (int)Math.Round(t.Height * sy, MidpointRounding.AwayFromZero));
            layer.Transform = t with
            {
                OriginX = t.OriginX * sx,
                OriginY = t.OriginY * sy,
                Width = w,
                Height = h,
            };
            if (layer.Pixels is { } surface
                && (surface.Width != w || surface.Height != h))
            {
                layer.Pixels = SurfaceOps.ResampleBilinear(surface, w, h);
            }
        }
        _doc.SetSize(_newWidth, _newHeight);
        _doc.Resolution = _resolution;
        _doc.Selection = null;
    }

    public void Undo()
    {
        foreach (var (layer, transform, pixels) in _originals)
        {
            layer.Transform = transform;
            layer.Pixels = pixels;
        }

        // The originals are NOT cleared. Clearing them here left _captured true, so a second Redo
        // skipped CaptureOriginals() and the second Undo then walked an empty list: the document
        // dimensions came back and the layer surfaces and transforms stayed at the resized values.
        // A resize cannot be undone without the pre-resize pixels, so they are kept for the
        // command's lifetime, which is what "retained on the redo stack" already implies. The
        // surfaces are immutable in the relevant sense: ResampleBilinear returns a new instance,
        // so the retained references stay valid and the history depth is unchanged.
        _doc.SetSize(_oldWidth, _oldHeight);
        _doc.Resolution = _oldResolution;
        _doc.Selection = null;
    }

    private int _oldWidth;
    private int _oldHeight;
    private double _oldResolution;
    private bool _captured;

    private void CaptureOriginals()
    {
        if (_captured)
        {
            return;
        }
        _oldWidth = _doc.Width;
        _oldHeight = _doc.Height;
        _oldResolution = _doc.Resolution;
        foreach (var layer in _doc.Layers)
        {
            _originals.Add((layer, layer.Transform, layer.Pixels));
        }
        _captured = true;
    }
}
