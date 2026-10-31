"""Check relative links and #anchors in every tracked or untracked Markdown file (GitHub slug rules).

Usage: python3 tools/docs/check-links.py [repo-root]. Exits 1 when any link or anchor is broken.
"""
import os, re, sys, unicodedata, subprocess
root = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..")
files = subprocess.run(["git","-C",root,"ls-files","--cached","--others","--exclude-standard","*.md"],
                       capture_output=True,text=True,check=True).stdout.split()
files = [f for f in files if os.path.exists(os.path.join(root,f))]

def slug(text):
    # github-slugger: lowercase, strip markdown/html, drop chars that are not letters, numbers,
    # marks, connector punctuation (underscore), spaces or hyphens; spaces -> '-'.
    text = re.sub(r"`([^`]*)`", r"\1", text)
    text = re.sub(r"\[([^\]]*)\]\([^)]*\)", r"\1", text)
    text = re.sub(r"[*_]{2}([^*_]+)[*_]{2}", r"\1", text)
    out = []
    for ch in text.lower():
        cat = unicodedata.category(ch)
        if ch in " -_" or cat[0] in "LNM":
            out.append(ch)
    return "".join(out).replace(" ", "-")

anchors = {}
def get_anchors(path):
    if path in anchors: return anchors[path]
    seen = {}; result = set(); in_code = False
    for line in open(path, encoding="utf-8"):
        if line.lstrip().startswith("```"): in_code = not in_code; continue
        if in_code: continue
        m = re.match(r"^(#{1,6})\s+(.*?)\s*#*\s*$", line)
        if not m: continue
        s = slug(m.group(2))
        n = seen.get(s, 0); seen[s] = n + 1
        result.add(s if n == 0 else f"{s}-{n}")
    anchors[path] = result
    return result

errors = 0
link_re = re.compile(r"(?<!!)\[[^\]]*\]\(([^)\s]+)\)")
for f in files:
    full = os.path.join(root, f); in_code = False
    for no, line in enumerate(open(full, encoding="utf-8"), 1):
        if line.lstrip().startswith("```"): in_code = not in_code; continue
        if in_code: continue
        for target in link_re.findall(re.sub(r"`[^`]*`", "", line)):
            if re.match(r"^[a-z]+://", target) or target.startswith("mailto:"): continue
            path, _, frag = target.partition("#")
            dest = os.path.normpath(os.path.join(os.path.dirname(full), path)) if path else full
            if not os.path.exists(dest):
                print(f"{f}:{no}: missing file {target}"); errors += 1; continue
            if frag:
                if not dest.endswith(".md"):
                    print(f"{f}:{no}: anchor on non-md {target}"); errors += 1; continue
                if frag not in get_anchors(dest):
                    print(f"{f}:{no}: missing anchor {target}"); errors += 1
print(f"checked {len(files)} files, errors={errors}")
sys.exit(1 if errors else 0)
