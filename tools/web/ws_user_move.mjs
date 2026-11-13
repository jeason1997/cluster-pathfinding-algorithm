const ws = new WebSocket("ws://127.0.0.1:8799/ws");
const q = [], wait = [];
ws.addEventListener("message", e => { const m = JSON.parse(e.data); wait.length ? wait.shift()(m) : q.push(m); });
function next(t = 10000) { if (q.length) return Promise.resolve(q.shift()); return new Promise((res, rej) => { const z = setTimeout(() => rej(new Error("timeout")), t); wait.push(m => { clearTimeout(z); res(m); }); }); }
async function type(t, timeout = 15000) { const end = Date.now() + timeout; for (;;) { const m = await next(Math.max(100, end - Date.now())); if (m.type === t) return m; } }
const send = o => ws.send(JSON.stringify(o));
await new Promise((r, j) => { ws.addEventListener("open", r); ws.addEventListener("error", j); });
await type("world"); await type("init");
send({ type: "map", id: "neicheng_walk01", count: 300 });
await type("world"); await type("reset");
send({ type: "units", count: 300, size: 0.5, speed: 6 });
await type("world"); await type("reset");
send({ type: "goal", x: 95.09, z: 35.32 });
const g = await type("goal"); console.log("goal", g.ok, g.error || "", g.goal, g.goalRadius);
let first = null, last = null;
const t0 = Date.now();
while (Date.now() - t0 < 35000) {
  const m = await next(5000); if (m.type !== "state") continue;
  const ds = m.agents.map(a => Math.hypot(a[0]-m.goal[0], a[1]-m.goal[1]));
  const avg = ds.reduce((x,y)=>x+y,0)/ds.length;
  if (!first) { first = {t:m.t, avg, max:Math.max(...ds)}; console.log("FIRST", m.t.toFixed(1), avg.toFixed(2), Math.max(...ds).toFixed(2)); }
  last = {t:m.t, avg, max:Math.max(...ds), arrived:m.arrived, finalized:m.finalized};
  if (m.t % 5 < 0.3) console.log("t", m.t.toFixed(1), "avg", avg.toFixed(2), "max", Math.max(...ds).toFixed(2), "arr", m.arrived);
  if (m.finalized) break;
}
console.log("FINAL", last);
ws.close();
