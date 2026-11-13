import json
import math
import os
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
DATA = ROOT / "visualizer" / "data" / "flagship_120.js"
OUT = ROOT / "out"


def load_scene(path):
    text = path.read_text(encoding="utf-8")
    start = text.index("{", text.index("]"))
    end = text.rindex("}")
    return json.loads(text[start:end + 1])


SCENE = load_scene(DATA)
MAP = SCENE["terrain"]
W, H, CELL = MAP["width"], MAP["height"], MAP["cell"]
SPAN = W * CELL
HEIGHTS = np.array(MAP["heights"], dtype=np.float32).reshape(H, W)
GOAL = SCENE["goal"]
GOAL_R = SCENE["goalRadius"]
R = SCENE["unitRadius"]
FRAMES = SCENE["frames"]


def sample_height(x, z):
    gx = np.clip(x / CELL - 0.5, 0, W - 1)
    gz = np.clip(z / CELL - 0.5, 0, H - 1)
    x0 = np.floor(gx).astype(int)
    z0 = np.floor(gz).astype(int)
    x1 = np.minimum(x0 + 1, W - 1)
    z1 = np.minimum(z0 + 1, H - 1)
    tx = gx - x0
    tz = gz - z0
    a = HEIGHTS[z0, x0] * (1 - tx) + HEIGHTS[z0, x1] * tx
    b = HEIGHTS[z1, x0] * (1 - tx) + HEIGHTS[z1, x1] * tx
    return a * (1 - tz) + b * tz


def build_background(size=900, ppm=14.5):
    w = int(SPAN * ppm)
    h = w
    yy, xx = np.mgrid[0:h, 0:w]
    x = (xx + 0.5) / ppm
    z = (yy + 0.5) / ppm
    hgt = sample_height(x, z)
    e = 0.35
    hx = (sample_height(x + e, z) - sample_height(x - e, z)) / (2 * e)
    hz = (sample_height(x, z + e) - sample_height(x, z - e)) / (2 * e)
    nl = np.sqrt(hx * hx + 1 + hz * hz)
    shade = (-hx * -0.55 + 1 * 0.72 + -hz * -0.42) / nl
    shade = np.clip(0.62 + shade * 0.55, 0.42, 1.2)
    lo, hi = float(hgt.min()), float(hgt.max())
    t = (hgt - lo) / max(1e-6, hi - lo)

    col = np.zeros((h, w, 3), dtype=np.float32)
    # low: green, mid: olive, high: sandy, peak: warm light
    stops = [
        (0.00, (58, 96, 78)),
        (0.28, (92, 133, 94)),
        (0.58, (150, 158, 105)),
        (0.82, (192, 177, 132)),
        (1.00, (229, 213, 176)),
    ]
    for i in range(len(stops) - 1):
        t0, c0 = stops[i]
        t1, c1 = stops[i + 1]
        m = (t >= t0) & (t <= t1)
        u = ((t[m] - t0) / max(1e-6, t1 - t0))[:, None]
        col[m] = np.array(c0)[None, :] * (1 - u) + np.array(c1)[None, :] * u
    col *= shade[:, :, None]

    # contour lines every 0.5m
    band = np.floor(hgt * 2)
    band_x = np.floor(sample_height(x + 0.08, z) * 2)
    band_z = np.floor(sample_height(x, z + 0.08) * 2)
    edge = (band != band_x) | (band != band_z)
    col[edge] *= 0.83

    arr = np.clip(col, 0, 255).astype(np.uint8)
    img = Image.fromarray(arr, "RGB")
    d = ImageDraw.Draw(img, "RGBA")
    for cx, cz, rr in SCENE["obstacles"]:
        px, py = cx * ppm, cz * ppm
        pr = rr * ppm
        d.ellipse([px - pr, py - pr, px + pr, py + pr], fill=(44, 49, 53, 255), outline=(8, 12, 14, 255), width=max(1, int(ppm * .15)))
        d.ellipse([px - pr * .62 - pr * .22, py - pr * .62 - pr * .22, px + pr * .62 - pr * .22, py + pr * .62 - pr * .22], fill=(58, 64, 68, 90))
    return img, ppm


def font(size):
    for name in ["msyh.ttc", "segoeui.ttf", "arial.ttf"]:
        try:
            return ImageFont.truetype(name, size)
        except Exception:
            continue
    return ImageFont.load_default()


FONT_SM = font(15)
FONT_MD = font(19)
FONT_LG = font(26)


def draw_star(d, cx, cy, r, fill=(255, 92, 106, 255), outline=(255, 255, 255, 220)):
    pts = []
    for k in range(10):
        a = -math.pi / 2 + k * math.pi / 5
        rad = r if k % 2 == 0 else r * 0.44
        pts.append((cx + math.cos(a) * rad, cy + math.sin(a) * rad))
    d.polygon(pts, fill=fill, outline=outline)


