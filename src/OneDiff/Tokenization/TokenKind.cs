namespace OneDiff.Tokenization;

/// <summary>How a string is cut into the items that get compared.</summary>
public enum TokenKind
{
    /// <summary>Lines. Each token includes its terminator (<c>\n</c>, <c>\r\n</c> or <c>\r</c>); the last line has none when the text does not end with one.</summary>
    Line = 0,

    /// <summary>
    /// Words, cut by script-aware rules that are simpler than Unicode word segmentation: a run of letters, digits, combining marks and
    /// connectors (and an apostrophe between letters, or a point or comma between digits) is one token; a run of white space is one token;
    /// each Han, Hiragana or Bopomofo character is a token of its own; Hangul, Katakana, Thai and Lao stay as runs; any other symbol is one
    /// grapheme cluster per token.
    /// </summary>
    Word = 1,

    /// <summary>Characters: one Unicode scalar value each, so a surrogate pair stays together. A lone surrogate is its own token.</summary>
    Character = 2,

    /// <summary>Grapheme clusters (user-perceived characters, as the .NET text-element rules define them).</summary>
    Grapheme = 3,
}
