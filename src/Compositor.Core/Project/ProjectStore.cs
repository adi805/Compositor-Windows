using System.IO.Compression;
using Compositor.Core.Imaging;
using System.Text.Json;

namespace Compositor.Core.Project;

/// <summary>
/// Reads and writes .comp project files: a single zip containing manifest.json
/// and (later, once pixels exist) images/&lt;uuid&gt;.png assets.
/// Format v1 is the Mac v6 subset documented in docs/RESEARCH.md.
/// All validation happens BEFORE the live document is replaced.
/// </summary>
public static class ProjectStore
{
    public const string Identifier = "com.compositor.windows.project";
    public const int Version = 2;

    public const int MaxSidePixels = 30_000;
    /// <summary>Upstream validates the canvas against <c>DocumentLimits.documentPixelBudget</c>.</summary>
    public static long MaxTotalPixels => ImageBudget.DocumentPixelBudget;
    public const int MaxLayers = 10_000;
    public const int MaxManifestBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>Serializes the document to a .comp file (atomic replace).</summary>
    public static void Save(Document doc, string path)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ValidateDocument(doc);

        var manifest = ToManifest(doc);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        if (manifestBytes.Length > MaxManifestBytes)
        {
            throw new InvalidOperationException(
                $"Manifest is {manifestBytes.Length} bytes; the limit is {MaxManifestBytes}.");
        }

        var tempPath = path + ".tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("manifest.json", CompressionLevel.NoCompression);
                using (var writer = entry.Open())
                {
                    writer.Write(manifestBytes);
                }

                foreach (var layer in doc.Layers)
                {
                    if (layer.Pixels is null)
                    {
                        continue;
                    }

                    var png = new MemoryStream();
                    Png.Encode(png, layer.Pixels.Width, layer.Pixels.Height, layer.Pixels.Pixels);
                    var imageEntry = zip.CreateEntry($"images/{layer.Id}.png");
                    using (var imageStream = imageEntry.Open())
                    {
                        imageStream.Write(png.ToArray());
                    }
                }
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    /// <summary>Loads a .comp file. Rejects anything invalid before returning a document.</summary>
    public static Document Load(string path) => Load(path, documentBudget: null);

    /// <summary>
    /// Loads a .comp file, charging layer rasters against an explicit document budget.
    ///
    /// The budget is a parameter so the cumulative ceiling can be tested at its boundary. The
    /// production value is derived from the host's memory, so a test asserting against it would
    /// pass or fail according to how much RAM the machine running it happens to have.
    /// </summary>
    public static Document Load(string path, long? documentBudget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        EnsureSafeEntries(zip);
        var manifestEntry = zip.GetEntry("manifest.json")
            ?? throw new InvalidOperationException("Project file has no manifest.json entry.");
        var json = ReadManifestText(manifestEntry);

        var manifest = JsonSerializer.Deserialize<Manifest>(json, JsonOptions)
            ?? throw new InvalidOperationException("Manifest is not valid JSON.");

        return FromManifest(manifest, zip, documentBudget);
    }

    /// <summary>
    /// Reads the manifest as text, refusing before it allocates.
    ///
    /// The previous form read the whole entry into a string and then compared its length to the
    /// ceiling, which is too late: a zip entry can declare an arbitrary uncompressed size and
    /// the read allocates it all first, so a 4 MB ceiling was enforced only after the memory was
    /// already committed (a 2 GB manifest would be read, then rejected). Checking
    /// <see cref="ZipArchiveEntry.Length"/> first costs nothing, and the bounded read below is
    /// the belt for an archive whose declared length and actual content disagree.
    /// </summary>
    private static string ReadManifestText(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxManifestBytes)
        {
            throw new InvalidOperationException(
                $"Manifest declares {entry.Length} bytes; the limit is {MaxManifestBytes}.");
        }

