using System.Collections.Immutable;
using OneDiff.Documents;
using OneDiff.Documents.Model;
using OneDiff.Word;
using RecursiveAI.OneOffice;
using RecursiveAI.OneOffice.Documents;
using RecursiveAI.OneOffice.IO;

namespace OneDiff.Word.Tests;

/// <summary>Resolves every revision of a tracked-changes output and compares the result with the document it must give back.</summary>
internal static class TrackedGate
{
    public static byte[] Resolve(byte[] docx, bool accept)
    {
        using Document doc = Document.Open(ByteSource.FromMemory(docx));
        if (accept)
        {
            doc.Revisions.AcceptAll();
        }
        else
        {
            doc.Revisions.RejectAll();
        }

        // OneOffice leaves the wrapper of a resolved hyperlink or smart tag behind when all its runs went with a revision, and a paragraph
        // that is joined to the next one carries the empty wrapper along. Word drops it; it holds nothing, so it is dropped here too.
        // (The same applies to nothing else: an empty paragraph, run or content control is still compared.)
        foreach (var e in OneDiff.Word.Internal.Wx.Descendants(doc.MainMarkup.Root))
        {
            if (OneDiff.Word.Internal.Wx.InMain(e) && (OneDiff.Word.Internal.Wx.Is(e, "hyperlink") || OneDiff.Word.Internal.Wx.Is(e, "smartTag")) && !OneDiff.Word.Internal.Wx.ChildElements(e).Any())
            {
                e.Remove();
            }
        }

        using var ms = new MemoryStream();
        doc.SaveAs(ms, OfficeFormat.Docx);
        return ms.ToArray();
    }

    /// <summary>Kinds of difference a tracked change cannot carry (see the writer notes); they are left out of the reject-all comparison.</summary>
    private static bool NotCarriedKind(NodeKind k) => k is NodeKind.Property or NodeKind.Comment or NodeKind.Header or NodeKind.Footer or NodeKind.Section;

    private static bool NotCarriedNode(CompareDocument d, int n) => NotCarriedKind(d[n].Kind) || (d[n].Kind == NodeKind.ContentControl && d[n].Parent >= 0 && d[d[n].Parent].Kind is not (NodeKind.Paragraph or NodeKind.ListItem));

