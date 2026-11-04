using System.Globalization;
using System.Text;
using OneDiff.Documents.Ledger;

namespace OneDiff.Documents.Html;

/// <summary>
/// The page around the redline: the head with its style sheet (all of it derived from the <see cref="RedlineStyle"/>), the cover,
/// the legend, the ledger of differences that are not shown, notes and the footer. Everything is inline; the page loads nothing.
/// </summary>
internal static class HtmlPage
{
    public const string Ink = "#1a1a1a";

    public static void Begin(HtmlOut o, Comparison c, RedlineStyle style, bool sideBySide)
    {
        string title = TitleOf(c, style);
        o.Raw("<!DOCTYPE html>\n<html");
        o.Attr("lang", LanguageOf(style));
        o.Raw(">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<meta name=\"generator\"");
        o.Attr("content", "OneDiff " + Comparison.LibraryVersion);
        o.Raw(">\n<title>");
        o.Text(title);
        o.Raw("</title>\n<style>\n");
        o.Raw(Css(style, sideBySide));
        o.Raw("</style>\n</head>\n<body class=\"");
        o.Raw(sideBySide ? "sbs" : "inline");
        o.Raw("\">\n");
        Header(o, c, style, title);
    }

    public static void End(HtmlOut o, Comparison c, RedlineStyle style)
    {
        Ledger(o, c);
        Notes(o, c);
        if (style.Footer)
        {
            Footer(o, c, style);
        }

        o.Raw("</body>\n</html>\n");
    }

