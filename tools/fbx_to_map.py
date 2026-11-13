import json
import re
import sys
import math
from pathlib import Path
from collections import deque
import numpy as np

SRC = Path(sys.argv[1] if len(sys.argv) > 1 else r"F:\SLG\client\Assets\Art\Art_Scene\Terrian\FBX\neicheng_jianzhu\neicheng_walk01.fbx")
OUT = Path(sys.argv[2] if len(sys.argv) > 2 else r"src\CrowdSim.Interactive\Maps\neicheng_walk01.json")
SCALE = 5.0
CELL = 0.8
MARGIN = 4.0
NAME = "导入：内城步行街（neicheng_walk01）"

text = SRC.read_text(encoding="utf-8", errors="replace")
m = re.search(r"Vertices:\s*\*\d+\s*\{\s*a:\s*([^\}]*)\}", text, re.S)
verts = np.array([float(x) for x in re.findall(r"[-+]?\d*\.?\d+(?:[Ee][-+]?\d+)?", m.group(1))], dtype=np.float64).reshape(-1, 3)
m = re.search(r"PolygonVertexIndex:\s*\*\d+\s*\{\s*a:\s*([^\}]*)\}", text, re.S)
idx = [int(x) for x in re.findall(r"-?\d+", m.group(1))]
polys, cur = [], []
for i in idx:
    if i < 0:
        cur.append(-i - 1); polys.append(cur); cur = []
    else:
        cur.append(i)
tris = np.array(polys, dtype=int)

xmin, xmax = verts[:, 0].min(), verts[:, 0].max()
zmin, zmax = verts[:, 2].min(), verts[:, 2].max()
span_x = (xmax - xmin) * SCALE + 2 * MARGIN
span_z = (zmax - zmin) * SCALE + 2 * MARGIN
width = int(math.ceil(span_x / CELL)) + 1
height = int(math.ceil(span_z / CELL)) + 1
print("native bounds", (xmin, xmax, zmin, zmax), "world", span_x, span_z, "grid", width, height)

h = np.full((height, width), np.nan)
mask = np.zeros((height, width), dtype=bool)
for iz in range(height):
    wz = (iz + 0.5) * CELL
    nz = (wz - MARGIN) / SCALE + zmin
    for ix in range(width):
        wx = (ix + 0.5) * CELL
        nx = (wx - MARGIN) / SCALE + xmin
        for tri in tris:
            a, b, c = verts[tri]
            ab = b - a; ac = c - a
            den = ab[0] * ac[2] - ab[2] * ac[0]
            if abs(den) < 1e-12: continue
            vx, vz = nx - a[0], nz - a[2]
            u = (vx * ac[2] - vz * ac[0]) / den
            v = (ab[0] * vz - ab[2] * vx) / den
            if u < -1e-6 or v < -1e-6 or u + v > 1 + 1e-6: continue
            y = a[1] + u * ab[1] + v * ac[1]
            h[iz, ix] = (y - verts[:, 1].min()) * SCALE
            mask[iz, ix] = True
            break
print("covered", mask.sum(), "of", width * height, "height", np.nanmin(h), np.nanmax(h))

# largest 4-connected component
comp = np.zeros((height, width), dtype=np.int32)
best_id, best_size = 0, 0
cid = 0
for z in range(height):
    for x in range(width):
        if not mask[z, x] or comp[z, x]: continue
        cid += 1
        q = deque([(z, x)]); comp[z, x] = cid; size = 0
        while q:
            zz, xx = q.popleft(); size += 1
            for dz, dx in ((1,0),(-1,0),(0,1),(0,-1)):
                nz2, nx2 = zz+dz, xx+dx
                if 0 <= nz2 < height and 0 <= nx2 < width and mask[nz2,nx2] and not comp[nz2,nx2]:
                    comp[nz2,nx2] = cid; q.append((nz2,nx2))
        if size > best_size:
            best_size, best_id = size, cid
print("components", cid, "largest", best_id, best_size)
mask = comp == best_id