def render_frame(bg, ppm, fi, trail=True):
    img = bg.copy()
    d = ImageDraw.Draw(img, "RGBA")
    f = FRAMES[fi]

    # goal footprint and marker
    gx, gy = GOAL[0] * ppm, GOAL[1] * ppm
    gr = GOAL_R * ppm
    side = SCENE.get("approachSide")
    if side and math.hypot(side[0], side[1]) > 1e-4:
        ang = math.degrees(math.atan2(side[1], side[0]))
        d.pieslice([gx - gr, gy - gr, gx + gr, gy + gr], -90 + ang, 90 + ang,
                   fill=(91, 200, 255, 12), outline=(150, 224, 255, 110), width=2)
    else:
        d.ellipse([gx - gr, gy - gr, gx + gr, gy + gr], fill=(91, 200, 255, 12),
                  outline=(150, 224, 255, 110), width=2)
    d.ellipse([gx - 3, gy - 3, gx + 3, gy + 3], fill=(255, 92, 106, 180))
    draw_star(d, gx, gy, max(8, ppm * .7))

    n = f["n"]
    xs, zs, ss = f["x"], f["z"], f["s"]
    if trail:
        start = max(0, fi - 16)
        for i in range(n):
            if ss[i] == 1:
                continue
            pts = []
            for k in range(start, fi + 1):
                fr = FRAMES[k]
                if i < fr["n"]:
                    pts.append((fr["x"][i] * ppm, fr["z"][i] * ppm))
            if len(pts) > 1:
                d.line(pts, fill=(110, 195, 255, 45), width=max(1, int(ppm * .06)), joint="curve")

    order = sorted(range(n), key=lambda i: zs[i])
    for i in order:
        st = ss[i]
        fill = (61, 220, 132) if st == 1 else (255, 181, 71) if st == 3 else (255, 92, 92) if st == 2 else (78, 168, 255)
        x, y = xs[i] * ppm, zs[i] * ppm
        r = max(3.0, R * ppm)
        d.ellipse([x - r * 1.15 + r * .25, y - r * .9 + r * .35, x + r * 1.15 + r * .25, y + r * .9 + r * .35], fill=(0, 0, 0, 65))
        d.ellipse([x - r, y - r, x + r, y + r], fill=fill, outline=(5, 10, 14, 200), width=max(1, int(r * .22)))

    # HUD
    top = f"t={f['t']:.1f}s   arrived {f['arr']}/{n}   min gap {f['min']:.3f}m"
    d.rectangle([12, 12, 12 + 560, 46], fill=(7, 12, 17, 190), outline=(50, 70, 86, 160), width=1)
    d.text((22, 21), top, font=FONT_SM, fill=(225, 238, 248, 255))

    # progress bar
    d.rectangle([12, img.height - 18, img.width - 12, img.height - 12], fill=(24, 35, 45, 200))
    progress = (fi / max(1, len(FRAMES) - 1))
    d.rectangle([12, img.height - 18, 12 + (img.width - 24) * progress, img.height - 12], fill=(92, 200, 255, 220))
    if f["arr"] == n:
        d.rectangle([img.width - 240, 12, img.width - 12, 46], fill=(17, 60, 40, 210), outline=(60, 160, 100, 200), width=1)
        d.text((img.width - 228, 21), "目标区域已压实", font=FONT_SM, fill=(170, 255, 205, 255))
    return img


def make_gif():
    # Small overview GIF. 0.2 s of simulation per image, 100 ms display => 2x speed.
    bg, ppm = build_background(ppm=9.0)
    indices = list(range(0, len(FRAMES), 8))
    frames = []
    for j, fi in enumerate(indices):
        im = render_frame(bg, ppm, fi).resize((540, 540), Image.LANCZOS)
        frames.append(im.quantize(colors=48, method=Image.Quantize.MEDIANCUT))
        if j % 40 == 0:
            print("gif frame", j, "/", len(indices))
    path = OUT / "flagship_crowd.gif"
    frames[0].save(path, save_all=True, append_images=frames[1:], duration=120, loop=0, optimize=True, disposal=2)
    print("wrote", path, path.stat().st_size)


