using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Compositor.Core.Imaging;

/// <summary>
/// Minimal PNG encode/decode for 8-bit RGBA (color type 6, non-interlaced)
/// project layer assets. Enough for .comp round-trips; not a general codec.
/// </summary>
public static class Png
{
    private const int ColorTypeRgba8 = 6;
    private const int BitsPerChannel = 8;
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private const double MillimetresPerMetreOverInch = 0.0254;

    /// <summary>
    /// Ceiling for a chunk that is not IDAT. IHDR is 13 bytes and pHYs 9, so this is far above
    /// anything a project asset legitimately carries; it exists so an unknown chunk's declared
    /// length cannot be turned into an allocation of the attacker's choosing.
    /// </summary>
    private const int MaxAncillaryChunkLength = 16 * 1024 * 1024;

    /// <summary>DPI to the pHYs unit PNG stores (pixels per metre, per ISO/IEC 10918 companion spec).</summary>
    public static uint PixelsPerMeter(double dpi) =>
        (uint)Math.Round(dpi / MillimetresPerMetreOverInch, MidpointRounding.AwayFromZero);

    /// <summary>Inverse of <see cref="PixelsPerMeter"/>; 72 dpi round-trips to 72.</summary>
    public static double DpiFromPixelsPerMeter(uint pixelsPerMetre) => pixelsPerMetre * MillimetresPerMetreOverInch;

    public static void Encode(Stream output, int width, int height, ReadOnlySpan<byte> rgba, double? dpi = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException("Invalid dimensions.");
        }

        var expected = width * height * 4;
        if (rgba.Length != expected)
        {
            throw new ArgumentException($"Pixel buffer is {rgba.Length} bytes; expected {expected}.");
        }

        output.Write(Signature);

        // IHDR: width, height, depth, color type, compression 0, filter 0, interlace 0.
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = BitsPerChannel;
        ihdr[9] = ColorTypeRgba8;
        WriteChunk(output, "IHDR"u8, ihdr);

        // pHYs: upstream stamps manifest.resolution into exported PNGs, so print size survives.
        if (dpi is { } value && double.IsFinite(value) && value > 0)
        {
            var phys = new byte[9];
            var perMeter = PixelsPerMeter(value);
            BinaryPrimitives.WriteUInt32BigEndian(phys, perMeter);
            BinaryPrimitives.WriteUInt32BigEndian(phys.AsSpan(4), perMeter);
            phys[8] = 1; // unit specifier: metre
            WriteChunk(output, "pHYs"u8, phys);
        }