        using var stream = entry.Open();
        using var buffer = new MemoryStream(capacity: (int)entry.Length);
        var chunk = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += read;
            if (total > MaxManifestBytes)
            {
                throw new InvalidOperationException(
                    $"Manifest exceeds {MaxManifestBytes} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static void EnsureSafeEntries(ZipArchive zip)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            if (string.IsNullOrEmpty(name)
                || name.StartsWith('/') || name.StartsWith('\\')
                || Path.IsPathRooted(name)
                || name.Split('/', '\\').Contains(".."))
            {
                throw new InvalidOperationException($"Unsafe zip entry name: '{name}'.");
            }

            if (!seen.Add(name))
            {
                throw new InvalidOperationException($"Duplicate zip entry: '{name}'.");
            }

            if (name != "manifest.json" && !name.StartsWith("images/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected zip entry: '{name}'.");
            }
        }
    }

    private static void ValidateDocument(Document doc)
    {
        ValidateDimensions(doc.Width, doc.Height);
        if (doc.Layers.Count > MaxLayers)
        {
            throw new InvalidOperationException(
                $"Document has {doc.Layers.Count} layers; the limit is {MaxLayers}.");
        }

        var ids = new HashSet<Guid>();
        long pixels = 0;
        foreach (var layer in doc.Layers)
        {
            if (!ids.Add(layer.Id))
            {
                throw new InvalidOperationException($"Duplicate layer id {layer.Id}.");
            }

            if (!double.IsFinite(layer.Opacity) || layer.Opacity is < 0.0 or > 1.0)
            {
                throw new InvalidOperationException(
                    $"Layer '{layer.Name}' opacity must be finite within 0-1.");
            }

            if (!layer.Blend.IsValid())
            {
                throw new InvalidOperationException(
                    $"Layer '{layer.Name}' has an out-of-range blend mode.");
            }

            // The same cumulative charge the load path applies, on the write side. A document is
            // reachable only through code that already checks, so this is the belt: it keeps the
            // invariant true at the boundary that defines the file format rather than trusting
            // every caller to have maintained it.
            if (layer.Pixels is { } surface)
            {
                if (!ImageBudget.Fits(surface.Width, surface.Height, pixels))
                {
                    throw new InvalidOperationException(
                        $"Layer '{layer.Name}' ({surface.Width}x{surface.Height}) exceeds the " +
                        $"document raster budget once earlier layers are counted.");
                }

                pixels += ImageBudget.PixelCount(surface.Width, surface.Height);
            }
        }

        if (doc.ActiveLayerId is { } active
            && doc.Layers.All(l => l.Id != active))
        {
            throw new InvalidOperationException("Active layer is not in this document.");
        }
    }

    private static void ValidateDimensions(int width, int height)
    {
        if (width is < 1 or > MaxSidePixels || height is < 1 or > MaxSidePixels)
        {
            throw new InvalidOperationException(
                $"Canvas {width}x{height} exceeds the {MaxSidePixels}px per-side limit.");
        }

        // The per-surface ceiling, enforced independently of the machine-scaled document budget.
        // A canvas IS a surface: without this check a 30,000 x 20,000 canvas (600 MP) is accepted
        // on any host whose document budget reaches 600 MP, and the first thing that then touches
        // it computes 600,000,000 * 4 = 2,400,000,000, which is past int.MaxValue.
        if ((long)width * height > ImageBudget.MaxSurfacePixels)
        {
            throw new InvalidOperationException(
                $"Canvas {width}x{height} exceeds the " +
                $"{ImageBudget.MaxSurfacePixels}px per-surface limit.");
        }

        if ((long)width * height > MaxTotalPixels)
        {
            throw new InvalidOperationException(
                $"Canvas {width}x{height} exceeds the {MaxTotalPixels}px total limit.");
        }
    }

    private static Manifest ToManifest(Document doc) => new()
    {
        DocumentUuid = doc.Id.ToString(),
        Name = doc.Name,
        Width = doc.Width,
        Height = doc.Height,
        Resolution = doc.Resolution,
        ActiveLayerUuid = doc.ActiveLayerId?.ToString(),
        Layers = doc.Layers.Select(l => new ManifestLayer
        {
            Uuid = l.Id.ToString(),
            Name = l.Name,
            IsVisible = l.IsVisible,
            IsLocked = l.IsLocked,
            Opacity = l.Opacity,
            BlendMode = l.Blend.ToString() switch
            {
                nameof(BlendMode.Normal) => "normal",
                nameof(BlendMode.Multiply) => "multiply",
                nameof(BlendMode.Screen) => "screen",
                nameof(BlendMode.Overlay) => "overlay",
                nameof(BlendMode.Darken) => "darken",
                nameof(BlendMode.Lighten) => "lighten",
                nameof(BlendMode.Difference) => "difference",
                nameof(BlendMode.ColorDodge) => "colorDodge",
                nameof(BlendMode.ColorBurn) => "colorBurn",
                nameof(BlendMode.Hue) => "hue",
                nameof(BlendMode.Saturation) => "saturation",
                nameof(BlendMode.Color) => "color",
                nameof(BlendMode.Luminosity) => "luminosity",
                _ => throw new InvalidOperationException(
                    $"Unknown blend mode {l.Blend}."),
            },
            Transform = new ManifestTransform
            {
                OriginX = l.Transform.OriginX,
                OriginY = l.Transform.OriginY,
                Width = l.Transform.Width,
                Height = l.Transform.Height,
                RotationDegrees = l.Transform.RotationDegrees,
                FlipH = l.Transform.FlipH,
                FlipV = l.Transform.FlipV,
            },
            Image = l.Pixels is null ? null : $"images/{l.Id}.png",
            ParentUuid = l.ParentId?.ToString(),
            IsGroup = l.IsGroup,
        }).ToList(),
    };

    private static Document FromManifest(Manifest manifest, ZipArchive zip, long? documentBudget = null)
    {
        if (!string.Equals(manifest.Identifier, Identifier, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Not a Compositor project: identifier '{manifest.Identifier}'.");
        }

        if (manifest.Version > Version)
        {
            throw new InvalidOperationException(
                $"Project format version {manifest.Version} is newer than " +
                $"this app supports ({Version}).");
        }

        if (manifest.Version < 1)
        {
            throw new InvalidOperationException(
                $"Invalid project format version {manifest.Version}.");
        }

        ValidateDimensions(manifest.Width, manifest.Height);
        if (manifest.Layers.Count > MaxLayers)
        {
            throw new InvalidOperationException(
                $"Project has {manifest.Layers.Count} layers; the limit is {MaxLayers}.");
        }

        if (!Guid.TryParse(manifest.DocumentUuid, out var docId))
        {
            throw new InvalidOperationException("Manifest documentUUID is not a valid UUID.");
        }

        var layerIds = new HashSet<Guid>();
        var layerIdByUuid = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var entries = new HashSet<string>(
            zip.Entries.Select(e => e.FullName), StringComparer.Ordinal);

        foreach (var layer in manifest.Layers)
        {
            if (!Guid.TryParse(layer.Uuid, out var id))
            {
                throw new InvalidOperationException(
                    $"Layer UUID '{layer.Uuid}' is not a valid UUID.");
            }

            if (!layerIds.Add(id))
            {
                throw new InvalidOperationException($"Duplicate layer UUID {layer.Uuid}.");
            }

            layerIdByUuid[layer.Uuid] = id;

            if (!double.IsFinite(layer.Opacity) || layer.Opacity is < 0.0 or > 1.0)
            {
                throw new InvalidOperationException(
                    $"Layer '{layer.Name}' opacity must be finite within 0-1.");
            }

            if (ParseBlend(layer.BlendMode) is null)
            {
                throw new InvalidOperationException(
                    $"Layer '{layer.Name}' has unknown blend mode '{layer.BlendMode}'.");
            }

            if (layer.Image is { } imageName)
            {
                var expected = $"images/{layer.Uuid}.png";
                if (!string.Equals(imageName, expected, StringComparison.Ordinal)
                    || !entries.Contains(imageName))
                {
                    throw new InvalidOperationException(
                        $"Layer '{layer.Name}' references missing image '{imageName}'.");
                }
            }

            if (layer.IsGroup && layer.Image is not null)
            {
                throw new InvalidOperationException(
                    $"Group layer '{layer.Name}' must not carry an image entry.");
            }

            if (layer.ParentUuid is { } parentUuid && !layerIdByUuid.ContainsKey(parentUuid))
            {
                throw new InvalidOperationException(
                    $"Layer '{layer.Name}' references missing parent '{parentUuid}'.");
            }
        }

        Guid? activeId = null;
        if (manifest.ActiveLayerUuid is { } activeUuid)
        {
            if (!layerIdByUuid.TryGetValue(activeUuid, out var parsed))
            {
                throw new InvalidOperationException(
                    "Manifest activeLayerUUID does not match any layer.");
            }

            activeId = parsed;
        }

        var doc = new Document(manifest.Width, manifest.Height, docId)
        {
            Name = manifest.Name,
            ActiveLayerId = activeId,
            Resolution = manifest.Resolution ?? 72,
        };

        // Raster accounting runs across the whole project, not per layer. Each image is inside
        // the per-surface ceiling on its own, but nothing stopped 10,000 of them from summing past
        // the document budget: the ceiling has to be charged cumulatively, at the point where the
        // next decode is still able to refuse, which is inside Png.Decode right after IHDR.
        long pixelsLoaded = 0;
        foreach (var layer in manifest.Layers)
        {
            var id = layerIdByUuid[layer.Uuid];
            RasterSurface? pixels = null;
            if (layer.Image is { } imageName)
            {
                using var entry = zip.GetEntry(imageName)!.Open();
                var (w, h, rgba) = Png.Decode(entry, out _, pixelsLoaded, documentBudget);
                pixels = new RasterSurface(w, h, rgba);
                pixelsLoaded += (long)w * h;
            }

            doc.AddLayer(new Layer(layer.Name, id)
            {
                IsVisible = layer.IsVisible,
                IsLocked = layer.IsLocked,
                IsGroup = layer.IsGroup,
                Opacity = layer.Opacity,
                Blend = ParseBlend(layer.BlendMode)!.Value,
                Transform = new LayerTransform(
                    layer.Transform.OriginX,
                    layer.Transform.OriginY,
                    layer.Transform.Width,
                    layer.Transform.Height,
                    layer.Transform.RotationDegrees,
                    layer.Transform.FlipH,
                    layer.Transform.FlipV),
                Pixels = pixels,
            });
        }

        // Second pass: resolve parent references (every layer now exists),
        // then enforce the structural invariants before handing the doc out.
        for (var i = 0; i < manifest.Layers.Count; i++)
        {
            if (manifest.Layers[i].ParentUuid is { } parentUuid)
            {
                doc.Layers[i].ParentId = layerIdByUuid[parentUuid];
            }
        }

        try
        {
            LayerHierarchy.Validate(doc.Layers);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"Invalid layer hierarchy: {ex.Message}", ex);
        }

        return doc;
    }

    private static BlendMode? ParseBlend(string value) => value switch
    {
        "normal" => BlendMode.Normal,
        "multiply" => BlendMode.Multiply,
        "screen" => BlendMode.Screen,
        "overlay" => BlendMode.Overlay,
        "darken" => BlendMode.Darken,
        "lighten" => BlendMode.Lighten,
        "difference" => BlendMode.Difference,
        "colorDodge" => BlendMode.ColorDodge,
        "colorBurn" => BlendMode.ColorBurn,
        "hue" => BlendMode.Hue,
        "saturation" => BlendMode.Saturation,
        "color" => BlendMode.Color,
        "luminosity" => BlendMode.Luminosity,
        _ => null,
    };

    private static string BlendToString(BlendMode mode) => mode switch
    {
        BlendMode.Normal => "normal",
        BlendMode.Multiply => "multiply",
        BlendMode.Screen => "screen",
        BlendMode.Overlay => "overlay",
        BlendMode.Darken => "darken",
        BlendMode.Lighten => "lighten",
        BlendMode.Difference => "difference",
        BlendMode.ColorDodge => "colorDodge",
        BlendMode.ColorBurn => "colorBurn",
        BlendMode.Hue => "hue",
        BlendMode.Saturation => "saturation",
        BlendMode.Color => "color",
        BlendMode.Luminosity => "luminosity",
        _ => throw new InvalidOperationException($"Unknown blend mode {mode}."),
    };
}
