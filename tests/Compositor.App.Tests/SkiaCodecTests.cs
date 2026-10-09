using Compositor.App.Imaging;
using Compositor.Core.Imaging;
using SkiaSharp;
using Xunit;

namespace Compositor.App.Tests;

/// <summary>
/// Codec capability and round-trip tests. The capability assertions exist so the format
/// matrix in Core can never claim more than the Skia build we actually ship: an over-claim
/// fails here, not on a user's file. Extra containers are embedded as real fixtures written
/// by other tools (ffmpeg, Pillow), because this Skia build has no encoder for them and a
/// fixture produced by the encoder under test would prove nothing.
/// </summary>
public class SkiaCodecTests
{
    private const int Width = 6;
    private const int Height = 4;

    // 8x4, left half red / right half green, GIF87a, 57 bytes (Pillow).
    private const string GifFixture = "R0lGODdhCAAEAIEAAAD/AP8AAAAAAAAAACwAAAAACAAEAAAIEgADCAwAoCCAgQQNIjR4cCDDgAA7";

    // 8x4 24-bit BMP (ffmpeg).
    private const string BmpFixture =
        "Qk22AAAAAAAAADYAAAAoAAAACAAAAAQAAAABACAAAAAAAIAAAAAAAAAAAAAAAAAAAAAAAAAAAAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8=";

    // 8x4 ICO (ffmpeg).
    private const string IcoFixture =
        "AAABAAEACAQAAAEAIACvAAAAFgAAACgAAAAIAAAACAAAAAEAIAAAAAAAgAAAAAAAAAAAAAAAAAAAAAAAAAAAAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAA//8AAP//AAD//wAAAAAAAAA=";

    // 8x4 lossy WebP (ffmpeg).
    private const string WebpFixture = "UklGRjwAAABXRUJQVlA4IDAAAADQAQCdASoIAAQAAgA0JaACdLoB+AADsAD+8MQL/yC5YXXI1/8gP+QH/ID/+PIAAAA=";

    // 8x4 left-red/right-green JPEG carrying a real EXIF orientation=6 written by Pillow, not
    // by us: it checks the tag parser against a third-party writer.
    private const string ExifOrientedJpeg =
        "/9j/4AAQSkZJRgABAQAAAQABAAD/4QAiRXhpZgAATU0AKgAAAAgAAQESAAMAAAABAAYAAAAAAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAAEAAgDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwCh4S/5fP8AgH/s1FFFfnuM/jS/rofKeIf/ACUmJ/7c/wDTcT//2Q==";

