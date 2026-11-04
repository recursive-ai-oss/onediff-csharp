using System.Text;
using OneDiff.Documents.Html;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;

namespace OneDiff.Documents.Tests.Html;

/// <summary>
/// Reads a written page back and checks it against the comparison it came from: every block's text, with inserted and deleted
/// text taken out in turn, reproduces the new and the old document; every change appears as exactly the elements its edits
/// call for, with exactly its text; every attribute edit and move appears once; hidden content is absent; nothing in the page
/// can run code or fetch anything.
/// </summary>
internal static class HtmlGate
{
    public static List<string> Check(Comparison c, string html, bool sideBySide)
    {
        var errors = new List<string>();
        void Fail(string m)
        {
            if (errors.Count < 20)
            {
                errors.Add(m);
            }
        }

        El root;
        try
        {
            root = HtmlDom.Parse(html);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return [$"page does not parse: {ex.Message}"];
        }

        Safety(html, root, Fail);
        List<El> all = [.. root.Descendants()];
        El body = all.First(e => e.Tag == "body");
        List<El> blocks = [.. body.Descendants().Where(e => e.Has("blk"))];

        CompareDocument oldDoc = c.Old, newDoc = c.New;
        CompareSettings settings = c.Settings;
        LedgerCategory?[] hiddenOld = HiddenNodes.Compute(oldDoc, settings), hiddenNew = HiddenNodes.Compute(newDoc, settings);

        // Blocks by side.
        El? Own(El blk) => Children(blk).FirstOrDefault(e => e.Tag == "p" && e.Has("t"));
        static IEnumerable<El> Children(El blk) =>
            blk.Elements.SelectMany(k => k.Tag is "caption" || k.Tag == "td" && k.Has("self") ? k.Elements : [k]);

        string NewText(El b) => Own(b) is { } t ? HtmlDom.TextOf(t, true, false) : "";
        string OldText(El b) => Own(b) is { } t ? HtmlDom.TextOf(t, false, true) : "";

        var liveByNew = new Dictionary<int, El>();
        var oldByNode = new Dictionary<int, El>();
        foreach (El b in blocks)
        {
            if (b.Attr("data-new") is { } dn)
            {
                int n = int.Parse(dn, System.Globalization.CultureInfo.InvariantCulture);
                if (!liveByNew.TryAdd(n, b))
                {
                    Fail($"new node {n} has two blocks");
                }
            }

            if (b.Attr("data-old") is { } dold && (b.Has("ghost") || sideBySide && InLeftCell(b)))
            {
                int o = int.Parse(dold, System.Globalization.CultureInfo.InvariantCulture);
                if (!oldByNode.TryAdd(o, b))
                {
                    Fail($"old node {o} has two blocks");
                }
            }
        }

        // New text of every visible new node.
        foreach (Node n in newDoc.Nodes)
        {
            if (hiddenNew[n.Id] is not null)
            {
                if (liveByNew.ContainsKey(n.Id))
                {
                    Fail($"hidden new node {n.Id} is shown");
                }

                continue;
            }

            string expected = ProjectedGroup.TextOf(newDoc, n, settings);
            if (liveByNew.TryGetValue(n.Id, out El? b))
            {
                string got = NewText(b);
                if (got != expected)
                {
                    Fail($"new node {n.Id}: shown '{Clip(got)}' expected '{Clip(expected)}'");
                }
            }
            else if (!sideBySide || expected.Length > 0)
            {
                Fail($"new node {n.Id} has no block");
            }
        }

        // Old text of every visible old node.
        foreach (Node o in oldDoc.Nodes)
        {
            if (hiddenOld[o.Id] is not null)
            {
                continue;
            }

            string expected = ProjectedGroup.TextOf(oldDoc, o, settings);
            AlignmentLink? link = c.Alignment.LinkOfOld(o.Id);
            if (link is not null && link.NewNodes.Any(nn => hiddenNew[nn] is not null))
            {
                // Paired with a node the settings hide: the pair is a ledger entry, not a shown change.
                continue;
            }

            if (sideBySide)
            {
                if (oldByNode.TryGetValue(o.Id, out El? lb))
                {
                    string got = OldText(lb);
                    if (got != expected)
                    {
                        Fail($"old node {o.Id} (left): shown '{Clip(got)}' expected '{Clip(expected)}'");
                    }
                }
                else if (expected.Length > 0)
                {
                    Fail($"old node {o.Id} has no left block");
                }

                continue;
            }

            if (link is null || (link.Kind == LinkKind.Move && link.OldNodes.Length == 1))
            {
                if (!oldByNode.TryGetValue(o.Id, out El? ghost))
                {
                    Fail($"old node {o.Id} has no ghost block");
                }
                else if (OldText(ghost) != expected)
                {
                    Fail($"old node {o.Id} (ghost): shown '{Clip(OldText(ghost))}' expected '{Clip(expected)}'");
                }
            }

            if (link is null)
            {
                continue;
            }

            // The old text of a link, rebuilt from its new blocks.
            string oldJoined = ProjectedGroup.Create(oldDoc, link.OldNodes, settings).Text;
            var parts = new List<string>();
            foreach (int n in link.NewNodes)
            {
                if (liveByNew.TryGetValue(n, out El? b))
                {
                    parts.Add(OldText(b));
                }
            }

            string rebuilt = link.Kind == LinkKind.Split ? string.Concat(parts) : string.Join('\u001E', parts);
            if (o.Id == link.OldNodes[0] && rebuilt != oldJoined)
            {
                Fail($"link {link.Id} ({link.Kind}, old [{string.Join(',', link.OldNodes)}] new [{string.Join(',', link.NewNodes)}], parts {parts.Count}): old text rebuilt '{Clip(rebuilt)}' expected '{Clip(oldJoined)}'");
            }
        }

        // Ghosts must belong to visible old nodes with no partner, or the old place of a move.
        foreach (int o in oldByNode.Keys.Where(o => !sideBySide))
        {
            AlignmentLink? link = c.Alignment.LinkOfOld(o);
            if (hiddenOld[o] is not null || (link is not null && link.Kind != LinkKind.Move))
            {
                Fail($"old node {o} is shown as a ghost but is neither deleted nor moved");
            }
        }

        Changes(c, all, sideBySide, Fail);
        return errors;
    }

