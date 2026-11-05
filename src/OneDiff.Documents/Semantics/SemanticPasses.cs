using System.Collections.Immutable;
using OneDiff.Documents.Model;
using OneDiff.Documents.Pipeline;

namespace OneDiff.Documents.Semantics;

/// <summary>The text of a change's sides in the coordinates the sides use (the nodes' joined, projected text), cached per node list.</summary>
internal sealed class SideTexts(Alignment alignment)
{
    private readonly Dictionary<string, string> cache = new(StringComparer.Ordinal);

    public string Of(bool old, ChangeSide side)
    {
        if (!side.IsPresent)
        {
            return "";
        }

        string key = (old ? "o" : "n") + string.Join(',', side.Nodes);
        if (!cache.TryGetValue(key, out string? text))
        {
            text = ProjectedGroup.Create(old ? alignment.Old : alignment.New, side.Nodes, alignment.Settings).Text;
            cache[key] = text;
        }

        return text;
    }
}

/// <summary>
/// The edits the rule grouping keeps out of its joining (docs/semantics.md): renamed-term edits and clause numbers that follow a
/// renumbering.
/// </summary>
/// <param name="Renames">The (old, new) texts of renamed terms.</param>
/// <param name="LabelPairs">The (old, new) list labels a renumbering moved, normalized.</param>
internal sealed record SemanticPins(HashSet<(string, string)> Renames, HashSet<(string, string)> LabelPairs)
{
    /// <summary>
    /// A key naming the clause number an edit sits in when that number is a reference that a renumbering rewrote (<c>Section 3.2</c>
    /// becoming <c>Section 3.3</c> where the label 3.2 became 3.3), the same key for every edit in one number; <see cref="long.MinValue"/> otherwise.
    /// </summary>
    private const bool PinReferences = false;

    public long ReferenceNumber(string oldText, string newText, RegionEdit e)
    {
        // Switched off until a pinned reference unit is widened to its whole number (the unit must read 8.2 to 8.3, not 2 to 3).
        if (LabelPairs.Count == 0 || !PinReferences)
        {
            return long.MinValue;
        }

        (int os, int oe) = Token(oldText, e.OldStart, e.OldEnd);
        (int ns, int ne) = Token(newText, e.NewStart, e.NewEnd);
        if (oe <= os || ne <= ns || e.OldStart < os || e.OldEnd > oe || e.NewStart < ns || e.NewEnd > ne)
        {
            return long.MinValue;
        }

        string o = oldText.Substring(os, oe - os), n = newText.Substring(ns, ne - ns);
        if (!LabelPairs.Contains((TextRules.NormalizeLabel(o), TextRules.NormalizeLabel(n)))
            || !TextRules.TryReference(oldText, os, oe - os, out _) || !TextRules.TryReference(newText, ns, ne - ns, out _))
        {
            return long.MinValue;
        }

        return ((long)os << 32) | (uint)ns;
    }

    /// <summary>The run of digits and inner full stops (<c>3.2</c>) that holds or touches [start, end).</summary>
    private static (int Start, int End) Token(string text, int start, int end)
    {
        int s = Math.Min(start, text.Length), e = Math.Min(end, text.Length);
        while (s > 0 && IsNumberChar(text, s - 1))
        {
            s--;
        }

        while (e < text.Length && IsNumberChar(text, e))
        {
            e++;
        }

        return (s, e);
    }

    private static bool IsNumberChar(string t, int i) =>
        char.IsAsciiDigit(t[i]) || (t[i] == '.' && i > 0 && i + 1 < t.Length && char.IsAsciiDigit(t[i - 1]) && char.IsAsciiDigit(t[i + 1]));
}

/// <summary>
/// Cascades and defined-term renames as display groups. Both are found from the changes alone and arranged as
/// <see cref="ChangeGroup"/>s; no change is altered, merged or dropped, so every edit stays in the change list and
/// <see cref="Stages.Verify"/> sees all of them.
/// <list type="bullet">
/// <item><b>Cascade.</b> A list label that changed (<c>3.2</c> to <c>3.3</c>), and every change that rewrites a reference to a clause
/// from one number to another when that pair of numbers is one of the label changes: the references the renumbering caused. One group
/// per comparison, titled "Renumbering".</item>
/// <item><b>Rename.</b> A term the old document defines (<c>"Software" means</c>) and the new one does not, replaced by a term the new
/// document defines and the old one does not, in at least two places. One group per pair of terms. A change takes part only when
/// its sides are exactly the two terms, so a rename never swallows another edit in the same sentence.</item>
/// </list>
/// A change that is part of a larger region (a moved paragraph with its contents) stays in that region.
/// </summary>
internal static class SemanticGrouping
{
    /// <summary>The longest side, in characters, that can be a term or a reference.</summary>
    private const int MaxSide = 64;

