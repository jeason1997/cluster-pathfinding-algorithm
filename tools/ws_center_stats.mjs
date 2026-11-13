const ws = new WebSocket("ws://127.0.0.1:8799/ws");
const q = [], wait = [];
ws.addEventListener("message", e => { const m = JSON.parse(e.data); wait.length ? wait.shift()(m) : q.push(m); });
function next(t = 10000) { if (q.length) return Promise.resolve(q.shift()); return new Promise((res, rej) => { const z = setTimeout(() => rej(new Error("timeout")), t); wait.push(m => { clearTimeout(z); res(m); }); }); }
async function type(t, timeout = 15000) { const end = Date.now() + timeout; for (;;) { const m = await next(Math.max(100, end - Date.now())); if (m.type === t) return m; } }
const send = o => ws.send(JSON.stringify(o));
await new Promise((r, j) => { ws.addEventListener("open", r); ws.addEventListener("error", j); });
await type("world"); await type("init");
send({ type: "speed", value: 4 });
send({ type: "map", id: "neicheng_walk01", count: 120 });
const w = await type("world"); await type("reset");
send({ type: "goal", x: w.suggestedGoal[0], z: w.suggestedGoal[1] });
const a = await type("goal"); console.log("goal", a.goal, a.goalRadius);
let last = null;
for (;;) { const m = await next(15000); if (m.type === "state") { last = m; if (m.finalized) break; } }
const g = last.goal;
const ds = last.agents.map(a => Math.hypot(a[0] - g[0], a[1] - g[1]));
console.log("final", { t: last.t, arrived: last.arrived, count: last.count,
  max: Math.max(...ds).toFixed(2), avg: (ds.reduce((x,y)=>x+y,0)/ds.length).toFixed(2),
  within2: ds.filter(d=>d<=2).length, within5: ds.filter(d=>d<=5).length,
  withinRadius: ds.filter(d=>d<=last.goalRadius).length });
ws.close();
