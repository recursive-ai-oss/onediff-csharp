using System.IO.Compression;

namespace OneDiff.Word.Internal;

/// <summary>Content-derived image fingerprints.</summary>
internal static class ImageHashes
{
    private const int MaxSide = 32_768;
    private const long MaxDecodedBytes = 256L << 20;

    /// <summary>
    /// A perceptual hash of the image: 16 lower-case hex digits, one bit per cell of an 8 x 8 grid (the cell's mean luminance is above
    /// the mean of all cells: the "average hash"). Decodes non-interlaced PNG of 8 or 16 bits per channel (palette: 8 bits only);
    /// null for any other format or for an image that is damaged or too large to decode within the limit.
    /// </summary>
    public static string? Perceptual(ReadOnlySpan<byte> image)
    {
        try
        {
            return PerceptualPng(image);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or OverflowException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static string? PerceptualPng(ReadOnlySpan<byte> png)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (png.Length < 33 || !png[..8].SequenceEqual(signature))
        {
            return null;
        }

        int width = 0, height = 0, depth = 0, colorType = 0, interlace = 0;
        byte[] palette = [];
        using var idat = new MemoryStream();
        int pos = 8;
        bool header = false;
        while (pos + 12 <= png.Length)
        {
            uint length = ReadUInt32(png, pos);
            if (length > int.MaxValue - 12 || pos + 12L + length > png.Length)
            {
                return null;
            }

            ReadOnlySpan<byte> type = png.Slice(pos + 4, 4);
            ReadOnlySpan<byte> data = png.Slice(pos + 8, (int)length);
            if (type.SequenceEqual("IHDR"u8) && length == 13)
            {
                width = (int)ReadUInt32(data, 0);
                height = (int)ReadUInt32(data, 4);
                depth = data[8];
                colorType = data[9];
                interlace = data[12];
                header = true;
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                palette = data.ToArray();
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                idat.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }

            pos += 12 + (int)length;
        }

        if (!header || interlace != 0 || width < 1 || height < 1 || width > MaxSide || height > MaxSide)
        {
            return null;
        }

        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (channels == 0 || !(depth == 8 || (depth == 16 && colorType != 3)))
        {
            return null;
        }

        int bytesPerPixel = channels * depth / 8;
        long stride = (long)width * bytesPerPixel;
        long expected = (stride + 1) * height;
        if (expected > MaxDecodedBytes)
        {
            return null;
        }

        var raw = new byte[expected];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            int got = 0;
            while (got < raw.Length)
            {
                int n = z.Read(raw, got, raw.Length - got);
                if (n == 0)
                {
                    return null;
                }

                got += n;
            }
        }

        Unfilter(raw, (int)stride, height, bytesPerPixel);

        // Luminance per pixel, summed into the 8 x 8 grid by integer box averaging.
        var luma = new long[64];
        var count = new long[64];
        for (int y = 0; y < height; y++)
        {
            int cy = (int)((long)y * 8 / height);
            int row = (int)((stride + 1) * y) + 1;
            for (int x = 0; x < width; x++)
            {
                int cx = (int)((long)x * 8 / width);
                int p = row + (x * bytesPerPixel);
                int step = depth / 8;
                int l = Luminance(raw, p, colorType, step, palette);
                luma[(cy * 8) + cx] += l;
                count[(cy * 8) + cx]++;
            }
        }

        var cells = new long[64];
        long total = 0;
        for (int i = 0; i < 64; i++)
        {
            // Fixed-point (x256) cell mean; a cell no pixel reached (an image narrower than 8) takes its neighbour's.
            cells[i] = count[i] == 0 ? -1 : luma[i] * 256 / count[i];
        }

        for (int i = 0; i < 64; i++)
        {
            if (cells[i] < 0)
            {
                int src = i;
                while (src > 0 && cells[src] < 0)
                {
                    src--;
                }

                cells[i] = cells[src] < 0 ? 0 : cells[src];
            }

            total += cells[i];
        }

        long mean = total / 64;
        ulong bits = 0;
        for (int i = 0; i < 64; i++)
        {
            if (cells[i] > mean)
            {
                bits |= 1UL << (63 - i);
            }
        }

        return bits.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int Luminance(byte[] raw, int p, int colorType, int step, byte[] palette)
    {
        int r, g, b, alpha = 255;
        switch (colorType)
        {
            case 0:
                r = g = b = raw[p];
                break;
            case 2:
                r = raw[p];
                g = raw[p + step];
                b = raw[p + (2 * step)];
                break;
            case 3:
                {
                    int index = raw[p] * 3;
                    if (index + 2 >= palette.Length)
                    {
                        return 0;
                    }

                    r = palette[index];
                    g = palette[index + 1];
                    b = palette[index + 2];
                    break;
                }

            case 4:
                r = g = b = raw[p];
                alpha = raw[p + step];
                break;
            default:
                r = raw[p];
                g = raw[p + step];
                b = raw[p + (2 * step)];
                alpha = raw[p + (3 * step)];
                break;
        }

        int y = ((299 * r) + (587 * g) + (114 * b)) / 1000;
        // Composite over white, so that a transparent pixel reads as paper.
        return ((y * alpha) + (255 * (255 - alpha))) / 255;
    }

    // PNG specification section 9: the five row filters, applied in place.
    private static void Unfilter(byte[] raw, int stride, int height, int bpp)
    {
        for (int y = 0; y < height; y++)
        {
            int row = (stride + 1) * y;
            byte filter = raw[row];
            int cur = row + 1;
            int prev = y == 0 ? -1 : cur - (stride + 1);
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? raw[cur + i - bpp] : 0;
                int b = prev >= 0 ? raw[prev + i] : 0;
                int c = prev >= 0 && i >= bpp ? raw[prev + i - bpp] : 0;
                int add = filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException("unknown PNG filter"),
                };
                raw[cur + i] = (byte)(raw[cur + i] + add);
            }
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> s, int at) =>
        ((uint)s[at] << 24) | ((uint)s[at + 1] << 16) | ((uint)s[at + 2] << 8) | s[at + 3];
}
