using System.Text;
using System.Text.RegularExpressions;

namespace OneDiff.Documents.Semantics;

/// <summary>
/// The text rules the semantic passes share: cross-references, defined terms and the folding of typography. All patterns are
/// source-generated and deterministic.
/// </summary>
internal static partial class TextRules
{
    // ---- references ----

    // The words that introduce a clause number, in the languages the library is measured on. A reference is one of these (or § or
    // the Chinese/Japanese 第…条 frame) followed by a dotted number.
    private const string RefWord = @"(?:sections?|clauses?|articles?|paragraphs?|schedules?|annex(?:es)?|appendix|abschnitt(?:e|s)?|artikel|ziffer|klausel|absatz|l['’]article|articles|artículos?|cláusulas?|clausulas?|secci[oó]n|sección|البند|المادة|القسم|الفقرة|§§?)";

    [GeneratedRegex(@"^(?:" + RefWord + @"\s?)?(?<n>\d+(?:\.\d+)*)(?:\((?:[a-z]|[ivx]+)\))*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex WholeReference();

    [GeneratedRegex(@"^第(?<n>\d+(?:\.\d+)*)(?:条|條|章|節|项|項)$", RegexOptions.CultureInvariant)]
    private static partial Regex WholeCjkReference();

    [GeneratedRegex(@"^\d+(?:\.\d+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex BareNumber();

    [GeneratedRegex(@"(?:" + RefWord + @"\s?|第)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ReferenceLead();

    /// <summary>
    /// Whether the range [<paramref name="start"/>, <paramref name="start"/> + <paramref name="length"/>) of <paramref name="context"/> is
    /// a reference to a numbered clause: a keyword and a number (<c>Section 3.2</c>, <c>§ 3.2</c>, <c>第3.2条</c>), or just the number with
    /// the keyword right before it. On success <paramref name="number"/> is the number alone (<c>3.2</c>), the same in every language.
    /// </summary>
    public static bool TryReference(string context, int start, int length, out string number)
    {
        number = "";
        if (start < 0 || length <= 0 || start + length > context.Length || length > 64)
        {
            return false;
        }

        string span = context.Substring(start, length).Trim();
        if (span.Length == 0)
        {
            return false;
        }

        Match m = WholeCjkReference().Match(span);
        if (m.Success)
        {
            number = m.Groups["n"].Value;
            return true;
        }

        if (BareNumber().IsMatch(span))
        {
            int lead = Math.Max(0, start - 24);
            int spanStart = start + (context.AsSpan(start, length).Length - context.AsSpan(start, length).TrimStart().Length);
            if (ReferenceLead().IsMatch(context.AsSpan(lead, spanStart - lead)))
            {
                number = span;
                return true;
            }

            return false;
        }

        m = WholeReference().Match(span);
        if (m.Success && span.Length > m.Groups["n"].Length)
        {
            number = m.Groups["n"].Value;
            return true;
        }

        return false;
    }

    /// <summary>A list label as it is written in references: without surrounding space, brackets or closing full stop (<c>3.2.</c> is <c>3.2</c>, <c>(a)</c> is <c>a</c>).</summary>
    public static string NormalizeLabel(string label) => label.Trim().Trim('(', ')', '[', ']').TrimEnd('.', ':').Trim();

    // ---- defined terms ----

    private static readonly System.Buffers.SearchValues<char> Quotes = System.Buffers.SearchValues.Create("\"“„«‹「『");

    // A quoted phrase that the text goes on to define: followed by a defining verb ("means", "bezeichnet", 是指 …) or closed by a
    // bracket as in (the "Supplier") and (以下「X」という).
    [GeneratedRegex(
        @"(?<open>[""“„«‹「『])(?<term>[^""“”„«»‹›「」『』\r\n]{1,60}?)[""”“»›」』](?<follow>\s*(?:[)）]|(?:means|shall\s+mean|has\s+the\s+meaning|refers\s+to|is\s+defined|are\s+defined|bezeichnet|bedeutet|d[ée]signe|signifie|significa|se\s+entiende|se\s+refiere|是指|指|とは|という|といい|をいう|を指す|تعني|يعني)))",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DefinedTermPattern();

    /// <summary>
    /// The terms a text defines, in order: <c>"X" means …</c>, <c>(the "X")</c>, <c>「X」とは</c> and their equivalents in the measured languages.
    /// Terms are trimmed and unique within the text.
    /// </summary>
    public static void DefinedTerms(string text, List<string> into)
    {
        if (text.AsSpan().IndexOfAny(Quotes) < 0)
        {
            return;
        }

        foreach (Match m in DefinedTermPattern().Matches(text))
        {
            string term = m.Groups["term"].Value.Trim();
            if (term.Length > 0 && !into.Contains(term))
            {
                into.Add(term);
            }
        }
    }

    /// <summary>
    /// Whether replacing <paramref name="o"/> by <paramref name="n"/> is a renamed defined term: the old text is a term only the old
    /// document defines and the new text a term only the new document defines. A prefix or suffix both texts share (an attached
    /// preposition, a clitic) is set aside when that is what it takes; <paramref name="oldTerm"/> and <paramref name="newTerm"/> are then the
    /// terms themselves.
    /// </summary>
    public static bool TermPair(string o, string n, HashSet<string> oldTerms, HashSet<string> newTerms, out string oldTerm, out string newTerm)
    {
        oldTerm = o;
        newTerm = n;
        if (Qualifies(o, n, oldTerms, newTerms))
        {
            return true;
        }

        int p = 0;
        while (p < o.Length && p < n.Length && o[p] == n[p])
        {
            p++;
        }

        int s = 0;
        while (s < o.Length - p && s < n.Length - p && o[o.Length - 1 - s] == n[n.Length - 1 - s])
        {
            s++;
        }

        for (int pp = 0; pp <= p; pp++)
        {
            for (int ss = 0; ss <= s; ss++)
            {
                if ((pp == 0 && ss == 0) || o.Length - pp - ss <= 0 || n.Length - pp - ss <= 0)
                {
                    continue;
                }

                string a = o.Substring(pp, o.Length - pp - ss), b = n.Substring(pp, n.Length - pp - ss);
                if (Qualifies(a, b, oldTerms, newTerms))
                {
                    oldTerm = a;
                    newTerm = b;
                    return true;
                }
            }
        }

        return false;

        static bool Qualifies(string a, string b, HashSet<string> oldTerms, HashSet<string> newTerms) =>
            oldTerms.Contains(a) && !newTerms.Contains(a) && newTerms.Contains(b) && !oldTerms.Contains(b);
    }

    // ---- typography ----

    /// <summary>
    /// A text with white space removed and quotation marks and dashes replaced by plain ones, so two texts that differ only in
    /// typography fold to the same string.
    /// </summary>
    public static string FoldTypography(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c) || c == '\u00AD' || c == '\u200B')
            {
                continue;
            }

            sb.Append(c switch
            {
                '‘' or '’' or '‚' or '′' => '\'',
                '“' or '”' or '„' or '″' => '"',
                '‐' or '‑' or '‒' or '–' or '—' or '−' => '-',
                _ => c,
            });
        }

        return sb.ToString();
    }
}
