using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using OneDiff.Documents.Html;
using OneDiff.Documents.Ledger;
using OneDiff.Documents.Model;

namespace OneDiff.Documents.Combine;

/// <summary>A count under a name: a change kind, a change type, a reviewer, a step.</summary>
/// <param name="Name">The kind, type, reviewer or step name.</param>
/// <param name="Count">How many.</param>
public sealed record ReportCount(string Name, int Count);

/// <summary>One change as the report lists it.</summary>
/// <param name="Id">The change's id in the thing reported (a comparison's change, a combined change, a step's change).</param>
/// <param name="Location">Where: <c>§4.2</c> for a block under a list label, otherwise <c>¶</c> and the block's number among the paragraphs.</param>
/// <param name="Kind">The change kind's name (<c>replace</c>, <c>insert</c>, …).</param>
/// <param name="Type">The change type's name (<c>duration</c>, …; <c>none</c> for ordinary text).</param>
/// <param name="Old">The old text of the change, shortened to 200 characters.</param>
/// <param name="New">The new text of the change, shortened to 200 characters.</param>
/// <param name="Resolved">The resolved values as <c>Duration 30 d → 60 d</c>, or empty.</param>
/// <param name="Reviewers">For a combination, the names of the reviewers who made it; otherwise empty.</param>
/// <param name="ConflictId">For a combination, the conflict it is in, or -1.</param>
/// <param name="Steps">For a net chain change, the steps that touched it; for a step's change, that step; otherwise empty.</param>
public sealed record ReportItem(
    int Id,
    string Location,
    string Kind,
    string Type,
    string Old,
    string New,
    string Resolved,
    ImmutableArray<string> Reviewers,
    int ConflictId,
    ImmutableArray<int> Steps);

/// <summary>A conflict as the report lists it.</summary>
/// <param name="Id">The conflict's id in the combination.</param>
/// <param name="Kind">The conflict kind's name.</param>
/// <param name="Reviewers">The names of the reviewers involved.</param>
/// <param name="Items">The conflicting changes, each with its reviewers.</param>
public sealed record ReportConflict(int Id, string Kind, ImmutableArray<string> Reviewers, ImmutableArray<ReportItem> Items);

/// <summary>The counts and the substantive list of one comparison.</summary>
/// <param name="Title">What was compared: <c>old → new</c>, or <c>net</c>.</param>
/// <param name="Total">Every change.</param>
/// <param name="Substantive">The changes that alter what the document says.</param>
/// <param name="ByKind">Counts by change kind, in kind order, only kinds that occur.</param>
/// <param name="ByType">Counts by change type, in type order, only types that occur.</param>
/// <param name="Items">The substantive changes in comparison order.</param>
/// <param name="Hidden">The ledger: hidden differences by category name, only categories that hid something.</param>
public sealed record ReportSection(string Title, int Total, int Substantive, ImmutableArray<ReportCount> ByKind, ImmutableArray<ReportCount> ByType, ImmutableArray<ReportItem> Items, ImmutableArray<ReportCount> Hidden);

/// <summary>
/// The change report: counts by kind and type, the substantive list with resolved values, and for a combination its
/// reviewers and conflicts, for a chain the counts of every step. A plain data object that a writer renders; <see cref="ToHtml"/>
/// renders it as one self-contained page (inline style, no scripts). Deterministic: no time, no environment. Immutable.
/// </summary>
public sealed class ChangeReport
{
    private ChangeReport(string title, string mode, ImmutableArray<ReportSection> sections, ImmutableArray<ReportCount> byReviewer, ImmutableArray<ReportConflict> conflicts, ImmutableArray<ReportCount> shared)
    {
        Title = title;
        Mode = mode;
        Sections = sections;
        ByReviewer = byReviewer;
        Conflicts = conflicts;
        Shared = shared;
    }

    /// <summary>A heading for the report.</summary>
    public string Title { get; }

