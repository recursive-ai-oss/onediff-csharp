using System.Globalization;
using System.Text;

namespace OneDiff.Tokenization;

/// <summary>
/// The text core's simple word tokenizer. It is script-aware but is not Unicode UAX #29 word segmentation (that comes from the
/// document layer); the rules are small enough to state in full:
/// <list type="bullet">
/// <item>White space (and U+200B zero width space) in a run is one token.</item>
/// <item>Han, Hiragana, Bopomofo and Tangut characters are one token each, with any combining marks and variation selectors that follow them.</item>
/// <item>Letters (every other script, including Katakana, Hangul and Thai/Lao/Khmer, which have no spaces and so stay as runs), decimal digits, combining marks,
/// connector punctuation such as <c>_</c>, and joiner/format characters inside a run are one token. An apostrophe (' or U+2019) between two letters and a
/// <c>.</c> or <c>,</c> between two digits stay inside the run, so <c>don't</c> and <c>3.14</c> are single words.</item>
/// <item>Anything else (punctuation, symbols, control and format characters outside a run) is one token per grapheme cluster, so an emoji sequence or a flag is not split.</item>
/// </list>
/// </summary>
internal static class WordTokenizer
{
    private enum Class : byte
    {
        Space,
        Letter,   // starts or continues a run (letters, digits, connector punctuation)
        Mark,     // combining marks: continue a run; attach to a single-character script; start a run when alone
        Joiner,   // format characters that continue a run (ZWJ, ZWNJ, soft hyphen, bidi marks)
        Single,   // ideographs and syllables that stand alone
        Other,
    }

    // ASCII classes, built once; avoids Rune work in the common case.
    private static readonly Class[] Ascii = BuildAscii();

    public static void Tokenize(ReadOnlySpan<char> text, List<TokenRange> destination)
    {
        int i = 0;
        while (i < text.Length)
        {
            Class cls = ClassAt(text, i, out int width, out Rune rune);
            int end = i + width;
            switch (cls)
            {
                case Class.Space:
                    while (end < text.Length)
                    {
                        if (ClassAt(text, end, out int w, out _) != Class.Space)
                        {
                            break;
                        }

                        end += w;
                    }

                    break;

                case Class.Letter:
                case Class.Mark:
                    end = ExtendRun(text, end, rune);
                    break;

                case Class.Single:
                    end = SkipMarks(text, end);
                    break;

                default:
                    // Punctuation, symbols, stray joiners and controls: one grapheme cluster, so ZWJ emoji sequences stay whole.
                    end = i + Math.Max(width, StringInfo.GetNextTextElementLength(text.Slice(i)));
                    break;
            }

            destination.Add(new TokenRange(i, end - i));
            i = end;
        }
    }

    // Extends a run that already holds the rune just before `end`.
    private static int ExtendRun(ReadOnlySpan<char> text, int end, Rune last)
    {
        Rune previous = last;
        while (end < text.Length)
        {
            Class cls = ClassAt(text, end, out int width, out Rune rune);
            if (cls is Class.Letter or Class.Mark or Class.Joiner)
            {
                previous = rune;
                end += width;
                continue;
            }

            // A joining punctuation mark stays in the word only when a letter (or digit) follows it, so a trailing apostrophe or full stop is its own token.
            if (cls == Class.Other && IsJoiner(rune) && end + width < text.Length)
            {
                Class after = ClassAt(text, end + width, out int afterWidth, out Rune afterRune);
                if (after == Class.Letter && JoinsAcross(previous, rune, afterRune))
                {
                    previous = afterRune;
                    end += width + afterWidth;
                    continue;
                }
            }

            break;
        }

        return end;
    }

    private static bool IsJoiner(Rune rune) => rune.Value is '\'' or 0x2019 or '.' or ',';

