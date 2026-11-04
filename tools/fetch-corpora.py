#!/usr/bin/env python3
"""Fetches the text and merge corpora used by the oracle harness and the benchmarks.

Usage: fetch-corpora.py [--root DIR] [--git PATH] [repos] [wiki] [synth]   (default: all three)

  --root  corpus root; default $ONEDIFF_CORPORA, else $ONEDIFF_HOME/corpora, else ~/.cache/onediff/corpora
  --git   git binary used to clone and read the public repositories; default the oracle install's git, else `git`

Layout under the root (data stays out of the repository):
  text/<source>/<n>/{old,new,meta.json}                 version pairs
  merge/<repo>/<n>/{base,ours,theirs,meta.json}         three-way triples from real merges
  text/<source>/{LICENSE.txt,SOURCE.txt}                the licence or terms beside each source
  manifest.json                                         sha256 of every file under text/ and merge/

Everything is pinned by corpora-pins.json beside this script: repository commits and Wikipedia revision ids, so a fresh
fetch gives the same files. The synthetic edge cases come from a fixed seed.
"""
import argparse, hashlib, json, os, random, subprocess, sys, urllib.parse, urllib.request
from pathlib import Path

HOME = Path(os.environ.get("ONEDIFF_HOME", Path.home() / ".cache" / "onediff"))
ROOT = Path(os.environ.get("ONEDIFF_CORPORA", HOME / "corpora"))
_oracle_git = HOME / "oracles" / "env" / "bin" / "git"
GIT = str(_oracle_git) if _oracle_git.exists() else "git"
PINS = json.loads((Path(__file__).resolve().parent / "corpora-pins.json").read_text(encoding="utf-8"))
REPOS = {  # permissive licences only
    "express": ("https://github.com/expressjs/express.git", "MIT"),
    "requests": ("https://github.com/psf/requests.git", "Apache-2.0"),
    "flask": ("https://github.com/pallets/flask.git", "BSD-3-Clause"),
    "serde": ("https://github.com/serde-rs/serde.git", "MIT OR Apache-2.0"),
    "newtonsoft-json": ("https://github.com/JamesNK/Newtonsoft.Json.git", "MIT"),
    "rustlings": ("https://github.com/rust-lang/rustlings.git", "MIT"),
}
TEXT_EXT = {".js", ".ts", ".py", ".rs", ".cs", ".md", ".rst", ".txt", ".json", ".toml", ".yml", ".yaml", ".html", ".css", ".cfg", ".sh"}
MAX_PAIRS, MAX_TRIPLES = 1500, 800
WIKI = {  # CC BY-SA 4.0 text: evaluation/test corpus only, fetch-only
    "en": ["Contract", "Diff", "Merge (version control)", "Unicode", "Copyright", "Software license", "Lease", "Non-disclosure agreement"],
    "zh": ["合同", "版权", "統一碼", "软件许可证"],
    "ja": ["契約", "著作権", "Unicode", "ソフトウェアライセンス"],
    "ko": ["계약", "저작권", "유니코드"],
    "ar": ["عقد", "حقوق التأليف والنشر"],
    "hi": ["अनुबंध", "कॉपीराइट"],
    "de": ["Vertrag", "Urheberrecht"],
}
UA = "onediff-corpus-fetch/0.1 (https://github.com/recursive-ai-oss/onediff-csharp)"


def git(repo, *args, binary=False):
    out = subprocess.run([GIT, "-C", str(repo), *args], capture_output=True, check=True).stdout
    return out if binary else out.decode("utf-8", "replace")


def is_text(b):
    if b"\0" in b[:8000]:
        return False
    try:
        b.decode("utf-8")
        return True
    except UnicodeDecodeError:
        return False


def blob(repo, rev, path):
    try:
        return git(repo, "show", f"{rev}:{path}", binary=True)
    except subprocess.CalledProcessError:
        return None


