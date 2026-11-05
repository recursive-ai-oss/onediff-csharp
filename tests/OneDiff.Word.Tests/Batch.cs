using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using OneDiff.Documents;
using OneDiff.Documents.Model;

namespace OneDiff.Word.Tests;

public class Batch
{
    static readonly (string Name, string Pattern)[] Kinds =
    [
        ("ins", "<w:ins "), ("del", "<w:del "), ("moveFrom", "<w:moveFrom "), ("moveTo", "<w:moveTo "),
        ("rPrChange", "<w:rPrChange "), ("pPrChange", "<w:pPrChange "), ("tblPrChange", "<w:tblPrChange "), ("trPrChange", "<w:trPrChange "),
        ("tcPrChange", "<w:tcPrChange "), ("sectPrChange", "<w:sectPrChange "), ("cellIns", "<w:cellIns "), ("cellDel", "<w:cellDel "),
        ("fieldChange", "<w:instrText"), ("delInstrText", "<w:delInstrText"), ("footnote", "<w:footnoteReference"), ("endnote", "<w:endnoteReference"),
        ("textbox", "<w:txbxContent"), ("contentControl", "<w:sdt>"), ("comment", "<w:commentReference"),
    ];

    static string KindsOf(byte[] docx, out bool inHeader, out bool boxRev, out bool markRev, out bool rowRev)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        inHeader = boxRev = markRev = rowRev = false;
        using var zip = new ZipArchive(new MemoryStream(docx));
        foreach (ZipArchiveEntry e in zip.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.Ordinal) && e.FullName.StartsWith("word/", StringComparison.Ordinal)))
        {
            string x = new StreamReader(e.Open(), Encoding.UTF8).ReadToEnd();
            bool hf = e.FullName.Contains("header", StringComparison.Ordinal) || e.FullName.Contains("footer", StringComparison.Ordinal);
            foreach ((string n, string p) in Kinds)
            {
                if (x.Contains(p, StringComparison.Ordinal) && (n is not ("fieldChange" or "footnote" or "endnote" or "textbox" or "contentControl" or "comment") || e.FullName.EndsWith("document.xml", StringComparison.Ordinal)))
                {
                    found.Add(n);
                    inHeader |= hf && n is "ins" or "del";
                }
            }

            foreach (Match m in Regex.Matches(x, @"<w:txbxContent>.*?</w:txbxContent>", RegexOptions.Singleline))
            {
                if (m.Value.Contains("<w:ins ", StringComparison.Ordinal) || m.Value.Contains("<w:del ", StringComparison.Ordinal))
                {
                    boxRev = true;
                }
            }

            if (Regex.IsMatch(x, @"<w:pPr>(?:(?!</w:pPr>).)*<w:rPr>(?:(?!</w:rPr>).)*<w:(ins|del) ", RegexOptions.Singleline))
            {
                markRev = true;
            }

            if (Regex.IsMatch(x, @"<w:trPr>(?:(?!</w:trPr>).)*<w:(ins|del) ", RegexOptions.Singleline))
            {
                rowRev = true;
            }

            if (hf && (x.Contains("<w:ins ", StringComparison.Ordinal) || x.Contains("<w:del ", StringComparison.Ordinal)))
            {
                found.Add("header-footer-revision");
            }
        }

        if (boxRev)
        {
            found.Add("textbox-revision");
        }

        if (markRev)
        {
            found.Add("paragraph-mark");
        }

        if (rowRev)
        {
            found.Add("row-revision");
        }

        return string.Join(',', found);
    }

    [Fact]
    public void Make()
    {
        string? dir = Environment.GetEnvironmentVariable("ONEDIFF_BATCH_OUT");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        var source = new WordDocumentSource();
        var rows = new List<string>();
        var covered = new HashSet<string>(StringComparer.Ordinal);
        int count = 0;
        (string Mode, WordBuildOn On, string Set, CompareSettings S)[] variants =
        [
            ("new", WordBuildOn.New, "Legal", CompareSettings.Legal), ("old", WordBuildOn.Old, "Strict", CompareSettings.Strict),
            ("old", WordBuildOn.Old, "Legal", CompareSettings.Legal), ("new", WordBuildOn.New, "Strict", CompareSettings.Strict),
        ];

        void Emit(string name, string src, byte[] ob, byte[] nb, (string Mode, WordBuildOn On, string Set, CompareSettings S) v)
        {
            Comparison c = Comparison.Of(source.Read(DocumentInput.FromBytes(ob, "o.docx")), source.Read(DocumentInput.FromBytes(nb, "n.docx")), v.S);
            using var ms = new MemoryStream();
            new DocxRedline { BuildOn = v.On }.Create(c, ms);
            byte[] bytes = ms.ToArray();
            string kinds = KindsOf(bytes, out _, out _, out _, out _);
            File.WriteAllBytes(Path.Combine(dir, name), bytes);
            rows.Add($"{name}\t{src}\t{v.Mode}\t{v.Set}\t{kinds}");
            count++;
        }

        // Seeds are tried in order; a file is kept when it adds a revision kind, or (after coverage) every fifth seed to reach about 44.
        for (int seed = 0; seed < 3000 && count < 52; seed++)
        {
            var gen = new Gen(seed);
            DocSpec o = gen.Document(), n = gen.Edit(o);
            byte[] ob = DocFactory.Build(o), nb = DocFactory.Build(n);
            var v = variants[seed % 4];
            Comparison c = Comparison.Of(source.Read(DocumentInput.FromBytes(ob, "o.docx")), source.Read(DocumentInput.FromBytes(nb, "n.docx")), v.S);
            using var ms = new MemoryStream();
            new DocxRedline { BuildOn = v.On }.Create(c, ms);
            string kinds = KindsOf(ms.ToArray(), out _, out _, out _, out _);
            bool adds = kinds.Split(',', StringSplitOptions.RemoveEmptyEntries).Any(k => covered.Add(k));
            if (adds || (seed % 9 == 0 && count < 30))
            {
                Emit($"gen-{seed:D4}-{v.Mode}-{v.Set.ToLowerInvariant()}.docx", $"generated seed {seed}", ob, nb, v);
            }
        }

        // The four modes on every one of a few seeds, so each combination is seen on the same content.
        foreach (int seed in (int[])[3, 17])
        {
            var gen = new Gen(seed);
            DocSpec o = gen.Document(), n = gen.Edit(o);
            byte[] ob = DocFactory.Build(o), nb = DocFactory.Build(n);
            foreach (var v in variants)
            {
                string name = $"gen-{seed:D4}-{v.Mode}-{v.Set.ToLowerInvariant()}.docx";
                if (!File.Exists(Path.Combine(dir, name)))
                {
                    Emit(name, $"generated seed {seed}", ob, nb, v);
                }
            }
        }

        // Targeted pairs: an edited text box and edited footnote/endnote text beside plain edits.
        foreach (var v in variants.Take(2))
        {
            DocSpec Make(string box, string note) => new(
                Para.Of("Intro paragraph that stays."),
                new Para(new T("The terms are in "), new Box(box), new T(" the box.")),
                new Para(new T("Payable on demand"), new Foot(note), new Foot(note + " (law)", End: true)),
                Para.Of("Closing paragraph."));
            Emit($"targeted-box-{v.Mode}-{v.Set.ToLowerInvariant()}.docx", "targeted: text box and notes", DocFactory.Build(Make("Payment within thirty days", "A footnote about payment.")), DocFactory.Build(Make("Payment within sixty days", "A footnote about late payment.")), v);
        }

        // Large documents: about 40 pages, in each build mode and setting.
        foreach (int cseed in (int[])[7, 11])
        {
            (DocSpec o, DocSpec n) = TimingTests.Contract(40, 120, cseed);
            byte[] ob = DocFactory.Build(o), nb = DocFactory.Build(n);
            foreach (var v in variants)
            {
                Emit($"contract40-{cseed}-{v.Mode}-{v.Set.ToLowerInvariant()}.docx", $"generated contract, 40 pages, seed {cseed}", ob, nb, v);
            }
        }

        File.WriteAllLines(Path.Combine(dir, "manifest.tsv"), rows.Prepend("file\tsource\tbuild\tsettings\trevision_kinds"));
        File.WriteAllText(Path.Combine(dir, "covered.txt"), string.Join('\n', covered.OrderBy(x => x, StringComparer.Ordinal)));
    }

    /// <summary>One variant per hypothesis about what Word needs: plain controls, then one small pair per kind of content.</summary>
    [Fact]
    public void MakeProbes()
    {
        string? dir = Environment.GetEnvironmentVariable("ONEDIFF_PROBE_OUT");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        var source = new WordDocumentSource();
        var rows = new List<string>();
        byte[] png1 = DocFactory.Png(10), png2 = DocFactory.Png(200);

        static byte[] Strip(byte[] docx, params string[] patterns)
        {
            using var input = new MemoryStream(docx);
            using var zip = new ZipArchive(input, ZipArchiveMode.Read);
            using var output = new MemoryStream();
            using (var outZip = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                foreach (ZipArchiveEntry e in zip.Entries)
                {
                    byte[] data;
                    using (var es = e.Open())
                    using (var ms = new MemoryStream())
                    {
                        es.CopyTo(ms);
                        data = ms.ToArray();
                    }

                    if (e.FullName == "word/document.xml")
                    {
                        string x = Encoding.UTF8.GetString(data);
                        foreach (string pat in patterns)
                        {
                            x = Regex.Replace(x, pat, "", RegexOptions.Singleline);
                        }

                        data = Encoding.UTF8.GetBytes(x);
                    }

                    using var w = outZip.CreateEntry(e.FullName).Open();
                    w.Write(data);
                }
            }

            return output.ToArray();
        }

        void Plain(string name, string note, byte[] bytes)
        {
            File.WriteAllBytes(Path.Combine(dir, name), bytes);
            rows.Add($"{name}\t{note}\t-\t-\tnone");
        }

        void Pair(string name, string note, DocSpec o, DocSpec n, bool buildOld = false, bool strict = false)
        {
            Comparison c = Comparison.Of(source.Read(DocumentInput.FromBytes(DocFactory.Build(o), "o.docx")), source.Read(DocumentInput.FromBytes(DocFactory.Build(n), "n.docx")), strict ? CompareSettings.Strict : CompareSettings.Legal);
            using var ms = new MemoryStream();
            new DocxRedline { BuildOn = buildOld ? WordBuildOn.Old : WordBuildOn.New }.Create(c, ms);
            File.WriteAllBytes(Path.Combine(dir, name), ms.ToArray());
            rows.Add($"{name}\t{note}\t{(buildOld ? "old" : "new")}\t{(strict ? "Strict" : "Legal")}\t{KindsOf(ms.ToArray(), out _, out _, out _, out _)}");
        }

        DocSpec Pic(byte[] png, string text) => new(Para.Of("Before."), new Para(new T(text), new Img(png, "logo")), Para.Of("After."));
        DocSpec Boxed(string box) => new(Para.Of("Intro."), new Para(new T("Terms in "), new Box(box), new T(" here.")), Para.Of("End."));

        byte[] pic = DocFactory.Build(Pic(png1, "Logo here"));
        Plain("ctl-pic-minimal-plain.docx", "no revisions; picture without nvPicPr and spPr", Strip(pic, @"<pic:nvPicPr>.*?</pic:nvPicPr>", @"<pic:spPr>.*?</pic:spPr>"));
        Plain("ctl-pic-complete-plain.docx", "no revisions; picture with nvPicPr and spPr", pic);
        byte[] box = DocFactory.Build(Boxed("Payment in thirty days"));
        Plain("ctl-box-minimal-plain.docx", "no revisions; text box shape without cNvSpPr, spPr, bodyPr", Strip(box, @"<wps:cNvSpPr[^>]*/>", @"<wps:spPr>.*?</wps:spPr>", @"<wps:bodyPr/>"));
        Plain("ctl-box-complete-plain.docx", "no revisions; text box shape complete", box);

        Pair("pic-replace.docx", "picture replaced beside a text edit", Pic(png1, "Logo here"), Pic(png2, "Logo there"));
        Pair("pic-insert.docx", "paragraph with picture inserted", new(Para.Of("Before."), Para.Of("After.")), Pic(png1, "Logo"));
        Pair("pic-delete-old.docx", "paragraph with picture deleted, built on old", Pic(png1, "Logo"), new(Para.Of("Before."), Para.Of("After.")), buildOld: true);
        Pair("box-edit.docx", "text box text edited", Boxed("Payment in thirty days"), Boxed("Payment in sixty days"));
        Pair("box-edit-old-strict.docx", "text box text edited, built on old", Boxed("Payment in thirty days"), Boxed("Payment in sixty days"), buildOld: true, strict: true);
        DocSpec Notes(string a, string b) => new(Para.Of("Intro."), new Para(new T("Payable on demand"), new Foot(a), new Foot(b, End: true)), Para.Of("End."));
        Pair("notes-edit.docx", "footnote and endnote text edited", Notes("A note.", "An end note."), Notes("A late note.", "A late end note."));
        DocSpec Head(string h) => new(Para.Of("Body one."), Para.Of("Body two.")) { Header = h };
        Pair("header-edit.docx", "header text edited", Head("Draft one"), Head("Draft two"));
        DocSpec Fmt(bool b, string jc) => new(Para.Of("Intro."), new Para(new T("Bold me") { Bold = b }, new T(" tail")) { Jc = jc }, Para.Of("End."));
        Pair("format-change.docx", "run and paragraph properties changed", Fmt(false, "left"), Fmt(true, "center"));
        DocSpec Cmt(string t) => new(Para.Of("Intro."), new Para(new T(t)) { Comment = "Check this." }, Para.Of("End."));
        Pair("comment-edit.docx", "commented paragraph edited", Cmt("Pay in thirty days."), Cmt("Pay in sixty days."));
        DocSpec Fld1(string r) => new(Para.Of("Intro."), new Para(new T("See "), new Fld("REF clause1", r), new T(" and "), new SimpleFld("PAGE", r)), Para.Of("End."));
        Pair("field-result.docx", "complex and simple field results changed", Fld1("Clause 3"), Fld1("Clause 4"));
        DocSpec Sect(bool land) => new(Para.Of("One."), new Para(new T("Two.")) { SectionBreak = true, Landscape = land }, Para.Of("Three."));
        Pair("section-change.docx", "section page setup changed", Sect(false), Sect(true));
        DocSpec Table(params string[] rows) => new(Para.Of("Intro."), new Tbl(rows.Select(r => new[] { new Cell(Para.Of(r)), new Cell(Para.Of(r + " b")) }).ToArray()), Para.Of("End."));
        Pair("table-rows.docx", "table row inserted and cell edited", Table("a one", "b two"), Table("a one", "c new", "b twice"));
        Pair("move.docx", "paragraph moved", new(Para.Of("Alpha clause stays here with some words."), Para.Of("Beta clause moves to the end of the document soon."), Para.Of("Gamma clause stays too with more words.")), new(Para.Of("Alpha clause stays here with some words."), Para.Of("Gamma clause stays too with more words."), Para.Of("Beta clause moves to the end of the document soon.")));
        Pair("sdt-edit.docx", "block and inline content controls edited", new(new Sdt("blk", Para.Of("Inside one.")), new Para(new Cc("Inline a", "t1"))), new(new Sdt("blk", Para.Of("Inside two.")), new Para(new Cc("Inline b", "t1"))));

        File.WriteAllLines(Path.Combine(dir, "manifest.tsv"), rows.Prepend("file\tsource\tbuild\tsettings\trevision_kinds"));
    }
}