    /// <summary>What is reported: <c>comparison</c>, <c>combination</c> or <c>chain</c>.</summary>
    public string Mode { get; }

    /// <summary>The comparison (one section), the combination (one section over the combined changes) or the chain (one per step, then the net).</summary>
    public ImmutableArray<ReportSection> Sections { get; }

    /// <summary>For a combination, per reviewer the number of combined changes attributed to the reviewer; empty otherwise.</summary>
    public ImmutableArray<ReportCount> ByReviewer { get; }

    /// <summary>For a combination, how many combined changes were made by one, two, … reviewers (names <c>1 reviewer</c>, <c>2 reviewers</c>, …); empty otherwise.</summary>
    public ImmutableArray<ReportCount> Shared { get; }

    /// <summary>For a combination, the conflicts; empty otherwise.</summary>
    public ImmutableArray<ReportConflict> Conflicts { get; }

    /// <summary>Reports one comparison.</summary>
    public static ChangeReport From(Comparison comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        string title = comparison.Provenance.OldName + " → " + comparison.Provenance.NewName;
        return new ChangeReport(title, "comparison", [Section(title, comparison, null)], [], [], []);
    }

    /// <summary>Reports a combination: its changes with their reviewers, per-reviewer and sharing counts, and every conflict.</summary>
    public static ChangeReport From(Combination combination)
    {
        ArgumentNullException.ThrowIfNull(combination);
        ImmutableArray<string> names = [.. combination.Reviewers.Select(r => r.Name)];
        var items = new List<ReportItem>();
        var counts = new List<(Change Change, bool Sub)>();
        foreach (CombinedChange cc in combination.Changes)
        {
            counts.Add((cc.Change, cc.Change.IsSubstantive));
        }

        ReportItem Item(CombinedChange cc) => MakeItem(cc.Id, cc.Change, combination.Original, combination.Comparisons[cc.Holders[0].Reviewer].New, [.. cc.Reviewers.Select(r => names[r])], cc.ConflictId, []);
        foreach (CombinedChange cc in combination.Changes.Where(c => c.Change.IsSubstantive))
        {
            items.Add(Item(cc));
        }

        string title = combination.Original.Name + " + " + string.Join(", ", names);
        ReportSection section = Build(title, counts, [.. items], ChangeLedger.Empty);
        ImmutableArray<ReportCount> perReviewer = [.. combination.Reviewers.Select(r => new ReportCount(r.Name, combination.Changes.Count(c => c.Reviewers.Contains(r.Index))))];
        var shared = new List<ReportCount>();
        for (int n = 1; n <= combination.Reviewers.Length; n++)
        {
            int count = combination.Changes.Count(c => c.Reviewers.Length == n);
            if (count > 0)
            {
                shared.Add(new ReportCount(n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " reviewer" : " reviewers"), count));
            }
        }

