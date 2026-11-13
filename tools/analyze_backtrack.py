import json
from pathlib import Path
import math

path = Path("visualizer/data/flagship_120.js")
text = path.read_text(encoding="utf-8")
start = text.index("{", text.index("]"))
data = json.loads(text[start:text.rindex("}") + 1])
frames = data["frames"]
goal = data["goal"]
gx, gz = goal


def dist(x, z):
    return math.hypot(x - gx, z - gz)


# For each agent, find frames where distance-to-goal increases while in a moving state.
events = []
trails = {}
for fi, f in enumerate(frames):
    for i in range(f["n"]):
        x, z, st = f["x"][i], f["z"][i], f["s"][i]
        if i not in trails:
            trails[i] = []
        trails[i].append((f["t"], x, z, st, f["v"][i], dist(x, z)))

for i, pts in trails.items():
    for k in range(1, len(pts)):
        t0, x0, z0, s0, v0, d0 = pts[k - 1]
        t1, x1, z1, s1, v1, d1 = pts[k]
        if s1 != 0:
            continue
        move = math.hypot(x1 - x0, z1 - z0)
        if move < 1e-4:
            continue
        # radial component: positive means moving away from goal
        rx, rz = x0 - gx, z0 - gz
        rlen = math.hypot(rx, rz) or 1
        radial = ((x1 - x0) * rx + (z1 - z0) * rz) / rlen
        if radial > 0.015:
            events.append((i, t0, t1, x0, z0, x1, z1, d0, d1, radial, move, v1))

print("backward-moving samples:", len(events), "agents:", len(set(e[0] for e in events)))
by_agent = {}
for e in events:
    by_agent.setdefault(e[0], []).append(e)

summary = []
for i, evs in by_agent.items():
    total = sum(max(0, e[9]) for e in evs)
    maxd = max(e[8] - e[7] for e in evs)
    summary.append((total, maxd, len(evs), i, evs[0][1], evs[-1][1]))
summary.sort(reverse=True)
print("top agents by backward distance:")
for total, maxd, cnt, i, t0, t1 in summary[:15]:
    evs = by_agent[i]
    # contiguous clusters
    clusters = []
    cur = [evs[0]]
    for e in evs[1:]:
        if e[1] - cur[-1][1] < 0.3:
            cur.append(e)
        else:
            clusters.append(cur)
            cur = [e]
    clusters.append(cur)
    print(f"agent {i}: backtrack={total:.2f}m max_radial={maxd:.2f}m samples={cnt} "
          f"time={t0:.1f}-{t1:.1f}s clusters={[(round(c[0][1],1), round(c[-1][1],1), len(c)) for c in clusters[:6]]}")

# Print trajectories around top clusters
print("\n--- detail top 3 ---")
for total, maxd, cnt, i, t0, t1 in summary[:3]:
    print(f"\nagent {i} backward={total:.2f}")
    pts = trails[i]
    for t, x, z, st, v, d in pts:
        if t0 - 1 <= t <= t1 + 2:
            print(f"  t={t:.2f} pos=({x:.2f},{z:.2f}) dGoal={d:.2f} state={st} v={v:.2f}")
