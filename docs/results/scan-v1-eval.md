# scan-v1 evaluation

The scan gold set (`docs/ocr.md` §4): the 600 eval pairs of the Latin-script languages of the digital gold set, each file of a pair scanned independently under a seeded degradation. Set sha256 `6fe36af35ea8c1c3bf02605e619503a36ff3ffe3e3d5c60d847ed7b7c1b33d0f` (1,200 PDFs plus block maps, 1.3 GB, not committed; rebuilt byte for byte by `OneDiff.GoldSets.Scan build` from the digital set and seed 20261104). The documents are all in the eval role: none shares a base document with the dev set that was used for every tuning decision (`check-split`: 210 and 42 base documents, 0 shared).

## Exact gates

Run through `OneDiff.GoldSets.Scan gate`: every pair read with the OCR source and compared under Legal and Strict; Verify passes with fallback 0; `Check` passes; the JSON carries the flags back; both marked PDFs (old and new) are re-read and the text under every mark is the changed text; a sampled pair is run twice for identical bytes.

| Gate | Cases | Failures |
|---|---|---|
| Verify passed, fallback level 0 | 600 pairs × Legal/Strict | 0 |
| Flags and confidences round-trip through JSON | 32,646 changes | 0 |
| Marks re-extracted, new and old | 54,334 marks | 0 after the band rule below (5 comparisons in 3 pairs without it) |
| Determinism (two runs, same bytes) | 100 pairs | 0 |
| x64 and arm64 digest (JSON and marked bytes) | 20 pairs (every 30th) | equal |

The band rule: a mark across a skewed line is a band from the line's first word to its last, so a word in between can stick out of it by a fraction of its height. The gate counts a word as under a band when its centre is within a quarter of the word's height. Without the allowance 5 of the 1,200 comparisons in 3 pairs fail (words at skewed line ends whose centres lie 0.2 pt above the band); with it all pass.

## Rows

Eval set, 120 documents (30 per language), 10,000 paired-bootstrap resamples over documents, 95% intervals. "Digital" is the same pairs read from the digital PDFs by the PDF source. All on the default settings (flag threshold 0.95).

| Row | Scan | Digital |
|---|---|---|
| align F1 same | .704 [.695,.713] | .950 [.945,.954] |
| align F1 edited | .500 [.470,.528] | .944 [.931,.957] |
| align F1 moved | .321 [.258,.381] | .847 [.788,.900] |
| align F1 unrelated | .052 [.043,.063] | .438 [.385,.490] |
| boundary F1 | .237 [.216,.259] | .879 [.862,.895] |
| boundary P / R | .140 / .767 | .855 / .904 |
| frag.sentence / frag.unit | 1.046 / 1.112 | 1.006 / 1.163 |
| missed.sentence | .095 [.076,.114] | .026 [.018,.034] |
| noise.changes.share | .865 [.850,.878] | .341 [.308,.372] |
| noise.changes.per10kchars | 46.7 [43.1,50.6] | 4.6 [4.0,5.3] |
| ocr.flag.P (flagged changes that no real edit explains) | .932 [.922,.941] | — |
| ocr.flag.R (such changes that are flagged) | .620 [.589,.649] | — |
| verify.pass / fallback | 1.0 / 0 | 1.0 / 0 |

Per language (scan): boundary F1 en .275, de .215, fr .215, es .246; noise share .845, .866, .886, .857; flag recall .535, .596, .707, .611.

### Reading the gap

The targets set for the dev set (noise share within .10 of digital, boundary F1 within .15, flag recall ≥ .80 at precision ≥ .90) are **not met**:

- **Cause: recognition errors, not segmentation.** On the dev set, the same pairs scanned *cleanly* (300 dpi, no blur, skew, speckle or compression) give boundary F1 .557 and noise share .494; the degraded scans give .26 and .84. What the recognizer gets wrong on a blurred, skewed or compressed page (a letter, a word, a dropped line, a stray mark) is a real difference between the two extractions, so the guarantee requires showing it. Hiding it would break the promise; the flag is how the model says "probably not an edit".
- **Segmentation was cut where it could be, without hiding text.** Soft block boundaries, clause numbers as list items, tables off by default, running furniture matched by letter similarity and page numbers folded took the dev boundary recall from .73 to .78 and missed sentences from .150 to .067; boundary precision stays low because every recognition error is an extra change.
- **Flag trade-off.** Word confidence alone cannot give both: at threshold .99 recall is .83 at precision .85 (dev). The default .95 takes the precision target (.93 on eval) and reaches recall .62. A change whose words were read with high confidence but differ because the other scan dropped or garbled them is not caught by confidence. A flag from the *other* side's alignment (this block has no counterpart anywhere on the page) would need the aligner.

### Aligner needs (findings, not changes)

- OCR segmentation differences should be recovered as split/merge links: align F1 split .055 and merged .109 on scans against .79 and .77 digital. A paragraph that one scan cuts in two and the other keeps whole is a split the aligner does not see because the halves' text overlaps the whole only partly after recognition errors.
- Table cells read inconsistently across scans produce moves of short blocks (moved precision .20): short repeated cell texts need the structural tie-break that the digital source already gets.
- Clause markers such as `1.1` are not list markers in the shared PDF text helper; the scan source handles them itself.

## Cost

A scan pair costs the recognizer's time plus the comparison. Indicative, one thread of the reference worker (AMD Ryzen 9 7950X3D, 32 logical cores, load about 17 before and after, 4 cores reserved), the 12 largest eval pairs (median 6 pages for the pair): recognition of both scans median 2.15 s (0.36 s per page), compare 3 ms, marking both pages 6 ms; the worst compare plus marking is 153 ms. Compare plus marking is far inside the 1 s budget on top of recognition. Not a bench-class run.

## Dev set and threshold sweep (for the record)

Flag threshold sweep on the dev set (120 pairs; used to choose the default; eval was never used for it):

| threshold | precision | recall |
|---|---|---|
| 0.80 | .946 | .408 |
| 0.90 | .939 | .484 |
| 0.95 | .898 | .588 |
| 0.97 | .877 | .676 |
| 0.98 | .869 | .717 |
| 0.99 | .851 | .835 |
| 0.995 | .844 | .872 |

## Environment

Worker: AMD Ryzen 9 7950X3D, 32 logical cores, .NET SDK 10.0.403, OneOcr 1.0.0, OneOcr.Pdf 1.0.0, OneImage 1.10.x, OnePdf 1.18.0-preview.3; gate chunks of 300 pairs took about 3 minutes each at 8 cores. arm64: arm64 Mac mini, same SDK line. Full solution: 685 tests, 0 failed (676 passed and 9 environment-gated skips on x64; 674 and 11 on arm64), 0 build warnings. NativeAOT smoke app (linux-x64, `-warnaserror`) publishes without warnings (35.6 MB) and runs `ok`, scan check included.
