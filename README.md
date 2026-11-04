# OneDiff

**Text diff and document comparison for .NET.**

OneDiff is a clean-room diff and comparison library for .NET: Myers, patience and histogram diff over any sequence, line, word, character and grapheme tokenizers, patches and three-way merge, with document comparison to follow. It targets `net10.0`, uses no native code and is designed to be NativeAOT-, trimming- and WebAssembly-friendly.

> **Preview.** This is an early preview, and APIs may change before 1.0. Measured results, including the rows that don't meet their bar yet, will be listed in `docs/results.md`; nothing beyond that page is claimed.

This project is unaffiliated with any other project named OneDiff, including the Python diffusion-model accelerator of that name.

## Packages

| Package | Contents |
|---|---|
| `RecursiveAI.OneDiff` | The text core: diff algorithms, tokenizers, cleanup, unified and diff-match-patch patches, three-way merge. Depends on nothing beyond the .NET base class library. |
| `RecursiveAI.OneDiff.Documents` | The document comparison engine (preview): compare model, alignment, verification, change list and HTML redlines. |

Further document packages (`.Pdf`, `.Word` and others) arrive later.

## Example

```csharp
using OneDiff;

TextDiff diff = Diff.Lines(oldText, newText);
foreach (Edit edit in diff.Script)
{
    Console.WriteLine($"{edit.Kind}: {diff.OldSpan(edit)} -> {diff.NewSpan(edit)}");
}
```

## Building

```
dotnet build -c Release
dotnet test --solution OneDiff.slnx -c Release
```

Build output goes outside the checkout; set `ONEDIFF_ARTIFACTS` to choose where. See [docs/building.md](docs/building.md) for CI, packing and preview dependencies.

## License

Dual-licensed under either of [MIT](LICENSE-MIT) or [Apache-2.0](LICENSE-APACHE), at your option. © Recursive AI, LLC.