# distance to non-walkable/edge (BFS) as clearance proxy (in cells)
dist = np.full((height, width), -1, dtype=np.int32)
q = deque()
for z in range(height):
    for x in range(width):
        if not mask[z,x] or x==0 or z==0 or x==width-1 or z==height-1:
            dist[z,x] = 0; q.append((z,x))
while q:
    z,x = q.popleft()
    for dz,dx in ((1,0),(-1,0),(0,1),(0,-1)):
        nz2,nx2=z+dz,x+dx
        if 0<=nz2<height and 0<=nx2<width and mask[nz2,nx2] and dist[nz2,nx2] < 0:
            dist[nz2,nx2]=dist[z,x]+1; q.append((nz2,nx2))
print("max clearance cells", dist.max(), "m", dist.max()*CELL)
# report largest-clearance points in the big right plateau (world x>70)
ys, xs = np.where(mask & (dist == dist.max()))
print("max points", [(round((x+0.5)*CELL,1), round((z+0.5)*CELL,1)) for z,x in list(zip(ys,xs))[:10]])
# Choose spawn: high clearance in right plateau (world x > 70)
cands = [(dist[z,x], x, z) for z in range(height) for x in range(width)
         if mask[z,x] and (x+0.5)*CELL > 70]
cands.sort(reverse=True)
spawn = None
for d,x,z in cands:
    if d*CELL >= 2.5:
        spawn=(x,z); break
if spawn is None: spawn=cands[0][1:]
sx, sz = spawn
spawn_world = ((sx+0.5)*CELL, (sz+0.5)*CELL)
print("spawn cell", spawn, "world", spawn_world, "clearance", dist[sz,sx]*CELL)

# The big right-hand plateau is the only open area large enough for hundreds of
# capsules. Put the start at its north end and the suggested goal at its south end;
# users can still click anywhere else on the map.
def world(nx, nz):
    return ((nx - xmin) * SCALE + MARGIN, (nz - zmin) * SCALE + MARGIN)

spawn_world = world(10.0, -6.5)
goal_world = world(10.0, 4.0)
sx = int(spawn_world[0] / CELL); sz = int(spawn_world[1] / CELL)
gx = int(goal_world[0] / CELL); gz = int(goal_world[1] / CELL)
assert mask[sz, sx] and mask[gz, gx], "chosen spawn/goal is outside the mesh"
print("spawn world", spawn_world, "clearance", dist[sz,sx]*CELL)
print("goal world", goal_world, "clearance", dist[gz,gx]*CELL, "distance", math.dist(spawn_world,goal_world))

flat_h = np.where(mask, h, 0.0)
# Keep the authored triangles for rendering. The simulation still uses the
# regular height field, but the 3D mesh must show the actual FBX surface.
mesh_vertices = []
for v in verts:
    wx = (v[0] - xmin) * SCALE + MARGIN
    wz = (v[2] - zmin) * SCALE + MARGIN
    wy = (v[1] - verts[:, 1].min()) * SCALE
    mesh_vertices.extend([round(float(wx), 4), round(float(wy), 4), round(float(wz), 4)])
mesh_indices = [int(i) for i in tris.ravel()]
data = {
    "id": "neicheng_walk01",
    "name": NAME,
    "source": str(SRC),
    "width": width,
    "height": height,
    "cell": CELL,
    "originX": 0.0,
    "originZ": 0.0,
    "scale": SCALE,
    "spawn": [round(spawn_world[0], 3), round(spawn_world[1], 3)],
    "spawnRadius": 10.0,
    "suggestedGoal": [round(goal_world[0], 3), round(goal_world[1], 3)],
    "heights": [round(float(v), 4) for v in flat_h.ravel()],
    "walkable": [1 if v else 0 for v in mask.ravel()],
    "mesh": {
        "vertices": mesh_vertices,
        "indices": mesh_indices,
    },
}
OUT.parent.mkdir(parents=True, exist_ok=True)
OUT.write_text(json.dumps(data, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
print("wrote", OUT, OUT.stat().st_size)
