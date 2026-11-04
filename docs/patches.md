# Patches

How OneDiff reads, writes and applies unified diffs and diff-match-patch patch text. The code is in `src/OneDiff/Patching`; every rule below is checked by the gates in [measurement.md](measurement.md).

## Unified diff

The format is the GNU unified format (GNU diffutils manual, "Unified Format" and "Detailed Unified").

**Lines.** Text is cut at `\n` only. Each line keeps its terminator, so `\r\n` files round-trip byte for byte and a lone `\r` is ordinary content. A last line without a terminator differs from the same line with one, which is what makes `\ No newline at end of file` come out right.

**Making.** Changes whose gap of unchanged lines is at most twice the context length share a hunk. The default context is three lines; zero is allowed. A side with no lines in a hunk names the line before it (`-0,0`, `+5,0`). A count of one is left out (`-3`). Names on the `---`/`+++` lines are the caller's; timestamps appear only when given. Identical inputs give the empty string. A name containing a control character, quote or backslash is written in git's quoted form.

**Parsing.**
- Text before the first `---`/`+++` pair and between files that is not a header is skipped, as the patch manual says. Git's extended header lines (`diff --git`, `index`, mode, rename and similarity lines) directly before a file are kept verbatim on that file. Binary and rename-only sections are not supported and are skipped.
- A hunk header is `@@ -a[,b] +c[,d] @@` followed by an optional section heading. Numbers above about a billion are rejected. A hunk takes exactly the lines its counts announce; fewer is a truncated patch, more is an unexpected line. An empty line inside a hunk is an empty context line. `\ No newline at end of file` removes the terminator from the line before it.
- Trailing carriage returns on header lines are dropped. A `@@` line outside a file's hunks, or a `---` line without `+++`, is an error.
- Input over 2^28 characters is refused. Memory use is proportional to the input; no allocation depends on a number in the input.

**Applying.** Hunks are matched against the text line by line, exactly. For each hunk:
1. It is looked for where its header says, shifted by the offset the previous hunk was found at, then at growing distance, later lines before earlier ones at the same distance.
2. If it does not match, context lines are ignored. *Fuzz n* leaves at most (longest context − n) lines at each end, so a hunk whose contexts differ (because it sits at the edge of the file) loses lines from its longer side first. At most two levels by default.
3. A hunk with less leading than trailing context (after fuzz) must match at the top of the file; the mirror case at the end.
4. A hunk may not match where its ignored leading context would fall before the first line. Ignored trailing context may run past the last line.
5. A hunk never matches before the lines earlier hunks used.
6. A hunk that matches nowhere is rejected, with the reason `ContextMismatch`, or `AlreadyApplied` when its new lines are found where its old lines are not. Applying never throws for a conflict; the rejected hunks are returned as a patch.

Rules 2–4 are not in the patch manual. They were settled by comparing results with GNU patch 2.8 (run as `patch -f`, which never asks and never assumes a reversed patch) on thousands of generated drifted files (`tests/OneDiff.Tests/Patches/GnuPatchAgreementTests.cs`).

**Reverse.** `Reverse` swaps the sides of every hunk; applying a reversed patch to the new text gives the old.

## diff-match-patch patch text

The format and the matching are those of diff-match-patch's published wiki ("Unidiff" and "API") and of Wu and Manber, "Fast text searching allowing errors" (1992).

**Format.** Each patch is `@@ -s1[,l1] +s2[,l2] @@` and one line per diff: a space, `-` or `+` followed by the text, URI-encoded as UTF-8 with the space left as it is. A length of one is left out; with a length of zero the start is written as it is, otherwise it is one-based. The context is rolling: the starts of a patch are those of the text after the earlier patches were applied. A lone surrogate is written as the three bytes its code unit would take, and read back the same way. Reading takes the starts from the headers and recomputes the lengths.

**Offsets.** Starts count UTF-16 code units by default (the JavaScript, Java and C# ports). The Python package counts code points; `DmpOffsetUnit.CodePoint` makes `Make` and `Apply` use them, which is needed to exchange patches with it for text outside the Basic Multilingual Plane.

**Making.** The character diff trims common ends, compares long texts line by line first, and never splits a surrogate pair. No readability cleanup is applied. Context grows by the margin (default 4) until the patch's old text is unique in the text, up to the match limit, plus one more margin.

**Applying.** The text is padded at both ends, patches longer than `MatchMaxBits` (default 32) are split, and each patch is placed by `Match` near where it expects to be, shifted by the drift of the patches before it. A patch whose old text is longer than the limit is located by its first and last stretch. When the found text differs from the expected text, the patch's edits are mapped through a diff between the two; a long deletion over text that differs by more than `PatchDeleteThreshold` is refused. Per patch the result says applied, not found, or too different. Cost is the text length times the number of patches.

**Match.** An exact hit at the expected place wins; otherwise Bitap scores each candidate as errors / pattern length + distance / `MatchDistance` and takes the best one under `MatchThreshold` (defaults 0.5, 1000).

## Limits and errors

Both parsers return a typed error (`PatchParseError`: kind, line, offset) through `TryParse`/`TryFromText`, or throw `PatchFormatException` from `Parse`/`FromText`. Nothing else is thrown for bad patch text. The parsers and appliers are fuzzed (`fuzz/OneDiff.Fuzz`).
