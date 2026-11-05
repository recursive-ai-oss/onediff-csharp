# Building, CI and releases

## Building

```
dotnet build OneDiff.slnx -c Release
dotnet test --solution OneDiff.slnx -c Release --no-build
```

The SDK version is pinned in `global.json`. Build output goes outside the checkout: set `ONEDIFF_ARTIFACTS` to choose the folder. Without it, output goes to `onediff/build` under the user's local application data folder (`~/.local/share` on Linux and macOS, `%LOCALAPPDATA%` on Windows), or under the temp folder when there is no such folder.

## Sister-library previews

The released packages (`RecursiveAI.OneDiff`, `.Documents`, `.Pdf`, `.Ocr` and `.Tool`) depend only on packages published on nuget.org: OneFont 1.4.0, OnePDF 1.18.0, OneImage 1.10.5, OneRender 1.3.3 and oneocr 1.0.0. A fresh clone with nothing configured builds and tests them.

The packages that ship later in 1.x (`.Word`, `.Spreadsheets`, `.Presentations`, `.Rendering`, `.Nlp`, `.All`) and some test and tool projects depend on a prerelease of another Recursive AI library that is not on nuget.org yet (OneDocument with OneArchive and OneCompress, OneNLP). The pins are the prerelease `RecursiveAI.*` entries in `Directory.Packages.props`. Building or testing one of those projects with nothing configured stops at restore with a single message naming the project and the packages, for example:

```
OneDiff.Word needs sister-library previews that are not on nuget.org yet: RecursiveAI.OneDocument 0.2.0-preview.2. Set ONEDIFF_PREVIEW_FEED to a folder or feed that holds them, then restore again (see docs/building.md).
```

**To restore them,** set `ONEDIFF_PREVIEW_FEED` to a folder or feed that holds the `.nupkg` files; `Directory.Build.props` adds it as one more restore source, so no other setup is needed:

```
ONEDIFF_PREVIEW_FEED=/path/to/previews dotnet build OneDiff.slnx -c Release
```

The other ways in: a `NuGet.Config` at the repository root (copy `docs/nuget-local.config.example`; the path is not tracked; the message above is not shown when the file exists and it maps the previews to a folder), or `-p:OneDiffPreviewsConfigured=true` when a source is configured elsewhere, such as the user-level NuGet config.

Once those libraries are published, the pins move to the released versions and none of this is needed (the CI selector reads the pins too).

## CI

`.github/workflows/ci.yml` runs on every push and pull request:

- **build**: build and test on linux-x64, linux-arm64, osx-arm64 and win-x64; publish and run the NativeAOT smoke app (every package's main path: text diff, documents, PDF comparison with on-page markup and rendered output, scans, JSON, HTML, combine) and the NativeAOT `onediff` tool on each with warnings as errors.
- **pack**: packs every packable project as a workflow artifact (nothing is pushed anywhere).
- **wasm**: publishes the test sources of the core, `.Documents`, `.Pdf` and `.Ocr` as browser WebAssembly apps (`tests/OneDiff.Tests.Wasm`, one per suite) and runs each in headless Chrome. A test that needs files, threads, processes or a native oracle carries `[Trait("Host", "Native")]` (or `Assert.SkipUnless`) and is counted as skipped; the run fails on any other failure. Locally: `tests/OneDiff.Tests.Wasm/run.sh <output-folder> [Core Documents Pdf Ocr]`, which needs the `wasm-tools` workload and `uv` or a Python with `playwright`.
- **plan**: decides what the jobs above build, from the preview pins.

Hosted runners cannot restore a prerelease that is not on nuget.org, so `plan` runs `tools/ci/select-solution.py`:

- With no prerelease pins, or with the repository variable `ONEDIFF_PREVIEW_FEED` set to a feed the runners can reach, everything is built (`OneDiff.slnx`) and the preview config above is written into each job.
- Otherwise the jobs build `OneDiff.Hosted.slnf`, generated on the fly: every project of the solution that does not depend, directly or through project references, on a prerelease package. The run prints a notice naming the projects it left out, so a green run never silently covers less than it says. The AOT smoke app runs only if it is among the projects built.

## Releases

A package **packs** when its project is under `src/` and `IsPackable` is true: `Directory.Build.props` makes everything non-packable, `src/Directory.Build.props` makes projects under `src/` packable, and a project whose package has no content yet sets `IsPackable` to false until it does. A local `dotnet pack OneDiff.slnx` packs all of them.

A package is **published to nuget.org** only when its project also sets `PublishToNuGet` to true (`src/Directory.Build.props` sets it to false). That is the one list of what the release publishes: `RecursiveAI.OneDiff`, `.Documents`, `.Pdf`, `.Ocr` and `.Tool` (the `onediff` command), the packages whose dependencies are all on nuget.org once the sister libraries are released. The release also checks that the packed set is exactly these five and that each carries both licences, the third-party notices and its readme. `EnablePackageValidation` is on in every one of those projects. `.github/workflows/release.yml` builds and tests the solution that restores without prerelease packages, packs each published project, and checks that it produced exactly one package per project marked `PublishToNuGet`. A package joins a release by setting the property; it must not depend on a prerelease.

Each package has its own `PACKAGE.md` beside its project; it is the readme shown on nuget.org, so its links are absolute.

All packages and the tool share one version, `<Version>` in `Directory.Build.props`. The workflow runs when a `vX.Y.Z[-suffix]` tag is pushed and has three jobs:

1. **verify**: the tag is well formed, its commit is on `main`, CI passed on that commit, and `CHANGELOG.md` has a `## [X.Y.Z]` section.
2. **publish**: every packable project builds the tagged version; the solution is built and tested; the packages are packed from those binaries, attested, and pushed to nuget.org with `--skip-duplicate` (the `.nupkg` files first, then the `.snupkg` symbol packages) using Trusted Publishing, so no API key is stored.
3. **release**: a GitHub release with the changelog section as notes and the packages attached. A version with a suffix (`-preview.1`) is marked as a pre-release.

Every action is pinned to a commit. The workflow also honours `ONEDIFF_PREVIEW_FEED` for restoring previews that are not published.

## Packing locally

```
dotnet pack OneDiff.slnx -c Release -o <folder>
```

Packs are built with `Deterministic`, and with `ContinuousIntegrationBuild` when `CI=true`. Every entry of a package (the assemblies, documentation files, licence files and the `.nuspec`) has the same contents on repeated builds. The `.nupkg` and `.snupkg` files as a whole do not hash the same, because NuGet stamps each zip entry with the file's modification time and the pack time. Compare two packages by their entries, not by the file hash:

```
for f in a.nupkg b.nupkg; do unzip -p "$f" | sha256sum; done
```
