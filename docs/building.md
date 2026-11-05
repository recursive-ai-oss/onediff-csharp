# Building, CI and releases

## Building

```
dotnet build OneDiff.slnx -c Release
dotnet test --solution OneDiff.slnx -c Release --no-build
```

The SDK version is pinned in `global.json`. Build output goes outside the checkout: set `ONEDIFF_ARTIFACTS` to choose the folder. Without it, output goes to `onediff/build` under the user's local application data folder (`~/.local/share` on Linux and macOS, `%LOCALAPPDATA%` on Windows), or under the temp folder when there is no such folder.

## Sister-library previews

Some packages depend on a prerelease of another Recursive AI library (OnePDF, OneFont, OneNLP, OneOffice with its OneArchive and OneCompress) that is not on nuget.org yet. The pins are the prerelease `RecursiveAI.*` entries in `Directory.Packages.props`. To restore them, point NuGet at a folder or feed that holds them:

1. Copy `docs/nuget-local.config.example` to `NuGet.Config` in the repository root (the path is not tracked).
2. Set `ONEDIFF_PREVIEW_FEED` to the folder (or feed URL).

Only the sister-library packages named in that file come from the preview source; everything else comes from nuget.org. Once the previews are published, the pins move to the released versions and none of this is needed. The OnePDF and OneFont pins are two properties at the top of `Directory.Packages.props`, `OnePdfVersion` and `OneFontVersion`; moving them to the released `1.18.0` and `1.4.0` is a two-line change (the CI selector reads them too).

## CI

`.github/workflows/ci.yml` runs on every push and pull request:

- **build**: build and test on linux-x64, linux-arm64, osx-arm64 and win-x64; publish and run the NativeAOT smoke app on each with warnings as errors.
- **pack**: packs every packable project as a workflow artifact (nothing is pushed anywhere).
- **wasm**: publishes the core tests as a browser WebAssembly app and runs them in headless Chrome.
- **plan**: decides what the jobs above build, from the preview pins.

Hosted runners cannot restore a prerelease that is not on nuget.org, so `plan` runs `tools/ci/select-solution.py`:

- With no prerelease pins, or with the repository variable `ONEDIFF_PREVIEW_FEED` set to a feed the runners can reach, everything is built (`OneDiff.slnx`) and the preview config above is written into each job.
- Otherwise the jobs build `OneDiff.Hosted.slnf`, generated on the fly: every project of the solution that does not depend, directly or through project references, on a prerelease package. The run prints a notice naming the projects it left out, so a green run never silently covers less than it says. The AOT smoke app runs only if it is among the projects built.

## Releases

A package **packs** when its project is under `src/` and `IsPackable` is true: `Directory.Build.props` makes everything non-packable, `src/Directory.Build.props` makes projects under `src/` packable, and a project whose package has no content yet sets `IsPackable` to false until it does. A local `dotnet pack OneDiff.slnx` packs all of them.

A package is **published to nuget.org** only when its project also sets `PublishToNuGet` to true (`src/Directory.Build.props` sets it to false). That is the one list of what the release publishes: the packages whose dependencies are all on nuget.org. `.github/workflows/release.yml` builds and tests the solution that restores without prerelease packages, packs each published project, and checks that it produced exactly one package per project marked `PublishToNuGet`. A package joins a release by setting the property; it must not depend on a prerelease.

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
