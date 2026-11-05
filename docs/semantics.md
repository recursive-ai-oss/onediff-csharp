# Change semantics

What a change is *about*, and which changes belong together. Everything here is deterministic and works without a model. Nothing here alters an edit: cascades and renames are display groups over changes that stay in the list, types and values are labels on changes, and `Verify` checks the same completeness it always did.

Where it runs:

| Stage | Setting | Does |
|---|---|---|
| `Stages.Group` | `CollapseCascades` | pins rename edits (section 1), then lifts the changes of a renumbering and of a rename into `GroupKind.Cascade` and `GroupKind.Rename` groups |
| `Stages.Classify` | `TypedChanges` for types and values; always for `IsSubstantive` | sets `Change.Type`, `OldValue`, `NewValue`, `IsSubstantive` and the decision |

`CompareSettings.Legal` turns both on; `Strict` and `TextOnly` leave them off (Strict collapses and types nothing, so every edit reads as it is).

## 1. Cascades and renames

**Cascade.** A *renumbering* is a change whose attribute edits are all a list label going from one value to another (`list.label` `3.2` to `3.3`; surrounding space, brackets and a closing full stop do not count: `3.` and `3`). A *reference change* is a replacement whose old side and new side are each a reference to a clause: a keyword and a number (`Section 3.2`, `§ 3.2`, `Abschnitt 3.2`, `l'article 3.2`, `cláusula 3.2`, `البند 3.2`, `第3.2条`), or just the number when the keyword stands right before it. A reference change is *caused by the renumbering* when its pair of numbers is one of the renumbered labels (`3.2` to `3.3` here and in a label). The cascade group holds every renumbering and every caused reference change of the comparison, one group per comparison, titled "Renumbering". A reference edited by hand to a number no label moved to is not in it; it is a substantive `Reference` change. A reference that the density rules of `docs/grouping.md` merged into a larger unit with other edits is not a reference change and is not in the group either; the other edits are then not hidden by it.

**Rename.** The terms a document defines are the quoted phrases that the text goes on to define: followed by `means`, `shall mean`, `has the meaning`, `bezeichnet`, `bedeutet`, `désigne`, `signifie`, `significa`, `是指`, `指`, `とは`, `をいう`, `تعني`, `يعني` and the like, or closed by a bracket as in `(the "Supplier")` and `(以下「X」という)`. Quotes may be `" “ „ « ‹ 「 『`. A *renamed term* is a replacement of a term only the old document defines by a term only the new document defines, in at least two places (the definition and a use, or more). A prefix or suffix both texts share (an attached preposition such as Arabic `لـ`) is set aside when that is what it takes to find the two terms. The rename group holds those changes, one group per pair of terms, titled `"old" → "new"`. A change takes part only if its sides are exactly the two terms.

**Pinned edits.** To keep a rename from swallowing the other edits of its sentence, an edit that replaces a renamed term by its new name is not joined with the edits around it by the density rules of `docs/grouping.md`; it is its own unit unless the units built without it already reach it (then it goes back among them, as before). The pairs are found from the edits alone, before units are built.

A change that sits inside a larger region (the paragraph of a move) is lifted out of that region into its group; the other changes of the region stay together. Every change is in exactly one group after this, as `Verify` requires.

## 2. Types and resolved values

`Change.Type` is `None` for an ordinary text change. With `TypedChanges`, in this order:

1. `Formatting` for a format change; `Numbering` for a list-label attribute change (values: the label without closing punctuation).
2. `Typography` for a text change whose sides are equal once white space is dropped and quotation marks and dashes are made plain.
3. In a cascade group, `Reference`; in a rename group, `DefinedTerm` (values: the two terms).
4. A reference outside a cascade is `Reference` with the clause numbers as values.
5. A value change: each side that is present is, as a whole, exactly one value found by the recognizer, and when both are present they are of one kind: `Amount`, `Date`, `Duration`, `Percentage`, `Number`, `Party`. A kind with no resolved value on a side (an ambiguous date) still types the change, with a decision score of 0.5 instead of 1 and no value on that side.
6. A replacement, insertion or deletion of a term the document defines (one side may be empty): `DefinedTerm`.

Everything is a rule: `Decision.By` is `Rule`, the source names the rule (`semantics.value`, `semantics.reference`, `semantics.numbering`, `semantics.term`, `semantics.party`, `semantics.format`, `semantics.typography`, or `semantics` for the rest), the score is 1 except as stated in 5. A model that decides later does so with `DecidedBy.Model` and its own score.

