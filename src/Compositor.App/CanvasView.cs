using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering;
using Compositor.App.Rendering;
using Compositor.Core;
using Compositor.Core.Imaging;

namespace Compositor.App;

/// <summary>
/// Editor canvas: checkerboard backdrop, real layer pixels rendered from each
/// layer's RasterSurface via cached WriteableBitmaps (invalidated by surface
/// Version), and pointer-driven brush strokes in document coordinates.
/// Implements ICustomHitTest because a plain Control with no drawn content is
/// otherwise invisible to Avalonia's hit tester.
/// </summary>
public sealed class CanvasView : Control, ICustomHitTest
{
    private static readonly Brush CheckerDark = new SolidColorBrush(Color.FromRgb(43, 43, 43));
    private static readonly Brush CheckerLight = new SolidColorBrush(Color.FromRgb(58, 58, 58));
    private static readonly IBrush[] LayerTints =
    {
        new SolidColorBrush(Color.FromArgb(90, 66, 133, 244)),
        new SolidColorBrush(Color.FromArgb(90, 219, 68, 55)),
        new SolidColorBrush(Color.FromArgb(90, 244, 180, 0)),
        new SolidColorBrush(Color.FromArgb(90, 15, 157, 88)),
    };

    private const double CheckerSize = 12;
    private const double MinLayerSize = 1;

    public static readonly StyledProperty<EditorViewModel?> ViewModelProperty =
        AvaloniaProperty.Register<CanvasView, EditorViewModel?>(nameof(ViewModel));

    public EditorViewModel? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private readonly List<LayerRow> _attachedRows = new();
    private EditorViewModel? _attached;
    private Point? _panLast;

