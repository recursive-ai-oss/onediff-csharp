# OneNLP value recognizer (`RecursiveAI.OneDiff.Nlp`)

A value recognizer for the seam in `docs/semantics.md` section 3, built on [OneNLP](https://recursiveai.org/onenlp/)'s recognizers. It reads what the built-in recognizer cannot (written-out numbers, `six percent`, `thirty days`, `two hundred thousand dollars`) and returns the same canonical values. Everything it does not cover is read by the built-in recognizer. The package references `RecursiveAI.OneDiff.Documents` and `RecursiveAI.OneNlp` (a preview while OneNLP is one) and nothing else of ours.

```csharp
using OneDiff.Nlp;

var settings = CompareSettings.Legal.UseOneNlp();       // per comparison
var cmp = Comparison.Of("v1.docx", "v2.docx", settings);

OneNlpRecognizers.Register();                            // or once, for every comparison that names none
```

Precedence, from the strongest: `CompareSettings.ValueRecognizer`; the recognizer registered with `ValueRecognizers.Register` (`OneNlpRecognizers.Register()` registers this one; `ValueRecognizers.Register(null)` undoes it); `BuiltInValueRecognizer`. The setting is not written to the JSON, which records only what a recognizer decided.

## What it does

`NlpValueRecognizer.Recognize(text, language)` runs OneNLP's English recognizer (`Recognizer.English`, no reference date) and maps each result to a value of this library:

| OneNLP result | Becomes | Canonical form |
|---|---|---|
| money, with a currency code | `Amount` | `USD 25000` (cents are converted: `fifty cents` is `USD 0.5`) |
| percent | `Percentage` | `6%` |
| cardinal | `Number` | `42` |
| duration | `Duration` | `30 d`, `2 w`, `18 mo`, `2 y`; stated in the unit the text used, so `thirty days` is `30 d`, not 2,592,000 seconds. A mixed duration is counted in its smallest unit (`one week and two days` is `9 d`, `1 year and 6 months` is `18 mo`). |
| date of a day | `Date` | `2028-05-16` |
| date that is a calendar month | `Date` | `2025-03` |

Two patterns that contracts use and OneNLP reads as separate numbers are joined here: a written number, the same number in brackets and a unit (`thirty (30) days`, `sixty (60) business days`) is one duration, and a number followed by `business days` or `working days` is `bd`.

Not mapped, because the library has no such value: ordinals, measures (`5 km`), times of day, recurrences, bare years and other periods (`spring 2021`). Relative expressions (`next Friday`, `tomorrow`) are not reported: they need a reference date, and a change side has none. Texts over 4,096 characters give no result, as for the built-in recognizer.

## How it combines with the built-in recognizer

The built-in values are the base; OneNLP adds to them.

1. A OneNLP value that overlaps no built-in value is added.
2. A OneNLP value that covers every built-in value it overlaps, and more (`forty-two thousand` over a bare `thousand`; `thirty (30) days` over `30`), replaces them.
3. Where both read exactly the same text, the built-in value stays, so the conventions it reads per language are never overridden (`18.000 EUR` is `EUR 18000`, where English rules would say 18). A date that the built-in recognizer found but would not resolve (`3/4/2025`, which could be March or April) stays unresolved.
4. If the caller declares English (`language` is `en` or `en-*`), rule 3 changes: OneNLP's value is taken (`1.200` is `1.2`), except for a date the built-in recognizer left unresolved.
5. If the caller declares another language, the built-in recognizer alone answers.

The comparison engine does not know a document's language and passes none, so a comparison made with this recognizer follows rules 1 to 3. Party names (OneNLP has none), other languages, dates without a year and everything else come from the built-in recognizer unchanged; on text in the seven measured languages that has no written-out values, the output is byte-for-byte what the built-in recognizer gives (`docs/results/synth-v1-eval-nlp.md`).

A recognizer only labels changes. It cannot add, remove, move or reshape a change, and `Verify` checks the same things with it as without.

## Limits

- OneNLP's rules are English only (its 0.5 adds other languages); until then other languages get nothing from it. The recognizer needs no change when they arrive: declared languages route by tag, and an undeclared text is gap-filled.
- Undeclared text is treated as possibly English. A text in another Latin-script language that happens to read as an English number or date, where the built-in recognizer reads nothing, is typed by OneNLP's English reading.
- The first call loads OneNLP's rules (a few milliseconds; see the results page).
