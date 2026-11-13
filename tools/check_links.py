from pathlib import Path
import re

root = Path(__file__).resolve().parent.parent
files = [root / "README.md"] + list((root / "docs").glob("*.md"))
missing = []
for f in files:
    text = f.read_text(encoding="utf-8")
    for m in re.finditer(r"\[[^\]]*\]\(([^)]+)\)", text):
        target = m.group(1)
        if "://" in target or target.startswith("#"):
            continue
        target = target.split("#")[0]
        p = (f.parent / target).resolve()
        if not p.exists():
            missing.append((str(f.relative_to(root)), target))
print("checked", len(files), "missing", len(missing))
for x in missing:
    print(x)