**Canonical values** are the same in every language:

| Type | Canonical | Examples |
|---|---|---|
| Amount | ISO 4217 code, space, number | `USD 25000` (from `USD 25,000`, `25,000 دولار`), `EUR 18000` (`18.000 EUR`, `2 500 EUR` is `EUR 2500`), `CNY 4800` (`人民币4,800元`), `USD 2000000` (`$2 million`) |
| Date | ISO 8601 | `2028-05-16`, `2029-06-01` (`1. Juni 2029`), `2025-01-03` (`3 de enero de 2025`), `2029-08-06` (`6 أغسطس 2029`), `2027-12-27` (`2027年12月27日`); `2025-03` and `--03-16` when the day or the year is missing |
| Duration | number, space, unit (`s min h d bd w mo y`) | `30 d`, `10 bd` (business or working days), `18 mo` (`18 Monaten`), `9 mo` (`9个月`), `30 d` (`三十日`), `1 y` (`1年`) |
| Percentage | number and `%` | `12.5%`, `6%` (`6 %`) |
| Number | invariant decimal | `42` |
| Reference | the clause number | `8.4` (from `Section 8.4`, `البند 8.4`, `第8.4条`) |
| Numbering | the label without closing punctuation | `2.2` |
| DefinedTerm, Party | the text | `Software` |

Numbers: a number with one separator and exactly three digits after it (and one to three before, not starting with 0) reads as a thousands group (`1.200` is 1200, `1,200` is 1200, `0.500` is 0.5); in every other case a single separator is the decimal mark; with both, the last is the decimal mark. A date whose day and month could be either (`3/4/2025`; with dots `3.4.2025` is day first) is recognized and not resolved.

Month, unit and currency names are written for English, German, French, Spanish, Arabic, Chinese and Japanese; other languages are recognized where they use digits and symbols. The `Party` recognizer finds a name that ends in a legal form (`Inc`, `Ltd`, `LLC`, `plc`, `GmbH`, `AG`, `S.A.`, `SARL`, `B.V.`, `株式会社`, `有限公司` …).

## 3. The recognizer seam

```csharp
public interface IValueRecognizer
{
    ImmutableArray<RecognizedValue> Recognize(string text, string? language);
}
public readonly record struct RecognizedValue(int Start, int Length, ChangeType Type, ResolvedValue? Value);

ChangeSet typed = Stages.Classify(changes, myRecognizer);
```

`Recognize` returns the values of a short text (a change side), ordered by position and not overlapping; `Type` is one of the value types (`Amount`, `Date`, `Duration`, `Percentage`, `Number`, `Party`); `Value` is null when the text reads as such a value but cannot be resolved. An implementation is **pure and thread-safe**: no clock, no current culture, no network, no state shared between calls. `language` is a BCP 47 tag when the caller knows it, otherwise null. A recognizer from another package (multilingual, model-backed) replaces `BuiltInValueRecognizer`; only values come from it. References, list labels, defined terms and the substantive flag are decided by the library's own rules, so a recognizer cannot hide or reclassify a change. `Comparison.Of` and the other one-liners use the built-in recognizer.

## 4. Substantive

`Change.IsSubstantive` says whether the change alters what the document says. It is true for a change nobody classified.

Not substantive: `Format` changes; `Attribute` changes (style, list label, level); `Typography`; the reference changes of a cascade; whole-paragraph insertions and deletions of empty text; splits and merges whose edits only add or remove the paragraph break and white space.

Substantive: everything else, including moves, defined-term changes and renames (a drafting choice a reader still wants to see), insertions and deletions of text, and an image or table inserted or deleted.

`Comparison.Changes.Where(c => c.IsSubstantive)` is the filter; the substantive view of the HTML pages and the change report build on it.

## 5. JSON and HTML

Schema version 1 gains four optional members on a change (`type`, `oldValue`, `newValue`, `substantive`); see `docs/schema/comparison-v1.json`. They are written only when set (`type` unless `none`, values when present, `substantive` only as `false`), so comparisons made without semantics are byte-identical to before. A reader that does not know them ignores them. Groups of kind `cascade` and `rename` already existed in the schema; a group's `changes` are its occurrences and `title` is `Renumbering` or the term pair.

The HTML pages list each cascade and rename group once under "Grouped changes", with its occurrences (`data-group`, `data-occurrence` attributes); the marks in the text are the changes themselves and are untouched.

## 6. Measured

Rows and definitions in `docs/measurement.md` (Semantic rows).