    private static string LanguageOf(RedlineStyle style)
    {
        // A language tag is letters, digits and hyphens; anything else falls back so the attribute stays well formed.
        string l = style.Language ?? "";
        if (l.Length == 0 || l.Length > 35 || !l.All(ch => ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-'))
        {
            return "en";
        }

        return l;
    }

    private static string TitleOf(Comparison c, RedlineStyle style)
    {
        if (!string.IsNullOrEmpty(style.Title))
        {
            return style.Title;
        }

        string a = c.Provenance.OldName, b = c.Provenance.NewName;
        return a.Length > 0 && b.Length > 0 ? "Comparison of " + a + " and " + b : "Comparison";
    }

    private static void Header(HtmlOut o, Comparison c, RedlineStyle style, string title)
    {
        o.Raw(style.CoverPage ? "<header class=\"cover\">\n<h1>" : "<header class=\"top\">\n<h1>");
        o.Text(title);
        o.Raw("</h1>\n");
        if (style.CoverPage)
        {
            o.Raw("<dl class=\"meta\">\n");
            Def(o, "Original", NameOrDefault(c.Provenance.OldName, "(unnamed)"));
            Def(o, "Revised", NameOrDefault(c.Provenance.NewName, "(unnamed)"));
            Def(o, "Settings", c.Settings.Name);
            string hidden = Capitalize(string.Join(", ", c.Settings.Hidden.ToArray().Select(x => Humanize(LedgerCategoryNames.NameOf(x)))));
            Def(o, "Hidden into the list below", hidden.Length == 0 ? "nothing" : hidden);
            Def(o, "Completeness", Completeness(c));
            o.Raw("</dl>\n<h2>Changes by type</h2>\n");
            Counts(o, c);
        }

        Legend(o, style);
        o.Raw("</header>\n");
    }

    /// <summary>"lineBreaks" as "line breaks".</summary>
    private static string Humanize(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        foreach (char ch in name)
        {
            if (char.IsUpper(ch))
            {
                sb.Append(' ').Append(char.ToLowerInvariant(ch));
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string NameOrDefault(string name, string fallback) => name.Length == 0 ? fallback : name;

    private static string Completeness(Comparison c)
    {
        string basis = c.Verification.Passed
            ? "Checked: every difference is shown as a change or listed under \u201CDifferences not shown\u201D."
            : "Verification failed; see the notes at the end.";
        return c.Verification.FallbackLevel > 0
            ? basis + " A coarser grouping was used (level " + c.Verification.FallbackLevel.ToString(CultureInfo.InvariantCulture) + ")."
            : basis;
    }

    private static void Def(HtmlOut o, string term, string value)
    {
        o.Raw("<dt>");
        o.Text(term);
        o.Raw("</dt><dd>");
        o.Text(value);
        o.Raw("</dd>\n");
    }

    private static void Counts(HtmlOut o, Comparison c)
    {
        var counts = new int[8];
        foreach (Change ch in c.Changes)
        {
            int k = (int)ch.Kind;
            if ((uint)k < counts.Length)
            {
                counts[k]++;
            }
        }

        if (c.Changes.IsEmpty)
        {
            o.Raw("<p>No changes were found.</p>\n");
            return;
        }

        o.Raw("<table class=\"counts\">\n<thead><tr><th scope=\"col\">Type</th><th scope=\"col\">Changes</th></tr></thead>\n<tbody>\n");
        (ChangeKind Kind, string Label)[] rows =
        [
            (ChangeKind.Insert, "Insertions"), (ChangeKind.Delete, "Deletions"), (ChangeKind.Replace, "Replacements"), (ChangeKind.Move, "Moves"),
            (ChangeKind.Split, "Paragraph splits"), (ChangeKind.Merge, "Paragraph merges"), (ChangeKind.Format, "Formatting changes"), (ChangeKind.Attribute, "Attribute changes"),
        ];
        foreach ((ChangeKind kind, string label) in rows)
        {
            int n = counts[(int)kind];
            if (n > 0)
            {
                o.Raw("<tr><th scope=\"row\">");
                o.Text(label);
                o.Raw("</th><td>");
                o.Int(n);
                o.Raw("</td></tr>\n");
            }
        }

        o.Raw("<tr><th scope=\"row\">Total</th><td>");
        o.Int(c.Changes.Length);
        o.Raw("</td></tr>\n</tbody>\n</table>\n");
    }

    private static void Legend(HtmlOut o, RedlineStyle style)
    {
        o.Raw("<p class=\"legend\"><span class=\"lg-label\">Key:</span> ");
        LegendItem(o, "lg-ins", "inserted", style.Insertion);
        LegendItem(o, "lg-del", "deleted", style.Deletion);
        LegendItem(o, "lg-mf", "moved from", style.MoveFrom);
        LegendItem(o, "lg-mt", "moved to", style.MoveTo);
        LegendItem(o, "lg-fm", "formatting changed", style.Formatting);
        o.Raw("</p>\n");
    }

    private static void LegendItem(HtmlOut o, string cls, string label, MarkStyle mark)
    {
        o.Raw("<span class=\"");
        o.Raw(cls);
        o.Raw("\">");
        if (mark.Glyph.Length > 0)
        {
            o.Raw("<span aria-hidden=\"true\">");
            o.Text(mark.Glyph);
            o.Raw("</span> ");
        }

        o.Text(label);
        o.Raw("</span> ");
    }

    private static void Ledger(HtmlOut o, Comparison c)
    {
        o.Raw("<section class=\"ledger\" aria-labelledby=\"ledger-h\">\n<h2 id=\"ledger-h\">Differences not shown</h2>\n");
        ChangeLedger ledger = c.Ledger;
        if (ledger.Entries.IsEmpty)
        {
            o.Raw("<p>Nothing was hidden: every difference is shown above.</p>\n</section>\n");
            return;
        }

        o.Raw("<p>These differences exist between the documents but the settings (");
        o.Text(c.Settings.Name);
        o.Raw(") keep them out of the redline. Each is counted here.</p>\n");
        o.Raw("<table class=\"counts\">\n<thead><tr><th scope=\"col\">Category</th><th scope=\"col\">Differences</th></tr></thead>\n<tbody>\n");
        foreach (LedgerEntry e in ledger.Entries)
        {
            o.Raw("<tr><th scope=\"row\">");
            o.Text(Capitalize(Humanize(LedgerCategoryNames.NameOf(e.Category))));
            o.Raw("</th><td>");
            o.Int(e.Count);
            o.Raw("</td></tr>\n");
        }

        o.Raw("</tbody>\n</table>\n");
        const int Shown = 50;
        foreach (LedgerEntry e in ledger.Entries)
        {
            if (e.Locations.IsDefaultOrEmpty)
            {
                continue;
            }

            o.Raw("<details><summary>");
            o.Text(Capitalize(Humanize(LedgerCategoryNames.NameOf(e.Category))));
            o.Raw(": where</summary>\n<ul>\n");
            int n = 0;
            foreach (LedgerLocation l in e.Locations)
            {
                if (n++ == Shown)
                {
                    break;
                }

                o.Raw("<li>");
                o.Text(Where(l));
                o.Raw("</li>\n");
            }

            if (e.Locations.Length > Shown)
            {
                o.Raw("<li>and ");
                o.Int(e.Locations.Length - Shown);
                o.Raw(" more</li>\n");
            }

            o.Raw("</ul>\n</details>\n");
        }

        o.Raw("</section>\n");
    }

    private static string Where(LedgerLocation l)
    {
        string where = l.OldNode >= 0 && l.NewNode >= 0
            ? "old block " + l.OldNode.ToString(CultureInfo.InvariantCulture) + ", new block " + l.NewNode.ToString(CultureInfo.InvariantCulture)
            : l.OldNode >= 0 ? "old block " + l.OldNode.ToString(CultureInfo.InvariantCulture)
            : l.NewNode >= 0 ? "new block " + l.NewNode.ToString(CultureInfo.InvariantCulture)
            : "document";
        return l.Detail.Length > 0 ? where + ": " + l.Detail : where;
    }

    private static void Notes(HtmlOut o, Comparison c)
    {
        var notes = c.Diagnostics.Where(d => d.Severity != DiagnosticSeverity.Info).ToList();
        if (notes.Count == 0)
        {
            return;
        }

        o.Raw("<section class=\"notes\" aria-labelledby=\"notes-h\">\n<h2 id=\"notes-h\">Notes</h2>\n<ul>\n");
        foreach (Diagnostic d in notes)
        {
            o.Raw("<li><code>");
            o.Text(d.Code);
            o.Raw("</code> ");
            o.Text(d.Message);
            o.Raw("</li>\n");
        }

        o.Raw("</ul>\n</section>\n");
    }

    private static void Footer(HtmlOut o, Comparison c, RedlineStyle style)
    {
        o.Raw("<footer>\n<p>Compared by OneDiff ");
        o.Text(Comparison.LibraryVersion);
        o.Raw("</p>\n<dl class=\"meta\">\n");
        FooterInput(o, "Original", c.Provenance.OldName, c.Provenance.OldHash);
        FooterInput(o, "Revised", c.Provenance.NewName, c.Provenance.NewHash);
        if (style.Timestamp is { } t)
        {
            Def(o, "Compared at", t.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        }

        o.Raw("</dl>\n</footer>\n");
    }

    private static void FooterInput(HtmlOut o, string term, string name, string hash)
    {
        o.Raw("<dt>");
        o.Text(term);
        o.Raw("</dt><dd>");
        o.Text(NameOrDefault(name, "(unnamed)"));
        if (hash.Length > 0)
        {
            o.Raw(" <code>sha256:");
            o.Text(hash);
            o.Raw("</code>");
        }

        o.Raw("</dd>\n");
    }

    // ---- style sheet ----

    private static string Css(RedlineStyle s, bool sideBySide)
    {
        var b = new StringBuilder(4096);
        b.Append("*{box-sizing:border-box}\n");
        b.Append("body{margin:0 auto;max-width:").Append(sideBySide ? "none" : "56rem").Append(";padding:1.5rem 1.5rem 3rem ").Append(sideBySide ? "1.5rem" : "2.5rem")
            .Append(";font:16px/1.55 system-ui,-apple-system,\"Segoe UI\",Roboto,\"Helvetica Neue\",Arial,\"Noto Sans\",sans-serif;color:").Append(Ink).Append(";background:#fff}\n");
        b.Append("h1{font-size:1.5rem;margin:0 0 .75rem}h2{font-size:1.15rem;margin:1.75rem 0 .5rem}\n");
        b.Append("dl.meta{display:grid;grid-template-columns:max-content 1fr;gap:.2rem 1rem;margin:.5rem 0 1rem}dl.meta dt{font-weight:600}dl.meta dd{margin:0;overflow-wrap:anywhere}\n");
        b.Append("code{font:.85em ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;overflow-wrap:anywhere}\n");
        b.Append("table.counts{border-collapse:collapse;margin:.5rem 0 1rem}table.counts th,table.counts td{border:1px solid #bbb;padding:.2rem .7rem;text-align:left}table.counts td{text-align:right}\n");
        b.Append(".legend{margin:.5rem 0 1.25rem;font-size:.9rem}.lg-label{font-weight:600}\n");
        b.Append(".cover{break-after:page;page-break-after:always}\n");
        b.Append(".blk{margin:.15rem 0}.t{margin:.2rem 0;white-space:pre-wrap;overflow-wrap:anywhere}\n");
        b.Append(".lbl{float:left;min-width:3rem;padding-right:.5rem;color:#444}.k-listitem>.t{margin-left:3.5rem}\n");
        b.Append(".kl{font-size:.8rem;color:#555;text-transform:uppercase;letter-spacing:.04em}\n");
        if (s.ChangeBars)
        {
            b.Append(".bar{border-left:3px solid ").Append(Color(s.ChangeBarColor)).Append(";margin-left:-.9rem;padding-left:calc(.9rem - 3px)}\n");
        }

        Mark(b, "ins,.lg-ins", s.Insertion);
        Mark(b, "del,.lg-del", s.Deletion);
        Mark(b, ".mv.from,.lg-mf", s.MoveFrom);
        Mark(b, ".mv.to,.lg-mt", s.MoveTo);
        Mark(b, ".fmt,.lg-fm", s.Formatting);
        b.Append(".gl{font-weight:600;margin-right:.1em}.mvlink{text-decoration:none;font-weight:600}\n");
        b.Append(".sr{position:absolute;width:1px;height:1px;margin:-1px;padding:0;overflow:hidden;clip:rect(0 0 0 0);clip-path:inset(50%);white-space:nowrap;border:0}\n");
        b.Append(".pbrk,.sep{font-size:.85em}.tag{font-size:.7rem;font-weight:600;text-transform:uppercase;border:1px solid currentColor;border-radius:2px;padding:0 .25em;margin-right:.3em;white-space:nowrap}\n");
        b.Append(".ctl{font-size:.8em;border:1px solid #888;border-radius:2px;padding:0 .15em;margin:0 .1em;color:#444}\n");
        b.Append(".attrs{font-size:.85rem;color:#333;margin:.1rem 0}.ae{display:block}\n");
        b.Append("table.blk{border-collapse:collapse;margin:.4rem 0}table.blk td{border:1px solid #bbb;padding:.2rem .5rem;vertical-align:top}\n");
        b.Append("td.self{border:0}\n");
        b.Append(".ledger summary{cursor:pointer}.ledger ul{margin:.25rem 0 .75rem}\n");
        b.Append("footer{margin-top:2.5rem;border-top:1px solid #bbb;padding-top:.5rem;font-size:.85rem;color:#333}\n");
        if (sideBySide)
        {
            b.Append("table.sbs{table-layout:fixed;width:100%;border-collapse:collapse}table.sbs th,table.sbs td{border:1px solid #ccc;padding:.25rem .6rem;vertical-align:top;width:50%;text-align:left}\n");
            b.Append("table.sbs td.empty{background:#f4f4f4}table.sbs th{background:#eee}.sbs .blk{margin:0}.sbs .d1{margin-left:.75rem}.sbs .d2{margin-left:1.5rem}.sbs .d3{margin-left:2.25rem}.sbs .d4{margin-left:3rem}.sbs .d5{margin-left:3.75rem}\n");
            if (s.ChangeBars)
            {
                b.Append(".sbs .bar{margin-left:0;padding-left:.4rem}\n");
            }
        }

        b.Append("@media print{body{max-width:none;padding:0 0 0 1.5rem}a{color:inherit}.ledger details{display:block}}\n");
        return b.ToString();
    }

    private static void Mark(StringBuilder b, string selector, MarkStyle m)
    {
        string c = Color(m.Color);
        b.Append(selector).Append('{');
        switch (m.Decoration)
        {
            case MarkDecoration.Underline:
                b.Append("color:").Append(c).Append(";text-decoration:underline ").Append(c);
                break;
            case MarkDecoration.DoubleUnderline:
                b.Append("color:").Append(c).Append(";text-decoration:underline double ").Append(c);
                break;
            case MarkDecoration.StrikeThrough:
                b.Append("color:").Append(c).Append(";text-decoration:line-through ").Append(c);
                break;
            case MarkDecoration.DoubleStrikeThrough:
                b.Append("color:").Append(c).Append(";text-decoration:line-through double ").Append(c);
                break;
            case MarkDecoration.Highlight:
                b.Append("background:").Append(Tint(c)).Append(";color:").Append(Ink).Append(";text-decoration:none;box-shadow:inset 0 -2px 0 ").Append(c);
                break;
            default:
                b.Append("color:").Append(c).Append(";text-decoration:none");
                break;
        }

        b.Append("}\n");
    }

    /// <summary>A colour from the style, checked to be <c>#RRGGBB</c> (anything else would be a way into the style sheet) and lower-cased; black otherwise.</summary>
    public static string Color(string? value)
    {
        if (value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit))
        {
            return value.ToLowerInvariant();
        }

        return "#000000";
    }

    /// <summary>The colour mixed with white, for a highlight behind dark text.</summary>
    private static string Tint(string hex)
    {
        int r = int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        int g = int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        int bl = int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        static int Mix(int ch) => (ch * 22 + 255 * 78 + 50) / 100;
        return "#" + Mix(r).ToString("x2", CultureInfo.InvariantCulture) + Mix(g).ToString("x2", CultureInfo.InvariantCulture) + Mix(bl).ToString("x2", CultureInfo.InvariantCulture);
    }
}