    public CanvasView()
    {
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    /// <summary>Whole control area accepts pointer input (ICustomHitTest).</summary>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    static CanvasView()
    {
        AffectsRender<CanvasView>(ViewModelProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ViewModelProperty)
        {
            Detach();
            _attached = ViewModel;
            if (_attached is not null)
            {
                _attached.DocumentChanged += OnDocumentChanged;
                _attached.Rows.CollectionChanged += OnRowsChanged;
                _attached.ViewChanged += OnViewChanged;
                AttachRows();
            }

            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(CheckerDark, bounds);

        if (ViewModel is null)
        {
            return;
        }

        var (crX, crY, crW, crH) = ViewModel.CanvasRect(bounds.Width, bounds.Height);
        var canvasRect = new Rect(crX, crY, crW, crH);
        if (canvasRect.Width <= 0 || canvasRect.Height <= 0)
        {
            return;
        }

        for (var y = 0.0; y < canvasRect.Height; y += CheckerSize)
        {
            var row = (int)(y / CheckerSize);
            for (var x = (row % 2) * CheckerSize;
                 x < canvasRect.Width;
                 x += CheckerSize * 2)
            {
                var cell = new Rect(
                    canvasRect.X + x,
                    canvasRect.Y + y,
                    Math.Min(CheckerSize, canvasRect.Width - x),
                    Math.Min(CheckerSize, canvasRect.Height - y));
                context.FillRectangle(CheckerLight, cell);
            }
        }

        // Layers can now be moved or magnified past the canvas edge; everything they paint
        // outside it belongs to the ruler/checker area, so the stack is clipped to the canvas.
        // Disposed before the overlays below: the transform handles sit outside the canvas and have to show.
        var canvasClip = context.PushClip(canvasRect);

        // Blank layers first, so the placeholder never sits on top of real pixels. A blank layer
        // has nothing to contribute to the composite, and its tint is an editor affordance rather
        // than content: drawn over the composite it would misreport what the file will contain.
        var tint = 0;
        foreach (var layer in LayerHierarchy.VisibleLayers(ViewModel.Doc.Layers))
        {
            if (!layer.IsVisible)
            {
                continue;
            }

            if (layer.Pixels is null)
            {
                // Blank layer: placeholder rect so it stays visible in the canvas.
                context.FillRectangle(
                    LayerTints[tint % LayerTints.Length],
                    Scaled(canvasRect, ViewModel.Doc, layer.Transform));
            }

            tint++;
        }

        // Pixel layers through the shared compositor. Drawing each surface straight into its
        // placement rect applied neither opacity nor blend mode, so an opaque layer at zero
        // opacity stayed on screen and disappeared from the export, and Multiply rendered as
        // ordinary alpha-over: the canvas showed something the file would never contain.
        // DrawingContext has no per-image blend mode, so parity is only reachable by compositing
        // in Core (Flatten, the same call the PNG export makes) and drawing the result as one
        // image. DownsampleCache still pre-reduces it, so a zoomed-out view is not scaled in one
        // step.
        if (Composite() is { } composite)
        {
            var factor = composite.Width > 0 ? canvasRect.Width / composite.Width : 1d;
            var (drawn, level) = DownsampleCache.Shared.For(composite, factor);
            var bitmap = GetCompositeBitmap(drawn, level);
            if (bitmap is not null)
            {
                context.DrawImage(bitmap, canvasRect);
            }
        }

        canvasClip.Dispose();

        RenderSelectionOverlay(context, canvasRect);
        RenderTransformOverlay(context, canvasRect);
    }

    private WriteableBitmap? _floatingBitmap;
    private Compositor.Core.Selection.FloatingSelection? _floatingSource;
    private static readonly IPen SelectionPen = new Pen(Brushes.Cyan, 1, DashStyle.Dash);

    /// <summary>Floating pixels, selection outline, and the live marquee draft.</summary>
    private void RenderSelectionOverlay(DrawingContext context, Rect canvasRect)
    {
        if (ViewModel is null)
        {
            return;
        }

        var doc = ViewModel.Doc;
        var scale = canvasRect.Width / doc.Width;

        if (ViewModel.Floating is { } floating)
        {
            if (_floatingBitmap is null || !ReferenceEquals(_floatingSource, floating))
            {
                _floatingBitmap?.Dispose();
                _floatingBitmap = CreateBitmap(floating.Width, floating.Height, floating.Pixels);
                _floatingSource = floating;
            }
            if (_floatingBitmap is not null)
            {
                var rect = new Rect(
                    canvasRect.X + (floating.X * scale),
                    canvasRect.Y + (floating.Y * scale),
                    floating.Width * scale,
                    floating.Height * scale);
                context.DrawImage(_floatingBitmap, rect);
                context.DrawRectangle(SelectionPen, rect);
            }
        }

        if (doc.Selection is { IsEmpty: false } selection)
        {
            var clip = selection.Clip(doc.Width, doc.Height);
            if (clip.Coverage is not null)
            {
                context.DrawRectangle(SelectionPen, new Rect(
                    canvasRect.X + (clip.X * scale),
                    canvasRect.Y + (clip.Y * scale),
                    clip.Width * scale,
                    clip.Height * scale));
            }
        }

        if (ViewModel.DraftBounds is { } draft)
        {
            context.DrawRectangle(SelectionPen, new Rect(
                canvasRect.X + (draft.X * scale),
                canvasRect.Y + (draft.Y * scale),
                draft.W * scale,
                draft.H * scale));
        }
    }

    private static WriteableBitmap? CreateBitmap(int width, int height, byte[] pixels)
    {
        try
        {
            var bitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormats.Rgba8888,
                AlphaFormat.Unpremul);
            using var fb = bitmap.Lock();
            Marshal.Copy(pixels, 0, fb.Address, pixels.Length);
            return bitmap;
        }
        catch (Exception)
        {
            return null; // headless/no-render-context
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (ViewModel is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsMiddleButtonPressed)
        {
            // Start a view pan; stroke painting stays on the left button.
            _panLast = e.GetPosition(this);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var doc = ToDocCoords(e.GetPosition(this));
        if (doc is null)
        {
            return;
        }

        if (ViewModel.Tool == EditorViewModel.EditorTool.Move)
        {
            var grab = ViewModel.ScreenToDocUnclamped(
                e.GetPosition(this).X, e.GetPosition(this).Y, Bounds.Width, Bounds.Height);
            if (ViewModel.BeginTransformDrag(grab.X, grab.Y, Bounds.Width, Bounds.Height))
            {
                e.Pointer.Capture(this);
                e.Handled = true;
                InvalidateVisual();
            }

            return;
        }

        if (EditorViewModel.IsSelectTool(ViewModel.Tool))
        {
            if (ViewModel.BeginMarquee(doc.Value.X, doc.Value.Y))
            {
                e.Pointer.Capture(this);
                e.Handled = true;
                InvalidateVisual();
            }
            return;
        }

        if (ViewModel.BeginTool(doc.Value.X, doc.Value.Y))
        {
            e.Pointer.Capture(this);
            e.Handled = true;
            InvalidateVisual();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (ViewModel is null)
        {
            return;
        }

        if (_panLast is { } last && e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed)
        {
            var pos = e.GetPosition(this);
            ViewModel.PanBy(pos.X - last.X, pos.Y - last.Y);
            _panLast = pos;
            e.Handled = true;
            return;
        }

        if (ViewModel.IsTransformDragActive)
        {
            var pos = e.GetPosition(this);
            var grab = ViewModel.ScreenToDocUnclamped(pos.X, pos.Y, Bounds.Width, Bounds.Height);
            ViewModel.ContinueTransformDrag(
                grab.X, grab.Y,
                shift: e.KeyModifiers.HasFlag(KeyModifiers.Shift),
                fromCenter: e.KeyModifiers.HasFlag(KeyModifiers.Alt));
            e.Handled = true;
            InvalidateVisual();
            return;
        }

        if (ViewModel.IsMarqueeActive)
        {
            var mdoc = ToDocCoords(e.GetPosition(this));
            if (mdoc is not null)
            {
                ViewModel.ContinueMarquee(mdoc.Value.X, mdoc.Value.Y);
                e.Handled = true;
                InvalidateVisual();
            }
            return;
        }

        if (!ViewModel.IsStrokeActive)
        {
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var doc = ToDocCoords(e.GetPosition(this));
        if (doc is null)
        {
            return;
        }

        ViewModel.ContinueTool(doc.Value.X, doc.Value.Y);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_panLast is not null)
        {
            _panLast = null;
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (ViewModel?.IsTransformDragActive == true)
        {
            ViewModel.EndTransformDrag();
            e.Pointer.Capture(null);
            e.Handled = true;
            InvalidateVisual();
            return;
        }

        if (ViewModel?.IsMarqueeActive == true)
        {
            ViewModel.EndMarquee();
            e.Pointer.Capture(null);
            e.Handled = true;
            InvalidateVisual();
            return;
        }

        if (ViewModel?.IsStrokeActive != true)
        {
            return;
        }

        ViewModel.EndTool();
        e.Pointer.Capture(null);
        e.Handled = true;
        InvalidateVisual();
    }

    /// <summary>Control-space point to document coordinates; null when outside the canvas.</summary>
    private (float X, float Y)? ToDocCoords(Point p) =>
        ViewModel?.ScreenToDoc(p.X, p.Y, Bounds.Width, Bounds.Height);

    /// <summary>
    /// The transform box and its eight handles, plus the rotation handle. Drawn outside the canvas clip: a
    /// layer can hang over the edge and its handles still have to be grabbable.
    /// </summary>
    private void RenderTransformOverlay(DrawingContext context, Rect canvasRect)
    {
        if (ViewModel?.TransformOverlay(Bounds.Width, Bounds.Height) is not var (handles, rotation))
        {
            return;
        }

        Point Screen((double X, double Y) point) => new(
            canvasRect.X + point.X / ViewModel.Doc.Width * canvasRect.Width,
            canvasRect.Y + point.Y / ViewModel.Doc.Height * canvasRect.Height);

        var corners = new[] { handles[0], handles[2], handles[4], handles[6] }.Select(Screen).ToArray();
        for (var i = 0; i < corners.Length; i++)
        {
            context.DrawLine(TransformPen, corners[i], corners[(i + 1) % corners.Length]);
        }

        context.DrawLine(TransformPen, Screen(handles[1]), Screen(rotation));

        foreach (var handle in handles)
        {
            var point = Screen(handle);
            context.DrawRectangle(HandleFill, HandlePen, new Rect(point.X - 3.5, point.Y - 3.5, 7, 7));
        }

        var knob = Screen(rotation);
        context.DrawEllipse(HandleFill, HandlePen, knob, 4, 4);
    }

    private static readonly IPen TransformPen = new Pen(Brushes.DodgerBlue, 1);
    private static readonly IPen HandlePen = new Pen(Brushes.DodgerBlue, 1);
    private static readonly IBrush HandleFill = Brushes.White;

    private WriteableBitmap? _compositeBitmap;
    private int _compositeBitmapLevel = -1;
    private long _compositeSignature = long.MinValue;
    private RasterSurface? _compositeSurface;

    /// <summary>
    /// The document composited with the same rules the exporter uses, or null when there is
    /// nothing to draw. Rebuilt only when the signature below moves, because this is the whole
    /// canvas stack and the ordinary case is a frame where nothing changed.
    /// </summary>
    private RasterSurface? Composite()
    {
        if (ViewModel is not { } vm)
        {
            return null;
        }

        var signature = CompositeSignature(vm);
        if (_compositeSurface is not null && _compositeSignature == signature)
        {
            return _compositeSurface;
        }

        _compositeBitmap?.Dispose();
        _compositeBitmap = null;
        _compositeBitmapLevel = -1;
        _compositeSurface = null;
        _compositeSignature = signature;

        var (width, height, rgba) = Flatten.ToRgba(
            vm.Doc,
            // Live adjustment preview replaces the active layer's own pixels. The override is
            // resolved here rather than in the compositor so both paths agree on which layer the
            // sheet is currently editing.
            layer => layer == vm.ActiveLayer ? vm.AdjustmentPreviewSurface : null);
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        _compositeSurface = new RasterSurface(width, height, rgba);
        return _compositeSurface;
    }

    /// <summary>
    /// Everything the composite depends on. A layer's pixel edits are caught by its surface
    /// Version; appearance edits (opacity, blend, visibility, placement) bump nothing, so they are
    /// folded in by value. Getting this wrong shows a stale canvas, so it errs toward rebuilding.
    /// </summary>
    private static long CompositeSignature(EditorViewModel vm)
    {
        var hash = new HashCode();
        hash.Add(vm.Doc.Width);
        hash.Add(vm.Doc.Height);
        hash.Add(vm.AdjustmentPreviewGeneration);
        foreach (var layer in vm.Doc.Layers)
        {
            hash.Add(layer.Id);
            hash.Add(layer.ParentId);
            hash.Add(layer.IsVisible);
            hash.Add(layer.Opacity);
            hash.Add(layer.Blend);
            hash.Add(layer.Transform);
            hash.Add(layer.Pixels?.Version ?? -1L);
        }

        return hash.ToHashCode();
    }

    private WriteableBitmap? GetCompositeBitmap(RasterSurface surface, int level)
    {
        // Keyed on the reduced level as well: zooming out picks a different halved copy, and the
        // bitmap has to follow it rather than keep drawing the previous level at the new size.
        if (_compositeBitmap is not null && _compositeBitmapLevel == level)
        {
            return _compositeBitmap;
        }

        _compositeBitmap?.Dispose();
        _compositeBitmap = CreateBitmap(surface.Width, surface.Height, surface.Pixels);
        _compositeBitmapLevel = level;
        return _compositeBitmap;
    }

    private void AttachRows()
    {
        DetachRows();
        if (_attached is null)
        {
            return;
        }

        foreach (var row in _attached.Rows)
        {
            row.PropertyChanged += OnRowPropertyChanged;
            _attachedRows.Add(row);
        }
    }

    private void DetachRows()
    {
        foreach (var row in _attachedRows)
        {
            row.PropertyChanged -= OnRowPropertyChanged;
        }

        _attachedRows.Clear();
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LayerRow.IsVisible))
        {
            InvalidateVisual();
        }
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        AttachRows();
        InvalidateVisual();
    }

    private void OnDocumentChanged()
    {
        InvalidateVisual();
    }

    private void OnViewChanged()
    {
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (ViewModel is null)
        {
            return;
        }

        var pos = e.GetPosition(this);
        ViewModel.ZoomAt(pos.X, pos.Y, Bounds.Width, Bounds.Height, e.Delta.Y > 0 ? 1.25 : 0.8);
        e.Handled = true;
    }

    private void Detach()
    {
        DetachRows();
        _floatingBitmap?.Dispose();
        _floatingBitmap = null;
        _floatingSource = null;
        _compositeBitmap?.Dispose();
        _compositeBitmap = null;
        _compositeBitmapLevel = -1;
        _compositeSurface = null;
        _compositeSignature = long.MinValue;
        if (_attached is null)
        {
            return;
        }

        _attached.DocumentChanged -= OnDocumentChanged;
        _attached.Rows.CollectionChanged -= OnRowsChanged;
        _attached.ViewChanged -= OnViewChanged;
        _attached = null;
    }

    private static Rect Scaled(Rect canvasRect, Document doc, LayerTransform t)
    {
        var docW = (double)doc.Width;
        var docH = (double)doc.Height;
        var width = t.CoversCanvas ? docW : t.Width;
        var height = t.CoversCanvas ? docH : t.Height;
        return new Rect(
            canvasRect.X + (t.OriginX / docW * canvasRect.Width),
            canvasRect.Y + (t.OriginY / docH * canvasRect.Height),
            Math.Max(MinLayerSize, width / docW * canvasRect.Width),
            Math.Max(MinLayerSize, height / docH * canvasRect.Height));
    }
}