#!/usr/bin/env python3
"""Fetches the real version pairs of the document gold sets. Nothing it writes is ever committed.

    fetch-real-pairs.py [--root DIR] [--pins FILE] [--only licences,yc,github,edgar] [--update-pins] [--git PATH]

Output, under DIR (default $ONEDIFF_EVAL/real-pairs, else ~/onediff/eval/real-pairs-v1):

    <source>/<pair>/old.txt, new.txt   plain text of the two versions (blank-line separated paragraphs)
    <source>/<pair>/SOURCE.txt         where each side came from, its licence or terms, and its SHA-256
    manifest.json                      SHA-256 of every file; its own SHA-256 is printed and identifies the set

Sources, with the terms read at each:
    licences  GPL 2 -> 3, LGPL 2.1 -> 3, MPL 1.1 -> 2.0 (verbatim licence texts), GPL 3 -> AGPL 3, GPL 3 -> LGPL 3.
              gnu.org: "Everyone is permitted to copy and distribute verbatim copies of this license document, but
              changing it is not allowed."; mozilla.org content is under a Creative Commons licence. Fetch-only.
    yc        Y Combinator post-money SAFE forms (US, Canada, Cayman Islands, Singapore variants and side letters).
              ycombinator.com: "All of the SAFE forms are free to download and use". No redistribution licence: fetch-only.
    github    Common Paper and Bonterms agreement histories. Both state in their repositories' READMEs that the
              agreements are "free to use" under CC BY 4.0. Version pairs come from the git history of one file.
    edgar     SEC EDGAR exhibits: an original agreement and its amended and restated version from the same filer.
              sec.gov: "Information presented on sec.gov is considered public information and may be copied or further
              distributed"; fair access: at most 10 requests per second and a declared User-Agent. This script makes at
              most 4 per second and sends "RecursiveAI OneDiff <contact>", with the contact taken from
              $ONEDIFF_SEC_CONTACT (an e-mail address; sec.gov refuses user agents without one).

With the pins file, a fetch is reproducible: every URL, repository commit and EDGAR document is named in it, and every
download is checked against the SHA-256 recorded there. --update-pins looks things up again (git history, EDGAR full-text
search) and rewrites the pins file; use it only to extend the set.
"""

import argparse
import hashlib
import html
import io
import json
import os
import re
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile
from html.parser import HTMLParser
from xml.etree import ElementTree

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_PINS = os.path.join(HERE, "real-pairs-pins.json")
SEC_MIN_INTERVAL = 0.25  # seconds between requests to sec.gov: 4 per second, under the 10 per second limit
_last_sec = [0.0]


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def user_agent(url):
    if "sec.gov" in url:
        contact = os.environ.get("ONEDIFF_SEC_CONTACT", "")
        if "@" not in contact:
            sys.exit("set ONEDIFF_SEC_CONTACT to a contact e-mail address: sec.gov refuses requests without a declared contact")
        return "RecursiveAI OneDiff " + contact
    return "RecursiveAI-OneDiff-goldset-fetch (+https://github.com/recursive-ai-oss/onediff-csharp)"


def http_get(url, retries=4, timeout=60):
    """GET with a declared User-Agent, a timeout and retries; sec.gov requests are paced."""
    for attempt in range(retries):
        if "sec.gov" in url:
            wait = SEC_MIN_INTERVAL - (time.monotonic() - _last_sec[0])
            if wait > 0:
                time.sleep(wait)
            _last_sec[0] = time.monotonic()
        req = urllib.request.Request(url, headers={"User-Agent": user_agent(url), "Accept-Encoding": "identity"})
        try:
            with urllib.request.urlopen(req, timeout=timeout) as r:
                return r.read()
        except (urllib.error.URLError, TimeoutError, ConnectionError) as e:
            if isinstance(e, urllib.error.HTTPError) and e.code in (403, 404, 410):
                raise
            if attempt == retries - 1:
                raise
            time.sleep(2 * (attempt + 1))
    raise RuntimeError("unreachable")


# ---- text conversion ------------------------------------------------------------------------------------------------

BLOCK_TAGS = {"p", "div", "br", "tr", "li", "h1", "h2", "h3", "h4", "h5", "h6", "table", "section", "article", "pre", "ul", "ol", "hr"}


