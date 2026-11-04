#!/usr/bin/env python3
"""Chooses what hosted CI builds, depending on whether sister-library previews can be restored.

Some packages are pinned to a prerelease of another Recursive AI library (see Directory.Packages.props)
that is not on nuget.org yet. A hosted runner can restore those only from a preview feed it is told about.

  * No prerelease pins, or ONEDIFF_PREVIEW_FEED set: build the whole solution (OneDiff.slnx).
  * Prerelease pins and no feed: build a solution filter holding every project that does not depend,
    directly or through project references, on a prerelease package. The filter is written to
    OneDiff.Hosted.slnf and the run says which projects were left out and why.

Prints key=value lines for $GITHUB_OUTPUT on stdout (solution, filtered, smoke, filter = the .slnf text on one line) and notices on stderr.
Usage: select-solution.py [repository-root]
"""
import json
import os
import re
import sys
from pathlib import Path

root = Path(sys.argv[1] if len(sys.argv) > 1 else ".").resolve()
solution = "OneDiff.slnx"


def prerelease_pins():
    text = (root / "Directory.Packages.props").read_text(encoding="utf-8")
    pins = set()
    for m in re.finditer(r'<PackageVersion\s+Include="([^"]+)"\s+Version="([^"]+)"', text):
        name, version = m.groups()
        # Our own packages are built here; only sister libraries can be missing from nuget.org.
        if name.startswith("RecursiveAI.") and not name.startswith("RecursiveAI.OneDiff") and "-" in version:
            pins.add(name)
    return pins


def solution_projects():
    text = (root / solution).read_text(encoding="utf-8")
    return [p.replace("\\", "/") for p in re.findall(r'<Project\s+Path="([^"]+)"', text)]


def main():
    pins = prerelease_pins()
    projects = solution_projects()
    feed = os.environ.get("ONEDIFF_PREVIEW_FEED", "").strip()

    if not pins or feed:
        print(f"solution={solution}")
        print("filtered=false")
        print("smoke=true")
        print("filter=")
        if pins:
            print(f"::notice::Restoring {', '.join(sorted(pins))} from the preview feed.", file=sys.stderr)
        return

    packages = {}
    refs = {}
    for p in projects:
        xml = (root / p).read_text(encoding="utf-8")
        packages[p] = {n for n in re.findall(r'<PackageReference\s+Include="([^"]+)"', xml)}
        refs[p] = []
        for r in re.findall(r'<ProjectReference\s+Include="([^"]+)"', xml):
            target = os.path.normpath(os.path.join(os.path.dirname(p), r.replace("\\", "/"))).replace(os.sep, "/")
            refs[p].append(target)

    needs = {p for p in projects if packages[p] & pins}
    changed = True
    while changed:  # a project needs previews when anything it references does
        changed = False
        for p in projects:
            if p not in needs and any(r in needs for r in refs[p]):
                needs.add(p)
                changed = True

    kept = [p for p in projects if p not in needs]
    filter_json = json.dumps({"solution": {"path": solution, "projects": kept}}, separators=(",", ":"))
    (root / "OneDiff.Hosted.slnf").write_text(filter_json + "\n", encoding="utf-8")
    print("filter=" + filter_json)
    print("solution=OneDiff.Hosted.slnf")
    print("filtered=true")
    print("smoke=" + ("true" if "samples/OneDiff.AotSmoke/OneDiff.AotSmoke.csproj" in kept else "false"))
    print(
        "::notice::No preview feed is configured (repository variable ONEDIFF_PREVIEW_FEED), so the projects that need "
        f"{', '.join(sorted(pins))} are left out of this run: {', '.join(sorted(needs)) or 'none'}. See docs/building.md.",
        file=sys.stderr)


main()