    private static bool InLeftCell(El b)
    {
        for (El? e = b.Parent; e is not null; e = e.Parent)
        {
            if (e.Tag == "td")
            {
                return e.Parent!.Elements.ToList().IndexOf(e) == 0;
            }
        }

        return false;
    }

    private static string Clip(string s) => s.Length > 60 ? s[..60] + "…" : s;

    private static void Changes(Comparison c, List<El> all, bool sideBySide, Action<string> fail)
    {
        var known = c.Changes.Select(x => x.Id).ToHashSet();
        var byChange = new Dictionary<int, List<El>>();
        foreach (El e in all)
        {
            if (e.Attr("data-change") is { } id)
            {
                int n = int.Parse(id, System.Globalization.CultureInfo.InvariantCulture);
                if (!known.Contains(n))
                {
                    fail($"element refers to unknown change {n}");
                }

                if (!byChange.TryGetValue(n, out List<El>? list))
                {
                    list = [];
                    byChange[n] = list;
                }

                list.Add(e);
            }
        }

        foreach (Change ch in c.Changes)
        {
            List<El> els = byChange.GetValueOrDefault(ch.Id) ?? [];
            List<El> ins = [.. els.Where(e => e.Tag == "ins")], del = [.. els.Where(e => e.Tag == "del")];
            List<El> fmt = [.. els.Where(e => e.Tag == "span" && e.Has("fmt"))];
            List<El> ae = [.. els.Where(e => e.Has("ae"))];
            List<El> from = [.. els.Where(e => e.Attr("data-role") == "from")], to = [.. els.Where(e => e.Attr("data-role") == "to")];
            string InsText() => string.Concat(ins.Select(e => HtmlDom.TextOf(e, true, true)));
            string DelText() => string.Concat(del.Select(e => HtmlDom.TextOf(e, true, true)));

            string where = $"change {ch.Id} ({ch.Kind})";
            if (els.Count != ins.Count + del.Count + fmt.Count + ae.Count + from.Count + to.Count)
            {
                fail(where + ": an element of an unexpected kind carries the change");
            }

            if (ch.LinkId < 0)
            {
                if (ch.Kind == ChangeKind.Insert)
                {
                    if (ins.Count != 1 || del.Count + fmt.Count + ae.Count != 0 || InsText() != ch.New.Text)
                    {
                        fail($"{where}: expected one ins with '{Clip(ch.New.Text)}', found {ins.Count} ('{Clip(InsText())}')");
                    }
                }
                else if (ch.Kind == ChangeKind.Delete)
                {
                    if (del.Count != 1 || ins.Count + fmt.Count + ae.Count != 0 || DelText() != ch.Old.Text)
                    {
                        fail($"{where}: expected one del with '{Clip(ch.Old.Text)}', found {del.Count} ('{Clip(DelText())}')");
                    }
                }

                continue;
            }

            if (ch.Kind == ChangeKind.Move)
            {
                if (from.Count != 1 || to.Count != 1 || els.Count != 2)
                {
                    fail($"{where}: expected one from and one to, found {from.Count} and {to.Count}");
                }
                else if (HtmlDom.TextOf(from[0], true, true) != ch.Old.Text)
                {
                    fail($"{where}: old place shows '{Clip(HtmlDom.TextOf(from[0], true, true))}' expected '{Clip(ch.Old.Text)}'");
                }

                continue;
            }

            if (ch.Kind == ChangeKind.Attribute || !ch.AttributeEdits.IsDefaultOrEmpty)
            {
                if (ae.Count != ch.AttributeEdits.Length)
                {
                    fail($"{where}: {ae.Count} attribute notes for {ch.AttributeEdits.Length} edits");
                }
                else
                {
                    for (int i = 0; i < ae.Count; i++)
                    {
                        AttributeEdit e = ch.AttributeEdits[i];
                        if (ae[i].Attr("data-key") != HtmlOut.SanitizeAttribute(e.Key)
                            || ae[i].Attr("data-old") != (e.OldValue is null ? null : HtmlOut.SanitizeAttribute(e.OldValue))
                            || ae[i].Attr("data-new") != (e.NewValue is null ? null : HtmlOut.SanitizeAttribute(e.NewValue)))
                        {
                            fail($"{where}: attribute note {i} does not match its edit");
                        }
                    }
                }
            }

            if (ch.Edits.IsDefaultOrEmpty)
            {
                if (ins.Count + del.Count + fmt.Count != 0)
                {
                    fail($"{where}: has text elements but no edits");
                }

                continue;
            }

            string expIns = string.Concat(ch.Edits.Where(e => !e.IsFormatOnly).Select(e => e.NewText));
            string expDel = string.Concat(ch.Edits.Where(e => !e.IsFormatOnly).Select(e => e.OldText));
            string expFmt = string.Concat(ch.Edits.Where(e => e.IsFormatOnly).Select(e => e.NewText));
            // Inline pages carry node separators inside deleted text; the insertion side splits blocks at them.
            string gotIns = InsText(), gotDel = DelText();
            string strippedExpIns = expIns.Replace("\u001E", "", StringComparison.Ordinal);
            string expDelCmp = sideBySide ? expDel.Replace("\u001E", "", StringComparison.Ordinal) : expDel;
            if (gotIns != strippedExpIns)
            {
                fail($"{where}: inserted text '{Clip(gotIns)}' expected '{Clip(strippedExpIns)}'");
            }

            if (gotDel != expDelCmp)
            {
                fail($"{where}: deleted text '{Clip(gotDel)}' expected '{Clip(expDelCmp)}'");
            }

            string gotFmt = string.Concat(fmt.Select(e => HtmlDom.TextOf(e, true, true)));
            // A formatting change shows on both sides of a side-by-side page.
            if (sideBySide)
            {
                expFmt += expFmt;
            }

            if (gotFmt != expFmt)
            {
                fail($"{where}: formatted text '{Clip(gotFmt)}' expected '{Clip(expFmt)}'");
            }

            bool anyIns = ch.Edits.Any(e => !e.IsFormatOnly && e.NewLength > 0), anyDel = ch.Edits.Any(e => !e.IsFormatOnly && e.OldLength > 0);
            if ((ins.Count > 0) != anyIns || (del.Count > 0) != anyDel || (fmt.Count > 0) != ch.Edits.Any(e => e.IsFormatOnly))
            {
                fail($"{where}: elements present ({ins.Count} ins, {del.Count} del, {fmt.Count} fmt) do not match its edits");
            }
        }
    }