        // Raw scanlines with filter byte 0 (None) per row, then deflate.
        var stride = (width * 4) + 1;
        var raw = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            rgba.Slice(y * width * 4, width * 4)
                .CopyTo(raw.AsSpan((y * stride) + 1));
        }

        using (var deflated = new MemoryStream())
        {
            var zlib = new ZLibStream(deflated, CompressionLevel.Optimal);
            zlib.Write(raw);
            zlib.Dispose();
            WriteChunk(output, "IDAT"u8, deflated.ToArray());
        }

        WriteChunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
    }

    public static (int Width, int Height, byte[] Rgba) Decode(Stream input) => Decode(input, out _);

    /// <summary>
    /// Decodes an 8-bit RGBA PNG, reporting the pHYs density as DPI when the chunk is
    /// present and metric. Everything else about pHYs is ignored, as upstream ignores it.
    /// </summary>
    public static (int Width, int Height, byte[] Rgba) Decode(Stream input, out double? dpi) =>
        Decode(input, out dpi, pixelsAlreadyUsed: 0);

    /// <summary>
    /// Decodes an 8-bit RGBA PNG, charging its pixels against a document budget.
    ///
    /// <paramref name="pixelsAlreadyUsed"/> is what every surface decoded before this one has
    /// spent, and the check runs immediately after IHDR: a project can be made of many layers
    /// that are each inside the per-surface ceiling and still sum past the document budget, and
    /// the only place that sum can stop an allocation is here, before the inflate and the RGBA
    /// buffer are created. <paramref name="documentBudget"/> overrides the machine-scaled budget
    /// so boundary tests do not depend on how much memory the host happens to have.
    /// </summary>
    public static (int Width, int Height, byte[] Rgba) Decode(
        Stream input,
        out double? dpi,
        long pixelsAlreadyUsed,
        long? documentBudget = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        dpi = null;

        var signature = new byte[8];
        ReadExactly(input, signature);
        if (!signature.AsSpan().SequenceEqual(Signature))
        {
            throw new InvalidDataException("Not a PNG file.");
        }

        int width = 0, height = 0;
        var idat = new List<byte[]>();
        long totalIdat = 0;
        long rawSize = 0;
        var chunkHeader = new byte[8];

        while (true)
        {
            if (!TryReadExactly(input, chunkHeader))
            {
                throw new InvalidDataException("PNG truncated before IEND.");
            }

            var length = BinaryPrimitives.ReadInt32BigEndian(chunkHeader);
            var type = (ReadOnlySpan<byte>)chunkHeader.AsSpan(4);

            // Checked before the allocation, not after. `new byte[length]` with a length read
            // straight off the wire is the whole attack: the previous form allocated whatever the
            // chunk header asked for, so a 2 GB IDAT length was committed to memory and only then
            // had its CRC checked. A negative length is not a length at all, and everything else
            // is bounded by what the declared image can actually need.
            if (length < 0)
            {
                throw new InvalidDataException($"PNG chunk length {length} is negative.");
            }

            var ceiling = ChunkCeiling(type, rawSize);
            if (length > ceiling)
            {
                throw new InvalidDataException(
                    $"PNG chunk '{TypeName(type)}' declares {length} bytes; " +
                    $"the limit is {ceiling} for a {width}x{height} image.");
            }

            var data = new byte[length];
            ReadExactly(input, data);

            var crc = new byte[4];
            ReadExactly(input, crc);
            var expectedCrc = Crc32(chunkHeader.AsSpan(4), data);
            if (BinaryPrimitives.ReadInt32BigEndian(crc) != (int)expectedCrc)
            {
                throw new InvalidDataException("PNG chunk CRC mismatch.");
            }

            if (type.SequenceEqual("IHDR"u8))
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4));
                if (width <= 0 || height <= 0 || data[8] != BitsPerChannel || data[9] != ColorTypeRgba8 || data[12] != 0)
                {
                    throw new InvalidDataException("Unsupported PNG: only 8-bit RGBA non-interlaced.");
                }

                // Bound the dimensions here, before anything multiplies them. Without this the
                // stride and the final `new byte[width * height * 4]` are computed from two
                // attacker-chosen ints, and 30,000 x 20,000 (600 MP) is 2,400,000,000 bytes,
                // past int.MaxValue, so the product wraps and the buffer comes out the wrong size.
                // The second call charges this surface against what earlier layers already spent.
                if (!ImageBudget.Fits(width, height, pixelsAlreadyUsed, documentBudget))
                {
                    throw new ImageException(ImageFailure.ImportTooLarge);
                }

                rawSize = ((long)width * 4 + 1) * height;
            }
            else if (type.SequenceEqual("pHYs"u8))
            {
                if (data.Length == 9 && data[8] == 1)
                {
                    dpi = DpiFromPixelsPerMeter(BinaryPrimitives.ReadUInt32BigEndian(data));
                }
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                totalIdat += length;
                if (totalIdat > IdatTotalCeiling(rawSize))
                {
                    throw new InvalidDataException(
                        $"PNG IDAT stream is {totalIdat} bytes; the limit is " +
                        $"{IdatTotalCeiling(rawSize)} for a {width}x{height} image.");
                }

                idat.Add(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }
        }

        if (width <= 0 || height <= 0 || idat.Count == 0)
        {
            throw new InvalidDataException("PNG missing IHDR or IDAT.");
        }

        var compressed = new byte[totalIdat];
        var offset = 0;
        foreach (var chunk in idat)
        {
            chunk.CopyTo(compressed, offset);
            offset += chunk.Length;
        }

        // Inflate to exactly the size the image needs and no further. `CopyTo` had no ceiling:
        // a few kilobytes of IDAT can inflate to gigabytes, and the ceiling has to be applied
        // while reading, because by the time the length is known the memory is already gone.
        var rawBytes = new byte[rawSize];
        using (var concatenated = new MemoryStream(compressed, writable: false))
        using (var inflate = new ZLibStream(concatenated, CompressionMode.Decompress))
        {
            var filled = 0;
            while (filled < rawBytes.Length)
            {
                var read = inflate.Read(rawBytes, filled, rawBytes.Length - filled);
                if (read == 0)
                {
                    break;
                }

                filled += read;
            }

            if (filled < rawBytes.Length)
            {
                throw new InvalidDataException("PNG pixel data truncated.");
            }

            if (inflate.ReadByte() >= 0)
            {
                throw new InvalidDataException(
                    "PNG pixel data is longer than the declared image needs.");
            }
        }

        var stride = (width * 4) + 1;
        Defilter(rawBytes, width, height);
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            rawBytes.AsSpan((y * stride) + 1, width * 4).CopyTo(rgba.AsSpan(y * width * 4));
        }

        return (width, height, rgba);
    }

    /// <summary>Largest single chunk this decoder will accept for the image declared so far.</summary>
    private static long ChunkCeiling(ReadOnlySpan<byte> type, long rawSize)
    {
        // IDAT is the only chunk whose legitimate size scales with the image. Deflate cannot
        // expand data by more than a fraction of a percent (stored blocks cost 5 bytes per
        // 65,535), so 1% plus a block of slack is a ceiling no valid encoder can reach.
        if (type.SequenceEqual("IDAT"u8) && rawSize > 0)
        {
            return rawSize + (rawSize / 100) + 65_536;
        }

        // Everything else in a project asset is small: IHDR is 13 bytes, pHYs 9, IEND 0. This
        // decoder ignores unknown chunks but still has to read them, so they get a generous cap.
        return MaxAncillaryChunkLength;
    }

    /// <summary>Largest total IDAT payload, applied while chunks are being collected.</summary>
    private static long IdatTotalCeiling(long rawSize) =>
        rawSize > 0 ? rawSize + (rawSize / 100) + (1024 * 1024) : MaxAncillaryChunkLength;

    private static string TypeName(ReadOnlySpan<byte> type)
    {
        Span<char> text = stackalloc char[4];
        for (var i = 0; i < 4; i++)
        {
            var b = type[i];
            text[i] = b is >= 32 and < 127 ? (char)b : '?';
        }

        return new string(text);
    }

    private static void Defilter(byte[] raw, int width, int height)
    {
        var stride = (width * 4) + 1;
        var bpp = 4;
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * stride];
            var row = y * stride;
            for (var x = 1; x < stride; x++)
            {
                var a = x > bpp ? raw[row + x - bpp] : (byte)0;
                var b = y > 0 ? raw[row - stride + x] : (byte)0;
                var c = y > 0 && x > bpp ? raw[row - stride + x - bpp] : (byte)0;
                raw[row + x] = filter switch
                {
                    0 => raw[row + x],
                    1 => (byte)(raw[row + x] + a),
                    2 => (byte)(raw[row + x] + b),
                    3 => (byte)(raw[row + x] + ((a + b) >> 1)),
                    4 => (byte)(raw[row + x] + Paeth(a, b, c)),
                    _ => throw new InvalidDataException($"Unknown PNG filter {filter}."),
                };
            }
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = (a + b) - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(header, data.Length);
        type.CopyTo(header[4..]);
        output.Write(header);
        output.Write(data);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(type, data));
        output.Write(crc);
    }

    private static uint Crc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var t in a)
        {
            crc = Update(crc, t);
        }

        foreach (var t in b)
        {
            crc = Update(crc, t);
        }

        return crc ^ 0xFFFFFFFF;

        static uint Update(uint crc, byte value)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                var mask = (crc & 1) != 0 ? 0xEDB88320u : 0;
                crc = (crc >> 1) ^ mask;
            }

            return crc;
        }
    }

    private static void ReadExactly(Stream input, Span<byte> buffer)
    {
        if (!TryReadExactly(input, buffer))
        {
            throw new EndOfStreamException();
        }
    }

    private static bool TryReadExactly(Stream input, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = input.Read(buffer[total..]);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }
}
