# synth-v1 eval: the OneNLP value recognizer

`RecursiveAI.OneDiff.Nlp` (`CompareSettings.Legal.UseOneNlp()`) against the built-in recognizer, on the synth-v1 **eval** split (set hash `e11b5614d5cd967d4a3feac46e636156fb0beb0e741f96a28bfe7236c3d402a8`, 1,050 pairs, seven languages). OneNLP `RecursiveAI.OneNlp` 0.1.0-preview.1 (English rules). Scored with the semantic, boundary, fragmentation, alignment and verification rows of [measurement.md](../measurement.md) (`score --recognizer nlp`), 10,000 bootstrap resamples over documents, seed 20261104. Method and rules: [nlp.md](../nlp.md). x64 Ryzen 9 7950X3D, .NET 10.0.

## Quality: a tie, by construction of the set

The gold set writes every value in digits or in the symbols and words of its language, which the built-in recognizer reads, and the engine passes no language, so OneNLP only fills gaps (nlp.md, rules 1 to 3). On this set that means the two recognizers type every change alike: the score files of the two runs are byte-identical (sha-256 of the row JSON equal), so every row, with its interval, is the same as in [synth-v1-eval-semantics.md](synth-v1-eval-semantics.md). Per language, type and resolved-value accuracy (the same for both recognizers; interval in brackets):

| Language | Type accuracy | Value accuracy |
|---|---|---|
| all | .9986 [.9971, .9997] | .9983 [.9966, .9997] |
| en | 1.0 | 1.0 |
| de, fr, zh, ar | 1.0 | 1.0 |
| es | 1.0 | .9978 [.9924, 1.0] |
| ja | .9913 [.9821, .9980] | .9913 [.9821, .9980] |

English is at least equal to the built-in recognizer (equal, difference 0 in every one of the 150 pairs); the other languages are unchanged. Existing rows (boundary F1, fragmentation, missed sentences, alignment, cascade, rename and substantive rows) show no regression: identical.

The set cannot show a gain, because nothing in it is written out in words. That is measured on a small English set of our own phrasing instead.

## Where OneNLP is stronger: written-out English values

26 phrases in `tests/OneDiff.Nlp.Tests/NlpValueRecognizerTests.cs` (our own wording, not from any eval pair), each a whole value with a known canonical form: written numbers, `six percent`, `thirty days`, `thirty (30) days`, `ten business days`, `1 year and 6 months`, `two hundred thousand dollars`, `fifty cents`, `the 16th of May, 2028`, and digit forms of the same kinds as controls.

| Recognizer | Phrases read whole and correctly |
|---|---|
| Built-in | 9 of 26 (the digit forms) |
| With OneNLP | 26 of 26 |

The 17 the built-in recognizer misses are all written-out or compound: `forty-two`, `one hundred and five`, `six percent`, `twelve and a half percent`, `thirty days`, `two weeks`, `ninety (90) days`, `sixty (60) business days`, `ten business days`, `eighteen months`, `1 year and 6 months`, `two years`, `one week and two days`, `two hundred thousand dollars`, `fifty cents`, `five million euros`, `the 16th of May, 2028`. Relative dates (`next Friday`) are out of reach for both: they need a reference date.

## Exact gates

| Check | Result |
|---|---|
| Verify under Legal with the recognizer selected, all 1,050 eval pairs (`nlp-gate`) | 1,050 pass, fallback 0, `Check()` empty |
| Two runs, JSON byte-identical | 1,050 of 1,050 |
| JSON different from the built-in recognizer's | 0 of 1,050 |
| Digest of 20 pairs spread across the set (every 52nd), JSON sha-256s hashed together | `9807ed12cc77182183a1dbf44e29f67224fd7092a2f74ee1d6fdb600dd66d4ba` on x64 (Ryzen 9 7950X3D, Linux) and arm64 (Apple M2, macOS), equal |
| Whole solution, Release, warnings as errors | 0 warnings; 723 tests, 714 pass, 9 skips (environment-gated, as before), 0 fail |
| NativeAOT smoke app (linux-x64, gcc), including the recognizer | zero warnings, prints `ok` |
| Packed `RecursiveAI.OneDiff.Nlp` | dependencies: `RecursiveAI.OneDiff.Documents`, `RecursiveAI.OneNlp`, nothing else; package validation on |

## Cost (indicative)

A synthetic contract of 100 pages (4,000 paragraphs, 300 edited values of four kinds), Legal, 4 cores, worker under other jobs' load (load average 22), five repetitions after a warm-up, `time-nlp`:

| | Comparison.Of, ms | Classify stage alone, ms |
|---|---|---|
| Built-in recognizer | 379, 374, 231, 232, 217 (first runs at the start of the process) | 12.7, 11.4, 3.2, 3.0, 2.7 |
| OneNLP recognizer | 229, 269, 243, 245, 236 | 13.0, 5.6, 5.8, 5.6, 5.3 |

The recognizer costs about 2.5 ms more over 300 typed changes (a Classify stage of about 5 ms against 3 ms), 1% of the comparison. The first OneNLP call, which loads its English rules, took 65 ms including JIT. Not a publishable timing: no bench class, a loaded machine.

## Limits

- A gain on real contracts is not measured: the real version pairs (EDGAR and the others) carry digit forms almost always, and are not labelled for values.
- Until OneNLP has rules for other languages, nothing changes for them.
