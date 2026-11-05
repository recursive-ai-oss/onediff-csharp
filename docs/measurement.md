# Measuring OneDiff

How the exactness gates and the speed rows are reproduced. Everything here is tooling: it lives in `tools/` and `bench/`, nothing in it ships in a package, and the comparators and reference tools it uses are only ever executed or called through their public interfaces.

## Reference tools

`tools/install-oracles.sh [ROOT]` installs pinned released binaries under `ROOT/oracles` (default `$ONEDIFF_HOME`, else `~/.cache/onediff`) without touching the system:

| Tool | Version | Source |
|---|---|---|
| git | 2.51.2 | conda-forge binary through a user-local micromamba |
| GNU diffutils (diff, diff3) | 3.12 | same |
| GNU patch | 2.8 | same |
| diff-match-patch (Python) | 20241021 | PyPI wheel (`--only-binary`) |

The script prints each tool's own version line and exits non-zero if one differs from its pin. Linux x64 and macOS arm64 are supported. The GPL tools are run as processes by the harness and the benchmarks and are never referenced from a package.

## Corpora

`tools/fetch-corpora.py [--root DIR] [repos] [wiki] [synth]` builds the text and merge corpora under `DIR` (default `$ONEDIFF_CORPORA`, else `$ONEDIFF_HOME/corpora`). Nothing is committed.

- `text/<source>/<n>/{old,new}`: version pairs from the history of six permissively licensed repositories, from Wikipedia revisions (seven languages) and from a seeded set of edge cases (emoji and joiners, combining marks, CRLF/LF mixes, missing final newline, a 1.2 MB single line, a 150k-line file, empty and identical sides).
- `merge/<repo>/<n>/{base,ours,theirs}`: three-way triples from real merges that changed the same file on both sides.
- A licence or terms file beside every source, and `manifest.json` with a SHA-256 for every file.

`tools/corpora-pins.json` pins every repository to a commit and every Wikipedia page to a list of revision ids, so a fresh fetch yields the same files. The manifest's own SHA-256 is the corpus identity; it is printed at the end of the fetch and recorded in every gate report.

## Exactness gates

`tools/OneDiff.Oracles` is a console tool that runs the reference tools as black boxes over the corpus:

```
dotnet run -c Release --project tools/OneDiff.Oracles -- run \
    --corpus ~/.cache/onediff/corpora --oracles ~/.cache/onediff/oracles --out out --cores 8
```

| Gate | Checks |
|---|---|
| `min-edit` | our Myers edit distance on lines equals `diff --minimal`'s (lines end at `\n`, keeping their terminator, as GNU diff cuts them) |
| `validity` | for every algorithm and for line, word and character tokens: the script is well formed, equal runs are equal, applying it gives new and undoing it gives old, and no algorithm beats Myers |
| `roundtrip-unified`, `roundtrip-dmp` | our patch, applied by our code, reproduces new |
| `accept-gnu-patch`, `accept-git-apply` | our unified output applied by GNU patch and by `git apply` reproduces new |
| `dmp-interop` | our diff-match-patch patch text applies in the Python package and the package's patch text applies in ours |
| `merge-file:merge`, `:diff3`, `:zdiff3` | our three-way merge equals `git merge-file -p` byte for byte in each conflict style, with the conflict count; each mismatch keeps a unified diff in `merge-mismatch/` |

The patch and merge gates call the library through one file, `tools/OneDiff.Oracles/Seam.cs`. Until the library has the feature its gates report `pending`; wiring it is filling in the seam's members and returning true from `Has`. `--seam oracle` answers from the oracles themselves, so every gate must pass against it; that tests the gate plumbing.

Cases run in parallel (`--cores`), are listed in ordinal order, and each finished case is appended to `results-<seam>.jsonl`; rerunning with the same `--out` skips them (`--fresh` starts over). `report.json` has no timings and is byte-identical across runs. Oracle timeouts and inputs too large for the Python package are counted as skips, never as passes. Exit code 1 means some gate has failures.

## Benchmarks