    private static void Safety(string html, El root, Action<string> fail)
    {
        foreach (El e in root.Descendants())
        {
            if (e.Tag is "script" or "iframe" or "object" or "embed" or "link" or "form" or "img" or "base")
            {
                fail($"<{e.Tag}> in page");
            }

            foreach ((string name, string value) in e.Attrs)
            {
                if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase) || name is "src" or "srcset" or "action" or "style")
                {
                    fail($"attribute {name} on <{e.Tag}>");
                }

                if (name == "href" && !value.StartsWith('#'))
                {
                    fail($"href to {value}");
                }
            }
        }

        string css = root.Descendants().Where(e => e.Tag == "style").SelectMany(e => e.Kids.OfType<string>()).FirstOrDefault() ?? "";
        if (css.Contains("url(", StringComparison.OrdinalIgnoreCase) || css.Contains("@import", StringComparison.OrdinalIgnoreCase) || css.Contains("expression(", StringComparison.OrdinalIgnoreCase))
        {
            fail("style sheet fetches or evaluates something");
        }

        if (html.Contains("http://", StringComparison.Ordinal) || html.Contains("https://", StringComparison.Ordinal))
        {
            // Only text from the documents themselves may contain a URL, and then only as escaped text.
            foreach (El e in root.Descendants())
            {
                foreach (string v in e.Attrs.Values)
                {
                    if (v.Contains("://", StringComparison.Ordinal) && e.Tag is not ("span" or "ae"))
                    {
                        fail($"URL in attribute of <{e.Tag}>");
                    }
                }
            }
        }

        if (!root.Descendants().Any(e => e.Tag == "html" && e.Attr("lang") is { Length: > 0 }))
        {
            fail("no lang on <html>");
        }

        if (root.Descendants().Count(e => e.Tag == "h1") != 1)
        {
            fail("expected exactly one h1");
        }

        _ = Encoding.UTF8;
    }
}
