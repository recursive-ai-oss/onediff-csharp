# Text output: terminal and Markdown

Renderers over a line-token `TextDiff`. Output is a pure function of the diff and the renderer's settings: the same input gives the same bytes on every platform. Every output line ends in `\n`, whatever the input's line endings.

## Hunks (both formats)

- Lines are the line tokens of the diff. A line's terminator (`\n`, `\r\n` or a lone `\r`) is not shown.
- A change is surrounded by `ContextLines` equal lines (default 3). Two changes whose gap is at most twice that share one hunk, so no line appears twice. With 0 context every change is its own hunk.
- A hunk starts with `@@ -a,b +c,d @@`: one-based first line and line count on each side. A count of 1 is written without `,1`. An empty range names the line before it (`-0,0` at the start of the text).
- Lines are prefixed `' '` (context), `-` (only in the old text) and `+` (only in the new text). Deletions of a changed block come before its insertions.
- A shown line that had no terminator is followed by `\ No newline at end of file`.
- When `OldName` or `NewName` is set, `--- old` and `+++ new` precede the first hunk.
- An identical pair renders as the empty string.

## Terminal

- Colour (default on): hunk headers cyan (`ESC[36m`), file headers bold, deletions red (`ESC[31m`), insertions green (`ESC[32m`), the no-newline note dim; each styled line ends with `ESC[0m`. Colour off writes the same text without any escape sequence.
- Word level (colour on only): in a changed block that has both deleted and inserted lines, the block's old and new lines are diffed word by word and the changed words are wrapped in reverse video (`ESC[7m` … `ESC[27m`). Skipped when either side of the block exceeds 8192 chars, or when under 30% of the longer side (counting non-white-space chars) is shared — those lines are rewrites and marking everything is noise.
- Control characters in the text (below U+0020 except tab, U+007F, U+0080–U+009F) are replaced by a visible picture (U+2400 block, U+2421, U+FFFD) so compared text cannot drive the terminal.

## Markdown

- Line diff: the hunks above in a fenced block opened with `diff`. The fence is three backticks, or one more than the longest backtick run in the content.
- Inline diff (any token kind): the new text, each deletion wrapped in `<del>` and each insertion in `<ins>`. A run that spans line breaks is wrapped per line, with the breaks outside the tags. `&` and `<` are escaped as `&amp;` and `&lt;`; other Markdown is left as written. `<del>`/`<ins>` is used rather than `~~`/`**` because those collide with markup already in the text and cannot be escaped reliably.
