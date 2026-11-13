const ws = new WebSocket("ws://127.0.0.1:8799/ws");
const q = [], wait = [];
ws.addEventListener("message", e => { const m = JSON.parse(e.data); wait.length ? wait.shift()(m) : q.push(m); });
function next(t = 10000) { if (q.length) return Promise.resolve(q.shift()); return new Promise((res, rej) => { const z = setTimeout(() => rej(new Error("timeout")), t); wait.push(m => { clearTimeout(z); res(m); }); }); }
async function type(t, timeout = 15000) { const end = Date.now() + timeout; for (;;) { const m = await next(Math.max(100, end - Date.now())); if (m.type === t) return m; } }
const send = o => ws.send(JSON.stringify(o));
await new Promise((r, j) => { ws.addEventListener("open", r); ws.addEventListener("error", j); });
await type("world"); await type("init");
send({ type: "speed", value: 4 });
send({ type: "map", id: "neicheng_walk01", count: 300 });
await type("world"); await type("reset");
send({ type: "units", count: 300, size: 0.5, speed: 3.1 });
const w = await type("world", 15000); const r = await type("reset", 15000);
console.log("units", r.count, "unitRadius", w.unitRadius, "slotSpacing", w.slotSpacing);
send({ type: "goal", x: 94.97, z: 38.42 });
const a = await type("goal", 15000);
console.log("goal", a.goal, "R", a.goalRadius);
let last = null, states = 0;
for (;;) { const m = await next(20000); if (m.type === "state") { last = m; states++; if (m.finalized) break; } }
const g = last.goal;
const ds = last.agents.map(a => Math.hypot(a[0]-g[0], a[1]-g[1]));
console.log("FINAL", { states, t: last.t, arrived: last.arrived, count: last.count,
  min: Math.min(...ds).toFixed(2), avg: (ds.reduce((x,y)=>x+y,0)/ds.length).toFixed(2),
  max: Math.max(...ds).toFixed(2), goalR: last.goalRadius,
  within2: ds.filter(d=>d<=2).length, within5: ds.filter(d=>d<=5).length });
ws.close();
