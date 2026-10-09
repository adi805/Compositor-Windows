namespace Compositor.Core.Imaging;

/// <summary>
/// Pixel ceilings shared by image IO and geometry. Mirrors upstream
/// <c>Document/DocumentLimits.swift</c>, which centralises them because two
/// separate ideas had collapsed onto one number:
/// <list type="bullet">
/// <item>how large a <b>single surface</b> may be (canvas, export, filter target,
/// adjustment or mask render), and</item>
/// <item>how much raster a <b>whole document</b> may hold across every layer and mask.</item>
/// </list>
/// Upstream: <c>maxSide = 30_000</c>, <c>maxSurfacePixels = 200_000_000</c>,
/// <c>documentPixelBudget = min(800_000_000, max(maxSurfacePixels, physicalMemory / 16))</c>.
/// </summary>
public static class ImageBudget
{
    /// Longest side, in pixels, of any canvas, layer, mask or generated surface.
    public const int MaxSide = 30_000;

    /// Largest single surface: a canvas, an export, a filter target, an adjustment or mask render.
    /// At RGBA8 one allocation is at most 800 MB, and a filter holds a few of them at once.
    public const long MaxSurfacePixels = 200_000_000;

    /// Total imported raster one document may hold, summed across every layer and mask.
    /// Upstream scales this to the machine's physical memory; we read what the runtime
    /// reports as available to this process, the closest portable equivalent, which also
    /// honours container limits where the Mac reads the host's RAM. Clamped so it is never
    /// below one surface and never above 800 MP.
    public static long DocumentPixelBudget { get; } = Math.Min(
        800_000_000L,
        Math.Max(MaxSurfacePixels, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 16));

    /// The single-surface ceiling in megapixels, for the messages that quote it back to the reader.
    public static int MaxSurfaceMegapixels => (int)(MaxSurfacePixels / 1_000_000);

    /// The document ceiling in megapixels, for the messages that quote it back to the reader.
    public static int DocumentBudgetMegapixels => (int)(DocumentPixelBudget / 1_000_000);

    /// Upstream <c>snapshot.manifest.resolution ?? 72</c>, written as PNG pHYs / JPEG density.
    public const double DefaultResolution = 72;

    /// Upstream import thumbnail: <c>scale = min(1, 96 / max(w, h))</c>.
    public const int ImportThumbnailLongSide = 96;

    /// Upstream JPEG sheet preview: <c>kCGImageSourceThumbnailMaxPixelSize: 1000</c>.
    public const int PreviewLongSide = 1_000;

    /// <summary>
    /// Bytes an RGBA8 surface of this size needs, computed in 64-bit so it cannot wrap.
    ///
    /// Why this exists as its own member: the naive <c>width * height * 4</c> in 32-bit
    /// arithmetic wraps silently past 2,147,483,647 bytes, and a wrapped product is either
    /// negative (the allocation throws something unrelated) or a small positive number (the
    /// allocation succeeds and the buffer is too short for the raster). At 30,000 x 20,000 the
    /// true figure is 2,400,000,000 bytes, which is past <see cref="int.MaxValue"/>: exactly
    /// the case the per-surface ceiling below is meant to stop before arithmetic matters.
    /// </summary>
    public static long RgbaByteCount(int width, int height) => width * (long)height * 4L;

    /// <summary>
    /// True when a single surface of this size is within the side limits AND the
    /// per-surface ceiling. This is the check the export path always had and the import path
    /// did not: the import path only consulted the document budget, which is scaled to the
    /// machine's memory and can therefore be several times larger than one surface may be.
    /// </summary>
    public static bool FitsSurface(int width, int height) =>
        width >= 1 &&
        height >= 1 &&
        width <= MaxSide &&
        height <= MaxSide &&
        width * (long)height <= MaxSurfacePixels;

    /// <summary>
    /// True when a buffer of this size fits the side limits and the remaining document budget.
    /// <paramref name="documentBudget"/> overrides the machine-scaled budget, which is what
    /// boundary tests use: the real value depends on how much memory the host happens to have,
    /// so a test that asserted against it would pass or fail by machine rather than by code.
    /// </summary>
    public static bool Fits(int width, int height, long pixelsAlreadyUsed = 0, long? documentBudget = null) =>
        FitsSurface(width, height) &&
        (width * (long)height) + pixelsAlreadyUsed <= (documentBudget ?? DocumentPixelBudget);

    /// <summary>
    /// Throws <see cref="ImageFailure.ExportTooLarge"/> outside the single-surface ceiling.
    /// </summary>
    public static void ValidateExport(int width, int height)
    {
        if (!FitsSurface(width, height))
        {
            throw new ImageException(ImageFailure.ExportTooLarge);
        }
    }

    /// <summary>
    /// Throws <see cref="ImageFailure.ImportTooLarge"/> when the image will not fit what is left.
    ///
    /// Both ceilings are enforced here, not just the document budget. The document budget is
    /// derived from the machine's memory, so on a large host it can be several times the
    /// per-surface ceiling: an import of 30,000 x 20,000 (600 MP) passed the budget check and
    /// then reached <c>rowBytes * height</c>, whose true value is 2.4 GB, past
    /// <see cref="int.MaxValue"/>. That is a crash waiting for a big enough machine, and the
    /// check that stops it is the per-surface one, which does not depend on the host at all.
    /// </summary>
    public static void ValidateImport(int width, int height, long pixelsAlreadyUsed, long? documentBudget = null)
    {
        if (!Fits(width, height, pixelsAlreadyUsed, documentBudget))
        {
            throw new ImageException(ImageFailure.ImportTooLarge);
        }
    }

    /// <summary>
    /// Throws when a canvas is outside the ceilings a canvas must satisfy: the side limit, the
    /// per-surface ceiling, and the document budget. A canvas is a surface, so the per-surface
    /// ceiling applies to it exactly as it does to an import or an export.
    /// </summary>
    public static void ValidateCanvas(int width, int height, long? documentBudget = null)
    {
        if (!Fits(width, height, 0, documentBudget))
        {
            throw new ImageException(ImageFailure.ImportTooLarge);
        }
    }

    /// Long-side scale factor for a thumbnail that must fit <paramref name="longSide"/>; 1 keeps size.
    public static double ThumbnailScale(int width, int height, int longSide)
    {
        var extent = Math.Max(width, height);
        return extent <= 0 ? 1 : Math.Min(1, (double)longSide / extent);
    }

    /// Total canvas pixels used by a buffer, the unit the budget is counted in.
    public static long PixelCount(int width, int height) => width * (long)height;
}