    public static List<string> Problems(CompareDocument oldDoc, CompareDocument newDoc, byte[] output, CompareSettings settings, WordBuildOn buildOn, ImmutableArray<WordNotCarried> notCarried = default, bool detail = false, List<string>? filtered = null)
    {
        var problems = new List<string>();
        var source = new WordDocumentSource();
        foreach (bool accept in new[] { true, false })
        {
            // An emptied paragraph whose mark cannot go (container end, before a table, section break) is left by accept or reject-all;
            // the writer lists each such paragraph, and only as many empty-paragraph differences as it listed are let through.
            string category = accept ? "paragraph-mark-accept" : "paragraph-mark-reject";
            int allowance = notCarried.IsDefault ? 0 : notCarried.Count(x => x.Category == category);
            bool leftovers = allowance > 0;
            byte[] resolved = Resolve(output, accept);
            CompareDocument got = source.Read(DocumentInput.FromBytes(resolved, accept ? "accepted.docx" : "rejected.docx"));
            CompareDocument want = accept ? newDoc : oldDoc;
            Comparison c = Comparison.Of(got, want, settings);
            bool sectionsDiffer = got.Nodes.Count(n => n.Kind == NodeKind.Section) != want.Nodes.Count(n => n.Kind == NodeKind.Section)
                || (!notCarried.IsDefault && notCarried.Any(x => x.Category == "section" || x.Message.Contains("section break", StringComparison.Ordinal)));
            // A paragraph that sits in another section on one side (the break between them is not carried) shows as a delete and an insert
            // of the same text; such a pair is let through, and a delete or insert without its partner is still a problem.
            // Likewise a block content control that exists on one side only (its wrapper cannot be tracked) shifts the blocks around it.
            bool blockControlSkew = !notCarried.IsDefault && notCarried.Any(x => x.Category == "content-control" && x.Message.StartsWith("a block content control", StringComparison.Ordinal));
            bool commentsDiffer = !notCarried.IsDefault && notCarried.Any(x => x.Category == "comments");
            var cross = new List<(string What, string Text, bool IsInsert)>();
            foreach (Change ch in c.Changes)
            {
                bool carried = true;
                foreach (int n in ch.Old.Nodes)
                {
                    carried &= !NotCarriedNode(got, n) && !Under(got, n);
                }

                foreach (int n in ch.New.Nodes)
                {
                    carried &= !NotCarriedNode(want, n) && !Under(want, n);
                }

                // A block content control cannot be added or removed as a tracked change, so blocks inside one show as moved.
                // Likewise a section break: blocks of a section that exists on one side only show as moved.
                if (ch.Kind == ChangeKind.Move && ch.Edits.Length == 0 && (sectionsDiffer || blockControlSkew || ch.Old.Nodes.Any(n => InBlockControl(got, n)) || ch.New.Nodes.Any(n => InBlockControl(want, n))))
                {
                    carried = false;
                }

                // An emptied list item left behind (above) takes a number, so the items after it are numbered one higher.
                if (leftovers && ch.Kind == ChangeKind.Attribute && ch.AttributeEdits.Length > 0 && ch.AttributeEdits.All(a => a.Key == "list.label"))
                {
                    filtered?.Add((accept ? "accept" : "reject") + ": list numbering shifted by a left-over empty item");
                    continue;
                }

                if (!carried)
                {
                    continue;
                }

                if (allowance > 0 && ch.Kind is ChangeKind.Insert or ChangeKind.Delete && EmptyParagraphs(got, want, ch))
                {
                    allowance--;
                    filtered?.Add((accept ? "accept" : "reject") + ": " + ch.Kind + " of an empty paragraph");
                    continue;
                }

                string what = $"{(accept ? "accept" : "reject")}: {ch.Kind} old[{string.Join(",", ch.Old.Nodes.Select(i => got[i].Kind))}]='{Clip(ch.Old.Text)}' new[{string.Join(",", ch.New.Nodes.Select(i => want[i].Kind))}]='{Clip(ch.New.Text)}'";
                if (detail)
                {
                    what += " attrs=" + string.Join(";", ch.AttributeEdits.Select(a => $"{a.Key}:{a.OldValue}->{a.NewValue}"));
                    what += " edits=" + string.Join(";", ch.Edits.Select(e => $"[{e.OldStart}+{e.OldLength}->{e.NewStart}+{e.NewLength} '{Clip(e.OldText)}'->'{Clip(e.NewText)}' oldruns={string.Join(",", e.OldRuns.Select(r => r.Start + "+" + r.Length + ":" + r.Format))} newruns={string.Join(",", e.NewRuns.Select(r => r.Start + "+" + r.Length + ":" + r.Format))}]"));
                }

                // A comment that exists on one side only (not carried) changes what its paragraph holds, so a text box beside it is
                // paired differently: it shows as a delete and an insert, and is let through only as such a pair (same paragraph text).
                if (commentsDiffer && ch.Kind is ChangeKind.Insert or ChangeKind.Delete && ch.Edits.Length == 0)
                {
                    bool ins = ch.Kind == ChangeKind.Insert;
                    CompareDocument doc = ins ? want : got;
                    var nodes = ins ? ch.New.Nodes : ch.Old.Nodes;
                    if (!nodes.IsDefaultOrEmpty && nodes.All(n => doc[n].Kind == NodeKind.TextBox && doc[n].Parent >= 0))
                    {
                        cross.Add((what, doc[doc[nodes[0]].Parent].Text, ins));
                        continue;
                    }
                }

                if (sectionsDiffer && ch.Kind is ChangeKind.Insert or ChangeKind.Delete && ch.Edits.Length == 0
                    && (ch.Kind == ChangeKind.Insert ? ch.New.Text : ch.Old.Text) is { Length: > 0 } t)
                {
                    cross.Add((what, t, ch.Kind == ChangeKind.Insert));
                    continue;
                }

                problems.Add(what);
            }

            foreach (var (what, text, isInsert) in cross)
            {
                int partner = cross.FindIndex(o => o.IsInsert != isInsert && o.Text == text);
                if (partner >= 0)
                {
                    filtered?.Add((accept ? "accept" : "reject") + ": a delete and an insert of one paragraph or text box, next to a section break or comment that is not carried");
                }
                else
                {
                    problems.Add(what);
                }
            }
        }

        return problems;
    }

    private static bool EmptyParagraphs(CompareDocument got, CompareDocument want, Change ch)
    {
        bool fromGot = !ch.Old.Nodes.IsDefaultOrEmpty;
        CompareDocument doc = fromGot ? got : want;
        ImmutableArray<int> nodes = fromGot ? ch.Old.Nodes : ch.New.Nodes;
        return !nodes.IsDefaultOrEmpty && nodes.All(n => doc[n].Kind is NodeKind.Paragraph or NodeKind.ListItem && doc[n].Text.Length == 0);
    }

    private static bool InBlockControl(CompareDocument d, int node)
    {
        for (int p = d[node].Parent; p >= 0; p = d[p].Parent)
        {
            if (NotCarriedNode(d, p) && d[p].Kind == NodeKind.ContentControl)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Under(CompareDocument d, int node)
    {
        for (int p = d[node].Parent; p >= 0; p = d[p].Parent)
        {
            if (NotCarriedKind(d[p].Kind))
            {
                return d[p].Kind != NodeKind.Section;
            }
        }

        return false;
    }

    private static string Clip(string s) => s.Length > 60 ? s[..60] + "…" : s;
}
