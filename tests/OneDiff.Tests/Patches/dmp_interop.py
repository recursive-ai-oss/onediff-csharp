"""Interop run against the released diff-match-patch package, used by the corpus gate.

For every id in the list (a file with one id per line) it reads <work>/<id>/{old.txt,new.txt,ours.patch,ours_default.patch},
applies our two patch texts with the package, and writes the package's own patch text for the pair to <work>/<id>/theirs.patch.
The outcome goes to <work>/<id>/status.txt. The package is only imported and called.
"""
import sys
import os
import multiprocessing

from diff_match_patch import diff_match_patch


def read(path):
    with open(path, "rb") as f:
        return f.read().decode("utf-8")


def one(args):
    work, ident = args
    d = os.path.join(work, ident)
    try:
        old = read(os.path.join(d, "old.txt"))
        new = read(os.path.join(d, "new.txt"))
        dmp = diff_match_patch()
        out = []
        for name in ("ours", "ours_default"):
            text = read(os.path.join(d, name + ".patch"))
            result, flags = dmp.patch_apply(dmp.patch_fromText(text), old)
            out.append(name + ("_ok" if (result == new and all(flags)) else "_FAIL"))
        theirs = dmp.patch_toText(dmp.patch_make(old, new))
        with open(os.path.join(d, "theirs.patch"), "wb") as f:
            f.write(theirs.encode("utf-8"))
        status = " ".join(out)
    except Exception as e:  # report, never crash the run
        status = "ERROR %s: %s" % (type(e).__name__, e)
    with open(os.path.join(d, "status.txt"), "w") as f:
        f.write(status + "\n")
    return ident, status


def main():
    work, ids = sys.argv[1], sys.argv[2]
    with open(ids) as f:
        names = [l.strip() for l in f if l.strip()]
    try:
        import importlib.metadata as md
        print("version diff-match-patch", md.version("diff-match-patch"))
    except Exception:
        print("version diff-match-patch unknown")
    with multiprocessing.Pool(8) as pool:
        for ident, status in pool.imap_unordered(one, [(work, n) for n in names], chunksize=4):
            if "FAIL" in status or "ERROR" in status:
                print(ident, status)


if __name__ == "__main__":
    main()
