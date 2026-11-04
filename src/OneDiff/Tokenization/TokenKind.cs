namespace OneDiff.Tokenization;

/// <summary>How a string is cut into the items that get compared.</summary>
public enum TokenKind
{
    /// <summary>Lines. Each token includes its terminator (<c>\n</c>, <c>\r\n</c> or <c>\r</c>); the last line has none when the text does not end with one.</summary>
    Line = 0,

    /// <summary>Words: a run of letters, digits and combining marks (or underscores) is one token, a run of white space is one token, and any other symbol is a token of its own.</summary>
    Word = 1,

    /// <summary>Characters: one Unicode scalar value each, so a surrogate pair stays together. A lone surrogate is its own token.</summary>
    Character = 2,

    /// <summary>Grapheme clusters (user-perceived characters, as the .NET text-element rules define them).</summary>
    Grapheme = 3,
}
