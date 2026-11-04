"""Prints C# test rows for ThreeWayMergeTests: random small triples over few distinct lines, with the text git merge-file printed.
Usage: gencases.py <git> <seed> <count> <tokens> <maxlen> <workdir>
Only triples whose merge has a conflict in every style and whose sides align non-trivially are kept."""
import os, random, subprocess, sys

git, seed, count, tokens, maxlen, work = sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4]), int(sys.argv[5]), sys.argv[6]
os.makedirs(work, exist_ok=True)
rnd = random.Random(seed)
env = dict(os.environ, GIT_CONFIG_GLOBAL="/dev/null", GIT_CONFIG_SYSTEM="/dev/null")


def text(lines):
    return "".join(l + "\n" for l in lines)


def merge(b, o, t, style):
    for n, s in (("b", b), ("o", o), ("t", t)):
        open(os.path.join(work, n), "w", newline="").write(s)
    args = [git, "merge-file", "-p", "-L", "ours", "-L", "base", "-L", "theirs"]
    if style != "merge":
        args.append("--" + style)
    r = subprocess.run(args + [os.path.join(work, "o"), os.path.join(work, "b"), os.path.join(work, "t")], capture_output=True, env=env)
    return r.stdout.decode(), r.returncode


def cs(s):
    return '"' + s.replace("\\", "\\\\").replace("\n", "\\n") + '"'


kept = 0
while kept < count:
    n = rnd.randint(4, maxlen)
    base = [chr(ord("a") + rnd.randrange(tokens)) for _ in range(n)]

    def edit(lines):
        r = list(lines)
        for _ in range(rnd.randint(1, 3)):
            at = rnd.randint(0, len(r))
            k = rnd.randrange(3)
            if k == 0:
                r.insert(at, chr(ord("a") + rnd.randrange(tokens)))
            elif at < len(r):
                if k == 1:
                    del r[at]
                else:
                    r[at] = chr(ord("a") + rnd.randrange(tokens))
        return r

    ours, theirs = edit(base), edit(base)
    B, O, T = text(base), text(ours), text(theirs)
    outs = {s: merge(B, O, T, s) for s in ("merge", "diff3", "zdiff3")}
    if any(rc <= 0 for _, rc in outs.values()) or len({o for o, _ in outs.values()}) < 3:
        continue
    kept += 1
    for s, (o, rc) in outs.items():
        style = {"merge": "M", "diff3": "D3", "zdiff3": "Z3"}[s]
        print(f"        {{ {cs(B)}, {cs(O)}, {cs(T)}, {style}, ConflictResolution.None, {cs(o)}, {rc} }},")