    public static ChangeSet Apply(ChangeSet set, CancellationToken ct)
    {
        if (!set.Settings.CollapseCascades || set.Changes.IsEmpty)
        {
            return set;
        }

        var texts = new SideTexts(set.Alignment);
        var slotOf = new Dictionary<int, int>();
        var titles = new List<(GroupKind Kind, string Title)>();

        // ---- cascade ----
        var labelPairs = new HashSet<(string, string)>();
        var labelChanges = new List<int>();
        foreach (Change c in set.Changes)
        {
            ct.ThrowIfCancellationRequested();
            if (c.Kind == ChangeKind.Attribute && !c.AttributeEdits.IsEmpty && IsRenumbering(c))
            {
                labelChanges.Add(c.Id);
                foreach (AttributeEdit e in c.AttributeEdits)
                {
                    labelPairs.Add((TextRules.NormalizeLabel(e.OldValue!), TextRules.NormalizeLabel(e.NewValue!)));
                }
            }
        }

        if (labelChanges.Count > 0)
        {
            var members = new List<int>(labelChanges);
            foreach (Change c in set.Changes)
            {
                ct.ThrowIfCancellationRequested();
                if (c.Kind == ChangeKind.Replace && c.LinkId >= 0 && c.Old.Length is > 0 and <= MaxSide && c.New.Length is > 0 and <= MaxSide
                    && ReferencePair(c, texts) is { } pair && labelPairs.Contains(pair))
                {
                    members.Add(c.Id);
                }
            }

            members.Sort();
            titles.Add((GroupKind.Cascade, "Renumbering"));
            foreach (int id in members)
            {
                slotOf[id] = 0;
            }
        }

        // ---- rename ----
        var candidates = new List<Change>();
        foreach (Change c in set.Changes)
        {
            if (c.Kind == ChangeKind.Replace && !slotOf.ContainsKey(c.Id) && c.Old.Length is > 0 and <= MaxSide + 8 && c.New.Length is > 0 and <= MaxSide + 8)
            {
                candidates.Add(c);
            }
        }

        if (candidates.Count >= 2)
        {
            (HashSet<string> oldTerms, HashSet<string> newTerms) = DefinedIn(set.Alignment);
            var pairsInOrder = new List<(string Old, string New)>();
            var byPair = new Dictionary<(string, string), List<int>>();
            if (oldTerms.Count > 0 && newTerms.Count > 0)
            {
                foreach (Change c in candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    if (TextRules.TermPair(c.Old.Text.Trim(), c.New.Text.Trim(), oldTerms, newTerms, out string a, out string b))
                    {
                        if (!byPair.TryGetValue((a, b), out List<int>? list))
                        {
                            list = [];
                            byPair[(a, b)] = list;
                            pairsInOrder.Add((a, b));
                        }

                        list.Add(c.Id);
                    }
                }
            }

            foreach ((string o, string n) in pairsInOrder)
            {
                List<int> ids = byPair[(o, n)];
                if (ids.Count >= 2)
                {
                    int slot = titles.Count;
                    titles.Add((GroupKind.Rename, "\"" + o + "\" \u2192 \"" + n + "\""));
                    foreach (int id in ids)
                    {
                        slotOf[id] = slot;
                    }
                }
            }
        }

        if (titles.Count == 0)
        {
            return set;
        }

        var members2 = new List<int>[titles.Count];
        for (int i = 0; i < members2.Length; i++)
        {
            members2[i] = [];
        }

        foreach (KeyValuePair<int, int> kv in slotOf.OrderBy(k => k.Key))
        {
            members2[kv.Value].Add(kv.Key);
        }

        // A lifted change leaves its region (the other changes of a moved paragraph stay together); the new group stands where its
        // first member stood.
        var emitted = new bool[titles.Count];
        ImmutableArray<ChangeGroup>.Builder groups = ImmutableArray.CreateBuilder<ChangeGroup>(set.Groups.Length + titles.Count);
        foreach (ChangeGroup g in set.Groups)
        {
            if (g.Kind != GroupKind.Region)
            {
                groups.Add(g with { Id = groups.Count });
                continue;
            }

            ImmutableArray<int> rest = [.. g.ChangeIds.Where(id => !slotOf.ContainsKey(id))];
            if (!rest.IsEmpty)
            {
                groups.Add(g with { Id = groups.Count, ChangeIds = rest });
            }

            foreach (int id in g.ChangeIds)
            {
                if (slotOf.TryGetValue(id, out int slot) && !emitted[slot])
                {
                    emitted[slot] = true;
                    groups.Add(new ChangeGroup(groups.Count, titles[slot].Kind, titles[slot].Title, [.. members2[slot]]));
                }
            }
        }

        return set.WithGroups(groups.ToImmutable());
    }

