using System.Text;

namespace OneDiff.Patching;

/// <summary>
/// The percent-encoding of patch text lines: the URI encoding of UTF-8 bytes, except that space is written as itself (the wiki's
/// "Unidiff" page). A lone surrogate is written as the three bytes its code unit would take in UTF-8 and read back the same way, so
/// any .NET string survives a round trip.
/// </summary>
internal static class DmpText
{
    private const string Unreserved = "-_.!~*'();/?:@&=+$,# ";

    public static void Encode(StringBuilder sb, string text)
    {
        const string hex = "0123456789ABCDEF";
        Span<byte> bytes = stackalloc byte[4]; // one scalar value takes at most four UTF-8 bytes
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c < 0x80 && (char.IsAsciiLetterOrDigit(c) || Unreserved.Contains(c, StringComparison.Ordinal)))
            {
                sb.Append(c);
                continue;
            }

            int cp = c;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                cp = char.ConvertToUtf32(c, text[i + 1]);
                i++;
            }

            // Standard UTF-8 for scalar values; a lone surrogate code unit falls into the three-byte form.
            int n;
            if (cp < 0x80)
            {
                bytes[0] = (byte)cp;
                n = 1;
            }
            else if (cp < 0x800)
            {
                bytes[0] = (byte)(0xC0 | (cp >> 6));
                bytes[1] = (byte)(0x80 | (cp & 0x3F));
                n = 2;
            }
            else if (cp < 0x10000)
            {
                bytes[0] = (byte)(0xE0 | (cp >> 12));
                bytes[1] = (byte)(0x80 | ((cp >> 6) & 0x3F));
                bytes[2] = (byte)(0x80 | (cp & 0x3F));
                n = 3;
            }
            else
            {
                bytes[0] = (byte)(0xF0 | (cp >> 18));
                bytes[1] = (byte)(0x80 | ((cp >> 12) & 0x3F));
                bytes[2] = (byte)(0x80 | ((cp >> 6) & 0x3F));
                bytes[3] = (byte)(0x80 | (cp & 0x3F));
                n = 4;
            }

            for (int k = 0; k < n; k++)
            {
                sb.Append('%').Append(hex[bytes[k] >> 4]).Append(hex[bytes[k] & 15]);
            }
        }
    }

    /// <summary>Decodes one line body. Returns false when an escape is malformed or the bytes are not UTF-8.</summary>
    public static bool TryDecode(ReadOnlySpan<char> s, out string text)
    {
        text = string.Empty;
        if (s.IndexOf('%') < 0)
        {
            text = s.ToString();
            return true;
        }

        var sb = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            if (s[i] != '%')
            {
                sb.Append(s[i++]);
                continue;
            }

            if (!TryByte(s, i, out int b0))
            {
                return false;
            }

            i += 3;
            if (b0 < 0x80)
            {
                sb.Append((char)b0);
                continue;
            }

            int extra;
            int cp;
            if (b0 is >= 0xC2 and <= 0xDF)
            {
                extra = 1;
                cp = b0 & 0x1F;
            }
            else if (b0 is >= 0xE0 and <= 0xEF)
            {
                extra = 2;
                cp = b0 & 0x0F;
            }
            else if (b0 is >= 0xF0 and <= 0xF4)
            {
                extra = 3;
                cp = b0 & 0x07;
            }
            else
            {
                return false;
            }

            for (int k = 0; k < extra; k++)
            {
                if (i >= s.Length || s[i] != '%' || !TryByte(s, i, out int bk) || (bk & 0xC0) != 0x80)
                {
                    return false;
                }

                cp = (cp << 6) | (bk & 0x3F);
                i += 3;
            }

            int min = extra == 1 ? 0x80 : extra == 2 ? 0x800 : 0x10000;
            if (cp < min || cp > 0x10FFFF)
            {
                return false;
            }

            if (cp >= 0x10000)
            {
                sb.Append(char.ConvertFromUtf32(cp));
            }
            else
            {
                sb.Append((char)cp); // includes lone surrogates written in the three-byte form
            }
        }

        text = sb.ToString();
        return true;
    }

    private static bool TryByte(ReadOnlySpan<char> s, int at, out int value)
    {
        value = 0;
        if (at + 2 >= s.Length)
        {
            return false;
        }

        int hi = HexValue(s[at + 1]);
        int lo = HexValue(s[at + 2]);
        if (hi < 0 || lo < 0)
        {
            return false;
        }

        value = (hi << 4) | lo;
        return true;
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