    private static byte[] Rgba(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> pixel)
    {
        var buffer = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b, a) = pixel(x, y);
                var at = (y * width * 4) + (x * 4);
                buffer[at] = r;
                buffer[at + 1] = g;
                buffer[at + 2] = b;
                buffer[at + 3] = a;
            }
        }

        return buffer;
    }

    private static (byte R, byte G, byte B, byte A) Pattern(int x, int y) =>
        ((byte)(20 + (x * 30)), (byte)(200 - (y * 40)), (byte)(40 + (x * 10)), (x + y) % 2 == 0 ? (byte)255 : (byte)128);

    private static byte[] PngBytes()
    {
        using var stream = new MemoryStream();
        Png.Encode(stream, Width, Height, Rgba(Width, Height, Pattern));
        return stream.ToArray();
    }

    private static byte[] OpaqueRgba(int width, int height) =>
        Rgba(width, height, static (x, y) => ((byte)(20 + (x * 30)), (byte)(200 - (y * 40)), (byte)(40 + (x * 10)), byte.MaxValue));

    /// Real bytes for a container plus the size it was authored at: ours where we write it,
    /// an embedded third-party fixture otherwise.
    private static (byte[] Bytes, int Width, int Height)? Fixture(ImageFormat format) => format switch
    {
        ImageFormat.Png => (PngBytes(), Width, Height),
        ImageFormat.Jpeg => (SkiaEncode(OpaqueRgba(Width, Height), SKEncodedImageFormat.Jpeg)!, Width, Height),
        ImageFormat.Gif => (Convert.FromBase64String(GifFixture), 8, 4),
        ImageFormat.Bmp => (Convert.FromBase64String(BmpFixture), 8, 4),
        ImageFormat.Ico => (Convert.FromBase64String(IcoFixture), 8, 4),
        ImageFormat.WebP => (Convert.FromBase64String(WebpFixture), 8, 4),
        _ => null,
    };

    private static byte[]? SkiaEncode(byte[] rgba, SKEncodedImageFormat format)
    {
        var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var image = SKImage.FromPixelCopy(info, rgba);
        using var data = image?.Encode(format, 95);
        return data?.ToArray();
    }

    /// APP1 EXIF segment holding only an orientation tag, spliced after SOI the way a camera writes it.
    private static byte[] WithExifOrientation(byte[] jpeg, byte orientation)
    {
        var tiff = new byte[26];
        tiff[0] = (byte)'I';
        tiff[1] = (byte)'I';
        tiff[2] = 0x2A; // magic 0x002A, little-endian
        tiff[4] = 0x08; // IFD0 at offset 8
        tiff[8] = 0x01; // one entry
        tiff[10] = 0x12; // tag 0x0112 = orientation
        tiff[11] = 0x01;
        tiff[12] = 0x03; // type SHORT
        tiff[14] = 0x01; // count 1
        tiff[18] = orientation; // value, first byte of the 4-byte value field

        var payload = new byte[6 + tiff.Length];
        "Exif\0\0"u8.CopyTo(payload);
        tiff.CopyTo(payload, 6);

        var segment = new byte[4 + payload.Length];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        segment[2] = (byte)((payload.Length + 2) >> 8);
        segment[3] = (byte)(payload.Length + 2);
        payload.CopyTo(segment, 4);

        var output = new byte[jpeg.Length + segment.Length];
        jpeg.AsSpan(0, 2).CopyTo(output);
        segment.CopyTo(output, 2);
        jpeg.AsSpan(2).CopyTo(output.AsSpan(2 + segment.Length));
        return output;
    }

    private static (byte R, byte G, byte B) At(DecodedImage image, int x, int y)
    {
        var at = (y * image.Width * 4) + (x * 4);
        return (image.Rgba[at], image.Rgba[at + 1], image.Rgba[at + 2]);
    }

    [Fact]
    public void NativeSkia_Loads_AndEncodesJpeg()
    {
        var jpeg = SkiaCodec.EncodeJpeg(OpaqueRgba(Width, Height), Width, Height, new JpegOptions());

        Assert.True(jpeg.Length > 100, $"encoder produced only {jpeg.Length} bytes");
        Assert.Equal(0xFF, jpeg[0]);
        Assert.Equal(0xD8, jpeg[1]);
        Assert.Equal(0xD9, jpeg[^1]); // EOI: a complete stream, not a truncated buffer
        Assert.Equal(0xFF, jpeg[^2]);
    }

    [Fact]
    public void FormatMatrix_EveryImportRow_IsActuallyDecodable()
    {
        foreach (var row in ImageFormatPolicy.Importable)
        {
            var fixture = Fixture(row.Format);
            Assert.True(fixture is not null, $"no fixture for {row.DisplayName}");
            var (bytes, width, height) = fixture!.Value;

            Assert.Equal(row.Format, ImageFormatPolicy.Detect(bytes));
            var decoded = SkiaCodec.Decode(bytes);
            Assert.Equal(width, decoded.Width);
            Assert.Equal(height, decoded.Height);
            Assert.Equal(width * height * 4, decoded.Rgba.Length);
        }
    }

    [Fact]
    public void ImportIsRefusedForContainersTheMatrixDoesNotClaim()
    {
        var tiff = new byte[] { 0x49, 0x49, 0x2A, 0x00, 0x08, 0, 0, 0 };
        var heif = new byte[76];
        heif[3] = 0x18;
        "ftypheic"u8.CopyTo(heif.AsSpan(4));

        Assert.Equal(ImageFormat.Tiff, ImageFormatPolicy.Detect(tiff));
        Assert.Equal(ImageFormat.Heif, ImageFormatPolicy.Detect(heif));

        foreach (var bytes in new[] { tiff, heif })
        {
            var error = Assert.Throws<ImageException>(() => SkiaCodec.Decode(bytes));
            Assert.Equal(ImageFailure.Unsupported, error.Failure);
        }
    }

    [Fact]
    public void Decode_Png_KeepsAlphaExactAndColorWithinPremultiplyRounding()
    {
        var original = Rgba(Width, Height, Pattern);
        var decoded = SkiaCodec.Decode(PngBytes());

        Assert.Equal(ImageFormat.Png, decoded.Format);
        Assert.Equal(original.Length, decoded.Rgba.Length);
        for (var i = 0; i < original.Length; i += 4)
        {
            Assert.Equal(original[i + 3], decoded.Rgba[i + 3]); // alpha survives exactly
            var exact = original[i + 3] is 0 or 255;
            for (var c = 0; c < 3; c++)
            {
                var delta = Math.Abs(original[i + c] - decoded.Rgba[i + c]);
                if (exact)
                {
                    Assert.Equal(0, delta);
                }
                else
                {
                    // Skia holds decoded bitmaps premultiplied, so a partial-alpha channel pays
                    // a one-step rounding trip on the way back out. ImageIO does the same thing
                    // upstream, so this is the codec's cost, not this port's.
                    Assert.True(delta <= 2, $"channel {c} moved by {delta}");
                }
            }
        }
    }

    [Fact]
    public void Decode_Jpeg_ReadsBackAsOpaqueRgba()
    {
        var decoded = SkiaCodec.Decode(Fixture(ImageFormat.Jpeg)!.Value.Bytes);

        Assert.Equal(ImageFormat.Jpeg, decoded.Format);
        foreach (var i in Enumerable.Range(0, Width * Height))
        {
            Assert.Equal(255, decoded.Rgba[(i * 4) + 3]);
        }
    }

    [Fact]
    public void EncodeJpeg_FlattensTransparencyOntoMatte()
    {
        // Fully transparent canvas onto a red matte: every decoded pixel must read red within
        // the lossy codec's tolerance.
        var transparent = Rgba(8, 8, static (x, y) => (0, 0, 0, 0));
        var jpeg = SkiaCodec.EncodeJpeg(transparent, 8, 8, new JpegOptions { Red = 1, Green = 0, Blue = 0 });
        var decoded = SkiaCodec.Decode(jpeg);

        Assert.Equal(255, decoded.Rgba[3]);
        Assert.True(decoded.Rgba[0] > 230, $"R was {decoded.Rgba[0]}");
        Assert.True(decoded.Rgba[1] < 25, $"G was {decoded.Rgba[1]}");
        Assert.True(decoded.Rgba[2] < 25, $"B was {decoded.Rgba[2]}");
    }

    [Fact]
    public void EncodeJpeg_WritesDensityIntoJfif()
    {
        var jpeg = SkiaCodec.EncodeJpeg(OpaqueRgba(Width, Height), Width, Height, new JpegOptions(), dpi: 300);

        Assert.Equal(300d, JpegDensity.TryGet(jpeg));
    }

    [Fact]
    public void EncodeJpeg_WithoutDensity_LeavesJfifUnstamped()
    {
        var jpeg = SkiaCodec.EncodeJpeg(OpaqueRgba(Width, Height), Width, Height, new JpegOptions());

        Assert.Null(JpegDensity.TryGet(jpeg));
    }

    [Fact]
    public void Decode_SplicedExifSegment_ReportsDeclaredOrientationAndSwapsDimensions()
    {
        var jpeg = SkiaCodec.EncodeJpeg(OpaqueRgba(8, 4), 8, 4, new JpegOptions());
        Assert.Equal(ExifOrientation.Identity, ExifOrientation.Read(jpeg));

        var withTag = WithExifOrientation(jpeg, orientation: 6);
        Assert.Equal(6, ExifOrientation.Read(withTag)); // our parser, on bytes we control

        var decoded = SkiaCodec.Decode(withTag);
        Assert.Equal(6, decoded.Orientation);
        Assert.Equal(4, decoded.Width); // Skia applied it: an 8x4 comes back 4x8
        Assert.Equal(8, decoded.Height);
    }

    [Fact]
    public void Decode_RealExifJpegFromAnotherWriter_LandsUpright()
    {
        var bytes = Convert.FromBase64String(ExifOrientedJpeg);

        Assert.Equal(ImageFormat.Jpeg, ImageFormatPolicy.Detect(bytes));
        Assert.Equal(6, ExifOrientation.Read(bytes)); // our parser agrees with Pillow's writer

        var decoded = SkiaCodec.Decode(bytes);

        // The source is 8 wide by 4 tall with red on the left. Orientation 6 rotates 90 CW,
        // which moves red to the top. If the parser or the codec mis-handled the tag, either
        // the dimensions or the colours below would not line up.
        Assert.Equal(4, decoded.Width);
        Assert.Equal(8, decoded.Height);
        var top = At(decoded, 0, 0);
        var bottom = At(decoded, 0, 7);
        Assert.True(top.R > 150 && top.G < 110, $"top pixel was {top}");
        Assert.True(bottom.G > 150 && bottom.R < 110, $"bottom pixel was {bottom}");
    }

    [Fact]
    public void Decode_GarbageBytes_ThrowsUnreadable()
    {
        var bytes = new byte[64];
        new Random(7).NextBytes(bytes);

        var error = Assert.Throws<ImageException>(() => SkiaCodec.Decode(bytes));
        Assert.Equal(ImageFailure.Unreadable, error.Failure);
    }

    [Fact]
    public void EncodableFormats_AtLeastPngAndJpeg()
    {
        var formats = SkiaCodec.EncodableFormats();

        Assert.Contains(SKEncodedImageFormat.Png, formats);
        Assert.Contains(SKEncodedImageFormat.Jpeg, formats);
    }

    [Fact]
    public void Budget_LargeImport_IsRejectedBeforeAnyAllocation()
    {
        var error = Assert.Throws<ImageException>(() => ImageBudget.ValidateImport(29_000, 29_000, 0));
        Assert.Equal(ImageFailure.ImportTooLarge, error.Failure);
        Assert.Contains($"{ImageBudget.DocumentBudgetMegapixels}-megapixel", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Budget_PerSurfaceCeilingHoldsWhenTheDocumentBudgetIsHuge()
    {
        // 30,000 x 20,000 is inside the per-side limit and would pass a 600 MP document budget,
        // which is exactly what a machine with enough memory reports. Its RGBA form is
        // 2,400,000,000 bytes, past int.MaxValue, so the per-surface ceiling is the only check
        // that can refuse it and it must not be reachable by raising the other number.
        var error = Assert.Throws<ImageException>(() =>
            ImageBudget.ValidateImport(30_000, 20_000, 0, documentBudget: 800_000_000));

        Assert.Equal(ImageFailure.ImportTooLarge, error.Failure);
    }

    [Fact]
    public void Budget_ImportBoundaryAtThePerSurfaceCeilingIsExact()
    {
        // 14,142^2 = 199,996,164 px fits; 14,143^2 = 200,024,449 px does not. Both with a
        // document budget large enough that only the per-surface ceiling is in play.
        ImageBudget.ValidateImport(14_142, 14_142, 0, documentBudget: 800_000_000);

        Assert.Throws<ImageException>(() =>
            ImageBudget.ValidateImport(14_143, 14_143, 0, documentBudget: 800_000_000));
    }
}
