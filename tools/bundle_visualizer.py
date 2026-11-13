from pathlib import Path

root = Path(__file__).resolve().parent.parent
html_path = root / "visualizer" / "index.html"
html = html_path.read_text(encoding="utf-8")
for name in ["flagship_120.js", "packing_160.js", "flagship_late_140.js"]:
    data = (root / "visualizer" / "data" / name).read_text(encoding="utf-8")
    tag = f'<script src="data/{name}"></script>'
    if tag not in html:
        raise SystemExit("missing tag " + tag)
    html = html.replace(tag, "<script>\n" + data + "\n</script>")
out = root / "visualizer" / "crowd_demo.html"
out.write_text(html, encoding="utf-8")
print(out, out.stat().st_size)