class _Text(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.parts = []
        self.skip = 0

    def handle_starttag(self, tag, attrs):
        if tag in ("script", "style", "head"):
            self.skip += 1
        if tag in BLOCK_TAGS:
            self.parts.append("\n\n" if tag != "br" else "\n")

    def handle_endtag(self, tag):
        if tag in ("script", "style", "head"):
            self.skip = max(0, self.skip - 1)
        if tag in BLOCK_TAGS:
            self.parts.append("\n\n" if tag != "br" else "\n")

    def handle_data(self, data):
        if not self.skip:
            self.parts.append(data)


def tidy(text):
    """Normalizes a text to paragraphs separated by one blank line, with single spaces inside."""
    text = text.replace("\r\n", "\n").replace("\r", "\n").replace("\u00a0", " ").replace("\u200b", "")
    paras = []
    for block in re.split(r"\n\s*\n", text):
        line = re.sub(r"[ \t\n]+", " ", block).strip()
        if line:
            paras.append(line)
    return "\n\n".join(paras) + "\n"


def decode(data):
    for enc in ("utf-8", "cp1252"):
        try:
            return data.decode(enc)
        except UnicodeDecodeError:
            continue
    return data.decode("latin-1")


def html_to_text(data):
    p = _Text()
    p.feed(decode(data))
    return tidy("".join(p.parts))


def md_to_text(data):
    text = decode(data)
    text = re.sub(r"<!--.*?-->", "", text, flags=re.S)
    text = re.sub(r"<[^>]+>", "", text)
    text = html.unescape(text)
    text = re.sub(r"!\[[^\]]*\]\([^)]*\)", "", text)
    text = re.sub(r"\[([^\]]*)\]\([^)]*\)", r"\1", text)
    text = re.sub(r"^\s{0,3}#{1,6}\s*", "", text, flags=re.M)
    text = re.sub(r"(\*\*|__|`)", "", text)
    # These agreements keep one clause or list item per line, so a line is a paragraph.
    return tidy(text.replace("\n", "\n\n"))


def docx_to_text(data):
    with zipfile.ZipFile(io.BytesIO(data)) as z:
        root = ElementTree.fromstring(z.read("word/document.xml"))
    w = "{http://schemas.openxmlformats.org/wordprocessingml/2006/main}"
    paras = []
    for p in root.iter(w + "p"):
        parts = []
        for e in p.iter():
            if e.tag == w + "t" and e.text:
                parts.append(e.text)
            elif e.tag == w + "tab":
                parts.append(" ")
            elif e.tag == w + "br":
                parts.append(" ")
        paras.append("".join(parts))
    return tidy("\n\n".join(paras))


# ---- output ---------------------------------------------------------------------------------------------------------

class Out:
    def __init__(self, root):
        self.root = root
        self.files = {}

    def write(self, rel, data):
        path = os.path.join(self.root, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        if isinstance(data, str):
            data = data.encode("utf-8")
        with open(path, "wb") as f:
            f.write(data)
        self.files[rel] = sha256(data)

    def pair(self, source, name, old, new, source_text):
        self.write(f"{source}/{name}/old.txt", old)
        self.write(f"{source}/{name}/new.txt", new)
        self.write(f"{source}/{name}/SOURCE.txt", source_text)

    def finish(self):
        manifest = json.dumps({"schema": "onediff.realpairs/1", "files": dict(sorted(self.files.items()))}, indent=1, sort_keys=True) + "\n"
        with open(os.path.join(self.root, "manifest.json"), "w", encoding="utf-8") as f:
            f.write(manifest)
        return sha256(manifest.encode("utf-8"))


def check_pin(entry, data, what):
    """Records the SHA-256 on the first fetch and refuses a changed document afterwards."""
    h = sha256(data)
    if entry.get("sha256") and entry["sha256"] != h:
        sys.exit(f"{what}: content changed (pinned {entry['sha256'][:12]}, got {h[:12]}); rerun with --update-pins if that is intended")
    entry["sha256"] = h
    return h


# ---- licences and YC ------------------------------------------------------------------------------------------------

def convert(url, data):
    if url.endswith(".docx"):
        return docx_to_text(data)
    if url.endswith(".txt"):
        return tidy(decode(data))
    if url.endswith(".md"):
        return md_to_text(data)
    return html_to_text(data)


def fetch_url_pairs(pins, key, out, terms):
    docs = {d["id"]: d for d in pins[key]["documents"]}
    cache = {}
    for pair in pins[key]["pairs"]:
        texts = []
        for side in ("old", "new"):
            d = docs[pair[side]]
            if d["id"] not in cache:
                url = d["url"]
                data = http_get(url)
                check_pin(d, data, url)
                cache[d["id"]] = convert(url, data)
            texts.append(cache[d["id"]])
        src = (f"old: {docs[pair['old']]['name']}\n  {docs[pair['old']]['url']}\n  sha256 {docs[pair['old']]['sha256']}\n"
               f"new: {docs[pair['new']]['name']}\n  {docs[pair['new']]['url']}\n  sha256 {docs[pair['new']]['sha256']}\n"
               f"terms: {terms}\nFetch-only: the texts are not redistributed with the repository.\n")
        out.pair(key, pair["id"], texts[0], texts[1], src)
    return len(pins[key]["pairs"])


# ---- GitHub histories -----------------------------------------------------------------------------------------------

def changed_paragraphs(a, b):
    """How many paragraphs of b are not in a."""
    have = set(a.split("\n\n"))
    return sum(1 for p in b.split("\n\n") if p not in have)


def run(cmd, cwd=None):
    return subprocess.run(cmd, cwd=cwd, check=True, capture_output=True).stdout


def update_github(pins, git, cache, per_file=3, max_pairs=40):
    """For each repository: pin HEAD, then pair the versions of each agreement file whose text changed materially."""
    total = 0
    for repo in pins["github"]["repos"]:
        dest = os.path.join(cache, repo["name"])
        if not os.path.isdir(dest):
            run([git, "clone", "-q", repo["url"], dest])
        else:
            run([git, "fetch", "-q", "--all"], cwd=dest)
        readme = next((f for f in os.listdir(dest) if f.lower().startswith("readme")), None)
        if not readme or not re.search(r"CC BY 4\.0|licenses/by/4\.0", open(os.path.join(dest, readme), encoding="utf-8", errors="replace").read()):
            print(f"  skipping {repo['name']}: its README does not state CC BY 4.0", file=sys.stderr)
            repo["pairs"] = []
            continue
        repo["commit"] = run([git, "rev-parse", "HEAD"], cwd=dest).decode().strip()
        files = [f for f in run([git, "ls-tree", "-r", "--name-only", repo["commit"]], cwd=dest).decode().splitlines()
                 if f.endswith(".md") and os.path.basename(f).lower() not in ("readme.md", "changelog.md", "contributing.md", "code_of_conduct.md")]
        repo["pairs"] = []
        for path in sorted(files):
            commits = run([git, "log", "--format=%H", repo["commit"], "--", path], cwd=dest).decode().split()
            versions = []  # newest first; keep a version when its text differs from the last kept one by at least 1%
            last = None
            for c in commits:
                try:
                    text = md_to_text(run([git, "show", f"{c}:{path}"], cwd=dest))
                except subprocess.CalledProcessError:
                    continue  # the file was deleted or renamed in this commit
                if len(text) < 3000:
                    continue
                if last is None or changed_paragraphs(last, text) >= 3:
                    versions.append((c, text))
                    last = text
            for i in range(min(per_file, len(versions) - 1)):
                (new_c, _), (old_c, _) = versions[i], versions[i + 1]
                repo["pairs"].append({"id": f"{repo['name']}-{os.path.splitext(os.path.basename(path))[0]}-{old_c[:7]}-{new_c[:7]}".lower().replace(" ", "-"),
                                      "path": path, "old": old_c, "new": new_c})
        total += len(repo["pairs"])
    # Keep the set bounded and spread over repositories.
    while total > max_pairs:
        biggest = max(pins["github"]["repos"], key=lambda r: len(r["pairs"]))
        biggest["pairs"].pop()
        total -= 1


def fetch_github(pins, out, git, cache):
    n = 0
    for repo in pins["github"]["repos"]:
        dest = os.path.join(cache, repo["name"])
        if not os.path.isdir(dest):
            run([git, "clone", "-q", repo["url"], dest])
        for p in repo["pairs"]:
            texts = []
            for side in ("old", "new"):
                data = run([git, "show", f"{p[side]}:{p['path']}"], cwd=dest)
                texts.append(md_to_text(data))
            src = (f"repository: {repo['url']}\nfile: {p['path']}\nold commit: {p['old']}\nnew commit: {p['new']}\n"
                   f"terms: {repo['terms']}\nCC BY 4.0: https://creativecommons.org/licenses/by/4.0/ — attribution: {repo['attribution']}\n")
            out.pair(f"github-{repo['org']}", p["id"], texts[0], texts[1], src)
            n += 1
    return n


# ---- EDGAR ----------------------------------------------------------------------------------------------------------

EFTS = "https://efts.sec.gov/LATEST/search-index"
DOC_URL = "https://www.sec.gov/Archives/edgar/data/{cik}/{acc}/{name}"
TITLE_RE = re.compile(r"AMENDED\s+AND\s+RESTATED\s+([A-Z][A-Z0-9 ,&'\-\.]{3,90}?)(?:\s+\(|\s+DATED|\s+BY\s|\s+AMONG|\s+BETWEEN|\s+Dated|\n|$)")
REF_RE = re.compile(r"(?:that certain|the|an?|which)\s+((?:[A-Z][\w&'\-\.]*\s+){1,7}?(?:Agreement|Lease|Plan|Indenture|Contract))\s*,?\s*(?:\([^)]{0,80}\)\s*)?(?:dated|made|entered into)(?: as of| on)?\s+([A-Z][a-z]+\s+\d{1,2},\s+\d{4})")


def efts(params):
    return json.loads(http_get(EFTS + "?" + urllib.parse.urlencode(params)))["hits"]["hits"]


def doc_of(hit):
    acc, name = hit["_id"].split(":")
    cik = hit["_source"]["ciks"][0].lstrip("0")
    return {"cik": cik, "acc": acc.replace("-", ""), "adsh": acc, "name": name, "date": hit["_source"]["file_date"],
            "type": hit["_source"]["file_type"], "company": hit["_source"]["display_names"][0],
            "url": DOC_URL.format(cik=cik, acc=acc.replace("-", ""), name=name)}


QUERIES = ["Amended and Restated Credit Agreement", "Amended and Restated Employment Agreement", "Amended and Restated Loan Agreement",
           "Amended and Restated Lease", "Amended and Restated License Agreement", "Amended and Restated Supply Agreement",
           "Amended and Restated Registration Rights Agreement", "Amended and Restated Services Agreement",
           "Amended and Restated Purchase Agreement", "Amended and Restated Distribution Agreement"]


def update_edgar(pins, target):
    """Finds amended and restated exhibits and the original agreement each one names, from the same filer."""
    e = pins["edgar"]
    pairs = []
    seen = set()
    years = [2012, 2014, 2016, 2018, 2020, 2022, 2024]
    per_query = max(2, target // len(QUERIES) + 1)
    for q in QUERIES:
        found = 0
        for year in years:
            if found >= per_query:
                break
            try:
                hits = efts({"q": f'"{q}"', "forms": "8-K,10-K,10-Q", "dateRange": "custom", "startdt": f"{year}-01-01", "enddt": f"{year}-12-31"})
            except urllib.error.URLError:
                continue
            for h in hits:
                if found >= per_query:
                    break
                if not h["_source"]["file_type"].startswith("EX-10") or h["_id"] in seen:
                    continue
                seen.add(h["_id"])
                new = doc_of(h)
                try:
                    new_bytes = http_get(new["url"])
                except urllib.error.URLError:
                    continue
                new_text = html_to_text(new_bytes)
                head = new_text[:12000]
                if not re.search(r"AMENDED\s+AND\s+RESTATED", head[:600], re.I):
                    continue
                m = REF_RE.search(head)
                if not m or not (8000 < len(new_text) < 400000):
                    continue
                title, date = re.sub(r"\s+", " ", m.group(1)), m.group(2)
                orig = find_original(new, title, date)
                if orig is None:
                    continue
                orig_bytes, orig_doc, orig_text = orig
                ratio = len(orig_text) / max(1, len(new_text))
                if not 0.3 < ratio < 3.0:
                    continue
                pid = f"edgar-{new['cik']}-{orig_doc['acc'][-8:]}-{new['acc'][-8:]}"
                if any(x["id"] == pid for x in pairs):
                    continue
                pairs.append({"id": pid, "kind": q.replace("Amended and Restated ", ""),
                              "old": {**orig_doc, "sha256": sha256(orig_bytes)}, "new": {**new, "sha256": sha256(new_bytes)}})
                found += 1
                print(f"  edgar pair {len(pairs)}: {new['company']} — {title} dated {date}", file=sys.stderr)
        if len(pairs) >= target:
            break
    e["pairs"] = pairs[:target]


def find_original(new, title, date):
    """The earlier exhibit that is the named agreement: dated as stated, titled without 'amended and restated'."""
    q = f'"{title}" "{date}"'
    try:
        hits = efts({"q": q, "ciks": new["cik"].zfill(10), "dateRange": "custom", "startdt": "2000-01-01", "enddt": new["date"]})
    except urllib.error.URLError:
        return None
    cands = sorted((doc_of(h) for h in hits if h["_source"]["file_type"].startswith("EX-") and h["_source"]["file_date"] < new["date"]), key=lambda d: d["date"])
    for d in cands[:4]:
        try:
            data = http_get(d["url"])
        except urllib.error.URLError:
            continue
        text = html_to_text(data)
        head = text[:3000]
        if re.search(r"AMENDED\s+AND\s+RESTATED", head[:500], re.I) or date not in head or not re.search(title.split()[0], head, re.I):
            continue
        if 8000 < len(text) < 400000:
            return data, d, text
    return None


def fetch_edgar(pins, out):
    n = 0
    for p in pins["edgar"]["pairs"]:
        texts = []
        for side in ("old", "new"):
            d = p[side]
            data = http_get(d["url"])
            check_pin(d, data, d["url"])
            texts.append(html_to_text(data))
        src = "".join(f"{side}: {p[side]['company']} {p[side]['type']} filed {p[side]['date']}\n  {p[side]['url']}\n  sha256 {p[side]['sha256']}\n" for side in ("old", "new"))
        src += "terms: SEC EDGAR filing, public information (sec.gov/privacy). Fetch-only: filed exhibits can carry third-party copyright.\n"
        out.pair("edgar", p["id"], texts[0], texts[1], src)
        n += 1
    return n


# ---- main -----------------------------------------------------------------------------------------------------------

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--root")
    ap.add_argument("--pins", default=DEFAULT_PINS)
    ap.add_argument("--only", default="licences,yc,github,edgar")
    ap.add_argument("--update-pins", action="store_true")
    ap.add_argument("--git", default=os.environ.get("ONEDIFF_GIT", "git"))
    ap.add_argument("--edgar-target", type=int, default=24)
    args = ap.parse_args()

    eval_root = os.environ.get("ONEDIFF_EVAL") or os.path.expanduser("~/onediff/eval")
    root = args.root or os.path.join(eval_root, "real-pairs-v1")
    only = set(args.only.split(","))
    with open(args.pins, encoding="utf-8") as f:
        pins = json.load(f)
    cache = os.path.join(root, "_git")
    if os.path.exists(os.path.join(root, "manifest.json")) and not args.update_pins:
        sys.exit(f"{root} already holds a fetch; remove it first or choose another --root")
    os.makedirs(root, exist_ok=True)
    out = Out(root)
    counts = {}
    if args.update_pins:
        if "github" in only:
            update_github(pins, args.git, cache)
        if "edgar" in only:
            update_edgar(pins, args.edgar_target)
    if "licences" in only:
        counts["licences"] = fetch_url_pairs(pins, "licences", out, "gnu.org: verbatim copying permitted, changing not allowed; mozilla.org: content under a Creative Commons licence")
    if "yc" in only:
        counts["yc"] = fetch_url_pairs(pins, "yc", out, "ycombinator.com/documents: all of the SAFE forms are free to download and use")
    if "github" in only:
        counts["github"] = fetch_github(pins, out, args.git, cache)
    if "edgar" in only:
        counts["edgar"] = fetch_edgar(pins, out)
    shutil_cache = cache
    if os.path.isdir(shutil_cache):
        subprocess.run(["rm", "-rf", shutil_cache], check=True)
    h = out.finish()
    if args.update_pins:
        with open(args.pins, "w", encoding="utf-8") as f:
            json.dump(pins, f, indent=1, ensure_ascii=False)
            f.write("\n")
    print(json.dumps({"root": root, "pairs": counts, "manifest_sha256": h}))


if __name__ == "__main__":
    main()