def fetch_repo(name, url, lic):
    src = ROOT / "src" / name
    if not src.exists():
        subprocess.run([GIT, "clone", "-q", url, str(src)], check=True)
    for cand in ("LICENSE", "LICENSE.md", "LICENSE.txt", "LICENSE-MIT", "LICENSE.rst"):
        if (src / cand).exists():
            lictext = (src / cand).read_bytes()
            break
    else:
        lictext = lic.encode()
    head = PINS["repos"][name]
    git(src, "cat-file", "-e", head + "^{commit}")  # fails loudly if the pinned commit is gone
    # text pairs: parent -> child per modified text file
    tdir = ROOT / "text" / f"git-{name}"
    tdir.mkdir(parents=True, exist_ok=True)
    (tdir / "LICENSE.txt").write_bytes(lictext)
    (tdir / "SOURCE.txt").write_text(f"{url} @ {head}\nlicence: {lic}\n")
    n = 0
    for c in git(src, "rev-list", "--no-merges", "--max-count=4000", head).split():
        if n >= MAX_PAIRS:
            break
        for line in git(src, "diff-tree", "--no-commit-id", "-r", "--diff-filter=M", "--name-only", f"{c}^", c).splitlines():
            if Path(line).suffix.lower() not in TEXT_EXT:
                continue
            a, b = blob(src, f"{c}^", line), blob(src, c, line)
            if not a or not b or not is_text(a) or not is_text(b):
                continue
            d = tdir / f"{n:05d}"
            d.mkdir(exist_ok=True)
            (d / "old").write_bytes(a)
            (d / "new").write_bytes(b)
            (d / "meta.json").write_text(json.dumps({"commit": c, "path": line}))
            n += 1
            if n >= MAX_PAIRS:
                break
    # merge triples
    mdir = ROOT / "merge" / name
    mdir.mkdir(parents=True, exist_ok=True)
    (mdir / "LICENSE.txt").write_bytes(lictext)
    (mdir / "SOURCE.txt").write_text(f"{url} @ {head}\nlicence: {lic}\n")
    m = 0
    for line in git(src, "rev-list", "--merges", "--parents", head).splitlines():
        if m >= MAX_TRIPLES:
            break
        parts = line.split()
        if len(parts) != 3:
            continue
        mc, p1, p2 = parts
        try:
            base = git(src, "merge-base", p1, p2).split()[0]
        except (subprocess.CalledProcessError, IndexError):
            continue
        f1 = set(git(src, "diff", "--name-only", "--diff-filter=M", base, p1).split("\n"))
        f2 = set(git(src, "diff", "--name-only", "--diff-filter=M", base, p2).split("\n"))
        for path in sorted((f1 & f2) - {""}):
            trip = [blob(src, r, path) for r in (base, p1, p2)]
            if any(t is None or not is_text(t) for t in trip):
                continue
            d = mdir / f"{m:05d}"
            d.mkdir(exist_ok=True)
            for nm, t in zip(("base", "ours", "theirs"), trip):
                (d / nm).write_bytes(t)
            (d / "meta.json").write_text(json.dumps({"merge": mc, "base": base, "ours": p1, "theirs": p2, "path": path}))
            m += 1
            if m >= MAX_TRIPLES:
                break
    print(f"{name}: {n} pairs, {m} triples", flush=True)


def fetch_wiki():
    # Each chain is a page's consecutive revisions, oldest first; pairs are (rev[i], rev[i+1]).
    for lang, chains in PINS["wiki"].items():
        tdir = ROOT / "text" / f"wiki-{lang}"
        tdir.mkdir(parents=True, exist_ok=True)
        (tdir / "SOURCE.txt").write_text(
            f"https://{lang}.wikipedia.org revision texts via the MediaWiki API.\n"
            "licence: CC BY-SA 4.0 (https://creativecommons.org/licenses/by-sa/4.0/); attribution: Wikipedia contributors, see page history.\n"
            "use: benchmarks and tests only; fetched on demand, never committed.\n")
        n = 0
        for chain in chains:
            q = urllib.parse.urlencode({"action": "query", "prop": "revisions", "revids": "|".join(map(str, chain)),
                                        "rvprop": "ids|content", "rvslots": "main", "format": "json"})
            req = urllib.request.Request(f"https://{lang}.wikipedia.org/w/api.php?{q}", headers={"User-Agent": UA})
            data = json.load(urllib.request.urlopen(req, timeout=60))
            text, title = {}, None
            for page in data["query"]["pages"].values():
                title = page["title"]
                for r in page["revisions"]:
                    text[r["revid"]] = r["slots"]["main"]["*"]
            for a, b in zip(chain, chain[1:]):
                d = tdir / f"{n:05d}"
                d.mkdir(exist_ok=True)
                (d / "old").write_text(text[a], encoding="utf-8")
                (d / "new").write_text(text[b], encoding="utf-8")
                (d / "meta.json").write_text(json.dumps({"title": title, "old": a, "new": b}, ensure_ascii=False))
                n += 1
        print(f"wiki-{lang}: {n} pairs", flush=True)