def make_keyframes():
    bg, ppm = build_background()
    targets = [0.0, 12.0, 24.0, FRAMES[-1]["t"]]
    imgs = []
    for t in targets:
        fi = 0
        for k, f in enumerate(FRAMES):
            if f["t"] <= t:
                fi = k
            else:
                break
        img = render_frame(bg, ppm, fi)
        imgs.append(img)
    thumb_w = 720
    thumbs = [im.resize((thumb_w, int(im.height * thumb_w / im.width)), Image.LANCZOS) for im in imgs]
    gap = 12
    canvas = Image.new("RGB", (thumb_w * 2 + gap * 3, thumbs[0].height * 2 + gap * 3), (8, 12, 17))
    for i, im in enumerate(thumbs):
        x = gap + (i % 2) * (thumb_w + gap)
        y = gap + (i // 2) * (thumbs[0].height + gap)
        canvas.paste(im, (x, y))
    path = OUT / "flagship_keyframes.png"
    canvas.save(path)
    print("wrote", path, path.stat().st_size)


def make_3d():
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from matplotlib import cm
    from matplotlib.colors import LightSource

    plt.rcParams["font.sans-serif"] = ["Microsoft YaHei", "SimHei", "Arial"]
    plt.rcParams["axes.unicode_minus"] = False
    step = 2
    xs = np.arange(0, W, step) * CELL + CELL / 2
    zs = np.arange(0, H, step) * CELL + CELL / 2
    X, Z = np.meshgrid(xs, zs)
    HH = HEIGHTS[::step, ::step]
    ls = LightSource(azdeg=315, altdeg=40)
    colors = ls.shade(HH, cmap=cm.terrain, blend_mode="soft", vert_exag=2.0, dx=CELL * step, dy=CELL * step)

    fig = plt.figure(figsize=(13, 9), dpi=150)
    ax = fig.add_subplot(111, projection="3d")
    ax.plot_surface(X, Z, HH, facecolors=colors, rstride=1, cstride=1, linewidth=0, antialiased=True, shade=False, alpha=.95)

    # trajectories of a few units, drawn at the terrain surface
    pick = [0, 12, 24, 36, 48, 60, 72, 84, 96, 108]
    nframes = len(FRAMES)
    for i in pick:
        px, pz, py = [], [], []
        for k in range(0, nframes, 2):
            f = FRAMES[k]
            if i >= f["n"]:
                continue
            x, z = f["x"][i], f["z"][i]
            px.append(x)
            pz.append(z)
            py.append(float(sample_height(np.array(x), np.array(z))) + .38)
        ax.plot(px, pz, py, color="#5bd3ff", linewidth=1.7, alpha=.78)
        ax.scatter([px[-1]], [pz[-1]], [py[-1]], color="#3ddc84", s=16, depthshade=False)

    # final crowd
    last = FRAMES[-1]
    fx = np.array(last["x"])
    fz = np.array(last["z"])
    fh = np.array([float(sample_height(np.array(x), np.array(z))) for x, z in zip(fx, fz)]) + .35
    ax.scatter(fx, fz, fh, color="#3ddc84", s=18, depthshade=False, label="到达单位")
    ax.scatter([GOAL[0]], [GOAL[1]], [float(sample_height(np.array(GOAL[0]), np.array(GOAL[1]))) + .45],
               color="#ff5f6d", marker="*", s=260, depthshade=False, label="目标点")
    start = FRAMES[0]
    ax.scatter([start["x"][0]], [start["z"][0]], [float(sample_height(np.array(start["x"][0]), np.array(start["z"][0]))) + .35],
               color="#ffcc66", marker="o", s=70, depthshade=False, label="出发点")

    ax.set_xlim(0, SPAN)
    ax.set_ylim(0, SPAN)
    ax.set_zlim(float(HEIGHTS.min()) - .5, float(HEIGHTS.max()) + 1.2)
    ax.set_box_aspect((1, 1, .32))
    ax.view_init(elev=34, azim=-57)
    ax.set_xlabel("X / m", labelpad=8)
    ax.set_ylabel("Z / m", labelpad=8)
    ax.set_zlabel("高度 / m", labelpad=6)
    metrics = SCENE.get("metrics", {})
    title_time = metrics.get("allArrivedTime") or FRAMES[-1]["t"]
    ax.set_title(f"高度场、真实坡道与群体轨迹（{SCENE['metrics']['units']} 人，{title_time:.1f}s 全部到达）",
                 fontsize=15, pad=12)
    ax.legend(loc="upper left", frameon=False)
    for pane in (ax.xaxis, ax.yaxis, ax.zaxis):
        pane.pane.set_facecolor((0.05, 0.07, 0.09, 1))
        pane.pane.set_edgecolor((0.2, 0.25, 0.3, 1))
    ax.grid(True, alpha=.15)
    fig.patch.set_facecolor("#0b1117")
    ax.set_facecolor("#0b1117")
    for label in ax.get_xticklabels() + ax.get_yticklabels() + ax.get_zticklabels():
        label.set_color("#a9bccb")
    for label in (ax.xaxis.label, ax.yaxis.label, ax.zaxis.label, ax.title):
        label.set_color("#dbe7f0")
    fig.tight_layout()
    path = OUT / "flagship_3d.png"
    fig.savefig(path, facecolor=fig.get_facecolor(), bbox_inches="tight")
    plt.close(fig)
    print("wrote", path, path.stat().st_size)


if __name__ == "__main__":
    OUT.mkdir(exist_ok=True)
    make_keyframes()
    make_3d()
    make_gif()
