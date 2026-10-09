using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Compositor.Core.Imaging;
using Xunit;

namespace Compositor.Core.Tests;

/// <summary>
/// Allocation bounds on the decode path. Every case here is a file that a hostile or corrupt
/// producer could hand the editor, and the assertion is always about WHERE the refusal happens:
/// a ceiling that fires before the allocation and a ceiling that fires after it look identical
/// in a pass/fail column, so the tests distinguish them by the exception. An
/// <see cref="InvalidDataException"/> naming the limit proves the length was rejected while
/// nothing had been committed yet; an <see cref="EndOfStreamException"/> would prove the
/// opposite, that the buffer was allocated and the read then ran out of file.
///
/// The PNGs are assembled here byte by byte rather than produced by the encoder, because the
/// encoder cannot emit the malformed shapes under test, and the CRC is computed by a local
/// implementation so that a defect in the production one shows up as a failure instead of being
/// mirrored by the test.
/// </summary>
public sealed class ImageBoundTests
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // ----- PNG chunk length -----

    [Fact]
    public void Decode_RejectsNegativeChunkLength()
    {
        var bytes = AssemblePng(RawChunkHeader(-1, "IDAT"));

        var error = Assert.Throws<InvalidDataException>(() => DecodeBytes(bytes));

        Assert.Contains("negative", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_RejectsOversizedIdatBeforeReadingIt()
    {
        // The declared 100 MB is never present in the stream. If the decoder allocated the chunk
        // first it would run out of file and raise EndOfStreamException; the limit message is the
        // proof that the check happened before the allocation.
        var bytes = AssemblePng(Chunk("IHDR", Ihdr(4, 4)), RawChunkHeader(100_000_000, "IDAT"));

        var error = Assert.Throws<InvalidDataException>(() => DecodeBytes(bytes));

        Assert.Contains("declares 100000000 bytes", error.Message, StringComparison.Ordinal);
        Assert.Contains("the limit is", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_RejectsOversizedAncillaryChunkBeforeReadingIt()
    {
        // 20 MB past the 16 MB cap for a chunk that is not IDAT. This one is placed before IHDR,
        // where the image size is not yet known and no image-derived ceiling can be computed.
        var bytes = AssemblePng(RawChunkHeader(20_000_000, "tEXt"));

        var error = Assert.Throws<InvalidDataException>(() => DecodeBytes(bytes));

        Assert.Contains("tEXt", error.Message, StringComparison.Ordinal);
        Assert.Contains("the limit is", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_RejectsAnIdatStreamThatInflatesPastTheImage()
    {
        // A genuine decompression bomb: 4 MB of zeros compresses to a few kilobytes, so it sails
        // through the chunk-length ceiling and only the inflate bound can stop it. A 4x4 image
        // needs 68 bytes of filtered rows; anything past that is data the file has no place for.
        var bomb = Zlib(new byte[4 * 1024 * 1024]);
        Assert.True(bomb.Length < 64 * 1024, $"the fixture must be small; it was {bomb.Length} bytes");

        var bytes = AssemblePng(Chunk("IHDR", Ihdr(4, 4)), Chunk("IDAT", bomb), Chunk("IEND", []));

        var error = Assert.Throws<InvalidDataException>(() => DecodeBytes(bytes));

        Assert.Contains("longer than the declared image needs", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_RejectsPixelDataThatIsTooShort()
    {
        // The other side of the same bound: 10 bytes cannot fill the 68 a 4x4 image needs.
        var bytes = AssemblePng(Chunk("IHDR", Ihdr(4, 4)), Chunk("IDAT", Zlib(new byte[10])), Chunk("IEND", []));

        var error = Assert.Throws<InvalidDataException>(() => DecodeBytes(bytes));

        Assert.Contains("truncated", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_AcceptsExactlyThePixelDataTheImageNeeds()
    {
        // Control for the two above: the same shape with exactly 68 bytes decodes.
        var bytes = Encode(4, 4);

        var (width, height, rgba) = Png.Decode(new MemoryStream(bytes));

        Assert.Equal(4, width);
        Assert.Equal(4, height);
        Assert.Equal(64, rgba.Length);
    }

    // ----- PNG dimensions -----

    [Fact]
    public void Decode_RejectsASurfaceOverThePerSurfaceCeiling()
    {
        // 30,000 x 20,000 = 600 MP. Inside the 30,000-per-side limit, and inside the document
        // budget on a machine with enough memory, but its RGBA form is 2,400,000,000 bytes, which
        // is past int.MaxValue. Only the per-surface ceiling stops it.
        var bytes = AssemblePng(Chunk("IHDR", Ihdr(30_000, 20_000)), Chunk("IDAT", Zlib(new byte[4])), Chunk("IEND", []));

        var error = Assert.Throws<ImageException>(() => DecodeBytes(bytes));

        Assert.Equal(ImageFailure.ImportTooLarge, error.Failure);
    }

    [Fact]
    public void Decode_PassesTheBudgetForASurfaceExactlyAtTheCeiling()
    {
        // 14,142^2 = 199,996,164 px, the largest square that fits one surface. The IDAT here is
        // deliberately too short, so the refusal that comes back must be about the pixel data and
        // not about the budget: that difference is what shows the budget check let it through.
        var bytes = AssemblePng(Chunk("IHDR", Ihdr(14_142, 14_142)), Chunk("IDAT", Zlib(new byte[4])), Chunk("IEND", []));

        var error = Assert.Throws<InvalidDataException>(() => DecodeBytes(bytes));

        Assert.Contains("truncated", error.Message, StringComparison.Ordinal);
    }

    // ----- cumulative raster accounting -----

    [Fact]
    public void Decode_ChargesEverySurfaceAgainstTheDocumentBudget()
    {
        var image = Encode(100, 100); // 10,000 px

        // First surface: 10,000 of a 15,000 px budget, so it fits.
        Png.Decode(new MemoryStream(image), out _, pixelsAlreadyUsed: 0, documentBudget: 15_000);

        // Second surface: the same image, charged on top of the first, no longer fits.
        var error = Assert.Throws<ImageException>(() =>
            Png.Decode(new MemoryStream(image), out _, pixelsAlreadyUsed: 10_000, documentBudget: 15_000));

        Assert.Equal(ImageFailure.ImportTooLarge, error.Failure);
    }

    [Theory]
    [InlineData(20_000, true)] // exactly: 10,000 px already spent + this 10,000 px image
    [InlineData(19_999, false)] // one pixel of budget short of it
    public void Decode_CumulativeBudgetBoundaryIsExact(long budget, bool accepted)
    {
        var image = Encode(100, 100);

        if (accepted)
        {
            Png.Decode(new MemoryStream(image), out _, pixelsAlreadyUsed: 10_000, documentBudget: budget);
        }
        else
        {
            Assert.Throws<ImageException>(() =>
                Png.Decode(new MemoryStream(image), out _, pixelsAlreadyUsed: 10_000, documentBudget: budget));
        }
    }

    [Fact]
    public void Decode_SurfaceCeilingHoldsHoweverLargeTheDocumentBudgetIs()
    {
        // The exact claim in the audit: a machine whose document budget reaches 600 MP would
        // accept this surface on the budget check alone. The per-surface ceiling is what refuses
        // it, and it must not be reachable by raising the other number.
        var bytes = AssemblePng(Chunk("IHDR", Ihdr(30_000, 20_000)), Chunk("IDAT", Zlib(new byte[4])), Chunk("IEND", []));

        Assert.Throws<ImageException>(() =>
            Png.Decode(new MemoryStream(bytes), out _, pixelsAlreadyUsed: 0, documentBudget: 800_000_000));
    }

    // ----- budget boundaries, with the document budget injected -----

    [Fact]
    public void Fits_RefusesASurfaceOverTheCeilingEvenWhenTheDocumentBudgetAllowsIt()
    {
        // The audit's exact claim. On a machine whose document budget reaches 600 MP, the budget
        // check alone accepts this surface; the per-surface ceiling is the only thing that refuses
        // it, and it must not be reachable by raising the other number.
        Assert.False(ImageBudget.Fits(30_000, 20_000, 0, documentBudget: 800_000_000));
        Assert.False(ImageBudget.FitsSurface(30_000, 20_000));
    }

    [Theory]
    [InlineData(14_142, 14_142, true)] // 199,996,164 px: the largest square that fits one surface
    [InlineData(14_143, 14_143, false)] // 200,024,449 px: one row past it
    [InlineData(30_000, 6_666, true)] // 199,980,000 px: a long thin surface still fits
    [InlineData(30_000, 6_667, false)] // 200,010,000 px: one row past it
    public void FitsSurface_BoundaryIsExact(int width, int height, bool expected) =>
        Assert.Equal(expected, ImageBudget.FitsSurface(width, height));

    [Fact]
    public void ValidateCanvas_EnforcesTheSameCeilingsAsImport()
    {
        ImageBudget.ValidateCanvas(14_142, 14_142, documentBudget: 200_000_000);

        Assert.Throws<ImageException>(() => ImageBudget.ValidateCanvas(14_143, 14_143, documentBudget: 200_000_000));
        Assert.Throws<ImageException>(() => ImageBudget.ValidateCanvas(30_000, 20_000, documentBudget: 800_000_000));
    }

    [Theory]
    [InlineData(0, 10, false)]
    [InlineData(10, 0, false)]
    [InlineData(-1, 10, false)]
    public void Fits_RefusesNonPositiveDimensions(int width, int height, bool expected) =>
        Assert.Equal(expected, ImageBudget.FitsSurface(width, height));

    [Fact]
    public void RgbaByteCount_IsComputedInSixtyFourBit()
    {
        // The premise behind every ceiling here, as arithmetic: the naive 32-bit product of the
        // largest surface is negative or small, which is how a wrapped allocation slips through.
        Assert.Equal(2_400_000_000L, ImageBudget.RgbaByteCount(30_000, 20_000));
        Assert.True(ImageBudget.RgbaByteCount(30_000, 20_000) > int.MaxValue);
        Assert.Equal(4L, ImageBudget.RgbaByteCount(1, 1));
    }

    // ----- fixtures -----

    private static void DecodeBytes(byte[] bytes) => Png.Decode(new MemoryStream(bytes));

    private static byte[] Encode(int width, int height)
    {
        var stream = new MemoryStream();
        Png.Encode(stream, width, height, new byte[width * height * 4]);
        stream.Position = 0;
        return stream.ToArray();
    }

    private static byte[] AssemblePng(params byte[][] chunks)
    {
        var bytes = new List<byte>(Signature);
        foreach (var chunk in chunks)
        {
            bytes.AddRange(chunk);
        }

        return bytes.ToArray();
    }

    private static byte[] Ihdr(int width, int height)
    {
        var data = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(data, width);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(4), height);
        data[8] = 8; // bit depth
        data[9] = 6; // colour type: RGBA
        data[10] = 0; // compression
        data[11] = 0; // filter
        data[12] = 0; // interlace
        return data;
    }

    /// <summary>A complete chunk: length, type, data, CRC over type+data.</summary>
    private static byte[] Chunk(string type, byte[] data)
    {
        var bytes = new byte[12 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(bytes, data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(bytes, 4);
        data.CopyTo(bytes, 8);
        BinaryPrimitives.WriteUInt32BigEndian(
            bytes.AsSpan(8 + data.Length),
            Crc32(bytes.AsSpan(4, 4 + data.Length)));
        return bytes;
    }

    /// <summary>A chunk header with a length the producer chose and no data behind it.</summary>
    private static byte[] RawChunkHeader(int length, string type)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(bytes, length);
        Encoding.ASCII.GetBytes(type).CopyTo(bytes, 4);
        return bytes;
    }

    private static byte[] Zlib(byte[] raw)
    {
        using var stream = new MemoryStream();
        using (var deflate = new ZLibStream(stream, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw);
        }

        return stream.ToArray();
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
            }
        }

        return ~crc;
    }
}
