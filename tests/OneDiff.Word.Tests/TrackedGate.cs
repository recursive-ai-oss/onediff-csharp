using System.Collections.Immutable;
using OneDiff.Documents;
using OneDiff.Documents.Model;
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

        using var ms = new MemoryStream();
        doc.SaveAs(ms, OfficeFormat.Docx);
        return ms.ToArray();
    }

    /// <summary>Kinds of difference a tracked change cannot carry (see the writer notes); they are left out of the reject-all comparison.</summary>
    private static bool NotCarriedKind(NodeKind k) => k is NodeKind.Property or NodeKind.Comment or NodeKind.Header or NodeKind.Footer or NodeKind.Section;

    private static bool NotCarriedNode(CompareDocument d, int n) => NotCarriedKind(d[n].Kind) || (d[n].Kind == NodeKind.ContentControl && d[n].Parent >= 0 && d[d[n].Parent].Kind is not (NodeKind.Paragraph or NodeKind.ListItem));

    public static List<string> Problems(CompareDocument oldDoc, CompareDocument newDoc, byte[] output, CompareSettings settings, WordBuildOn buildOn, bool detail = false)
    {
        var problems = new List<string>();
        var source = new WordDocumentSource();
        foreach (bool accept in new[] { true, false })
        {
            byte[] resolved = Resolve(output, accept);
            CompareDocument got = source.Read(DocumentInput.FromBytes(resolved, accept ? "accepted.docx" : "rejected.docx"));
            CompareDocument want = accept ? newDoc : oldDoc;
            Comparison c = Comparison.Of(got, want, settings);
            bool sectionsDiffer = got.Nodes.Count(n => n.Kind == NodeKind.Section) != want.Nodes.Count(n => n.Kind == NodeKind.Section);
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
                if (ch.Kind == ChangeKind.Move && ch.Edits.Length == 0 && (sectionsDiffer || ch.Old.Nodes.Any(n => InBlockControl(got, n)) || ch.New.Nodes.Any(n => InBlockControl(want, n))))
                {
                    carried = false;
                }

                if (!carried)
                {
                    continue;
                }

                string what = $"{(accept ? "accept" : "reject")}: {ch.Kind} old[{string.Join(",", ch.Old.Nodes.Select(i => got[i].Kind))}]='{Clip(ch.Old.Text)}' new[{string.Join(",", ch.New.Nodes.Select(i => want[i].Kind))}]='{Clip(ch.New.Text)}'";
                if (detail)
                {
                    what += " attrs=" + string.Join(";", ch.AttributeEdits.Select(a => $"{a.Key}:{a.OldValue}->{a.NewValue}"));
                    what += " edits=" + string.Join(";", ch.Edits.Select(e => $"[{e.OldStart}+{e.OldLength}->{e.NewStart}+{e.NewLength} '{Clip(e.OldText)}'->'{Clip(e.NewText)}' oldruns={string.Join(",", e.OldRuns.Select(r => r.Start + "+" + r.Length + ":" + r.Format))} newruns={string.Join(",", e.NewRuns.Select(r => r.Start + "+" + r.Length + ":" + r.Format))}]"));
                }

                problems.Add(what);
            }
        }

        return problems;
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