    /// <summary>
    /// What the grouping keeps apart from its neighbours: the pairs of texts that edits replace in at least two places and that are a
    /// defined-term rename (see <see cref="TextRules.TermPair"/>), and the pairs of list labels a renumbering moved (to recognize the
    /// clause numbers that follow). Null when there is neither.
    /// </summary>
    public static SemanticPins? Pins(PairDiffs diffs, CancellationToken ct)
    {
        var labelPairs = new HashSet<(string, string)>();
        var counts = new Dictionary<(string, string), int>();
        foreach (LinkDiff d in diffs.Links)
        {
            ct.ThrowIfCancellationRequested();
            foreach (AttributeEdit e in d.AttributeEdits)
            {
                if (e.Key == FormatKeys.ListLabel && e.OldValue is not null && e.NewValue is not null)
                {
                    labelPairs.Add((TextRules.NormalizeLabel(e.OldValue), TextRules.NormalizeLabel(e.NewValue)));
                }
            }

            foreach (RegionEdit e in d.Edits)
            {
                if (e.OldLength is > 0 and <= MaxSide && e.NewLength is > 0 and <= MaxSide && !e.IsFormatOnly)
                {
                    (string, string) key = (e.OldText.Trim(), e.NewText.Trim());
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
            }
        }

        var renames = new HashSet<(string, string)>();
        if (counts.Any(static kv => kv.Value >= 2))
        {
            (HashSet<string> oldTerms, HashSet<string> newTerms) = DefinedIn(diffs.Alignment);
            if (oldTerms.Count > 0 && newTerms.Count > 0)
            {
                foreach (KeyValuePair<(string, string), int> kv in counts.OrderBy(static kv => kv.Key.Item1, StringComparer.Ordinal).ThenBy(static kv => kv.Key.Item2, StringComparer.Ordinal))
                {
                    if (kv.Value >= 2 && kv.Key.Item1.Length > 0 && kv.Key.Item2.Length > 0 && TextRules.TermPair(kv.Key.Item1, kv.Key.Item2, oldTerms, newTerms, out _, out _))
                    {
                        renames.Add(kv.Key);
                    }
                }
            }
        }

        return renames.Count == 0 && labelPairs.Count == 0 ? null : new SemanticPins(renames, labelPairs);
    }

    /// <summary>Whether every attribute edit of the change is a list label that went from one value to another.</summary>
    private static bool IsRenumbering(Change c)
    {
        foreach (AttributeEdit e in c.AttributeEdits)
        {
            if (e.Key != FormatKeys.ListLabel || e.OldValue is null || e.NewValue is null)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The pair of clause numbers a change rewrites (<c>3.2</c> to <c>3.3</c>), or null when it is not a reference change.</summary>
    private static (string, string)? ReferencePair(Change c, SideTexts texts)
    {
        if (TextRules.TryReference(texts.Of(true, c.Old), c.Old.Start, c.Old.Length, out string a)
            && TextRules.TryReference(texts.Of(false, c.New), c.New.Start, c.New.Length, out string b))
        {
            return (TextRules.NormalizeLabel(a), TextRules.NormalizeLabel(b));
        }

        return null;
    }

    /// <summary>The terms each document defines.</summary>
    internal static (HashSet<string> Old, HashSet<string> New) DefinedIn(Alignment al)
    {
        var o = new HashSet<string>(StringComparer.Ordinal);
        var n = new HashSet<string>(StringComparer.Ordinal);
        Collect(al.Old, o);
        Collect(al.New, n);
        return (o, n);

        static void Collect(CompareDocument doc, HashSet<string> into)
        {
            var terms = new List<string>();
            foreach (Node node in doc.Nodes)
            {
                if (node.Kind is NodeKind.Paragraph or NodeKind.ListItem)
                {
                    terms.Clear();
                    TextRules.DefinedTerms(node.Text, terms);
                    foreach (string t in terms)
                    {
                        into.Add(t);
                    }
                }
            }
        }
    }
}

/// <summary>
/// Classification: who decided each change, what it is about (<see cref="Change.Type"/>), the values it resolves
/// (<see cref="Change.OldValue"/>, <see cref="Change.NewValue"/>) and whether it matters (<see cref="Change.IsSubstantive"/>).
/// Every decision is a deterministic rule at score 1, except a value that reads as such but cannot be resolved (score 0.5).
/// </summary>
internal static class ChangeClassifier
{
    public static ChangeSet Classify(ChangeSet set, IValueRecognizer recognizer, CancellationToken ct)
    {
        bool typed = set.Settings.TypedChanges;
        var groupKind = new Dictionary<int, GroupKind>();
        foreach (ChangeGroup g in set.Groups)
        {
            if (g.Kind is GroupKind.Cascade or GroupKind.Rename)
            {
                foreach (int id in g.ChangeIds)
                {
                    groupKind[id] = g.Kind;
                }
            }
        }

        var texts = new SideTexts(set.Alignment);
        HashSet<string>? oldTerms = null, newTerms = null;
        ImmutableArray<Change>.Builder b = ImmutableArray.CreateBuilder<Change>(set.Changes.Length);
        foreach (Change c in set.Changes)
        {
            ct.ThrowIfCancellationRequested();
            groupKind.TryGetValue(c.Id, out GroupKind kind);
            Verdict v = Analyze(c, kind, typed, recognizer, texts, set.Alignment, ref oldTerms, ref newTerms);
            b.Add(c with
            {
                Decision = new Decision(DecidedBy.Rule, v.Source, v.Score),
                Type = typed ? v.Type : ChangeType.None,
                OldValue = typed ? v.Old : null,
                NewValue = typed ? v.New : null,
                IsSubstantive = v.Substantive,
            });
        }

        return new ChangeSet(set.Alignment, b.MoveToImmutable(), set.Groups, set.Ledger, set.Diagnostics, true);
    }

    private readonly record struct Verdict(ChangeType Type, ResolvedValue? Old, ResolvedValue? New, bool Substantive, string Source, double Score);

    private static Verdict Plain(bool substantive) => new(ChangeType.None, null, null, substantive, "semantics", 1.0);

    private static Verdict Analyze(
        Change c,
        GroupKind inGroup,
        bool typed,
        IValueRecognizer recognizer,
        SideTexts texts,
        Alignment al,
        ref HashSet<string>? oldTerms,
        ref HashSet<string>? newTerms)
    {
        switch (c.Kind)
        {
            case ChangeKind.Format:
                return new Verdict(ChangeType.Formatting, null, null, false, "semantics.format", 1.0);
            case ChangeKind.Attribute:
                if (!c.AttributeEdits.IsEmpty && c.AttributeEdits.All(static e => e.Key == FormatKeys.ListLabel))
                {
                    AttributeEdit e = c.AttributeEdits[0];
                    return new Verdict(
                        ChangeType.Numbering,
                        e.OldValue is null ? null : new ResolvedValue(TextRules.NormalizeLabel(e.OldValue)),
                        e.NewValue is null ? null : new ResolvedValue(TextRules.NormalizeLabel(e.NewValue)),
                        false,
                        "semantics.numbering",
                        1.0);
                }

                return Plain(false);
            case ChangeKind.Move:
                return Plain(true);
            case ChangeKind.Split or ChangeKind.Merge:
                foreach (RegionEdit e in c.Edits)
                {
                    if (!e.IsFormatOnly && !string.Equals(TextRules.FoldTypography(e.OldText), TextRules.FoldTypography(e.NewText), StringComparison.Ordinal))
                    {
                        return Plain(true);
                    }
                }

                return Plain(false);
        }

        string o = c.Old.Text.Trim(), n = c.New.Text.Trim();
        string fo = TextRules.FoldTypography(o), fn = TextRules.FoldTypography(n);
        if (fo.Length == 0 && fn.Length == 0)
        {
            // White space or nothing at all: a blank paragraph is not substantive, a node of another kind (an image) is.
            bool paragraphLike = c.LinkId >= 0 || (c.Old.IsPresent ? al.Old[c.Old.Nodes[0]].Kind : al.New[c.New.Nodes[0]].Kind) is NodeKind.Paragraph or NodeKind.ListItem;
            return c.LinkId < 0 && !paragraphLike ? Plain(true) : new Verdict(ChangeType.Typography, null, null, false, "semantics.typography", 1.0);
        }

        if (string.Equals(fo, fn, StringComparison.Ordinal))
        {
            return new Verdict(ChangeType.Typography, null, null, false, "semantics.typography", 1.0);
        }

        if (inGroup == GroupKind.Cascade)
        {
            Verdict r = ReferenceVerdict(c, texts, false) ?? Plain(false);
            return r with { Substantive = false };
        }

        if (inGroup == GroupKind.Rename)
        {
            if (oldTerms is null || newTerms is null)
            {
                (oldTerms, newTerms) = SemanticGrouping.DefinedIn(al);
            }

            // The change may carry a clitic or a prefix next to the term; the value is the term itself.
            return TextRules.TermPair(o, n, oldTerms, newTerms, out string ta, out string tb)
                ? new Verdict(ChangeType.DefinedTerm, new ResolvedValue(ta), new ResolvedValue(tb), true, "semantics.term", 1.0)
                : new Verdict(ChangeType.DefinedTerm, new ResolvedValue(o), new ResolvedValue(n), true, "semantics.term", 1.0);
        }

        if (!typed || o.Length > 128 || n.Length > 128)
        {
            return Plain(true);
        }

        if (ReferenceVerdict(c, texts, true) is { } reference)
        {
            return reference;
        }

        if (ValueVerdict(o, n, recognizer) is { } value)
        {
            return value;
        }

        if (o.Length <= 64 && n.Length <= 64 && (o.Length > 0 || n.Length > 0))
        {
            if (oldTerms is null || newTerms is null)
            {
                (oldTerms, newTerms) = SemanticGrouping.DefinedIn(al);
            }

            bool oldOk = o.Length == 0 || oldTerms.Contains(o), newOk = n.Length == 0 || newTerms.Contains(n);
            if (oldOk && newOk)
            {
                return new Verdict(ChangeType.DefinedTerm, o.Length == 0 ? null : new ResolvedValue(o), n.Length == 0 ? null : new ResolvedValue(n), true, "semantics.term", 1.0);
            }
        }

        return Plain(true);
    }

    private static Verdict? ReferenceVerdict(Change c, SideTexts texts, bool substantive)
    {
        bool hasOld = c.Old.Length > 0, hasNew = c.New.Length > 0;
        if (!hasOld && !hasNew)
        {
            return null;
        }

        string a = "", b = "";
        if ((hasOld && !TextRules.TryReference(texts.Of(true, c.Old), c.Old.Start, c.Old.Length, out a))
            || (hasNew && !TextRules.TryReference(texts.Of(false, c.New), c.New.Start, c.New.Length, out b)))
        {
            return null;
        }

        return new Verdict(
            ChangeType.Reference,
            hasOld ? new ResolvedValue(TextRules.NormalizeLabel(a)) : null,
            hasNew ? new ResolvedValue(TextRules.NormalizeLabel(b)) : null,
            substantive,
            "semantics.reference",
            1.0);
    }

    /// <summary>A typed value change: each present side is exactly one recognized value, and both are the same kind.</summary>
    private static Verdict? ValueVerdict(string o, string n, IValueRecognizer recognizer)
    {
        RecognizedValue? ro = null, rn = null;
        if (o.Length > 0)
        {
            ro = Whole(recognizer.Recognize(o, null), o);
            if (ro is null)
            {
                return null;
            }
        }

        if (n.Length > 0)
        {
            rn = Whole(recognizer.Recognize(n, null), n);
            if (rn is null)
            {
                return null;
            }
        }

        if (ro is { } a && rn is { } b && a.Type != b.Type)
        {
            return null;
        }

        ChangeType type = (ro ?? rn)!.Value.Type;
        bool resolved = (ro is null || ro.Value.Value is not null) && (rn is null || rn.Value.Value is not null);
        return new Verdict(type, ro?.Value, rn?.Value, true, "semantics." + (type == ChangeType.Party ? "party" : "value"), resolved ? 1.0 : 0.5);
    }

    private static RecognizedValue? Whole(ImmutableArray<RecognizedValue> values, string text) =>
        values.Length == 1 && values[0].Start == 0 && values[0].Length == text.Length ? values[0] : null;
}