        ImmutableArray<ReportConflict> conflicts = [.. combination.Conflicts.Select(k => new ReportConflict(
            k.Id,
            ConflictName(k.Kind),
            [.. k.Reviewers.Select(r => names[r])],
            [.. k.ChangeIds.Select(i => Item(combination.Changes[i]))]))];
        return new ChangeReport(title, "combination", [section], perReviewer, conflicts, [.. shared]);
    }

    /// <summary>Reports a chain: a section per step with its counts and substantive list, then the net change from first to last version with the steps that touched each change.</summary>
    public static ChangeReport From(ComparisonChain chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        var sections = new List<ReportSection>();
        foreach (ChainStep s in chain.Steps)
        {
            sections.Add(Section($"step {(s.Index + 1).ToString(CultureInfo.InvariantCulture)}: {s.FromName} → {s.ToName}", s.Comparison, s.Index));
        }

        string title = chain.Versions[0].Name + " → " + chain.Versions[^1].Name;
        var items = new List<ReportItem>();
        foreach (Change c in chain.Net.Changes.Where(c => c.IsSubstantive))
        {
            items.Add(MakeItem(c.Id, c, chain.Net.Old, chain.Net.New, [], -1, chain.StepsOf(c.Id)));
        }

        sections.Add(Build("net: " + title, [.. chain.Net.Changes.Select(c => (c, c.IsSubstantive))], [.. items], chain.Net.Ledger));
        return new ChangeReport(title, "chain", [.. sections], [], [], []);
    }

    private static ReportSection Section(string title, Comparison c, int? step)
    {
        ImmutableArray<int> steps = step is { } s ? [s] : [];
        ImmutableArray<ReportItem> items = [.. c.Changes.Where(x => x.IsSubstantive).Select(x => MakeItem(x.Id, x, c.Old, c.New, [], -1, steps))];
        return Build(title, [.. c.Changes.Select(x => (x, x.IsSubstantive))], items, c.Ledger);
    }

    private static ReportSection Build(string title, List<(Change Change, bool Sub)> all, ImmutableArray<ReportItem> items, ChangeLedger ledger)
    {
        var kinds = new SortedDictionary<int, int>();
        var types = new SortedDictionary<int, int>();
        foreach ((Change c, _) in all)
        {
            kinds[(int)c.Kind] = kinds.GetValueOrDefault((int)c.Kind) + 1;
            types[(int)c.Type] = types.GetValueOrDefault((int)c.Type) + 1;
        }

        return new ReportSection(
            title,
            all.Count,
            all.Count(x => x.Sub),
            [.. kinds.Select(k => new ReportCount(Serialization.JsonNames.ChangeKinds.Name((ChangeKind)k.Key), k.Value))],
            [.. types.Select(k => new ReportCount(Serialization.JsonNames.ChangeTypes.Name((ChangeType)k.Key), k.Value))],
            items,
            [.. ledger.Entries.Select(e => new ReportCount(LedgerCategoryNames.NameOf(e.Category), e.Count))]);
    }

    private static string ConflictName(ConflictKind k) => k switch
    {
        ConflictKind.EditAndDelete => "edit and delete",
        ConflictKind.Edits => "overlapping edits",
        ConflictKind.Move => "moved differently",
        ConflictKind.Structure => "split or merged differently",
        ConflictKind.Attribute => "attribute set differently",
        _ => "inserted at the same place",
    };

    private static ReportItem MakeItem(int id, Change c, CompareDocument oldDoc, CompareDocument newDoc, ImmutableArray<string> reviewers, int conflict, ImmutableArray<int> steps)
    {
        string oldText, newText;
        if (!c.Edits.IsDefaultOrEmpty && c.Edits.Any(e => !e.IsFormatOnly))
        {
            oldText = string.Join(" … ", c.Edits.Where(e => e.OldLength > 0).Select(e => e.OldText));
            newText = string.Join(" … ", c.Edits.Where(e => e.NewLength > 0).Select(e => e.NewText));
        }
        else
        {
            oldText = c.Old.Text;
            newText = c.New.Text;
        }

        string resolved = "";
        if (c.OldValue is not null || c.NewValue is not null)
        {
            string type = c.Type == ChangeType.None ? "" : Capital(Serialization.JsonNames.ChangeTypes.Name(c.Type)) + " ";
            resolved = type + (c.OldValue?.Canonical ?? "—") + " → " + (c.NewValue?.Canonical ?? "—");
        }

        string where = c.Old.IsPresent ? Where(oldDoc, c.Old.Nodes[0]) : c.New.IsPresent ? Where(newDoc, c.New.Nodes[0]) : "";
        return new ReportItem(
            id,
            where,
            Serialization.JsonNames.ChangeKinds.Name(c.Kind),
            Serialization.JsonNames.ChangeTypes.Name(c.Type),
            Short(oldText),
            Short(newText),
            resolved,
            reviewers,
            conflict,
            steps);
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string Short(string s)
    {
        const int Max = 200;
        s = s.Replace(HtmlOut.Separator, ' ');
        if (s.Length <= Max)
        {
            return s;
        }

        int cut = Max;
        if (char.IsHighSurrogate(s[cut - 1]))
        {
            cut--;
        }

        return s[..cut] + "…";
    }

    /// <summary>§label for the block or its nearest labelled ancestor; otherwise ¶ and the block's rank among paragraphs and list items.</summary>
    private static string Where(CompareDocument d, int node)
    {
        for (int n = node; n >= 0; n = d[n].Parent)
        {
            if (d[n].Kind == NodeKind.ListItem && d[n].Attributes.Get(FormatKeys.ListLabel) is { Length: > 0 } label)
            {
                return "§" + label.TrimEnd('.', ')');
            }
        }

        int rank = 0;
        for (int i = 0; i <= node; i++)
        {
            if (d[i].Kind is NodeKind.Paragraph or NodeKind.ListItem)
            {
                rank++;
            }
        }

        return "¶" + rank.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Renders the report as one self-contained HTML page: counts first, then the substantive lists, conflicts flagged.</summary>
    /// <returns>The page; the same report always gives the same text.</returns>
    public string ToHtml()
    {
        var sw = new StringWriter(new StringBuilder(), CultureInfo.InvariantCulture) { NewLine = "\n" };
        var o = new HtmlOut(sw);
        o.Raw("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<title>");
        o.Text("Change report: " + Title);
        o.Raw("</title>\n<style>\n");
        o.Raw(Css);
        o.Raw("</style>\n</head>\n<body>\n<h1>Change report</h1>\n<p class=\"sub\">");
        o.Text(Title);
        o.Raw("</p>\n");
        if (Mode == "combination")
        {
            Counts(o, "Reviewers", "Changes attributed", ByReviewer);
            Counts(o, "Made by", "Changes", Shared);
            Conflict(o);
        }

        foreach (ReportSection s in Sections)
        {
            Section(o, s);
        }

        o.Raw("</body>\n</html>\n");
        return sw.ToString();
    }

    private void Conflict(HtmlOut o)
    {
        o.Raw("<h2>Conflicts</h2>\n");
        if (Conflicts.IsEmpty)
        {
            o.Raw("<p>No conflicts: no two reviewers changed the same place differently.</p>\n");
            return;
        }

        o.Raw("<p>Each conflict lists every reviewer's version. None is preferred.</p>\n");
        foreach (ReportConflict k in Conflicts)
        {
            o.Raw("<section class=\"conflict\" data-conflict=\"");
            o.Int(k.Id);
            o.Raw("\">\n<h3>Conflict ");
            o.Int(k.Id + 1);
            o.Raw(": ");
            o.Text(k.Kind);
            o.Raw(" (");
            o.Text(string.Join(", ", k.Reviewers));
            o.Raw(")</h3>\n");
            Table(o, k.Items, true);
            o.Raw("</section>\n");
        }
    }

    private void Section(HtmlOut o, ReportSection s)
    {
        o.Raw("<h2>");
        o.Text(s.Title);
        o.Raw("</h2>\n<p>");
        o.Int(s.Total);
        o.Raw(s.Total == 1 ? " change, " : " changes, ");
        o.Int(s.Substantive);
        o.Raw(" substantive.</p>\n");
        Counts(o, "Kind", "Changes", s.ByKind);
        Counts(o, "Type", "Changes", s.ByType);
        Counts(o, "Hidden in the ledger", "Differences", s.Hidden);
        if (s.Items.IsEmpty)
        {
            o.Raw("<p>No substantive changes.</p>\n");
            return;
        }

        o.Raw("<h3>Substantive changes</h3>\n");
        Table(o, s.Items, Mode == "combination");
    }

    private static void Counts(HtmlOut o, string head, string countHead, ImmutableArray<ReportCount> counts)
    {
        if (counts.IsEmpty)
        {
            return;
        }

        o.Raw("<table class=\"counts\">\n<thead><tr><th scope=\"col\">");
        o.Text(head);
        o.Raw("</th><th scope=\"col\">");
        o.Text(countHead);
        o.Raw("</th></tr></thead>\n<tbody>\n");
        foreach (ReportCount c in counts)
        {
            o.Raw("<tr><td>");
            o.Text(c.Name);
            o.Raw("</td><td class=\"n\">");
            o.Int(c.Count);
            o.Raw("</td></tr>\n");
        }

        o.Raw("</tbody>\n</table>\n");
    }

    private static void Table(HtmlOut o, ImmutableArray<ReportItem> items, bool withReviewers)
    {
        bool withSteps = items.Any(i => !i.Steps.IsDefaultOrEmpty);
        o.Raw("<table class=\"items\">\n<thead><tr><th scope=\"col\">Where</th><th scope=\"col\">Kind</th><th scope=\"col\">Change</th>");
        if (withReviewers)
        {
            o.Raw("<th scope=\"col\">By</th>");
        }

        if (withSteps)
        {
            o.Raw("<th scope=\"col\">Step</th>");
        }

        o.Raw("</tr></thead>\n<tbody>\n");
        foreach (ReportItem i in items)
        {
            o.Raw("<tr");
            o.IntAttr("data-item", i.Id);
            if (i.ConflictId >= 0)
            {
                o.IntAttr("data-conflict", i.ConflictId);
            }

            o.Raw("><td>");
            o.Text(i.Location);
            o.Raw("</td><td>");
            o.Text(i.Kind);
            o.Raw("</td><td>");
            if (i.Resolved.Length > 0)
            {
                o.Raw("<strong>");
                o.Text(i.Resolved);
                o.Raw("</strong> ");
            }

            if (i.Old.Length > 0)
            {
                o.Raw("<del>");
                o.Text(i.Old);
                o.Raw("</del> ");
            }

            if (i.New.Length > 0)
            {
                o.Raw("<ins>");
                o.Text(i.New);
                o.Raw("</ins>");
            }

            o.Raw("</td>");
            if (withReviewers)
            {
                o.Raw("<td>");
                o.Text(string.Join(", ", i.Reviewers));
                if (i.ConflictId >= 0)
                {
                    o.Raw(" <span class=\"flag\">conflict ");
                    o.Int(i.ConflictId + 1);
                    o.Raw("</span>");
                }

                o.Raw("</td>");
            }

            if (withSteps)
            {
                o.Raw("<td>");
                o.Text(string.Join(", ", i.Steps.Select(s => (s + 1).ToString(CultureInfo.InvariantCulture))));
                o.Raw("</td>");
            }

            o.Raw("</tr>\n");
        }

        o.Raw("</tbody>\n</table>\n");
    }

    private const string Css = """
        body { font: 16px/1.5 system-ui, sans-serif; color: #1a1a1a; margin: 2rem auto; max-width: 62rem; padding: 0 1rem; }
        h1 { font-size: 1.6rem; margin-bottom: 0; } .sub { color: #444; margin-top: 0.2rem; }
        h2 { font-size: 1.2rem; margin-top: 2rem; border-bottom: 1px solid #bbb; } h3 { font-size: 1rem; }
        table { border-collapse: collapse; margin: 0.5rem 0 1rem; } th, td { border: 1px solid #bbb; padding: 0.25rem 0.6rem; text-align: left; vertical-align: top; }
        table.counts td.n { text-align: right; } table.items { width: 100%; }
        ins { color: #0000cc; text-decoration: underline double; } del { color: #b00000; text-decoration: line-through; }
        .conflict { border-left: 0.35rem solid #c05000; padding-left: 0.8rem; margin: 1rem 0; } .flag { color: #8a3b00; font-weight: 600; }
        """;
}