`bench/OneDiff.Bench` holds the BenchmarkDotNet suites. The comparators, DiffPlex 1.9.0 and DiffMatchPatch 5.0.0 (the diff-match-patch authors' C# package), are restored for this project only. Inputs are corpus pairs of about 2 KB, 40 KB and 400 KB (the nearest pair available, named in the report), read at run time.

| Area | Ours | Comparators |
|---|---|---|
| lines | `Diff.Lines` per algorithm | DiffPlex line differ; diff-match-patch line mode |
| words | `Diff.Words` per algorithm | DiffPlex word differ |
| chars | `Diff.Characters` per algorithm | DiffPlex character differ; diff-match-patch `diff_main` |
| cleanup | semantic and efficiency | diff-match-patch cleanup |
| patch | make, toText, fromText, apply | diff-match-patch patches |

Settings, so the work is the same on both sides:

- Everything is single-threaded, workstation non-concurrent GC.
- No cleanup in the diff rows: each implementation returns its raw edit script.
- diff-match-patch runs with `Diff_Timeout = 0` (no time limit), so it finds the full diff. Its line mode maps each distinct line to one character, diffs the characters and reads the result in lines; the package keeps its own line mapping non-public, so the mapping is done in the benchmark, inside the timed work.
- DiffPlex's word differ cuts words at the separators it is given (spaces, line ends and common punctuation), so the word rows do not see identical token streams. `info` prints the edit distance each implementation finds on each input; lines and characters agree between DiffPlex and diff-match-patch.
- Cleanup rows start each call from a copy of the same diff, and the copy is timed on both sides.
- Rows whose library side does not exist yet are not run and show as pending; rows on stand-in algorithms run but are marked as placeholders and draw no verdict.

`bench/run-bench.sh OUT [BenchmarkDotNet args]` runs everything: the suites (with `ONEDIFF_BENCH_MODE=smoke` for a short indicative pass), the input and edit-distance listing, peak working set per row (one cold run per child process, reported above a load-only baseline), and `results-table.md`: per size, ours against each comparator with time ratio, allocations and allocation ratio, a geometric mean per area and comparator, and the verdict against the bars (geomean at most 1.0, no row above 1.25). `bench/record-run.sh` writes the CPU model, core count, instruction-set flags and the load average before, during and after into the same folder. Timings from a machine that was doing other work are not results.

## Restoring the sister-library previews

`RecursiveAI.OneDiff.Pdf` builds on a preview of `RecursiveAI.OnePdf` that is not on nuget.org yet. Until it is, restore needs one extra package source that is kept out of the repository:

1. Put the preview `.nupkg` files (`RecursiveAI.OnePdf`, `.Layout` and `.Fonts` at the version in `Directory.Packages.props`) in a folder and point `ONEDIFF_PREVIEW_FEED` at it.
2. Copy `docs/nuget-local.config.example` to `NuGet.Config` at the repository root. The file is not tracked, so no machine path ever lands in a commit. It maps only the OnePDF package ids to that folder and everything else to nuget.org; the mapping also keeps central package management from warning about several sources.
3. `dotnet restore` as usual. Without the file, restore of the PDF projects fails with an unresolved package version; every other project restores from nuget.org alone.

Hosted CI cannot restore the PDF projects until the version is published.

## Gold sets

The document engine is measured against labelled pairs. Three kinds of data exist, and each has a fixed role: **synthetic pairs with exact labels** (the scored set), **the same pairs rendered to PDF plus real version pairs** (the PDF gate set and the exact gates), and **real pairs without labels** (invertibility, fallback and re-extraction only). Nothing in this section is committed except the tools; data lives under `~/onediff/eval/` (evaluation) and is regenerated from seeds and pins.

### Synthetic pairs

`tools/OneDiff.GoldSets generate --out DIR [--role eval|dev|train] [--seed N] [--docs-per-lang N] [--verify]` writes labelled pairs.

- **Documents** are contract-like and written for the generator, in seven languages (English, German, French, Spanish, Chinese, Japanese, Arabic): a title, a preamble, three recitals, a definitions article, five to eight articles of numbered clauses (one to three sentences each), a fee table and a closing line. Sentences come from twelve templates per language with two phrasings each; amounts, durations, dates, percentages, jurisdictions, one defined term and cross-references (`Section 3.2`) are filled in from small value lists. Nothing is copied from any real document.
- **Edits** (twenty types) each produce labelled change units: `term-swap`, `value-amount`, `value-date`, `value-duration`, `value-percentage`, `clause-rewrite` (the same content in the other phrasing), `sentence-insert`, `sentence-delete`, `para-insert`, `para-delete`, `move`, `move-edit` (moved and changed), `cascade` (a clause inserted before a referenced one, so labels and cross-references renumber), `term-rename` (the defined term changes everywhere), `split`, `merge`, `format` (bold on a word), `table-cell`, `table-row-insert`, `table-row-delete`. Renumbering that follows from any edit is labelled too (`renumber` units and `cascade-reference` units, group `cascade`).
- **Pairs.** Per base document: one mixed pair (`-m`, four to eight edits) and four single-type pairs (`-s1` to `-s4`, one to three edits of one type; the types rotate so each is covered in every language). A pair never edits the same sentence twice, so units never overlap.
- **Split.** Per language 60 base documents are shuffled with a seeded generator and cut into 30 `eval`, 6 `dev` and 24 `train` documents. All pairs of a base document share its role. The split does not depend on which role is written: `split.json` lists every document with its role and the SHA-256 of its base text.
- **Files.** `pairs/<id>.docs.json` (old and new document as compare-model nodes: kind, parent, text, attributes, formatting runs; a node's id is its position) and `pairs/<id>.labels.json`; `split.json`; `manifest.json` (every file's SHA-256, counts per language and edit type, the SHA-256 of `split.json`); `manifest.sha256`, the **set hash**. The same seed gives the same bytes.
- **Labels.** `links`: content blocks (paragraphs and list items) by node id, kind `match`, `move`, `split` or `merge`; `deleted` and `inserted` content blocks; `units`: each human-meaningful change with its `type`, `group` (`rename`, `cascade`), `typed` value (kinds `amount`, `date`, `duration`, `percentage`, `term`, `modal`/`adverb`/`jurisdiction` swaps, `reference`, `number`; values in a language-neutral canonical form such as `USD 12500`, `30 d`, `2027-03-01`) and its character spans on each side; `sentences`: the changed gold sentences. A unit without spans (`split`, `merge`, `renumber`) is structural.
- **Sanity gate.** `--verify` runs every pair through the current pipeline with nothing hidden and requires `Verify` to pass at fallback level 0. The generator also refuses a pair in which a difference between the documents is not covered by a unit.

### Keeping evaluation out of training

Evaluation data is written only under `~/onediff/eval/`; training data under `~/onediff/train/`, and no tool reads across. `generate --role train` never writes eval pairs, and the roles of a seed are fixed by the shuffle above, so a training set cut from `--role train` of the **same seed** is disjoint from the eval set by construction. `check-split --a EVALDIR --b TRAINDIR` proves it for any two directories: it compares the SHA-256 of every base document and exits 1 on a shared one. Distillation and head training load `train` and `dev` only. Real pairs and the PDF renderings of eval pairs are eval. The eval manifest (set hash) is recorded before any model sees training data.

### The PDF gate set

`tools/OneDiff.GoldSets.Pdf render --data DIR [--real DIR] --out PDFDIR` renders every synthetic pair (and every real pair) twice, old and new, with OnePDF's layout engine. The layout is chosen from the pair id (four variants: a single justified, hyphenated column on A5; two columns on A5 with a header; one wide-margin column on A4 with a header; two narrow columns on A5 with a header), all with a "Page n / N" footer, tables, lists, page- and column-spanning paragraphs, soft-hyphenated line ends, CJK (Noto Sans CJK) and Arabic (Noto Sans Arabic, right to left). The file id is derived from the file name, so equal input gives equal bytes. After rendering, the text is extracted again with OnePDF and every content block is looked up in order; `pdf/<id>.blocks.json` keeps node id, label, text and page per block, and the manifest reports how many blocks were located. A block that is not found is a limit of extraction (a title word split across lines, a percent sign in right-to-left text), not a missing label. The gold node ids of a pair are the ids of the source documents; a PDF source numbers its own blocks, so scoring a PDF comparison maps blocks by text through `blocks.json`.

### Real pairs

`tools/fetch-real-pairs.py` fetches version pairs into `~/onediff/eval/real-pairs-v1` and records everything in `tools/real-pairs-pins.json` (URLs, repository commits, EDGAR documents, each with the SHA-256 it had; a changed document stops the fetch). Real pairs carry no labels: they exercise invertibility, fallback and re-extraction. Sources and terms are in the script's header; nothing fetched is committed. SEC requests are paced at 4 per second under a declared user agent (`ONEDIFF_SEC_CONTACT`).

## Scoring

`tools/OneDiff.GoldSets score --data DIR --out FILE [--pipeline NAME]` runs the reference pipeline with nothing hidden (`CompareSettings.Strict`, no throwing), or reads `Comparison` JSON from `--cmp DIR` (`<id>.cmp.json`), checks every file against the manifest, scores each pair and writes rows. A row is a metric on a scope (`all`, `lang:xx`, `mixed`, `edit:<type>`) with its value, a bootstrap 95% interval, the number of documents and pairs, the set hash and the pipeline name.

- **Content blocks** are paragraphs and list items; table, row, cell and section nodes carry no text and are not scored.
- **Alignment F1 per link type.** A link is typed from its alignment kind and texts: `same` (a match with equal text), `edited` (a match with different text), `moved`, `split`, `merged`; a content block with no link is `unrelated` (deleted or inserted). A predicted link is correct when its type and its sets of old and new node ids equal a gold link's. Precision, recall and F1 per type, from counts summed over documents.
- **Boundary F1.** A change unit's spans are trimmed of white space; the boundaries are the (side, node, offset) of every span start and end. Predicted spans come from the engine's changes of all kinds except attribute, split and merge (those have no text span); a side of a change is mapped to its nodes' text. Boundary precision, recall and F1 are over exact matches.
- **Fragmentation.** Two counts are kept apart, because a display region can be as large as one likes and still look like "one per sentence".
  - A change's **text extent** is the union of its sides' trimmed spans. A move change spans its whole node, so it has an extent only for a gold move unit; the edits inside a moved node are changes of their own. Attribute, split and merge changes have none.
  - A **changed gold sentence** is a sentence of the new document that carries a text change (of the old document, for deleted text). A gold unit's target is its spans.
  - `frag.sentence` is the mean, over changed sentences touched by at least one change, of the number of **changes** whose extent overlaps the sentence; `frag.unit` is the same per gold unit (the changes overlapping any of its spans; a gold move unit counts move changes). 1.0 means one change per sentence or unit; more means a change is shown in pieces. This is the fragmentation row.
  - `regions.sentence` and `regions.unit` are the same means over **display regions** (change groups) that hold a counted change. They say how the pieces are presented, never how many there are: a whole-paragraph region holding five changes is five changes and one region. Read `frag` for unit quality and `regions` for display.
  - `missed.sentence` is the share of changed sentences no change touches.
  - Fragmentation is meaningful only beside boundary precision: one unit spanning a whole paragraph also has one change per sentence, and pays in `boundary.P`. No row is read alone.
- **Pairs with no difference.** A pair whose two documents are identical (the generator can undo a defined-term rename with a second one) has nothing to compare, whatever its unit labels say. Its unit and sentence labels are left out of the boundary and fragmentation counts and `pairs.nodiff` reports how many pairs were left out. Alignment rows still count them.
- **Aggregation.** Counts are summed per base document (all pairs of the document in the scope); documents are the resampling unit. The interval is the percentile bootstrap (2.5% and 97.5%) of the metric recomputed on 10,000 resamples of the documents with replacement, seed 20261104. The tool also prints the per-document distribution (minimum, median, 90th percentile, maximum) of boundary F1, changes per changed sentence and regions per changed sentence. `--resamples N` changes the resample count (0 skips the intervals, for sweeps that compare point values); every reported row uses 10,000. `<out>.missed.txt` lists, per pair, the changed sentences no change touched. A metric whose denominator is zero in more than 10% of resamples has no interval.
- **Verification** is scored too: `verify.pass` and `verify.fallback` are the shares of pairs that passed and that needed a coarser grouping.

## DOCX gold set and comparison tools

The DOCX set is the evaluation split of the synthetic pairs written as Word files, so the labels carry over exactly. Everything here is indicative until a final run is published (`docs/results/docx-v1-comparators.md`).

### Rendering and block maps

`tools/OneDiff.GoldSets.Docx render --data DIR --out DIR` writes `docx/<id>.{old,new}.docx`, `<id>.blocks.json` and a manifest whose hash is the set identity. Packages are assembled from OOXML parts and opened and read back through OneOffice, which is also how list labels are checked (`labelsOk` equals `labelsTotal` on every side). Six looks, picked by a hash of the pair id, so each language and edit type appears in each: three plain looks (different fonts, page set-up and table styles), one with bookmarks and cross-reference fields, one with a footnote and a comment on the title, one with a content control around the title. Every look has numbering, a table with a header row and a merged-cell signature box, a header with a picture and a footer with page-number fields.

A block map lists, per side, the gold content blocks in order (`node`, `kind`, `label`, `text`, and for PDFs the page). PDF and DOCX renderings write the same shape. The scorer maps a source's content nodes onto gold blocks by the longest common subsequence of equal texts after dropping white space, soft hyphens and direction marks, so order is kept and repeated texts pair up in order; a block's list label may prefix the text (a PDF prints it). Offsets are mapped by counting the characters kept. A source node that matches nothing gets an id past the gold nodes, so lookups never fail and the node counts as unmatched in the printed `block map: matched/total` line.

- `score --cmp DIR --blocks DIR` maps comparisons made from rendered files (read as `<id>.cmp.json`); `OneDiff.GoldSets.Pdf compare --pdf DIR --out DIR [--every N]` makes them from PDFs.
- `score --changes DIR --blocks DIR` maps another tool's output, read as `<id>.changes.json` (schema `onediff.changes/1`: two line views and the changes as line intervals). A missing file scores as an empty result and is counted as "pairs without output". `--ids FILE` limits the pairs.

### Tracked-changes outputs

`OneDiff.GoldSets.Docx read --tool T --tracked DIR --docx DIR --out DIR` reads each tool's DOCX through OneOffice's markup and revision model, writes `<id>.changes.json`, and runs accept-all and reject-all on copies, comparing the paragraph sequence with the new and old input (`roundtrip.jsonl`). The two views are the document with every revision rejected and with every revision accepted, one line per non-empty paragraph; a removed paragraph mark joins a paragraph to the next (ECMA-376 §17.13.5).

A change is a run of adjacent revisions in one paragraph with no non-blank unchanged text between them (white space between two revisions does not split a change); the two halves of a move are one change, paired by the name on their range markers; formatting revisions are not changes. Every change is its own display region, so `regions.*` equal `frag.*` for tool outputs. One package quirk is repaired before reading: some tools type every `.xml` part through one default content type and give the main part no override; the reader adds the override and changes nothing else.

### Comparison tools

Run only as released binaries, from the harness in `tools/OneDiff.Comparators` (in-process .NET packages), `tools/comparators/wmlcomparer.py` (a Python package that carries the engine as a compiled binary; install with `--only-binary` into a virtual environment) and `tools/comparators/libreoffice_compare.py` (headless office through its UNO bridge, run with the Python shipped in the office's program folder). `tools/install-libreoffice.sh` installs LibreOffice 26.8.2 from the release tarball (sha256 checked) under the oracle root, Linux x64 or macOS arm64; the office is only executed. Pins and settings are in `docs/results/docx-v1-comparators.md`. Each runner records one line per pair (`runs.jsonl`: ok, milliseconds, output size, error) and skips recorded pairs, so a run resumes.

`tools/score-docx-comparators.sh` scores each tool on all pairs, on completed pairs only and per look; `tools/summarize-docx-comparators.py` makes the tables. The engine's own row is scored the same way once its Word package writes comparisons: `score --cmp DIR --blocks docx/` and the name `onediff` in the script.

### Real DOCX pairs

`tools/fetch-real-pairs.py --only docx --root DIR` fetches eight pairs of related Bonterms templates (CC BY 4.0) pinned in `tools/real-pairs-pins.json` with their SHA-256. They carry no labels: the gates are that each tool runs, the output opens, and accept-all and reject-all reproduce the inputs.

### DOCX speed

`bench/OneDiff.Bench.Docx` times document in, tracked-changes document out, in process and single threaded, for the in-process tools, on joined gold-set pairs of about 5, 40 and 120 pages (`OneDiff.GoldSets.Docx bench-pairs --data DIR --out DIR --lang en --sizes 1,8,24`). One command runs it with the machine record (CPU, cores, ISA flags, load before and after):

```
dotnet build bench/OneDiff.Bench.Docx -c Release
ONEDIFF_DOCX_BENCH=<bench-pairs folder> bench/run-docx-bench.sh OUT
```

The engine's row is one more `[Benchmark]` method in `DocxBench` with the same shape (open both, compare, save). The out-of-process tools' wall time per pair is in their `runs.jsonl` and is information only, since it includes process start.