def synth():
    """Edge cases we write ourselves (committable): emoji/ZWJ, combining marks, CRLF/LF, no trailing newline, long lines, large files."""
    rnd = random.Random(20261104)
    tdir = ROOT / "text" / "synthetic"
    tdir.mkdir(parents=True, exist_ok=True)
    (tdir / "SOURCE.txt").write_text("Generated by fetch-corpora.py (seed 20261104). Our own text; licence MIT OR Apache-2.0.\n")
    words = "the of and to in contract party shall agreement term notice payment within days section clause".split()
    emoji = ["👍", "👩‍💻", "🏳️‍🌈", "👨‍👩‍👧‍👦", "🇯🇵", "✌🏽", "❤️"]
    marks = ["e\u0301", "a\u0308", "n\u0303", "\u1100\u1161\u11a8", "o\u0302\u0323", "क्षि"]

    def line(k):
        return " ".join(rnd.choice(words + emoji + marks) for _ in range(k))

    def mutate(lines):
        out = list(lines)
        for _ in range(max(1, len(out) // 20)):
            op, i = rnd.random(), rnd.randrange(len(out) + 1)
            if op < 0.33 and out:
                out.pop(min(i, len(out) - 1))
            elif op < 0.66:
                out.insert(i, line(rnd.randint(1, 15)))
            elif out:
                j = min(i, len(out) - 1)
                w = out[j].split(" ")
                w[rnd.randrange(len(w))] = rnd.choice(words + emoji + marks)
                out[j] = " ".join(w)
        return out

    cases = []
    for i in range(200):
        base = [line(rnd.randint(1, 15)) for _ in range(rnd.randint(1, 300))]
        new = mutate(base)
        eol_a = rnd.choice(["\n", "\r\n", "mix"])
        eol_b = rnd.choice(["\n", "\r\n", "mix"])

        def join(ls, eol):
            if eol == "mix":
                return "".join(l + rnd.choice(["\n", "\r\n"]) for l in ls)
            return "".join(l + eol for l in ls)
        a, b = join(base, eol_a), join(new, eol_b)
        if rnd.random() < 0.3:
            a = a.rstrip("\r\n")
        if rnd.random() < 0.3:
            b = b.rstrip("\r\n")
        cases.append((a, b))
    long_a = line(200000)
    long_b = long_a[:500000] + " inserted " + long_a[500010:]
    cases.append((long_a, long_b))
    big = [f"{k:08d} " + line(12) for k in range(150000)]
    cases.append(("\n".join(big) + "\n", "\n".join(mutate(big)) + "\n"))
    cases += [("", ""), ("", "a\n"), ("a\n", ""), ("same\n" * 1000, "same\n" * 1000),
              ("\n".join(str(k) for k in range(5000)), "\n".join(str(k + 100000) for k in range(5000))),
              ("x\n" * 3000, "x\n" * 2999 + "y\n" + "x\n" * 50)]
    for i, (a, b) in enumerate(cases):
        d = tdir / f"{i:05d}"
        d.mkdir(exist_ok=True)
        (d / "old").write_bytes(a.encode("utf-8"))
        (d / "new").write_bytes(b.encode("utf-8"))
    print(f"synthetic: {len(cases)} pairs", flush=True)


def manifest():
    entries = {}
    for kind in ("text", "merge"):
        for p in sorted((ROOT / kind).rglob("*")):
            if p.is_file():
                entries[str(p.relative_to(ROOT))] = hashlib.sha256(p.read_bytes()).hexdigest()
    (ROOT / "manifest.json").write_text(json.dumps(entries, indent=0, sort_keys=True))
    h = hashlib.sha256((ROOT / "manifest.json").read_bytes()).hexdigest()
    print(f"manifest: {len(entries)} files, sha256 {h}")


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description="Fetch the OneDiff text and merge corpora.")
    ap.add_argument("--root", help="corpus root")
    ap.add_argument("--git", help="git binary")
    ap.add_argument("what", nargs="*", choices=["repos", "wiki", "synth"])
    args = ap.parse_args()
    if args.root:
        ROOT = Path(args.root).expanduser()
    if args.git:
        GIT = args.git
    ROOT.mkdir(parents=True, exist_ok=True)
    what = args.what or ["repos", "wiki", "synth"]
    if "repos" in what:
        for name, (url, lic) in REPOS.items():
            fetch_repo(name, url, lic)
    if "wiki" in what:
        fetch_wiki()
    if "synth" in what:
        synth()
    manifest()
