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