    private static bool JoinsAcross(Rune before, Rune joiner, Rune after)
    {
        if (joiner.Value is '\'' or 0x2019)
        {
            return Rune.IsLetter(before) && Rune.IsLetter(after);
        }

        // '.' and ',' join digits only.
        return Rune.GetUnicodeCategory(before) == UnicodeCategory.DecimalDigitNumber
            && Rune.GetUnicodeCategory(after) == UnicodeCategory.DecimalDigitNumber;
    }

    private static int SkipMarks(ReadOnlySpan<char> text, int end)
    {
        while (end < text.Length)
        {
            Class cls = ClassAt(text, end, out int width, out _);
            if (cls != Class.Mark)
            {
                break;
            }

            end += width;
        }

        return end;
    }

    private static Class ClassAt(ReadOnlySpan<char> text, int index, out int width, out Rune rune)
    {
        char c = text[index];
        if (c < 0x80)
        {
            width = 1;
            rune = new Rune(c);
            return Ascii[c];
        }

        // A lone surrogate decodes as U+FFFD with width 1, which falls into Other and becomes its own token.
        Rune.DecodeFromUtf16(text.Slice(index), out rune, out width);
        return Classify(rune);
    }

    private static Class Classify(Rune rune)
    {
        int value = rune.Value;
        if (value == 0x200B || Rune.IsWhiteSpace(rune))
        {
            return Class.Space;
        }

        if (IsStandAloneScript(value))
        {
            return Class.Single;
        }

        switch (Rune.GetUnicodeCategory(rune))
        {
            case UnicodeCategory.UppercaseLetter:
            case UnicodeCategory.LowercaseLetter:
            case UnicodeCategory.TitlecaseLetter:
            case UnicodeCategory.ModifierLetter:
            case UnicodeCategory.OtherLetter:
            case UnicodeCategory.DecimalDigitNumber:
            case UnicodeCategory.LetterNumber:
            case UnicodeCategory.OtherNumber:
            case UnicodeCategory.ConnectorPunctuation:
                return Class.Letter;
            case UnicodeCategory.NonSpacingMark:
            case UnicodeCategory.SpacingCombiningMark:
            case UnicodeCategory.EnclosingMark:
                return Class.Mark;
            case UnicodeCategory.Format:
                // Zero width (non-)joiner, soft hyphen, word joiner and bidi marks sit inside words; BOM and tag characters do not matter enough to special-case.
                return value is 0x200C or 0x200D or 0x00AD or 0x2060 or 0x200E or 0x200F or 0x061C ? Class.Joiner : Class.Other;
            default:
                return Class.Other;
        }
    }

    // Scripts written without spaces whose characters are words in their own right (ideographs) or whose syllables are read one by one.
    private static bool IsStandAloneScript(int v) =>
        (v >= 0x4E00 && v <= 0x9FFF)           // CJK Unified Ideographs
        || (v >= 0x3400 && v <= 0x4DBF)        // Extension A
        || (v >= 0xF900 && v <= 0xFAFF)        // Compatibility Ideographs
        || (v >= 0x20000 && v <= 0x323AF)      // Extensions B and later (the unassigned gaps decode as Other anyway)
        || v == 0x3005 || v == 0x3006 || v == 0x3007 // iteration mark, closing mark, ideographic zero
        || (v >= 0x3041 && v <= 0x3096)        // Hiragana
        || (v >= 0x309D && v <= 0x309F)
        || (v >= 0x3105 && v <= 0x312F)        // Bopomofo
        || (v >= 0x31A0 && v <= 0x31BF)
        || (v >= 0x17000 && v <= 0x18AFF)      // Tangut
        || (v >= 0x1B170 && v <= 0x1B2FF);     // Nushu

    private static Class[] BuildAscii()
    {
        var table = new Class[128];
        for (int c = 0; c < 128; c++)
        {
            table[c] = c switch
            {
                ' ' or '\t' or '\n' or '\v' or '\f' or '\r' => Class.Space,
                >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_' => Class.Letter,
                _ => Class.Other,
            };
        }

        return table;
    }
}
